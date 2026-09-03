using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
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
        Assert.IsFalse(runtime.QueueCalls[0].AllowCommittedPathReceipt);
    }

    [TestMethod]
    public void DisabledModeOrUnavailableLibrary_DoesNotQueueRetry()
    {
        var runtime = new RecordingRuntime { IsLr2ModeEnabled = false };
        var owner = CreateOwner(runtime);

        owner.RequestStatusBarRetry();

        runtime.IsLr2ModeEnabled = true;
        runtime.IsLibraryAvailable = false;
        owner.RequestStatusBarRetry();

        Assert.AreEqual(0, runtime.QueueCalls.Count);
        CollectionAssert.AreEqual(Array.Empty<string>(), runtime.Events.ToArray());
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
        Assert.IsTrue(runtime.QueueCalls[0].AllowCommittedPathReceipt);
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

    private static Lr2SongDbSyncWorkflowOwner CreateOwner(RecordingRuntime runtime)
    {
        return new Lr2SongDbSyncWorkflowOwner(
            runtime,
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

        internal Exception QueueFailure { get; set; } = null!;

        bool ILr2SongDbSyncWorkflowRuntime.IsLr2ModeEnabled => IsLr2ModeEnabled;

        bool ILr2SongDbSyncWorkflowRuntime.IsLibraryAvailable => IsLibraryAvailable;

        public void DiscardCommittedPathReceipt(string reason)
        {
        }

        public void Queue(
            string reason,
            bool force,
            bool prepareGeneratedData = false,
            bool allowIncompleteToQueue = true,
            bool allowCommittedPathReceipt = false)
        {
            Events.Add("queue:" + reason);
            QueueCalls.Add(new QueueCall(reason, force, allowIncompleteToQueue, allowCommittedPathReceipt));
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

    }

    private sealed record QueueCall(
        string Reason,
        bool Force,
        bool AllowIncompleteToQueue,
        bool AllowCommittedPathReceipt);
}
