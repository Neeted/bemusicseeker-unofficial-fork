using System.IO;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Scenarios.Tests.Playlist;

/// <summary>実Appで外部表・ローカル表・設定の通常操作と保存を一回縦断します。</summary>
[TestClass]
public sealed class PlaylistDailyScenarioTests
{
    /// <summary>保証: SC09。外部同期ONのMEMO、実property登録、通常行追加、Level・MEMO・所属・フォルダ順、JSON二出力と設定取消が実保存と別processへ接続します。</summary>
    /// <remarks>UIA入力と同期ONのContentRendered起動はDT01へ、編集権限・失敗・出力部分失敗の条件表は既存Componentsへ分担します。</remarks>
    [TestMethod(DisplayName = "[SC-09] プレイリスト編集・JSON・即時設定を通常保存と別processで保持する")]
    [TestCategory("Daily")]
    public async Task PlaylistAndSettings_RealOperationsPersistAcrossNewProcess()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = Sc09PlaylistFixture.Create(workspace);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            int externalId = 0;
            int localId = 0;
            string[] identities = [];
            await RunApplicationAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                ScenarioPlaylistCompleted imported = await app.ImportPlaylistAsync(fixture.ExternalUrl, deadline);
                Assert.AreEqual(1, imported.Import.Length);
                Assert.AreEqual(fixture.ExternalUrl, imported.Import[0].Uri);
                Assert.AreEqual(Sc09PlaylistFixture.ExternalName, imported.Import[0].Name);
                Assert.AreEqual("Imported", imported.Import[0].Kind);
                CollectionAssert.AreEquivalent(new[] { Sc09PlaylistFixture.ExternalName }, imported.Snapshot.Tables.Select(table => table.Name).ToArray());
                externalId = imported.Snapshot.Tables.Single().Id;
                Assert.IsTrue(imported.Snapshot.Tables.Single().ExternalSync);
                AssertSummary((await app.SelectPlaylistAsync(null, deadline)).Snapshot, [externalId]);
                Sc09PlaylistOracle.AssertExternal(fixture, externalId, (await app.SelectPlaylistAsync(externalId, deadline)).Snapshot, edited: false);
                Sc09PlaylistOracle.AssertExternal(fixture, externalId,
                    (await app.EditPlaylistAsync(fixture.A.Md5, Sc09PlaylistFixture.ExternalMemo, level: false, deadline)).Snapshot, edited: true);

