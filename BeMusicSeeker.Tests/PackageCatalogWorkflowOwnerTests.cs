using System;
using System.Collections.Generic;
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
public sealed class PackageCatalogWorkflowOwnerTests
{
    [TestMethod]
    public async Task ClearAllAsync_WithoutAttachedLibraryIsNoOp()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var owner = CreateOwner(() => null!, events, store, AcceptedDialogs());

        PackageCatalogMutationResult result = await owner.ClearAllAsync(PackageCatalogSection.Pending);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemoveAllCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [DataTestMethod]
    [DataRow((int)PackageCatalogSection.Pending)]
    [DataRow((int)PackageCatalogSection.Installed)]
    public async Task ClearAllAsync_PreservesConfirmationAndMutationBoundaryOrder(int sectionValue)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var section = (PackageCatalogSection)sectionValue;
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = AcceptedDialogs();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.ClearAllAsync(section);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(section, store.LastSection);
        Assert.IsNotNull(dialogs.ConfirmationRequest);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-remove-all", "suppression-end", "activity-end" },
            events);
    }

    [TestMethod]
    public async Task ClearAllAsync_RejectionSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.ClearAllAsync(PackageCatalogSection.Installed);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemoveAllCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task ClearAllAsync_ConfirmationFailureSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.ClearAllAsync(PackageCatalogSection.Pending);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemoveAllCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task RemovePackageAsync_PendingConfirmationPrecedesMutation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = AcceptedDialogs();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);
        var package = new ChartPackage { path = @"C:\Pending\package" };

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            package);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-remove-packages",
                "suppression-end",
                "activity-end"
            },
            events);
        Assert.AreSame(package, store.LastPackages[0]);
        StringAssert.Contains(dialogs.ConfirmationRequest!.MessageBoxText, package.DisplayTitle);
    }

    [TestMethod]
    public async Task RemovePackageAsync_PublishesEmptySectionIntent()
    {
        var events = new List<string>();
        var store = new RecordingStore(events)
        {
            EmptySection = PackageCatalogSection.Pending
        };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            new ChartPackage());

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(PackageCatalogSection.Pending, result.EmptySection);
    }

    [TestMethod]
    public async Task RemovePackageAsync_InstalledDoesNotPrompt()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        var package = new ChartPackage();
        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Installed,
            package);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.IsNull(dialogs.ConfirmationRequest);
        CollectionAssert.Contains(events, "store-remove-packages");
    }

    [TestMethod]
    public async Task RemovePackageAsync_PendingRejectionSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemovePackagesCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task RemovePackageAsync_PendingConfirmationFailureSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemovePackagesCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task RemoveSelectionAsync_ResolvesAndRemovesInsideOneMutation()
    {
        var events = new List<string>();
        var expectedPackage = new ChartPackage();
        var store = new RecordingStore(events) { ResolvedPackages = [expectedPackage] };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());
        ChartOperationTarget target = CreateTarget();

        PackageCatalogRemovalRequest request = PackageCatalogRemovalRequest.CreatePending([target]);
        PackageCatalogMutationResult result = await owner.RemoveSelectionAsync(
            request);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(PackageCatalogSection.Pending, store.LastSection);
        CollectionAssert.AreEqual(new[] { target }, (System.Collections.ICollection)store.LastTargets);
        CollectionAssert.AreEqual(new[] { expectedPackage }, (System.Collections.ICollection)store.LastPackages);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "suppression-start",
                "store-resolve",
                "store-remove-packages",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task RemoveSelectionAsync_DialogFailureSkipsMutationAndViewApply()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogMutationResult result = await owner.RemoveSelectionAsync(
            PackageCatalogRemovalRequest.CreatePending([CreateTarget()]));

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreSame(failure, result.Failure.InnerException);
        Assert.IsFalse(result.ShouldApplyView);
        Assert.AreEqual(0, store.RemovePackagesCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task RemovePackageAsync_PreservesMutationAndCleanupFailures()
    {
        var events = new List<string>();
        var mutationFailure = new InvalidOperationException("remove failed");
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var store = new RecordingStore(events)
        {
            Failure = mutationFailure,
            EmptySection = PackageCatalogSection.Installed
        };
        var phaseObserver = new RecordingPhaseObserver(events) { EndActivityFailure = cleanupFailure };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs(), phaseObserver);

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Installed,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.AreEqual(PackageCatalogSection.Installed, result.EmptySection);
        Assert.IsInstanceOfType<AggregateException>(result.Failure);
        var exception = (AggregateException)result.Failure;
        Assert.AreSame(mutationFailure, exception.InnerExceptions[0]);
        Assert.AreSame(cleanupFailure, exception.InnerExceptions[1]);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-remove-packages", "suppression-end", "activity-end" },
            events);
    }

    [TestMethod]
    public async Task RemovePackageAsync_PreservesMutationBeforeEmptySectionFailures()
    {
        var events = new List<string>();
        var mutationFailure = new InvalidOperationException("remove failed");
        var emptySectionFailure = new InvalidOperationException("empty-section query failed");
        var store = new RecordingStore(events)
        {
            Failure = mutationFailure,
            EmptySectionFailure = emptySectionFailure
        };
        var owner = CreateOwner(CreateLibrary, events, store, AcceptedDialogs());

        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Installed,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.ShouldApplyView);
        Assert.IsInstanceOfType<AggregateException>(result.Failure);
        var exception = (AggregateException)result.Failure;
        Assert.AreSame(mutationFailure, exception.InnerExceptions[0]);
        Assert.AreSame(emptySectionFailure, exception.InnerExceptions[1]);
    }

    [TestMethod]
    public async Task ClearAllAsync_WaitsForSharedChartFileGate()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var phaseObserver = new RecordingPhaseObserver(events);
        ChartMutationActivityOwner activity = new();
        var owner = new PackageCatalogWorkflowOwner(
            CreateLibrary,
            synchronizer,
            activity,
            AcceptedDialogs(),
            mutation => Task.Factory.StartNew(
                mutation,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default),
            store);
        owner.MutationPhasePublished += phaseObserver.OnPhasePublished;
        activity.ActivityChanged += phaseObserver.OnActivityChanged;
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        var gateThread = new Thread(() =>
        {
            using (synchronizer.Enter())
            {
                gateHeld.Set();
                releaseGate.Wait();
            }
        });
        gateThread.Start();
        Assert.IsTrue(gateHeld.Wait(TimeSpan.FromSeconds(5)));

        Task<PackageCatalogMutationResult> removal = owner.ClearAllAsync(PackageCatalogSection.Installed);
        try
        {
            Task activityStarted = phaseObserver.ActivityStarted;
            Assert.AreSame(
                activityStarted,
                await Task.WhenAny(activityStarted, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.IsFalse(removal.IsCompleted);
            Assert.AreEqual(0, store.RemoveAllCount);
        }
        finally
        {
            releaseGate.Set();
            gateThread.Join();
        }
        PackageCatalogMutationResult result = await removal;
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, store.RemoveAllCount);
    }

    private static PackageCatalogWorkflowOwner CreateOwner(
        Func<BMSLibrary> libraryProvider,
        List<string> events,
        IPackageCatalogStore store,
        IUiDialogService dialogs,
        RecordingPhaseObserver? phaseObserver = null)
    {
        phaseObserver ??= new RecordingPhaseObserver(events);
        ChartMutationActivityOwner activity = new();
        var owner = new PackageCatalogWorkflowOwner(
            libraryProvider,
            new ChartFileOperationSynchronizer(),
            activity,
            dialogs,
            mutation => Task.Run(mutation),
            store);
        owner.MutationPhasePublished += phaseObserver.OnPhasePublished;
        activity.ActivityChanged += phaseObserver.OnActivityChanged;
        return owner;
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

    private static ChartOperationTarget CreateTarget()
    {
        var chart = (ChartFile)FormatterServices.GetUninitializedObject(typeof(ChartFile));
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);
    }

    private sealed class RecordingPhaseObserver
    {
        private readonly List<string> events;
        private readonly TaskCompletionSource<bool> activityStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal RecordingPhaseObserver(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        internal Task ActivityStarted => activityStarted.Task;

        internal void OnActivityChanged(object sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender;
            events.Add(activity.IsActive ? "activity-start" : "activity-end");
            if (activity.IsActive)
            {
                activityStarted.TrySetResult(true);
            }
            if (!activity.IsActive && EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
        }

        internal void OnPhasePublished(object sender, PackageCatalogMutationPhaseEventArgs e)
        {
            events.Add(e.Phase switch
            {
                PackageCatalogMutationPhase.RefreshSuppressionStarted => "suppression-start",
                PackageCatalogMutationPhase.RefreshSuppressionEnded => "suppression-end",
                _ => throw new ArgumentOutOfRangeException(nameof(e.Phase), e.Phase, "Unsupported package catalog mutation phase.")
            });
        }
    }

    private sealed class RecordingStore : IPackageCatalogStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal int RemoveAllCount { get; private set; }

        internal int RemovePackagesCount { get; private set; }

        internal PackageCatalogSection LastSection { get; private set; }

        internal IReadOnlyList<ChartPackage> LastPackages { get; private set; } = [];

        internal IReadOnlyList<ChartOperationTarget> LastTargets { get; private set; } = [];

        internal IReadOnlyList<ChartPackage> ResolvedPackages { get; set; } = [];

        internal PackageCatalogSection? EmptySection { get; set; }

        internal Exception? EmptySectionFailure { get; set; }

        internal Exception? Failure { get; set; }

        public void RemoveAll(BMSLibrary library, PackageCatalogSection section)
        {
            events.Add("store-remove-all");
            RemoveAllCount++;
            LastSection = section;
            ThrowIfConfigured();
        }

        public void RemovePackages(
            BMSLibrary library,
            PackageCatalogSection section,
            IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-remove-packages");
            RemovePackagesCount++;
            LastSection = section;
            LastPackages = packages;
            ThrowIfConfigured();
        }

        public IReadOnlyList<ChartPackage> ResolvePackages(
            BMSLibrary library,
            PackageCatalogSection section,
            IReadOnlyList<ChartOperationTarget> targets)
        {
            events.Add("store-resolve");
            LastSection = section;
            LastTargets = targets;
            return ResolvedPackages;
        }

        public bool IsSectionEmpty(BMSLibrary library, PackageCatalogSection section)
        {
            if (EmptySectionFailure != null)
            {
                throw EmptySectionFailure;
            }
            return EmptySection == section;
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

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            return Task.FromResult(ConfirmationResult
                ?? throw new InvalidOperationException("Confirmation result was not configured."));
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
