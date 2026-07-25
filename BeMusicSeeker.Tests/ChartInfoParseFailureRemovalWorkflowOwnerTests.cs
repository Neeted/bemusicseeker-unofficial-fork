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

    [TestMethod]
    public async Task RemoveAsync_AcceptsConfirmationAndWaitsForBackgroundStore()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
            // Acceptance completion and the background scheduling continuation are independent
            // asynchronous phases; use the store gate below to observe their deterministic order.
            await storeStarted.Task;
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
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RemoveAsync_UserRejectionDoesNotScheduleOrStore()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

    [TestMethod]
    public async Task RemoveAsync_DialogFailureIsExplicitAndDoesNotStore()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

    [TestMethod]
    public async Task RemoveAsync_AcceptedButLibraryUnavailableIsFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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

    [TestMethod]
    public async Task RemoveAsync_StoreFailureIsPropagatedAsAcceptedFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
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
        using (var database = new LR2SongDBExtended(songDbPath))
        {
        }
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

        public void Remove(BMSLibrary library, IReadOnlyList<string> md5s)
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
            throw new NotSupportedException();

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
