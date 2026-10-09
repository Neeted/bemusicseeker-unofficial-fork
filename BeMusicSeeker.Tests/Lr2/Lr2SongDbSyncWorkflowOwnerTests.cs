using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
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
        Lr2SongDbSyncWorkflowOwner owner = CreateOwner(runtime);

        owner.RequestStatusBarRetry();

        Assert.AreEqual(1, runtime.QueueCalls.Count);
        Assert.AreEqual("status_bar_retry", runtime.QueueCalls[0].Reason);
        Assert.IsFalse(runtime.QueueCalls[0].Force);
        Assert.IsTrue(runtime.QueueCalls[0].PrepareGeneratedData);
    }

    [TestMethod]
    public async Task RequestManualResync_QueuesForcedRequestAndAwaitsIt()
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime { QueueTask = terminal.Task };
        Lr2SongDbSyncWorkflowOwner owner = CreateOwner(runtime);
        Task operation = owner.RequestManualResyncAsync();
        try
        {
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(1, runtime.QueueCalls.Count);
            Assert.AreEqual("setting_dialog_manual_resync", runtime.QueueCalls[0].Reason);
            Assert.IsTrue(runtime.QueueCalls[0].Force);
            Assert.IsFalse(runtime.QueueCalls[0].AllowCommittedPathReceipt);
            Assert.IsFalse(runtime.QueueCalls[0].AcceptedBackground);
            terminal.TrySetResult();
            await operation;
        }
        finally { terminal.TrySetResult(); await operation; }
    }

    [TestMethod]
    public void DisabledModeOrUnavailableLibrary_DoesNotQueueRetry()
    {
        var runtime = new RecordingRuntime { IsLr2ModeEnabled = false };
        Lr2SongDbSyncWorkflowOwner owner = CreateOwner(runtime);

        owner.RequestStatusBarRetry();

        runtime.IsLr2ModeEnabled = true;
        runtime.IsLibraryAvailable = false;
        owner.RequestStatusBarRetry();

        Assert.AreEqual(0, runtime.QueueCalls.Count);
        CollectionAssert.AreEqual(Array.Empty<string>(), runtime.Events.ToArray());
    }

    [TestMethod]
    public async Task SettingsCoreSync_ForwardsLiveCapabilityAndAwaitsBothSurfaceRequest()
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime { QueueTask = terminal.Task };
        Lr2SongDbSyncWorkflowOwner owner = CreateOwner(runtime);
        var admission = new ChartFileOperationSynchronizer();
        Assert.IsTrue(admission.TryEnter(out IDisposable lease));
        using (lease)
        using (LibraryFileMutationCapability capability = admission.CreateMutationCapability(lease))
        {
            Task operation = owner.SyncFolderDataAfterSettingsChangeAsync("SettingDialog.SaveSettings", capability);
            try
            {
                Assert.IsFalse(operation.IsCompleted);
                Assert.AreEqual(1, runtime.QueueCalls.Count);
                QueueCall call = runtime.QueueCalls[0];
                Assert.AreEqual("SettingDialog.SaveSettings", call.Reason);
                Assert.IsFalse(call.AllowIncompleteToQueue);
                Assert.IsTrue(call.PrepareGeneratedData);
                Assert.IsTrue(call.IncludeBuiltinGeneratedData);
                Assert.AreSame(capability, call.Capability);
                capability.Validate(admission);
                terminal.TrySetResult();
                await operation;
                Assert.IsTrue(admission.IsActive);
            }
            finally { terminal.TrySetResult(); await operation; }
        }
        Assert.IsFalse(admission.IsActive);
    }

    [TestMethod]
    public async Task SettingsExternalImpact_UsesExternalRowsOnly()
    {
        var runtime = new RecordingRuntime();
        Lr2SongDbSyncWorkflowOwner owner = CreateOwner(runtime);

        var admission = new ChartFileOperationSynchronizer();
        Assert.IsTrue(admission.TryEnter(out IDisposable lease));
        using (lease)
        using (LibraryFileMutationCapability capability = admission.CreateMutationCapability(lease))
        { await owner.SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChangeAsync("SettingDialog.SaveSettings", capability); }

        CollectionAssert.AreEqual(new[] { "external:SettingDialog.SaveSettings" }, runtime.Events.ToArray());
        Assert.AreEqual(0, runtime.QueueCalls.Count);
    }

    [TestMethod]
    public async Task SettingsExternalImpact_TracksActualOperationFailureUntilTerminal()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime { ExternalSyncTask = completion.Task };
        Task? observed = null;
        var owner = new Lr2SongDbSyncWorkflowOwner(runtime,
            action => { action(); return Task.CompletedTask; },
            (task, _) => observed = task);
        var failure = new System.IO.IOException("external sync failed");
        var admission = new ChartFileOperationSynchronizer();
        Assert.IsTrue(admission.TryEnter(out IDisposable lease));
        using (lease)
        using (LibraryFileMutationCapability capability = admission.CreateMutationCapability(lease))
        {
            try
            {
                observed = owner.SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChangeAsync("SettingDialog.SaveSettings", capability);
                Assert.IsNotNull(observed);
                Assert.IsFalse(observed.IsCompleted);
                completion.SetException(failure);
                System.IO.IOException thrown = await Assert.ThrowsExceptionAsync<System.IO.IOException>(() => observed);
                Assert.AreSame(failure, thrown);
                Assert.IsTrue(observed.IsCompleted);
            }
            finally
            {
                completion.TrySetResult();
                if (observed != null)
                {
                    try { await observed; }
                    catch (System.IO.IOException exception) when (ReferenceEquals(exception, failure)) { }
                }
            }
        }
    }

    [TestMethod]
    public async Task PostStartupSync_InvokesContinuationAfterQueueAttempt()
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime { QueueTask = terminal.Task };
        Task? observed = null;
        var owner = new Lr2SongDbSyncWorkflowOwner(runtime, action => { action(); return Task.CompletedTask; }, (task, _) => observed = task);
        int continuationCount = 0;
        owner.SchedulePostStartupSync("initialization_complete", () => continuationCount++);
        Assert.IsNotNull(observed);
        try
        {
            Assert.IsFalse(observed.IsCompleted);
            Assert.AreEqual(0, continuationCount);
            Assert.AreEqual(1, runtime.QueueCalls.Count);
            Assert.AreEqual("post_startup_initialization_complete", runtime.QueueCalls[0].Reason);
            Assert.IsTrue(runtime.QueueCalls[0].AllowCommittedPathReceipt);
            Assert.IsTrue(runtime.QueueCalls[0].AcceptedBackground);
            terminal.TrySetResult();
            await observed;
            Assert.AreEqual(1, continuationCount);
        }
        finally { terminal.TrySetResult(); await observed; }
    }

    [TestMethod]
    public async Task PostStartupSync_InvokesContinuationWhenQueueFailsAndPreservesFailure()
    {
        var failure = new InvalidOperationException("queue failure");
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new RecordingRuntime { QueueTask = terminal.Task };
        Task? observed = null;
        var owner = new Lr2SongDbSyncWorkflowOwner(runtime, action => { action(); return Task.CompletedTask; }, (task, _) => observed = task);
        int continuationCount = 0;
        owner.SchedulePostStartupSync("initialization_complete", () => continuationCount++);
        Assert.IsNotNull(observed);
        try
        {
            Assert.IsFalse(observed.IsCompleted);
            Assert.AreEqual(0, continuationCount);
            terminal.SetException(failure);
            InvalidOperationException thrown = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => observed);
            Assert.AreSame(failure, thrown);
            Assert.AreEqual(1, continuationCount);
        }
        finally
        {
            terminal.TrySetResult();
            try { await observed; } catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
        }
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

        internal Task QueueTask { get; set; } = Task.CompletedTask;

        internal Task ExternalSyncTask { get; set; } = Task.CompletedTask;

        internal Exception QueueFailure { get; set; } = null!;

        bool ILr2SongDbSyncWorkflowRuntime.IsLr2ModeEnabled => IsLr2ModeEnabled;

        bool ILr2SongDbSyncWorkflowRuntime.IsLibraryAvailable => IsLibraryAvailable;

        public void DiscardCommittedPathReceipt(string reason)
        {
        }

        public async Task<bool> QueueAsync(
            string reason,
            bool force,
            bool prepareGeneratedData = false,
            bool allowIncompleteToQueue = true,
            bool allowCommittedPathReceipt = false, LibraryFileMutationCapability? capability = null, bool acceptedBackground = false, bool includeBuiltinGeneratedData = false, LibraryFileMutationCapability? playlistCapability = null)
        {
            if (prepareGeneratedData)
            {
                Events.Add("prepare-playlist:" + reason);
                if (includeBuiltinGeneratedData) { Events.Add("prepare-builtin:" + reason); }
            }
            Events.Add("queue:" + reason);
            QueueCalls.Add(new QueueCall(reason, force, allowIncompleteToQueue, allowCommittedPathReceipt, capability, acceptedBackground, prepareGeneratedData, includeBuiltinGeneratedData));
            if (QueueFailure != null)
            {
                throw QueueFailure;
            }
            await QueueTask.ConfigureAwait(false);
            return true;
        }



        public Task SyncExternalFolderRowsForCustomFolderOutputBaseChangeAsync(string reason, LibraryFileMutationCapability? capability = null, LibraryFileMutationCapability? playlistCapability = null)
        {
            Events.Add("external:" + reason);
            return ExternalSyncTask;
        }

    }

    private sealed record QueueCall(
        string Reason,
        bool Force,
        bool AllowIncompleteToQueue,
        bool AllowCommittedPathReceipt,
        LibraryFileMutationCapability? Capability,
        bool AcceptedBackground,
        bool PrepareGeneratedData,
        bool IncludeBuiltinGeneratedData);
}
