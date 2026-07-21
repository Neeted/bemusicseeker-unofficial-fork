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
                DefaultSettings,
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
                DefaultSettings,
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
    public async Task ForceInstallPackagesAsync_ApprovesOverrideThenStopsPlaybackAndMutatesPackageScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartFile chart = CreateChart(installDestination: @"C:\Installed\song.bms");
        ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
        };
        var owner = new InstallDestinationWorkflowOwner(
            CreateLibrary,
            new ChartFileOperationSynchronizer(),
            presentation,
            dialogs,
            DefaultSettings,
            store);

        await owner.ForceInstallPackagesAsync([package], () => events.Add("shell-prepare"));

        CollectionAssert.AreEqual(
            new[]
            {
                "shell-prepare",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(InstallDestinationRefreshScope.PackageMutation, presentation.LastRefreshScope);
        Assert.AreEqual(chart.Path, presentation.StoppedCharts.Single().Path);
        Assert.IsTrue(store.ApprovedNormalInstallOverridePackages.Contains(package));
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Confirm_NormalInstallOverride, dialogs.ConfirmationRequest!.MessageBoxText);
    }

    [TestMethod]
    public async Task ForceInstallPackagesAsync_OverrideRejectionRunsBoundaryWithoutApprovingPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartFile chart = CreateChart(installDestination: @"C:\Installed\song.bms");
        ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.No)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        await owner.ForceInstallPackagesAsync([package], () => events.Add("shell-prepare"));

        CollectionAssert.AreEqual(
            new[]
            {
                "shell-prepare",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(0, store.ApprovedNormalInstallOverridePackages.Count);
        Assert.AreSame(package, store.LastPackages.Single());
    }

    [TestMethod]
    public async Task ManualInstallPackagesAsync_RejectionPreservesShellSelectionAndSkipsMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = new InstallDestinationWorkflowOwner(
            CreateLibrary,
            new ChartFileOperationSynchronizer(),
            new RecordingPresentation(events),
            dialogs,
            () => new InstallDestinationWorkflowSettingsSnapshot(
                showManualInstallConfirmation: true,
                deletePendingPackageSourceAfterInstall: true),
            store);
        int prepareCount = 0;

        await owner.ManualInstallPackagesAsync(
            [ChartPackage.FromChartEntries([PackageChartEntry.FromChart(CreateChart())])],
            () => prepareCount++);

        Assert.AreEqual(0, prepareCount);
        Assert.AreEqual(0, events.Count);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_manual_installation_delete_source,
            dialogs.ConfirmationRequest!.MessageBoxText);
    }

    [TestMethod]
    public async Task InstallPendingAsync_ResolvesRequestBeforePreparingShellAndForceInstalling()
    {
        var events = new List<string>();
        ChartFile chart = CreateChart();
        ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
        var store = new RecordingStore(events) { ResolvedPackages = [package] };
        var owner = CreateOwner(CreateLibrary, events, store, new FakeUiDialogService());
        PendingInstallPackageOperationRequest request =
            PendingInstallPackageOperationRequest.CreateForceInstall([CreateTarget(chart)]);

        await owner.InstallPendingAsync(request, () => events.Add("shell-prepare"));

        CollectionAssert.AreEqual(
            new[]
            {
                "store-resolve-packages",
                "shell-prepare",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-force-install",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreSame(package, store.LastPackages.Single());
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_WarnsWithoutDestinationAndDoesNotMutate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(), ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.FixInstalledLocationsAsync(request);

        Assert.AreEqual(0, events.Count);
        Assert.AreEqual(
            BeMusicSeeker.Properties.Resources.Msg_fix_installation_warning,
            dialogs.MessageRequest!.MessageBoxText);
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_WarningDisplayFailurePropagates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var failure = new InvalidOperationException("warning display failed");
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            MessageResult = UiDialogResult.Failed(failure)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(CreateChart(), ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.FixInstalledLocationsAsync(request));

        Assert.AreSame(failure, exception.InnerException);
        StringAssert.Contains(exception.Message, "Installed-location repair warning");
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task FixInstalledLocationsAsync_ApprovesDuplicateRemovalAndMutatesPackageScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        ChartFile chart = CreateChart(
            path: @"C:\Charts\repair.bms",
            installDestination: @"C:\Installed\repair.bms");
        var store = new RecordingStore(events)
        {
            DuplicateConfirmations =
            [
                new BMSLibrary.DuplicateInstallRepairConfirmation(
                    chart,
                    [@"C:\Duplicate\repair.bms"])
            ]
        };
        var presentation = new RecordingPresentation(events);
        var dialogs = new FakeUiDialogService();
        dialogs.EnqueueConfirmation(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        dialogs.EnqueueConfirmation(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes));
        var owner = new InstallDestinationWorkflowOwner(
            CreateLibrary,
            new ChartFileOperationSynchronizer(),
            presentation,
            dialogs,
            DefaultSettings,
            store);
        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
            [CreateTarget(chart, ChartOperationCapabilities.RepairInstalledLocation)],
            out RepairInstalledLocationRequest request));

        await owner.FixInstalledLocationsAsync(request);

        CollectionAssert.AreEqual(
            new[]
            {
                "store-get-duplicate-confirmations",
                "activity-start",
                "playback-stop",
                "suppression-start",
                "store-fix-installed-locations",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreEqual(InstallDestinationRefreshScope.PackageMutation, presentation.LastRefreshScope);
        Assert.AreEqual(chart.Path, presentation.StoppedCharts.Single().Path);
        CollectionAssert.AreEqual(
            new[] { chart.Path },
            store.ApprovedDuplicateRemovalChartPaths.ToArray());
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
            DefaultSettings,
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
            DefaultSettings,
            store);
    }

    private static FakeUiDialogService AcceptedDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
    }

    private static InstallDestinationWorkflowSettingsSnapshot DefaultSettings()
    {
        return new InstallDestinationWorkflowSettingsSnapshot(
            showManualInstallConfirmation: false,
            deletePendingPackageSourceAfterInstall: false);
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
    }

    private static ChartFile CreateChart(
        string path = @"C:\Charts\song.bms",
        string? installDestination = null)
    {
        var bmsFile = new BMSFile { path = path };
        return new ChartFile(
            ChartFileKind.Bms,
            path,
            md5: "0123456789abcdef0123456789abcdef",
            sha256: null,
            title: "Title",
            rawTitle: "Title",
            artist: "Artist",
            genre: "Genre",
            folder: "Charts",
            tag: null,
            levelText: null,
            level: null,
            mode: null,
            chartInfo: null,
            bmsFile: bmsFile,
            bmsonSong: null,
            installDestination: installDestination);
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

        internal InstallDestinationRefreshScope LastRefreshScope { get; private set; }

        internal IReadOnlyList<ChartFile> StoppedCharts { get; private set; } = [];

        public void BeginActivity() => events.Add("activity-start");

        public void BeginRefreshSuppression(InstallDestinationRefreshScope scope)
        {
            LastRefreshScope = scope;
            events.Add("suppression-start");
        }

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

        public void StopIfPlayingCharts(IReadOnlyList<ChartFile> charts)
        {
            StoppedCharts = charts;
            events.Add("playback-stop");
        }
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

        internal IReadOnlyList<ChartPackage> ResolvedPackages { get; set; } = [];

        internal ISet<ChartPackage> ApprovedNormalInstallOverridePackages { get; private set; } = new HashSet<ChartPackage>();

        internal IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> DuplicateConfirmations { get; set; } = [];

        internal IReadOnlyList<string> ApprovedDuplicateRemovalChartPaths { get; private set; } = [];

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

        public IReadOnlyList<ChartPackage> ResolvePendingPackages(
            BMSLibrary library,
            IReadOnlyList<ChartOperationTarget> targets)
        {
            events.Add("store-resolve-packages");
            ThrowIfConfigured();
            return ResolvedPackages;
        }

        public void ForceInstallPackages(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            ISet<ChartPackage> approvedNormalInstallOverridePackages)
        {
            events.Add("store-force-install");
            LastPackages = packages;
            ApprovedNormalInstallOverridePackages = new HashSet<ChartPackage>(approvedNormalInstallOverridePackages);
            ThrowIfConfigured();
        }

        public void ManualInstallPackages(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-manual-install");
            LastPackages = packages;
            ThrowIfConfigured();
        }

        public IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> GetDuplicateInstallRepairConfirmations(
            BMSLibrary library,
            IReadOnlyList<ChartFile> repairCharts)
        {
            events.Add("store-get-duplicate-confirmations");
            ThrowIfConfigured();
            return DuplicateConfirmations;
        }

        public void FixInstalledLocations(
            BMSLibrary library,
            IReadOnlyList<ChartFile> repairCharts,
            IReadOnlyList<string> approvedDuplicateRemovalChartPaths)
        {
            events.Add("store-fix-installed-locations");
            ChangedCharts = repairCharts;
            ApprovedDuplicateRemovalChartPaths = approvedDuplicateRemovalChartPaths;
            ThrowIfConfigured();
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
        private readonly Queue<UiDialogResult> confirmationResults = new();

        internal UiDialogResult? ConfirmationResult { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        internal UiMessageRequest? MessageRequest { get; private set; }

        internal UiDialogResult? MessageResult { get; set; }

        internal void EnqueueConfirmation(UiDialogResult result)
        {
            confirmationResults.Enqueue(result);
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageRequest = request;
            return Task.FromResult(MessageResult ?? UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            if (confirmationResults.Count > 0)
            {
                return Task.FromResult(confirmationResults.Dequeue());
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
