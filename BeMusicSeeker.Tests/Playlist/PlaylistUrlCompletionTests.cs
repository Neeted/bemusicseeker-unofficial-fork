using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

/// <summary>URL候補の解析・優先順位と、実playlistへの適用・編集保存を分担して確認します。</summary>
/// <remarks>通信は固定応答にし、保存差・hash互換は実DBの新readerで観測します。画面の実接続はMainWindow側に分担します。</remarks>
[TestClass]
public sealed class PlaylistUrlCompletionTests
{
    private string isolatedSettingsDirectory = string.Empty;
    private BeMusicSeeker.Properties.Settings testSettings = null!;

    [TestInitialize]
    public void InitializeIsolatedSettings()
    {
        isolatedSettingsDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistUrlCompletionTests),
            "settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedSettingsDirectory);
        testSettings = PortableSettingsPersistenceTests.OpenSettings(
            Path.Combine(isolatedSettingsDirectory, "user.config"));
    }

    [TestCleanup]
    public void CleanupIsolatedSettings()
    {
        if (Directory.Exists(isolatedSettingsDirectory))
        {
            Directory.Delete(isolatedSettingsDirectory, recursive: true);
        }
    }

    /// <summary>TSVの見出し・不正MD5・URL欠損を除外し、重複MD5は最初の主URLと差分URLを残して件数も区別します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ParseMd5UrlMappingTsv_SkipsHeaderInvalidRowsAndKeepsFirstDuplicate()
    {
        string content = string.Join("\r\n",
        [
            "md5\turl_diff\turl",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\thttps://example.com/diff-a\thttps://example.com/main-a",
            "not-md5\thttps://example.com/diff-invalid\thttps://example.com/main-invalid",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\thttps://example.com/diff-duplicate\thttps://example.com/main-duplicate",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\t\t",
            "cccccccccccccccccccccccccccccccc\t\thttps://example.com/main-c"
        ]);

        PlaylistUrlCompletionSourceSnapshot snapshot = PlaylistUrlCompletionSupport.ParseMd5UrlMappingTsv(content);

        Assert.AreEqual(2, snapshot.CandidateCount);
        Assert.AreEqual(1, snapshot.DuplicateCount);
        Assert.AreEqual(2, snapshot.IgnoredRowCount);
        Assert.AreEqual(new Uri("https://example.com/main-a"), snapshot.Candidates["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].Url);
        Assert.AreEqual(new Uri("https://example.com/diff-a"), snapshot.Candidates["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].UrlDiff);
        Assert.AreEqual(new Uri("https://example.com/main-c"), snapshot.Candidates["cccccccccccccccccccccccccccccccc"].Url);
        Assert.IsNull(snapshot.Candidates["cccccccccccccccccccccccccccccccc"].UrlDiff);
    }

    /// <summary>Stellaの主URL・差分URLを直接読み、重複は先勝ち、不正hash・URLは除外し、submissionだけから差分URLを作りません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ParseStellaUploadFullJson_UsesUrlAndUrlDiffWithoutSubmissionBuild()
    {
        string content = "[" +
            "{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"url\":\"https://example.com/main-a\",\"url_diff\":\"https://example.com/diff-a\"}," +
            "{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"url\":\"https://example.com/main-duplicate\",\"url_diff\":\"https://example.com/diff-duplicate\"}," +
            "{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"url\":\"https://example.com/main-b\",\"submission\":\"222\"}," +
            "{\"md5\":\"cccccccccccccccccccccccccccccccc\",\"url\":\"not-url\",\"url_diff\":\"\"}," +
            "{\"md5\":\"invalid\",\"url\":\"https://example.com/main-invalid\",\"url_diff\":\"https://example.com/diff-invalid\"}" +
            "]";

        PlaylistUrlCompletionSourceSnapshot snapshot = PlaylistUrlCompletionSupport.ParseStellaUploadFullJson(content);

        Assert.AreEqual(2, snapshot.CandidateCount);
        Assert.AreEqual(1, snapshot.DuplicateCount);
        Assert.AreEqual(2, snapshot.IgnoredRowCount);
        Assert.AreEqual(new Uri("https://example.com/main-a"), snapshot.Candidates["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].Url);
        Assert.AreEqual(new Uri("https://example.com/diff-a"), snapshot.Candidates["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"].UrlDiff);
        Assert.AreEqual(new Uri("https://example.com/main-b"), snapshot.Candidates["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"].Url);
        Assert.IsNull(snapshot.Candidates["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"].UrlDiff);
    }

    /// <summary>大文字MD5キーとnull行を除外して小文字キーの有効行だけを受理し、誤った形の行を候補へ混入させません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ParseStellaUploadFullJson_PreservesCaseSensitiveAndWrongShapeFiltering()
    {
        string content = "["
            + "{\"MD5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"url\":\"https://example.com/ignored\"},"
            + "null,"
            + "{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"url\":\"https://example.com/accepted\"}"
            + "]";

        PlaylistUrlCompletionSourceSnapshot snapshot = PlaylistUrlCompletionSupport.ParseStellaUploadFullJson(content);

        Assert.AreEqual(1, snapshot.CandidateCount);
        Assert.AreEqual(2, snapshot.IgnoredRowCount);
        Assert.AreEqual(new Uri("https://example.com/accepted"), snapshot.Candidates["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"].Url);
    }

    /// <summary>配列の後ろに別JSON文書が続く入力を読取例外とし、先頭配列だけの成功へ隠しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void ParseStellaUploadFullJson_RejectsTrailingDocument()
    {
        Assert.ThrowsException<JsonReaderException>(
            () => PlaylistUrlCompletionSupport.ParseStellaUploadFullJson("[]{}"));
    }

    /// <summary>外部JSONのURLをUriへ変換し、名前・symbol・小文字tagを読み、大文字tagで上書きしません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableSimple_ParsesTypedCaseSensitiveFields()
    {
        var table = new BMSTableSimple(JObject.Parse("{\"symbol\":\"st\",\"name\":\"Table\",\"url\":\"https://example.com/table\",\"tag1\":\"First\",\"tag2\":\"Second\",\"TAG1\":\"ignored\"}"));

        Assert.AreEqual("st", table.symbol);
        Assert.AreEqual("Table", table.name);
        Assert.AreEqual(new Uri("https://example.com/table"), table.url);
        Assert.AreEqual("First", table.tag1);
        Assert.AreEqual("Second", table.tag2);
    }

    /// <summary>外部JSONの壊れた絶対URLをUriFormatExceptionとして公開し、不正URLを空の成功へ置換しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableSimple_InvalidUrlPreservesFailureContract()
    {
        Assert.ThrowsException<UriFormatException>(
            () => new BMSTableSimple(JObject.Parse("{\"name\":\"Table\",\"url\":\"http://[broken\"}")));
    }

    /// <summary>ローカル絶対パスをfileの絶対URIへ解決し、HTTP形式だけを受理する誤りを検出します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void TryResolveSourceUri_LocalFilePath_ResolvesFileUri()
    {
        string tempFilePath = Path.Combine(Path.GetTempPath(), "playlist-url-source.tsv");
        bool resolved = PlaylistUrlCompletionSupport.TryResolveSourceUri(tempFilePath, out Uri uri);

        Assert.IsTrue(resolved);
        Assert.IsNotNull(uri);
        Assert.IsTrue(uri.IsAbsoluteUri);
        Assert.AreEqual(Uri.UriSchemeFile, uri.Scheme);
    }

    /// <summary>上書き有効時は保存済みURLを保持したまま実効URLを補完候補へ切り替え、保存値そのものを破壊しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_EffectiveUrlPrefersRuntimeCompletionWhenOverwriteEnabled()
    {
        BMSTableEntry entry = CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "SongA");
        entry.Url = new Uri("https://example.com/persisted");

        bool changed = entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), null, overwriteExisting: true);

        Assert.IsTrue(changed);
        Assert.AreEqual(new Uri("https://example.com/persisted"), entry.Url);
        Assert.AreEqual(new Uri("https://example.com/runtime"), entry.RuntimeUrlCompletion);
        Assert.AreEqual(new Uri("https://example.com/runtime"), entry.EffectiveUrl);
    }

    /// <summary>利用者が主URLと差分URLを編集すると各補完値を消し、手編集より古い補完を表示し続けません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ExplicitUrlEditClearsRuntimeCompletion()
    {
        BMSTableEntry entry = CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "SongB");
        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

        entry.Url = new Uri("https://example.com/manual");
        entry.Url_diff = new Uri("https://example.com/manual-diff");

        Assert.IsNull(entry.RuntimeUrlCompletion);
        Assert.IsNull(entry.RuntimeUrlDiffCompletion);
        Assert.AreEqual(new Uri("https://example.com/manual"), entry.EffectiveUrl);
        Assert.AreEqual(new Uri("https://example.com/manual-diff"), entry.EffectiveUrlDiff);
    }

    /// <summary>上書き無効で通常の保存済みURLがある場合は補完候補を採らず、利用者のURLを優先します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_OverwriteDisabledKeepsPersistedUrl()
    {
        BMSTableEntry entry = CreateEntry("cccccccccccccccccccccccccccccccc", "SongC");
        entry.Url = new Uri("https://example.com/persisted");

        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), null, overwriteExisting: false);

        Assert.IsNull(entry.RuntimeUrlCompletion);
        Assert.AreEqual(new Uri("https://example.com/persisted"), entry.EffectiveUrl);
    }

    /// <summary>既知の失効サイトURLはarchive経由と省略schemeも補完し、上書き無効でも表示を更新しつつ元保存値を残します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_OverwriteDisabledCompletesKnownDeadPlaylistUrls()
    {
        BMSTableEntry entry = CreateEntry("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", "DeadUrlSong");
        entry.Url = new Uri("https://web.archive.org/web/20200101000000/http://absolute.pv.land.to/archive.zip");
        entry.Url_diff = new Uri("ttp://gnqg.rosx.net/diff.zip");

        bool changed = entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

        Assert.IsTrue(changed);
        Assert.AreEqual(new Uri("https://web.archive.org/web/20200101000000/http://absolute.pv.land.to/archive.zip"), entry.Url);
        Assert.AreEqual(new Uri("ttp://gnqg.rosx.net/diff.zip"), entry.Url_diff);
        Assert.AreEqual(new Uri("https://example.com/runtime"), entry.RuntimeUrlCompletion);
        Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.RuntimeUrlDiffCompletion);
        Assert.AreEqual(new Uri("https://example.com/runtime"), entry.EffectiveUrl);
        Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.EffectiveUrlDiff);
    }

    /// <summary>失効サイトでも候補がなければ元の主URL・差分URLを実効値として保持し、候補なしを理由に消しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_KnownDeadPlaylistUrlRemainsWhenCompletionCandidateIsMissing()
    {
        BMSTableEntry entry = CreateEntry("cececececececececececececececece", "DeadUrlNoCandidateSong");
        entry.Url = new Uri("http://absolute.pv.land.to/archive.zip");
        entry.Url_diff = new Uri("https://web.archive.org/web/20200101000000/http://gnqg.rosx.net/diff.zip");

        bool changed = entry.ApplyRuntimeUrlCompletion(null, null, overwriteExisting: false);

        Assert.IsFalse(changed);
        Assert.IsNull(entry.RuntimeUrlCompletion);
        Assert.IsNull(entry.RuntimeUrlDiffCompletion);
        Assert.AreEqual(new Uri("http://absolute.pv.land.to/archive.zip"), entry.EffectiveUrl);
        Assert.AreEqual(new Uri("https://web.archive.org/web/20200101000000/http://gnqg.rosx.net/diff.zip"), entry.EffectiveUrlDiff);
    }

    /// <summary>前回の主URL・差分URL補完がある行から候補が消えると両方を解除し、古い実効URLを残しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ApplyRuntimeUrlCompletion_ClearsWhenCandidateDisappears()
    {
        BMSTableEntry entry = CreateEntry("dddddddddddddddddddddddddddddddd", "SongD");
        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

        bool changed = entry.ApplyRuntimeUrlCompletion(null, null, overwriteExisting: false);

        Assert.IsTrue(changed);
        Assert.IsNull(entry.RuntimeUrlCompletion);
        Assert.IsNull(entry.RuntimeUrlDiffCompletion);
        Assert.IsNull(entry.EffectiveUrl);
        Assert.IsNull(entry.EffectiveUrlDiff);
    }

    /// <summary>同じ設定で再要求してもTSVとStellaを各一回取得し、競合はTSV優先、TSVのURI変更時だけ再取得して適用します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task BMSPlaylist_UrlCompletionFetchesSourcesOnceAndRefetchesTsvWhenUriChanges()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            int tsvFetchCount = 0;
            int stellaFetchCount = 0;
            testSettings.EnablePlaylistUrlCompletion = true;
            testSettings.OverwritePlaylistUrlsWithCompletion = false;
            testSettings.EnableStellaFullPlaylistUrlCompletion = true;
            testSettings.PlaylistMd5UrlMappingTsvUri = "https://example.com/map-a.tsv";
            Func<Uri, CancellationToken, Task<string>> tsvFetcher = (uri, cancellationToken) =>
            {
                tsvFetchCount++;
                string mainUrl = uri.AbsoluteUri.IndexOf("map-b.tsv", StringComparison.OrdinalIgnoreCase) >= 0 ? "https://example.com/main-tsv-b" : "https://example.com/main-tsv-a";
                return Task.FromResult("md5\turl_diff\turl\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\thttps://example.com/diff-tsv\t" + mainUrl);
            };
            Func<Uri, CancellationToken, Task<string>> stellaFetcher = (uri, cancellationToken) =>
            {
                stellaFetchCount++;
                return Task.FromResult("[" +
                    "{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"url\":\"https://example.com/main-stella-a\",\"url_diff\":\"https://example.com/diff-stella-a\"}," +
                    "{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"url\":\"https://example.com/main-stella-b\",\"url_diff\":\"https://example.com/diff-stella-b\"}" +
                    "]");
            };
            var playlist = new TestBmsPlaylist(
                tempDbPath,
                null,
                null,
                null,
                () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings),
                () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings),
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings),
                null,
                tsvFetcher,
                stellaFetcher);
            BMSTable table = CreateTable(5001, "CompletionTable");
            BMSTableEntry tsvEntry = CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "TsvSong");
            BMSTableEntry stellaEntry = CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "StellaSong");
            table.entries = [tsvEntry, stellaEntry];
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });

            await ScheduleUrlCompletionRefreshAsync(playlist, "first");
            await ScheduleUrlCompletionRefreshAsync(playlist, "reload");

            Assert.AreEqual(1, tsvFetchCount);
            Assert.AreEqual(1, stellaFetchCount);
            Assert.AreEqual(new Uri("https://example.com/main-tsv-a"), tsvEntry.RuntimeUrlCompletion);
            Assert.AreEqual(new Uri("https://example.com/diff-tsv"), tsvEntry.RuntimeUrlDiffCompletion);
            Assert.AreEqual(new Uri("https://example.com/main-stella-b"), stellaEntry.RuntimeUrlCompletion);
            Assert.AreEqual(new Uri("https://example.com/diff-stella-b"), stellaEntry.RuntimeUrlDiffCompletion);

            testSettings.PlaylistMd5UrlMappingTsvUri = "https://example.com/map-b.tsv";
            await ScheduleUrlCompletionRefreshAsync(playlist, "settings_changed");

            Assert.AreEqual(2, tsvFetchCount);
            Assert.AreEqual(1, stellaFetchCount);
            Assert.AreEqual(new Uri("https://example.com/main-tsv-b"), tsvEntry.RuntimeUrlCompletion);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>通常の取得失敗は旧補完を保持し、内部故障は失敗した作業として返します。次の明示要求では再取得できます。</summary>
    [DataTestMethod]
    [DataRow("http")]
    [DataRow("io")]
    [DataRow("timeout")]
    [DataRow("internal")]
    [DataRow("resource")]
    public async Task BMSPlaylist_UrlCompletionDistinguishesUnavailableSourceFromInternalFailure(string kind)
    {
        string databasePath = CreateEmptySongDbPath();
        TestBmsPlaylist? playlist = null;
        try
        {
            Exception failure = kind switch
            {
                "http" => new HttpRequestException("source unavailable"),
                "io" => new IOException("source unreadable"),
                "timeout" => new TaskCanceledException("source timeout"),
                "resource" => new OutOfMemoryException("resource failure"),
                _ => new InvalidOperationException("internal failure")
            };
            bool shouldFail = false;
            int fetchCount = 0;
            PlaylistUrlCompletionOptionsSnapshot options = new()
            {
                EnablePlaylistUrlCompletion = true,
                PlaylistMd5UrlMappingTsvUri = "https://example.invalid/first.tsv",
                OverwritePlaylistUrlsWithCompletion = true
            };
            playlist = new TestBmsPlaylist(databasePath, null, null, null, () => options,
                () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot(), null,
                (uri, _) =>
                {
                    fetchCount++;
                    if (shouldFail) { throw failure; }
                    return Task.FromResult("md5\turl_diff\turl\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\t\t" + uri.AbsoluteUri);
                }, null);
            BMSTable table = CreateTable(5004, "SourceFailureTable");
            BMSTableEntry entry = CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "SourceFailureSong");
            table.entries = [entry];
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            await ScheduleUrlCompletionRefreshAsync(playlist, "initial");
            Uri? previous = entry.RuntimeUrlCompletion;
            Assert.AreEqual(new Uri("https://example.invalid/first.tsv"), previous);

            options = new PlaylistUrlCompletionOptionsSnapshot
            {
                EnablePlaylistUrlCompletion = true,
                PlaylistMd5UrlMappingTsvUri = "https://example.invalid/next.tsv",
                OverwritePlaylistUrlsWithCompletion = true
            };
            shouldFail = true;
            Exception? observed = null;
            try { await ScheduleUrlCompletionRefreshAsync(playlist, "source_changed"); }
            catch (Exception exception) { observed = exception; }
            if (kind is "internal" or "resource") { Assert.AreSame(failure, observed); }
            else { Assert.IsNull(observed); }
            Assert.AreEqual(previous, entry.RuntimeUrlCompletion);
            Assert.AreEqual(2, fetchCount);

            shouldFail = false;
            await ScheduleUrlCompletionRefreshAsync(playlist, "next_explicit_request");
            Assert.AreEqual(3, fetchCount);
            Assert.AreEqual(new Uri("https://example.invalid/next.tsv"), entry.RuntimeUrlCompletion);
        }
        finally
        {
            playlist?.RequestShutdown("source-failure-fixture");
            DeleteTempSongDbDirectory(databasePath);
        }
    }

    /// <summary>Stella無効時は取得せず、有効化で適用後に再無効化すると補完を消し、不要な再取得もしません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task BMSPlaylist_StellaFullSettingDisablesFetchAndClearsStellaRuntimeCompletion()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            int stellaFetchCount = 0;
            testSettings.EnablePlaylistUrlCompletion = true;
            testSettings.OverwritePlaylistUrlsWithCompletion = false;
            testSettings.EnableStellaFullPlaylistUrlCompletion = false;
            testSettings.PlaylistMd5UrlMappingTsvUri = "https://example.com/empty.tsv";
            Func<Uri, CancellationToken, Task<string>> tsvFetcher = (uri, cancellationToken) => Task.FromResult("md5\turl_diff\turl");
            Func<Uri, CancellationToken, Task<string>> stellaFetcher = (uri, cancellationToken) =>
            {
                stellaFetchCount++;
                return Task.FromResult("[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"url\":\"https://example.com/main-stella\",\"url_diff\":\"https://example.com/diff-stella\"}]");
            };
            var playlist = new TestBmsPlaylist(
                tempDbPath,
                null,
                null,
                null,
                () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings),
                () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings),
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings),
                null,
                tsvFetcher,
                stellaFetcher);
            BMSTable table = CreateTable(5002, "StellaToggleTable");
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            BMSTableEntry stellaEntry = CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "StellaSong");
            table.entries = [stellaEntry];
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });
            playlist.CommitBMSTableWithEntriesToDB(table);
            int initialEntryVersion = table.PlaylistEntriesRevision;
            DateTime initialLastUpdate = table.last_update;

            await ScheduleUrlCompletionRefreshAsync(playlist, "disabled");

            Assert.AreEqual(0, stellaFetchCount);
            Assert.IsNull(stellaEntry.RuntimeUrlCompletion);
            Assert.IsNull(stellaEntry.RuntimeUrlDiffCompletion);

            testSettings.EnableStellaFullPlaylistUrlCompletion = true;
            await ScheduleUrlCompletionRefreshAsync(playlist, "enabled");
            PlaylistEntryCommitReceipt completed = playlist.DrainPlaylistEntryCommits().Single();
            Assert.AreEqual(initialEntryVersion, completed.BaseRevision);
            Assert.AreEqual(initialEntryVersion + 1, completed.Revision);
            Assert.AreSame(stellaEntry, completed.Changes.Single().Entry);
            Assert.AreEqual(new Uri("https://example.com/main-stella"), completed.Changes.Single().After.EffectiveUrl);
            Assert.AreEqual(initialLastUpdate, table.last_update);

            Assert.AreEqual(1, stellaFetchCount);
            Assert.AreEqual(new Uri("https://example.com/main-stella"), stellaEntry.RuntimeUrlCompletion);
            Assert.AreEqual(new Uri("https://example.com/diff-stella"), stellaEntry.RuntimeUrlDiffCompletion);

            testSettings.EnableStellaFullPlaylistUrlCompletion = false;
            await ScheduleUrlCompletionRefreshAsync(playlist, "disabled_again");
            PlaylistEntryCommitReceipt cleared = playlist.DrainPlaylistEntryCommits().Single();
            Assert.AreEqual(completed.Revision, cleared.BaseRevision);
            Assert.AreEqual(completed.Revision + 1, cleared.Revision);
            Assert.IsNull(cleared.Changes.Single().After.EffectiveUrl);
            Assert.AreEqual(new Uri("https://example.com/main-stella"), completed.Changes.Single().After.EffectiveUrl,
                "過去の補完結果は解除後も不変です。");
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(string.Empty, db.ExecuteScalar<string>("SELECT url FROM playlist_entry WHERE playlist_id = 5002;"));
                Assert.AreEqual(initialLastUpdate, db.Table<BMSTable>().Single(row => row.playlist_id == 5002).last_update);
            }

            Assert.AreEqual(1, stellaFetchCount);
            Assert.IsNull(stellaEntry.RuntimeUrlCompletion);
            Assert.IsNull(stellaEntry.RuntimeUrlDiffCompletion);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>独立した設定入力で実TSVの適用を確認し、通信中の退役ではモデルtokenを取り消して旧行へ適用しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("Playlist")]
    public async Task BMSPlaylist_UrlCompletionUsesInjectedOptionsAndRetiredResultDoesNotApply(bool retire)
    {
        string tempDbPath = CreateEmptySongDbPath();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task refresh = Task.CompletedTask;
        CancellationToken capturedToken = default;
        try
        {
            PlaylistUrlCompletionOptionsSnapshot options = new()
            {
                EnablePlaylistUrlCompletion = false,
                PlaylistMd5UrlMappingTsvUri = "https://example.com/injected.tsv",
                OverwritePlaylistUrlsWithCompletion = true
            };
            int fetchCount = 0;
            Func<Uri, CancellationToken, Task<string>> tsvFetcher = async (uri, cancellationToken) =>
            {
                fetchCount++;
                capturedToken = cancellationToken;
                entered.TrySetResult();
                await release.Task;
                return "md5\turl_diff\turl\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\t\thttps://example.com/injected";
            };
            var playlist = new TestBmsPlaylist(
                tempDbPath,
                null,
                null,
                null,
                () => options,
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                null,
                tsvFetcher,
                null);
            BMSTable table = CreateTable(5003, "InjectedOptionsTable");
            BMSTableEntry entry = CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "InjectedOptionsSong");
            table.entries = [entry];
            playlist.BMSTables = new ObservableCollection<BMSTable>(new[] { table });

            await ScheduleUrlCompletionRefreshAsync(playlist, "disabled");

            Assert.AreEqual(0, fetchCount);
            Assert.IsNull(entry.RuntimeUrlCompletion);

            options = new PlaylistUrlCompletionOptionsSnapshot
            {
                EnablePlaylistUrlCompletion = true,
                PlaylistMd5UrlMappingTsvUri = "https://example.com/injected.tsv",
                OverwritePlaylistUrlsWithCompletion = true
            };
            refresh = ScheduleUrlCompletionRefreshAsync(playlist, "enabled");
            await Task.WhenAny(entered.Task, refresh);
            if (!entered.Task.IsCompleted) { await refresh; Assert.Fail("実TSV通信へ到達しませんでした。"); }
            Assert.IsFalse(refresh.IsCompleted);
            if (retire) { playlist.RequestStop("test_replaced_pair"); Assert.IsTrue(capturedToken.IsCancellationRequested); }
            release.TrySetResult();
            if (retire)
            {
                OperationCanceledException? canceled = null;
                try { await refresh; }
                catch (OperationCanceledException failure) { canceled = failure; }
                Assert.IsNotNull(canceled);
                Assert.AreEqual(capturedToken, canceled.CancellationToken);
                Assert.IsTrue(refresh.IsCanceled);
            }
            else { await refresh; }

            Assert.AreEqual(1, fetchCount);
            if (retire) { Assert.IsNull(entry.RuntimeUrlCompletion); }
            else { Assert.AreEqual(new Uri("https://example.com/injected"), entry.RuntimeUrlCompletion); }
        }
        finally
        {
            release.TrySetResult();
            if (retire)
            {
                OperationCanceledException? canceled = null;
                try { await refresh; }
                catch (OperationCanceledException failure) { canceled = failure; }
                Assert.IsNotNull(canceled);
                Assert.AreEqual(capturedToken, canceled.CancellationToken);
                Assert.IsTrue(refresh.IsCanceled);
            }
            else { await refresh; }
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>保存URLが未設定でも表示sourceは主URL・差分URLの補完を映し、保存値だけを参照して補完表示を落としません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void PlaylistDetailSourceRow_UsesEffectiveUrlForDisplay()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Path = "SongE" + ".bms", Title = "SongE", RawTitle = "SongE", Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        var entry = new BMSTableEntry(file);
        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

        var sourceRow = new PlaylistDetailSourceRow(entry, (file));

        Assert.AreEqual(new Uri("https://example.com/runtime"), sourceRow.Url);
        Assert.AreEqual(new Uri("https://example.com/runtime-diff"), sourceRow.Url_diff);
    }

    /// <summary>ローカル表のmemo編集保存で主URL・差分URLの補完も確定し、新DB読取と表の更新時刻へ反映します。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task DetailEdit_LocalPlaylistMaterializesEffectiveUrlsIntoDatabase()
    {
        string tempDbPath = CreateEmptySongDbPath();
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            BMSTable table = CreateTable(4001, "LocalTable");
            DateTime oldLastUpdate = DateTime.Now.AddDays(1);
            table.last_update = oldLastUpdate;
            BMSTableEntry entry = CreateEntry("ffffffffffffffffffffffffffffffff", "LocalSong");
            table.entries = [entry];
            workspace = CreateSavedDetailWorkspace(tempDbPath, table, out playlist, out library);
            entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

            await SaveDetailMemoAsync(workspace, entry, "saved memo");

            using LR2SongDBExtended db = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            BMSTableEntry storedEntry = db.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.md5 == entry.md5);
            Assert.AreEqual("saved memo", storedEntry.memo);
            Assert.AreEqual(new Uri("https://example.com/runtime"), entry.Url);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.Url_diff);
            Assert.AreEqual(new Uri("https://example.com/runtime"), entry.EffectiveUrl);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.EffectiveUrlDiff);
            Assert.AreEqual(new Uri("https://example.com/runtime"), storedEntry.Url);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), storedEntry.Url_diff);
            BMSTable storedTable = db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id);
            Assert.IsTrue(table.last_update > oldLastUpdate);
            Assert.AreEqual(table.last_update, storedTable.last_update);
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
            }
            playlist?.RequestShutdown("url-completion-edit-fixture");
            library?.RequestShutdown("url-completion-edit-fixture");
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>URL1/2の空文字・空白だけは当該補完ごと解除し、有効入力は置換します。他方の補完材料化・保存値保持をDBと実効表示で確認します。</summary>
    /// <remarks>実編集Taskと別属性保存を終端まで待ち、旧補完の復活と両URL一括消去を検出します。実セルの空欄配送とEscapeは既存WPF系列が分担します。</remarks>
    [DataTestMethod]
    [DataRow(nameof(PlaylistDetailRow.Url), "", false)]
    [DataRow(nameof(PlaylistDetailRow.Url), "", true)]
    [DataRow(nameof(PlaylistDetailRow.Url), " \t ", false)]
    [DataRow(nameof(PlaylistDetailRow.Url), " \t ", true)]
    [DataRow(nameof(PlaylistDetailRow.Url_diff), "", false)]
    [DataRow(nameof(PlaylistDetailRow.Url_diff), "", true)]
    [DataRow(nameof(PlaylistDetailRow.Url_diff), " \t ", false)]
    [DataRow(nameof(PlaylistDetailRow.Url_diff), " \t ", true)]
    [DataRow(nameof(PlaylistDetailRow.Url), "https://example.com/manual", false)]
    [DataRow(nameof(PlaylistDetailRow.Url), "https://example.com/manual", true)]
    [DataRow(nameof(PlaylistDetailRow.Url_diff), "https://example.com/manual", false)]
    [DataRow(nameof(PlaylistDetailRow.Url_diff), "https://example.com/manual", true)]
    [TestCategory("Playlist")]
    public async Task DetailEdit_LocalUrlClearOrReplacePreservesOtherUrl(string propertyName, string text, bool completeOther)
    {
        string path = CreateEmptySongDbPath();
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(path);
            BMSTable table = CreateTable(4103, "URL edit");
            BMSTableEntry entry = CreateEntry(new string('e', 32), "URL edit song");
            entry.Url = new Uri("https://example.com/saved-main");
            entry.Url_diff = new Uri("https://example.com/saved-diff");
            table.entries = [entry];
            workspace = CreateSavedDetailWorkspace(path, table, out playlist, out library);
            bool editMain = propertyName == nameof(PlaylistDetailRow.Url);
            entry.ApplyRuntimeUrlCompletion(
                editMain || completeOther ? new Uri("https://example.com/runtime-main") : null,
                !editMain || completeOther ? new Uri("https://example.com/runtime-diff") : null,
                overwriteExisting: true);
            var source = new PlaylistDetailSourceRow(entry, resolvedChart: null);
            PlaylistDetailRow row = source.CreateViewRow();
            workspace.DetailViewState.Source.Rows = [source];
            var context = new MainChartListCellEditContext(row, propertyName,
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            Assert.IsTrue(workspace.CanBeginDetailEdit(context));
            workspace.BeginDetailEdit(context);
            await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, text, commit: true));
            Assert.IsFalse(workspace.DetailViewState.Source.IsPlaylistCellEditing);

            Uri? editedUrl = string.IsNullOrWhiteSpace(text) ? null : new Uri("https://example.com/manual");
            Uri otherUrl = new(editMain
                ? completeOther ? "https://example.com/runtime-diff" : "https://example.com/saved-diff"
                : completeOther ? "https://example.com/runtime-main" : "https://example.com/saved-main");
            Uri? expectedMain = editMain ? editedUrl : otherUrl;
            Uri? expectedDiff = editMain ? otherUrl : editedUrl;
            void AssertUrls()
            {
                Assert.AreEqual(expectedMain, entry.Url);
                Assert.AreEqual(expectedDiff, entry.Url_diff);
                Assert.AreEqual(expectedMain, entry.EffectiveUrl);
                Assert.AreEqual(expectedDiff, entry.EffectiveUrlDiff);
                Assert.IsNull(entry.RuntimeUrlCompletion);
                Assert.IsNull(entry.RuntimeUrlDiffCompletion);
                Assert.AreEqual(expectedMain, row.Url);
                Assert.AreEqual(expectedDiff, row.Url_diff);
                PlaylistDetailRow current = new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow();
                Assert.AreEqual(expectedMain, current.Url);
                Assert.AreEqual(expectedDiff, current.Url_diff);
                using LR2SongDBExtended db = new BmsLibraryDbGateway(path).OpenSongDbReadOnly();
                BMSTableEntry saved = db.Table<BMSTableEntry>().Single();
                Assert.AreEqual(expectedMain, saved.Url);
                Assert.AreEqual(expectedDiff, saved.Url_diff);
            }
            AssertUrls();
            await SaveDetailMemoAsync(workspace, entry, "after URL edit");
            AssertUrls();
        }
        finally
        {
            if (workspace != null) { workspace.CancelDetailBuilds(); await workspace.WaitForDetailBuildIdleAsync(); workspace.Dispose(); }
            playlist?.RequestShutdown("url-edit-fixture");
            library?.RequestShutdown("url-edit-fixture");
            DeleteTempSongDbDirectory(path);
        }
    }

    /// <summary>外部同期表のmemoは保存する一方、主URL・差分URL補完は実効表示だけに保持し、外部由来の保存値へ混入させません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task DetailEdit_ExternalSyncPlaylistDoesNotMaterializeEffectiveUrls()
    {
        string tempDbPath = CreateEmptySongDbPath();
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            BMSTable table = CreateTable(4002, "ExternalTable");
            table.Page_url = new Uri("https://example.com/page.html");
            table.Header_url = new Uri("https://example.com/header.json");
            table.Data_url = new Uri("https://example.com/data.json");
            table.EnableExternalSync();
            BMSTableEntry entry = CreateEntry("12121212121212121212121212121212", "ExternalSong");
            table.entries = [entry];
            workspace = CreateSavedDetailWorkspace(tempDbPath, table, out playlist, out library);
            entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

            await SaveDetailMemoAsync(workspace, entry, "saved memo");

            using LR2SongDBExtended db = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            BMSTableEntry storedEntry = db.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.md5 == entry.md5);
            Assert.AreEqual("saved memo", storedEntry.memo);
            Assert.IsNull(entry.Url);
            Assert.IsNull(entry.Url_diff);
            Assert.AreEqual(new Uri("https://example.com/runtime"), entry.EffectiveUrl);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.EffectiveUrlDiff);
            Assert.IsNull(storedEntry.Url);
            Assert.IsNull(storedEntry.Url_diff);
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
            }
            playlist?.RequestShutdown("url-completion-edit-fixture");
            library?.RequestShutdown("url-completion-edit-fixture");
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>MD5なしのSHA256行を二回memo編集しても一行を更新し、互換識別子で重複行を増やしません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task DetailEdit_Sha256Identity_ReplacesExistingRowWithoutDuplicates()
    {
        string tempDbPath = CreateEmptySongDbPath();
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            BMSTable table = CreateTable(4003, "ShaTable");
            TestablePlaylistEntry entry = CreateShaOnlyEntry("3434343434343434343434343434343434343434343434343434343434343434", "ShaSong", "before");
            table.entries = [entry];
            workspace = CreateSavedDetailWorkspace(tempDbPath, table, out playlist, out library);

            await SaveDetailMemoAsync(workspace, entry, "memo-1");
            await SaveDetailMemoAsync(workspace, entry, "memo-2");

            using LR2SongDBExtended db = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            List<BMSTableEntry> rows = [.. db.Table<BMSTableEntry>().Where(row => row.playlist_id == table.playlist_id)];
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("memo-2", rows[0].memo);
            Assert.IsNull(rows[0].md5);
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
            }
            playlist?.RequestShutdown("url-completion-edit-fixture");
            library?.RequestShutdown("url-completion-edit-fixture");
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>SHA256だけで保存された行の編集時にbmsonのMD5を補い、元SHA256を保って旧行を置換し二重登録しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task DetailEdit_AddedMd5ReplacesExistingSha256CompatibilityRow()
    {
        string tempDbPath = CreateEmptySongDbPath();
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            BMSTable table = CreateTable(4005, "ShaToBothTable");
            string sha256 = "4545454545454545454545454545454545454545454545454545454545454545";
            TestablePlaylistEntry entry = CreateShaOnlyEntry(sha256, "BmsonSong", "old");
            table.entries = [entry];
            workspace = CreateSavedDetailWorkspace(tempDbPath, table, out playlist, out library);
            ChartFile chart = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(Path.GetDirectoryName(tempDbPath)!, "song.bmson"),
                Md5 = "abababababababababababababababab",
                Sha256 = sha256
            };

            await SaveDetailMemoAsync(workspace, entry, "new", chart);

            using LR2SongDBExtended db = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            List<BMSTableEntry> rows = [.. db.Table<BMSTableEntry>().Where(row => row.playlist_id == table.playlist_id)];
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(chart.Md5, rows[0].md5);
            Assert.AreEqual(sha256, rows[0].sha256);
            Assert.AreEqual("new", rows[0].memo);
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
            }
            playlist?.RequestShutdown("url-completion-edit-fixture");
            library?.RequestShutdown("url-completion-edit-fixture");
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>MD5なしのSHA256行をJSON経由で再読込し、hashとタイトルが外部形式への変換で失われません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ToJsonObject_RoundTripsSha256()
    {
        TestablePlaylistEntry entry = CreateShaOnlyEntry("5656565656565656565656565656565656565656565656565656565656565656", "ShaRoundTrip", "memo");

        var reloaded = new BMSTableEntry(entry.ToJsonObject());

        Assert.AreEqual(entry.sha256, reloaded.sha256);
        Assert.AreEqual(entry.title, reloaded.title);
    }

    /// <summary>元譜面MD5のnullを空配列・空文字としてJSONへ正規化し、保存互換形式にnullを残しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ToJsonObject_NormalizesNullOrgMd5ToEmpty()
    {
        TestablePlaylistEntry entry = CreateShaOnlyEntry("6767676767676767676767676767676767676767676767676767676767676767", "OrgEmpty", "memo");
        entry.Org_md5 = null;

        JObject json = entry.ToJsonObject();

        CollectionAssert.AreEqual(Array.Empty<string>(), ((JArray)json["org_md5s"]!).Values<string>().ToArray());
        Assert.AreEqual(string.Empty, (string?)json["org_md5"]);
        Assert.AreEqual(string.Empty, entry.org_md5);
    }

    /// <summary>旧保存形式の文字列nullを元譜面なしとしてJSON出力し、文字列をhash候補へ誤解釈しません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ParsesStringNullOrgMd5AsEmpty()
    {
        TestablePlaylistEntry entry = CreateShaOnlyEntry("7878787878787878787878787878787878787878787878787878787878787878", "OrgStringNull", "memo");
        entry.SetOrgMd5Raw("null");

        JObject json = entry.ToJsonObject();

        CollectionAssert.AreEqual(Array.Empty<string>(), ((JArray)json["org_md5s"]!).Values<string>().ToArray());
        Assert.AreEqual(string.Empty, (string?)json["org_md5"]);
    }

    /// <summary>bmsonのmemo編集後もMD5・SHA256・元譜面MD5を新DB読取へ保存し、BMS専用処理で情報を落としません。</summary>
    [TestMethod]
    [TestCategory("Playlist")]
    public async Task DetailEdit_BmsonPlaylistEntry_PersistsBothHashesAndOrgMd5()
    {
        string tempDbPath = CreateEmptySongDbPath();
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            BMSTable table = CreateTable(4004, "BmsonTable");
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(Path.GetTempPath(), "playlist-bmson-test", "song.bmson"),
                Folder = string.Empty,
                RawTitle = "BmsonSong",
                RawArtist = "Artist",
                Level = 12,
                Md5 = "99999999999999999999999999999999",
                Sha256 = "8989898989898989898989898989898989898989898989898989898989898989"
            };
            var entry = new BMSTableEntry((song))
            {
                folder = string.Empty,
                playlist_id = table.playlist_id,
                Org_md5 = ["abababababababababababababababab"]
            };

            table.entries = [entry];
            workspace = CreateSavedDetailWorkspace(tempDbPath, table, out playlist, out library);
            await SaveDetailMemoAsync(workspace, entry, "saved memo");

            using LR2SongDBExtended db = new BmsLibraryDbGateway(tempDbPath).OpenSongDbReadOnly();
            BMSTableEntry storedEntry = db.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.sha256 == entry.sha256);
            Assert.AreEqual("saved memo", storedEntry.memo);
            Assert.AreEqual("99999999999999999999999999999999", storedEntry.md5);
            Assert.AreEqual("8989898989898989898989898989898989898989898989898989898989898989", storedEntry.sha256);
            CollectionAssert.AreEqual(new[] { "abababababababababababababababab" }, storedEntry.Org_md5);
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
            }
            playlist?.RequestShutdown("url-completion-edit-fixture");
            library?.RequestShutdown("url-completion-edit-fixture");
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    /// <summary>
    /// 保証：通常ローカル表の5編集属性を実保存Taskの終端まで待ち、entry・編集/同期/再作成row・新DB・新playlistへ反映する。
    /// Aの未編集属性と識別子、B全属性を保持する。実効URLの確定と必要LR2出力は既存ケースへ分担する。
    /// </summary>
    [DataTestMethod]
    [DataRow(nameof(PlaylistDetailRow.Level), "★12.5", 12.5)]
    [DataRow(nameof(PlaylistDetailRow.Level), "A", 2d)]
    [DataRow(nameof(PlaylistDetailRow.Level), "", null)]
    [DataRow(nameof(PlaylistDetailRow.Url), "https://example.com/edited-main", 2d)]
    [DataRow(nameof(PlaylistDetailRow.Url_diff), "https://example.com/edited-diff", 2d)]
    [DataRow(nameof(PlaylistDetailRow.comment), "edited comment", 2d)]
    [DataRow(nameof(PlaylistDetailRow.memo), "edited memo", 2d)]
    public async Task DetailEdit_LocalAttributesPersistThroughFreshPlaylistAndPresentation(string propertyName, string text, double? expectedLevel)
    {
        string songDbPath = CreateEmptySongDbPath();
        TestBmsPlaylist? playlist = null;
        TestBmsPlaylist? reloaded = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = CreateTable(4101, "LocalAttributes");
            BMSTableEntry entryA = CreateEntry(new string('a', 32), "A");
            BMSTableEntry entryB = CreateEntry(new string('b', 32), "B");
            entryA.level = 2;
            entryA.Url = new Uri("https://example.com/a-main");
            entryA.Url_diff = new Uri("https://example.com/a-diff");
            entryA.comment = "a comment";
            entryA.memo = "a memo";
            entryB.level = 3;
            entryB.Url = new Uri("https://example.com/b-main");
            entryB.Url_diff = new Uri("https://example.com/b-diff");
            entryB.comment = "b comment";
            entryB.memo = "b memo";
            table.entries = [entryA, entryB];
            BMSTableEntry expectedA = entryA.CreatePlaylistReloadSnapshot();
            BMSTableEntry expectedB = entryB.CreatePlaylistReloadSnapshot();
            switch (propertyName)
            {
                case nameof(PlaylistDetailRow.Level): expectedA.level = expectedLevel; break;
                case nameof(PlaylistDetailRow.Url): expectedA.Url = new Uri("https://example.com/edited-main"); break;
                case nameof(PlaylistDetailRow.Url_diff): expectedA.Url_diff = new Uri("https://example.com/edited-diff"); break;
                case nameof(PlaylistDetailRow.comment): expectedA.comment = "edited comment"; break;
                case nameof(PlaylistDetailRow.memo): expectedA.memo = "edited memo"; break;
                default: throw new AssertFailedException("未承認の編集属性です。");
            }
            workspace = CreateSavedDetailWorkspace(songDbPath, table, out playlist, out library);
            var sourceA = new PlaylistDetailSourceRow(entryA, resolvedChart: null);
            var sourceB = new PlaylistDetailSourceRow(entryB, resolvedChart: null);
            PlaylistDetailRow editedRow = sourceA.CreateViewRow();
            workspace.DetailViewState.Source.Rows = [sourceA, sourceB];
            var context = new MainChartListCellEditContext(editedRow, propertyName,
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            workspace.BeginDetailEdit(context);
            Assert.IsTrue(workspace.DetailViewState.Source.IsPlaylistCellEditing);
            await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, text, commit: true));
            Assert.IsFalse(workspace.DetailViewState.Source.IsPlaylistCellEditing);

            void AssertEntry(BMSTableEntry actual, bool isA)
            {
                Assert.AreEqual(table.playlist_id, actual.playlist_id);
                Assert.AreEqual(new string(isA ? 'a' : 'b', 32), actual.md5);
                Assert.AreEqual(isA ? "A" : "B", actual.title);
                Assert.AreEqual("TestArtist", actual.artist);
                Assert.AreEqual(string.Empty, actual.folder);
                Assert.AreEqual(isA && propertyName == nameof(PlaylistDetailRow.Level) ? expectedLevel : isA ? 2d : 3d, actual.level);
                Assert.AreEqual(new Uri(isA && propertyName == nameof(PlaylistDetailRow.Url) ? "https://example.com/edited-main"
                    : isA ? "https://example.com/a-main" : "https://example.com/b-main"), actual.Url);
                Assert.AreEqual(new Uri(isA && propertyName == nameof(PlaylistDetailRow.Url_diff) ? "https://example.com/edited-diff"
                    : isA ? "https://example.com/a-diff" : "https://example.com/b-diff"), actual.Url_diff);
                Assert.AreEqual(isA && propertyName == nameof(PlaylistDetailRow.comment) ? "edited comment" : isA ? "a comment" : "b comment", actual.comment);
                Assert.AreEqual(isA && propertyName == nameof(PlaylistDetailRow.memo) ? "edited memo" : isA ? "a memo" : "b memo", actual.memo);
                Assert.IsTrue(JToken.DeepEquals((isA ? expectedA : expectedB).ToJsonObject(), actual.ToJsonObject()),
                    "固定入力の未編集属性と識別子を保持します。");
            }
            void AssertRow(PlaylistDetailRow row, bool isA)
            {
                AssertEntry(row.Entry, isA);
                Assert.AreEqual(isA && propertyName == nameof(PlaylistDetailRow.Level) ? expectedLevel?.ToString() ?? string.Empty : isA ? "2" : "3", row.Level);
                Assert.AreEqual(row.Entry.Url, row.Url);
                Assert.AreEqual(row.Entry.Url_diff, row.Url_diff);
                Assert.AreEqual(row.Entry.comment, row.comment);
                Assert.AreEqual(row.Entry.memo, row.memo);
                Assert.AreEqual(row.Entry.level, row.EntryLevelSortKey);
                Assert.AreEqual(row.Entry.level, row.Chart.Level);
                Assert.AreEqual(row.Level, row.Chart.LevelText);
            }
            AssertEntry(entryA, isA: true);
            AssertEntry(entryB, isA: false);
            AssertRow(editedRow, isA: true);
            Assert.AreEqual(2, workspace.DetailViewState.Source.Rows.Count);
            CollectionAssert.AreEquivalent(new[] { new string('a', 32), new string('b', 32) },
                workspace.DetailViewState.Source.Rows.Select(source => source.Entry.md5).ToArray());
            foreach (PlaylistDetailSourceRow source in workspace.DetailViewState.Source.Rows)
            {
                AssertRow(source.CreateViewRow(), source.Entry.md5 == entryA.md5);
            }
            Assert.AreEqual("a memo", sourceA.memo, "旧不変入力を確定表示へ書き換えません。");
            Assert.AreEqual(2d, sourceA.EntryValue.Level);
            AssertRow(sourceB.CreateViewRow(), isA: false);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                BMSTableEntry[] stored = [.. db.Table<BMSTableEntry>().Where(entry => entry.playlist_id == table.playlist_id)];
                Assert.AreEqual(2, stored.Length);
                CollectionAssert.AreEquivalent(new[] { new string('a', 32), new string('b', 32) }, stored.Select(entry => entry.md5).ToArray());
                foreach (BMSTableEntry entry in stored) { AssertEntry(entry, entry.md5 == entryA.md5); }
            }
            reloaded = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings, library: library);
            // 保存読戻しの保証に不要な起動時の出力・全表同期を通さず、実repositoryと新playlistのhydration ownerで読む。
            // 期待値はreaderへ渡さず、同期読込の終端後に新しいentry・表示rowを照合する。
            reloaded.BMSTables = new ObservableCollection<BMSTable>(new PlaylistPersistenceRepository(songDbPath).LoadPlaylistHeaders());
            BMSTable freshTable = reloaded.BMSTables.Single(item => item.playlist_id == table.playlist_id);
            Assert.AreNotSame(table, freshTable);
            Assert.IsFalse(freshTable.ArePlaylistEntriesLoaded);
            reloaded.EnsurePlaylistEntriesLoaded(freshTable, "detail_attribute_readback");
            Assert.IsTrue(freshTable.ArePlaylistEntriesLoaded);
            Assert.AreEqual(2, freshTable.entries.Count);
            CollectionAssert.AreEquivalent(new[] { new string('a', 32), new string('b', 32) }, freshTable.entries.Select(entry => entry.md5).ToArray());
            foreach (BMSTableEntry entry in freshTable.entries)
            {
                AssertEntry(entry, entry.md5 == entryA.md5);
                AssertRow(new PlaylistDetailSourceRow(entry, resolvedChart: null).CreateViewRow(), entry.md5 == entryA.md5);
            }
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
            }
            reloaded?.RequestShutdown("detail-attribute-fixture");
            playlist?.RequestShutdown("detail-attribute-fixture");
            library?.RequestShutdown("detail-attribute-fixture");
            DeleteTempSongDbDirectory(songDbPath);
        }
    }

    /// <summary>
    /// 保存済みの現行項目へ無効URIを確定要求し、実Taskの終端後も編集行・source・正本・新DBが元URLと未編集属性を保ち、編集状態を解放することを確認します。
    /// </summary>
    /// <remarks>URI検査前の受付を実playlistへ通し、早期例外による見かけの非保存を除きます。正常保存とURL材料化は既存の実保存系列、取消時の解放はPlaylistViewPipelineTestsが分担します。</remarks>
    [DataTestMethod]
    [DataRow(nameof(PlaylistDetailRow.Url))]
    [DataRow(nameof(PlaylistDetailRow.Url_diff))]
    public async Task DetailEdit_InvalidUriPreservesSavedEntryAndReleasesEditing(string propertyName)
    {
        const int playlistId = 4102;
        const string originalUrl = "https://example.com/original-main";
        const string originalDiffUrl = "https://example.com/original-diff";
        string songDbPath = CreateEmptySongDbPath();
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = CreateTable(playlistId, "InvalidUri");
            table.is_external_sync = false;
            BMSTableEntry entry = CreateEntry(new string('c', 32), "Original");
            entry.level = 7;
            entry.Url = new Uri(originalUrl);
            entry.Url_diff = new Uri(originalDiffUrl);
            entry.comment = "original comment";
            entry.memo = "original memo";
            table.entries = [entry];
            workspace = CreateSavedDetailWorkspace(songDbPath, table, out playlist, out library);
            var source = new PlaylistDetailSourceRow(entry, resolvedChart: null);
            PlaylistDetailRow editedRow = source.CreateViewRow();
            workspace.DetailViewState.Source.Rows = [source];
            var context = new MainChartListCellEditContext(editedRow, propertyName,
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);

            Assert.IsTrue(playlist.ContainsCurrentDetailEntry(entry));
            Assert.IsTrue(workspace.CanBeginDetailEdit(context));
            workspace.BeginDetailEdit(context);
            Assert.IsTrue(workspace.DetailViewState.Source.IsPlaylistCellEditing);
            await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "not a URI", commit: true));
            Assert.IsFalse(workspace.DetailViewState.Source.IsPlaylistCellEditing);

            void AssertEntry(BMSTableEntry actual)
            {
                Assert.AreEqual(playlistId, actual.playlist_id);
                Assert.AreEqual(new string('c', 32), actual.md5);
                Assert.AreEqual("Original", actual.title);
                Assert.AreEqual("TestArtist", actual.artist);
                Assert.AreEqual(string.Empty, actual.folder);
                Assert.AreEqual(7d, actual.level);
                Assert.AreEqual(new Uri(originalUrl), actual.Url);
                Assert.AreEqual(new Uri(originalDiffUrl), actual.Url_diff);
                Assert.AreEqual("original comment", actual.comment);
                Assert.AreEqual("original memo", actual.memo);
            }
            void AssertRow(PlaylistDetailRow actual)
            {
                AssertEntry(actual.Entry);
                Assert.AreEqual("7", actual.Level);
                Assert.AreEqual(new Uri(originalUrl), actual.Url);
                Assert.AreEqual(new Uri(originalDiffUrl), actual.Url_diff);
                Assert.AreEqual("original comment", actual.comment);
                Assert.AreEqual("original memo", actual.memo);
            }
            void AssertHeader(BMSTable actual)
            {
                Assert.AreEqual(playlistId, actual.playlist_id);
                Assert.AreEqual("InvalidUri", actual.name);
                Assert.AreEqual("I", actual.symbol);
                Assert.AreEqual("InvalidUri", actual.Output_dir);
                Assert.IsFalse(actual.is_external_sync);
            }

            AssertEntry(entry);
            AssertRow(editedRow);
            Assert.AreEqual(1, workspace.DetailViewState.Source.Rows.Count);
            Assert.AreSame(source, workspace.DetailViewState.Source.Rows.Single());
            Assert.AreEqual(new Uri(originalUrl), source.Url);
            AssertRow(source.CreateViewRow());
            Assert.AreEqual(1, playlist.BMSTables.Count);
            BMSTable currentTable = playlist.BMSTables.Single();
            AssertHeader(currentTable);
            Assert.AreEqual(1, currentTable.entries.Count);
            BMSTableEntry currentEntry = currentTable.entries.Single();
            Assert.AreSame(entry, currentEntry);
            AssertEntry(currentEntry);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                // 操作後の正本を期待値に使わず、専用DBの全件数と保存入力を独立に照合します。
                BMSTable[] storedTables = [.. db.Table<BMSTable>()];
                BMSTableEntry[] storedEntries = [.. db.Table<BMSTableEntry>()];
                Assert.AreEqual(1, storedTables.Length);
                Assert.AreEqual(1, storedEntries.Length);
                AssertHeader(storedTables.Single());
                AssertEntry(storedEntries.Single());
            }
        }
        finally
        {
            if (workspace != null)
            {
                workspace.CancelDetailBuilds();
                await workspace.WaitForDetailBuildIdleAsync();
            }
            playlist?.RequestShutdown("invalid-uri-edit-fixture");
            library?.RequestShutdown("invalid-uri-edit-fixture");
            DeleteTempSongDbDirectory(songDbPath);
        }
    }

    /// <summary>memo編集中の正規URL補完を保存へ反映し、ローカルURL1/2の空欄解除と別属性保存後にも明示の新候補を適用できます。外部同期表はURLを材料化しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DetailDraft_NormalUrlCompletionThenMemoCommitUsesLatestInput(bool external)
    {
        string path = CreateEmptySongDbPath();
        PlaylistPersistenceRepository.EnsureSchema(path);
        PlaylistWorkspaceViewModel? workspace = null;
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        try
        {
            testSettings.OperationModeLR2DB = false;
            testSettings.EnablePlaylistUrlCompletion = true;
            testSettings.EnableStellaFullPlaylistUrlCompletion = false;
            testSettings.OverwritePlaylistUrlsWithCompletion = false;
            testSettings.PlaylistMd5UrlMappingTsvUri = "https://example.com/playlist-draft.tsv";
            string candidateMain = "https://example.com/completed";
            string candidateDiff = "https://example.com/completed-diff";
            library = MainWindowViewModelTestFactory.CreateLibrary(path, testSettings);
            playlist = new TestBmsPlaylist(path, null, null, null,
                () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(testSettings),
                () => BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings),
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(testSettings), null,
                (_, _) => Task.FromResult($"md5\turl_diff\turl\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\t{candidateDiff}\t{candidateMain}"),
                (_, _) => Task.FromResult("[]"));
            BMSTable table = CreateTable(6201, "draft completion"); table.is_external_sync = external;
            table.entries = [CreateEntry(new string('a', 32), "draft entry")];
            playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
            playlist.CommitBMSTableWithEntriesToDB(table);
            TestBmsPlaylist store = playlist; TestBmsLibrary catalog = library;
            workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _, playlistStoreProvider: () => store,
                playlistLibraryProvider: () => catalog);
            workspace.SetDetailDataSource(new PlaylistDetailDataSource(catalog, store, new MainChartRowProjectionOwner()));
            workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
            workspace.RequestDetailSelection(table);
            await workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected,
                MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected, false, default);
            var rows = (PlaylistDetailVirtualView)workspace.DetailViewState.View.Rows;
            var oldRow = (PlaylistDetailRow)rows[0];
            int reloads = 0;
            workspace.PlaylistDetailReloadRefreshRequested += (_, _) => reloads++;
            var work = new List<string>();
            workspace.DetailWorkObserver = work.Add;
            var context = new MainChartListCellEditContext(oldRow, nameof(PlaylistDetailRow.memo),
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            workspace.BeginDetailEdit(context);
            oldRow.memo = "memo draft";
            await ScheduleUrlCompletionRefreshAsync(store, "draft_overlap");
            Assert.AreSame(oldRow, rows[0]);
            Assert.AreEqual("memo draft", oldRow.memo);
            Assert.AreEqual(new Uri("https://example.com/completed"), oldRow.Url);
            Assert.AreNotEqual("memo draft", oldRow.Entry.memo);
            await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "memo draft", commit: true));
            Assert.AreSame(oldRow, rows[0]);
            Assert.AreEqual("memo draft", oldRow.memo);
            Assert.AreEqual(new Uri("https://example.com/completed"), oldRow.Url);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(path).OpenSongDbReadOnly())
            {
                Assert.AreEqual("memo draft", db.ExecuteScalar<string>("SELECT memo FROM playlist_entry;"));
                string? savedUrl = db.ExecuteScalar<string?>("SELECT url FROM playlist_entry;");
                if (external) { Assert.IsTrue(string.IsNullOrEmpty(savedUrl)); }
                else { Assert.AreEqual("https://example.com/completed", savedUrl); }
            }
            Uri expectedMain = new("https://example.com/completed");
            if (!external)
            {
                // 同じ表示sourceを維持し、通常の編集で別属性を保存します。fixtureの再構築で局所更新の保証を弱めません。
                async Task SaveCurrentMemoAsync(string value)
                {
                    var memoContext = new MainChartListCellEditContext(oldRow, nameof(PlaylistDetailRow.memo),
                        ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
                    workspace.BeginDetailEdit(memoContext);
                    await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(memoContext, value, commit: true));
                }
                foreach (string property in new[] { nameof(PlaylistDetailRow.Url), nameof(PlaylistDetailRow.Url_diff) })
                {
                    var urlContext = new MainChartListCellEditContext(oldRow, property,
                        ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
                    workspace.BeginDetailEdit(urlContext);
                    await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(urlContext, string.Empty, commit: true));
                    Assert.IsNull(property == nameof(PlaylistDetailRow.Url) ? oldRow.Url : oldRow.Url_diff);
                    await SaveCurrentMemoAsync("memo after clear " + property);
                    Assert.IsNull(property == nameof(PlaylistDetailRow.Url) ? oldRow.Url : oldRow.Url_diff);
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(path).OpenSongDbReadOnly())
                    {
                        string? saved = db.ExecuteScalar<string?>(property == nameof(PlaylistDetailRow.Url)
                            ? "SELECT url FROM playlist_entry;" : "SELECT url_diff FROM playlist_entry;");
                        Assert.IsTrue(string.IsNullOrEmpty(saved));
                    }
                    candidateMain = "https://example.com/reapplied-main-" + property;
                    candidateDiff = "https://example.com/reapplied-diff-" + property;
                    testSettings.PlaylistMd5UrlMappingTsvUri = "https://example.com/reapply-" + property + ".tsv";
                    await ScheduleUrlCompletionRefreshAsync(store, "explicit_after_clear");
                    Uri expected = new(property == nameof(PlaylistDetailRow.Url) ? candidateMain : candidateDiff);
                    Assert.AreEqual(expected, property == nameof(PlaylistDetailRow.Url) ? oldRow.Url : oldRow.Url_diff);
                    Assert.AreEqual(expected, property == nameof(PlaylistDetailRow.Url) ? oldRow.Entry.EffectiveUrl : oldRow.Entry.EffectiveUrlDiff);
                    await SaveCurrentMemoAsync("memo draft");
                    using (LR2SongDBExtended db = new BmsLibraryDbGateway(path).OpenSongDbReadOnly())
                    {
                        string? saved = db.ExecuteScalar<string?>(property == nameof(PlaylistDetailRow.Url)
                            ? "SELECT url FROM playlist_entry;" : "SELECT url_diff FROM playlist_entry;");
                        Assert.AreEqual(expected.AbsoluteUri, saved);
                    }
                    if (property == nameof(PlaylistDetailRow.Url)) { expectedMain = expected; }
                }
            }
            testSettings.EnablePlaylistUrlCompletion = false;
            await ScheduleUrlCompletionRefreshAsync(store, "draft_overlap_disable");
            Assert.AreEqual("memo draft", oldRow.memo);
            Assert.AreEqual(external ? null : expectedMain, oldRow.Url);
            Assert.AreEqual(0, reloads);
            Assert.AreEqual(0, work.Count(kind => kind == "playlist_dependency_index_entry"));
            Assert.AreEqual(0, work.Count(kind => kind == "playlist_order_buffer"));
        }
        finally
        {
            if (workspace != null) { workspace.CancelDetailBuilds(); await workspace.WaitForDetailBuildIdleAsync(); workspace.Dispose(); }
            playlist?.RequestShutdown("draft_url_fixture"); library?.RequestShutdown("draft_url_fixture");
            DeleteTempSongDbDirectory(path);
        }
    }

    private static async Task ScheduleUrlCompletionRefreshAsync(BMSPlaylist playlist, string reason)
    {
        Task scheduledWork = Task.CompletedTask;
        bool schedulerCalled = false;
        playlist.StartupBackgroundTaskScheduler = (operation, requestReason, dependency, work) =>
        {
            scheduledWork = work();
            schedulerCalled = true;
            return true;
        };

        playlist.SchedulePlaylistUrlCompletionRefresh(reason);

        Assert.IsTrue(schedulerCalled);
        await scheduledWork;
    }

    private static BMSTableEntry CreateEntry(string md5, string title)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = md5, Path = title + ".bms", Title = title, RawTitle = title, Artist = "TestArtist", RawArtist = "TestArtist", Genre = "TestGenre", Mode = 7 };
        return new BMSTableEntry(file)
        {
            folder = string.Empty
        };
    }

    private static TestablePlaylistEntry CreateShaOnlyEntry(string sha256, string title, string memo)
    {
        var entry = new TestablePlaylistEntry
        {
            folder = string.Empty,
            memo = memo
        };
        entry.SetSha256(sha256);
        entry.SetTitle(title);
        return entry;
    }

    private static BMSTable CreateTable(int playlistId, string name)
    {
        return new BMSTable
        {
            playlist_id = playlistId,
            name = name,
            symbol = name.Substring(0, 1),
            Output_dir = name
        };
    }

    /// <summary>実編集 writer の待機中に live entry・日時・版・別表示行へ draft が漏れず、解放後だけ確定値が届くことを確認します。</summary>
    [TestMethod]
    public async Task DetailEdit_WaitingWriterKeepsDraftOutsideLiveEntry()
    {
        string songDbPath = CreateEmptySongDbPath();
        TestBmsPlaylist? playlist = null;
        TestBmsLibrary? library = null;
        PlaylistWorkspaceViewModel? workspace = null;
        Task? reader = null;
        Task? edit = null;
        var readerHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            BMSTable table = CreateTable(4199, "DraftIsolation");
            BMSTableEntry entry = CreateEntry(new string('d', 32), "draft");
            entry.memo = "before";
            table.entries = [entry];
            workspace = CreateSavedDetailWorkspace(songDbPath, table, out playlist, out library);
            var source = new PlaylistDetailSourceRow(entry, resolvedChart: null);
            PlaylistDetailRow row = source.CreateViewRow();
            workspace.DetailViewState.Source.Rows = [source];
            var context = new MainChartListCellEditContext(row, nameof(PlaylistDetailRow.memo),
                ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
            DateTime date = table.last_update;
            int revision = table.PlaylistEntriesRevision;
            reader = Task.Run(() =>
            {
                using (table.ReaderWriterLock.GetReaderGuard())
                {
                    readerHeld.TrySetResult();
                    SpinWait.SpinUntil(() => table.ReaderWriterLock.WaitingWriteCount > 0 || editFinished.Task.IsCompleted);
                    Assert.IsTrue(table.ReaderWriterLock.WaitingWriteCount > 0);
                    Assert.AreEqual("before", entry.memo);
                    Assert.AreEqual(date, table.last_update);
                    Assert.AreEqual(revision, table.PlaylistEntriesRevision);
                    Assert.AreEqual("before", source.CreateViewRow().memo);
                }
            });
            await readerHeld.Task;
            workspace.BeginDetailEdit(context);
            edit = workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, "saved", commit: true));
            _ = edit.ContinueWith(_ => editFinished.TrySetResult(), TaskScheduler.Default);
            await Task.WhenAll(reader, edit);
            Assert.AreEqual("saved", entry.memo);
            Assert.AreEqual("saved", row.memo);
        }
        finally
        {
            editFinished.TrySetResult();
            try { if (reader != null) { await reader; } }
            finally
            {
                try { if (edit != null) { await edit; } }
                finally
                {
                    workspace?.Dispose();
                    playlist?.RequestShutdown("draft-isolation");
                    library?.RequestShutdown("draft-isolation");
                    DeleteTempSongDbDirectory(songDbPath);
                }
            }
        }
    }

    /// <summary>既存の保存fixtureを組み立て、実Task合流後に解放するplaylistとlibraryの所有者を返す。</summary>
    private PlaylistWorkspaceViewModel CreateSavedDetailWorkspace(string songDbPath, BMSTable table,
        out TestBmsPlaylist playlist, out TestBmsLibrary library)
    {
        testSettings.OperationModeLR2DB = false;
        library = new TestBmsLibrary(songDbPath, null, null, null,
            () => BmsLibraryOptionsSnapshot.CreateCurrent(testSettings));
        playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDbPath, testSettings, library: library);
        playlist.BMSTables = new ObservableCollection<BMSTable>([table]);
        playlist.CommitBMSTableWithEntriesToDB(table);
        TestBmsPlaylist acceptedPlaylist = playlist;
        TestBmsLibrary acceptedLibrary = library;
        PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceFixtureFactory.CreateDetailWorkspace(out _,
            playlistStoreProvider: () => acceptedPlaylist, playlistLibraryProvider: () => acceptedLibrary);
        workspace.PlaylistOperationNotificationPresentationRequested += (_, _) => { };
        workspace.SetDetailDataSource(new PlaylistDetailDataSource(acceptedLibrary, acceptedPlaylist, new MainChartRowProjectionOwner()));
        return workspace;
    }

    private static async Task SaveDetailMemoAsync(PlaylistWorkspaceViewModel workspace,
        BMSTableEntry entry, string memo, ChartFile? chart = null)
    {
        var source = new PlaylistDetailSourceRow(entry, chart);
        PlaylistDetailRow row = source.CreateViewRow();
        workspace.DetailViewState.Source.Rows = [source];
        var context = new MainChartListCellEditContext(row, nameof(PlaylistDetailRow.memo),
            ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        workspace.BeginDetailEdit(context);
        await workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(context, memo, commit: true));
        Assert.AreEqual(memo, entry.memo);
    }

    private static string CreateEmptySongDbPath()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistUrlCompletionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string tempDbPath = Path.Combine(tempDirectory, "song.db");
        using (var _ = new LR2SongDBExtended(tempDbPath))
        {
        }
        return tempDbPath;
    }

    private static void DeleteTempSongDbDirectory(string songDbPath)
    {
        if (string.IsNullOrWhiteSpace(songDbPath))
        {
            return;
        }
        string directoryPath = Path.GetDirectoryName(songDbPath)!;
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }


    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public void SetOrgMd5Raw(string value)
        {
            org_md5 = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }


        public void SetTitle(string value)
        {
            title = value;
        }
    }
}
