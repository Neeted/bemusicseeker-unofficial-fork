using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Ribbit.Net;
using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>外部表取得の対象失敗と操作失敗を、実取得・SQLite・Workspace・STATUSの境界で確認します。</summary>
[TestClass]
public sealed class PlaylistExternalFailureBoundaryTests
{
    /// <summary>
    /// 通常の取得失敗で全体同期が例外終了せず、旧表・成功表のDB・STATUSと受付解放を保つことを確認します。
    /// 全対象失敗も全件成功とは通知しません。明示的な再取得では同じ表の失敗状態が解消します。
    /// 進捗表示が失敗する行でも対象成否・DB・後続購読者への終端を保ち、成功した次操作へ失敗を持ち越しません。
    /// 原因一覧外の実NULパス失敗と起動schedulerの実work Taskも同じ混在入力で確認します。
    /// HTTPは既存loopback fixture、JSONは実ファイルを使い、WPF Windowや外部ネットワークは使いません。
    /// </summary>
    [DataTestMethod]
    [DataRow(404, "http", true, (int)PlaylistSyncStatusKind.Http404, false)]
    [DataRow(403, "http", true, (int)PlaylistSyncStatusKind.Http403, false)]
    [DataRow(400, "http", true, (int)PlaylistSyncStatusKind.HttpError, false)]
    [DataRow(200, "header", true, (int)PlaylistSyncStatusKind.HeaderParseError, false)]
    [DataRow(200, "data", true, (int)PlaylistSyncStatusKind.DataParseError, false)]
    [DataRow(200, "entry", true, (int)PlaylistSyncStatusKind.DataParseError, false)]
    [DataRow(200, "url", true, (int)PlaylistSyncStatusKind.InvalidDataUrl, false)]
    [DataRow(200, "page-scheme", true, (int)PlaylistSyncStatusKind.InvalidDataUrl, false)]
    [DataRow(200, "header-scheme", true, (int)PlaylistSyncStatusKind.InvalidDataUrl, false)]
    [DataRow(200, "data-scheme", true, (int)PlaylistSyncStatusKind.InvalidDataUrl, false)]
    [DataRow(200, "missing-file", true, (int)PlaylistSyncStatusKind.UnknownError, false)]
    [DataRow(200, "unknown-path", true, (int)PlaylistSyncStatusKind.UnknownError, false)]
    [DataRow(200, "unknown-startup", true, (int)PlaylistSyncStatusKind.UnknownError, false)]
    [DataRow(200, "oversized-html", true, (int)PlaylistSyncStatusKind.UnknownError, false)]
    [DataRow(200, "array-header", true, (int)PlaylistSyncStatusKind.HeaderParseError, false)]
    [DataRow(200, "json-with-html-meta", true, (int)PlaylistSyncStatusKind.HeaderParseError, false)]
    [DataRow(200, "header-not-found", true, (int)PlaylistSyncStatusKind.HeaderNotFound, false)]
    [DataRow(404, "http", false, (int)PlaylistSyncStatusKind.Http404, false)]
    [DataRow(200, "data", true, (int)PlaylistSyncStatusKind.DataParseError, true)]
    [DataRow(200, "data", false, (int)PlaylistSyncStatusKind.DataParseError, true)]
    [TestCategory("Playlist")]
    public async Task ExternalSync_SourceFailurePreservesStatusAndCommittedTables(
        int statusCode, string malformedPart, bool includeSuccessfulTarget, int expectedKind, bool failProgress)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistExternalFailureBoundaryTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SingleRequestHttpServer? server = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Func<Task>? scheduledWork = null;
        Task? startupTask = null;
        try
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            string songPath = CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string header = Path.Combine(directory, "good-header.json");
            string data = Path.Combine(directory, "good-data.json");
            string badHeader = Path.Combine(directory, "bad-header.json");
            string badData = Path.Combine(directory, "bad-data.json");
            const string oldData = "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]";
            File.WriteAllText(header, "{\"name\":\"Good\",\"symbol\":\"G\",\"data_url\":\"good-data.json\"}");
            File.WriteAllText(data, oldData);
            File.WriteAllText(badHeader, "{\"name\":\"Bad\",\"symbol\":\"B\",\"data_url\":\"bad-data.json\"}");
            File.WriteAllText(badData, oldData);
            var library = new TestBmsLibrary(songPath, settings: settings);
            var playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songPath, CustomFolderOutputPhysicalSurface.Empty),
                settings: settings);
            BMSTable good = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            BMSTable bad = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(badHeader));
            good.playlist_id = 7901;
            bad.playlist_id = 7902;
            good.is_bmt_output = false;
            bad.is_bmt_output = false;
            bad.entries[0].memo = "keep-local-memo";
            if (malformedPart == "http")
            {
                server = new SingleRequestHttpServer([], responseStatusCode: (HttpStatusCode)statusCode);
                bad.Page_url = server.Address;
                bad.Header_url = server.Address;
            }
            Assert.IsTrue(good.EnableExternalSync());
            Assert.IsTrue(bad.EnableExternalSync());
            playlist.BMSTables = new ObservableCollection<BMSTable>(includeSuccessfulTarget ? [good, bad] : [bad]);
            playlist.CommitBMSTablesWithEntriesToDB(playlist.BMSTables);
            DateTime oldUpdate = bad.last_update;
            string oldHash = bad.data_sha256;
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"After\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
            switch (malformedPart)
            {
                case "array-header": File.WriteAllText(badHeader, "[]"); break;
                case "oversized-html": File.WriteAllText(badHeader, "<html>" + new string(' ', ExternalPlaylistContentReader.HtmlLimitBytes) + "<meta name=\"bmstable\" content=\"good-header.json\">"); break;
                case "json-with-html-meta": File.WriteAllText(badHeader, "{ invalid json }<html><meta name=\"bmstable\" content=\"good-header.json\"></html>"); break;
                case "header": File.WriteAllText(badHeader, "{ invalid json }"); break;
                case "data": File.WriteAllText(badData, "{ invalid json }"); break;
                case "missing-file": File.Delete(badData); break;
                case "unknown-path":
                case "unknown-startup":
                    // 明示file URIでは%00がNULへ展開され、読取りが原因一覧外のArgumentExceptionになる。
                    // DOS path由来の暗黙file URIでは%00がliteralのため、この境界を通らない。
                    bad.Page_url = new Uri(new Uri(badHeader).AbsoluteUri);
                    bad.Header_url = bad.Page_url;
                    File.WriteAllText(badHeader, "{\"name\":\"Bad\",\"symbol\":\"B\",\"data_url\":\"bad%00-data.json\"}");
                    break;
                case "header-not-found": File.WriteAllText(badHeader, "<html><body>No table</body></html>"); break;
                case "entry": File.WriteAllText(badData, "[1]"); break;
                case "url": File.WriteAllText(badHeader, "{\"name\":\"Bad\",\"symbol\":\"B\"}"); break;
                case "page-scheme": bad.Page_url = new Uri("ftp://example.invalid/table.json"); break;
                case "header-scheme": File.WriteAllText(badHeader, "<html><head><meta name=\"bmstable\" content=\"ftp://example.invalid/header.json\"></head></html>"); break;
                case "data-scheme": File.WriteAllText(badHeader, "{\"name\":\"Bad\",\"symbol\":\"B\",\"data_url\":\"ftp://example.invalid/data.json\"}"); break;
            }
            if (malformedPart == "unknown-startup")
            {
                workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _,
                    playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library,
                    externalSyncScheduler: (_, work) => { scheduledWork = work; return true; });
                workspace.RefreshPlaylistTreeTables(playlist, library);
            }
            else { workspace = CreatePlaylistWorkspace(playlist, library); }
            var progress = new List<PlaylistSyncProgressSnapshot>();
            var execution = new List<bool>();
            int queuedCount = 0;
            int referenceAppliedCount = 0;
            if (failProgress)
            {
                workspace.PlaylistExternalSyncQueued += (_, _) => throw new InvalidOperationException("queued display failed");
                workspace.PlaylistExternalSyncReferenceApplied += (_, _) => throw new InvalidOperationException("reference progress failed");
                workspace.PlaylistExternalSyncCompleted += (_, _) => throw new InvalidOperationException("completed display failed");
                workspace.PlaylistSyncProgressChanged += (_, _) => throw new InvalidOperationException("progress display failed");
            }
            workspace.PlaylistExternalSyncQueued += (_, _) => queuedCount++;
            workspace.PlaylistExternalSyncReferenceApplied += (_, _) => referenceAppliedCount++;
            workspace.PlaylistSyncProgressChanged += (_, change) => progress.Add(change.Snapshot);
            workspace.RequestProgressReporter = (_, running) =>
            {
                execution.Add(running);
                if (failProgress) { throw new InvalidOperationException("request display failed"); }
            };
            int completedCount = 0;
            bool reportedSkipped = true;
            bool reportedSuccess = true;
            workspace.PlaylistExternalSyncCompleted += (_, completion) =>
            {
                completedCount++;
                reportedSuccess = completion.Succeeded;
                reportedSkipped = completion.WasSkipped;
            };

            // 全体再読込みが使う同期入口を直接待つ。失敗をAssert.Throwsで成功条件にしません。
            if (malformedPart == "unknown-startup")
            {
                // 起動時と同じ予約入口で、schedulerが所有する実work Taskを終端まで待つ。
                workspace.QueueExternalPlaylistSync("Initialize", false, true, 1);
                Assert.IsNotNull(scheduledWork);
                startupTask = Task.Run(scheduledWork);
                await startupTask;
                Assert.IsTrue(startupTask.IsCompletedSuccessfully);
            }
            else { await workspace.RunExternalPlaylistSyncAsync("source-failure", true, true, 1); }
            Assert.AreEqual(1, completedCount);
            Assert.IsFalse(reportedSuccess);
            Assert.IsFalse(reportedSkipped);
            Assert.AreEqual(1, queuedCount);
            Assert.AreEqual(1, referenceAppliedCount);
            Assert.IsTrue(progress.Any(snapshot => snapshot.IsActive));
            Assert.IsFalse(progress.Last().IsActive);
            CollectionAssert.AreEqual(new[] { true, false }, execution);
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            bool admissionAccepted = playlist.TryEnterPlaylistMutation(out IDisposable admission);
            admission?.Dispose();
            Assert.IsTrue(admissionAccepted, "失敗した同期の受付を解放します。");
            Assert.AreSame(bad, playlist.BMSTables.Single(table => table.playlist_id == 7902));
            Assert.AreEqual(oldUpdate, bad.last_update);
            Assert.AreEqual(oldHash, bad.data_sha256);
            Assert.AreEqual("keep-local-memo", bad.entries.Single().memo);
            PlaylistSyncRuntimeStatus status = workspace.CapturePlaylistSyncStatusSnapshot()["id:7902"];
            Assert.AreEqual((PlaylistSyncStatusKind)expectedKind, status.Kind);
            Assert.IsTrue(status.HasFailureStatus);
            Assert.IsFalse(string.IsNullOrWhiteSpace(status.StatusText));
            Assert.IsFalse(status.Detail.Contains("System.AggregateException", StringComparison.Ordinal));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == 7902));
                LR2SongDBExtended.playlist retained = db.Find<LR2SongDBExtended.playlist>(7902);
                Assert.AreEqual(oldHash, retained.data_sha256);
                Assert.AreEqual(oldUpdate, retained.last_update);
                LR2SongDBExtended.playlist_entry retainedEntry = db.Table<LR2SongDBExtended.playlist_entry>().Where(entry => entry.playlist_id == 7902).Single();
                Assert.AreEqual("Before", retainedEntry.title);
                Assert.AreEqual("keep-local-memo", retainedEntry.memo);
                if (includeSuccessfulTarget)
                {
                    Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == 7901));
                    Assert.AreEqual(2, playlist.BMSTables.Single(table => table.playlist_id == 7901).entries.Count);
                    Assert.IsFalse(workspace.CapturePlaylistSyncStatusSnapshot()["id:7901"].HasFailureStatus);
                }
            }

            if (malformedPart is "unknown-path" or "unknown-startup")
            {
                // 原因一覧外の実読取り失敗は対象に残り、一般操作のException/ThrowIfFailedへ戻らない。
                PlaylistExternalSyncOwner.PlaylistReloadOperationResult result =
                    await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([bad]);
                Assert.IsInstanceOfType<ArgumentException>(result.Single().Exception);
                Assert.IsTrue(result.Single().IsHandledExternalLoadFailure);
                Assert.IsFalse(result.Single().Succeeded);
                Assert.IsNull(result.Exception);
                result.ThrowIfFailed();
            }

            // 同じ対象の入力を修正して明示的に再取得する。過去の失敗を次操作へ持ち越しません。
            bad.Page_url = new Uri(badHeader);
            bad.Header_url = new Uri(badHeader);
            File.WriteAllText(badHeader, "{\"name\":\"Bad\",\"symbol\":\"B\",\"data_url\":\"bad-data.json\"}");
            File.WriteAllText(badData, oldData);
            playlist.CommitBMSTableHeaderToDB(bad);
            await workspace.ResyncPlaylistsAsync([bad]);
            Assert.IsFalse(workspace.CapturePlaylistSyncStatusSnapshot()["id:7902"].HasFailureStatus);

            // 同じ表示障害を残した全件成功の同期でも、実結果・終端・次の受付を保持します。
            await workspace.RunExternalPlaylistSyncAsync("source-recovered", true, true, 2);
            Assert.AreEqual(2, completedCount);
            Assert.IsTrue(reportedSuccess);
            Assert.IsFalse(reportedSkipped);
            Assert.AreEqual(2, queuedCount);
            Assert.AreEqual(2, referenceAppliedCount);
            Assert.IsFalse(progress.Last().IsActive);
            CollectionAssert.AreEqual(new[] { true, false, true, false }, execution);
            bool freshAdmissionAccepted = playlist.TryEnterPlaylistMutation(out IDisposable freshAdmission);
            freshAdmission?.Dispose();
            Assert.IsTrue(freshAdmissionAccepted);
        }
        finally
        {
            if (startupTask != null) { try { await startupTask; } catch (Exception) { } }
            if (workspace != null)
            {
                await workspace.WaitForDetailBuildIdleAsync();
                await workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
                await workspace.WaitForPlaylistReloadCleanupIdleAsync();
            }
            if (server != null) { await server.DisposeAsync(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// HTTPと不正ヘッダーの実取得で、取得境界が元例外・取得元を診断し、STATUSへ分類だけを渡します。
    /// 診断先が失敗する行でも元原因を置き換えません。Workspaceの表示・参照・DB保持は実再取得テストへ分担します。
    /// </summary>
    [DataTestMethod]
    [DataRow(403, "", false, (int)PlaylistSyncStatusKind.Http403)]
    [DataRow(403, "", true, (int)PlaylistSyncStatusKind.Http403)]
    [DataRow(200, "{", false, (int)PlaylistSyncStatusKind.HeaderParseError)]
    [DataRow(200, "{", true, (int)PlaylistSyncStatusKind.HeaderParseError)]
    [TestCategory("Playlist")]
    public async Task ExternalLoad_ReportsOriginalFailureWithoutRetainingItInStatus(
        int statusCode, string responseBody, bool failLogging, int expectedKind)
    {
        await using var server = new SingleRequestHttpServer(
            Encoding.UTF8.GetBytes(responseBody), responseStatusCode: (HttpStatusCode)statusCode);
        var recommended = new PlaylistRecommendedTableOwner(
            static _ => Task.FromException<WalkureScoreInput>(new InvalidOperationException("内蔵推薦は対象外です。")),
            new PlaylistOperationNotificationOwner(),
            static () => new CustomFolderOutputSettingsSnapshot());
        var diagnostics = new ConcurrentQueue<(Exception Exception, string Message)>();
        var owner = new PlaylistExternalSyncOwner(AppHttpClient.Shared, recommended,
            (exception, message) =>
            {
                diagnostics.Enqueue((exception, message));
                if (failLogging) { throw new IOException("診断先への書込みに失敗しました。"); }
            },
            static () => false, static _ => { });

        Exception failure;
        if (statusCode == (int)HttpStatusCode.OK)
        {
            failure = await Assert.ThrowsExceptionAsync<PlaylistHeaderParseException>(
                () => owner.LoadExternalTableAsync(server.Address));
            Assert.IsInstanceOfType(failure.InnerException, typeof(JsonReaderException));
        }
        else
        {
            failure = await Assert.ThrowsExceptionAsync<HttpRequestException>(
                () => owner.LoadExternalTableAsync(server.Address));
        }

        Assert.AreEqual(1, diagnostics.Count);
        (Exception loggedException, string loggedMessage) = diagnostics.Single();
        Assert.AreSame(failure, loggedException, "元原因を含む例外を診断先へ渡します。");
        StringAssert.Contains(loggedMessage, "playlist_external_load_failed");
        StringAssert.Contains(loggedMessage, "pageUri=" + server.Address);

        var status = PlaylistSyncAttemptResult.CreateReportedExternalFailure(
            null, server.Address, failure);
        Assert.AreEqual((PlaylistSyncStatusKind)expectedKind, status.FailureKind);
        Assert.AreEqual(server.Address, status.PageUri);
        Assert.IsFalse(status.Succeeded);
        Assert.IsNull(status.Exception);
        Assert.AreEqual(string.Empty, status.Detail);
    }

    /// <summary>
    /// 外部取得が成功しても実SQLite保存の失敗を処理済み取得失敗にせず、旧正本と受付終端を保って表面化します。
    /// 故障triggerを除いた次の明示要求で同じ入力を確定でき、保存失敗を取得失敗として捨てる誤りを検出します。
    /// </summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ExternalReload_DatabaseSaveFailureRemainsUnhandledAndPreservesCommittedTable()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistExternalFailureBoundaryTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string songPath = CreateTempSongDbPath(directory);
        bool triggerCreated = false;
        try
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            string header = Path.Combine(directory, "header.json"), data = Path.Combine(directory, "data.json");
            File.WriteAllText(header, "{\"name\":\"External\",\"symbol\":\"E\",\"data_url\":\"data.json\"}");
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"level\":\"1\"}]");
            var playlist = new TestBmsPlaylist(songPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                CreateDeterministicLr2PlaylistFolderSynchronizationPort(songPath, CustomFolderOutputPhysicalSurface.Empty), settings: settings);
            BMSTable oldTable = await playlist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(header));
            oldTable.playlist_id = 7903;
            oldTable.is_bmt_output = false;
            Assert.IsTrue(oldTable.EnableExternalSync());
            playlist.BMSTables = new ObservableCollection<BMSTable>([oldTable]);
            playlist.CommitBMSTableWithEntriesToDB(oldTable);
            string oldHash = oldTable.data_sha256;
            File.WriteAllText(data, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"After\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Added\",\"level\":\"2\"}]");
            using (var db = new LR2SongDBExtended(songPath))
            {
                db.Execute("CREATE TRIGGER external_save_failure BEFORE INSERT ON playlist WHEN NEW.playlist_id = 7903 BEGIN SELECT RAISE(ABORT, 'external DB save failure'); END;");
                triggerCreated = true;
            }

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult result =
                await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([oldTable]);
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult target = result.Single();
            Assert.IsFalse(target.IsHandledExternalLoadFailure);
            Assert.IsFalse(target.Succeeded);
            Assert.IsFalse(target.StatePersisted);
            Assert.IsNotNull(target.Exception);
            Assert.IsNotNull(result.Exception);
            StringAssert.Contains(result.Exception.ToString(), "external DB save failure");
            Exception? surfaced = null;
            try { result.ThrowIfFailed(); } catch (Exception failure) { surfaced = failure; }
            Assert.IsNotNull(surfaced);
            StringAssert.Contains(surfaced?.ToString() ?? string.Empty, "external DB save failure");
            Assert.AreSame(oldTable, playlist.BMSTables.Single());
            Assert.AreEqual(oldHash, oldTable.data_sha256);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(oldHash, db.Table<BMSTable>().Single().data_sha256);
                Assert.AreEqual("Before", db.Table<LR2SongDBExtended.playlist_entry>().Single().title);
            }
            Assert.IsFalse(playlist.IsPlaylistUpdating);
            bool accepted = playlist.TryEnterPlaylistMutation(out IDisposable admission);
            admission?.Dispose();
            Assert.IsTrue(accepted);
            using (var db = new LR2SongDBExtended(songPath)) { db.Execute("DROP TRIGGER external_save_failure;"); }
            triggerCreated = false;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult next =
                await playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync([oldTable]);
            next.ThrowIfFailed();
            Assert.IsTrue(next.Single().Succeeded);
            Assert.AreEqual(2, playlist.BMSTables.Single().entries.Count);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songPath).OpenSongDbReadOnly();
            Assert.AreEqual(2, verify.Table<LR2SongDBExtended.playlist_entry>().Count());
        }
        finally
        {
            if (triggerCreated)
            {
                using var cleanup = new LR2SongDBExtended(songPath);
                cleanup.Execute("DROP TRIGGER external_save_failure;");
            }
            Directory.Delete(directory, recursive: true);
        }
    }

}
