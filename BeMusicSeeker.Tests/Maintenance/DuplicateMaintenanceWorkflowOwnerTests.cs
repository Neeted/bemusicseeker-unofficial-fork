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
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DuplicateMaintenanceWorkflowOwnerTests
{
    /// <summary>空の登録子を含む統合元の拒否を本番storeへ通し、停止後も実体とDBを保ち、終端後の通常統合を新規に受け付けます。</summary>
    /// <remarks>判断・物理処理・SQLiteは実装を使い、再生・確認・表示だけを既存portで記録します。</remarks>
    [TestMethod]
    public async Task RunFolderMergeAsync_RegisteredScopeRefusalReleasesAdmissionForNextMerge()
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string source = Path.Combine(root, "Source");
            string empty = Path.Combine(source, "EmptyRegistered");
            string destination = Path.Combine(root, "Destination");
            string normal = Path.Combine(root, "Normal");
            ChartFile[] charts = [LibraryResourceIndexTestSupport.CreateChart(source, "same.bms"), LibraryResourceIndexTestSupport.CreateChart(destination, "same.bms"), LibraryResourceIndexTestSupport.CreateChart(normal, "same.bms"), LibraryResourceIndexTestSupport.CreateChart(normal, "unique.bms")];
            Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(source, "kept.wav"), "protected audio");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new FileDbReportRecordingDialogs(),
                settings: TestBmsFactory.CreateRootSettings([root, empty]))
            { BmsCharts = charts };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(charts);
            var events = new List<string>();
            var presentation = new RecordingPresentation(events);
            var activity = new ChartMutationActivityOwner();
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, library.OperationAdmission, activity, presentation,
                AcceptedDialogs(), () => true, Directory.Exists, _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), new BmsLibraryDuplicateMaintenanceStore());
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            activity.ActivityChanged += presentation.OnActivityChanged;
            try
            {
                DuplicateMaintenanceMutationResult refused = await owner.RunFolderMergeAsync(source, destination, new DuplicateGroup(charts.ToList(), [source, destination]));
                Assert.IsFalse(refused.Succeeded);
                Assert.IsNotNull(refused.MutationReceipt);
                Assert.IsFalse(refused.MutationReceipt.MergeApplied);
                Assert.IsTrue(events.Contains("playback-stop"));
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.IsTrue(Directory.Exists(empty));
                Assert.AreEqual("protected audio", File.ReadAllText(Path.Combine(source, "kept.wav")));
                foreach (ChartFile chart in charts) { Assert.IsTrue(File.Exists(chart.Path)); }
                using (LR2SongDBExtended db = gateway.OpenSongDbReadOnly()) { Assert.AreEqual(charts.Length, db.Table<LR2SongDB.song>().Count()); }
                DuplicateMaintenanceMutationResult accepted = await owner.RunFolderMergeAsync(normal, destination, new DuplicateGroup(charts.ToList(), [normal, destination]));
                Assert.IsTrue(accepted.Succeeded, accepted.Failure?.ToString());
                Assert.IsFalse(Directory.Exists(normal));
                Assert.IsTrue(File.Exists(Path.Combine(destination, "unique.bms")));
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(activity.IsActive);
            }
            finally
            {
                owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                activity.ActivityChanged -= presentation.OnActivityChanged;
                library.RequestShutdown("registered-merge-workflow");
            }
        });
    }

    /// <summary>明示保守の必要な準備が失敗しても、統合とHash削除それぞれの先行確定・元失敗を結果へ残します。実ライブラリの接続で同じ存続権限と必要指定を確認し、準備終端まで親Lを保持して最後に回収します。</summary>
    [TestMethod]
    [DataRow("merge")]
    [DataRow("hash")]
    public async Task ExplicitMaintenance_RequiredPreparationFailurePreservesFactsAndReleasesAdmission(string operationKind)
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException();
            ChartFile keeper = CreateChart(Path.Combine(root, "a.bms"), "same-hash");
            ChartFile duplicate = CreateChart(Path.Combine(root, "duplicate.bms"), "same-hash");
            File.WriteAllText(keeper.Path, "same content");
            File.WriteAllText(duplicate.Path, "same content");
            FakeUiDialogService dialogs = AcceptedDialogs();
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new FileDbReportRecordingDialogs(),
                settings: TestBmsFactory.CreateRootSettings([root]))
            { BmsCharts = [keeper, duplicate] };
            var originalFailure = new IOException("required original finalization failure");
            var preparationFailure = new IOException("required duplicate preparation failure");
            var session = new LibraryMutationSessionReceipt([new LibraryMutationSessionTarget(duplicate.Path, keeper.Path)],
                durableCommit: true, finalizationFailure: originalFailure);
            var mergeReceipt = new DuplicateMergeMaintenanceReceipt(true, ResourceHealthIndexUpdateMode.DeferOnUpdates,
                MaintenanceWorkflowResultFacts.From(null), false, false, false, false, false, session);
            var removal = new LibraryChartRemovalOutcome([new(duplicate.Path, LibraryChartRemovalState.Confirmed)], true, true, originalFailure);
            var events = new List<string>();
            var store = new TerminalRecordingStore(events, mergeReceipt) { RemovalOutcome = removal };
            var activity = new ChartMutationActivityOwner();
            var presentation = new RecordingPresentation(events);
            var selection = DuplicateViewContext.ForGroup(Path.Combine(root, "next"));
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, library.OperationAdmission, activity, presentation,
                dialogs, () => false, _ => true, _ => new ExplorerOpenResult(), _ => selection, store);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<BMSLibrary, LibraryFileMutationCapability, bool, Task> prepare = (source, capability, requireGroups) =>
            {
                Assert.AreSame(library, source);
                Assert.IsTrue(requireGroups, "明示保守は現在画面によらず成功後選択の準備を指定します。");
                using (source.OperationAdmission.Borrow(capability)) { }
                Assert.IsTrue(source.OperationAdmission.IsActive);
                reached.TrySetResult();
                return CompletePreparationAsync();
            };
            async Task CompletePreparationAsync()
            {
                await release.Task;
                throw preparationFailure;
            }
            library.ConnectDuplicateDataPreparation(prepare);
            Task<DuplicateMaintenanceMutationResult>? operation = null;
            try
            {
                var group = new DuplicateGroup([keeper, duplicate], [root, Path.Combine(root, "destination")]);
                operation = operationKind == "merge"
                    ? owner.RunFolderMergeAsync(root, Path.Combine(root, "destination"), group)
                    : owner.RunHashCleanupAsync(group, root);
                Assert.AreSame(reached.Task, await Task.WhenAny(reached.Task, operation));
                await reached.Task;
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsTrue(library.OperationAdmission.IsActive);
                Assert.IsFalse(library.OperationAdmission.TryEnter(out IDisposable rejected));
                Assert.IsNull(rejected);
                release.TrySetResult();
                DuplicateMaintenanceMutationResult result = await operation;
                Assert.IsFalse(result.Succeeded);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.IsTrue(result.HasDurableFinalizationFailure);
                Assert.AreSame(selection, result.SelectionContext);
                Assert.IsInstanceOfType(result.Failure, typeof(AggregateException));
                CollectionAssert.AreEquivalent(new Exception[] { originalFailure, preparationFailure },
                    ((AggregateException)result.Failure).Flatten().InnerExceptions.ToArray());
                if (operationKind == "merge")
                {
                    Assert.AreSame(mergeReceipt, result.MutationReceipt);
                    Assert.AreEqual(1, store.MergeWithReceiptCallCount);
                }
                else
                {
                    Assert.AreSame(removal, result.RemovalOutcome);
                    Assert.AreEqual(1, result.RemovedChartCount);
                    CollectionAssert.AreEqual(new[] { duplicate.Path }, store.Charts.Select(chart => chart.Path).ToArray());
                }
                Assert.IsFalse(activity.IsActive);
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable next));
                next.Dispose();
            }
            finally
            {
                release.TrySetResult();
                try { if (operation != null) { await operation; } }
                finally
                {
                    library.DisconnectDuplicateDataPreparation(prepare);
                    library.RequestShutdown("explicit-maintenance-required-preparation");
                }
            }
        });
    }

    /// <summary>
    /// フォルダ統合は再生停止後に表示抑制・保存処理の順に進み、解放後に活動を終えて操作前に捕捉した次の所属を返し、処理順の逆転を検出する。
    /// </summary>
    [TestMethod]
    public async Task RunFolderMergeAsync_PreservesMutationBoundaryAndReceipt()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";
        var group = new DuplicateGroup([], [source, destination]);
        ChartFile expected = CreateChart(@"C:\Songs\Next\chart.bms", "next");
        ChartFile other = CreateChart(@"C:\Songs\Other\chart.bms", "other");
        var next = new DuplicateGroup([expected], [@"C:\Songs\Next"]) { Header = string.Empty };
        var sameName = new DuplicateGroup([other], [@"C:\Songs\Other"]) { Header = string.Empty };
        var state = new SelectionPresentationState { DuplicateChartGroups = [group, next, sameName] };
        using var tree = new MaintenanceTreeViewModel();
        tree.AttachPresentationState(state);
        var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FakeUiDialogService { PendingConfirmation = confirmation, ConfirmationReached = reached };
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, presentation, dialogs, store,
            duplicateGroupNextSelectionProvider: tree.CaptureNextDuplicateGroupSelection);
        Task<DuplicateMaintenanceMutationResult> operation = owner.RunFolderMergeAsync(source, destination, group);
        try
        {
            Assert.AreSame(reached.Task, await Task.WhenAny(reached.Task, operation));
            await reached.Task;
            // 要求作成後の次対象を変えても、操作前に捕捉した群を現在結果から解決します。
            state.DuplicateChartGroups = [group, sameName, new([expected], [@"C:\Songs\Next"]) { Header = "renamed" }];
            confirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            DuplicateMaintenanceMutationResult result = await operation;
            Assert.IsTrue(result.Succeeded);
            Assert.IsNotNull(result.SelectionContext);
            DuplicateGroup selected = result.SelectionContext.ResolveGroup(state.DuplicateChartGroups);
            Assert.IsNotNull(selected);
            CollectionAssert.AreEqual(new[] { expected.Path }, selected.ChartFiles.Select(chart => chart.Path).ToArray());
            Assert.AreEqual(source, store.SourceDirectory);
            Assert.AreEqual(destination, store.DestinationDirectory);
            CollectionAssert.AreEqual(new[] { "activity-start", "playback-stop", "suppression-start", "store-merge", "suppression-end", "activity-end" }, events);
        }
        finally
        {
            confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
            await operation;
        }
    }

    /// <summary>
    /// 統合または後続保守の必須反映失敗では保存確定と元失敗・receiptを維持して非成功を返し、確定失敗の成功化や処理の二重呼出しを検出する。
    /// </summary>
    // falseは統合未適用、trueは統合適用後の必須反映失敗。
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
                "store-merge-with-receipt",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    /// <summary>
    /// 未完了の確認を非同期に待つ間は共通排他を保持して再入を拒否し、承認後だけ一回統合して排他を解放することで、未承認変更を検出する。
    /// </summary>
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
    /// 片付け・保守の異常を排他と活動の解放後に一回報告し、報告先の失敗でも保存確定・元原因・成功区分を保持して通知障害による事実の書換えを検出する。
    /// </summary>
    // 第一値は報告先の例外、第二値は必須保守失敗の有無。片付け失敗は全入力で保持する。
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
                reportAfterRelease = released && !activity.IsActive && events.Contains("suppression-end");
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

    /// <summary>
    /// 重複グループにない統合先は引数失敗で拒否し、グループ外のフォルダへ変更を始める誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// 同一ハッシュの最短ファイル名を残し、確認済み削除だけの件数と保存後失敗を返して、未実行対象の成功集計や保持対象の削除を検出する。
    /// </summary>
    // falseは正常保存反映、trueは削除確定後の必須カタログ反映失敗。
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
            var cleanupGroup = new DuplicateGroup([keeper, duplicate, missing], [root]);
            ChartFile expected = CreateChart(@"C:\Songs\Next\chart.bms", "next");
            ChartFile other = CreateChart(@"C:\Songs\Other\chart.bms", "other");
            var next = new DuplicateGroup([expected], [@"C:\Songs\Next"]) { Header = string.Empty };
            var sameName = new DuplicateGroup([other], [@"C:\Songs\Other"]) { Header = string.Empty };
            var state = new SelectionPresentationState { DuplicateChartGroups = [cleanupGroup, next, sameName] };
            using var tree = new MaintenanceTreeViewModel();
            tree.AttachPresentationState(state);
            var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialogs.PendingConfirmation = confirmation;
            dialogs.ConfirmationReached = reached;
            DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
                [],
                new RecordingPresentation([]),
                dialogs,
                store,
                duplicateGroupNextSelectionProvider: tree.CaptureNextDuplicateGroupSelection, gate: gate, activity: activity);

            Task<DuplicateMaintenanceMutationResult> operation = owner.RunHashCleanupAsync(cleanupGroup, root);
            DuplicateMaintenanceMutationResult result;
            try
            {
                Assert.AreSame(reached.Task, await Task.WhenAny(reached.Task, operation));
                await reached.Task;
                state.DuplicateChartGroups = [sameName, new([expected], [@"C:\Songs\Next"]) { Header = "changed" }];
                confirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                result = await operation;
            }
            finally
            {
                confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
                await operation;
            }
            Assert.AreEqual(!catalogFailure, result.Succeeded);
            Assert.AreSame(outcome.CatalogFailure, result.Failure);
            Assert.AreEqual(catalogFailure, result.HasDurableFinalizationFailure);
            Assert.AreEqual(1, result.RemovedChartCount);
            Assert.IsNotNull(result.SelectionContext);
            DuplicateGroup selected = result.SelectionContext.ResolveGroup(state.DuplicateChartGroups);
            Assert.IsNotNull(selected);
            CollectionAssert.AreEqual(new[] { expected.Path }, selected.ChartFiles.Select(chart => chart.Path).ToArray());
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

    /// <summary>
    /// 重複がない単一譜面は確認や活動・保存を始めず件数なしを返し、不要な削除要求の開始を検出する。
    /// </summary>
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

    /// <summary>
    /// 初回確認のCancelは失敗なしの未実行、表示不能は元原因付きで返し、folder確認・停止・出力開始を防ぎます。
    /// </summary>
    [DataTestMethod]
    [DataRow("cancel")]
    [DataRow("failed")]
    [DataRow("unavailable")]
    public async Task RunHashCleanupAsync_RejectionDoesNotStartMutation(string decision)
    {
        var events = new List<string>();
        var failure = new IOException("first confirmation failed");
        var store = new RecordingStore(events) { WholeFolderCandidates = [@"C:\One"] };
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        var presentation = new RecordingPresentation(events);
        int prompts = 0;
        var dialogs = new FakeUiDialogService
        {
            ConfirmationHandler = request =>
        {
            prompts++;
            Assert.AreEqual(MessageBoxButton.OKCancel, request.Button);
            Assert.AreEqual(MessageBoxResult.Cancel, request.DefaultResult);
            return Task.FromResult(decision switch
            {
                "failed" => UiDialogResult.Failed(failure),
                "unavailable" => UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable),
                _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            });
        }
        };
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
            events,
            presentation,
            dialogs,
            store, gate: gate, activity: activity);
        ChartFile first = CreateChart(@"C:\Songs\a.bms", "hash");
        ChartFile second = CreateChart(@"C:\Songs\long-name.bms", "hash");

        try
        {
            DuplicateMaintenanceMutationResult result = await owner.RunHashCleanupAsync(
                new DuplicateGroup([first, second], [@"C:\Songs"]),
                @"C:\Songs");

            Assert.IsFalse(result.Succeeded);
            if (decision == "cancel") { Assert.IsNull(result.Failure); }
            else
            {
                Assert.IsNotNull(result.Failure);
                if (decision == "failed") { Assert.AreSame(failure, result.Failure.InnerException); }
                else { StringAssert.Contains(result.Failure.Message, "OwnerUnavailable"); }
            }
            Assert.AreEqual(1, prompts);
            Assert.AreEqual(0, store.PhysicalMutationStarts);
            Assert.IsFalse(gate.IsActive);
            Assert.IsFalse(activity.IsActive);
            Assert.AreEqual(0, events.Count);
        }
        finally
        {
            owner.WorkflowChanged -= presentation.OnWorkflowChanged;
            activity.ActivityChanged -= presentation.OnActivityChanged;
        }
    }

    /// <summary>
    /// 確定成功後の活動表示の片付けが失敗しても表示抑制を解放して成功receiptを保ち、任意表示障害で保存成功を失う誤りを検出する。
    /// </summary>
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
            duplicateGroupNextSelectionProvider: _ => DuplicateViewContext.ForGroup(@"C:\Songs\Next"));
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
                "store-merge-with-receipt",
                "suppression-end",
                "activity-end"
            },
            events);
    }

    /// <summary>
    /// 同一・親子パスを候補から除き、兄弟と接頭辞だけ同じ兄弟を大小文字重複なしで残す。候補ゼロは無操作とし、有効候補が一件でも元の件数によるメニュー・直接統合・単一フォルダ整理を維持する。
    /// </summary>
    [TestMethod]
    public void DuplicateFolderInteractionQueries_PreserveDestinationAndKeyboardPolicy()
    {
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(
            [],
            new RecordingPresentation([]),
            AcceptedDialogs(),
            new RecordingStore([]),
            directoryExists: _ => throw new AssertFailedException("統合候補の照会で存在確認を行わない。"));
        const string sourcePath = @"C:\Songs\A";
        var group = new DuplicateGroup([], [sourcePath, @"c:\songs\a", @"C:\Songs\A\",
            @"C:\Songs", @"C:\Songs\A\Child", @"C:\Songs\B", @"c:\songs\b", @"C:\Songs\AB"]);

        IReadOnlyList<string> destinations = owner.CaptureDuplicateFolderMergeDestinations(group, sourcePath);
        Assert.AreEqual(2, destinations.Count);
        CollectionAssert.AreEquivalent(
            new[] { @"C:\SONGS\B", @"C:\SONGS\AB" },
            destinations.Select(path => path.ToUpperInvariant()).ToArray());
        DuplicateFolderKeyboardAction menuAction = owner.CaptureDuplicateFolderKeyboardAction(group, sourcePath);
        Assert.AreEqual(DuplicateFolderKeyboardActionKind.OpenContextMenu, menuAction.Kind);

        // 二件の親・子・同一表記違いと、三件以上で全て親子の場合は、空メニューも統合も開始しない。
        List<string>[] groupsWithoutDestinations =
        [
            [sourcePath, @"C:\Songs"],
            [sourcePath, @"C:\Songs\A\Child"],
            [sourcePath, @"c:\songs\a\"],
            [sourcePath, @"C:\Songs", @"C:\Songs\A\Child"]
        ];
        foreach (List<string> folders in groupsWithoutDestinations)
        {
            group.Folders = folders;
            Assert.AreEqual(0, owner.CaptureDuplicateFolderMergeDestinations(group, sourcePath).Count);
            DuplicateFolderKeyboardAction noAction = owner.CaptureDuplicateFolderKeyboardAction(group, sourcePath);
            Assert.AreEqual(DuplicateFolderKeyboardActionKind.None, noAction.Kind);
            Assert.IsNull(noAction.DestinationPath);
        }

        // 有効候補数ではなく元の三件を使い、一件に絞れても直接統合へ切り替えない。
        group.Folders = [sourcePath, @"C:\Songs", @"C:\Songs\B"];
        Assert.AreEqual(1, owner.CaptureDuplicateFolderMergeDestinations(group, sourcePath).Count);
        Assert.AreEqual(
            DuplicateFolderKeyboardActionKind.OpenContextMenu,
            owner.CaptureDuplicateFolderKeyboardAction(group, sourcePath).Kind);

        group.Folders = [sourcePath, @"C:\Songs\B"];
        DuplicateFolderKeyboardAction mergeAction = owner.CaptureDuplicateFolderKeyboardAction(group, sourcePath);
        Assert.AreEqual(DuplicateFolderKeyboardActionKind.Merge, mergeAction.Kind);
        Assert.AreEqual(@"C:\Songs\B", mergeAction.DestinationPath);

        group.Folders = [sourcePath];
        Assert.AreEqual(
            DuplicateFolderKeyboardActionKind.Cleanup,
            owner.CaptureDuplicateFolderKeyboardAction(group, sourcePath).Kind);
    }

    /// <summary>
    /// 存在しないフォルダは開かず、存在先のshell失敗でも一回だけ試みて、別対象への代替実行を検出する。
    /// </summary>
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

    /// <summary>
    /// 実モデルで確認前に固定した重複対象を使い、確認中のBusy拒否から実削除・片付け終端まで共通権限を保持して対象再計算や早期解放を検出する。
    /// </summary>
    /// <remarks>合成した祖先groupのprepared応答プロトコルを実store・小SQLiteへ接続する。通常UIのwhole-folder到達は保証せず、実解析のkeeper保持は別代表が担当する。</remarks>
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
            string configPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(configPath);
            settings.OperationModeLR2DB = false; settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([folder]);
            settings.BMSRootPath = folder; settings.BMSInstallDir = folder; settings.Save();
            byte[] registeredConfig = File.ReadAllBytes(configPath);
            string resource = Path.Combine(folder, "user.wav"); File.WriteAllText(resource, "keep resource");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => BmsLibraryOptionsSnapshot.CreateCurrent(settings), settings: settings)
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
                () => true, Directory.Exists, _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), new BmsLibraryDuplicateMaintenanceStore());
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
                Assert.IsTrue(Directory.Exists(folder));
                Assert.AreEqual("keep resource", File.ReadAllText(resource));
                CollectionAssert.AreEqual(registeredConfig, File.ReadAllBytes(configPath));
                CollectionAssert.AreEqual(new[] { folder }, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
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
                Assert.IsTrue(Directory.Exists(folder));
                Assert.AreEqual("keep resource", File.ReadAllText(resource));
                CollectionAssert.AreEqual(registeredConfig, File.ReadAllBytes(configPath));
                CollectionAssert.AreEqual(new[] { folder }, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
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

    /// <summary>
    /// 固定した全フォルダ候補のYes・No・未選択閉鎖を変更前に判断し、承認範囲だけ実削除して所持行を保存し、判断中の受付解放や未承認資源の削除を検出する。
    /// </summary>
    /// <remarks>合成した祖先groupのprepared応答プロトコルを実store・小SQLiteへ接続する。通常UIのwhole-folder到達は保証せず、実解析のkeeper保持は別代表が担当する。</remarks>
    // 0は件数確認なし・フォルダYes、1は件数確認あり・フォルダNo、2は件数確認あり・未選択閉鎖。
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
                    Assert.AreEqual(MessageBoxButton.YesNoCancel, request.Button);
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
                () => decision != 0, Directory.Exists, _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), store);
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
                    2 => UiDialogResult.ClosedByUser(MessageBoxResult.Yes),
                    _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
                };
                confirmation.SetResult(answer);
                DuplicateMaintenanceMutationResult result = await operation;
                Assert.AreEqual(decision == 0 ? 0 : 1, countPrompts);
                Assert.AreEqual(1, folderPrompts);
                Assert.AreEqual(1, store.PrepareCount);
                Assert.AreEqual(decision == 2 ? 0 : 1, store.ExecuteCount);
                if (decision != 2)
                {
                    Assert.IsNotNull(store.ApprovedPaths);
                    CollectionAssert.AreEqual(decision == 0 ? new[] { deletionFolder } : [], store.ApprovedPaths.ToArray());
                }
                Assert.AreEqual(0, modelDialogs.ModelMessages);
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.IsTrue(File.Exists(keeperPath));
                Assert.IsTrue(Directory.Exists(keeperFolder));
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(decision != 2, result.Succeeded);
                Assert.IsNull(result.Failure);
                Assert.AreEqual(decision == 2 ? 0 : 1, result.RemovedChartCount);
                Assert.AreEqual(decision == 2, File.Exists(deletionPath));
                Assert.AreEqual(decision != 0, File.Exists(resourcePath));
                Assert.AreEqual(decision != 0, Directory.Exists(deletionFolder));
                CollectionAssert.AreEquivalent(decision == 2 ? new[] { keeperPath, deletionPath } : [keeperPath],
                    readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
                if (decision == 2) { Assert.AreEqual(0, events.Count); }
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

    /// <summary>先行Yesの後も回答収集だけを行い、Cancelと既定Yes付きCloseは全要求を取消し、明示Noだけを一回の実行へ渡します。</summary>
    [DataTestMethod]
    [DataRow("no")]
    [DataRow("cancel")]
    [DataRow("close")]
    public async Task RunHashCleanupAsync_CollectsWholeFolderDecisionsBeforeStartingMutation(string decision)
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string keeper = Path.Combine(root, "a.bms");
            string duplicate = Path.Combine(root, "long-name.bms");
            File.WriteAllText(keeper, "same"); File.WriteAllText(duplicate, "same");
            var events = new List<string>();
            var store = new RecordingStore(events) { WholeFolderCandidates = [@"C:\One", @"C:\Two"] };
            var presentation = new RecordingPresentation(events);
            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = new List<UiConfirmationRequest>();
            var dialogs = new FakeUiDialogService
            {
                ConfirmationHandler = request =>
                {
                    if (request.Button == MessageBoxButton.OKCancel)
                    {
                        Assert.AreEqual(MessageBoxResult.Cancel, request.DefaultResult);
                        return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    }
                    Assert.AreEqual(MessageBoxButton.YesNoCancel, request.Button);
                    Assert.AreEqual(MessageBoxResult.Yes, request.DefaultResult);
                    requests.Add(request);
                    if (requests.Count == 1) { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)); }
                    reached.TrySetResult(true);
                    return answer.Task;
                }
            };
            DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, presentation, dialogs, store, gate: gate, activity: activity);
            Task<DuplicateMaintenanceMutationResult>? operation = null;
            void AssertNotStarted()
            {
                Assert.AreEqual(0, store.PhysicalMutationStarts);
                Assert.AreEqual(0, store.Charts.Count);
                Assert.AreEqual(0, events.Count);
                Assert.IsFalse(activity.IsActive);
            }
            try
            {
                operation = owner.RunHashCleanupAsync(new DuplicateGroup([CreateChart(keeper, "hash"), CreateChart(duplicate, "hash")], [root]), root);
                Assert.AreSame(reached.Task, await Task.WhenAny(reached.Task, operation));
                await reached.Task;
                Assert.IsTrue(gate.IsActive);
                AssertNotStarted();
                answer.SetResult(decision switch
                {
                    "no" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.No),
                    "close" => UiDialogResult.ClosedByUser(MessageBoxResult.Yes),
                    _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
                });
                DuplicateMaintenanceMutationResult result = await operation;
                Assert.AreEqual(2, requests.Count);
                Assert.IsFalse(gate.IsActive);
                Assert.IsNull(result.Failure);
                Assert.AreEqual(decision == "no", result.Succeeded);
                if (decision == "no")
                {
                    Assert.AreEqual(1, store.PhysicalMutationStarts);
                    Assert.AreEqual(1, events.Count(value => value == "store-remove"));
                    CollectionAssert.AreEqual(store.WholeFolderCandidates.Where(path => requests[0].MessageBoxText.Contains(path)).ToArray(),
                        store.ApprovedPaths.ToArray());
                    Assert.AreEqual(duplicate, store.Charts.Single().Path);
                }
                else { AssertNotStarted(); }
                Assert.IsTrue(File.Exists(keeper)); Assert.IsTrue(File.Exists(duplicate));
            }
            finally
            {
                answer.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
                try { if (operation != null) { await operation; } }
                finally
                {
                    owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                    activity.ActivityChanged -= presentation.OnActivityChanged;
                }
            }
        });
    }

    /// <summary>保護groupだけで確定実対象が0なら、preparedプロトコルの削除で不要な再生停止を始めません。</summary>
    [TestMethod]
    public async Task RunHashCleanupAsync_ProtectedPreparedGroupDoesNotStopPlayback()
    {
        var events = new List<string>();
        var presentation = new RecordingPresentation(events);
        var store = new RecordingStore(events) { WholeFolderCandidates = [@"C:\Songs"], RegisteredUserRoots = [@"C:\Songs"] };
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        var dialogs = new FakeUiDialogService
        {
            ConfirmationHandler = request => Task.FromResult(
            UiDialogResult.FromMessageBoxResult(request.Button == MessageBoxButton.OKCancel ? MessageBoxResult.OK : MessageBoxResult.Yes))
        };
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, presentation, dialogs, store, gate: gate, activity: activity);
        try
        {
            await owner.RunHashCleanupAsync(new DuplicateGroup([CreateChart(@"C:\Songs\a.bms", "hash"),
                CreateChart(@"C:\Songs\long-name.bms", "hash")], [@"C:\Songs"]), @"C:\Songs");
            Assert.AreEqual(1, events.Count(value => value == "store-remove"));
            Assert.AreEqual(0, store.Charts.Count);
            Assert.AreEqual(0, store.ApprovedPaths.Count);
            Assert.IsFalse(events.Contains("playback-stop"));
            Assert.IsFalse(gate.IsActive);
            Assert.IsFalse(activity.IsActive);
        }
        finally
        {
            owner.WorkflowChanged -= presentation.OnWorkflowChanged;
            activity.ActivityChanged -= presentation.OnActivityChanged;
        }
    }

    /// <summary>
    /// 先行フォルダのYesがあっても後続の表示失敗・null応答・例外では全変更を始めず元原因と入力を保ち、確認途中の部分実行を検出する。
    /// </summary>
    // 0は表示失敗結果、1はnull応答、2は確認portの例外。いずれも二件目で注入する。
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
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
                if (failureKind == 3) { return Task.FromResult(UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable)); }
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
            if (failureKind == 3) { StringAssert.Contains(result.Failure.Message, "OwnerUnavailable"); }
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

    /// <summary>実フォルダ統合の旧資源退避後に復元不能が生じると、通常失敗へ吸収せず、所有する実処理とL/P・activity解放後に終了へ接続する。</summary>
    /// <remarks>本番store・FS・SQLiteから危険事実を生産し、最終終了だけを記録へ差し替える。補償条件の全組合せは下位executorが分担する。</remarks>
    [TestMethod]
    public async Task RunFolderMergeAsync_UnrestoredDestinationExitsAfterOwnedTerminal()
    {
        string root = Path.Combine(Path.GetTempPath(), "MergeUnrestored_" + Guid.NewGuid().ToString("N"));
        Task<DuplicateMaintenanceMutationResult>? operation = null;
        TestBmsLibrary? library = null;
        using var releaseCleanup = new ManualResetEventSlim();
        try
        {
            string source = Path.Combine(root, "source");
            string destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string sourceChart = Path.Combine(source, "source.bms");
            string destinationChart = Path.Combine(destination, "owned.bms");
            File.WriteAllText(sourceChart, "#TITLE Source\r\n#BPM 120\r\n#WAV01 note.wav\r\n#00111:01\r\n");
            File.WriteAllText(destinationChart, "#TITLE Owned\r\n#BPM 120\r\n#WAV01 note.wav\r\n#00111:01\r\n");
            File.WriteAllText(Path.Combine(source, "note.wav"), "new resource");
            File.WriteAllText(Path.Combine(destination, "note.wav"), "old resource");
            ChartFile[] charts = [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChart)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(destinationChart))];
            string songDbPath = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDbPath, []);
            using (var initialize = new LR2SongDBExtended(songDbPath)) { }
            var files = new UnrestoredDestinationFileMutationService(Path.Combine(destination, "note.wav"));
            var playlist = new ChartFileOperationSynchronizer();
            var configOwner = new Lr2ConfigOwner(BmsPlaylistTestSupport.CreateLr2Config(Path.Combine(root, "LR2"), root));
            library = new TestBmsLibrary(songDbPath, configOwner, null, files,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                    LR2CustomFolderOutputBaseDir = root,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                playlistOperationAdmission: playlist)
            { BmsCharts = charts.ToList(), BmsonCharts = [] };
            new BmsLibraryDbGateway(songDbPath).UpsertSongs(charts);
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var activity = new ChartMutationActivityOwner();
            var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            activity.ActivityChanged += (_, _) => { if (!activity.IsActive) { cleanupReached.TrySetResult(); releaseCleanup.Wait(); } };
            int exits = 0;
            bool releasedAtExit = false;
            Exception? exitFailure = null;
            FakeUiDialogService dialogs = AcceptedDialogs();
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, gate, activity,
                new NoOpChartMutationPlaybackPort(), dialogs, () => false, Directory.Exists,
                _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), new BmsLibraryDuplicateMaintenanceStore(), failure =>
                {
                    exits++;
                    exitFailure = failure;
                    releasedAtExit = !gate.IsActive && !playlist.IsActive && !activity.IsActive;
                    return Task.CompletedTask;
                });
            operation = owner.RunFolderMergeAsync(source, destination, new DuplicateGroup(charts.ToList(), [source, destination]));
            Assert.AreSame(cleanupReached.Task, await Task.WhenAny(cleanupReached.Task, operation));
            Assert.AreEqual(0, exits);
            Assert.IsFalse(operation.IsCompleted);
            Assert.IsTrue(gate.IsActive);
            Assert.IsTrue(playlist.IsActive);
            releaseCleanup.Set();
            DuplicateMaintenanceMutationResult result = await operation;
            Assert.IsNotNull(result.MutationReceipt);
            Assert.IsTrue(result.MutationReceipt.SessionReceipt.HasUnrestoredExistingDestination);
            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(result.MutationReceipt.SessionReceipt.DurableCommit);
            Assert.AreEqual(1, exits);
            Assert.IsTrue(releasedAtExit);
            Assert.AreSame(result.MutationReceipt.SessionReceipt.PrimaryFailure, exitFailure);
            Assert.AreEqual(2, files.FailedMoveCount);
            Assert.IsNotNull(files.BackupPath);
            Assert.AreEqual("old resource", File.ReadAllText(files.BackupPath));
            Assert.IsTrue(File.Exists(sourceChart));
            Assert.IsTrue(File.Exists(destinationChart));
            using LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(2, db.ExecuteScalar<int>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0, dialogs.Messages.Count);
        }
        finally
        {
            releaseCleanup.Set();
            try { if (operation != null) { await operation; } }
            finally { library?.RequestShutdown("merge-unrestored-fixture"); if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
        }
    }

    /// <summary>三以上の統合では消失する元folderでなく宛先の所属を捕捉し、同見出しの別群や次群を選ばないことを確認します。</summary>
    [TestMethod]
    public async Task RunFolderMergeAsync_ThreeFoldersSelectsSurvivingDestinationGroup()
    {
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";
        string remaining = @"C:\Songs\Remaining";
        var original = new DuplicateGroup([], [source, destination, remaining]);
        ChartFile expected = CreateChart(Path.Combine(destination, "chart.bms"), "survivor");
        ChartFile other = CreateChart(@"C:\Songs\Other\chart.bms", "other");
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner([], new RecordingPresentation([]), AcceptedDialogs(), new RecordingStore([]));
        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(source, destination, original);
        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.SelectionContext);
        DuplicateGroup[] current = [new([other], [@"C:\Songs\Other"]), new([expected], [destination, remaining])];
        DuplicateGroup selected = result.SelectionContext.ResolveGroup(current);
        Assert.IsNotNull(selected);
        CollectionAssert.AreEqual(new[] { expected.Path }, selected.ChartFiles.Select(chart => chart.Path).ToArray());
    }

    private sealed class SelectionPresentationState : IMaintenanceTreePresentationState
    {
        public List<DuplicateGroup> DuplicateChartGroups { get; set; } = [];
        public int DuplicateChartGroupsInvalidationVersion => 0;
        public bool IsWriteLockHeldInitializdBMSFilesHealthStatus => false;
        public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo => false;
        public bool IsWriteLockHeldInitializeBMSFilesZeroNote => false;
        public bool IsWriteLockHeldDuplicateChartGroups => false;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private static DuplicateMaintenanceWorkflowOwner CreateOwner(
        List<string> events,
        RecordingPresentation presentation,
        IUiDialogService dialogs,
        RecordingStore store,
        Func<string, bool>? directoryExists = null,
        Func<string, ExplorerOpenResult>? explorerOpen = null,
        Func<DuplicateGroup, DuplicateViewContext?>? duplicateGroupNextSelectionProvider = null,
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
            duplicateGroupNextSelectionProvider ?? (_ => null),
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
        internal IReadOnlyList<string> RegisteredUserRoots { get; set; } = [];
        internal IReadOnlyList<string> ApprovedPaths { get; private set; } = [];
        internal int PhysicalMutationStarts { get; private set; }

        public bool TryBeginPhysicalMutation(BMSLibrary library, IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease? lease)
        { PhysicalMutationStarts++; lease = null; return true; }

        public LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts)
            => new(charts, WholeFolderCandidates, [], charts.Count, 0, registeredUserRoots: RegisteredUserRoots);

        public LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderPaths)
        {
            IReadOnlyList<ChartFile> charts = prepared.Targets;
            events.Add("store-remove");
            ApprovedPaths = approvedWholeFolderPaths;
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
