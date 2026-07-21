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
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SelectedChartMutationWorkflowOwnerTests
{
    [TestMethod]
    public void DeletePendingAsync_RejectedDialogDoesNotMutate()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        var dialogs = new FakeUiDialogService
        {
            PendingDeleteResult = new UiWindowDialogResult<bool>(UiDialogStatus.CancelledByUser)
        };
        var owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget target = CreateTarget("pending.bms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.UpdateInstallDestination);

        SelectedChartDeleteConfirmationResult confirmation = owner.ConfirmDelete(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.InstallPending));

        Assert.IsFalse(confirmation.Accepted);
        Assert.IsNull(confirmation.Operation);
        Assert.AreEqual(0, store.PendingDeleteCalls);
    }

    [TestMethod]
    public async Task DeletePendingAsync_AcceptedPreservesFolderCheckboxAndOperationOrder()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        var dialogs = new FakeUiDialogService
        {
            PendingDeleteResult = new UiWindowDialogResult<bool>(UiDialogStatus.Accepted, true, true)
        };
        var owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget target = CreateTarget("pending.bms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.UpdateInstallDestination);

        SelectedChartDeleteConfirmationResult confirmation = owner.ConfirmDelete(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.InstallPending));
        SelectedChartMutationResult result = await owner.DeleteAsync(confirmation.Operation);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(store.DeleteContainingPackageFoldersWhenNoBms);
        Assert.AreEqual(
            5,
            presentation.Events.Count,
            string.Join("|", presentation.Events));
        CollectionAssert.AreEqual(
            new[] { "activity-start", "pending-playback", "pending-refresh-start", "pending-refresh-end", "activity-end" },
            presentation.Events);
    }

    [TestMethod]
    public async Task DeleteLibraryAsync_FiltersScopeAndPassesApprovedWholeFolders()
    {
        var store = new RecordingStore { WholeFolderDeletePaths = [@"C:\Songs\Folder"] };
        var presentation = new RecordingPresentation();
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
            ])
        };
        var owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget libraryTarget = CreateTarget("library.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RemoveFromLibrary);
        ChartOperationTarget pendingTarget = CreateTarget("pending.bms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.UpdateInstallDestination);

        SelectedChartDeleteConfirmationResult confirmation = owner.ConfirmDelete(
            new SelectedChartDeleteRequest([libraryTarget, pendingTarget], libraryTarget, MainViewOperationSection.Library));
        SelectedChartMutationResult result = await owner.DeleteAsync(confirmation.Operation);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, store.LibraryDeleteCalls);
        CollectionAssert.AreEqual(store.WholeFolderDeletePaths.ToArray(), store.ApprovedWholeFolderDeletePaths.ToArray());
        Assert.AreEqual("library.bms", Path.GetFileName(store.LibraryCharts[0].Path));
    }

    [TestMethod]
    public async Task DeleteLibraryAsync_StopsSelectedChartsAndApprovedDirectoriesBeforeStoreWrite()
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { WholeFolderDeletePaths = [@"C:\Songs\Folder"] };
        var presentation = new RecordingPresentation(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
            ])
        };
        var owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget target = CreateTarget("library.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RemoveFromLibrary);

        SelectedChartDeleteConfirmationResult confirmation = owner.ConfirmDelete(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));
        SelectedChartMutationResult result = await owner.DeleteAsync(confirmation.Operation);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(7, events.Count, string.Join("|", events));
        CollectionAssert.AreEqual(
            new[] { "activity-start", "library-playback", "library-directories", "library-refresh-start", "store-library-delete", "library-refresh-end", "activity-end" },
            events);
    }

    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_SplitsBmsAndPmsAndPreservesRouteOrder()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        var dialogs = AcceptedMessageDialogs();
        var owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget bChart = CreateTarget("alpha.bme", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RenameInvalidExtension);
        ChartOperationTarget pChart = CreateTarget("beta.pms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RenameInvalidExtension);

        SelectedInvalidExtensionRenameConfirmationResult confirmation = owner.ConfirmRenameInvalidExtensions(
            new SelectedInvalidExtensionRenameRequest([bChart, pChart], isPendingSelected: false));
        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(confirmation.Operation);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new[] { "library:.bmx", "library:.pmx" }, store.RenameOperations);
        Assert.AreEqual(2, store.RenameCallCount);
    }

    [TestMethod]
    public async Task MoveAsync_AcceptedUsesSelectedLibraryRefsAndPublishesPathRefresh()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        var owner = CreateOwner(presentation, AcceptedMessageDialogs(), store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary);

        SelectedChartMoveConfirmationResult confirmation = owner.ConfirmMove(
            new SelectedChartMoveRequest([target], @"D:\Moved"));
        SelectedChartMutationResult result = await owner.MoveAsync(confirmation.Operation);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(@"D:\Moved", store.MovedDirectory);
        Assert.AreEqual(1, presentation.PathRefreshCalls);
    }

    [TestMethod]
    public async Task MutationFailure_ReleasesSuppressionAndActivity()
    {
        var store = new RecordingStore { Failure = new IOException("mutation failed") };
        var presentation = new RecordingPresentation();
        var owner = CreateOwner(presentation, AcceptedMessageDialogs(), store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary);

        SelectedChartMoveConfirmationResult confirmation = owner.ConfirmMove(
            new SelectedChartMoveRequest([target], @"D:\Moved"));
        SelectedChartMutationResult result = await owner.MoveAsync(confirmation.Operation);

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "library-playback", "library-refresh-start", "library-refresh-end", "activity-end" },
            presentation.Events);
    }

    [TestMethod]
    public void DialogFailure_DoesNotFallbackToMutation()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        var owner = CreateOwner(presentation, new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary);

        SelectedChartMoveConfirmationResult confirmation = owner.ConfirmMove(
            new SelectedChartMoveRequest([target], @"D:\Moved"));

        Assert.IsFalse(confirmation.Accepted);
        Assert.IsNotNull(confirmation.Failure);
        Assert.IsNull(confirmation.Operation);
        Assert.AreEqual(string.Empty, store.MovedDirectory);
    }

    private static SelectedChartMutationWorkflowOwner CreateOwner(
        RecordingPresentation presentation,
        FakeUiDialogService dialogs,
        RecordingStore store)
    {
        return new SelectedChartMutationWorkflowOwner(
            () => (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary)),
            new ChartFileOperationSynchronizer(),
            presentation,
            presentation,
            presentation,
            dialogs,
            store);
    }

    private static FakeUiDialogService AcceptedMessageDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            ])
        };
    }

    private static ChartOperationTarget CreateTarget(
        string fileName,
        ChartOperationSourceScope sourceScope,
        bool isPending,
        ChartOperationCapabilities capabilities)
    {
        return new ChartOperationTarget(
            CreateChart(Path.Combine(@"C:\Songs", fileName)),
            null,
            sourceScope,
            isOwned: true,
            isPending,
            isPlaylistMissing: false,
            capabilities);
    }

    private static ChartFile CreateChart(string path)
    {
        var file = new BMSFile
        {
            path = path
        };
        return new ChartFile(
            ChartFileKind.Bms,
            path,
            "hash-" + Path.GetFileNameWithoutExtension(path),
            null,
            "Title",
            "Title",
            "Artist",
            "Genre",
            "Folder",
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            file,
            null);
    }

    private sealed class RecordingPresentation :
        ISelectedChartMutationActivityPort,
        ISelectedChartMutationRefreshPort,
        ISelectedChartMutationPlaybackPort
    {
        internal List<string> Events { get; }

        internal RecordingPresentation(List<string>? events = null)
        {
            Events = events ?? [];
        }

        internal int PathRefreshCalls { get; private set; }

        public void BeginActivity() => Events.Add("activity-start");

        public void EndActivity() => Events.Add("activity-end");

        public void BeginRefreshSuppression(SelectedChartMutationRefreshScope scope) => Events.Add(scope == SelectedChartMutationRefreshScope.Pending ? "pending-refresh-start" : "library-refresh-start");

        public void EndRefreshSuppression() => Events.Add(Events.Contains("pending-refresh-start") && !Events.Contains("library-refresh-end") ? "pending-refresh-end" : "library-refresh-end");

        public void ApplyLibraryPathMutationRefresh() => PathRefreshCalls++;

        public void StopPlaybackForPendingCharts(IReadOnlyList<ChartFile> charts) => Events.Add("pending-playback");

        public void StopPlaybackForLibraryCharts(IReadOnlyList<LibraryChartRef> charts) => Events.Add("library-playback");

        public void StopPlaybackForChartDirectories(IReadOnlyList<string> directories)
        {
            Events.Add("library-directories");
        }
    }

    private sealed class RecordingStore : ISelectedChartMutationStore
    {
        private readonly List<string>? events;

        internal RecordingStore(List<string>? events = null)
        {
            this.events = events;
        }

        internal IReadOnlyList<string> WholeFolderDeletePaths { get; set; } = [];

        internal IReadOnlyList<string> ApprovedWholeFolderDeletePaths { get; private set; } = [];

        internal IReadOnlyList<LibraryChartRef> LibraryCharts { get; private set; } = [];

        internal int LibraryDeleteCalls { get; private set; }

        internal int PendingDeleteCalls { get; private set; }

        internal bool DeleteContainingPackageFoldersWhenNoBms { get; private set; }

        internal List<string> RenameOperations { get; } = [];

        internal int RenameCallCount { get; private set; }

        internal string MovedDirectory { get; private set; } = string.Empty;

        internal Exception Failure { get; set; } = null!;

        public IReadOnlyList<string> GetLibraryWholeFolderDeleteConfirmationPaths(BMSLibrary library, IReadOnlyList<LibraryChartRef> charts) => WholeFolderDeletePaths;

        public void RemoveLibraryCharts(BMSLibrary library, IReadOnlyList<LibraryChartRef> charts, IReadOnlyList<string> approvedWholeFolderDeletePaths)
        {
            ThrowIfConfigured();
            LibraryDeleteCalls++;
            events?.Add("store-library-delete");
            LibraryCharts = charts;
            ApprovedWholeFolderDeletePaths = approvedWholeFolderDeletePaths;
        }

        public void RemovePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, bool sendToRecycleBin, bool deleteContainingPackageFoldersWhenNoBms)
        {
            ThrowIfConfigured();
            PendingDeleteCalls++;
            events?.Add("store-pending-delete");
            DeleteContainingPackageFoldersWhenNoBms = deleteContainingPackageFoldersWhenNoBms;
        }

        public void RenameLibraryCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension)
        {
            ThrowIfConfigured();
            events?.Add("store-library-rename");
            RenameOperations.Add("library:" + newExtension);
            RenameCallCount++;
        }

        public void RenamePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension)
        {
            ThrowIfConfigured();
            events?.Add("store-pending-rename");
            RenameOperations.Add("pending:" + newExtension);
            RenameCallCount++;
        }

        public void MoveLibraryCharts(BMSLibrary library, ChartLibraryMoveRequest request)
        {
            ThrowIfConfigured();
            events?.Add("store-move");
            MovedDirectory = request.NewParentDirectory;
        }

        private void ThrowIfConfigured()
        {
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

    private sealed class FakeUiDialogService : IUiDialogService
    {
        internal Queue<UiDialogResult> ConfirmationResults { get; set; } = new();

        internal UiWindowDialogResult<bool> PendingDeleteResult { get; set; } = null!;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            if (ConfirmationResults.Count == 0)
            {
                throw new InvalidOperationException("No confirmation result configured.");
            }
            return Task.FromResult(ConfirmationResults.Dequeue());
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            return Task.FromResult((UiWindowDialogResult<TResult>)(object)PendingDeleteResult);
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
