using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PlaylistUrlCompletionTests
{
    [TestInitialize]
    public void ResetPlaylistUrlCompletionSourceCacheBeforeTest()
    {
        BMSPlaylist.ResetPlaylistUrlCompletionSourceCacheForTests();
    }

    [TestCleanup]
    public void ResetPlaylistUrlCompletionSourceCacheAfterTest()
    {
        BMSPlaylist.ResetPlaylistUrlCompletionSourceCacheForTests();
    }

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

    [TestMethod]
    [TestCategory("Playlist")]
    public void TryResolveSourceUri_LocalFilePath_ResolvesFileUri()
    {
        string tempFilePath = Path.GetTempFileName();
        try
        {
            bool resolved = PlaylistUrlCompletionSupport.TryResolveSourceUri(tempFilePath, out Uri uri);

            Assert.IsTrue(resolved);
            Assert.IsNotNull(uri);
            Assert.IsTrue(uri.IsAbsoluteUri);
            Assert.AreEqual(Uri.UriSchemeFile, uri.Scheme);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

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

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task BMSPlaylist_UrlCompletionFetchesSourcesOnceAndRefetchesTsvWhenUriChanges()
    {
        string tempDbPath = CreateEmptySongDbPath();
        string previousTsvUri = Settings.Default.PlaylistMd5UrlMappingTsvUri;
        bool previousEnableCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOverwriteCompletion = Settings.Default.OverwritePlaylistUrlsWithCompletion;
        bool previousEnableStella = Settings.Default.EnableStellaFullPlaylistUrlCompletion;
        Func<Uri, CancellationToken, Task<string>> previousTsvFetcher = BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests;
        Func<Uri, CancellationToken, Task<string>> previousStellaFetcher = BMSPlaylist.PlaylistUrlCompletionStellaContentFetcherForTests;
        try
        {
            int tsvFetchCount = 0;
            int stellaFetchCount = 0;
            Settings.Default.EnablePlaylistUrlCompletion = true;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = false;
            Settings.Default.EnableStellaFullPlaylistUrlCompletion = true;
            Settings.Default.PlaylistMd5UrlMappingTsvUri = "https://example.com/map-a.tsv";
            BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests = (uri, cancellationToken) =>
            {
                tsvFetchCount++;
                string mainUrl = uri.AbsoluteUri.IndexOf("map-b.tsv", StringComparison.OrdinalIgnoreCase) >= 0 ? "https://example.com/main-tsv-b" : "https://example.com/main-tsv-a";
                return Task.FromResult("md5\turl_diff\turl\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\thttps://example.com/diff-tsv\t" + mainUrl);
            };
            BMSPlaylist.PlaylistUrlCompletionStellaContentFetcherForTests = (uri, cancellationToken) =>
            {
                stellaFetchCount++;
                return Task.FromResult("[" +
                    "{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"url\":\"https://example.com/main-stella-a\",\"url_diff\":\"https://example.com/diff-stella-a\"}," +
                    "{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"url\":\"https://example.com/main-stella-b\",\"url_diff\":\"https://example.com/diff-stella-b\"}" +
                    "]");
            };
            var playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(5001, "CompletionTable");
            BMSTableEntry tsvEntry = CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "TsvSong");
            BMSTableEntry stellaEntry = CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "StellaSong");
            table.entries = [tsvEntry, stellaEntry];
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);

            await playlist.RefreshPlaylistUrlCompletionForTestsAsync("first");
            await playlist.RefreshPlaylistUrlCompletionForTestsAsync("reload");

            Assert.AreEqual(1, tsvFetchCount);
            Assert.AreEqual(1, stellaFetchCount);
            Assert.AreEqual(new Uri("https://example.com/main-tsv-a"), tsvEntry.RuntimeUrlCompletion);
            Assert.AreEqual(new Uri("https://example.com/diff-tsv"), tsvEntry.RuntimeUrlDiffCompletion);
            Assert.AreEqual(new Uri("https://example.com/main-stella-b"), stellaEntry.RuntimeUrlCompletion);
            Assert.AreEqual(new Uri("https://example.com/diff-stella-b"), stellaEntry.RuntimeUrlDiffCompletion);

            Settings.Default.PlaylistMd5UrlMappingTsvUri = "https://example.com/map-b.tsv";
            await playlist.RefreshPlaylistUrlCompletionForTestsAsync("settings_changed");

            Assert.AreEqual(2, tsvFetchCount);
            Assert.AreEqual(1, stellaFetchCount);
            Assert.AreEqual(new Uri("https://example.com/main-tsv-b"), tsvEntry.RuntimeUrlCompletion);
        }
        finally
        {
            BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests = previousTsvFetcher;
            BMSPlaylist.PlaylistUrlCompletionStellaContentFetcherForTests = previousStellaFetcher;
            Settings.Default.PlaylistMd5UrlMappingTsvUri = previousTsvUri;
            Settings.Default.EnablePlaylistUrlCompletion = previousEnableCompletion;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = previousOverwriteCompletion;
            Settings.Default.EnableStellaFullPlaylistUrlCompletion = previousEnableStella;
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task BMSPlaylist_StellaFullSettingDisablesFetchAndClearsStellaRuntimeCompletion()
    {
        string tempDbPath = CreateEmptySongDbPath();
        string previousTsvUri = Settings.Default.PlaylistMd5UrlMappingTsvUri;
        bool previousEnableCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOverwriteCompletion = Settings.Default.OverwritePlaylistUrlsWithCompletion;
        bool previousEnableStella = Settings.Default.EnableStellaFullPlaylistUrlCompletion;
        Func<Uri, CancellationToken, Task<string>> previousTsvFetcher = BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests;
        Func<Uri, CancellationToken, Task<string>> previousStellaFetcher = BMSPlaylist.PlaylistUrlCompletionStellaContentFetcherForTests;
        try
        {
            int stellaFetchCount = 0;
            Settings.Default.EnablePlaylistUrlCompletion = true;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = false;
            Settings.Default.EnableStellaFullPlaylistUrlCompletion = false;
            Settings.Default.PlaylistMd5UrlMappingTsvUri = "https://example.com/empty.tsv";
            BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests = (uri, cancellationToken) => Task.FromResult("md5\turl_diff\turl");
            BMSPlaylist.PlaylistUrlCompletionStellaContentFetcherForTests = (uri, cancellationToken) =>
            {
                stellaFetchCount++;
                return Task.FromResult("[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"url\":\"https://example.com/main-stella\",\"url_diff\":\"https://example.com/diff-stella\"}]");
            };
            var playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(5002, "StellaToggleTable");
            BMSTableEntry stellaEntry = CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "StellaSong");
            table.entries = [stellaEntry];
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);

            await playlist.RefreshPlaylistUrlCompletionForTestsAsync("disabled");

            Assert.AreEqual(0, stellaFetchCount);
            Assert.IsNull(stellaEntry.RuntimeUrlCompletion);
            Assert.IsNull(stellaEntry.RuntimeUrlDiffCompletion);

            Settings.Default.EnableStellaFullPlaylistUrlCompletion = true;
            await playlist.RefreshPlaylistUrlCompletionForTestsAsync("enabled");

            Assert.AreEqual(1, stellaFetchCount);
            Assert.AreEqual(new Uri("https://example.com/main-stella"), stellaEntry.RuntimeUrlCompletion);
            Assert.AreEqual(new Uri("https://example.com/diff-stella"), stellaEntry.RuntimeUrlDiffCompletion);

            Settings.Default.EnableStellaFullPlaylistUrlCompletion = false;
            await playlist.RefreshPlaylistUrlCompletionForTestsAsync("disabled_again");

            Assert.AreEqual(1, stellaFetchCount);
            Assert.IsNull(stellaEntry.RuntimeUrlCompletion);
            Assert.IsNull(stellaEntry.RuntimeUrlDiffCompletion);
        }
        finally
        {
            BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests = previousTsvFetcher;
            BMSPlaylist.PlaylistUrlCompletionStellaContentFetcherForTests = previousStellaFetcher;
            Settings.Default.PlaylistMd5UrlMappingTsvUri = previousTsvUri;
            Settings.Default.EnablePlaylistUrlCompletion = previousEnableCompletion;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = previousOverwriteCompletion;
            Settings.Default.EnableStellaFullPlaylistUrlCompletion = previousEnableStella;
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task BMSPlaylist_UrlCompletionUsesInjectedOptionsProvider()
    {
        string tempDbPath = CreateEmptySongDbPath();
        Func<Uri, CancellationToken, Task<string>> previousTsvFetcher = BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests;
        try
        {
            PlaylistUrlCompletionOptionsSnapshot options = new()
            {
                EnablePlaylistUrlCompletion = false,
                PlaylistMd5UrlMappingTsvUri = "https://example.com/injected.tsv",
                OverwritePlaylistUrlsWithCompletion = true
            };
            int fetchCount = 0;
            BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests = (uri, cancellationToken) =>
            {
                fetchCount++;
                return Task.FromResult("md5\turl_diff\turl\r\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\t\thttps://example.com/injected");
            };
            var playlist = new BMSPlaylist(
                tempDbPath,
                null,
                null,
                null,
                null,
                () => options,
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot());
            BMSTable table = CreateTable(5003, "InjectedOptionsTable");
            BMSTableEntry entry = CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "InjectedOptionsSong");
            table.entries = [entry];
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);

            await playlist.RefreshPlaylistUrlCompletionForTestsAsync("disabled");

            Assert.AreEqual(0, fetchCount);
            Assert.IsNull(entry.RuntimeUrlCompletion);

            options = new PlaylistUrlCompletionOptionsSnapshot
            {
                EnablePlaylistUrlCompletion = true,
                PlaylistMd5UrlMappingTsvUri = "https://example.com/injected.tsv",
                OverwritePlaylistUrlsWithCompletion = true
            };
            await playlist.RefreshPlaylistUrlCompletionForTestsAsync("enabled");

            Assert.AreEqual(1, fetchCount);
            Assert.AreEqual(new Uri("https://example.com/injected"), entry.RuntimeUrlCompletion);
        }
        finally
        {
            BMSPlaylist.PlaylistUrlCompletionTsvContentFetcherForTests = previousTsvFetcher;
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void PlaylistDetailSourceRow_UsesEffectiveUrlForDisplay()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "SongE", 7);
        var entry = new BMSTableEntry(file);
        entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

        var sourceRow = new PlaylistDetailSourceRow(entry, ChartFileProjection.FromBmsFile(file));

        Assert.AreEqual(new Uri("https://example.com/runtime"), sourceRow.Url);
        Assert.AreEqual(new Uri("https://example.com/runtime-diff"), sourceRow.Url_diff);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_LocalPlaylistMaterializesEffectiveUrlsIntoDatabase()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(4001, "LocalTable");
            var oldLastUpdate = DateTime.Now.AddDays(1);
            table.last_update = oldLastUpdate;
            BMSTableEntry entry = CreateEntry("ffffffffffffffffffffffffffffffff", "LocalSong");
            table.entries = [entry];
            InsertPlaylistHeader(tempDbPath, table);
            entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

            playlist.CommitBMSTableEntry(entry);

            using var db = new LR2SongDBExtended(tempDbPath);
            BMSTableEntry storedEntry = db.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.md5 == entry.md5);
            Assert.AreEqual(new Uri("https://example.com/runtime"), entry.Url);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.Url_diff);
            Assert.IsNull(entry.RuntimeUrlCompletion);
            Assert.IsNull(entry.RuntimeUrlDiffCompletion);
            Assert.AreEqual(new Uri("https://example.com/runtime"), storedEntry.Url);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), storedEntry.Url_diff);
            BMSTable storedTable = db.Table<BMSTable>().Single(row => row.playlist_id == table.playlist_id);
            Assert.IsTrue(table.last_update > oldLastUpdate);
            Assert.AreEqual(table.last_update, storedTable.last_update);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_ExternalSyncPlaylistDoesNotMaterializeEffectiveUrls()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(4002, "ExternalTable");
            table.Page_url = new Uri("https://example.com/page.html");
            table.Header_url = new Uri("https://example.com/header.json");
            table.Data_url = new Uri("https://example.com/data.json");
            table.EnableExternalSync();
            BMSTableEntry entry = CreateEntry("12121212121212121212121212121212", "ExternalSong");
            table.entries = [entry];
            InsertPlaylistHeader(tempDbPath, table);
            entry.ApplyRuntimeUrlCompletion(new Uri("https://example.com/runtime"), new Uri("https://example.com/runtime-diff"), overwriteExisting: false);

            playlist.CommitBMSTableEntry(entry);

            using var db = new LR2SongDBExtended(tempDbPath);
            BMSTableEntry storedEntry = db.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.md5 == entry.md5);
            Assert.IsNull(entry.Url);
            Assert.IsNull(entry.Url_diff);
            Assert.AreEqual(new Uri("https://example.com/runtime"), entry.EffectiveUrl);
            Assert.AreEqual(new Uri("https://example.com/runtime-diff"), entry.EffectiveUrlDiff);
            Assert.IsNull(storedEntry.Url);
            Assert.IsNull(storedEntry.Url_diff);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_Sha256Identity_ReplacesExistingRowWithoutDuplicates()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(4003, "ShaTable");
            InsertPlaylistHeader(tempDbPath, table);
            TestablePlaylistEntry first = CreateShaOnlyEntry("3434343434343434343434343434343434343434343434343434343434343434", "ShaSong", "memo-1");
            first.playlist_id = table.playlist_id;
            TestablePlaylistEntry second = CreateShaOnlyEntry("3434343434343434343434343434343434343434343434343434343434343434", "ShaSong", "memo-2");
            second.playlist_id = table.playlist_id;

            playlist.CommitBMSTableEntry(first);
            playlist.CommitBMSTableEntry(second);

            using var db = new LR2SongDBExtended(tempDbPath);
            List<BMSTableEntry> rows = [.. db.Table<BMSTableEntry>().Where(row => row.playlist_id == table.playlist_id && row.sha256 == second.sha256)];
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("memo-2", rows[0].memo);
            Assert.IsNull(rows[0].md5);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_AddedMd5ReplacesExistingSha256CompatibilityRow()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(4005, "ShaToBothTable");
            InsertPlaylistHeader(tempDbPath, table);
            string sha256 = "4545454545454545454545454545454545454545454545454545454545454545";
            TestablePlaylistEntry first = CreateShaOnlyEntry(sha256, "BmsonSong", "old");
            first.playlist_id = table.playlist_id;
            TestablePlaylistEntry second = CreateShaOnlyEntry(sha256, "BmsonSong", "new");
            second.SetMd5("abababababababababababababababab");
            second.playlist_id = table.playlist_id;

            playlist.CommitBMSTableEntry(first);
            playlist.CommitBMSTableEntry(second);

            using var db = new LR2SongDBExtended(tempDbPath);
            List<BMSTableEntry> rows = [.. db.Table<BMSTableEntry>().Where(row => row.playlist_id == table.playlist_id)];
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(second.md5, rows[0].md5);
            Assert.AreEqual(second.sha256, rows[0].sha256);
            Assert.AreEqual("new", rows[0].memo);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ToDynamicJson_RoundTripsSha256()
    {
        TestablePlaylistEntry entry = CreateShaOnlyEntry("5656565656565656565656565656565656565656565656565656565656565656", "ShaRoundTrip", "memo");

        var reloaded = new BMSTableEntry(entry.ToDynamicJson());

        Assert.AreEqual(entry.sha256, reloaded.sha256);
        Assert.AreEqual(entry.title, reloaded.title);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ToDynamicJson_NormalizesNullOrgMd5ToEmpty()
    {
        TestablePlaylistEntry entry = CreateShaOnlyEntry("6767676767676767676767676767676767676767676767676767676767676767", "OrgEmpty", "memo");
        entry.Org_md5 = null;

        dynamic json = entry.ToDynamicJson();

        CollectionAssert.AreEqual(Array.Empty<string>(), ((object[])json.org_md5s).Select(value => value?.ToString()).ToArray());
        Assert.AreEqual(string.Empty, (string)json.org_md5);
        Assert.AreEqual(string.Empty, entry.org_md5);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void BMSTableEntry_ParsesStringNullOrgMd5AsEmpty()
    {
        TestablePlaylistEntry entry = CreateShaOnlyEntry("7878787878787878787878787878787878787878787878787878787878787878", "OrgStringNull", "memo");
        entry.SetOrgMd5Raw("null");

        dynamic json = entry.ToDynamicJson();

        CollectionAssert.AreEqual(Array.Empty<string>(), ((object[])json.org_md5s).Select(value => value?.ToString()).ToArray());
        Assert.AreEqual(string.Empty, (string)json.org_md5);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_BmsonPlaylistEntry_PersistsBothHashesAndOrgMd5()
    {
        string tempDbPath = CreateEmptySongDbPath();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(tempDbPath);
            var playlist = new BMSPlaylist(tempDbPath);
            BMSTable table = CreateTable(4004, "BmsonTable");
            InsertPlaylistHeader(tempDbPath, table);
            var song = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(Path.GetTempPath(), "playlist-bmson-test", "song.bmson"),
                folder = string.Empty,
                title = "BmsonSong",
                artist = "Artist",
                level = 12,
                md5 = "99999999999999999999999999999999",
                sha256 = "8989898989898989898989898989898989898989898989898989898989898989"
            };
            var entry = new BMSTableEntry(ChartFileProjection.FromBmsonSong(song))
            {
                folder = string.Empty,
                playlist_id = table.playlist_id,
                Org_md5 = ["abababababababababababababababab"]
            };

            playlist.CommitBMSTableEntry(entry);

            using var db = new LR2SongDBExtended(tempDbPath);
            BMSTableEntry storedEntry = db.Table<BMSTableEntry>().Single(row => row.playlist_id == table.playlist_id && row.sha256 == entry.sha256);
            Assert.AreEqual("99999999999999999999999999999999", storedEntry.md5);
            Assert.AreEqual("8989898989898989898989898989898989898989898989898989898989898989", storedEntry.sha256);
            CollectionAssert.AreEqual(new[] { "abababababababababababababababab" }, storedEntry.Org_md5);
        }
        finally
        {
            DeleteTempSongDbDirectory(tempDbPath);
        }
    }

    private static BMSTableEntry CreateEntry(string md5, string title)
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot(md5, title, 7);
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

    private static void InsertPlaylistHeader(string songDbPath, BMSTable table)
    {
        using var db = new LR2SongDBExtended(songDbPath);
        db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
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
        string directoryPath = Path.GetDirectoryName(songDbPath);
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void ApplySnapshot(string snapshotHash, string snapshotTitle, int? snapshotMode)
        {
            hash = snapshotHash;
            path = snapshotTitle + ".bms";
            Title = snapshotTitle;
            Artist = "TestArtist";
            genre = "TestGenre";
            mode = snapshotMode;
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

        public void SetMd5(string value)
        {
            md5 = value;
        }

        public void SetTitle(string value)
        {
            title = value;
        }
    }
}
