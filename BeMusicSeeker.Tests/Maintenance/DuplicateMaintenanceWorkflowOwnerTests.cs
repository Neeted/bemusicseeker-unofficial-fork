using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DuplicateMaintenanceWorkflowOwnerTests
{
    [TestMethod]
    public async Task RunFolderMergeAsync_PreservesMutationBoundaryAndReceipt()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
            events,
            presentation,
            AcceptedDialogs(),
            store,
            duplicateGroupNextHeaderProvider: _ => "Next group");
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, destination]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("Next group", result.SelectionHeader);
        Assert.AreEqual(source, store.SourceDirectory);
        Assert.AreEqual(destination, store.DestinationDirectory);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "priority-start:merge_folder",
                "store-merge",
                "suppression-end",
                "priority-release:merge_folder",
                "activity-end"
            },
            events);
    }

    /// <summary>
    /// merge 必須反映と後続 maintenance のどちらの失敗も、durable facts を維持して操作の非成功を返します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunFolderMergeAsync_DurableFinalizationFailurePublishesFailureWithoutSuccess(bool mergeApplied)
    {
        var events = new List<string>();
        var finalizationFailure = new IOException("merge finalization failed");
        var mutationReceipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\Songs\Source", @"C:\Songs\Destination")],
            durableCommit: true,
            finalizationFailure: finalizationFailure);
        var mergeReceipt = new DuplicateMergeMaintenanceReceipt(
            mergeApplied: mergeApplied,
            intermediateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
            maintenanceResult: MaintenanceWorkflowResultFacts.From(null),
            intermediateDeferred: false,
            maintenanceHadUpdates: false,
            resourceHealthIndexDeferred: false,
            resourceHealthIndexDeltaApplied: false,
            resourceHealthIndexFullRebuilt: false,
            mutationReceipt);
        var store = new TerminalRecordingStore(events, mergeReceipt);
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
            events,
            new RecordingPresentation(events),
            AcceptedDialogs(),
            store);
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, destination]));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.IsTrue(result.HasDurableFinalizationFailure);
        Assert.AreSame(finalizationFailure, result.Failure);
        Assert.AreSame(mergeReceipt, result.MutationReceipt);
        Assert.AreEqual(1, store.MergeWithReceiptCallCount);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "priority-start:merge_folder",
                "store-merge-with-receipt",
                "suppression-end",
                "priority-release:merge_folder",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public async Task RunFolderMergeAsync_AwaitsConfirmationWithoutBlockingCaller()
    {
        var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FakeUiDialogService { PendingConfirmation = confirmation, ConfirmationReached = reached };
        var events = new List<string>();
        var presentation = new RecordingPresentation(events);
        var store = new RecordingStore(events);
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, presentation, dialogs, store, gate: gate, activity: activity);
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";
        var group = new DuplicateGroup([], [source, destination]);
        Task<DuplicateMaintenanceMutationResult> resultTask = owner.RunFolderMergeAsync(source, destination, group);
        try
        {
            Task arrived = await Task.WhenAny(reached.Task, resultTask);
            Assert.AreSame(reached.Task, arrived);
            await reached.Task;
            Assert.IsFalse(resultTask.IsCompleted);
            Assert.IsFalse(gate.TryEnter(out IDisposable concurrent));
            concurrent?.Dispose();
            DuplicateMaintenanceMutationResult rejected = await owner.RunFolderMergeAsync(source, destination, group);
            Assert.IsFalse(rejected.Succeeded);
            Assert.AreEqual(string.Empty, store.SourceDirectory);
            Assert.IsFalse(events.Contains("store-merge"));

            confirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            DuplicateMaintenanceMutationResult result = await resultTask;

            Assert.IsTrue(result.Succeeded);
            Assert.IsNotNull(dialogs.ConfirmationRequest);
            Assert.AreEqual(1, events.Count(value => value == "store-merge"));
            Assert.IsTrue(gate.TryEnter(out IDisposable released));
            released.Dispose();
        }
        finally
        {
            confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
            try { await resultTask; }
            finally
            {
                owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                activity.ActivityChanged -= presentation.OnActivityChanged;
            }
        }
    }

    /// <summary>
    /// cleanup 警告と maintenance エラーを解放後に一度報告し、reporter 失敗で session の確定事実を変更しません。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task RunFolderMergeAsync_ReportsAfterReleaseAndPreservesFactsWhenReporterFails(bool reporterThrows, bool maintenanceFails)
    {
        var events = new List<string>();
        IOException? maintenanceFailure = maintenanceFails ? new IOException("maintenance failed") : null;
        var cleanupFailure = new IOException("source cleanup failed");
        var mutation = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\Source", @"D:\Destination")],
            durableCommit: true,
            finalizationFailure: maintenanceFailure,
            cleanupFailure: cleanupFailure);
        var receipt = new DuplicateMergeMaintenanceReceipt(true, ResourceHealthIndexUpdateMode.DeferOnUpdates,
            MaintenanceWorkflowResultFacts.From(null), false, false, false, false, false, mutation);
        var store = new TerminalRecordingStore(events, receipt);
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        bool reportAfterRelease = false;
        var dialogs = new FileDbReportRecordingDialogs
        {
            OnMessage = () =>
            {
                bool released = gate.TryEnter(out IDisposable lease);
                reportAfterRelease = released && !activity.IsActive && events.Contains("priority-release:merge_folder");
                lease?.Dispose();
            },
            MessageFailure = reporterThrows ? new IOException("report failed") : null
        };
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store, gate: gate, activity: activity);
        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(@"C:\Source", @"D:\Destination",
            new DuplicateGroup([], [@"C:\Source", @"D:\Destination"]));
        Assert.AreEqual(!maintenanceFails, result.Succeeded);
        Assert.AreSame(maintenanceFailure, result.Failure);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.AreEqual(maintenanceFails, result.HasDurableFinalizationFailure);
        Assert.AreSame(mutation, result.MutationReceipt!.SessionReceipt);
        Assert.AreSame(cleanupFailure, result.MutationReceipt.SessionReceipt.CleanupFailure);
        Assert.AreSame(receipt, result.MutationReceipt);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(maintenanceFails ? MessageBoxImage.Error : MessageBoxImage.Warning, dialogs.Messages[0].Icon);
        Assert.IsTrue(reportAfterRelease);
        Assert.AreEqual(1, store.MergeWithReceiptCallCount);
    }

    [TestMethod]
    public void RunFolderMergeAsync_RejectsFolderOutsideGroup()
    {
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner([], new RecordingPresentation([]), AcceptedDialogs(), new RecordingStore([]));

        Assert.ThrowsException<ArgumentException>(() => owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, @"C:\Songs\Other"])));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunHashCleanupAsync_ChoosesShortestNameAndReturnsRemovalCount(bool catalogFailure)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_DuplicateMaintenance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ChartFile keeper = CreateChart(Path.Combine(root, "a.bms"), "same-hash");
            ChartFile duplicate = CreateChart(Path.Combine(root, "long-name.bms"), "same-hash");
            ChartFile missing = CreateChart(Path.Combine(root, "missing-long-name.bms"), "same-hash");
            var outcome = new LibraryChartRemovalOutcome([
                new(duplicate.Path, LibraryChartRemovalState.Confirmed),
                new(missing.Path, LibraryChartRemovalState.NotExecuted)], true, true,
                catalogFailure ? new IOException("required catalog finalization failure") : null);
            var store = new RecordingStore([]) { RemovalOutcome = outcome };
            FakeUiDialogService dialogs = AcceptedDialogs();
            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            bool releasedAtReport = false;
            dialogs.OnMessage = () =>
            {
                releasedAtReport = gate.TryEnter(out IDisposable lease) && !activity.IsActive;
                lease?.Dispose();
            };
            DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
                [],
                new RecordingPresentation([]),
                dialogs,
                store,
                duplicateGroupNextHeaderProvider: _ => "Next group", gate: gate, activity: activity);

            DuplicateMaintenanceMutationResult result = await owner.RunHashCleanupAsync(
                new DuplicateGroup([keeper, duplicate, missing], [root]),
                root);

            Assert.AreEqual(!catalogFailure, result.Succeeded);
            Assert.AreSame(outcome.CatalogFailure, result.Failure);
            Assert.AreEqual(catalogFailure, result.HasDurableFinalizationFailure);
            Assert.AreEqual(1, result.RemovedChartCount);
            Assert.AreEqual("Next group", result.SelectionHeader);
            CollectionAssert.AreEqual(new[] { duplicate, missing }, (System.Collections.ICollection)store.Charts);
            Assert.IsNotNull(dialogs.ConfirmationRequest);
            StringAssert.Contains(dialogs.ConfirmationRequest!.MessageBoxText, "2");
            Assert.IsTrue(releasedAtReport);
            Assert.AreSame(outcome, result.RemovalOutcome);
            Assert.AreEqual(1, dialogs.Messages.Count);
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
    public async Task RunHashCleanupAsync_NoWorkDoesNotStartMutation()
    {
        var events = new List<string>();
        FakeUiDialogService dialogs = AcceptedDialogs();
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
            events,
            new RecordingPresentation(events),
            dialogs,
            new RecordingStore(events));
        ChartFile chart = CreateChart(@"C:\Songs\a.bms", "hash");

        DuplicateMaintenanceMutationResult result = await owner.RunHashCleanupAsync(
            new DuplicateGroup([chart], [@"C:\Songs"]),
            @"C:\Songs");

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(0, result.RemovedChartCount);
        Assert.IsNull(dialogs.ConfirmationRequest);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [TestMethod]
    public async Task RunHashCleanupAsync_RejectionDoesNotStartMutation()
    {
        var events = new List<string>();
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
            events,
            new RecordingPresentation(events),
            new FakeUiDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            },
            new RecordingStore(events));
        ChartFile first = CreateChart(@"C:\Songs\a.bms", "hash");
        ChartFile second = CreateChart(@"C:\Songs\long-name.bms", "hash");

        DuplicateMaintenanceMutationResult result = await owner.RunHashCleanupAsync(
            new DuplicateGroup([first, second], [@"C:\Songs"]),
            @"C:\Songs");

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public async Task RunFolderMergeAsync_TerminalObserverCleanupFailureIsOptionalAfterPriorityRelease()
    {
        var events = new List<string>();
        var receipt = new DuplicateMergeMaintenanceReceipt(true, ResourceHealthIndexUpdateMode.DeferOnUpdates,
            MaintenanceWorkflowResultFacts.From(null), false, false, false, false, false,
            new LibraryMutationSessionReceipt(
                [new LibraryMutationSessionTarget(@"C:\Source", @"D:\Destination")], durableCommit: true));
        var store = new TerminalRecordingStore(events, receipt);
        var dialogs = new FileDbReportRecordingDialogs();
        var presentation = new RecordingPresentation(events)
        {
            EndActivityFailure = new InvalidOperationException("activity cleanup failed")
        };
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
            events,
            presentation,
            dialogs,
            store,
            duplicateGroupNextHeaderProvider: _ => "Next group");
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(
            source,
            destination,
            new DuplicateGroup([], [source, destination]));

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreSame(receipt, result.MutationReceipt);
        Assert.AreEqual(0, dialogs.Messages.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                "activity-start",
                "playback-stop",
                "suppression-start",
                "priority-start:merge_folder",
                "store-merge-with-receipt",
                "suppression-end",
                "priority-release:merge_folder",
                "activity-end"
            },
            events);
    }

    [TestMethod]
    public void DuplicateFolderInteractionQueries_PreserveDestinationAndKeyboardPolicy()
    {
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner([], new RecordingPresentation([]), AcceptedDialogs(), new RecordingStore([]));
        var group = new DuplicateGroup([], [@"C:\A", @"C:\B", @"C:\C"]);

        CollectionAssert.AreEqual(
            new[] { @"C:\B", @"C:\C" },
            (System.Collections.ICollection)owner.CaptureDuplicateFolderMergeDestinations(group, @"C:\A"));
        DuplicateFolderKeyboardAction menuAction = owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\A");
        Assert.AreEqual(DuplicateFolderKeyboardActionKind.OpenContextMenu, menuAction.Kind);

        group.Folders = [@"C:\A", @"C:\B"];
        DuplicateFolderKeyboardAction mergeAction = owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\A");
        Assert.AreEqual(DuplicateFolderKeyboardActionKind.Merge, mergeAction.Kind);
        Assert.AreEqual(@"C:\B", mergeAction.DestinationPath);

        group.Folders = [@"C:\A"];
        Assert.AreEqual(
            DuplicateFolderKeyboardActionKind.Cleanup,
            owner.CaptureDuplicateFolderKeyboardAction(group, @"C:\A").Kind);
    }

    [TestMethod]
    public void OpenDuplicateFolderInExplorer_ValidatesBeforeOpeningWithoutFallback()
    {
        int openCount = 0;
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
            [],
            new RecordingPresentation([]),
            AcceptedDialogs(),
            new RecordingStore([]),
            directoryExists: path => path == @"C:\Existing",
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

        owner.OpenDuplicateFolderInExplorer(@"C:\Missing");
        owner.OpenDuplicateFolderInExplorer(@"C:\Existing");

        Assert.AreEqual(1, openCount);
    }

    /// <summary>実モデルのprepared要求を最初の確認から持ち越し、共通受付Busy、固定対象の実削除とcleanup終端までの権限保持を確認します。</summary>
    [TestMethod]
    public async Task RunHashCleanupAsync_RealPreparedTargetsKeepGateAndModelAdmission()
    {

        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Pack");
            Directory.CreateDirectory(folder);
            string keeperPath = Path.Combine(folder, "a.bms");
            string duplicatePath = Path.Combine(folder, "long-name.bms");
            File.WriteAllText(keeperPath, "#PLAYER 1");
            File.WriteAllText(duplicatePath, "#PLAYER 1");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [CreateFile(new string('a', 32), keeperPath), CreateFile(new string('a', 32), duplicatePath)], BmsonCharts = [] };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs(library.BmsCharts);
            ChartFile[] selected = library.BmsCharts.ToArray();
            var group = new DuplicateGroup([.. selected], [folder]);
            var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int confirmationCount = 0;
            var dialogs = new FakeUiDialogService
            {
                ConfirmationHandler = _ =>
                {
                    confirmationCount++;
                    reached.TrySetResult(true);
                    return confirmation.Task;
                }
            };
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var activity = new ChartMutationActivityOwner();
            var events = new List<string>();
            var presentation = new RecordingPresentation(events);
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, gate, activity, presentation, dialogs,
                () => true, Directory.Exists, _ => new ExplorerOpenResult(), _ => "next", new BmsLibraryDuplicateMaintenanceStore());
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            activity.ActivityChanged += presentation.OnActivityChanged;
            var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseCleanup = new ManualResetEventSlim();
            activity.ActivityChanged += HoldActualCleanup;
            void HoldActualCleanup(object? sender, EventArgs args)
            {
                if (!activity.IsActive)
                {
                    cleanupReached.TrySetResult();
                    releaseCleanup.Wait();
                }
            }
            Task<DuplicateMaintenanceMutationResult>? operation = null;
            try
            {
                operation = owner.RunHashCleanupAsync(group, folder);
                Task arrived = await Task.WhenAny(reached.Task, operation);
                Assert.AreSame(reached.Task, arrived, "確認到達前にTaskが終端しました。");
                await reached.Task;
                Assert.IsTrue(gate.IsActive);
                Assert.IsFalse(gate.TryEnter(out IDisposable concurrent));
                concurrent?.Dispose();
                DuplicateMaintenanceMutationResult rejected = await owner.RunHashCleanupAsync(group, folder);
                Assert.IsFalse(rejected.Succeeded);
                Assert.AreEqual(0, rejected.RemovedChartCount);
                Assert.AreEqual(0, filesystem.FileDeleteCalls);
                using (LibraryFileMutationLease rejectedModel = library.TryBeginLibraryFileMutation("prepared_cleanup_model_guard", showMessage: false))
                { Assert.IsNull(rejectedModel, "確認中の別モデル変更も副作用前Busyです。"); }
                Assert.AreEqual(1, confirmationCount);
                Assert.AreEqual(0, events.Count, "拒否要求は追加確認や再生停止を始めません。");
                confirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                await Task.WhenAny(cleanupReached.Task, operation);
                if (!cleanupReached.Task.IsCompleted) { await operation; Assert.Fail("実削除後のcleanupへ到達しませんでした。"); }
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsTrue(gate.IsActive, "実store返却後もscope回収の終端まで受付を保持します。");
                Assert.IsFalse(gate.TryEnter(out IDisposable duringCleanup));
                duringCleanup?.Dispose();
                using (LibraryFileMutationLease cleanupProbe = library.TryBeginLibraryFileMutation("prepared_cleanup_terminal_guard", showMessage: false))
                { Assert.IsNull(cleanupProbe); }
                Assert.AreEqual(1, filesystem.FileDeleteCalls);
                Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
                Assert.AreEqual(0, filesystem.FileMoveCalls);
                Assert.IsTrue(File.Exists(keeperPath));
                Assert.IsFalse(File.Exists(duplicatePath));
                Assert.AreEqual(keeperPath, library.BmsCharts.Single().Path);
                using (LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
                { Assert.AreEqual(keeperPath, readback.Table<LR2SongDB.song>().Single().path); }
                releaseCleanup.Set();
                DuplicateMaintenanceMutationResult result = await operation;
                Assert.IsTrue(result.Succeeded);
                Assert.IsNull(result.Failure);
                Assert.AreEqual(1, result.RemovedChartCount);
                Assert.IsNotNull(result.RemovalOutcome);
                Assert.AreEqual(1, result.RemovalOutcome.ConfirmedChartCount);
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.AreEqual(1, confirmationCount, "拒否した要求は終端後にも再実行しません。");
                Assert.AreEqual(1, filesystem.FileDeleteCalls);
                Assert.IsTrue(gate.TryEnter(out IDisposable probe));
                probe.Dispose();
                using LibraryFileMutationLease next = library.TryBeginLibraryFileMutation("prepared_cleanup_released", showMessage: false);
                Assert.IsNotNull(next);
            }
            finally
            {
                releaseCleanup.Set();
                confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
                try { if (operation != null) { await operation; } }
                finally
                {
                    owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                    activity.ActivityChanged -= presentation.OnActivityChanged;
                    activity.ActivityChanged -= HoldActualCleanup;
                    library.RequestShutdown("prepared-duplicate-test");
                }
            }
        });
    }

    /// <summary>全フォルダ判断中も論理受付を保持し、scope・再生停止より前に判断を済ませ同じpreparedと生存権限を実storeへ渡します。</summary>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task RunHashCleanupAsync_WholeFolderDecisionUsesPreparedRealStore(int decision)
    {

        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string keeperFolder = Path.Combine(root, "Keep");
            string deletionFolder = Path.Combine(root, "Delete");
            Directory.CreateDirectory(keeperFolder);
            Directory.CreateDirectory(deletionFolder);
            string keeperPath = Path.Combine(keeperFolder, "a.bms");
            string deletionPath = Path.Combine(deletionFolder, "long-name.bms");
            string resourcePath = Path.Combine(deletionFolder, "user.wav");
            File.WriteAllText(keeperPath, "#PLAYER 1");
            File.WriteAllText(deletionPath, "#PLAYER 1");
            File.WriteAllText(resourcePath, "user resource");
            DateTime fixedDate = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(keeperPath, fixedDate);
            File.SetLastWriteTimeUtc(deletionPath, fixedDate);
            var filesystem = new TestFileMutationService();
            var modelDialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, modelDialogs,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [CreateFile(new string('a', 32), keeperPath), CreateFile(new string('a', 32), deletionPath)], BmsonCharts = [] };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs(library.BmsCharts);
            var group = new DuplicateGroup([.. library.BmsCharts], [root]);
            var store = new DelegatingPreparedStore();
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var activity = new ChartMutationActivityOwner();
            var events = new List<string>();
            var presentation = new RecordingPresentation(events);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            int countPrompts = 0;
            int folderPrompts = 0;
            var dialogs = new FakeUiDialogService
            {
                ConfirmationHandler = request =>
                {
                    if (request.Button == MessageBoxButton.OKCancel)
                    {
                        countPrompts++;
                        return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    }
                    folderPrompts++;
                    Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Confirm_DeleteFolderWithNoBms, deletionFolder), request.MessageBoxText);
                    Assert.AreEqual(BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm, request.Caption);
                    Assert.AreEqual(MessageBoxButton.YesNo, request.Button);
                    Assert.AreEqual(MessageBoxImage.Question, request.Icon);
                    Assert.AreEqual(MessageBoxResult.Yes, request.DefaultResult);
                    Assert.IsFalse(gate.TryEnter(out IDisposable concurrent));
                    concurrent?.Dispose();
                    Assert.IsFalse(activity.IsActive);
                    Assert.AreEqual(0, events.Count);
                    Assert.AreEqual(0, store.ExecuteCount);
                    using LibraryFileMutationLease modelProbe = library.TryBeginLibraryFileMutation("folder_confirmation_probe", showMessage: false);
                    Assert.IsNull(modelProbe, "フォルダ確認中も未受理のモデル変更はBusyです。");
                    reached.TrySetResult(true);
                    return confirmation.Task;
                }
            };
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, gate, activity, presentation, dialogs,
                () => decision != 0, Directory.Exists, _ => new ExplorerOpenResult(), _ => "next", store);
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            activity.ActivityChanged += presentation.OnActivityChanged;
            Task<DuplicateMaintenanceMutationResult>? operation = null;
            try
            {
                operation = owner.RunHashCleanupAsync(group, root);
                Task arrived = await Task.WhenAny(reached.Task, operation);
                Assert.AreSame(reached.Task, arrived, "フォルダ確認前にTaskが終端しました。");
                await reached.Task;
                Assert.IsNotNull(store.Prepared);
                CollectionAssert.AreEqual(new[] { deletionFolder }, store.Prepared.WholeFolderCandidatePaths.ToArray());
                UiDialogResult answer = decision switch
                {
                    1 => UiDialogResult.FromMessageBoxResult(MessageBoxResult.No),
                    2 => UiDialogResult.ClosedByUser(MessageBoxResult.No),
                    _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
                };
                confirmation.SetResult(answer);
                DuplicateMaintenanceMutationResult result = await operation;
                Assert.AreEqual(decision == 0 ? 0 : 1, countPrompts);
                Assert.AreEqual(1, folderPrompts);
                Assert.AreEqual(1, store.PrepareCount);
                Assert.AreEqual(1, store.ExecuteCount);
                Assert.IsNotNull(store.ApprovedPaths);
                CollectionAssert.AreEqual(decision == 0 ? new[] { deletionFolder } : [], store.ApprovedPaths.ToArray());
                Assert.AreEqual(0, modelDialogs.ModelMessages);
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.IsTrue(File.Exists(keeperPath));
                Assert.IsTrue(Directory.Exists(keeperFolder));
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.IsTrue(result.Succeeded);
                Assert.IsNull(result.Failure);
                Assert.AreEqual(1, result.RemovedChartCount);
                Assert.IsFalse(File.Exists(deletionPath));
                Assert.AreEqual(decision != 0, File.Exists(resourcePath));
                Assert.AreEqual(decision != 0, Directory.Exists(deletionFolder));
                Assert.AreEqual(keeperPath, readback.Table<LR2SongDB.song>().Single().path);
            }
            finally
            {
                confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.No));
                try { if (operation != null) { await operation; } }
                finally
                {
                    owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                    activity.ActivityChanged -= presentation.OnActivityChanged;
                    library.RequestShutdown("duplicate-folder-confirmation-test");
                }
            }
        });
    }

    /// <summary>先行Yesを実行せず、最後の確認の表示失敗・null応答・例外を失敗として保持します。</summary>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task RunHashCleanupAsync_WholeFolderFailureDoesNotStartMutation(int failureKind)
    {

        var events = new List<string>();
        var presentation = new RecordingPresentation(events);
        var store = new RecordingStore(events) { WholeFolderCandidates = [@"C:\One", @"C:\Two"] };
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        var expectedFailure = new IOException("folder prompt failed");
        int folderCount = 0;
        var dialogs = new FakeUiDialogService
        {
            ConfirmationHandler = request =>
            {
                if (request.Button == MessageBoxButton.OKCancel) { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
                folderCount++;
                Assert.IsTrue(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.AreEqual(0, events.Count);
                if (folderCount == 1) { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)); }
                if (failureKind == 2) { throw expectedFailure; }
                // 外部実装が契約を破ってnullを返すケースを、nullableな配列から実際の応答として渡します。
                return Task.FromResult(failureKind == 1
                    ? new object?[] { null }.Cast<UiDialogResult>().Single()
                    : UiDialogResult.Failed(expectedFailure));
            }
        };
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, presentation, dialogs, store, gate: gate, activity: activity);
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DuplicateFolderFailure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string keeper = Path.Combine(root, "a.bms");
        string duplicate = Path.Combine(root, "long-name.bms");
        File.WriteAllText(keeper, "same");
        File.WriteAllText(duplicate, "same");
        try
        {
            DuplicateMaintenanceMutationResult result = await owner.RunHashCleanupAsync(
                new DuplicateGroup([CreateChart(keeper, "hash"), CreateChart(duplicate, "hash")], [root]), root);
            Assert.IsFalse(result.Succeeded);
            Assert.IsNotNull(result.Failure);
            if (failureKind == 2) { Assert.AreSame(expectedFailure, result.Failure); }
            if (failureKind == 0) { Assert.AreSame(expectedFailure, result.Failure.InnerException); }
            Assert.AreEqual(2, folderCount);
            Assert.AreEqual(0, events.Count);
            Assert.AreEqual(0, store.Charts.Count);
            Assert.IsFalse(activity.IsActive);
            Assert.IsTrue(gate.TryEnter(out IDisposable released));
            released.Dispose();
            Assert.IsTrue(File.Exists(keeper));
            Assert.IsTrue(File.Exists(duplicate));
        }
        finally
        {
            owner.WorkflowChanged -= presentation.OnWorkflowChanged;
            activity.ActivityChanged -= presentation.OnActivityChanged;
            Directory.Delete(root, recursive: true);
        }
    }

    private static DuplicateMaintenanceWorkflowOwner CreateOwner(
        List<string> events,
        RecordingPresentation presentation,
        IUiDialogService dialogs,
        RecordingStore store,
        Func<string, bool>? directoryExists = null,
        Func<string, ExplorerOpenResult>? explorerOpen = null,
        Func<DuplicateGroup, string>? duplicateGroupNextHeaderProvider = null,
        bool showConfirmation = true,
        ChartFileOperationSynchronizer? gate = null,
        ChartMutationActivityOwner? activity = null)
    {
        activity ??= new();
        var owner = new DuplicateMaintenanceWorkflowOwner(
            CreateLibrary,
            gate ?? new ChartFileOperationSynchronizer(),
            activity,
            presentation,
            dialogs,
            () => showConfirmation,
            directoryExists ?? (_ => true),
            explorerOpen ?? (_ => new ExplorerOpenResult()),
            duplicateGroupNextHeaderProvider ?? (_ => (string)null!),
            store);
        owner.WorkflowChanged += presentation.OnWorkflowChanged;
        activity.ActivityChanged += presentation.OnActivityChanged;
        return owner;
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary));
    }

    private static ChartFile CreateChart(string path, string hash)
    {
        return new ChartFile(ChartFileKind.Bms, path, hash, null, "Duplicate", "Duplicate", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    private static FakeUiDialogService AcceptedDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
    }

    private sealed class RecordingPresentation : IChartMutationPlaybackPort
    {
        private readonly List<string> events;

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        internal Exception? EndActivityFailure { get; set; }

        internal void OnActivityChanged(object? sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender!;
            events.Add(activity.IsActive ? "activity-start" : "activity-end");
            if (!activity.IsActive && EndActivityFailure != null)
            {
                throw EndActivityFailure;
            }
        }

        internal void OnWorkflowChanged(
            object? sender,
            DuplicateMaintenanceWorkflowChangedEventArgs e)
        {
            switch (e)
            {
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

        public Task StopPlaybackForMutationAsync()
        {
            events.Add("playback-stop");
            return Task.CompletedTask;
        }
    }

    /// <summary>preparedと承認列を観測しつつ、処理結果は本番storeへ委譲します。</summary>
    private sealed class DelegatingPreparedStore : IDuplicateMaintenanceStore
    {
        private readonly IDuplicateMaintenanceStore inner;
        private readonly DelegatingPreparedStore observations;

        internal DelegatingPreparedStore()
        { inner = new BmsLibraryDuplicateMaintenanceStore(); observations = this; }

        private DelegatingPreparedStore(DelegatingPreparedStore observations, IDuplicateMaintenanceStore inner)
        { this.observations = observations; this.inner = inner; }

        internal LibraryChartRemovalPreflight? Prepared { get; private set; }
        internal IReadOnlyList<string>? ApprovedPaths { get; private set; }
        internal int PrepareCount { get; private set; }
        internal int ExecuteCount { get; private set; }

        /// <summary>生存権限を内側の実storeへ転送し、観測だけを元wrapperへ集約します。</summary>
        public IDuplicateMaintenanceStore ForAcceptedOperation(LibraryFileMutationCapability capability)
            => new DelegatingPreparedStore(observations, inner.ForAcceptedOperation(capability));

        public LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts)
        {
            observations.PrepareCount++;
            return observations.Prepared = inner.PrepareChartRemoval(library, charts);
        }
        public LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderPaths)
        {
            observations.ExecuteCount++;
            Assert.AreSame(observations.Prepared, prepared);
            observations.ApprovedPaths = approvedWholeFolderPaths;
            return inner.RemoveCharts(library, prepared, approvedWholeFolderPaths);
        }
        public DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId)
            => inner.MergeFolderWithReceipt(library, sourceDirectory, destinationDirectory, operationId);
    }

    private class RecordingStore : IDuplicateMaintenanceStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal string SourceDirectory { get; private set; } = string.Empty;

        internal string DestinationDirectory { get; private set; } = string.Empty;

        internal IReadOnlyList<ChartFile> Charts { get; private set; } = [];

        protected void AddEvent(string value) => events.Add(value);

        public virtual DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
            BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId)
        {
            events.Add("store-merge");
            SourceDirectory = sourceDirectory;
            DestinationDirectory = destinationDirectory;
            return new DuplicateMergeMaintenanceReceipt(
                true, ResourceHealthIndexUpdateMode.DeferOnUpdates,
                MaintenanceWorkflowResultFacts.From(null), false, false, false, false, false,
                LibraryMutationSessionReceipt.Empty);
        }

        internal LibraryChartRemovalOutcome RemovalOutcome { get; set; } = null!;
        internal IReadOnlyList<string> WholeFolderCandidates { get; set; } = [];

        public LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts)
            => new(charts, WholeFolderCandidates, [], charts.Count, 0);

        public LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderPaths)
        {
            IReadOnlyList<ChartFile> charts = prepared.Targets;
            events.Add("store-remove");
            Charts = charts;
            return RemovalOutcome ?? new LibraryChartRemovalOutcome(charts.Select(chart => new LibraryChartRemovalTarget(chart.Path, LibraryChartRemovalState.Confirmed)), true, true);
        }
    }

    private sealed class TerminalRecordingStore : RecordingStore
    {
        private readonly DuplicateMergeMaintenanceReceipt receipt;

        internal TerminalRecordingStore(
            List<string> events,
            DuplicateMergeMaintenanceReceipt receipt)
            : base(events)
        {
            this.receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        }

        internal int MergeWithReceiptCallCount { get; private set; }

        public override DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
            BMSLibrary library,
            string sourceDirectory,
            string destinationDirectory,
            long operationId)
        {
            MergeWithReceiptCallCount++;
            AddEvent("store-merge-with-receipt");
            return receipt;
        }
    }

    private sealed class FakeUiDialogService : IUiDialogService
    {
        internal UiDialogResult? ConfirmationResult { get; set; }
        internal Func<UiConfirmationRequest, Task<UiDialogResult>>? ConfirmationHandler { get; set; }

        internal TaskCompletionSource<UiDialogResult>? PendingConfirmation { get; set; }
        internal TaskCompletionSource<bool>? ConfirmationReached { get; set; }

        internal UiConfirmationRequest? ConfirmationRequest { get; private set; }

        internal List<UiMessageRequest> Messages { get; } = [];
        internal Action? OnMessage { get; set; }
        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            OnMessage?.Invoke();
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            ConfirmationRequest = request;
            if (ConfirmationHandler != null) { return ConfirmationHandler(request); }
            ConfirmationReached?.TrySetResult(true);
            if (PendingConfirmation != null)
            {
                return PendingConfirmation.Task;
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
}
