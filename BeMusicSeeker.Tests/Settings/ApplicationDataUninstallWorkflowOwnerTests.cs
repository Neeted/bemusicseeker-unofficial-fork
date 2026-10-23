using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>アプリ由来データの削除に必要な受付・確認・通知・終了判断を検証します。</summary>
/// <remarks>削除窓口とダイアログを差し替え、実 DB の削除はストア側、実 Window の終了は MainWindow 側に分担します。</remarks>
[TestClass]
public sealed class ApplicationDataUninstallWorkflowOwnerTests
{
    /// <summary>workspace 未準備なら確認も削除も始めず NotStarted を返すことを確認します。</summary>
    [TestMethod]
    public async Task WorkspaceNotReady_DoesNotOpenDialogsOrStore()
    {
        var dialogs = new RecordingDialogService();
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.ConfirmAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: false,
            isLibraryOperationInProgress: false));

        Assert.AreEqual(ApplicationDataUninstallOutcome.NotStarted, result.Outcome);
        Assert.AreEqual(0, store.CallCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), dialogs.Events.ToArray());
    }

    /// <summary>ライブラリ操作中は拒否通知だけを送り、確認や削除へ進まないことを確認します。</summary>
    [TestMethod]
    public async Task LibraryOperationInProgress_ShowsBlockedMessageWithoutConfirmationOrStore()
    {
        var dialogs = new RecordingDialogService();
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.ConfirmAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: true));

        Assert.AreEqual(ApplicationDataUninstallOutcome.Blocked, result.Outcome);
        Assert.AreEqual(0, store.CallCount);
        CollectionAssert.AreEqual(new[] { "message" }, dialogs.Events.ToArray());
    }

    /// <summary>利用者が確認を取り消した場合は Cancelled とし、永続データ削除や終了通知を始めないことを確認します。</summary>
    [TestMethod]
    public async Task ConfirmationRejected_DoesNotStartDurableOrTerminalRoute()
    {
        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.ConfirmAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: false));

        Assert.AreEqual(ApplicationDataUninstallOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(0, store.CallCount);
        CollectionAssert.AreEqual(new[] { "confirm" }, dialogs.Events.ToArray());
    }

    /// <summary>承認後に指定 DB の削除を呼び、成功→終了案内の順に通知して終了可能な結果を返すことを確認します。</summary>
    [TestMethod]
    public async Task AcceptedOperationPublishesSuccessThenExitAndCompletes()
    {
        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.ConfirmAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: false));

        Assert.AreEqual(ApplicationDataUninstallOutcome.Approved, result.Outcome);
        Assert.AreEqual(0, store.CallCount);
        result = await owner.CompleteAsync("song.db", dialogs);
        Assert.AreEqual(ApplicationDataUninstallOutcome.Completed, result.Outcome);

        Assert.AreEqual("song.db", store.LastSongDbPath);
        CollectionAssert.AreEqual(new[] { "confirm", "store", "message", "message" }, dialogs.Events.ToArray());
        Assert.AreEqual(2, dialogs.MessageRequests.Count);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_success_uninstall, dialogs.MessageRequests[0].MessageBoxText);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_ApplicationWillExit, dialogs.MessageRequests[1].MessageBoxText);
    }

    /// <summary>確認時には削除せず、終了の終端段階で削除失敗を元例外と失敗通知で伝えることを確認します。</summary>
    [TestMethod]
    public async Task StoreFailureShowsFailureAtTerminalShutdown()
    {
        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var failure = new InvalidOperationException("store failure");
        var store = new RecordingStore(dialogs.Events) { Failure = failure };
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.ConfirmAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: false));

        Assert.AreEqual(ApplicationDataUninstallOutcome.Approved, result.Outcome);
        Assert.AreEqual(0, store.CallCount);
        result = await owner.CompleteAsync("song.db", dialogs);
        Assert.AreEqual(ApplicationDataUninstallOutcome.Failed, result.Outcome);
        Assert.AreSame(failure, result.Failure);

        CollectionAssert.AreEqual(new[] { "confirm", "store", "message" }, dialogs.Events.ToArray());
        StringAssert.Contains(dialogs.MessageRequests[0].MessageBoxText, BeMusicSeeker.Properties.Resources.Msg_failed_uninstall);
    }

    /// <summary>確認の表示失敗を取消結果へ隠さず例外として返し、削除を開始しないことを確認します。</summary>
    [TestMethod]
    public async Task DialogFailureIsNotTreatedAsCancellation()
    {
        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog unavailable"))
        };
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.ConfirmAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: false)));

        Assert.AreEqual(0, store.CallCount);
        CollectionAssert.AreEqual(new[] { "confirm" }, dialogs.Events.ToArray());
    }

    /// <summary>存在しない DB パスの削除は例外なく完了し、初回利用など DB が未作成でも削除処理を呼べることを確認します。</summary>
    [TestMethod]
    public void MissingSongDbIsAStableNoOpForDurableStore()
    {
        string missingPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BeMusicSeeker_missing_song_db_" + Guid.NewGuid().ToString("N"),
            "song.db");

        new Lr2ApplicationDataUninstallStore().Uninstall(missingPath);
    }

    private sealed class RecordingStore : IApplicationDataUninstallStore
    {
        private readonly ConcurrentQueue<string> events;

        internal RecordingStore(ConcurrentQueue<string> events)
        {
            this.events = events;
        }

        internal int CallCount { get; private set; }

        internal string LastSongDbPath { get; private set; } = string.Empty;

        internal Exception? Failure { get; set; }

        public void Uninstall(string songDbPath)
        {
            CallCount++;
            LastSongDbPath = songDbPath;
            events.Enqueue("store");
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal ConcurrentQueue<string> Events { get; } = new();

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Events.Enqueue("message");
            lock (MessageRequests)
            {
                MessageRequests.Add(request);
            }
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            Events.Enqueue("confirm");
            return Task.FromResult(ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
