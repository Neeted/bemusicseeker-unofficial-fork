using System.Globalization;
using System.IO;
using System.Xml.Linq;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.Scenarios.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.Recovery;

/// <summary>schema取消・SQL復元・起動時LR2保存の異なる終端を、実製品processとraw永続値で検証します。</summary>
[TestClass]
public sealed class RecoveryScenarioTests
{
    /// <summary>保証: SC13。実LR2旧schema確認の取消はDBを変更せず自然終了し、別processの承認で修復して通常検索へ到達します。</summary>
    /// <remarks>Cancel/Noneの条件表とL/P解放はCore、通常Close接続はWindows、実modal入力はDT02へ分担します。</remarks>
    [TestMethod(DisplayName = "[SC-13] 旧schema取消でDBを保ち自然終了し別processの承認で回復する")]
    [TestCategory("Full")]
    public async Task LegacySchema_CancelExitsUnchanged_ApproveInNewProcessRecovers()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = new RecoveryScenarioFixture(workspace, legacy: true);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            byte[] original = File.ReadAllBytes(fixture.SongDb);
            string schema;
            using (var db = new SQLiteConnection(fixture.SongDb, SQLiteOpenFlags.ReadOnly))
            {
                schema = db.ExecuteScalar<string>("SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_master ORDER BY name)");
                Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name='app_schema'"));
                Assert.IsFalse(db.GetTableInfo("playlist_entry").Any(column => column.Name == "sha256"));
            }
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                await app.WaitForSchemaConfirmationAsync(deadline);
                app.ExpectNaturalExit();
                await app.AnswerSchemaAsync(approve: false, deadline);
                ScenarioInitializationResult result = await app.WaitForStartupAsync(deadline);
                Assert.AreEqual(StartupInitializationOutcome.ExitRequested, result.Outcome);
                Assert.IsFalse(result.CompletionPublished);
                Assert.IsFalse(result.IsInitializationCompleted);
                Assert.IsNull(result.Failure);
                await app.WaitForExpectedExitAsync(deadline);
            }, pendingSchema: true);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.SongDb));
            using (var db = new SQLiteConnection(fixture.SongDb, SQLiteOpenFlags.ReadOnly))
            {
                Assert.AreEqual(schema, db.ExecuteScalar<string>("SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_master ORDER BY name)"));
                Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name='app_schema'"));
                AssertLegacyMembership(fixture, db);
            }
            RecoveryScenarioOracle.AssertSentinels(fixture);
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                await app.WaitForSchemaConfirmationAsync(deadline);
                await app.AnswerSchemaAsync(approve: true, deadline);
                AssertStartup(await app.WaitForStartupAsync(deadline));
                using (var db = new SQLiteConnection(fixture.SongDb, SQLiteOpenFlags.ReadOnly))
                {
                    Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name='app_schema'"));
                    Assert.IsTrue(db.GetTableInfo("playlist_entry").Any(column => column.Name == "sha256"));
                    AssertLegacyMembership(fixture, db);
                }
                ScenarioKeywordCompleted found = await app.SetKeywordAsync(fixture.Charts[0].Title, deadline);
                CollectionAssert.AreEqual(new[] { fixture.Charts[0].Md5 }, found.Snapshot.Visible.Select(chart => chart.Md5).ToArray());
                RecoveryScenarioOracle.AssertSentinels(fixture);
                await app.CloseAsync(deadline);
            }, pendingSchema: true);
            RecoveryScenarioOracle.AssertSentinels(fixture);
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    /// <summary>保証: SC14a。実設定SQL保存後のBを復元Aへ戻し、必要LR2出力・自発終了・次起動の表示と対象外値保持を確認します。</summary>
    /// <remarks>
    /// 取消・Busy・必須失敗・旧出力整理警告・全値型条件は既存Componentへ残します。外からCloseして復元終了を代替しません。
    /// 実modalの帰還を復元の成功と数えず、handler終端後のコマンド応答、制御窓口の回収、実際の退出の順で確認します。
    /// </remarks>
    [TestMethod(DisplayName = "[SC-14] SQL復元が三表と必要LR2出力を戻して自然終了し設定Bを保持する")]
    [TestCategory("Full")]
    public async Task PlaylistSql_RestorePublishesAAndExits_PreservesUnselectedB()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = new RecoveryScenarioFixture(workspace);
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            var savedFolderOrders = new Dictionary<int, string>();
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                RecoveryScenarioOracle.AssertPlaylist(fixture);
                // 業務上の順序は独立manifestへ照合済み。整形の書式を仕様化せず、SQL往復では実保存文字列も保持します。
                using (var before = new SQLiteConnection(fixture.SongDb, SQLiteOpenFlags.ReadOnly))
                {
                    foreach (int id in new[] { RecoveryScenarioFixture.MainId, RecoveryScenarioFixture.CourseTableId })
                    { savedFolderOrders.Add(id, before.ExecuteScalar<string>("SELECT folder_order FROM playlist WHERE playlist_id=?", id)); }
                }
                await app.PlaylistSqlAsync(fixture.SqlPath, restore: false, deadline);
                RecoveryScenarioOracle.AssertSqlBackup(fixture, workspace.GetPath("replayed.sql.db"), savedFolderOrders);
                await app.SelectPlaylistAsync(RecoveryScenarioFixture.MainId, deadline);
                await app.EditPlaylistAsync(fixture.Charts[0].Md5, "変更後 B", level: false, deadline);
                await app.AddPlaylistRowsAsync(RecoveryScenarioFixture.MainId, [fixture.Charts[2].Md5], null, deadline);
                await app.RemovePlaylistAsync(RecoveryScenarioFixture.CourseTableId, deadline);
                await app.EditSettingsAsync(RecoveryScenarioFixture.EditedFont, workspace.GetPath("cancelled-path"), deadline);
                using (var db = new SQLiteConnection(fixture.SongDb, SQLiteOpenFlags.ReadOnly))
                {
                    Assert.AreEqual("変更後 B", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id=? AND md5=?", RecoveryScenarioFixture.MainId, fixture.Charts[0].Md5));
                    Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE md5=?", fixture.Charts[2].Md5));
                    Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_course"));
                    Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist"));
                }
                RecoveryScenarioOracle.AssertSettingsB(fixture);
                app.ExpectNaturalExit();
                await app.PlaylistSqlAsync(fixture.SqlPath, restore: true, deadline);
                await app.WaitForExpectedExitAsync(deadline);
            });
            // 次起動が必要出力・DB保存漏れを修復する前に、退出直後の実永続値を照合します。
            RecoveryScenarioOracle.AssertPlaylist(fixture, savedFolderOrders: savedFolderOrders);
            RecoveryScenarioOracle.AssertLr2Output(fixture);
            RecoveryScenarioOracle.AssertSentinels(fixture);
            RecoveryScenarioOracle.AssertSettingsB(fixture);
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                ScenarioPlaylistSnapshot summary = (await app.SelectPlaylistAsync(null, deadline)).Snapshot;
                CollectionAssert.AreEquivalent(new[] { RecoveryScenarioFixture.MainId, RecoveryScenarioFixture.CourseTableId }, summary.SummaryTableIds.ToArray());
                ScenarioPlaylistSnapshot detail = (await app.SelectPlaylistAsync(RecoveryScenarioFixture.MainId, deadline)).Snapshot;
                CollectionAssert.AreEquivalent(new[] { fixture.Charts[0].Md5, fixture.Charts[1].Md5 }, detail.Rows.Select(row => row.Md5).ToArray());
                Assert.AreEqual(RecoveryScenarioFixture.Memo, detail.Rows.Single(row => row.Md5 == fixture.Charts[0].Md5).Memo);
                Assert.AreEqual("Alpha", detail.Rows.Single(row => row.Md5 == fixture.Charts[0].Md5).Folder);
                Assert.AreEqual("Beta", detail.Rows.Single(row => row.Md5 == fixture.Charts[1].Md5).Folder);
                Assert.AreEqual(RecoveryScenarioFixture.EditedFont, detail.MainFontSize);
                Assert.AreEqual(RecoveryScenarioFixture.EditedFont, detail.SummaryFontSize);
                RecoveryScenarioOracle.AssertPlaylist(fixture, savedFolderOrders: savedFolderOrders);
                RecoveryScenarioOracle.AssertSettingsB(fixture);
                await app.CloseAsync(deadline);
            });
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    /// <summary>保証: SC14b。実起動が全選択対象と全Score subtreeをVACUUM前に保存し、新世代と最新旧世代だけを保持します。</summary>
    /// <remarks>Saved/Failureの条件表、none・間隔・欠落・prune失敗・最適化失敗は既存Coreへ分担します。物理最適化後の容量を固定しません。</remarks>
    [TestMethod(DisplayName = "[SC-14] 起動時LR2バックアップが全ScoreとVACUUM前bytesを保存し二世代を保持する")]
    [TestCategory("Full")]
    public async Task StartupBackup_PublishesAllScoreBeforeVacuum_AndKeepsTwoGenerations()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = new RecoveryScenarioFixture(workspace, startupBackup: true);
            string latestOld = Directory.GetDirectories(fixture.Backups).Order(StringComparer.Ordinal).Last();
            byte[] oldSentinel = File.ReadAllBytes(Path.Combine(latestOld, "old.txt"));
            DateTime startDate = DateTime.Today;
            var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromMinutes(3));
            await RunAsync(fixture, workspace, deadline, async app =>
            {
                AssertStartup(await app.WaitForStartupAsync(deadline));
                DateTime endDate = DateTime.Today;
                string[] generations = Directory.GetDirectories(fixture.Backups);
                Assert.AreEqual(2, generations.Length);
                Assert.IsTrue(generations.Contains(latestOld));
                CollectionAssert.AreEqual(oldSentinel, File.ReadAllBytes(Path.Combine(latestOld, "old.txt")));
                string current = generations.Single(path => path != latestOld);
                var date = DateTime.ParseExact(Path.GetFileName(current), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                Assert.IsTrue(date >= startDate && date <= endDate);
                CollectionAssert.AreEquivalent(new[] { "config.xml", "song.db", "Score" }, Directory.GetFileSystemEntries(current).Select(Path.GetFileName).ToArray());
                string scores = Path.Combine(current, "Score");
                CollectionAssert.AreEquivalent(fixture.ScoreFiles.Keys.ToArray(), Directory.GetFiles(scores, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(scores, path)).ToArray());
                foreach ((string relative, byte[] bytes) in fixture.ScoreFiles)
                { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(scores, relative)), relative); }
                var savedConfig = XDocument.Load(Path.Combine(current, "config.xml"));
                Assert.AreEqual("selected", savedConfig.Root?.Element("player")?.Element("id")?.Value);
                using (var song = new SQLiteConnection(Path.Combine(current, "song.db"), SQLiteOpenFlags.ReadOnly))
                {
                    Assert.AreEqual(1, song.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name='app_schema'"));
                    Assert.AreEqual(3, song.ExecuteScalar<int>("SELECT count(*) FROM song WHERE tag='利用者タグ' AND favorite=3"));
                    Assert.AreEqual("unknown user table", song.ExecuteScalar<string>("SELECT value FROM user_notes WHERE id=9"));
                }
                foreach ((string relative, int sentinel) in new[] { ("selected.db", 101), ("other.db", 202), (Path.Combine("nested", "deep", "history.db"), 303) })
                {
                    using var source = new SQLiteConnection(Path.Combine(fixture.ScoreRoot, relative), SQLiteOpenFlags.ReadOnly);
                    using var backup = new SQLiteConnection(Path.Combine(scores, relative), SQLiteOpenFlags.ReadOnly);
                    Assert.AreEqual(sentinel, source.ExecuteScalar<int>("SELECT value FROM sentinel"));
                    Assert.AreEqual("before VACUUM", source.ExecuteScalar<string>("SELECT note FROM sentinel"));
                    Assert.AreEqual(sentinel, source.ExecuteScalar<int>("SELECT perfect FROM score WHERE hash=?", fixture.Charts[0].Md5));
                    Assert.AreEqual(backup.ExecuteScalar<string>("SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_master ORDER BY name)"),
                        source.ExecuteScalar<string>("SELECT group_concat(sql,'|') FROM (SELECT sql FROM sqlite_master ORDER BY name)"));
                    if (sentinel == 303)
                    {
                        Assert.IsTrue(backup.ExecuteScalar<int>("PRAGMA freelist_count") > 0, "保存物には最適化前の未使用pageが残ります。");
                        Assert.AreEqual(0, source.ExecuteScalar<int>("PRAGMA freelist_count"), "実起動の最適化が原本の未使用pageを解消します。");
                    }
                }
                foreach ((string path, byte[] bytes) in fixture.OriginalFiles) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
                await app.CloseAsync(deadline);
            });
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    private static void AssertLegacyMembership(RecoveryScenarioFixture fixture, SQLiteConnection db)
    {
        Dictionary<string, object?>[] expectedRows = fixture.Manifest["playlist_entry"];
        Assert.AreEqual(expectedRows.Length, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry"));
        foreach (Dictionary<string, object?> row in expectedRows)
        {
            object?[] key = [row["playlist_id"], row["md5"]];
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id=? AND md5=?", key));
            Assert.AreEqual(row["folder"], db.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id=? AND md5=?", key));
            Assert.AreEqual(row["memo"] is null ? "null" : "text",
                db.ExecuteScalar<string>("SELECT typeof(memo) FROM playlist_entry WHERE playlist_id=? AND md5=?", key));
            Assert.AreEqual(row["memo"], db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id=? AND md5=?", key));
        }
    }

    private static void AssertStartup(ScenarioInitializationResult result)
    {
        Assert.AreEqual(StartupInitializationOutcome.Succeeded, result.Outcome);
        Assert.IsTrue(result.CompletionPublished);
        Assert.IsTrue(result.IsInitializationCompleted);
        Assert.IsNull(result.Failure, result.Failure?.StackTrace);
    }

    private static async Task RunAsync(RecoveryScenarioFixture fixture, TestWorkspace workspace, ProcessDeadline deadline,
        Func<ApplicationScenarioDriver, Task> body, bool pendingSchema = false)
    {
        string executable = ScenarioApplicationPaths.ExecutablePath;
        ApplicationScenarioDriver app = await ApplicationScenarioDriver.StartAsync(executable, fixture.Profile,
            workspace.GetPath("session.json"), workspace.CaseId, fixture.UpdateUrl, deadline, pendingSchema);
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
