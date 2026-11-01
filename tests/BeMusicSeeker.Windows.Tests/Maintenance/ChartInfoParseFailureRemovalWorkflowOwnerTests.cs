using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.ChartInfoParseFailureRemovalWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartInfoParseFailureRemovalWorkflowOwnerTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 承認後にライブラリ取得・worker起動・保存削除の順に進み、保存処理の解放まで終端しないことで、未確定の完了通知を検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_AcceptsConfirmationAndWaitsForBackgroundStore()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        try
        {
            BMSLibrary library = CreateLibrary(root);
            var dialogs = new RecordingDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var storeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStore = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var store = new RecordingStore
            {
                BeforeRemove = () =>
                {
                    order.Add("store");
                    storeStarted.TrySetResult(true);
                },
                RemoveAction = () => releaseStore.Task.GetAwaiter().GetResult()
            };
            var owner = new ChartInfoParseFailureRemovalWorkflowOwner(
                () =>
                {
                    order.Add("library");
                    return library;
                },
                dialogs,
                action =>
                {
                    order.Add("schedule");
                    return Task.Run(action);
                },
                store);
            ChartInfoParseFailureRemovalRequest request = new([" " + new string('A', 32) + " ", new string('a', 32)]);

            ChartInfoParseFailureRemovalOperation operation = owner.BeginRemove(request);
            ChartInfoParseFailureRemovalAcceptance acceptance = await operation.Acceptance;
            Assert.IsTrue(acceptance.Accepted);
            // 受理通知とworker起動は別の非同期段階なので、保存処理の到達を実終端に接続して順序を観測する。
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(storeStarted.Task, operation.Completion, "parse-failure-removal-store");
                Assert.IsFalse(operation.Completion.IsCompleted);
                CollectionAssert.AreEqual(new[] { "library", "schedule", "store" }, order.ToArray());
                Assert.IsNotNull(dialogs.LastConfirmationRequest);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_remove_chart_info_parse_failure_record, dialogs.LastConfirmationRequest.MessageBoxText);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Confirm, dialogs.LastConfirmationRequest.Caption);
                Assert.AreEqual(MessageBoxButton.OKCancel, dialogs.LastConfirmationRequest.Button);
                Assert.AreEqual(MessageBoxImage.Question, dialogs.LastConfirmationRequest.Icon);
                Assert.AreEqual(MessageBoxResult.Cancel, dialogs.LastConfirmationRequest.DefaultResult);

                releaseStore.TrySetResult(true);
                ChartInfoParseFailureRemovalResult result = await operation.Completion;

                Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Removed, result.Status);
                Assert.IsTrue(result.Accepted);
                Assert.AreEqual(1, store.CallCount);
                CollectionAssert.AreEqual(new[] { new string('a', 32) }, store.Md5s.ToArray());
            }
            finally
            {
                releaseStore.TrySetResult(true);
                await operation.Completion;
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// 承認済みの保存削除が失敗したら一回実行した元例外を受理済み失敗へ保持し、失敗の破棄や再実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_StoreFailureIsPropagatedAsAcceptedFailure()
    {

        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

        string root = CreateRoot(caseWorkspace);
        try
        {
            BMSLibrary library = CreateLibrary(root);
            var failure = new InvalidOperationException("database write failed");
            var store = new RecordingStore { Failure = failure };
            var owner = new ChartInfoParseFailureRemovalWorkflowOwner(
                () => library,
                new RecordingDialogService
                {
                    ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
                },
                action => Task.Run(action),
                store);

            ChartInfoParseFailureRemovalResult result = await owner.BeginRemove(
                new ChartInfoParseFailureRemovalRequest([new string('a', 32)])).Completion;

            Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Failed, result.Status);
            Assert.IsTrue(result.Accepted);
            Assert.AreSame(failure, result.Failure);
            Assert.AreEqual(1, store.CallCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// 実DBの解析失敗行は共通排他の保持中に変更せず、解放後の明示要求で選択MD5だけを削除して、対象外削除や受付の残存を検出する。
    /// </summary>
    /// <remarks>実adapterと独立したSQLite DBを使い、固定対象の保存削除と受付解放を確認する。</remarks>
    [TestMethod]
    public async Task RealStore_RemovesOnlyAcceptedParseFailureAndReleasesAdmission()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = CreateRoot(caseWorkspace);
        BMSLibrary? library = null;
        try
        {
            library = CreateLibrary(root);
            string db = Path.Combine(root, "song.db");
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), db);
            gateway.EnsureChartInfoSchema();
            string selected = new string('a', 32);
            string keeper = new string('b', 32);
            gateway.UpsertChartInfoParseFailures([new ChartParseFailure { md5 = selected, path = "first.bms", parser_version = 1, failure_kind = "fixture", updated_at = DateTime.UtcNow },
                new ChartParseFailure { md5 = keeper, path = "keeper.bms", parser_version = 1, failure_kind = "fixture", updated_at = DateTime.UtcNow }]);
            var dialogs = new RecordingDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
            var owner = new ChartInfoParseFailureRemovalWorkflowOwner(() => library, dialogs, Task.Run);
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable busy));
            using (busy)
            {
                ChartInfoParseFailureRemovalResult rejected = await owner.BeginRemove(new([selected])).Completion;
                Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Busy, rejected.Status);
                using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                Assert.AreEqual(2, read.Table<LR2SongDBExtended.chart_info_parse_failure>().Count());
            }
            Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Removed, (await owner.BeginRemove(new([selected])).Completion).Status);
            using (LR2SongDBExtended read = gateway.OpenSongDbReadOnly())
            { Assert.AreEqual(keeper, read.Table<LR2SongDBExtended.chart_info_parse_failure>().Single().md5); }
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        finally { library?.RequestShutdown("parse-failure-entry-test"); DeleteRoot(root); }
    }

    private static string CreateRoot(TestWorkspace workspace)
    {
        string root = Path.Combine(
            workspace.RootDirectory,
            nameof(ChartInfoParseFailureRemovalWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private BMSLibrary CreateLibrary(string root)
    {
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        return new TestBmsLibrary(databaseScope, songDbPath);
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
