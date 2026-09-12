using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DuplicateMaintenanceWorkflowOwnerTests
{
    [TestMethod]
    public async Task RunFolderMergeAsync_PreservesMutationBoundaryAndReceipt()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        var owner = CreateOwner(
            events,
            presentation,
            AcceptedDialogs(),
            store,
            duplicateGroupNextHeaderProvider: _ => "Next group");
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, destination]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Next group", result.SelectionHeader);
        Assert.AreEqual(source, store.SourceDirectory);
        Assert.AreEqual(destination, store.DestinationDirectory);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "stop-merge",
                "suppression-start",
                "priority-start:merge_folder",
                "store-merge",
                "suppression-end",
                "priority-release:merge_folder",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task RunFolderMergeAsync_DurableFinalizationFailurePublishesFailureWithoutSuccess()
    {
        var events = new List<string>();
        var finalizationFailure = new IOException("merge finalization failed");
        var mutationReceipt = new FileDbMutationReceipt(
            Guid.NewGuid(),
            FileDbMutationTerminalState.DurableFinalizationFailed,
            durableCommit: true,
            compensationAttemptCount: 0,
            cleanupAttemptCount: 0,
            sourcePaths: [@"C:\Songs\Source"],
            destinationPaths: [@"C:\Songs\Destination\chart.bms"],
            stagingPaths: [],
            backupPaths: [],
            recoveryPaths: [],
            failure: finalizationFailure,
            finalizationFailure: finalizationFailure);
        var mergeReceipt = new DuplicateMergeMaintenanceReceipt(
            mergeApplied: false,
            intermediateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
            maintenanceResult: MaintenanceWorkflowResultFacts.From(null),
            intermediateDeferred: false,
            maintenanceHadUpdates: false,
            resourceHealthIndexDeferred: false,
            resourceHealthIndexDeltaApplied: false,
            resourceHealthIndexFullRebuilt: false,
            mutationReceipt);
        var store = new TerminalRecordingStore(events, mergeReceipt);
        var owner = CreateOwner(
            events,
            new RecordingPresentation(events),
            AcceptedDialogs(),
            store);
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, destination]));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsTrue(result.HasDurableFinalizationFailure);
        Assert.AreSame(finalizationFailure, result.Failure);
        Assert.AreSame(mergeReceipt, result.MutationReceipt);
        Assert.AreEqual(1, store.MergeWithReceiptCallCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "stop-merge",
                "suppression-start",
                "priority-start:merge_folder",
                "store-merge-with-receipt",
                "suppression-end",
                "priority-release:merge_folder",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task RunFolderMergeAsync_AwaitsConfirmationWithoutBlockingCaller()
    {
        var confirmation = new TaskCompletionSource<UiDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FakeUiDialogService { PendingConfirmation = confirmation };
        var owner = CreateOwner(
            [],
            new RecordingPresentation([]),
            dialogs,
            new RecordingStore([]));
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        Task<DuplicateMaintenanceMutationResult> resultTask = owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, destination]));

        Assert.IsFalse(resultTask.IsCompleted);
        confirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        DuplicateMaintenanceMutationResult result = await resultTask;

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(dialogs.ConfirmationRequest);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunFolderMergeAsync_ReportsAfterReleaseAndPreservesFactsWhenReporterFails(bool reporterThrows)
    {
        var events = new List<string>();
        var mutation = FileDbMutationReportTests.Receipt(FileDbMutationTerminalState.CompletedWithCleanupFailure);
        var receipt = new DuplicateMergeMaintenanceReceipt(true, ResourceHealthIndexUpdateMode.DeferOnUpdates,
            MaintenanceWorkflowResultFacts.From(null), false, false, false, false, false, mutation);
        var store = new TerminalRecordingStore(events, receipt);
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        bool reportAfterRelease = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            OnMessage = () =>
            {
                bool released = gate.TryEnter(out IDisposable lease);
                reportAfterRelease = released && !activity.IsActive && events.Contains("priority-release:merge_folder");
                lease?.Dispose();
            },
            MessageFailure = reporterThrows ? new IOException("report failed") : null
        };
        var owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store, gate: gate, activity: activity);
        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(@"C:\Source", @"D:\Destination",
            new DuplicateGroup([], [@"C:\Source", @"D:\Destination"]));
        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreSame(receipt, result.MutationReceipt);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(MessageBoxImage.Warning, dialogs.Messages[0].Icon);
        Assert.IsTrue(reportAfterRelease);
        Assert.AreEqual(1, store.MergeWithReceiptCallCount);
    }

    [TestMethod]
    public void RunFolderMergeAsync_RejectsFolderOutsideGroup()
    {
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";
        var owner = CreateOwner([], new RecordingPresentation([]), AcceptedDialogs(), new RecordingStore([]));

        Assert.ThrowsException<ArgumentException>(() => owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, @"C:\Songs\Other"])));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunHashCleanupAsync_ChoosesShortestNameAndReturnsRemovalCount(bool catalogFailure)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_DuplicateMaintenance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ChartFile keeper = CreateChart(Path.Combine(root, "a.bms"), "same-hash");
            ChartFile duplicate = CreateChart(Path.Combine(root, "long-name.bms"), "same-hash");
            ChartFile missing = CreateChart(Path.Combine(root, "missing-long-name.bms"), "same-hash");
            var outcome = new LibraryChartRemovalOutcome([
                new(duplicate.Path, LibraryChartRemovalState.Confirmed),
                new(missing.Path, LibraryChartRemovalState.NotExecuted)], true, true,
                catalogFailure ? new IOException("required catalog finalization failure") : null);
            var store = new RecordingStore([]) { RemovalOutcome = outcome };
            var dialogs = AcceptedDialogs();
            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            bool releasedAtReport = false;
            dialogs.OnMessage = () =>
            {
                releasedAtReport = gate.TryEnter(out IDisposable lease) && !activity.IsActive;
                lease?.Dispose();
            };
            var owner = CreateOwner(
                [],
                new RecordingPresentation([]),
                dialogs,
                store,
                duplicateGroupNextHeaderProvider: _ => "Next group", gate: gate, activity: activity);

            DuplicateMaintenanceMutationResult result = await owner.RunHashCleanupAsync(
                new DuplicateGroup([keeper, duplicate, missing], [root]),
                root);

            Assert.AreEqual(!catalogFailure, result.Succeeded);
            Assert.AreSame(outcome.CatalogFailure, result.Failure);
            Assert.AreEqual(catalogFailure, result.HasDurableFinalizationFailure);
            Assert.AreEqual(1, result.RemovedChartCount);
            Assert.AreEqual("Next group", result.SelectionHeader);
            CollectionAssert.AreEqual(new[] { duplicate, missing }, (System.Collections.ICollection)store.Charts);
            Assert.IsNotNull(dialogs.ConfirmationRequest);
            StringAssert.Contains(dialogs.ConfirmationRequest!.MessageBoxText, "2");
            Assert.IsTrue(releasedAtReport);
            Assert.AreSame(outcome, result.RemovalOutcome);
            Assert.AreEqual(1, dialogs.Messages.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunHashCleanupAsync_NoWorkDoesNotStartMutation()
    {
        var events = new List<string>();
        var dialogs = AcceptedDialogs();
        var owner = CreateOwner(
            events,
            new RecordingPresentation(events),
            dialogs,
            new RecordingStore(events));
        ChartFile chart = CreateChart(@"C:\Songs\a.bms", "hash");

        DuplicateMaintenanceMutationResult result = await owner.RunHashCleanupAsync(
            new DuplicateGroup([chart], [@"C:\Songs"]),
            @"C:\Songs");

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(0, result.RemovedChartCount);
        Assert.IsNull(dialogs.ConfirmationRequest);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task RunHashCleanupAsync_RejectionDoesNotStartMutation()
    {
        var events = new List<string>();
        var owner = CreateOwner(
            events,
            new RecordingPresentation(events),
            new FakeUiDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            },
            new RecordingStore(events));
        ChartFile first = CreateChart(@"C:\Songs\a.bms", "hash");
        ChartFile second = CreateChart(@"C:\Songs\long-name.bms", "hash");

        DuplicateMaintenanceMutationResult result = await owner.RunHashCleanupAsync(
            new DuplicateGroup([first, second], [@"C:\Songs"]),
            @"C:\Songs");

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task RunFolderMergeAsync_TerminalObserverCleanupFailureIsOptionalAfterPriorityRelease()
    {
        var events = new List<string>();
        var receipt = new DuplicateMergeMaintenanceReceipt(true, ResourceHealthIndexUpdateMode.DeferOnUpdates,
            MaintenanceWorkflowResultFacts.From(null), false, false, false, false, false,
            FileDbMutationReportTests.Receipt(FileDbMutationTerminalState.Completed));
        var store = new TerminalRecordingStore(events, receipt);
        var dialogs = new FileDbReportRecordingDialogs();
        var presentation = new RecordingPresentation(events)
        {
            EndActivityFailure = new InvalidOperationException("activity cleanup failed")
        };
        var owner = CreateOwner(
            events,
            presentation,
            dialogs,
            store,
            duplicateGroupNextHeaderProvider: _ => "Next group");
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, destination]));

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreSame(receipt, result.MutationReceipt);
        Assert.AreEqual(0, dialogs.Messages.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "stop-merge",
                "suppression-start",
                "priority-start:merge_folder",
                "store-merge-with-receipt",
                "suppression-end",
                "priority-release:merge_folder",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public void DuplicateFolderInteractionQueries_PreserveDestinationAndKeyboardPolicy()
    {
        var owner = CreateOwner([], new RecordingPresentation([]), AcceptedDialogs(), new RecordingStore([]));
        var group = new DuplicateGroup([], [@"C:\A", @"C:\B", @"C:\C"]);

        CollectionAssert.AreEqual(
            new[] { @"C:\B", @"C:\C" },
            (System.Collections.ICollection)owner.CaptureDuplicateFolderMergeDestinations(group, @"C:\A"));
        DuplicateFolderKeyboardAction menuAction = owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\A");
        Assert.AreEqual(DuplicateFolderKeyboardActionKind.OpenContextMenu, menuAction.Kind);

        group.Folders = [@"C:\A", @"C:\B"];
        DuplicateFolderKeyboardAction mergeAction = owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\A");
        Assert.AreEqual(DuplicateFolderKeyboardActionKind.Merge, mergeAction.Kind);
        Assert.AreEqual(@"C:\B", mergeAction.DestinationPath);

        group.Folders = [@"C:\A"];
        Assert.AreEqual(
            DuplicateFolderKeyboardActionKind.Cleanup,
            owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\A").Kind);
    }

    [TestMethod]
    public void OpenDuplicateFolderInExplorer_ValidatesBeforeOpeningWithoutFallback()
    {
        int openCount = 0;
        var owner = CreateOwner(
            [],
            new RecordingPresentation([]),
            AcceptedDialogs(),
            new RecordingStore([]),
            directoryExists: path => path == @"C:\Existing",
            explorerOpen: path =>
            {
                openCount++;
                return new ExplorerOpenResult
                {
                    Kind = ExplorerOpenResultKind.Failed,
                    RequestedPath = path,
                    FailureReason = "shell_failed"
                };
            });

        owner.OpenDuplicateFolderInExplorer(@"C:\Missing");
        owner.OpenDuplicateFolderInExplorer(@"C:\Existing");

        Assert.AreEqual(1, openCount);
    }

    private static DuplicateMaintenanceWorkflowOwner CreateOwner(
        List<string> events,
        RecordingPresentation presentation,
        IUiDialogService dialogs,
        RecordingStore store,
        Func<string, bool>? directoryExists = null,
        Func<string, ExplorerOpenResult>? explorerOpen = null,
        Func<DuplicateGroup, string>? duplicateGroupNextHeaderProvider = null,
        bool showConfirmation = true,
        ChartFileOperationSynchronizer? gate = null,
        ChartMutationActivityOwner? activity = null)
    {
        activity ??= new();
        var owner = new DuplicateMaintenanceWorkflowOwner(
            CreateLibrary,
            gate ?? new ChartFileOperationSynchronizer(),
            activity,
            presentation,
            dialogs,
            () => showConfirmation,
            directoryExists ?? (_ => true),
            explorerOpen ?? (_ => new ExplorerOpenResult()),
            duplicateGroupNextHeaderProvider ?? (_ => (string)null!),
            store);
        owner.WorkflowChanged += presentation.OnWorkflowChanged;
        activity.ActivityChanged += presentation.OnActivityChanged;
        return owner;
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
    }

    private static ChartFile CreateChart(string path, string hash)
    {
        return new ChartFile(
            ChartFileKind.Bms,
            path,
            hash,
            null,
            "Duplicate",
            "Duplicate",
            "Artist",
            "Genre",
            "Folder",
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            null,
            null);
    }

    private static FakeUiDialogService AcceptedDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
    }

    private sealed class RecordingPresentation : IDuplicateMaintenancePlaybackPort
    {
        private readonly List<string> events;

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        internal void OnActivityChanged(object? sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender!;
            events.Add(activity.IsActive ? "activity-start" : "activity-end");
            if (!activity.IsActive && EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
        }

        internal void OnWorkflowChanged(
            object? sender,
            DuplicateMaintenanceWorkflowChangedEventArgs e)
        {
            switch (e)
            {
                case DuplicateMaintenanceRefreshSuppressionChangedEventArgs suppressionChanged:
                    events.Add(suppressionChanged.IsSuppressed ? "suppression-start" : "suppression-end");
                    break;
                case DuplicateMaintenanceRefreshPriorityWindowChangedEventArgs priorityChanged:
                    events.Add(
                        (priorityChanged.IsActive ? "priority-start:" : "priority-release:")
                        + priorityChanged.Reason);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(e),
                        e,
                        "Unsupported duplicate-maintenance workflow change.");
            }
        }

        public void StopPlaybackForMerge() => events.Add("stop-merge");

        public void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts) => events.Add("stop-charts");
    }

    private class RecordingStore : IDuplicateMaintenanceStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal string SourceDirectory { get; private set; } = string.Empty;

        internal string DestinationDirectory { get; private set; } = string.Empty;

        internal IReadOnlyList<ChartFile> Charts { get; private set; } = [];

        protected void AddEvent(string value) => events.Add(value);

        public void MergeFolder(BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId)
        {
            events.Add("store-merge");
            SourceDirectory = sourceDirectory;
            DestinationDirectory = destinationDirectory;
        }

        internal LibraryChartRemovalOutcome RemovalOutcome { get; set; } = null!;

        public LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts)
        {
            events.Add("store-remove");
            Charts = charts;
            return RemovalOutcome ?? new LibraryChartRemovalOutcome(charts.Select(chart => new LibraryChartRemovalTarget(chart.Path, LibraryChartRemovalState.Confirmed)), true, true);
        }
    }

    private sealed class TerminalRecordingStore : RecordingStore, IDuplicateMaintenanceTerminalStore
    {
        private readonly DuplicateMergeMaintenanceReceipt receipt;

        internal TerminalRecordingStore(
            List<string> events,
            DuplicateMergeMaintenanceReceipt receipt)
            : base(events)
        {
            this.receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        }

        internal int MergeWithReceiptCallCount { get; private set; }

        public DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
            BMSLibrary library,
            string sourceDirectory,
            string destinationDirectory,
            long operationId)
        {
            MergeWithReceiptCallCount++;
            AddEvent("store-merge-with-receipt");
            return receipt;
        }
    }

    private sealed class FakeUiDialogService : IUiDialogService
    {
        internal UiDialogResult? ConfirmationResult { get; set; }

        internal TaskCompletionSource<UiDialogResult>? PendingConfirmation { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        internal List<UiMessageRequest> Messages { get; } = [];
        internal Action? OnMessage { get; set; }
        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            OnMessage?.Invoke();
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            if (PendingConfirmation != null)
            {
                return PendingConfirmation.Task;
            }
            return Task.FromResult(ConfirmationResult ?? throw new InvalidOperationException("Confirmation result was not configured."));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
