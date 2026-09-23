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

[TestClass]
public sealed class ApplicationDataUninstallWorkflowOwnerTests
{
    [TestMethod]
    public async Task WorkspaceNotReady_DoesNotOpenDialogsOrStore()
    {
        var dialogs = new RecordingDialogService();
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.RunAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: false,
            isLibraryOperationInProgress: false,
            songDbPath: "song.db"));

        Assert.AreEqual(ApplicationDataUninstallOutcome.NotStarted, result.Outcome);
        Assert.AreEqual(0, store.CallCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), dialogs.Events.ToArray());
    }

    [TestMethod]
    public async Task LibraryOperationInProgress_ShowsBlockedMessageWithoutConfirmationOrStore()
    {
        var dialogs = new RecordingDialogService();
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.RunAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: true,
            songDbPath: "song.db"));

        Assert.AreEqual(ApplicationDataUninstallOutcome.Blocked, result.Outcome);
        Assert.AreEqual(0, store.CallCount);
        CollectionAssert.AreEqual(new[] { "message" }, dialogs.Events.ToArray());
    }

    [TestMethod]
    public async Task ConfirmationRejected_DoesNotStartDurableOrTerminalRoute()
    {
        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.RunAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: false,
            songDbPath: "song.db"));

        Assert.AreEqual(ApplicationDataUninstallOutcome.Cancelled, result.Outcome);
        Assert.AreEqual(0, store.CallCount);
        CollectionAssert.AreEqual(new[] { "confirm" }, dialogs.Events.ToArray());
    }

    [TestMethod]
    public async Task AcceptedOperationPublishesSuccessThenExitAndCompletes()
    {
        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.RunAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: false,
            songDbPath: "song.db"));

        Assert.AreEqual(ApplicationDataUninstallOutcome.Completed, result.Outcome);
        Assert.IsTrue(result.ShouldCloseApplication);
        Assert.AreEqual("song.db", store.LastSongDbPath);
        CollectionAssert.AreEqual(new[] { "confirm", "store", "message", "message" }, dialogs.Events.ToArray());
        Assert.AreEqual(2, dialogs.MessageRequests.Count);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_success_uninstall, dialogs.MessageRequests[0].MessageBoxText);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_ApplicationWillExit, dialogs.MessageRequests[1].MessageBoxText);
    }

    [TestMethod]
    public async Task StoreFailureShowsFailureAndDoesNotCompleteClose()
    {
        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var failure = new InvalidOperationException("store failure");
        var store = new RecordingStore(dialogs.Events) { Failure = failure };
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        ApplicationDataUninstallResult result = await owner.RunAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: false,
            songDbPath: "song.db"));

        Assert.AreEqual(ApplicationDataUninstallOutcome.Failed, result.Outcome);
        Assert.AreSame(failure, result.Failure);
        Assert.IsFalse(result.ShouldCloseApplication);
        CollectionAssert.AreEqual(new[] { "confirm", "store", "message" }, dialogs.Events.ToArray());
        StringAssert.Contains(dialogs.MessageRequests[0].MessageBoxText, BeMusicSeeker.Properties.Resources.Msg_failed_uninstall);
    }

    [TestMethod]
    public async Task DialogFailureIsNotTreatedAsCancellation()
    {
        var dialogs = new RecordingDialogService
        {
            ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog unavailable"))
        };
        var store = new RecordingStore(dialogs.Events);
        var owner = new ApplicationDataUninstallWorkflowOwner(dialogs, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => owner.RunAsync(new ApplicationDataUninstallRequest(
            isWorkspaceReady: true,
            isLibraryOperationInProgress: false,
            songDbPath: "song.db")));

        Assert.AreEqual(0, store.CallCount);
        CollectionAssert.AreEqual(new[] { "confirm" }, dialogs.Events.ToArray());
    }

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

        internal Exception Failure { get; set; } = null!;

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
