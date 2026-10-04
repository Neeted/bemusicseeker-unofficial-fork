using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistRecommendedTableOwnerTests
{
    [TestMethod]
    public async Task LocalTables_CompleteWithUnavailableNetworkWithoutSendingRequests()
    {
        using var handler = new UnavailableNetworkHandler();
        using var client = new HttpClient(handler);
        var external = new PlaylistExternalSyncOwner(new AppHttpClient(client, TimeProvider.System), CreateOwner(),
            (_, _) => { }, () => false, _ => { });
        foreach (string query in new[] { "table.estimation?type=easy", "table.estimation?type=normal", "table.estimation?type=hard",
            "table.estimation?type=fc", "table.recommended", "table.recommended?base=failed", "table.recommended?failed=noplay" })
        {
            BMSTable table = await external.LoadExternalTableAsync(new Uri("bmseeker:" + query));
            Assert.IsNotNull(table.entries);
        }
        Assert.AreEqual(0, handler.Requests);
    }

    private sealed class UnavailableNetworkHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("network is unavailable"));
        }
    }

    [DataTestMethod]
    [DataRow("easy", .73)]
    [DataRow("normal", 2.56)]
    [DataRow("hard", 4.34)]
    [DataRow("fc", 23.40)]
    public async Task LoadWalkureTable_EstimationUsesStoredStarsWithoutScores(string type, double expected)
    {
        int reads = 0;
        PlaylistRecommendedTableOwner owner = CreateOwner(_ => { reads++; throw new InvalidOperationException(); });
        BMSTable table = await owner.LoadWalkureTableAsync(new Uri("bmseeker:table.estimation?type=" + type + "&name=ignored&unknown=x"));
        Assert.AreEqual(0, reads);
        Assert.AreEqual(expected, table.entries.Single(row => row.md5 == "621ad2a006a4d39a57749c5f8dcc11b6").level);
        Assert.AreEqual(1252, table.entries.Select(row => row.md5).Distinct().Count());
        BMSTableEntry[] dual = table.entries.Where(row => row.md5 == "a4a9c721a726435eaf37b62b1768b4e1").ToArray();
        Assert.AreEqual(2, dual.Length);
        Assert.IsTrue(dual.Any(row => row.folder.StartsWith("INSANE", StringComparison.Ordinal)));
        Assert.IsTrue(dual.Any(row => row.folder.StartsWith("Overjoy", StringComparison.Ordinal)));
        Assert.IsTrue(table.entries.All(row => row.md5.Length == 32));
        Assert.IsTrue(table.entries.All(row => string.IsNullOrEmpty(row.artist) && string.IsNullOrEmpty(row.url)));
        if (type == "fc")
        {
            Assert.IsTrue(table.entries.Where(row => row.md5 == "ac29456828fbd27bb27bd99d52a666c4").All(row => row.level == null));
        }
        BMSTable renamed = await owner.LoadWalkureTableAsync(new Uri("bmseeker:table.estimation?type=" + type),
            new BMSTable { name = "手動の推定表名", org_name = "元の推定表名" });
        Assert.AreEqual("手動の推定表名", renamed.name);
        Assert.AreEqual(table.org_name, renamed.org_name);
        Assert.AreEqual(0, reads);
    }

    [DataTestMethod]
    [DataRow("https://example.invalid/table.json")]
    [DataRow("bmseeker:table.unsupported")]
    [DataRow("bmseeker:table.estimation?type=unsupported")]
    [DataRow("bmseeker:table.recommended?base=failed&failed=noplay")]
    public async Task LoadWalkureTable_RejectsUnsupportedInputBeforeReading(string uri)
    {
        int reads = 0;
        PlaylistRecommendedTableOwner owner = CreateOwner(_ => { reads++; throw new InvalidOperationException(); });
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => owner.LoadWalkureTableAsync(new Uri(uri)));
        Assert.AreEqual(0, reads);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(1)]
    public async Task LoadWalkureTable_MissingFailedOrEmptyScoresFail(int status)
    {
        PlaylistRecommendedTableOwner owner = CreateOwner(_ => Task.FromResult(new WalkureScoreInput((ScoreTableLoadStatus)status, ImmutableDictionary<string, WalkureLamp>.Empty)));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended")));
    }

    [TestMethod]
    public async Task LoadWalkureTable_RecommendedPreservesBasePropertiesAndIgnoresLegacyParameters()
    {
        PlaylistRecommendedTableOwner owner = CreateOwner();
        var original = new BMSTable
        {
            name = "手動名 ★999.00",
            org_name = "旧取得元名 ★0.00",
            compat_prefix = "BASE ",
            playlist_id = 17,
            symbol = "BASE",
            Output_dir = "original-output",
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
            is_external_sync = false,
            custom_folder_output_base_name = "base-output",
            bmt_sort = 4,
            is_bmt_output = true
        };
        BMSTable baseline = await owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended"), original);
        Assert.AreEqual(baseline.org_name, baseline.name);
        StringAssert.Contains(baseline.name, "★" + (ReadWalkureCase("mixed")["rating"]?.Value<double>("playerStarRating")
            ?? throw new FormatException()).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        foreach (string query in new[] { "mode=readonly&id=999&name=other", "mode=update&filter=hard", "mode=unknown&unknown=x" })
        {
            BMSTable result = await owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended?" + query), original);
            Assert.AreEqual(baseline.data_sha256, result.data_sha256);
            Assert.AreEqual(baseline.org_name, result.org_name);
            Assert.AreEqual(result.org_name, result.name);
            Assert.AreEqual(original.compat_prefix, result.compat_prefix);
            Assert.AreEqual(original.playlist_id, result.playlist_id);
            Assert.AreEqual(original.symbol, result.symbol);
            Assert.AreEqual(original.ignore_folder_output, result.ignore_folder_output);
            Assert.AreEqual(original.is_external_sync, result.is_external_sync);
            Assert.AreEqual(original.Output_dir, result.Output_dir);
            Assert.AreEqual(original.custom_folder_output_base_name, result.custom_folder_output_base_name);
            Assert.AreEqual(original.bmt_sort, result.bmt_sort);
            Assert.AreEqual(original.is_bmt_output, result.is_bmt_output);
        }
        Assert.AreEqual("R★", baseline.org_symbol);
        CollectionAssert.AreEqual(new[] { "EASY", "NORMAL", "HARD", "FC" }, baseline.Folder_order);
    }

    [TestMethod]
    public async Task LoadExternalTableSnapshotsAsync_CapturesOnceAcrossPoliciesAndRefreshesNextOperation()
    {
        int reads = 0;
        WalkureScoreInput input = ReadWalkureInput("standard");
        PlaylistRecommendedTableOwner owner = CreateOwner(_ => { reads++; return Task.FromResult(input); });
        PlaylistExternalSyncOwner external = External(owner);
        BMSTable[] targets = [.. new[] { "", "?base=failed", "?failed=noplay" }.Select(query => new BMSTable
        { Page_url = new Uri("bmseeker:table.recommended" + query), name = "手動名", org_name = "旧取得元名" })];
        List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> results = await external.LoadExternalTableSnapshotsAsync(targets, true);
        Assert.AreEqual(1, reads);
        foreach ((PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult? result, string? expectedCase) in results.Zip(new[] { "standard", "baseFailed", "omitFailed" }))
        {
            Assert.IsTrue(result.Succeeded);
            JObject independent = ReadWalkureCase(expectedCase);
            string star = (independent["rating"]?.Value<double>("playerStarRating") ?? throw new FormatException()).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            StringAssert.Contains(result.ExternalTable.org_name, "★" + star);
            Assert.AreEqual(result.ExternalTable.org_name, result.ExternalTable.name);
        }
        Assert.AreEqual(3, results.Select(row => row.ExternalTable.name).Distinct().Count());
        input = ReadWalkureInput("mixed");
        BMSTable next = await external.LoadExternalTableAsync(targets[0].Page_url);
        Assert.AreEqual(2, reads);
        Assert.AreNotEqual(results[0].ExternalTable.data_sha256, next.data_sha256);
        await external.LoadExternalTableAsync(new Uri("bmseeker:table.estimation?type=easy"));
        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    public async Task LoadExternalTableSnapshotsAsync_SharesReadFailureWithoutCachingAcrossOperations()
    {
        int reads = 0;
        bool fail = true;
        PlaylistRecommendedTableOwner owner = CreateOwner(_ => { reads++; return fail ? Task.FromException<WalkureScoreInput>(new IOException("read failed")) : Task.FromResult(ReadWalkureInput()); });
        PlaylistExternalSyncOwner external = External(owner);
        BMSTable[] targets = [.. new[] { "", "?base=failed", "?failed=noplay" }.Select(query => new BMSTable { Page_url = new Uri("bmseeker:table.recommended" + query) })];
        List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> results = await external.LoadExternalTableSnapshotsAsync(targets, false);
        Assert.AreEqual(1, reads);
        Assert.IsTrue(results.All(result => !result.Succeeded && result.Exception is IOException));
        fail = false;
        await external.LoadExternalTableAsync(targets[0].Page_url);
        Assert.AreEqual(2, reads);
    }

    [DataTestMethod]
    [DataRow(ClearType.NO_PLAY, 0)]
    [DataRow(ClearType.FAILED, 1)]
    [DataRow(ClearType.INVALID, 1)]
    [DataRow(ClearType.L_ASSIST, 1)]
    [DataRow(ClearType.EASY, 2)]
    [DataRow(ClearType.CLEAR, 3)]
    [DataRow(ClearType.HARD, 4)]
    [DataRow(ClearType.EX_HARD, 4)]
    [DataRow(ClearType.FC, 5)]
    [DataRow(ClearType.PA, 5)]
    [DataRow(ClearType.MAX, 5)]
    public void RecommendationScores_NormalizeExistingClearSemantics(ClearType clear, int expected)
        => Assert.AreEqual(expected == 0 ? (WalkureLamp?)null : (WalkureLamp)expected, BMSLibrary.NormalizeRecommendationLamp(clear));

    [TestMethod]
    public async Task RecommendationScores_ReadFreshLr2IncludingCourseAndRankZeroWithoutUpdatingGlobalScores()
    {
        string directory = NewDirectory();
        BMSLibrary? library = null;
        try
        {
            string songPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            string scorePath = Path.Combine(directory, "score.db");
            WalkureRecommendationModel model = WalkureRecommendationModel.Bundled;
            string course = model.Entries.First(row => row.IsCourse).Md5;
            using (var db = new SQLiteConnection(scorePath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<BMSScore>(); db.CreateTable<LR2ScoreDB.player>();
                    db.Insert(new LR2ScoreDB.player { id = "player", irid = 0 });
                    db.Insert(new BMSScore { hash = model.Entries[0].Md5, clear = ClearType.EASY, op_history = ClearTypeStorageConverter.OptionHistoryEasy });
                    db.Insert(new BMSScore { hash = model.Entries[1].Md5, clear = ClearType.EASY, op_history = 0 });
                    db.Insert(new BMSScore { hash = course, clear = ClearType.HARD });
                });
            }

            library = NewLibrary(songPath, scorePath, () => new BmsLibraryOptionsSnapshot());
            var binding = new BmsPlaylistLibraryBindings(library);
            WalkureScoreInput first = await binding.ReadRecommendationScoresAsync(CancellationToken.None);
            Assert.AreEqual(ScoreTableLoadStatus.Loaded, first.Status);
            Assert.AreEqual(WalkureLamp.Easy, first.Scores[model.Entries[0].Md5]);
            Assert.AreEqual(WalkureLamp.Failed, first.Scores[model.Entries[1].Md5]);
            Assert.AreEqual(WalkureLamp.Hard, first.Scores[course]);
            Assert.AreEqual(0, library.GetBMSScores().Count);
            using (var db = new SQLiteConnection(scorePath))
            {
                db.Execute("UPDATE score SET clear=3 WHERE hash=?", model.Entries[0].Md5);
            }

            WalkureScoreInput next = await binding.ReadRecommendationScoresAsync(CancellationToken.None);
            Assert.AreEqual(WalkureLamp.Normal, next.Scores[model.Entries[0].Md5]);
            Assert.AreEqual(WalkureLamp.Easy, first.Scores[model.Entries[0].Md5]);
            BMSTable table = await CreateOwner(binding.ReadRecommendationScoresAsync).LoadWalkureTableAsync(new Uri("bmseeker:table.recommended"));
            Assert.IsTrue(table.entries.All(row => row.md5.Length == 32));
            Assert.AreEqual(0, library.GetBMSScores().Count);
        }
        finally { library?.RequestShutdown("test_cleanup"); Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RecommendationScores_SelectsBeatorajaModeZeroAndExistingHashInformation()
    {
        string directory = NewDirectory();
        BMSLibrary? library = null;
        try
        {
            string songPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            string scorePath = Path.Combine(directory, "score.db");
            ImmutableArray<WalkureModelEntry> entries = WalkureRecommendationModel.Bundled.Entries;
            using (var db = new LR2SongDBExtended(songPath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2SongDBExtended.chart_info>();
                    db.Insert(new LR2SongDBExtended.chart_info { md5 = entries[0].Md5, sha256 = new string('a', 64) });
                    db.Insert(new LR2SongDBExtended.chart_info { md5 = entries[1].Md5, sha256 = new string('b', 64) });
                });
            }

            using (var db = new SQLiteConnection(scorePath))
            {
                db.RunInTransaction(() =>
                {
                    db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
                    db.Execute("INSERT INTO score VALUES (?,0,5,0,0,0,0,0,0,0,1,1)", new string('a', 64));
                    db.Execute("INSERT INTO score VALUES (?,0,2,0,0,0,0,0,0,0,1,0)", new string('b', 64));
                    db.Execute("INSERT INTO score VALUES (?,10000,8,0,0,0,0,0,0,0,1,1)", new string('b', 64));
                    db.Execute("INSERT INTO score VALUES (?,0,8,0,0,0,0,0,0,0,1,1)", new string('c', 64));
                });
            }

            BmsLibraryOptionsSnapshot options = new() { UseBeatorajaScoreDb = true, BeatorajaScoreDbPath = scorePath };
            library = NewLibrary(songPath, null, () => options);
            WalkureScoreInput input = await library.ReadRecommendationScoresAsync(CancellationToken.None);
            Assert.AreEqual(ScoreTableLoadStatus.Loaded, input.Status, input.FailureMessage);
            Assert.AreEqual(2, input.Scores.Count);
            Assert.AreEqual(WalkureLamp.Normal, input.Scores[entries[0].Md5]);
            Assert.AreEqual(WalkureLamp.Failed, input.Scores[entries[1].Md5]);
            Assert.AreEqual(0, library.GetBMSScores().Count);
            PlaylistRecommendedTableOwner owner = CreateOwner(library.ReadRecommendationScoresAsync);
            await owner.LoadWalkureTableAsync(new Uri("bmseeker:table.recommended"));
            using (var db = new SQLiteConnection(scorePath))
            {
                db.Execute("UPDATE score SET clear=6 WHERE sha256=? AND mode=0", new string('a', 64));
            }

            WalkureScoreInput next = await library.ReadRecommendationScoresAsync(CancellationToken.None);
            Assert.AreEqual(WalkureLamp.Hard, next.Scores[entries[0].Md5]);
            string secondPlayerDirectory = Path.Combine(directory, "second-player");
            Directory.CreateDirectory(secondPlayerDirectory);
            string secondPlayerScore = Path.Combine(secondPlayerDirectory, "score.db");
            File.Copy(scorePath, secondPlayerScore);
            using (var db = new SQLiteConnection(secondPlayerScore))
            {
                db.RunInTransaction(() => db.Execute("UPDATE score SET clear=8 WHERE sha256=? AND mode=0", new string('a', 64)));
            }
            options = new BmsLibraryOptionsSnapshot { UseBeatorajaScoreDb = true, BeatorajaScoreDbPath = secondPlayerScore };
            Assert.AreEqual(WalkureLamp.FullCombo, (await library.ReadRecommendationScoresAsync(CancellationToken.None)).Scores[entries[0].Md5]);
            options = new BmsLibraryOptionsSnapshot { UseBeatorajaScoreDb = true, BeatorajaScoreDbPath = scorePath };
            Assert.AreEqual(WalkureLamp.Hard, (await library.ReadRecommendationScoresAsync(CancellationToken.None)).Scores[entries[0].Md5]);
            options = new BmsLibraryOptionsSnapshot();
            Assert.AreEqual(ScoreTableLoadStatus.NotConfigured, (await library.ReadRecommendationScoresAsync(CancellationToken.None)).Status);
        }
        finally { library?.RequestShutdown("test_cleanup"); Directory.Delete(directory, true); }
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReloadPlaylistTargetsAsync_LocalReadFailurePreservesDataAndExitsUpdating(bool cancelCaller)
    {
        string directory = NewDirectory();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WalkureScoreInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult>>? request = null;
        Exception? primaryFailure = null;
        try
        {
            string songPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
            PlaylistPersistenceRepository.EnsureSchema(songPath);
            var repository = new PlaylistPersistenceRepository(songPath);
            var aggregate = new PlaylistAggregatePersistenceOwner(repository, new ReaderWriterLockSlimWrapper(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            var table = new BMSTable { name = "Original", org_name = "旧方針 ★3.87", Page_url = new Uri("bmseeker:table.recommended"), entries = [new BMSTableEntry { md5 = new string('a', 32), title = "Original song", memo = "keep" }] };
            aggregate.MarkActiveTables([table]); aggregate.ReplaceTablesWithEntries([table]);
            int updating = 0, reads = 0;
            var notifications = new PlaylistOperationNotificationOwner();
            PlaylistRecommendedTableOwner owner = CreateOwner(async token => { reads++; if (reads > 1) { return ReadWalkureInput("standard"); } entered.TrySetResult(); return await release.Task.WaitAsync(token); }, notifications, showSkillUpdates: true);
            var external = new PlaylistExternalSyncOwner(AppHttpClient.Shared, owner, (_, _) => { }, () => false, _ => { },
                playlistAggregatePersistenceOwner: aggregate, isActiveTable: aggregate.IsActive,
                enterPlaylistUpdating: () => updating++, exitPlaylistUpdating: () => updating--);
            using PlaylistOperationNotificationOwner.OperationNotificationSession session = notifications.BeginSession();
            request = external.ReloadPlaylistTargetsAsync([table], cancellationToken: cancellation.Token);
            await ReachOrComplete(entered.Task, request);
            Assert.AreEqual(1, updating);
            if (cancelCaller)
            {
                cancellation.Cancel();
            }
            else
            {
                release.TrySetException(new IOException("local read failure"));
            }

            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = (await request).Single();
            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(result.StatePersisted);
            Assert.IsNull(result.UpdateReceipt);
            Assert.AreSame(table, result.ResultTable);
            Assert.AreEqual(0, updating);
            Assert.AreEqual("Original", repository.LoadPlaylistHeaders().Single().name);
            Assert.AreEqual("keep", repository.LoadPersistedPlaylistEntries(table.playlist_id, true).Single().memo);
            Assert.IsTrue(session.TakeReceipt().IsEmpty);
            await external.LoadExternalTableAsync(table.Page_url);
            Assert.AreEqual(2, reads);
        }
        catch (Exception ex) { primaryFailure = ex; throw; }
        finally
        {
            cancellation.Cancel(); release.TrySetResult(ReadWalkureInput("standard"));
            if (request != null)
            {
                try { await request; } catch when (primaryFailure != null) { }
            }

            Directory.Delete(directory, true);
        }
    }

    [DataTestMethod]
    [DataRow("旧方針 ★10.00", "新方針 ★10.75", 10.75, .75, true, "bmseeker:table.recommended", 1)]
    [DataRow("旧方針 ★10.75", "新方針 ★10.00", 10.00, -.75, true, "bmseeker:table.recommended", 1)]
    [DataRow("旧方針 ★10.0", "新方針 ★10.00", 10.00, 0, true, "bmseeker:table.recommended", 0)]
    [DataRow("手動名だけ", "新方針 ★10.75", 10.75, .75, true, "bmseeker:table.recommended", 0)]
    [DataRow("旧方針 ★10.00", "新方針 ★10.75", 10.75, .75, false, "bmseeker:table.recommended", 0)]
    [DataRow("旧方針 ★10.00", "新方針 ★10.75", 10.75, .75, true, "bmseeker:table.estimation?type=easy", 0)]
    public void AppliedSkillNotification_UsesOrgNameNumbersAndSettingWithoutDate(
        string previousName, string currentName, double newSkill, double difference, bool enabled, string uri, int expectedCount)
    {
        var notifications = new PlaylistOperationNotificationOwner();
        PlaylistRecommendedTableOwner owner = CreateOwner(notifications: notifications, showSkillUpdates: enabled);
        using PlaylistOperationNotificationOwner.OperationNotificationSession session = notifications.BeginSession();
        owner.NotifyAppliedSkillChange(previousName, new BMSTable
        {
            Page_url = new Uri(uri),
            org_name = currentName,
            name = "異なる手動名 ★999.00",
            last_update = new DateTime(2024, 6, 1, 10, 20, 30)
        });
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt = session.TakeReceipt();
        Assert.AreEqual(expectedCount, receipt.Notifications.Count);
        if (expectedCount != 0)
        {
            PlaylistOperationNotificationOwner.OperationNotification notification = receipt.Notifications.Single();
            Assert.AreEqual(Resources.Recommend_SkillUpdatedTitle, notification.Caption);
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information, notification.Severity);
            Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage, newSkill.ToString("F2"),
                difference.ToString(" (+#0.00); (-#0.00);")), notification.Message);
        }
    }

    private static PlaylistExternalSyncOwner External(PlaylistRecommendedTableOwner owner)
        => new(AppHttpClient.Shared, owner, (_, _) => { }, () => false, _ => { });
    private static PlaylistRecommendedTableOwner CreateOwner(Func<CancellationToken, Task<WalkureScoreInput>>? read = null,
        PlaylistOperationNotificationOwner? notifications = null, bool showSkillUpdates = false)
        => new(read ?? (_ => Task.FromResult(ReadWalkureInput())), notifications ?? new PlaylistOperationNotificationOwner(),
            () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = showSkillUpdates });
    private static BMSLibrary NewLibrary(string songPath, string? scorePath, Func<BmsLibraryOptionsSnapshot> options)
        => new(songPath, null, scorePath, null, options, new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), TestBmsFactory.MissingEverythingBridge);
    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistRecommendedTableOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static async Task ReachOrComplete(Task reached, Task operation)
    {
        Task first = await Task.WhenAny(reached, operation);
        await first;
        Assert.IsTrue(reached.IsCompletedSuccessfully, "到達前に本体が終結しました。");
    }
}
