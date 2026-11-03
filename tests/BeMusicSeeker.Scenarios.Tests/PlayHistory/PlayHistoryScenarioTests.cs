using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Scenarios.Tests.PlayHistory;

/// <summary>実Appで外部score入力・実履歴reader・期間検索・通常一覧・ランプ画面を接続します。</summary>
[TestClass]
public sealed class PlayHistoryScenarioTests
{
    /// <summary>保証: SC11。実schema導入は既存scoreを履歴化せず、外部score/player更新のtrigger履歴と選択beatoraja playerの更新記録を表示・検索・ランプへ接続します。</summary>
    /// <remarks>LR2は更新後基準の実7日期間で日跨ぎを含め、beatorajaは固定日の境界と陽性keywordを確認します。内部の旧callback競合・reader全条件は既存Componentへ分担し、内蔵再生や履歴表への直接補充は使いません。</remarks>
    [TestMethod(DisplayName = "[SC-11] 外部履歴・設定provider切替・期間検索を通常表示とランプへ接続する")]
    [TestCategory("Full")]
    public async Task ExternalHistory_RealSchemaSettingsAndPresentationPreserveSourceIdentity()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = new PlayHistoryScenarioFixture(workspace);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            ApplicationScenarioDriver app = await ApplicationScenarioDriver.StartAsync(
                ScenarioApplicationPaths.ExecutablePath, fixture.Profile,
                workspace.GetPath("session.json"), workspace.CaseId, fixture.UpdateUrl, deadline);
            Exception? primary = null;
            try
            {
                ScenarioInitializationResult ready = await app.WaitForStartupAsync(deadline);
                Assert.AreEqual(StartupInitializationOutcome.Succeeded, ready.Outcome);
                Assert.IsTrue(ready.CompletionPublished && ready.IsInitializationCompleted);
                Assert.IsNull(ready.Failure, ready.Failure?.StackTrace);
                await app.SettingsSectionAsync(ScenarioSettingsSectionAction.InstallHistorySchema, "", "", deadline);
                PlayHistoryScenarioOracle.InstalledWithoutBackfill(fixture);
                (long From, long To) interval = fixture.PlayLr2();
                string periodAnchor = PlayHistoryScenarioOracle.Recent7DaysAnchor(interval.From, interval.To);
                PlayHistoryScenarioOracle.RawHistory[] raw = PlayHistoryScenarioOracle.Lr2Raw(fixture, interval.From, interval.To);
                await app.HistoryAsync(ScenarioHistoryAction.ReloadScores, deadline);
                ScenarioHistoryCompleted period = await app.HistoryAsync(ScenarioHistoryAction.PeriodRecent7Days, deadline, periodAnchor);
                PlayHistoryScenarioOracle.Lr2View(fixture, raw, period, keyword: false);
                ScenarioHistoryCompleted keyword = await app.HistoryAsync(ScenarioHistoryAction.Keyword, deadline, "HistoryMatch");
                Assert.IsTrue(keyword.KeywordRevision > period.KeywordRevision);
                PlayHistoryScenarioOracle.Lr2View(fixture, raw, keyword, keyword: true);
                await app.HistoryAsync(ScenarioHistoryAction.Keyword, deadline, "");
                PlayHistoryScenarioOracle.Navigation(fixture, await app.HistoryAsync(ScenarioHistoryAction.Library, deadline));
                PlayHistoryScenarioOracle.Navigation(fixture, await app.HistoryAsync(ScenarioHistoryAction.Duplicates, deadline));
                PlayHistoryScenarioOracle.Lamp(await app.HistoryAsync(ScenarioHistoryAction.Lamp, deadline, tableId: PlayHistoryScenarioFixture.PlaylistId), "HARD");

                await app.SettingsSectionAsync(ScenarioSettingsSectionAction.BeatorajaRootCommit, fixture.BeatRoot, "", deadline);
                Assert.AreEqual(fixture.BeatRoot, PlayHistoryScenarioOracle.Setting(fixture, "BeatorajaRootPath"));
                await app.SettingsSectionAsync(ScenarioSettingsSectionAction.BeatorajaScoreCommit, "selected", "", deadline);
                Assert.AreEqual("selected", PlayHistoryScenarioOracle.Setting(fixture, "BeatorajaPlayerId"));
                Assert.AreEqual(fixture.BeatScore, PlayHistoryScenarioOracle.Setting(fixture, "BeatorajaScoreDbPath"));
                Assert.IsTrue(bool.Parse(PlayHistoryScenarioOracle.Setting(fixture, "UseBeatorajaScoreDb")));
                Assert.IsTrue(bool.Parse(PlayHistoryScenarioOracle.Setting(fixture, "OperationModeLR2DB")), "provider変更は第三の動作modeを作りません。");
                ScenarioHistoryCompleted beat = await app.HistoryAsync(ScenarioHistoryAction.PeriodDay, deadline, PlayHistoryScenarioFixture.BeatDay);
                PlayHistoryScenarioOracle.BeatorajaView(fixture, beat, keyword: false);
                ScenarioHistoryCompleted beatKeyword = await app.HistoryAsync(ScenarioHistoryAction.Keyword, deadline, "HistoryMatch");
                Assert.IsTrue(beatKeyword.KeywordRevision > beat.KeywordRevision);
                PlayHistoryScenarioOracle.BeatorajaView(fixture, beatKeyword, keyword: true);
                await app.HistoryAsync(ScenarioHistoryAction.Keyword, deadline, "");
                PlayHistoryScenarioOracle.Navigation(fixture, await app.HistoryAsync(ScenarioHistoryAction.Library, deadline));
                PlayHistoryScenarioOracle.Lamp(await app.HistoryAsync(ScenarioHistoryAction.Lamp, deadline, tableId: PlayHistoryScenarioFixture.PlaylistId), "NORMAL");
                await app.SettingsSectionAsync(ScenarioSettingsSectionAction.Lr2ScoreCommit, "", "", deadline);
                Assert.IsFalse(bool.Parse(PlayHistoryScenarioOracle.Setting(fixture, "UseBeatorajaScoreDb")));
                PlayHistoryScenarioOracle.Lr2View(fixture, raw,
                    await app.HistoryAsync(ScenarioHistoryAction.PeriodRecent7Days, deadline, periodAnchor), keyword: false);
                PlayHistoryScenarioOracle.Lr2Raw(fixture, interval.From, interval.To);
                await app.CloseAsync(deadline);
            }
            catch (Exception failure) { primary = failure; throw; }
            finally
            {
                try { await app.DisposeAsync(); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["ScenarioCleanupFailure"] = cleanup; }
            }
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }
}
