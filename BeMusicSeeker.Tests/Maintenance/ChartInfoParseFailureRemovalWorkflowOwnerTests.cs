using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartInfoParseFailureRemovalWorkflowOwnerTests
{
    /// <summary>
    /// 空値を除きMD5をtrim・小文字化して重複をまとめ、対象有無を判断することで同一ハッシュの二重削除や空対象の受理を検出する。
    /// </summary>
    [TestMethod]
    public void Request_NormalizesAndFreezesSelectedMd5s()
    {
        ChartInfoParseFailureRemovalRequest request = new(
            [null, " ", new string('A', 32), new string('a', 32), " " + new string('B', 32) + " "]);

        CollectionAssert.AreEqual(
            new[] { new string('a', 32), new string('b', 32) },
            request.Md5s.ToArray());
        Assert.IsTrue(request.HasTargets);
        Assert.IsFalse(new ChartInfoParseFailureRemovalRequest([null, " "]).HasTargets);
    }

    /// <summary>
    /// 承認後にライブラリ取得・worker起動・保存削除の順に進み、保存処理の解放まで終端しないことで、未確定の完了通知を検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_AcceptsConfirmationAndWaitsForBackgroundStore()
    {

        string root = CreateRoot();
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
    /// No・Cancel・未選択閉鎖は拒否で終え、workerと保存削除を始めず、利用者の拒否を承認へ読み替える誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_UserRejectionDoesNotScheduleOrStore()
    {

        foreach (UiDialogResult confirmation in new[]
        {
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.No),
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel),
            UiDialogResult.ClosedByUser(MessageBoxResult.Cancel)
        })
        {
            var dialogs = new RecordingDialogService { ConfirmationResult = confirmation };
            var store = new RecordingStore();
            int scheduleCalls = 0;
            var owner = new ChartInfoParseFailureRemovalWorkflowOwner(
                () => null!,
                dialogs,
                action =>
                {
                    scheduleCalls++;
                    return Task.Run(action);
                },
                store);

            ChartInfoParseFailureRemovalResult result = await owner.BeginRemove(
                new ChartInfoParseFailureRemovalRequest([new string('a', 32)])).Completion;

            Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Rejected, result.Status);
            Assert.IsFalse(result.Accepted);
            Assert.AreEqual(0, scheduleCalls);
            Assert.AreEqual(0, store.CallCount);
        }
    }

    /// <summary>
    /// 確認画面の失敗は元原因を含む未受理失敗として返し、保存削除を呼ばず、表示できなかった確認での実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_DialogFailureIsExplicitAndDoesNotStore()
    {

        var failure = new InvalidOperationException("dialog unavailable");
        var dialogs = new RecordingDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var store = new RecordingStore();
        var owner = new ChartInfoParseFailureRemovalWorkflowOwner(
            () => null!,
            dialogs,
            action => Task.Run(action),
            store);

        ChartInfoParseFailureRemovalResult result = await owner.BeginRemove(
            new ChartInfoParseFailureRemovalRequest([new string('a', 32)])).Completion;

        Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Failed, result.Status);
        Assert.IsFalse(result.Accepted);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.AreEqual(0, store.CallCount);
    }

    /// <summary>
    /// 利用者の承認後にライブラリがなければ受理済み失敗として返し、workerを始めず、対象消失を成功扱いする誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_AcceptedButLibraryUnavailableIsFailure()
    {

        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        int scheduleCalls = 0;
        var owner = new ChartInfoParseFailureRemovalWorkflowOwner(
            () => null!,
            dialogs,
            action =>
            {
                scheduleCalls++;
                return Task.Run(action);
            },
            new RecordingStore());

        ChartInfoParseFailureRemovalResult result = await owner.BeginRemove(
            new ChartInfoParseFailureRemovalRequest([new string('a', 32)])).Completion;

        Assert.AreEqual(ChartInfoParseFailureRemovalStatus.Failed, result.Status);
        Assert.IsTrue(result.Accepted);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(0, scheduleCalls);
    }

    /// <summary>
    /// 承認済みの保存削除が失敗したら一回実行した元例外を受理済み失敗へ保持し、失敗の破棄や再実行を検出する。
    /// </summary>
    [TestMethod]
    public async Task RemoveAsync_StoreFailureIsPropagatedAsAcceptedFailure()
    {

        string root = CreateRoot();
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
        string root = CreateRoot();
        BMSLibrary? library = null;
        try
        {
            library = CreateLibrary(root);
            string db = Path.Combine(root, "song.db");
            var gateway = new BmsLibraryDbGateway(db);
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

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(ChartInfoParseFailureRemovalWorkflowOwnerTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static BMSLibrary CreateLibrary(string root)
    {
        string songDbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDbPath, []);
        return new TestBmsLibrary(songDbPath);
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingStore : IChartInfoParseFailureRemovalStore
    {
        internal int CallCount { get; private set; }

        internal IReadOnlyList<string> Md5s { get; private set; } = [];

        internal Action BeforeRemove { get; set; } = null!;

        internal Action RemoveAction { get; set; } = null!;

        internal Exception Failure { get; set; } = null!;

        public void Remove(BMSLibrary library, IReadOnlyList<string> md5s, LibraryFileMutationCapability capability)
        {
            CallCount++;
            Md5s = md5s.ToArray();
            BeforeRemove?.Invoke();
            RemoveAction?.Invoke();
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } = null!;

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; } = null!;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            LastConfirmationRequest = request;
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
