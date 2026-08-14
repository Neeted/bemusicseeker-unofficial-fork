using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistRecommendedTableOwnerTests
{
    [TestMethod]
    public void LoadWalkureTable_RejectsNonBmseekerUri()
    {
        PlaylistRecommendedTableOwner owner = CreateOwner();

        ArgumentException exception = Assert.ThrowsException<ArgumentException>(
            () => owner.LoadWalkureTable(new Uri("https://example.invalid/table.json")));

        StringAssert.StartsWith(exception.Message, Resources.Error_SchemeMustBeBemusic);
    }

    [TestMethod]
    public void LoadWalkureTable_RejectsUnsupportedRoute()
    {
        PlaylistRecommendedTableOwner owner = CreateOwner();

        ArgumentException exception = Assert.ThrowsException<ArgumentException>(
            () => owner.LoadWalkureTable(new Uri("bmseeker:table.unsupported")));

        StringAssert.StartsWith(exception.Message, Resources.Error_UnsupportedURI);
    }

    [TestMethod]
    public void LoadWalkureTable_RecommendedWithoutScoreDatabaseFailsBeforeFetch()
    {
        PlaylistRecommendedTableOwner owner = CreateOwner();

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
            () => owner.LoadWalkureTable(new Uri("bmseeker:table.recommended?id=0")));

        Assert.AreEqual(Resources.Error_ScoreDBConnectionFailed, exception.Message);
    }

    [TestMethod]
    public void LoadWalkureTable_RecommendedBuildsEntriesAndPreservesBaseProperties()
    {
        const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable insane = CreateTable(CreateEntry(md5, "1001", "Insane song"));
        BMSTable overjoy = CreateTable();
        var httpClient = new FakeHttpClient
        {
            GetStringHandler = _ => "{\"status\":\"success\",\"hoshi\":12.5,\"last_modified\":0,\"name\":\"Remote〜Name\",\"recommended\":[{\"bms\":{\"type\":\"normal\",\"bmsid\":1001},\"new_lamp\":\"hard\",\"p\":4.25}]}",
        };
        PlaylistRecommendedTableOwner owner = CreateOwner(
            httpClient: httpClient,
            externalTableLoader: uri => uri.AbsoluteUri.IndexOf("insane1", StringComparison.Ordinal) >= 0 ? insane : overjoy);
        var baseTable = new BMSTable
        {
            compat_prefix = "BASE ",
            playlist_id = 17,
            symbol = "BASE",
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
            is_external_sync = false,
            custom_folder_output_base_name = "base-output",
            bmt_sort = 4,
            is_bmt_output = true
        };

        BMSTable table = owner.LoadWalkureTable(
            new Uri("bmseeker:table.recommended?id=123&mode=readonly&name=Shown"),
            baseTable);

        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual(md5, table.entries.Single().md5);
        Assert.AreEqual("HARD", table.entries.Single().folder);
        Assert.AreEqual(4.25, table.entries.Single().level);
        Assert.AreEqual("BASE ", table.compat_prefix);
        Assert.AreEqual(17, table.playlist_id);
        Assert.AreEqual("BASE", table.symbol);
        Assert.AreEqual(LR2SongDBExtended.playlist.CustomFolderType.UserFolder, table.ignore_folder_output);
        Assert.IsFalse(table.is_external_sync);
        Assert.AreEqual("base-output", table.custom_folder_output_base_name);
        Assert.AreEqual(4, table.bmt_sort);
        Assert.IsTrue(table.is_bmt_output);
        Assert.AreEqual(1, httpClient.GetUris.Count);
        StringAssert.Contains(httpClient.GetUris.Single().Query, "id=123");
    }

    [TestMethod]
    public void LoadWalkureTable_RecommendedSkipsInvalidRowsAndKeepsNumericContract()
    {
        const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable insane = CreateTable(CreateEntry(md5, "1001", "Insane song"));
        var httpClient = new FakeHttpClient
        {
            GetStringHandler = _ => "{\"status\":\"success\",\"hoshi\":12.5,\"last_modified\":0,\"name\":\"Remote\",\"recommended\":[null,{\"bms\":{\"type\":\"normal\",\"bmsid\":\"1001\"},\"new_lamp\":\"hard\",\"p\":4.25},{\"bms\":{\"type\":\"normal\",\"bmsid\":1001},\"new_lamp\":\"clear\",\"p\":3.5}]}"
        };
        PlaylistRecommendedTableOwner owner = CreateOwner(
            httpClient: httpClient,
            externalTableLoader: uri => uri.AbsoluteUri.IndexOf("insane1", StringComparison.Ordinal) >= 0 ? insane : CreateTable());

        BMSTable table = owner.LoadWalkureTable(new Uri("bmseeker:table.recommended?id=123&mode=readonly"));

        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual("CLEAR", table.entries.Single().folder);
        Assert.AreEqual(3.5, table.entries.Single().level);
    }

    [TestMethod]
    public void LoadWalkureTable_RecommendedSkipsRowsWithMissingRequiredFields()
    {
        const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable insane = CreateTable(CreateEntry(md5, "1001", "Insane song"));
        var httpClient = new FakeHttpClient
        {
            GetStringHandler = _ => "{\"status\":\"success\",\"hoshi\":12.5,\"last_modified\":0.5,\"name\":\"Remote\",\"recommended\":["
                + "{\"bms\":{\"bmsid\":1001},\"new_lamp\":\"hard\",\"p\":4.25},"
                + "{\"bms\":{\"type\":\"normal\",\"bmsid\":1001},\"p\":4.25},"
                + "{\"bms\":{\"type\":\"normal\",\"bmsid\":1001},\"new_lamp\":\"hard\"},"
                + "{\"bms\":{\"type\":\"normal\",\"bmsid\":1001.5},\"new_lamp\":\"clear\",\"p\":null}]}"
        };
        PlaylistRecommendedTableOwner owner = CreateOwner(
            httpClient: httpClient,
            externalTableLoader: uri => uri.AbsoluteUri.IndexOf("insane1", StringComparison.Ordinal) >= 0 ? insane : CreateTable());

        BMSTable table = owner.LoadWalkureTable(new Uri("bmseeker:table.recommended?id=123&mode=readonly"));

        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual("CLEAR", table.entries.Single().folder);
        Assert.IsNull(table.entries.Single().level);
    }

    [TestMethod]
    public void LoadWalkureTable_EstimationSkipsRowsWithMissingRequiredFields()
    {
        const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        BMSTable insane = CreateTable(CreateEntry(md5, "1001", "Insane song"));
        var httpClient = new FakeHttpClient
        {
            GetStringHandler = _ => "{\"1\":{\"bmsid\":999,\"hoshi\":{\"easy\":1.5,\"normal\":null,\"hard\":3,\"fc\":4},\"type\":\"normal\"},"
                + "\"2\":{\"bmsid\":1001,\"hoshi\":{\"easy\":2.5,\"normal\":null,\"hard\":3,\"fc\":4}},"
                + "\"3\":{\"bmsid\":1001.5,\"hoshi\":{\"easy\":3.5,\"normal\":null,\"hard\":3,\"fc\":4},\"type\":\"normal\"}}"
        };
        PlaylistRecommendedTableOwner owner = CreateOwner(
            httpClient: httpClient,
            externalTableLoader: uri => uri.AbsoluteUri.IndexOf("insane1", StringComparison.Ordinal) >= 0 ? insane : CreateTable());

        BMSTable table = owner.LoadWalkureTable(new Uri("bmseeker:table.estimation?type=easy"));

        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual(3.5, table.entries.Single().level);
    }

    [TestMethod]
    public void LoadWalkureTable_RecommendedFetchFailureQueuesWarningAndThrows()
    {
        var httpClient = new FakeHttpClient
        {
            GetStringHandler = _ => "{\"status\":\"failed\",\"message\":\"offline\"}"
        };
        var notificationOwner = new PlaylistOperationNotificationOwner();
        PlaylistRecommendedTableOwner owner = CreateOwner(
            httpClient: httpClient,
            notificationOwner: notificationOwner);

        using PlaylistOperationNotificationOwner.OperationNotificationSession session = notificationOwner.BeginSession();
        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
            () => owner.LoadWalkureTable(new Uri("bmseeker:table.recommended?id=123&mode=readonly")));

        Assert.AreEqual(Resources.Error_RecommendFetchFailed, exception.Message);
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
        Assert.AreEqual(1, receipt.Notifications.Count);
        StringAssert.Contains(receipt.Notifications[0].Message, "offline");
        Assert.AreEqual(1, httpClient.GetUris.Count);
    }

    [TestMethod]
    public void LoadWalkureTable_RecommendedMissingDocumentFieldsFailsWithoutWarning()
    {
        foreach (string response in new[]
        {
            "{\"hoshi\":12.5,\"last_modified\":0,\"name\":\"Remote\",\"recommended\":[]}",
            "{\"status\":\"failed\",\"hoshi\":12.5,\"last_modified\":0,\"name\":\"Remote\",\"recommended\":[]}",
            "{\"status\":\"success\",\"hoshi\":12.5,\"last_modified\":0,\"recommended\":[]}"
        })
        {
            var httpClient = new FakeHttpClient { GetStringHandler = _ => response };
            var notificationOwner = new PlaylistOperationNotificationOwner();
            PlaylistRecommendedTableOwner owner = CreateOwner(
                httpClient: httpClient,
                notificationOwner: notificationOwner);

            using PlaylistOperationNotificationOwner.OperationNotificationSession session = notificationOwner.BeginSession();
            Assert.ThrowsException<FormatException>(
                () => owner.LoadWalkureTable(new Uri("bmseeker:table.recommended?id=123&mode=readonly")));

            Assert.AreEqual(0, session.TakeReceipt().Notifications.Count);
        }
    }

    [TestMethod]
    public async Task LoadWalkureTable_EstimationLoadsJsonOnceForConcurrentRequests()
    {
        int getCount = 0;
        var startCalls = new ManualResetEventSlim();
        var allCallsIssued = new CountdownEvent(8);
        var firstRequestEntered = new ManualResetEventSlim();
        var releaseFirstRequest = new ManualResetEventSlim();
        var httpClient = new FakeHttpClient
        {
            GetStringHandler = _ =>
            {
                Interlocked.Increment(ref getCount);
                firstRequestEntered.Set();
                releaseFirstRequest.Wait();
                return "{}";
            }
        };
        PlaylistRecommendedTableOwner owner = CreateOwner(
            httpClient: httpClient,
            externalTableLoader: _ => CreateTable());
        Uri uri = new("bmseeker:table.estimation?type=easy");

        Task<BMSTable>[] loadTasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    startCalls.Wait();
                    allCallsIssued.Signal();
                    return owner.LoadWalkureTable(uri);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        try
        {
            startCalls.Set();
            Assert.IsTrue(firstRequestEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(allCallsIssued.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, Volatile.Read(ref getCount));

            releaseFirstRequest.Set();
            BMSTable[] tables = await Task.WhenAll(loadTasks).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, Volatile.Read(ref getCount));
            Assert.IsTrue(tables.All(table => table.entries.Count == 0));
        }
        finally
        {
            startCalls.Set();
            releaseFirstRequest.Set();
            bool allTasksCompleted = false;
            try
            {
                await Task.WhenAll(loadTasks).WaitAsync(TimeSpan.FromSeconds(5));
                allTasksCompleted = true;
            }
            catch
            {
                // Cleanup must observe every issued caller without replacing the test's primary failure.
                allTasksCompleted = loadTasks.All(task => task.IsCompleted);
            }
            if (allTasksCompleted)
            {
                startCalls.Dispose();
                allCallsIssued.Dispose();
                firstRequestEntered.Dispose();
                releaseFirstRequest.Dispose();
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadWalkureTable_RecommendedUpdatesClearedSongsAndNotifiesSkillChange()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistRecommendedTableOwnerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string scoreDbPath = Path.Combine(tempDirectory, "score.db");
        const string md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        bool previousShowMessage = Settings.Default.ShowRecommUpdatedMsg;
        Settings.Default.ShowRecommUpdatedMsg = true;
        try
        {
            using (var db = new LR2ScoreDBExtended(scoreDbPath))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.Insert(new LR2ScoreDB.player { id = "player", irid = 321, name = "Player" });
            }
            BMSTable insane = CreateTable(CreateEntry(md5, "1001", "Insane song"));
            BMSTable overjoy = CreateTable();
            var httpClient = new FakeHttpClient
            {
                GetStringHandler = _ => "{\"status\":\"success\",\"hoshi\":12.5,\"last_modified\":0,\"name\":\"Remote Name\",\"recommended\":[{\"bms\":{\"type\":\"normal\",\"bmsid\":1001},\"new_lamp\":\"clear\",\"p\":3.5}]}",
            };
            httpClient.PostFormHandler = (_, form) => string.Empty;
            var postForms = new List<NameValueCollection>();
            httpClient.PostFormObserver = form => postForms.Add(form);
            var notificationOwner = new PlaylistOperationNotificationOwner();
            PlaylistRecommendedTableOwner owner = CreateOwner(
                scoreDbPath,
                () =>
                [new BMSScore
                {
                    hash = md5,
                    clear = ClearType.HARD,
                    rank = RankType.A
                }],
                httpClient,
                uri => uri.AbsoluteUri.IndexOf("insane1", StringComparison.Ordinal) >= 0 ? insane : overjoy,
                notificationOwner: notificationOwner);
            var baseTable = new BMSTable { org_name = "Recommended ★11.00" };

            using PlaylistOperationNotificationOwner.OperationNotificationSession session = notificationOwner.BeginSession();
            BMSTable table = owner.LoadWalkureTable(
                new Uri("bmseeker:table.recommended?mode=normal&filter=clear&base=failed"),
                baseTable);

            Assert.AreEqual(1, postForms.Count);
            Assert.AreEqual("321", postForms[0].Get("id"));
            Assert.AreEqual("Player", postForms[0].Get("name"));
            StringAssert.Contains(postForms[0].Get("data"), "1001-4");
            Assert.AreEqual(1, table.entries.Count);
            Assert.AreEqual("CLEAR", table.entries.Single().folder);
            Assert.AreEqual(3.5, table.entries.Single().level);
            PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
            Assert.AreEqual(1, receipt.Notifications.Count);
            StringAssert.Contains(receipt.Notifications[0].Message, "12.50");
        }
        finally
        {
            Settings.Default.ShowRecommUpdatedMsg = previousShowMessage;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static PlaylistRecommendedTableOwner CreateOwner(
        string? lr2ScoreDbPath = null,
        Func<List<BMSScore>>? bmsScoresProvider = null,
        IPlaylistRecommendedTableHttpClient? httpClient = null,
        Func<Uri, BMSTable>? externalTableLoader = null,
        PlaylistOperationNotificationOwner? notificationOwner = null)
    {
        return new PlaylistRecommendedTableOwner(
            lr2ScoreDbPath: lr2ScoreDbPath,
            bmsScoresProvider: bmsScoresProvider ?? (() => null!),
            initializationSemaphoreProvider: () => null,
            externalTableLoader: externalTableLoader ?? (_ => null!),
            httpClient: httpClient ?? new FakeHttpClient(),
            notificationOwner: notificationOwner ?? new PlaylistOperationNotificationOwner(),
            playlistSettingsProvider: () => CustomFolderOutputSettingsSnapshot.CreateCurrent(Settings.Default));
    }

    private sealed class FakeHttpClient : IPlaylistRecommendedTableHttpClient
    {
        internal Func<Uri, string>? GetStringHandler { get; set; }

        internal Func<Uri, NameValueCollection, string>? PostFormHandler { get; set; }

        internal Action<NameValueCollection>? PostFormObserver { get; set; }

        internal List<Uri> GetUris { get; } = [];

        public string GetString(Uri uri)
        {
            GetUris.Add(uri);
            return GetStringHandler?.Invoke(uri) ?? throw new InvalidOperationException("Unexpected HTTP GET: " + uri);
        }

        public string PostForm(Uri uri, NameValueCollection formData)
        {
            NameValueCollection copy = new();
            foreach (string key in formData.AllKeys)
            {
                copy.Add(key, formData.Get(key));
            }
            PostFormObserver?.Invoke(copy);
            return PostFormHandler?.Invoke(uri, formData) ?? throw new InvalidOperationException("Unexpected HTTP POST: " + uri);
        }
    }

    private static BMSTable CreateTable(params BMSTableEntry[] entries)
    {
        return new BMSTable { entries = [.. entries] };
    }

    private static BMSTableEntry CreateEntry(string md5, string lr2BmsId, string title)
    {
        return new BMSTableEntry(JObject.Parse(
            "{\"md5\":\"" + md5 + "\",\"lr2_bmsid\":\"" + lr2BmsId + "\",\"title\":\"" + title + "\"}"));
    }
}
