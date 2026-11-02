using System.Globalization;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
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
        try
        {
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
            if (expectedCount != 0)
            {
                PlaylistOperationNotificationOwner.OperationNotification notification = receipt.Notifications.Single();
                Assert.AreEqual(Resources.Recommend_SkillUpdatedTitle, notification.Caption);
                Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information, notification.Severity);
                Assert.AreEqual(string.Format(Resources.Recommend_SkillUpdatedMessage, newSkill == 10.75 ? "10.75" : "10.00",
                    difference > 0 ? " (+0.75)" : " (-0.75)"), notification.Message);
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previousCulture; }
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