                ScenarioPlaylistSnapshot created = (await app.CreatePlaylistAsync(Sc09PlaylistFixture.LocalName, Sc09PlaylistFixture.LocalSymbol, deadline)).Snapshot;
                CollectionAssert.AreEquivalent(new[] { Sc09PlaylistFixture.ExternalName, Sc09PlaylistFixture.LocalName }, created.Tables.Select(table => table.Name).ToArray());
                localId = created.Tables.Single(table => table.Name == Sc09PlaylistFixture.LocalName).Id;
                Assert.IsTrue(localId > 0 && localId != externalId);
                Assert.IsFalse(created.Tables.Single(table => table.Id == localId).ExternalSync);
                await app.AddPlaylistRowsAsync(localId, [fixture.A.Md5, fixture.B.Md5], null, deadline);
                await app.SelectPlaylistAsync(localId, deadline);
                ScenarioPlaylistSnapshot level = (await app.EditPlaylistAsync(fixture.A.Md5, "12.5", level: true, deadline)).Snapshot;
                Sc09PlaylistOracle.AssertLocalEdit(fixture, localId, level, memoEdited: false);
                ScenarioPlaylistSnapshot memo = (await app.EditPlaylistAsync(fixture.A.Md5, Sc09PlaylistFixture.LocalMemo, level: false, deadline)).Snapshot;
                Sc09PlaylistOracle.AssertLocalEdit(fixture, localId, memo, memoEdited: true);
                await app.CreatePlaylistFolderAsync(localId, Sc09PlaylistFixture.Alpha, deadline);
                await app.CreatePlaylistFolderAsync(localId, Sc09PlaylistFixture.Beta, deadline);
                await app.SelectPlaylistAsync(localId, deadline);
                await app.AddPlaylistRowsAsync(localId, [fixture.A.Md5], Sc09PlaylistFixture.Alpha, deadline);
                await app.SelectPlaylistAsync(localId, deadline);
                await app.AddPlaylistRowsAsync(localId, [fixture.B.Md5], Sc09PlaylistFixture.Beta, deadline);
                await app.MovePlaylistFolderAsync(localId, Sc09PlaylistFixture.Alpha, down: true, deadline);
                Sc09PlaylistOracle.AssertLocal(fixture, localId, (await app.SelectPlaylistAsync(localId, deadline)).Snapshot);
                identities = Sc09PlaylistOracle.AssertFinalDatabase(fixture, externalId, localId);
                await app.ExportPlaylistAsync(localId, fixture.HeaderPath, fixture.DataPath, deadline);
                Sc09PlaylistOracle.AssertJson(fixture);
                ScenarioPlaylistCompleted settings = await app.EditSettingsAsync(Sc09PlaylistFixture.EditedFont, fixture.CandidatePath, deadline);
                Assert.IsNotNull(settings.SettingsEdit);
                Assert.AreEqual(Sc09PlaylistFixture.EditedFont, settings.SettingsEdit.ProviderFont);
                Assert.AreEqual(Sc09PlaylistFixture.EditedFont, double.Parse(settings.SettingsEdit.RawFont, System.Globalization.CultureInfo.InvariantCulture));
                Assert.AreEqual(string.Empty, settings.SettingsEdit.ProviderPath);
                Assert.AreEqual(string.Empty, settings.SettingsEdit.RawPath);
                Assert.AreEqual(fixture.CandidatePath, settings.SettingsEdit.CandidateBeforeClose);
                Assert.AreEqual(string.Empty, settings.SettingsEdit.CandidateAfterClose);
                Sc09PlaylistOracle.AssertSettings(fixture, settings.Snapshot);
                AssertSummary((await app.SelectPlaylistAsync(null, deadline)).Snapshot, [externalId, localId]);
                Sc09PlaylistOracle.AssertFinalDatabase(fixture, externalId, localId);
                await app.CloseAsync(deadline);
            });

            // 閉鎖前のDB検査後に別processを明示起動し、テスト側から保存値を補充しません。
            await RunApplicationAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                AssertSummary((await app.SelectPlaylistAsync(null, deadline)).Snapshot, [externalId, localId]);
                Sc09PlaylistOracle.AssertExternal(fixture, externalId, (await app.SelectPlaylistAsync(externalId, deadline)).Snapshot, edited: true);
                ScenarioPlaylistSnapshot restored = (await app.SelectPlaylistAsync(localId, deadline)).Snapshot;
                Sc09PlaylistOracle.AssertLocal(fixture, localId, restored);
                Sc09PlaylistOracle.AssertSettings(fixture, restored);
                CollectionAssert.AreEqual(identities, Sc09PlaylistOracle.AssertFinalDatabase(fixture, externalId, localId));
                await app.CloseAsync(deadline);
            });
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    private static void AssertSummary(ScenarioPlaylistSnapshot snapshot, int[] ids)
    {
        Assert.IsTrue(snapshot.SummaryGeneration > 0);
        CollectionAssert.AreEquivalent(ids, snapshot.Tables.Select(table => table.Id).ToArray());
        CollectionAssert.AreEquivalent(ids, snapshot.SummaryTableIds.ToArray());
    }
    private static void AssertStartup(ScenarioInitializationResult result)
    {
        Assert.AreEqual(StartupInitializationOutcome.Succeeded, result.Outcome);
        Assert.IsTrue(result.CompletionPublished);
        Assert.IsTrue(result.IsInitializationCompleted);
        Assert.IsNull(result.Failure, result.Failure?.StackTrace);
    }
    private static async Task RunApplicationAsync(Sc09PlaylistFixture fixture, TestWorkspace workspace, ProcessDeadline deadline,
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
            catch (Exception cleanup) when (primary is not null) { primary.Data["ScenarioCleanupFailure"] = cleanup; }
        }
    }
}
