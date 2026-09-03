using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncWorkflowOwnerTests
{
    [TestMethod]
    public void RequestStatusBarRetry_QueuesWithReasonAndGeneratedDataPreparation()
    {
        var runtime = new RecordingRuntime();
        var owner = CreateOwner(runtime);

        owner.RequestStatusBarRetry();

        Assert.AreEqual(1, runtime.QueueCalls.Count);
        Assert.AreEqual("status_bar_retry", runtime.QueueCalls[0].Reason);
        Assert.IsFalse(runtime.QueueCalls[0].Force);
        CollectionAssert.Contains(runtime.Events, "prepare-playlist:status_bar_retry");
    }

    [TestMethod]
    public async Task RequestManualResync_QueuesForcedRequestAndAwaitsIt()
    {
        var runtime = new RecordingRuntime();
        var owner = CreateOwner(runtime);

        await owner.RequestManualResyncAsync();

        Assert.AreEqual(1, runtime.QueueCalls.Count);
        Assert.AreEqual("setting_dialog_manual_resync", runtime.QueueCalls[0].Reason);
        Assert.IsTrue(runtime.QueueCalls[0].Force);
    }

    [TestMethod]
    public void DisabledModeOrUnavailableLibrary_DoesNotWriteOrOpenCleanupDialog()
    {
        var runtime = new RecordingRuntime { IsLr2ModeEnabled = false };
        var dialogs = new RecordingDialogService(runtime.Events);
        var owner = CreateOwner(runtime, dialogs);

        owner.RequestStatusBarRetry();
        owner.CleanupStartupScanBlockersAndRetry();

        runtime.IsLr2ModeEnabled = true;
        runtime.IsLibraryAvailable = false;
        owner.CleanupStartupScanBlockersAndRetry();

        Assert.AreEqual(0, runtime.QueueCalls.Count);
        Assert.AreEqual(0, runtime.CleanupCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), runtime.Events.ToArray());
    }

    [TestMethod]
    public void CleanupRejected_SkipsDurableCleanupAndRetry()
    {
        var runtime = new RecordingRuntime();
        var dialogs = new RecordingDialogService(runtime.Events)
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(runtime, dialogs);

        owner.CleanupStartupScanBlockersAndRetry();

        Assert.AreEqual(0, runtime.CleanupCount);
        Assert.AreEqual(0, runtime.QueueCalls.Count);
        CollectionAssert.AreEqual(new[] { "confirm" }, runtime.Events.ToArray());
    }

    [TestMethod]
    public void CleanupAccepted_CompletesCleanupBeforeRetryQueue()
    {
        var runtime = new RecordingRuntime();
        var dialogs = new RecordingDialogService(runtime.Events);
        var owner = CreateOwner(runtime, dialogs);

        owner.CleanupStartupScanBlockersAndRetry();

        Assert.AreEqual(1, runtime.CleanupCount);
        Assert.AreEqual(1, runtime.QueueCalls.Count);
        Assert.AreEqual("status_bar_cleanup_retry", runtime.QueueCalls[0].Reason);
        Assert.IsTrue(runtime.Events.IndexOf("confirm") < runtime.Events.IndexOf("cleanup"));
        Assert.IsTrue(runtime.Events.IndexOf("cleanup") < runtime.Events.IndexOf("queue:status_bar_cleanup_retry"));
    }

    [TestMethod]
    public void CleanupFailure_ShowsFailureAndDoesNotQueueRetry()
    {
        var failure = new InvalidOperationException("cleanup failure");
        var runtime = new RecordingRuntime { CleanupFailure = failure };
        var dialogs = new RecordingDialogService(runtime.Events);
        var owner = CreateOwner(runtime, dialogs);

        owner.CleanupStartupScanBlockersAndRetry();

        Assert.AreEqual(1, runtime.CleanupCount);
        Assert.AreEqual(0, runtime.QueueCalls.Count);
        CollectionAssert.AreEqual(new[] { "confirm", "cleanup", "message" }, runtime.Events.ToArray());
        StringAssert.Contains(dialogs.MessageRequests[0].MessageBoxText, failure.Message);
    }

    [TestMethod]
    public void DialogFailure_IsNotNormalizedToCancellation()
    {
        var runtime = new RecordingRuntime();
        var dialogs = new RecordingDialogService(runtime.Events)
        {
            ConfirmationResult = UiDialogResult.Failed(new InvalidOperationException("dialog unavailable"))
        };
        var owner = CreateOwner(runtime, dialogs);

        Assert.ThrowsException<InvalidOperationException>(() => owner.CleanupStartupScanBlockersAndRetry());

        Assert.AreEqual(0, runtime.CleanupCount);
        Assert.AreEqual(0, runtime.QueueCalls.Count);
    }

    [TestMethod]
    public void SettingsCoreSync_PreparesBothSurfacesBeforeIncompleteQueue()
    {
        var runtime = new RecordingRuntime();
        var owner = CreateOwner(runtime);

        owner.SyncFolderDataAfterSettingsChange("SettingDialog.SaveSettings");

        Assert.AreEqual(1, runtime.QueueCalls.Count);
        Assert.AreEqual("SettingDialog.SaveSettings", runtime.QueueCalls[0].Reason);
        Assert.IsFalse(runtime.QueueCalls[0].AllowIncompleteToQueue);
        Assert.IsTrue(runtime.Events.IndexOf("prepare-playlist:SettingDialog.SaveSettings") < runtime.Events.IndexOf("prepare-builtin:SettingDialog.SaveSettings"));
        Assert.IsTrue(runtime.Events.IndexOf("prepare-builtin:SettingDialog.SaveSettings") < runtime.Events.IndexOf("queue:SettingDialog.SaveSettings"));
    }

    [TestMethod]
    public void SettingsExternalImpact_UsesExternalRowsOnly()
    {
        var runtime = new RecordingRuntime();
        var owner = CreateOwner(runtime);

        owner.SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChange("SettingDialog.SaveSettings");

        CollectionAssert.AreEqual(new[] { "external:SettingDialog.SaveSettings" }, runtime.Events.ToArray());
        Assert.AreEqual(0, runtime.QueueCalls.Count);
    }

    [TestMethod]
    public void PostStartupSync_InvokesContinuationAfterQueueAttempt()
    {
        var runtime = new RecordingRuntime();
        var owner = CreateOwner(runtime);
        int continuationCount = 0;

        owner.SchedulePostStartupSync("initialization_complete", () => continuationCount++);

        Assert.AreEqual(1, runtime.QueueCalls.Count);
        Assert.AreEqual("post_startup_initialization_complete", runtime.QueueCalls[0].Reason);
        Assert.AreEqual(1, continuationCount);
    }

    [TestMethod]
    public void PostStartupSync_InvokesContinuationWhenQueueFailsAndPreservesFailure()
    {
        var failure = new InvalidOperationException("queue failure");
        var runtime = new RecordingRuntime { QueueFailure = failure };
        var owner = CreateOwner(runtime);
        int continuationCount = 0;

        InvalidOperationException thrown = Assert.ThrowsException<InvalidOperationException>(
            () => owner.SchedulePostStartupSync("initialization_complete", () => continuationCount++));

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(1, continuationCount);
    }

    private static Lr2SongDbSyncWorkflowOwner CreateOwner(
        RecordingRuntime runtime,
        RecordingDialogService dialogs = null!)
    {
        dialogs ??= new RecordingDialogService(runtime.Events);
        return new Lr2SongDbSyncWorkflowOwner(
            runtime,
            dialogs,
            action =>
            {
                action();
                return Task.CompletedTask;
            },
            (_, _) => { });
    }

    private sealed class RecordingRuntime : ILr2SongDbSyncWorkflowRuntime
    {
        internal bool IsLr2ModeEnabled { get; set; } = true;

        internal bool IsLibraryAvailable { get; set; } = true;

        internal List<string> Events { get; } = [];

        internal List<QueueCall> QueueCalls { get; } = [];

        internal int CleanupCount { get; private set; }

        internal Exception CleanupFailure { get; set; } = null!;

        internal Exception QueueFailure { get; set; } = null!;

        bool ILr2SongDbSyncWorkflowRuntime.IsLr2ModeEnabled => IsLr2ModeEnabled;

        bool ILr2SongDbSyncWorkflowRuntime.IsLibraryAvailable => IsLibraryAvailable;

        public void Queue(
            string reason,
            bool force,
            bool prepareGeneratedData = false,
            bool allowIncompleteToQueue = true)
        {
            Events.Add("queue:" + reason);
            QueueCalls.Add(new QueueCall(reason, force, allowIncompleteToQueue));
            if (QueueFailure != null)
            {
                throw QueueFailure;
            }
            if (prepareGeneratedData)
            {
                Events.Add("prepare-playlist:" + reason);
            }
        }

        public bool TryRunDataPreparation(
            string reason,
            bool includeBuiltinGeneratedData = false,
            Action queueAfterPreparation = null!)
        {
            Events.Add("prepare-gate:" + reason);
            Events.Add("prepare-playlist:" + reason);
            if (includeBuiltinGeneratedData)
            {
                Events.Add("prepare-builtin:" + reason);
            }
            queueAfterPreparation?.Invoke();
            return true;
        }

        public void SyncExternalFolderRowsForCustomFolderOutputBaseChange(string reason)
        {
            Events.Add("external:" + reason);
        }

        public Lr2StartupScanBlockerCleanupResult CleanupStartupScanBlockerFolderRows(string reason)
        {
            CleanupCount++;
            Events.Add("cleanup");
            if (CleanupFailure != null)
            {
                throw CleanupFailure;
            }

            return null!;
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        private readonly List<string> events;

        internal RecordingDialogService(List<string> events)
        {
            this.events = events;
        }

        internal UiDialogResult ConfirmationResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal List<UiMessageRequest> MessageRequests { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("message");
            MessageRequests.Add(request);
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("confirm");
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

    private sealed record QueueCall(string Reason, bool Force, bool AllowIncompleteToQueue);
}
