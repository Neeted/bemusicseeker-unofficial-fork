using System.IO;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Scenarios.Tests.SettingsScenarios;

/// <summary>設定の節確定・取消・即時保存を、独立した二つの実App profileで確認します。</summary>
[TestClass]
public sealed class SettingsScenarioTests
{
    /// <summary>保証: SC10A。root候補取消と同Taskの差分確定、即時font保存とClose取消、全終了後の一回の明示起動へ接続します。</summary>
    /// <remarks>保存失敗・競合・再表示中の同owner保持の条件表は既存Settings Componentへ分担します。</remarks>
    [TestMethod(DisplayName = "[SC-10A] BMS節確定と即時保存を取消・通常終了・次回起動へ接続する")]
    [TestCategory("Full")]
    public async Task StandaloneRoots_SectionCommitAndImmediateSettingPersistAcrossNewProcess()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = new SettingsScenarioFixture(workspace, lr2: false);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            await RunAsync(workspace, fixture, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                ScenarioSettingsSectionCompleted canceled = await app.SettingsSectionAsync(ScenarioSettingsSectionAction.AddRootsCancel, fixture.Root1, "", deadline);
                CollectionAssert.AreEquivalent(new[] { fixture.Root0, fixture.Root1 }, canceled.Settings.CandidateRoots.ToArray());
                CollectionAssert.AreEquivalent(new[] { fixture.Root0 }, canceled.Settings.Roots.ToArray());
                SettingsScenarioOracle.Roots(fixture, [fixture.Root0]);
                SettingsScenarioOracle.Library(fixture.SongDb, canceled.Library, fixture.A);
                ScenarioSettingsSectionCompleted committed = await app.SettingsSectionAsync(ScenarioSettingsSectionAction.AddRootsCommit, fixture.Root1, "", deadline);
                CollectionAssert.AreEquivalent(new[] { fixture.Root0, fixture.Root1 }, committed.Settings.Roots.ToArray());
                SettingsScenarioOracle.Roots(fixture, [fixture.Root0, fixture.Root1]);
                SettingsScenarioOracle.Library(fixture.SongDb, committed.Library, fixture.A, fixture.B);
                ScenarioPlaylistCompleted edited = await app.EditSettingsAsync(SettingsScenarioFixture.Font, fixture.Candidate, deadline);
                Assert.IsNotNull(edited.SettingsEdit);
                Assert.AreEqual(fixture.Candidate, edited.SettingsEdit.CandidateBeforeClose);
                Assert.AreEqual("", edited.SettingsEdit.CandidateAfterClose);
                Assert.AreEqual(SettingsScenarioFixture.Font, edited.SettingsEdit.ProviderFont);
                Assert.AreEqual(SettingsScenarioFixture.Font, edited.Snapshot.MainFontSize);
                Assert.AreEqual(SettingsScenarioFixture.Font, edited.Snapshot.SummaryFontSize);
                SettingsScenarioOracle.Font(fixture);
                await app.CloseAsync(deadline);
            });
            await RunAsync(workspace, fixture, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                ScenarioSettingsSectionCompleted restored = await app.SettingsSectionAsync(ScenarioSettingsSectionAction.Inspect, "", "", deadline);
                Assert.AreEqual(SettingsScenarioFixture.Font, restored.Settings.FontSize);
                CollectionAssert.AreEquivalent(new[] { fixture.Root0, fixture.Root1 }, restored.Settings.Roots.ToArray());
                SettingsScenarioOracle.Roots(fixture, [fixture.Root0, fixture.Root1]);
                SettingsScenarioOracle.Font(fixture);
                SettingsScenarioOracle.Library(fixture.SongDb, restored.Library, fixture.A, fixture.B);
                await app.CloseAsync(deadline);
            });
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    /// <summary>保証: SC10B。出力候補取消と実出力・外部folder同期、LR2詳細候補取消と承認後の同Job後継ready・全所有回収を確認します。</summary>
    /// <remarks>テスト起動は一回、製品後継とpeer再束縛は一回です。保存と出力の条件網羅は既存Componentへ分担します。</remarks>
    [TestMethod(DisplayName = "[SC-10B] LR2出力節と詳細環境確定を一回の製品再起動へ接続する")]
    [TestCategory("Full")]
    public async Task Lr2OutputAndDetails_CommitReachesSingleOwnedSuccessor()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = new SettingsScenarioFixture(workspace, lr2: true);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            await RunAsync(workspace, fixture, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                int initialPid = app.ProcessId;
                ScenarioPlaylistCompleted created = await app.CreatePlaylistAsync(SettingsScenarioFixture.PlaylistName, "S", deadline);
                int tableId = created.Snapshot.Tables.Single(table => table.Name == SettingsScenarioFixture.PlaylistName).Id;
                await app.AddPlaylistRowsAsync(tableId, [fixture.A.Md5], null, deadline);
                ScenarioSettingsSectionCompleted canceled = await app.SettingsSectionAsync(ScenarioSettingsSectionAction.OutputCancel, fixture.Output1, "", deadline);
                Assert.AreEqual(fixture.Output1, canceled.Settings.CandidateOutput);
                Assert.AreEqual(fixture.Output0, canceled.Settings.Output);
                Assert.AreEqual(fixture.Output0, canceled.Settings.SavedOutput);
                SettingsScenarioOracle.Output(fixture, fixture.Output0);
                ScenarioSettingsSectionCompleted committed = await app.SettingsSectionAsync(ScenarioSettingsSectionAction.OutputCommit, fixture.Output1, "", deadline);
                Assert.AreEqual(fixture.Output1, committed.Settings.Output);
                Assert.AreEqual(fixture.Output1, committed.Settings.SavedOutput);
                SettingsScenarioOracle.Output(fixture, fixture.Output1);
                ScenarioSettingsSectionCompleted details = await app.SettingsSectionAsync(ScenarioSettingsSectionAction.DetailsCancel, fixture.NextSongDb, fixture.NextConfig, deadline);
                Assert.AreEqual(fixture.NextSongDb, details.Settings.CandidateSongDb);
                Assert.AreEqual(fixture.NextConfig, details.Settings.CandidateConfig);
                Assert.AreEqual(fixture.SongDb, details.Settings.SongDb);
                Assert.AreEqual(fixture.Config, details.Settings.Config);
                Assert.AreEqual(fixture.SongDb, details.Settings.SavedSongDb);
                Assert.AreEqual(fixture.Config, details.Settings.SavedConfig);
                SettingsScenarioOracle.Library(fixture.SongDb, details.Library, fixture.A);
                await app.RestartEnvironmentAsync(fixture.NextSongDb, fixture.NextConfig, deadline);
                Assert.AreEqual(0, await app.RootExit);
                Assert.AreNotEqual(initialPid, app.ProcessId);
                Assert.IsNotNull(app.SuccessorStartTime);
                AssertStartup(await app.WaitForStartupAsync(deadline));
                ScenarioSettingsSectionCompleted restored = await app.SettingsSectionAsync(ScenarioSettingsSectionAction.Inspect, "", "", deadline);
                Assert.AreEqual(fixture.NextSongDb, restored.Settings.SongDb);
                Assert.AreEqual(fixture.NextConfig, restored.Settings.Config);
                Assert.AreEqual(fixture.NextSongDb, SettingsScenarioOracle.Setting(fixture, "LR2SongDBPath"));
                Assert.AreEqual(fixture.NextConfig, SettingsScenarioOracle.Setting(fixture, "LR2ConfigXmlPath"));
                SettingsScenarioOracle.Library(fixture.NextSongDb, restored.Library, fixture.A, fixture.B);
                await app.CloseAsync(deadline);
            });
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    private static async Task RunAsync(TestWorkspace workspace, SettingsScenarioFixture fixture, ProcessDeadline deadline, Func<ApplicationScenarioDriver, Task> body)
    {
        ApplicationScenarioDriver app = await ApplicationScenarioDriver.StartAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "application", "BeMusicSeeker.exe"), fixture.Profile,
            workspace.GetPath("session.json"), workspace.CaseId, fixture.UpdateUrl, deadline);
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
        Assert.IsTrue(result.CompletionPublished);
        Assert.IsTrue(result.IsInitializationCompleted);
        Assert.IsTrue(result.OperationToken > 0);
        Assert.IsNull(result.Failure, result.Failure?.StackTrace);
    }
}
