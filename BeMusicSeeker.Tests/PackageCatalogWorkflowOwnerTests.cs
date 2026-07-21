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

        Assert.IsTrue(owner.ConfirmClearAll(PackageCatalogSection.Pending).Accepted);
        PackageCatalogMutationResult result = await owner.ClearAllAsync(PackageCatalogSection.Pending);

        Assert.IsTrue(result.Succeeded);
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

        PackageCatalogConfirmationResult confirmation = owner.ConfirmClearAll(section);
        Assert.IsTrue(confirmation.Accepted);
        await owner.ClearAllAsync(section);

        Assert.AreEqual(section, store.LastSection);
        Assert.IsNotNull(dialogs.ConfirmationRequest);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-remove-all", "suppression-end", "activity-end" },
            events);
    }

    [TestMethod]
    public void ClearAllAsync_RejectionSkipsMutation()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
        };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogConfirmationResult confirmation = owner.ConfirmClearAll(PackageCatalogSection.Installed);

        Assert.IsFalse(confirmation.Accepted);
        Assert.IsNull(confirmation.Failure);
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

        PackageCatalogConfirmationResult confirmation = owner.ConfirmRemovePackage(PackageCatalogSection.Pending, package);
        Assert.IsTrue(confirmation.Accepted);
        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Pending,
            package);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
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
    public async Task RemovePackageAsync_InstalledDoesNotPrompt()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService();
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogConfirmationResult confirmation = owner.ConfirmRemovePackage(
            PackageCatalogSection.Installed,
            new ChartPackage());
        Assert.IsTrue(confirmation.Accepted);
        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Installed,
            new ChartPackage());

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsNull(dialogs.ConfirmationRequest);
        CollectionAssert.Contains(events, "store-remove-packages");
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
        Assert.IsTrue(owner.ConfirmRemoveSelection(request).Accepted);
        PackageCatalogMutationResult result = await owner.RemoveSelectionAsync(
            request);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
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
    public void RemoveSelectionAsync_DialogFailureIsNotTreatedAsRejection()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var failure = new InvalidOperationException("dialog failed");
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.Failed(failure) };
        var owner = CreateOwner(CreateLibrary, events, store, dialogs);

        PackageCatalogConfirmationResult confirmation = owner.ConfirmRemoveSelection(
            PackageCatalogRemovalRequest.CreatePending([CreateTarget()]));

        Assert.IsFalse(confirmation.Accepted);
        Assert.IsNotNull(confirmation.Failure);
        Assert.AreSame(failure, confirmation.Failure.InnerException);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task RemovePackageAsync_PreservesMutationAndCleanupFailures()
    {
        var events = new List<string>();
        var mutationFailure = new InvalidOperationException("remove failed");
        var cleanupFailure = new InvalidOperationException("cleanup failed");
        var store = new RecordingStore(events) { Failure = mutationFailure };
        var presentation = new RecordingPresentation(events) { EndActivityFailure = cleanupFailure };
        var owner = new PackageCatalogWorkflowOwner(
            CreateLibrary,
            new ChartFileOperationSynchronizer(),
            presentation,
            AcceptedDialogs(),
            store);

        Assert.IsTrue(owner.ConfirmRemovePackage(PackageCatalogSection.Installed, new ChartPackage()).Accepted);
        PackageCatalogMutationResult result = await owner.RemovePackageAsync(
            PackageCatalogSection.Installed,
            new ChartPackage());

        Assert.IsFalse(result.Succeeded);
        Assert.IsInstanceOfType<AggregateException>(result.Failure);
        var exception = (AggregateException)result.Failure;
        Assert.AreSame(mutationFailure, exception.InnerExceptions[0]);
        Assert.AreSame(cleanupFailure, exception.InnerExceptions[1]);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-remove-packages", "suppression-end", "activity-end" },
            events);
    }

    [TestMethod]
    public async Task ClearAllAsync_WaitsForSharedChartFileGate()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        var owner = new PackageCatalogWorkflowOwner(
            CreateLibrary,
            synchronizer,
            presentation,
            AcceptedDialogs(),
            store);
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

        Assert.IsTrue(owner.ConfirmClearAll(PackageCatalogSection.Installed).Accepted);
        Task<PackageCatalogMutationResult> removal = owner.ClearAllAsync(PackageCatalogSection.Installed);
        try
        {
            Task activityStarted = presentation.ActivityStarted;
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
        IUiDialogService dialogs)
    {
        return new PackageCatalogWorkflowOwner(
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

    private sealed class RecordingPresentation : IPackageCatalogMutationPresentation
    {
        private readonly List<string> events;
        private readonly TaskCompletionSource<bool> activityStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        internal Task ActivityStarted => activityStarted.Task;

        public void BeginActivity()
        {
            events.Add("activity-start");
            activityStarted.TrySetResult(true);
        }

        public void BeginRefreshSuppression() => events.Add("suppression-start");

        public void EndRefreshSuppression() => events.Add("suppression-end");

        public void EndActivity()
        {
            events.Add("activity-end");
            if (EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
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

        internal PackageCatalogSection LastSection { get; private set; }

        internal IReadOnlyList<ChartPackage> LastPackages { get; private set; } = [];

        internal IReadOnlyList<ChartOperationTarget> LastTargets { get; private set; } = [];

        internal IReadOnlyList<ChartPackage> ResolvedPackages { get; set; } = [];

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
