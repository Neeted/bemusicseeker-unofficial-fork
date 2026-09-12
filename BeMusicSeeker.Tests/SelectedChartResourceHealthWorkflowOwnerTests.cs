using System;
using System.Collections.Generic;
using System.IO;
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
public sealed class SelectedChartResourceHealthWorkflowOwnerTests
{
    [TestMethod]
    public async Task RescanAsync_UsesStoreAndPublishesCompletionAfterSuccess()
    {
        var store = new RecordingStore();
        var dialogs = new RecordingDialogService();
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(store, dialogs);
        int completionCalls = 0;
        owner.RescanCompleted += (_, _) =>
        {
            Assert.AreEqual(1, store.RescanCalls);
            completionCalls++;
        };
        ChartResourceHealthRequest request = CreateRequest();

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(request);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, store.RescanCalls);
        Assert.AreEqual(1, completionCalls);
        Assert.AreEqual(0, dialogs.MessageCalls);
    }

    [TestMethod]
    public async Task RescanAsync_WhenLibraryBlocksMutation_ShowsWarningWithoutRefresh()
    {
        var store = new RecordingStore { RescanResult = new MaintenanceWorkflowResult { Canceled = true } };
        var dialogs = new RecordingDialogService
        {
            MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(store, dialogs);
        int completionCalls = 0;
        owner.RescanCompleted += (_, _) => completionCalls++;

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(CreateRequest());

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Canceled);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(1, store.RescanCalls);
        Assert.AreEqual(0, completionCalls);
        Assert.AreEqual(1, dialogs.MessageCalls);
        StringAssert.Contains(dialogs.LastMessage, BeMusicSeeker.Properties.Resources.Warn_Lr2SongDbSyncRunning);
    }

    [TestMethod]
    public async Task RescanAsync_WhenBlockedWarningCannotBeShown_ReturnsFailure()
    {
        var store = new RecordingStore { RescanResult = new MaintenanceWorkflowResult { Canceled = true } };
        var dialogs = new RecordingDialogService
        {
            MessageResult = UiDialogResult.Failed(new InvalidOperationException("dialog unavailable"))
        };
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(store, dialogs);
        int completionCalls = 0;
        owner.RescanCompleted += (_, _) => completionCalls++;

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(CreateRequest());

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Canceled);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(0, completionCalls);
    }

    [TestMethod]
    public async Task RescanAsync_WhenCompletionObserverFails_ReturnsFailure()
    {
        var store = new RecordingStore();
        var failure = new InvalidOperationException("resource health presentation failed");
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(store, new RecordingDialogService());
        owner.RescanCompleted += (_, _) => throw failure;

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(CreateRequest());

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.Canceled);
        Assert.AreSame(failure, result.Failure);
        Assert.AreEqual(1, store.RescanCalls);
    }

    [TestMethod]
    public void SetWarningsIgnored_PassesUnsetAndDoesNotFallbackAfterFailure()
    {
        var store = new RecordingStore();
        SelectedChartResourceHealthWorkflowOwner owner = CreateOwner(
            store,
            new RecordingDialogService());

        SelectedChartResourceHealthWorkflowResult ignored = owner.SetWarningsIgnored(CreateRequest());
        SelectedChartResourceHealthWorkflowResult unignored = owner.SetWarningsIgnored(CreateRequest(), unset: true);

        Assert.IsTrue(ignored.Succeeded);
        Assert.IsTrue(unignored.Succeeded);
        CollectionAssert.AreEqual(new[] { false, true }, store.UnsetValues);

        store.Failure = new IOException("warning persistence failed");
        SelectedChartResourceHealthWorkflowResult failed = owner.SetWarningsIgnored(CreateRequest());

        Assert.IsFalse(failed.Succeeded);
        Assert.IsNotNull(failed.Failure);
        CollectionAssert.AreEqual(new[] { false, true, false }, store.UnsetValues);
    }

    private static SelectedChartResourceHealthWorkflowOwner CreateOwner(
        RecordingStore store,
        RecordingDialogService dialogs)
    {
        return new SelectedChartResourceHealthWorkflowOwner(
            () => (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary)),
            dialogs,
            store);
    }

    private static ChartResourceHealthRequest CreateRequest()
    {
        ChartOperationTarget target = new(
            CreateChart(@"C:\Songs\resource-health.bms"),
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            ChartOperationCapabilities.RunResourceHealthCheck);
        Assert.IsTrue(ChartResourceHealthRequest.TryCreate([target], out ChartResourceHealthRequest request));
        return request;
    }

    private static ChartFile CreateChart(string path)
    {
        var file = new BMSFile { path = path };
        return new ChartFile(
            ChartFileKind.Bms,
            path,
            "resource-health-hash",
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

    private sealed class RecordingStore : ISelectedChartResourceHealthStore
    {
        internal int RescanCalls { get; private set; }

        internal MaintenanceWorkflowResult RescanResult { get; set; } = new();

        internal Exception Failure { get; set; } = null!;

        internal List<bool> UnsetValues { get; } = [];

        public MaintenanceWorkflowResult RescanResourceHealthCharts(
            BMSLibrary library,
            IReadOnlyList<ChartFile> charts)
        {
            RescanCalls++;
            ThrowIfConfigured();
            return RescanResult;
        }

        public void SetChartResourceWarningsIgnored(
            BMSLibrary library,
            IReadOnlyList<ChartFile> charts,
            bool unset)
        {
            UnsetValues.Add(unset);
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

    private sealed class RecordingDialogService : IUiDialogService
    {
        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal int MessageCalls { get; private set; }

        internal string LastMessage { get; private set; } = string.Empty;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            MessageCalls++;
            LastMessage = request.MessageBoxText;
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
