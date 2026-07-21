using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
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
public sealed class InstallDestinationWorkflowOwnerTests
{
    [TestMethod]
    public async Task SearchPendingAsync_AppliesMutationAndTerminalRefreshInOrder()
    {
        var events = new List<string>();
        var chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        PendingInstallDestinationSearchRequest request =
            PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([CreateTarget(chart)]);

        await owner.SearchPendingAsync(request);

        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-pending",
                "transient",
                "suppression-end",
                "activity-end",
                "invalidate",
                "identity-refresh"
            },
            events);
    }

    [TestMethod]
    public async Task SearchPendingAsync_MergeRejectionDoesNotStartMutation()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        PendingInstallDestinationSearchRequest request =
            PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch([CreateTarget(CreateChart())]);

        await owner.SearchPendingAsync(request);

        Assert.IsNotNull(dialogs.ConfirmationRequest);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Msg_estimate_merge_confirm, dialogs.ConfirmationRequest.MessageBoxText);
        Assert.AreEqual(0, store.SearchPendingCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task SearchPackagesAsync_MergeAcceptanceRunsMutation()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = AcceptedDialogs();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        var package = new ChartPackage();

        await owner.SearchPackagesAsync(PendingInstallDestinationSearchKind.MergeDestination, [package]);

        Assert.AreEqual(PendingInstallDestinationSearchKind.MergeDestination, store.LastSearchKind);
        CollectionAssert.AreEqual(new[] { package }, (System.Collections.ICollection)store.LastPackages);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-packages",
                "suppression-end",
                "activity-end",
                "invalidate",
                "identity-refresh"
            },
            events);
    }

    [TestMethod]
    public async Task SearchPendingAsync_DialogFailureIsNotTreatedAsRejection()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        PendingInstallDestinationSearchRequest request =
            PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch([CreateTarget(CreateChart())]);

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.SearchPendingAsync(request));

        Assert.AreSame(failure, exception.InnerException);
        Assert.AreEqual(0, store.SearchPendingCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task ClearPackagesAsync_WithoutAttachedLibraryStillClearsInMemoryState()
    {
        var events = new List<string>();
        var chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        var owner = CreateOwner(() => null!, events, store, AcceptedDialogs());

        await owner.ClearPackagesAsync([new ChartPackage()]);

        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-clear-packages",
                "transient",
                "suppression-end",
                "activity-end",
                "invalidate"
            },
            events);
    }

    [TestMethod]
    public async Task SearchCorrectAsync_ProjectsAndInvalidatesBeforeSuppressionEnds()
    {
        var events = new List<string>();
        var chart = CreateChart();
        var store = new RecordingStore(events) { ChangedCharts = [chart] };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(chart, ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.SearchCorrectAsync(request);

        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-search-correct",
                "transient",
                "invalidate",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task SetPendingAsync_RefreshesEditedRowAfterMutationBoundary()
    {
        var events = new List<string>();
        var chart = CreateChart();
        var store = new RecordingStore(events) { SetResult = chart };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(
            CreateTarget(chart),
            out PendingInstallDestinationEditRequest request));

        await owner.SetPendingAsync(request, "C:\\Installed");

        Assert.AreEqual("C:\\Installed", store.LastDestinationDirectory);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-set",
                "suppression-end",
                "activity-end",
                "transient",
                "invalidate",
                "display-refresh"
            },
            events);
    }

    [TestMethod]
    public async Task ClearPendingAsync_PropagatesFailureAndClosesMutationBoundary()
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { Failure = new InvalidOperationException("clear failed") };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate(
            [CreateTarget(CreateChart())],
            out PendingInstallDestinationClearRequest request));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.ClearPendingAsync(request));

        Assert.AreEqual("clear failed", exception.Message);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-clear-pending",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task SearchPackagesAsync_EndActivityFailureStillDetachesAndFlushesDialogScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(InstallDestinationWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var events = new List<string>();
            var dialogService = new RecordingLibraryDialogService();
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            var library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectory = Path.Combine(tempDirectory, "missing");
            var store = new RecordingStore(events)
            {
                SearchPackagesAction = activeLibrary => activeLibrary.RenameChartFolder(missingDirectory, "renamed")
            };
            var failure = new InvalidOperationException("activity end failed");
            var presentation = new RecordingPresentation(events) { EndActivityFailure = failure };
            var owner = new InstallDestinationWorkflowOwner(
                () => library,
                new ChartFileOperationSynchronizer(),
                presentation,
                AcceptedDialogs(),
                store);

            InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => owner.SearchPackagesAsync(
                    PendingInstallDestinationSearchKind.InstallDestination,
                    [new ChartPackage()]));

            Assert.AreSame(failure, exception);
            Assert.AreEqual(1, dialogService.CallCount);
            CollectionAssert.AreEqual(
                new[]
                {
                    "activity-start",
                    "suppression-start",
                    "store-search-packages",
                    "suppression-end",
                    "activity-end"
                },
                events);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SearchPackagesAsync_MultipleFailuresPreserveMutationAndEveryCleanupFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(InstallDestinationWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var mutationFailure = new InvalidOperationException("mutation failed");
            var suppressionFailure = new InvalidOperationException("suppression end failed");
            var activityFailure = new InvalidOperationException("activity end failed");
            var flushFailure = new InvalidOperationException("dialog flush failed");
            var events = new List<string>();
            var dialogService = new RecordingLibraryDialogService { Failure = flushFailure };
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            var library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectory = Path.Combine(tempDirectory, "missing");
            var store = new RecordingStore(events)
            {
                Failure = mutationFailure,
                SearchPackagesAction = activeLibrary => activeLibrary.RenameChartFolder(missingDirectory, "renamed")
            };
            var presentation = new RecordingPresentation(events)
            {
                EndRefreshSuppressionFailure = suppressionFailure,
                EndActivityFailure = activityFailure
            };
            var owner = new InstallDestinationWorkflowOwner(
                () => library,
                new ChartFileOperationSynchronizer(),
                presentation,
                AcceptedDialogs(),
                store);

            AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(
                () => owner.SearchPackagesAsync(
                    PendingInstallDestinationSearchKind.InstallDestination,
                    [new ChartPackage()]));

            CollectionAssert.AreEqual(
                new Exception[] { mutationFailure, suppressionFailure, activityFailure, flushFailure },
                exception.InnerExceptions);
            CollectionAssert.AreEqual(
                new[]
                {
                    "activity-start",
                    "suppression-start",
                    "store-search-packages",
                    "suppression-end",
                    "activity-end"
                },
                events);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SearchPackagesAsync_WaitsForSharedChartFileGate()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var owner = new InstallDestinationWorkflowOwner(
            CreateLibrary,
            synchronizer,
            new RecordingPresentation(events),
            AcceptedDialogs(),
            store);
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        Task gateHolder = Task.Run(() =>
        {
            using (synchronizer.Enter())
            {
                gateHeld.Set();
                Assert.IsTrue(releaseGate.Wait(TimeSpan.FromSeconds(5)));
            }
        });
        Assert.IsTrue(gateHeld.Wait(TimeSpan.FromSeconds(5)));

        Task search = owner.SearchPackagesAsync(
            PendingInstallDestinationSearchKind.InstallDestination,
            [new ChartPackage()]);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => search.Status == TaskStatus.Running || search.IsCompleted,
            TimeSpan.FromSeconds(5)));
        Assert.IsFalse(search.IsCompleted);
        Assert.AreEqual(0, store.SearchPackagesCount);

        releaseGate.Set();
        await search;
        await gateHolder;
        Assert.AreEqual(1, store.SearchPackagesCount);
    }

    private static InstallDestinationWorkflowOwner CreateOwner(
        Func<BMSLibrary> libraryProvider,
        List<string> events,
        IInstallDestinationStore store,
        IUiDialogService dialogs)
    {
        return new InstallDestinationWorkflowOwner(
            libraryProvider,
            new ChartFileOperationSynchronizer(),
            new RecordingPresentation(events),
            dialogs,
            store);
    }

    private static FakeUiDialogService AcceptedDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
    }

    private static ChartFile CreateChart()
    {
        return (ChartFile)FormatterServices.GetUninitializedObject(typeof(ChartFile));
    }

    private static ChartOperationTarget CreateTarget(
        ChartFile chart,
        ChartOperationCapabilities capabilities = ChartOperationCapabilities.UpdateInstallDestination)
    {
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            capabilities,
            PackageChartEntry.FromChart(chart));
    }

    private sealed class RecordingPresentation : IInstallDestinationMutationPresentation
    {
        private readonly List<string> events;

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        internal Exception? EndRefreshSuppressionFailure { get; set; }

        public void BeginActivity() => events.Add("activity-start");

        public void BeginRefreshSuppression() => events.Add("suppression-start");

        public void EndRefreshSuppression()
        {
            events.Add("suppression-end");
            if (EndRefreshSuppressionFailure != null)
            {
                throw EndRefreshSuppressionFailure;
            }
        }

        public void EndActivity()
        {
            events.Add("activity-end");
            if (EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
        }

        public void UpdateTransientStates(IEnumerable<ChartFile> charts) => events.Add("transient");

        public void InvalidateInstallDestinationSort() => events.Add("invalidate");

        public void RefreshIdentitySortKey() => events.Add("identity-refresh");

        public void RequestDisplayRefresh() => events.Add("display-refresh");
    }

    private sealed class RecordingStore : IInstallDestinationStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal int SearchPackagesCount { get; private set; }

        internal int SearchPendingCount { get; private set; }

        internal PendingInstallDestinationSearchKind LastSearchKind { get; private set; }

        internal IReadOnlyList<ChartPackage> LastPackages { get; private set; } = [];

        internal IReadOnlyList<ChartFile> ChangedCharts { get; set; } = [];

        internal ChartFile? SetResult { get; set; }

        internal string? LastDestinationDirectory { get; private set; }

        internal Exception? Failure { get; set; }

        internal Action<BMSLibrary>? SearchPackagesAction { get; set; }

        public void SearchPackages(
            BMSLibrary library,
            PendingInstallDestinationSearchKind kind,
            IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-search-packages");
            SearchPackagesCount++;
            LastSearchKind = kind;
            LastPackages = packages;
            SearchPackagesAction?.Invoke(library);
            ThrowIfConfigured();
        }

        public IReadOnlyList<ChartFile> SearchPending(
            BMSLibrary library,
            PendingInstallDestinationSearchRequest request)
        {
            events.Add("store-search-pending");
            SearchPendingCount++;
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearPackages(IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-clear-packages");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearPending(
            BMSLibrary library,
            PendingInstallDestinationClearRequest request)
        {
            events.Add("store-clear-pending");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> SearchCorrect(
            BMSLibrary library,
            RepairInstalledLocationRequest request)
        {
            events.Add("store-search-correct");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearCorrect(
            BMSLibrary library,
            RepairInstalledLocationRequest request)
        {
            events.Add("store-clear-correct");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public ChartFile SetPending(
            BMSLibrary library,
            PendingInstallDestinationEditRequest request,
            string destinationDirectory)
        {
            events.Add("store-set");
            LastDestinationDirectory = destinationDirectory;
            ThrowIfConfigured();
            return SetResult!;
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
        internal UiDialogResult? ConfirmationResult { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

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

    private sealed class RecordingLibraryDialogService : IBmsLibraryDialogService
    {
        internal int CallCount { get; private set; }

        internal Exception? Failure { get; set; }

        public MessageBoxResult Show(
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon,
            MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            CallCount++;
            if (Failure != null)
            {
                throw Failure;
            }
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
    }
}
