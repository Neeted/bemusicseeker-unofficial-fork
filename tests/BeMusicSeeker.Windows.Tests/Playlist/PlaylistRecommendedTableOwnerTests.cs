using System.Globalization;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using Ribbit.Net;
using static BeMusicSeeker.TestSupport.Components.BmsPlaylistTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistRecommendedTableTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>内蔵難度表・推薦表の生成と、選択スコアの読取り・保存・通知接続を検証する。</summary>
/// <remarks>数学の独立期待値はWalkureRecommendationMathTestsが担う。ここではURI方針と実DB・共通登録・再読込みを通し、生成結果の所属や保存・取消時の状態を観測する。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class PlaylistRecommendedTableOwnerTests
{
    private TestWorkspace? ownedCaseWorkspace;

    /// <summary>返却されたfixtureの終端まで、ケースが専有媒体の親領域を保持します。</summary>
    private TestWorkspace CaseWorkspace => ownedCaseWorkspace ?? throw new InvalidOperationException("ケースの媒体は初期化後に使用します。");

    /// <summary>ケース専有領域を開始し、既存の相対pathの存在条件を維持します。</summary>
    [TestInitialize]
    public void InitializeCaseWorkspace() => ownedCaseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース本文のfinallyとfixtureの終端後に外側の媒体を回収します。</summary>
    [TestCleanup]
    public void CleanupCaseWorkspace() => ownedCaseWorkspace?.Dispose();

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>読取り例外は対象失敗、利用者取消は操作失敗に分け、旧DB・メモ・正本参照と通知なしを維持して更新状態を解除する。部分反映と失敗・取消の隠蔽を検出する。</summary>
    [TestMethod]
    // 読取りgate中の利用者取消と通信に依存しないローカルIOExceptionを分ける。
    [DataRow(true)]
    [DataRow(false)]
    public async Task ReloadPlaylistTargetsAsync_LocalReadFailurePreservesDataAndExitsUpdating(bool cancelCaller)
    {
        string directory = NewDirectory();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<WalkureScoreInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PlaylistExternalSyncOwner.PlaylistReloadOperationResult>? request = null;
        Exception? primaryFailure = null;
        try
        {
            string songPath = PlaylistTestFixture.CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            var repository = new PlaylistPersistenceRepository(databaseScope, songPath);
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

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult operationResult = await request;
            if (cancelCaller)
            {
                Assert.IsNotNull(operationResult.OperationException);
                Assert.IsTrue(cancellation.IsCancellationRequested);
                Assert.IsTrue(new AggregateException(operationResult.OperationException).Flatten().InnerExceptions
                    .Any(failure => failure is OperationCanceledException));
                Assert.IsTrue(operationResult.All(result => !result.StatePersisted && result.UpdateReceipt == null
                    && ReferenceEquals(table, result.ResultTable)));
            }
            else
            {
                Assert.IsNull(operationResult.OperationException);
                PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = operationResult.Single();
                Assert.IsInstanceOfType<IOException>(result.Exception);
                Assert.IsFalse(result.Succeeded);
                Assert.IsFalse(result.StatePersisted);
                Assert.IsNull(result.UpdateReceipt);
                Assert.AreSame(table, result.ResultTable);
            }
            Assert.IsTrue(aggregate.IsActive(table));
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

    /// <summary>fr-FRでも取得元名の★数値と差分を固定表記で表示し、増減して通知設定ONの推薦だけ一回通知する。同値表記・比較不能・設定OFF・推定表は通知せず、手動名や更新日時を通知判断へ混ぜる誤りを検出する。</summary>
    [TestMethod]
    [DoNotParallelize]
    // 増加・減少は通知し、同値表記・比較不能・OFF・推定表では通知しない。
    [DataRow("旧方針 ★10.00", "新方針 ★10.75", 10.75, .75, true, "bmseeker:table.recommended", 1)]
    [DataRow("旧方針 ★10.75", "新方針 ★10.00", 10.00, -.75, true, "bmseeker:table.recommended", 1)]
    [DataRow("旧方針 ★10.0", "新方針 ★10.00", 10.00, 0, true, "bmseeker:table.recommended", 0)]
    [DataRow("手動名だけ", "新方針 ★10.75", 10.75, .75, true, "bmseeker:table.recommended", 0)]
    [DataRow("旧方針 ★10.00", "新方針 ★10.75", 10.75, .75, false, "bmseeker:table.recommended", 0)]
    [DataRow("旧方針 ★10.00", "新方針 ★10.75", 10.75, .75, true, "bmseeker:table.estimation?type=easy", 0)]
    public void AppliedSkillNotification_UsesOrgNameNumbersAndSettingWithoutDate(
        string previousName, string currentName, double newSkill, double difference, bool enabled, string uri, int expectedCount)
    {
        CultureInfo previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        CultureInfo? previousResourcesCulture = Resources.Culture;
        try
        {
            // 通知はja-JPで積み、初表示相当の整形をfr-FRで行って、本文・見出しが整形時の言語になることを確かめる。
            ResourceService.Current.ChangeCulture("ja-JP");
            string japaneseTitle = Resources.Recommend_SkillUpdatedTitle;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
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
            ResourceService.Current.ChangeCulture("fr-FR");
            if (expectedCount != 0)
            {
                Assert.AreNotEqual(japaneseTitle, Resources.Recommend_SkillUpdatedTitle);
                PlaylistOperationNotificationOwner.OperationNotification notification = receipt.Notifications.Single();
                Assert.AreEqual(Resources.Recommend_SkillUpdatedTitle, notification.FormatCaption());
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information, notification.Severity);
                Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage, newSkill == 10.75 ? "10.75" : "10.00",
                    difference > 0 ? " (+0.75)" : " (-0.75)"), notification.FormatMessage());
            }
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
            Resources.Culture = previousResourcesCulture;
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }
    /// <summary>
    /// 条件：ja-JPで取り込んだ内蔵の難度推定表・推薦表を、表示言語を en-US へ切り替えてから再読込みする。内容（推定の種類・推薦の方針・★）は変わらない。
    /// 期待結果：更新扱いにならず、DBの last_update と header_sha256 は変わらず、org_name だけが英語の名称へ保存される。
    /// 検出する誤り：表示言語のリソース文字列を header_sha256 へ含め、言語切替の最初の再読込みで「更新」と判定して last_update を進め、BMTを再生成する誤り。
    /// </summary>
    /// <remarks>★の変化による推薦表の更新は Core の ReloadPlaylistTargetsAsync_SavedRecommendedPrefixSurvivesSelectedScoreDatabaseChange が担います。言語の切替と Resources.Culture の復元は AppliedSkillNotification の既存ケースに倣います。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("bmseeker:table.estimation?type=easy")]
    [DataRow("bmseeker:table.recommended")]
    public async Task ReloadPlaylistTargetsAsync_LanguageChangeDoesNotUpdateBuiltInTable(string uri)
    {
        CultureInfo previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        CultureInfo? previousResourcesCulture = Resources.Culture;
        string directory = NewDirectory();
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            string songPath = PlaylistTestFixture.CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
            var repository = new PlaylistPersistenceRepository(databaseScope, songPath);
            var aggregate = new PlaylistAggregatePersistenceOwner(repository, new ReaderWriterLockSlimWrapper(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            PlaylistRecommendedTableOwner owner = CreateOwner(_ => Task.FromResult(ReadWalkureInput("standard")));
            var external = new PlaylistExternalSyncOwner(AppHttpClient.Shared, owner, (_, _) => { }, () => false, _ => { },
                playlistAggregatePersistenceOwner: aggregate, isActiveTable: aggregate.IsActive,
                enterPlaylistUpdating: () => { }, exitPlaylistUpdating: () => { });
            BMSTable table = await external.LoadExternalTableAsync(new Uri(uri));
            string japaneseName = table.org_name;
            // 日時の前進を観測できるよう、取込み直後の日時を過去の固定値へそろえて保存します。
            table.last_update = new DateTime(2024, 1, 2, 3, 4, 5);
            var tables = new System.Collections.ObjectModel.ObservableCollection<BMSTable>([table]);
            aggregate.SetActiveCollection([], tables);
            aggregate.MarkActiveTables([table]);
            aggregate.ReplaceTablesWithEntries([table]);
            BMSTable before = repository.LoadPlaylistHeaders().Single();
            Assert.AreEqual(japaneseName, before.org_name);

            ResourceService.Current.ChangeCulture("en-US");
            string englishName = (await external.LoadExternalTableAsync(new Uri(uri))).org_name;
            Assert.AreNotEqual(japaneseName, englishName, "言語を切り替えると名称は変わります。");
            PlaylistExternalSyncOwner.PlaylistReloadTargetResult result = (await external.ReloadPlaylistTargetsAsync([table])).Single();

            Assert.IsNull(result.Exception);
            Assert.IsFalse(result.Updated, "言語だけの差は更新扱いにしません。");
            BMSTable after = repository.LoadPlaylistHeaders().Single();
            Assert.AreEqual(before.last_update, after.last_update);
            Assert.AreEqual(before.header_sha256, after.header_sha256);
            Assert.AreEqual(before.data_sha256, after.data_sha256);
            Assert.AreEqual(englishName, after.org_name, "名称は英語へ保存します。");
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
            Resources.Culture = previousResourcesCulture;
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            Directory.Delete(directory, true);
        }
    }

    private string NewDirectory()
    {
        string directory = Path.Combine(CaseWorkspace.RootDirectory, nameof(PlaylistRecommendedTableOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
    private static async Task ReachOrComplete(Task reached, Task operation)
    {
        Task first = await Task.WhenAny(reached, operation);
        await first;
        Assert.IsTrue(reached.IsCompletedSuccessfully, "到達前に本体が終結しました。");
    }

}
