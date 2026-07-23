using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DuplicateMaintenanceWorkflowOwnerTests
{
    [TestMethod]
    public async Task MergeFolderAsync_PreservesMutationBoundaryAndReceipt()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        var owner = CreateOwner(events, presentation, AcceptedDialogs(), store);
        var request = new DuplicateFolderMergeRequest(
            @"C:\\Songs\\Source",
            @"C:\\Songs\\Destination",
            [@"C:\\Songs\\Source", @"C:\\Songs\\Destination"],
            "Group",
            "Next group");

        DuplicateMaintenanceConfirmationResult confirmation = owner.ConfirmFolderMerge(request);
        Assert.IsTrue(confirmation.Accepted);
        DuplicateMaintenanceMutationResult result = await owner.MergeFolderAsync(confirmation.Operation);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Next group", result.SelectionHeader);
        Assert.AreEqual(request.SourceDirectory, store.SourceDirectory);
        Assert.AreEqual(request.DestinationDirectory, store.DestinationDirectory);
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
    public void ConfirmFolderMerge_RejectsFolderOutsideGroup()
    {
        var owner = CreateOwner([], new RecordingPresentation([]), AcceptedDialogs(), new RecordingStore([]));
        var request = new DuplicateFolderMergeRequest(
            @"C:\\Songs\\Source",
            @"C:\\Songs\\Destination",
            [@"C:\\Songs\\Source", @"C:\\Songs\\Other"],
            "Group",
            null);

        Assert.ThrowsException<ArgumentException>(() => owner.ConfirmFolderMerge(request));
    }

    [TestMethod]
    public void ConfirmHashCleanup_ChoosesShortestNameWhenWriteTimesMatch()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateMaintenance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ChartFile keeper = CreateChart(Path.Combine(root, "a.bms"), "same-hash");
            ChartFile duplicate = CreateChart(Path.Combine(root, "long-name.bms"), "same-hash");
            var dialogs = AcceptedDialogs();
            var owner = CreateOwner([], new RecordingPresentation([]), dialogs, new RecordingStore([]));
            var request = new DuplicateHashCleanupRequest(root, [keeper, duplicate], "Next group");

            DuplicateHashCleanupConfirmationResult confirmation = owner.ConfirmHashCleanup(request);

            Assert.IsTrue(confirmation.Accepted);
            Assert.IsTrue(confirmation.HasWork);
            CollectionAssert.AreEqual(new[] { duplicate }, (System.Collections.ICollection)confirmation.Plan.ChartsToRemove);
            StringAssert.Contains(dialogs.ConfirmationRequest.MessageBoxText, "1");
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
    public async Task CleanupHashAsync_UsesChartSnapshotAndSelectionReceipt()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        var owner = CreateOwner(events, presentation, AcceptedDialogs(), store);
        ChartFile chart = CreateChart(@"C:\\Songs\\a.bms", "hash");
        ChartFile duplicate = CreateChart(@"C:\\Songs\\long-name.bms", "hash");
        DuplicateHashCleanupConfirmationResult confirmation = owner.ConfirmHashCleanup(
            new DuplicateHashCleanupRequest(@"C:\\Songs", [chart, duplicate], "Next group"));

        DuplicateMaintenanceMutationResult result = await owner.CleanupHashAsync(confirmation.Operation);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Next group", result.SelectionHeader);
        CollectionAssert.AreEqual(new[] { duplicate }, (System.Collections.ICollection)store.Charts);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "stop-charts",
                "suppression-start",
                "store-remove",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task MergeFolderAsync_ObserverCleanupFailureIsAggregatedAfterPriorityRelease()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events)
        {
            EndActivityFailure = new InvalidOperationException("activity cleanup failed")
        };
        var owner = CreateOwner(events, presentation, AcceptedDialogs(), store);
        var request = new DuplicateFolderMergeRequest(
            @"C:\\Songs\\Source",
            @"C:\\Songs\\Destination",
            [@"C:\\Songs\\Source", @"C:\\Songs\\Destination"],
            "Group",
            "Next group");

        DuplicateMaintenanceConfirmationResult confirmation = owner.ConfirmFolderMerge(request);
        DuplicateMaintenanceMutationResult result = await owner.MergeFolderAsync(confirmation.Operation);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Failure.Message, "activity cleanup failed");
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
    public void ConfirmHashCleanup_RejectionDoesNotCreateMutationPlanForExecution()
    {
        ChartFile first = CreateChart(@"C:\\Songs\\a.bms", "hash");
        ChartFile second = CreateChart(@"C:\\Songs\\long-name.bms", "hash");
        var owner = CreateOwner(
            [],
            new RecordingPresentation([]),
            new FakeUiDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel) },
            new RecordingStore([]));

        DuplicateHashCleanupConfirmationResult confirmation = owner.ConfirmHashCleanup(
            new DuplicateHashCleanupRequest(@"C:\\Songs", [first, second], null));

        Assert.IsFalse(confirmation.Accepted);
        Assert.IsTrue(confirmation.HasWork);
        Assert.IsNotNull(confirmation.Plan);
        Assert.IsNull(confirmation.Operation);
    }

    [TestMethod]
    public void DuplicateFolderInteractionQueries_PreserveDestinationAndKeyboardPolicy()
    {
        var owner = CreateOwner([], new RecordingPresentation([]), AcceptedDialogs(), new RecordingStore([]));
        var group = new DuplicateGroup([], [@"C:\\A", @"C:\\B", @"C:\\C"]);

        CollectionAssert.AreEqual(
            new[] { @"C:\\B", @"C:\\C" },
            (System.Collections.ICollection)owner.CaptureDuplicateFolderMergeDestinations(group, @"C:\\A"));
        DuplicateFolderKeyboardAction menuAction = owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\\A");
        Assert.AreEqual(DuplicateFolderKeyboardActionKind.OpenContextMenu, menuAction.Kind);

        group.Folders = [@"C:\\A", @"C:\\B"];
        DuplicateFolderKeyboardAction mergeAction = owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\\A");
        Assert.AreEqual(DuplicateFolderKeyboardActionKind.Merge, mergeAction.Kind);
        Assert.AreEqual(@"C:\\B", mergeAction.DestinationPath);

        group.Folders = [@"C:\\A"];
        Assert.AreEqual(
            DuplicateFolderKeyboardActionKind.Cleanup,
            owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\\A").Kind);
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
            directoryExists: path => path == @"C:\\Existing",
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

        owner.OpenDuplicateFolderInExplorer(@"C:\\Missing");
        owner.OpenDuplicateFolderInExplorer(@"C:\\Existing");

        Assert.AreEqual(1, openCount);
    }

    private static DuplicateMaintenanceWorkflowOwner CreateOwner(
        List<string> events,
        RecordingPresentation presentation,
        IUiDialogService dialogs,
        RecordingStore store,
        Func<string, bool>? directoryExists = null,
        Func<string, ExplorerOpenResult>? explorerOpen = null)
    {
        var owner = new DuplicateMaintenanceWorkflowOwner(
            CreateLibrary,
            new ChartFileOperationSynchronizer(),
            presentation,
            dialogs,
            () => true,
            directoryExists ?? (_ => true),
            explorerOpen ?? (_ => new ExplorerOpenResult()),
            store);
        owner.WorkflowChanged += presentation.OnWorkflowChanged;
        return owner;
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
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

        internal void OnWorkflowChanged(
            object sender,
            DuplicateMaintenanceWorkflowChangedEventArgs e)
        {
            switch (e)
            {
                case DuplicateMaintenanceActivityChangedEventArgs activityChanged:
                    events.Add(activityChanged.IsActive ? "activity-start" : "activity-end");
                    if (!activityChanged.IsActive && EndActivityFailure != null)
                    {
                        throw EndActivityFailure;
                    }
                    break;
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

    private sealed class RecordingStore : IDuplicateMaintenanceStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal string SourceDirectory { get; private set; } = string.Empty;

        internal string DestinationDirectory { get; private set; } = string.Empty;

        internal IReadOnlyList<ChartFile> Charts { get; private set; } = [];

        public void MergeFolder(BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId)
        {
            events.Add("store-merge");
            SourceDirectory = sourceDirectory;
            DestinationDirectory = destinationDirectory;
        }

        public void RemoveCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts)
        {
            events.Add("store-remove");
            Charts = charts;
        }
    }

    private sealed class FakeUiDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } = null!;

        internal UiConfirmationRequest ConfirmationRequest { get; private set; } = null!;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
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
