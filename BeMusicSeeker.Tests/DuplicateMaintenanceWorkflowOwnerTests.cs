using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
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

    private static DuplicateMaintenanceWorkflowOwner CreateOwner(
        List<string> events,
        RecordingPresentation presentation,
        IUiDialogService dialogs,
        RecordingStore store)
    {
        return new DuplicateMaintenanceWorkflowOwner(
            CreateLibrary,
            new ChartFileOperationSynchronizer(),
            presentation,
            presentation,
            presentation,
            dialogs,
            () => true,
            store);
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

    private sealed class RecordingPresentation : IDuplicateMaintenanceActivityPort, IDuplicateMaintenanceRefreshPort, IDuplicateMaintenancePlaybackPort
    {
        private readonly List<string> events;

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        public void BeginActivity() => events.Add("activity-start");

        public void BeginRefreshSuppression() => events.Add("suppression-start");

        public void EndRefreshSuppression() => events.Add("suppression-end");

        public void EndActivity() => events.Add("activity-end");

        public void StopPlaybackForMerge() => events.Add("stop-merge");

        public void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts) => events.Add("stop-charts");

        public void BeginRefreshPriorityWindow(string reason) => events.Add("priority-start:" + reason);

        public void ScheduleRefreshPriorityWindowRelease(string reason) => events.Add("priority-release:" + reason);
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
