using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.SelectedChartResourceHealthWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SelectedChartResourceHealthWorkflowOwnerTests
{
    /// <summary>再検査storeの呼出後に一回だけ完了通知し、呼出の欠落や余計な警告表示を検出します。</summary>
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

    /// <summary>変更拒否は取消とbusy警告で返し、完了通知や成功への読み替えを検出します。</summary>
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
        StringAssert.Contains(dialogs.LastMessage, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
    }

    /// <summary>拒否警告が表示できない場合は失敗を返し、通常取消として表示失敗を隠す誤りを検出します。</summary>
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

    /// <summary>再検査後の通知先例外を元の失敗として返し、成功結果だけを返す誤りを検出します。</summary>
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

    /// <summary>無視・解除の要求値を保存へ渡し、保存失敗後に再試行や成功への代替を行う誤りを検出します。</summary>
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
            () => UninitializedBmsLibrary.Create(),
            dialogs,
            store, new ChartFileOperationSynchronizer());
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
        return new ChartFile(ChartFileKind.Bms, path, "resource-health-hash", null, "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    private sealed class RecordingStore : ISelectedChartResourceHealthStore
    {
        internal int RescanCalls { get; private set; }

        internal MaintenanceWorkflowResult RescanResult { get; set; } = new();

        internal Exception Failure { get; set; } = null!;

        internal List<bool> UnsetValues { get; } = [];

        public MaintenanceWorkflowResult RescanResourceHealthCharts(
            BMSLibrary library,
            IReadOnlyList<ChartFile> charts, LibraryFileMutationCapability capability,
            IReadOnlyList<PackageChartEntry> pendingEntries)
        {
            RescanCalls++;
            ThrowIfConfigured();
            return RescanResult;
        }

        public void SetChartResourceWarningsIgnored(
            BMSLibrary library,
            IReadOnlyList<ChartFile> charts,
            bool unset, LibraryFileMutationCapability capability)
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

}
