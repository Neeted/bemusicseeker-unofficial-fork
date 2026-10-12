using System;
using System.Collections.Concurrent;
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
    /// HTTPは既存loopback fixture、JSONは実ファイルを使い、WPF Windowや外部ネットワークは使いません。
    /// </summary>
    [DataTestMethod]
    [DataRow(404, "http", true, (int)PlaylistSyncStatusKind.Http404)]
    [DataRow(403, "http", true, (int)PlaylistSyncStatusKind.Http403)]
    [DataRow(400, "http", true, (int)PlaylistSyncStatusKind.HttpError)]
    [DataRow(200, "header", true, (int)PlaylistSyncStatusKind.HeaderParseError)]
    [DataRow(200, "data", true, (int)PlaylistSyncStatusKind.DataParseError)]
    [DataRow(200, "entry", true, (int)PlaylistSyncStatusKind.DataParseError)]
    [DataRow(200, "url", true, (int)PlaylistSyncStatusKind.InvalidDataUrl)]
    [DataRow(200, "missing-file", true, (int)PlaylistSyncStatusKind.UnknownError)]
    [DataRow(200, "header-not-found", true, (int)PlaylistSyncStatusKind.HeaderNotFound)]
    [DataRow(404, "http", false, (int)PlaylistSyncStatusKind.Http404)]
    [TestCategory("Playlist")]
    public async Task ExternalSync_SourceFailurePreservesStatusAndCommittedTables(
        int statusCode, string malformedPart, bool includeSuccessfulTarget, int expectedKind)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistExternalFailureBoundaryTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SingleRequestHttpServer? server = null;
        PlaylistWorkspaceViewModel? workspace = null;
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
                case "header": File.WriteAllText(badHeader, "{ invalid json }"); break;
                case "data": File.WriteAllText(badData, "{ invalid json }"); break;
                case "missing-file": File.Delete(badData); break;
                case "header-not-found": File.WriteAllText(badHeader, "<html><body>No table</body></html>"); break;
                case "entry": File.WriteAllText(badData, "[1]"); break;
                case "url": File.WriteAllText(badHeader, "{\"name\":\"Bad\",\"symbol\":\"B\"}"); break;
            }
            workspace = CreatePlaylistWorkspace(playlist, library);
            int completedCount = 0;
            bool reportedSuccess = true;
            workspace.PlaylistExternalSyncCompleted += (_, completion) =>
            {
                completedCount++;
                reportedSuccess = completion.Succeeded;
                Assert.IsFalse(completion.WasSkipped);
            };

            // 全体再読込みが使う同期入口を直接待つ。失敗をAssert.Throwsで成功条件にしません。
            await workspace.RunExternalPlaylistSyncAsync("source-failure", true, true, 1);
            Assert.AreEqual(1, completedCount);
            Assert.IsFalse(reportedSuccess);
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
                if (includeSuccessfulTarget)
                {
                    Assert.AreEqual(2, db.Table<LR2SongDBExtended.playlist_entry>().Count(entry => entry.playlist_id == 7901));
                    Assert.AreEqual(2, playlist.BMSTables.Single(table => table.playlist_id == 7901).entries.Count);
                    Assert.IsFalse(workspace.CapturePlaylistSyncStatusSnapshot()["id:7901"].HasFailureStatus);
                }
            }

            // 同じ対象の入力を修正して明示的に再取得する。過去の失敗を次操作へ持ち越しません。
            bad.Page_url = new Uri(badHeader);
            bad.Header_url = new Uri(badHeader);
            File.WriteAllText(badHeader, "{\"name\":\"Bad\",\"symbol\":\"B\",\"data_url\":\"bad-data.json\"}");
            File.WriteAllText(badData, oldData);
            playlist.CommitBMSTableHeaderToDB(bad);
            await workspace.ResyncPlaylistsAsync([bad]);
            Assert.IsFalse(workspace.CapturePlaylistSyncStatusSnapshot()["id:7902"].HasFailureStatus);
        }
        finally
        {
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

    /// <summary>HTTPの型付き応答コードと通常の入力不良を分類し、包装された内部故障・OOM・混合集約を通常取得失敗にしません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void SourceFailureClassification_DoesNotConsumeInternalFailures()
    {
        Exception[] expected =
        [
            new HttpRequestException("not found", null, HttpStatusCode.NotFound),
            new HttpRequestException("network unavailable"),
            new OperationCanceledException(),
            new FileNotFoundException("source missing"),
            new UnauthorizedAccessException("source denied"),
            new PlaylistHeaderParseException("header", new JsonReaderException("invalid json")),
            new PlaylistDataParseException("data", new FormatException("invalid entry"))
        ];
        foreach (Exception failure in expected)
        {
            Assert.IsTrue(PlaylistSyncAttemptResult.IsExpectedExternalLoadFailure(failure));
        }
        Exception[] unexpected =
        [
            new InvalidOperationException("publication failed"),
            new PlaylistDataParseException("data", new NullReferenceException()),
            new PlaylistDataParseException("data", new OutOfMemoryException()),
            new PlaylistDataParseException("data", new ArgumentNullException("internalState")),
            new AggregateException(expected[0], new InvalidOperationException("save failed"))
        ];
        foreach (Exception failure in unexpected)
        {
            Assert.IsFalse(PlaylistSyncAttemptResult.IsExpectedExternalLoadFailure(failure));
        }
        var uri = new Uri("https://example.invalid/table.json");
        var status = PlaylistSyncAttemptResult.CreateReportedExternalFailure(null, uri, expected[0]);
        Assert.AreEqual(PlaylistSyncStatusKind.Http404, status.FailureKind);
        Assert.IsFalse(status.Succeeded);
        Assert.IsNull(status.Exception);
        Assert.AreEqual(string.Empty, status.Detail);
        Assert.AreEqual(PlaylistSyncStatusKind.NetworkError,
            PlaylistSyncAttemptResult.CreateReportedExternalFailure(null, uri, expected[1]).FailureKind);
    }
}
