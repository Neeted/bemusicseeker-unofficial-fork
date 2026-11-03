using System.IO;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Scenarios.Tests.Install;

/// <summary>明示導入先消失から再読込・再選択・新要求・別processの保存確認までを検証します。</summary>
[TestClass]
public sealed class MissingDestinationScenarioTests
{
    /// <summary>消失先を再作成せず入力を保全し、実Reload後の新要求だけで導入が成立することを保証します。</summary>
    /// <remarks>削除/改名の条件表はCoreに分担し、本caseは改名の代表と本番Task・通知・永続化の接続を担当します。</remarks>
    [TestMethod(DisplayName = "[SC-05] 消失した明示導入先を再読込して再選択し保存する")]
    [TestCategory("Full")]
    public async Task MissingDestination_ReloadReselectAndNewRequest_PreserveThenCommit()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = new MissingDestinationFixture(workspace);
            var deadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromMinutes(3));
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                AssertInitialization(await app.WaitForStartupAsync(deadline));
                MissingDestinationOracle.AssertState(fixture, false, false, false, false);
                ScenarioDropCompleted drop = await app.DropAsync([fixture.Source], deadline);
                Assert.AreNotEqual(Guid.Empty, drop.BatchId);
                Assert.IsTrue(drop.Generation > 0);
                Assert.IsNull(drop.Failure, drop.Failure?.StackTrace);
                Assert.IsNotNull(drop.Result);
                Assert.IsFalse(drop.Result.HasRequiredFailure || drop.Result.SynchronizationIncomplete);
                Assert.AreEqual(0, drop.Result.Failures.Length);
                Assert.AreEqual(0, drop.Result.UnprocessedPackageCount);
                AssertDestination(await app.NavigatePackagesAsync(true, deadline), fixture, null, checkDestination: false);
                AssertDestination((await app.ApplyPendingAsync(ScenarioPendingAction.Clear, [fixture.InputChart], deadline)).View, fixture, null);
                AssertDestination((await app.ApplyPendingAsync(ScenarioPendingAction.Set, [fixture.InputChart], deadline, fixture.Original)).View, fixture, fixture.Original);
                MissingDestinationOracle.AssertState(fixture, false, false, true, false);
                Directory.Move(fixture.Original, fixture.Renamed);
                ScenarioPendingCompleted failed = await app.ApplyPendingAsync(ScenarioPendingAction.Install, [fixture.InputChart], deadline,
                    expectInstallFailureReport: true);
                Assert.IsNotNull(failed.Mutation);
                Assert.IsFalse(failed.Mutation.Succeeded);
                Assert.IsNotNull(failed.Mutation.Session);
                Assert.IsFalse(failed.Mutation.Session.DurableCommit);
                Assert.IsTrue(failed.Mutation.Session.HasRequiredFailure);
                Assert.IsFalse(failed.Mutation.Session.HasUnrestoredExistingDestination || failed.Mutation.Session.ManualRecoveryRequired);
                Assert.AreEqual(0, failed.Mutation.Session.ConfirmedTargets.Length);
                Assert.IsTrue(failed.Mutation.Session.Failures.Append(failed.Mutation.Failure).OfType<ScenarioFailure>()
                    .Any(failure => failure.StackTrace?.Contains(typeof(DirectoryNotFoundException).FullName ?? nameof(DirectoryNotFoundException), StringComparison.Ordinal) == true),
                    "実receiptに元DirectoryNotFoundExceptionが必要です。");
                AssertDestination(failed.View, fixture, fixture.Original);
                MissingDestinationOracle.AssertState(fixture, true, false, true, false);

                ScenarioReloadCompleted reload = await app.ReloadAsync(deadline);
                Assert.IsNotNull(reload.Result);
                AssertInitialization(reload.Result);
                Assert.IsTrue(reload.Snapshot.Catalog.Any(chart => chart.Path == Path.Combine(fixture.Renamed, fixture.Existing.Name)));
                MissingDestinationOracle.AssertState(fixture, true, true, true, false);
                ScenarioPackageViewCompleted current = await app.NavigatePackagesAsync(true, deadline);
                string currentPath = current.Rows.Single(row => row.Chart.Md5 == fixture.Difference.Md5).Chart.Path;
                Assert.AreEqual(fixture.InputChart, currentPath);
                AssertDestination((await app.ApplyPendingAsync(ScenarioPendingAction.Set, [currentPath], deadline, fixture.Renamed)).View, fixture, fixture.Renamed);
                ScenarioPendingCompleted installed = await app.ApplyPendingAsync(ScenarioPendingAction.Install, [currentPath], deadline);
                Assert.IsNotNull(installed.Mutation);
                Assert.IsTrue(installed.Mutation.Succeeded, installed.Mutation.Failure?.StackTrace);
                Assert.IsFalse(installed.Mutation.HasInstallRefusal);
                Assert.IsNull(installed.Mutation.Failure);
                Assert.IsNotNull(installed.Mutation.Session);
                ScenarioMutationReceipt receipt = installed.Mutation.Session;
                Assert.IsTrue(receipt.DurableCommit);
                Assert.IsFalse(receipt.HasRequiredFailure || receipt.HasUnrestoredExistingDestination || receipt.ManualRecoveryRequired || receipt.CompletedWithCleanupFailure);
                Assert.AreEqual(0, receipt.Failures.Length);
                Assert.AreEqual(0, receipt.UnprocessedTargets.Length);
                Assert.AreEqual(0, receipt.DestinationConflictCount);
                Assert.AreEqual(0, installed.View.Rows.Length);
                AssertMoves(receipt, fixture);
                MissingDestinationOracle.AssertState(fixture, true, true, false, true);
                AssertCatalog((await app.SnapshotAsync(deadline)).Snapshot, fixture);
                await app.CloseAsync(deadline);
            });
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                AssertInitialization(await app.WaitForStartupAsync(deadline));
                MissingDestinationOracle.AssertState(fixture, true, true, false, true);
                AssertCatalog((await app.SnapshotAsync(deadline)).Snapshot, fixture);
                Assert.AreEqual(0, (await app.NavigatePackagesAsync(true, deadline)).Rows.Length);
                await app.CloseAsync(deadline);
            });
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    private static async Task RunAsync(MissingDestinationFixture fixture, TestWorkspace workspace, ProcessDeadline deadline,
        Func<ApplicationScenarioDriver, Task> body)
    {
        string executable = ScenarioApplicationPaths.ExecutablePath;
        ApplicationScenarioDriver app = await ApplicationScenarioDriver.StartAsync(executable, fixture.Profile,
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

    private static void AssertInitialization(ScenarioInitializationResult result)
    {
        Assert.AreEqual(StartupInitializationOutcome.Succeeded, result.Outcome);
        Assert.IsTrue(result.CompletionPublished && result.IsInitializationCompleted && result.OperationToken > 0);
        Assert.IsNull(result.Failure, result.Failure?.StackTrace);
    }

    private static void AssertDestination(ScenarioPackageViewCompleted view, MissingDestinationFixture fixture, string? destination, bool checkDestination = true)
    {
        Assert.IsTrue(view.Pending && view.RowsGeneration > 0);
        Assert.AreEqual(1, view.Rows.Length);
        ScenarioPackageChart row = view.Rows.Single();
        Assert.AreEqual(fixture.InputChart, row.Chart.Path);
        Assert.AreEqual(fixture.Difference.Md5, row.Chart.Md5);
        if (!checkDestination) { return; }
        if (destination is null) { Assert.IsTrue(string.IsNullOrEmpty(row.Destination)); }
        else { Assert.AreEqual(destination, row.Destination); }
    }

    private static void AssertCatalog(ScenarioLibrarySnapshot snapshot, MissingDestinationFixture fixture)
        => CollectionAssert.AreEquivalent(new[] { Path.Combine(fixture.Renamed, fixture.Existing.Name),
            Path.Combine(fixture.Renamed, fixture.Difference.Name), Path.Combine(fixture.Library, "Sentinel", fixture.Sentinel.Name) },
            snapshot.Catalog.Select(chart => chart.Path).ToArray());

    private static void AssertMoves(ScenarioMutationReceipt receipt, MissingDestinationFixture fixture)
    {
        foreach (MissingDestinationFile file in fixture.DifferenceFiles)
        {
            string destination = Path.Combine(fixture.Renamed, file.Name);
            ScenarioMutationTarget[] targets = receipt.ConfirmedTargets.Where(target => target.DestinationPath == destination
                || target.DestinationPath == fixture.Renamed).ToArray();
            Assert.AreEqual(1, targets.Length, destination);
            ScenarioMutationTarget target = targets.Single();
            string source = target.DestinationPath == destination ? target.SourcePath : Path.Combine(target.SourcePath, file.Name);
            Assert.AreEqual(Path.Combine(fixture.Source, file.Name), source);
        }
        Assert.IsTrue(receipt.ConfirmedTargets.All(target => target.DestinationPath == fixture.Renamed
            || fixture.DifferenceFiles.Any(file => target.DestinationPath == Path.Combine(fixture.Renamed, file.Name))));
    }
}
