using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using Ribbit.Net;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistRecommendedTableOwnerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LoadWalkureTable_RejectsNonBmseekerUri()
    {
        PlaylistRecommendedTableOwner owner = CreateOwner();

        ArgumentException exception = await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => owner.LoadWalkureTableAsync(new Uri("https://example.invalid/table.json")));

        StringAssert.StartsWith(exception.Message, Resources.Error_SchemeMustBeBemusic);
    }

    [TestMethod]
    public async Task LoadWalkureTable_RejectsUnsupportedRoute()
    {
        PlaylistRecommendedTableOwner owner = CreateOwner();

        ArgumentException exception = await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => owner.LoadWalkureTableAsync(new Uri("bmseeker:table.unsupported")));

        StringAssert.StartsWith(exception.Message, Resources.Error_UnsupportedURI);
    }

    [TestMethod]
    public async Task LoadWalkureTable_RecommendedWithoutScoreDatabaseFailsBeforeFetch()
    {
        PlaylistRecommendedTableOwner owner = CreateOwner();

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended?id=0")));

        Assert.AreEqual(Resources.Error_ScoreDBConnectionFailed, exception.Message);
    }

    [TestMethod]
    public async Task LoadWalkureTable_RecommendedBuildsEntriesAndPreservesBaseProperties()
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

        BMSTable table = await owner.LoadWalkureTableAsync(
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
    public async Task LoadWalkureTable_RecommendedSkipsInvalidRowsAndKeepsNumericContract()
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

        BMSTable table = await owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended?id=123&mode=readonly"));

        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual("CLEAR", table.entries.Single().folder);
        Assert.AreEqual(3.5, table.entries.Single().level);
    }

    [TestMethod]
    public async Task LoadWalkureTable_RecommendedSkipsRowsWithMissingRequiredFields()
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

        BMSTable table = await owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended?id=123&mode=readonly"));

        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual("CLEAR", table.entries.Single().folder);
        Assert.IsNull(table.entries.Single().level);
    }

    [TestMethod]
    public async Task LoadWalkureTable_EstimationSkipsRowsWithMissingRequiredFields()
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

        BMSTable table = await owner.LoadWalkureTableAsync(new Uri("bmseeker:table.estimation?type=easy"));

        Assert.AreEqual(1, table.entries.Count);
        Assert.AreEqual(3.5, table.entries.Single().level);
    }

    [TestMethod]
    public async Task LoadWalkureTable_RecommendedFetchFailureQueuesWarningAndThrows()
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
        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended?id=123&mode=readonly")));

        Assert.AreEqual(Resources.Error_RecommendFetchFailed, exception.Message);
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
        Assert.AreEqual(1, receipt.Notifications.Count);
        StringAssert.Contains(receipt.Notifications[0].Message, "offline");
        Assert.AreEqual(1, httpClient.GetUris.Count);
    }

    [TestMethod]
    public async Task LoadWalkureTable_RecommendedMissingDocumentFieldsFailsWithoutWarning()
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
            await Assert.ThrowsExceptionAsync<FormatException>(
                () => owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended?id=123&mode=readonly")));

            Assert.AreEqual(0, session.TakeReceipt().Notifications.Count);
        }
    }

    [TestMethod]
    public async Task LoadWalkureTable_EstimationLoadsJsonOnceForConcurrentRequests()
    {
        int getCount = 0;
        var firstRequestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var httpClient = new FakeHttpClient
        {
            GetStringAsyncHandler = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref getCount);
                firstRequestEntered.TrySetResult();
                await releaseFirstRequest.Task.WaitAsync(cancellationToken);
                return "{}";
            }
        };
        PlaylistRecommendedTableOwner owner = CreateOwner(
            httpClient: httpClient,
            externalTableLoader: _ => CreateTable());
        Uri uri = new("bmseeker:table.estimation?type=easy");
        Task<BMSTable>[] loadTasks = Enumerable.Range(0, 8)
            .Select(_ => owner.LoadWalkureTableAsync(uri))
            .ToArray();
        Exception? primaryFailure = null;
        try
        {
            await firstRequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, Volatile.Read(ref getCount));

            releaseFirstRequest.TrySetResult();
            BMSTable[] tables = await Task.WhenAll(loadTasks).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, Volatile.Read(ref getCount));
            Assert.IsTrue(tables.All(table => table.entries.Count == 0));
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            releaseFirstRequest.TrySetResult();
            await ObserveRequestForCleanupAsync(Task.WhenAll(loadTasks), primaryFailure);
        }
    }

    [TestMethod]
    public async Task LoadWalkureTable_RecommendedUpdatesClearedSongsAndNotifiesSkillChange()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "PlaylistRecommendedTableOwnerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string scoreDbPath = Path.Combine(tempDirectory, "score.db");
        const string md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
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
                notificationOwner: notificationOwner,
                settings: new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = true });
            var baseTable = new BMSTable { org_name = "Recommended ★11.00" };

            using PlaylistOperationNotificationOwner.OperationNotificationSession session = notificationOwner.BeginSession();
            BMSTable table = await owner.LoadWalkureTableAsync(
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
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [DataTestMethod]
    [DataRow("bmseeker:table.estimation?type=easy")]
    [DataRow("bmseeker:table.recommended?id=123&mode=readonly")]
    public async Task LoadExternalTableAsync_CancellationReachesWalkureGetAndAllowsNextLoad(string address)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeRequests = 0;
        var httpClient = new FakeHttpClient
        {
            GetStringAsyncHandler = async (_, token) =>
            {
                Interlocked.Increment(ref activeRequests);
                entered.TrySetResult();
                try { return await response.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref activeRequests); }
            }
        };
        PlaylistExternalSyncOwner externalOwner = CreateExternalOwner(CreateOwner(
            httpClient: httpClient,
            externalTableLoader: _ => CreateTable()));
        Task<BMSTable> request = externalOwner.LoadExternalTableAsync(new Uri(address), cancellationToken: cancellation.Token);
        Exception? primaryFailure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await AssertCanceledAsync(request.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, Volatile.Read(ref activeRequests));

            // 取消し済み取得を cache / single-flight gate に残さない。
            response.TrySetResult(address.Contains("table.estimation", StringComparison.Ordinal) ? "{}" : EmptyRecommendationJson);
            BMSTable next = await externalOwner.LoadExternalTableAsync(new Uri(address)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, next.entries.Count);
            Assert.AreEqual(2, httpClient.GetUris.Count);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            response.TrySetResult("{}");
            await ObserveRequestForCleanupAsync(request, primaryFailure);
        }
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task LoadExternalTableAsync_CancellationReachesReferenceTable(int blockedReference)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int referenceCalls = 0;
        PlaylistExternalSyncOwner externalOwner = CreateExternalOwner(CreateOwner(
            httpClient: new FakeHttpClient { GetStringHandler = _ => "{}" },
            externalTableLoaderAsync: async (_, token) =>
            {
                if (Interlocked.Increment(ref referenceCalls) == blockedReference)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return CreateTable();
            }));
        Task<BMSTable> request = externalOwner.LoadExternalTableAsync(
            new Uri("bmseeker:table.estimation?type=easy"), cancellationToken: cancellation.Token);
        Exception? primaryFailure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await AssertCanceledAsync(request.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(blockedReference, referenceCalls);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await ObserveRequestForCleanupAsync(request, primaryFailure);
        }
    }

    [TestMethod]
    public async Task LoadExternalTableAsync_CanceledEstimationWaiterDoesNotCancelLeader()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var httpClient = new FakeHttpClient
        {
            GetStringAsyncHandler = async (_, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return "{}";
            }
        };
        PlaylistExternalSyncOwner externalOwner = CreateExternalOwner(CreateOwner(
            httpClient: httpClient,
            externalTableLoader: _ => CreateTable()));
        Task<BMSTable> leader = externalOwner.LoadExternalTableAsync(new Uri("bmseeker:table.estimation?type=easy"));
        Task<BMSTable>? waiter = null;
        Exception? primaryFailure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waiter = externalOwner.LoadExternalTableAsync(new Uri("bmseeker:table.estimation?type=hard"), cancellationToken: cancellation.Token);
            cancellation.Cancel();
            await AssertCanceledAsync(waiter.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(leader.IsCompleted);
            Assert.AreEqual(1, httpClient.GetUris.Count);

            release.TrySetResult();
            await leader.WaitAsync(TimeSpan.FromSeconds(5));
            await externalOwner.LoadExternalTableAsync(new Uri("bmseeker:table.estimation?type=fc")).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, httpClient.GetUris.Count);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await ObserveRequestForCleanupAsync(waiter == null ? leader : Task.WhenAll(leader, waiter), primaryFailure);
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LoadExternalTableAsync_RecommendedPostDistinguishesCallerCancellationFromTimeout(bool cancelCaller)
    {
        string directory = Path.Combine(Path.GetTempPath(), "PlaylistRecommendedTableOwnerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BMSTable>? request = null;
        Exception? primaryFailure = null;
        try
        {
            string scoreDbPath = Path.Combine(directory, "score.db");
            using (var db = new LR2ScoreDBExtended(scoreDbPath))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.Insert(new LR2ScoreDB.player { id = "player", irid = 321, name = "Player" });
            }
            int postCount = 0;
            var httpClient = new FakeHttpClient
            {
                GetStringHandler = _ => EmptyRecommendationJson,
                PostFormAsyncHandler = async (_, _, token) =>
                {
                    Interlocked.Increment(ref postCount);
                    entered.TrySetResult();
                    if (!cancelCaller) throw new OperationCanceledException("HTTP request deadline expired.");
                    return await release.Task.WaitAsync(token);
                }
            };
            var notifications = new PlaylistOperationNotificationOwner();
            PlaylistExternalSyncOwner externalOwner = CreateExternalOwner(CreateOwner(
                lr2ScoreDbPath: scoreDbPath,
                bmsScoresProvider: () => [],
                httpClient: httpClient,
                externalTableLoader: _ => CreateTable(),
                notificationOwner: notifications));
            using PlaylistOperationNotificationOwner.OperationNotificationSession session = notifications.BeginSession();
            request = externalOwner.LoadExternalTableAsync(
                new Uri("bmseeker:table.recommended?mode=normal"), cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelCaller)
            {
                cancellation.Cancel();
                await AssertCanceledAsync(request.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(1, postCount);
                Assert.AreEqual(0, httpClient.GetUris.Count);
                Assert.IsTrue(session.TakeReceipt().IsEmpty);
            }
            else
            {
                BMSTable table = await request.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(0, table.entries.Count);
                Assert.AreEqual(6, postCount); // 既存契約の初回 + 最大5回を増減させない。
                Assert.AreEqual(1, httpClient.GetUris.Count);
                PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
                Assert.AreEqual(1, receipt.Notifications.Count);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, receipt.Notifications[0].Severity);
            }
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult(string.Empty);
            if (request != null) await ObserveRequestForCleanupAsync(request, primaryFailure);
            Directory.Delete(directory, recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReloadPlaylistTargetsAsync_WalkureReadFailurePreservesDataAndExitsUpdating(bool cancelCaller)
    {
        string directory = Path.Combine(Path.GetTempPath(), "PlaylistRecommendedTableOwnerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult>>? request = null;
        Exception? primaryFailure = null;
        try
        {
            string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var repository = new PlaylistPersistenceRepository(songDbPath);
            var aggregate = new PlaylistAggregatePersistenceOwner(
                repository,
                new ReaderWriterLockSlimWrapper(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            BMSTable table = CreateTable(CreateEntry(md5, "1001", "Original"));
            table.name = "Original table";
            table.Page_url = new Uri("bmseeker:table.estimation?type=easy");
            aggregate.MarkActiveTables([table]);
            aggregate.ReplaceTablesWithEntries([table]);
            int updating = 0;
            int exitCount = 0;
            var attempts = new List<PlaylistSyncAttemptResult>();
            var httpClient = new FakeHttpClient
            {
                GetStringAsyncHandler = async (_, token) =>
                {
                    entered.TrySetResult();
                    return await response.Task.WaitAsync(token);
                }
            };
            var externalOwner = new PlaylistExternalSyncOwner(
                AppHttpClient.Shared,
                CreateOwner(httpClient: httpClient),
                (_, _) => { },
                () => false,
                _ => { },
                playlistAggregatePersistenceOwner: aggregate,
                isActiveTable: aggregate.IsActive,
                enterPlaylistUpdating: () => Interlocked.Increment(ref updating),
                exitPlaylistUpdating: () => { Interlocked.Decrement(ref updating); Interlocked.Increment(ref exitCount); });
            request = externalOwner.ReloadPlaylistTargetsAsync([table], attempts.Add, cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, updating);
            if (cancelCaller) cancellation.Cancel();
            else response.TrySetException(new OperationCanceledException("HTTP request deadline expired."));
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = await request.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(0, updating);
            Assert.AreEqual(1, exitCount);
            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Succeeded);
            Assert.IsFalse(results[0].StatePersisted);
            Assert.IsTrue(results[0].Exception is OperationCanceledException);
            Assert.IsNull(results[0].UpdateReceipt);
            Assert.AreSame(table, results[0].ResultTable);
            Assert.AreEqual(1, attempts.Count);
            Assert.IsFalse(attempts[0].Succeeded);
            Assert.AreEqual(md5, table.entries.Single().md5);
            Assert.AreEqual(md5, repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: true).Single().md5);
            Assert.AreEqual("Original table", repository.LoadPlaylistHeaders().Single().name);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            cancellation.Cancel();
            response.TrySetResult("{}");
            if (request != null) await ObserveRequestForCleanupAsync(request, primaryFailure);
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string EmptyRecommendationJson = "{\"status\":\"success\",\"hoshi\":1.0,\"last_modified\":0,\"name\":\"Player\",\"recommended\":[]}";

    private static PlaylistExternalSyncOwner CreateExternalOwner(PlaylistRecommendedTableOwner recommendedOwner)
    {
        return new PlaylistExternalSyncOwner(AppHttpClient.Shared, recommendedOwner, (_, _) => { }, () => false, _ => { });
    }

    private static async Task AssertCanceledAsync(Task request)
    {
        try { await request; }
        catch (OperationCanceledException) { return; }
        Assert.Fail("Cancellation must terminate the acquisition without returning a table.");
    }

    private async Task ObserveRequestForCleanupAsync(Task request, Exception? primaryFailure)
    {
        try
        {
            await request.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // The fixture cancels owned acquisitions before draining them.
        }
        catch (Exception cleanupFailure) when (primaryFailure != null)
        {
            TestContext.WriteLine("Owned request cleanup: " + cleanupFailure);
            if (!request.IsCompleted)
            {
                // Keep the primary failure, but do not remove a DB directory while
                // a still-running acquisition may own it. No global SQLite close/retry.
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }
        }
    }

    private static PlaylistRecommendedTableOwner CreateOwner(
        string? lr2ScoreDbPath = null,
        Func<List<BMSScore>>? bmsScoresProvider = null,
        IPlaylistRecommendedTableHttpClient? httpClient = null,
        Func<Uri, BMSTable>? externalTableLoader = null,
        PlaylistOperationNotificationOwner? notificationOwner = null,
        Func<Uri, CancellationToken, Task<BMSTable>>? externalTableLoaderAsync = null,
        CustomFolderOutputSettingsSnapshot? settings = null)
    {
        // Every fixture owns its options. Parallel classes can change Settings.Default
        // without affecting an in-flight asynchronous acquisition in this fixture.
        settings ??= new CustomFolderOutputSettingsSnapshot();
        return new PlaylistRecommendedTableOwner(
            lr2ScoreDbPath: lr2ScoreDbPath,
            bmsScoresProvider: bmsScoresProvider ?? (() => null!),
            externalTableLoader: externalTableLoaderAsync ?? ((uri, _) => Task.FromResult(externalTableLoader?.Invoke(uri)!)),
            httpClient: httpClient ?? new FakeHttpClient(),
            notificationOwner: notificationOwner ?? new PlaylistOperationNotificationOwner(),
            playlistSettingsProvider: () => settings);
    }

    private sealed class FakeHttpClient : IPlaylistRecommendedTableHttpClient
    {
        internal Func<Uri, string>? GetStringHandler { get; set; }

        internal Func<Uri, CancellationToken, Task<string>>? GetStringAsyncHandler { get; set; }

        internal Func<Uri, NameValueCollection, string>? PostFormHandler { get; set; }

        internal Func<Uri, NameValueCollection, CancellationToken, Task<string>>? PostFormAsyncHandler { get; set; }

        internal Action<NameValueCollection>? PostFormObserver { get; set; }

        internal List<Uri> GetUris { get; } = [];

        public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetUris.Add(uri);
            return GetStringAsyncHandler?.Invoke(uri, cancellationToken)
                ?? Task.FromResult(GetStringHandler?.Invoke(uri) ?? throw new InvalidOperationException("Unexpected HTTP GET: " + uri));
        }

        public Task<string> PostFormAsync(Uri uri, NameValueCollection formData, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NameValueCollection copy = new();
            foreach (string? key in formData.AllKeys)
            {
                copy.Add(key, formData.Get(key));
            }
            PostFormObserver?.Invoke(copy);
            return PostFormAsyncHandler?.Invoke(uri, formData, cancellationToken)
                ?? Task.FromResult(PostFormHandler?.Invoke(uri, formData) ?? throw new InvalidOperationException("Unexpected HTTP POST: " + uri));
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
