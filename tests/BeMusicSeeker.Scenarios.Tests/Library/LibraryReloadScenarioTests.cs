using System.IO;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Scenarios.Tests.Library;

/// <summary>通常製品の起動・走査・差分保存・次回起動を一つの独立profileで検証します。</summary>
[TestClass]
public sealed class LibraryReloadScenarioTests
{
    /// <summary>保証: SC-01。実ContentRendered初期化と検索・外部追加削除・差分保存・次起動が同じ譜面集合へ収束し、利用者列とroot外fileを壊しません。</summary>
    /// <remarks>型付きdriverは通常ownerを呼びます。入力event/BindingはWindows/Desktop、生成列更新時の利用者列保持は既存writer/文字コード変更Componentへ分担します。</remarks>
    [TestMethod(DisplayName = "[SC-01] 起動・差分保存・新processで譜面集合と利用者列を保持する")]
    [TestCategory("Daily")]
    public async Task Startup_DiffReloadAndNewProcess_PreserveLibraryAndUserColumns()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = Sc01LibraryFixture.Create(workspace);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            await RunApplicationAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                Sc01LibraryOracle.AssertDatabaseAndFiles(fixture, afterExternalChanges: false);
                ScenarioLibrarySnapshot initial = (await app.SnapshotAsync(deadline)).Snapshot;
                AssertCharts(fixture.InitialCharts, initial.Catalog);
                AssertCharts(fixture.InitialCharts, initial.Visible);
                AssertUserColumns(initial.Catalog, fixture);

                ScenarioKeywordCompleted firstSearch = await app.SetKeywordAsync("match", deadline);
                AssertSearch(firstSearch, [fixture.B]);
                fixture.ApplyExternalChanges();
                ScenarioReloadCompleted reloaded = await app.ReloadAsync(deadline);
                Assert.IsNotNull(reloaded.Result, "差分要求が受理されていません。");
                AssertStartup(reloaded.Result);
                Sc01LibraryOracle.AssertDatabaseAndFiles(fixture, afterExternalChanges: true);
                AssertCharts(fixture.UpdatedCharts, reloaded.Snapshot.Catalog);
                AssertCharts([fixture.B, fixture.D], reloaded.Snapshot.Visible);
                AssertUserColumns(reloaded.Snapshot.Catalog, fixture);
                await app.CloseAsync(deadline);
            });

            // 再起動時の走査・修復で直前の保存漏れを隠さないよう、上の実DB検査を済ませてから新processを作ります。
            await RunApplicationAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                Sc01LibraryOracle.AssertDatabaseAndFiles(fixture, afterExternalChanges: true);
                ScenarioLibrarySnapshot restored = (await app.SnapshotAsync(deadline)).Snapshot;
                AssertCharts(fixture.UpdatedCharts, restored.Catalog);
                AssertCharts(fixture.UpdatedCharts, restored.Visible);
                AssertUserColumns(restored.Catalog, fixture);
                AssertSearch(await app.SetKeywordAsync("match", deadline), [fixture.B, fixture.D]);
                await app.CloseAsync(deadline);
            });
        }
        catch
        {
            workspace.PreserveArtifacts();
            throw;
        }
    }

    private static async Task RunApplicationAsync(Sc01LibraryFixture fixture, TestWorkspace workspace, ProcessDeadline deadline,
        Func<ApplicationScenarioDriver, Task> body)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "fixtures", "application", "BeMusicSeeker.exe");
        ApplicationScenarioDriver app = await ApplicationScenarioDriver.StartAsync(executable, fixture.ProfileDirectory,
            workspace.GetPath("session.json"), workspace.CaseId, fixture.UpdateManifestUrl, deadline);
        Exception? primary = null;
        try { await body(app); }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try { await app.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null)
            { primary.Data["ScenarioCleanupFailure"] = cleanup; }
        }
    }

    private static void AssertStartup(ScenarioInitializationResult result)
    {
        Assert.AreEqual(StartupInitializationOutcome.Succeeded, result.Outcome);
        Assert.IsTrue(result.CompletionPublished, "必須UI反映を完了していません。");
        Assert.IsTrue(result.IsInitializationCompleted, "ライブラリが操作可能になっていません。");
        Assert.IsNull(result.Failure, result.Failure?.StackTrace);
        Assert.IsTrue(result.OperationToken > 0);
    }

    private static void AssertSearch(ScenarioKeywordCompleted result, IReadOnlyList<Sc01ChartManifest> expected)
    {
        Assert.AreEqual("match", result.Keyword);
        Assert.IsTrue(result.ViewRequestId > 0);
        Assert.IsTrue(result.RowsGeneration > 0);
        Assert.AreEqual(result.ViewRequestId, result.Snapshot.ViewRequestId);
        Assert.AreEqual(result.RowsGeneration, result.Snapshot.RowsGeneration);
        AssertCharts(expected, result.Snapshot.Visible);
    }

    private static void AssertCharts(IReadOnlyList<Sc01ChartManifest> expected, IReadOnlyList<ScenarioChart> actual)
    {
        CollectionAssert.AreEquivalent(expected.Select(chart => chart.Path).ToArray(), actual.Select(chart => chart.Path).ToArray());
        foreach (Sc01ChartManifest chart in expected)
        {
            ScenarioChart observed = actual.Single(row => row.Path == chart.Path);
            Assert.AreEqual(chart.Title, observed.Title, chart.Path);
            Assert.AreEqual(chart.Md5, observed.Md5, chart.Path);
            Assert.AreEqual(chart.Sha256, observed.Sha256, chart.Path);
            Assert.AreEqual(chart.Format.ToString(), observed.Format, chart.Path);
        }
    }

    private static void AssertUserColumns(IReadOnlyList<ScenarioChart> charts, Sc01LibraryFixture fixture)
    {
        ScenarioChart retained = charts.Single(row => row.Path == fixture.B.Path);
        Assert.AreEqual(Sc01LibraryFixture.PersistedTag, retained.Tag);
        Assert.AreEqual(Sc01LibraryFixture.PersistedFavorite, retained.Favorite);
        Assert.AreEqual(Sc01LibraryFixture.PersistedAddDate, retained.AddDate);
    }
}
