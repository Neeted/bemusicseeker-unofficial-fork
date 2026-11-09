using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
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

    /// <summary>
    /// 変更拒否は取消とbusy警告で返し、完了通知や成功への読み替えを検出します。
    /// 警告の表示の時点で受付が解放済みであることも確かめ、受付を保持したまま表示を待つ誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_WhenLibraryBlocksMutation_ShowsWarningWithoutRefresh()
    {
        var store = new RecordingStore { RescanResult = new MaintenanceWorkflowResult { Canceled = true } };
        var dialogs = new RecordingDialogService
        {
            MessageResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var gate = new ChartFileOperationSynchronizer();
        bool admissionFreeAtDisplay = false;
        dialogs.OnMessage = () =>
        {
            admissionFreeAtDisplay = gate.TryEnter(out IDisposable lease);
            lease?.Dispose();
        };
        var owner = new SelectedChartResourceHealthWorkflowOwner(() => UninitializedBmsLibrary.Create(), dialogs, store, gate);
        int completionCalls = 0;
        owner.RescanCompleted += (_, _) => completionCalls++;

        SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(CreateRequest());

        Assert.IsTrue(admissionFreeAtDisplay, "警告は受付と権限を解放した後に表示する。");

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

    /// <summary>
    /// 選択の再検査の読込み失敗は、処理中に窓口へ表示せず、受付と書込み保護を解放した後に一つの通知へまとめて表示する。
    /// 表示の時点で受付が取得でき、表示が終わる前でも別の実保守が完了し、通知はライブラリ側の窓口を通らず、
    /// 再検査の結果は表示の終端まで返らない。読取りスレッドの即時表示、受付を保持したままの表示の待ち、
    /// 一件ずつの表示、窓口の取り違え、標本の順の乱れ、表示の終端を待たない結果を検出する。
    /// </summary>
    [TestMethod]
    [DataRow(MaintenanceReadFailureFixture.FailureCount)]
    [DataRow(1)]
    public async Task RescanAsync_ShowsOneGroupedReadFailureNoticeAfterAdmissionIsReleased(int failureCount)
    {
        await MaintenanceReadFailureFixture.RunAsync(async fixture =>
        {
            using var run = new PendingDisplayRun(fixture, fixture.Library);
            Task<SelectedChartResourceHealthWorkflowResult> rescan = run.Owner.RescanAsync(fixture.CreateRequest(failureCount));
            try
            {
                Assert.AreSame(run.Requested.Task, await Task.WhenAny(run.Requested.Task, rescan), "通知の要求が先に届く。");
                Assert.IsTrue(run.AdmissionHeldDuringRescan, "保守の実行中は受付を保持している。");
                Assert.AreEqual(0, run.OwnerRequestsDuringRescan, "処理中の表示は0件。");
                Assert.AreEqual(0, run.ModelRequestsDuringRescan, "モデルの窓口へは表示しない。");
                Assert.IsTrue(run.AdmissionFreeAtDisplay, "表示の時点で受付が取得できる。");
                Assert.IsFalse(rescan.IsCompleted, "結果は通知の表示の終端まで返らない。");
                MaintenanceWorkflowResult other = await Task.Run(() => fixture.Library.RescanResourceHealthCharts([fixture.Readable]));
                Assert.IsFalse(other.Canceled, "表示の終了前でも別の実保守が完了できる。");
            }
            finally { run.ReleaseDisplay(); }

            SelectedChartResourceHealthWorkflowResult result = await rescan;

            Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
            UiMessageRequest request = run.OwnerDialogs.Messages.Single();
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.MessageBoxTitle_Error, request.Caption);
            Assert.AreEqual(MessageBoxImage.Hand, request.Icon);
            Assert.AreEqual(fixture.ExpectedBody(failureCount), request.MessageBoxText);
            Assert.AreEqual(0, fixture.LibraryDialogs.ModelMessages);
            Assert.AreEqual(0, fixture.LibraryDialogs.Messages.Count);
        });
    }

    /// <summary>
    /// 確定が失敗しても、集めた読込み失敗は捨てず、受付の解放後に表示してから元の失敗を返す。
    /// 例外の経路で収集先を失う誤りと、受付を保持したままの表示を検出する。
    /// </summary>
    [TestMethod]
    public async Task RescanAsync_WhenCommitFails_ShowsCollectedReadFailuresAndReturnsOriginalFailure()
    {
        await MaintenanceReadFailureFixture.RunAsync(async fixture =>
        {
            fixture.FailMaintenanceCommit();
            using var run = new PendingDisplayRun(fixture, fixture.Library);
            Task<SelectedChartResourceHealthWorkflowResult> rescan = run.Owner.RescanAsync(fixture.CreateRequest());
            try
            {
                Assert.AreSame(run.Requested.Task, await Task.WhenAny(run.Requested.Task, rescan), "通知の要求が先に届く。");
                Assert.IsTrue(run.AdmissionFreeAtDisplay, "表示の時点で受付が取得できる。");
                Assert.IsFalse(rescan.IsCompleted, "異常結果は通知の表示の後に返る。");
            }
            finally { run.ReleaseDisplay(); }

            SelectedChartResourceHealthWorkflowResult result = await rescan;

            Assert.IsFalse(result.Succeeded);
            MaintenanceReadFailureFixture.AssertCommitFailure(result.Failure);
            Assert.AreEqual(fixture.ExpectedBody(), run.OwnerDialogs.Messages.Single().MessageBoxText);
        });
    }

    /// <summary>
    /// 通知の表示が失敗しても、再検査の結果は成功のままで、例外を出さず、受付も保守の保存も成功時と同じ。
    /// 表示の失敗を操作の失敗へ混ぜる誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("result")]
    [DataRow("faulted")]
    [DataRow("throws")]
    public async Task RescanAsync_WhenNoticeCannotBeShown_KeepsSucceededResult(string shape)
    {
        await MaintenanceReadFailureFixture.RunAsync(async fixture =>
        {
            var failure = new IOException("notice display failure " + shape);
            var ownerDialogs = new FileDbReportRecordingDialogs();
            switch (shape)
            {
                case "result": ownerDialogs.MessageHandler = _ => Task.FromResult(UiDialogResult.Failed(failure)); break;
                case "faulted": ownerDialogs.MessageHandler = _ => Task.FromException<UiDialogResult>(failure); break;
                default: ownerDialogs.MessageFailure = failure; break;
            }
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => fixture.Library, ownerDialogs);

            SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(fixture.CreateRequest());

            Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
            Assert.AreEqual(1, ownerDialogs.Messages.Count, "表示を一度だけ試みる。");
            Assert.IsTrue(fixture.Library.OperationAdmission.TryEnter(out IDisposable? lease), "受付は解放済み。");
            lease.Dispose();
            using LR2SongDBExtended read = new BmsLibraryDbGateway(
                fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.DatabasePath).OpenSongDbReadOnly();
            Assert.IsNotNull(read.Find<LR2SongDBExtended.maintenance>(fixture.Readable.Path), "読める譜面の保守行は保存済み。");
        });
    }

    /// <summary>
    /// 終了の要求後、または対象のライブラリが差し替わった後は、集めた読込み失敗を表示しない。
    /// 結果は成功のままで、受付は解放済み。終了後や世代の失効後に表示する誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("shutdown")]
    [DataRow("replaced")]
    public async Task RescanAsync_AfterShutdownOrLibraryReplacement_DoesNotShowNotice(string shape)
    {
        await MaintenanceReadFailureFixture.RunAsync(async fixture =>
        {
            BMSLibrary current = fixture.Library;
            BMSLibrary replacement = UninitializedBmsLibrary.Create();
            var ownerDialogs = new FileDbReportRecordingDialogs();
            var store = new ObservingStore(new BmsLibrarySelectedChartResourceHealthStore(), () =>
            {
                if (shape == "shutdown") { fixture.Library.RequestStop("read-failure-notice-test"); }
                else { current = replacement; }
            });
            var owner = new SelectedChartResourceHealthWorkflowOwner(() => current, ownerDialogs, store);
            BMSLibrary requested = fixture.Library;

            SelectedChartResourceHealthWorkflowResult result = await owner.RescanAsync(fixture.CreateRequest());

            Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
            Assert.AreEqual(0, ownerDialogs.Messages.Count);
            Assert.AreEqual(0, fixture.LibraryDialogs.ModelMessages);
            Assert.IsTrue(requested.OperationAdmission.TryEnter(out IDisposable? lease), "受付は解放済み。");
            lease.Dispose();
        });
    }

    /// <summary>
    /// 通知の表示を未完了のまま止め、保守の実行中と表示の時点の受付・要求の件数を記録する所有者の準備です。
    /// ライブラリの窓口と所有者の窓口を別のインスタンスにし、取り違えを検出できるようにします。
    /// </summary>
    private sealed class PendingDisplayRun : IDisposable
    {
        private readonly TaskCompletionSource<UiDialogResult> display = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal PendingDisplayRun(MaintenanceReadFailureFixture fixture, BMSLibrary library)
        {
            OwnerDialogs = new FileDbReportRecordingDialogs { MessageHandler = _ => display.Task };
            OwnerDialogs.OnMessage = () =>
            {
                AdmissionFreeAtDisplay = library.OperationAdmission.TryEnter(out IDisposable? lease);
                lease?.Dispose();
                Requested.TrySetResult(true);
            };
            var store = new ObservingStore(new BmsLibrarySelectedChartResourceHealthStore(), () =>
            {
                OwnerRequestsDuringRescan = OwnerDialogs.Messages.Count;
                ModelRequestsDuringRescan = fixture.LibraryDialogs.ModelMessages + fixture.LibraryDialogs.Messages.Count;
                AdmissionHeldDuringRescan = !library.OperationAdmission.TryEnter(out IDisposable? lease);
                lease?.Dispose();
            });
            Owner = new SelectedChartResourceHealthWorkflowOwner(() => library, OwnerDialogs, store);
        }

        internal FileDbReportRecordingDialogs OwnerDialogs { get; }

        internal SelectedChartResourceHealthWorkflowOwner Owner { get; }

        internal TaskCompletionSource<bool> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool AdmissionFreeAtDisplay { get; private set; }

        internal bool AdmissionHeldDuringRescan { get; private set; }

        internal int OwnerRequestsDuringRescan { get; private set; } = -1;

        internal int ModelRequestsDuringRescan { get; private set; } = -1;

        internal void ReleaseDisplay() => display.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        public void Dispose() => ReleaseDisplay();
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
            IReadOnlyList<PackageChartEntry> pendingEntries,
            MaintenanceReadFailureCollector readFailures)
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
