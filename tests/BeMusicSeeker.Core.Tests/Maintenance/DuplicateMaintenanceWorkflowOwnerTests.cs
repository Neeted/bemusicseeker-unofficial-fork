using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.DuplicateMaintenanceWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DuplicateMaintenanceWorkflowOwnerTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

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
    [TestMethod]
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
    [TestMethod]
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

        Assert.ThrowsExactly<ArgumentException>(() => owner.RunFolderMergeAsync(
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
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(
            caseWorkspace1.RootDirectory,
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
    [TestMethod]
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

    /// <summary>先行Yesの後も回答収集だけを行い、Cancelと既定Yes付きCloseは全要求を取消し、明示Noだけを一回の実行へ渡します。</summary>
    [TestMethod]
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
    [TestMethod]
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
        using var caseWorkspace2 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace2.RootDirectory, "BeMusicSeeker_DuplicateFolderFailure_" + Guid.NewGuid().ToString("N"));
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
        using var caseWorkspace3 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace3.RootDirectory, "MergeUnrestored_" + Guid.NewGuid().ToString("N"));
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
            using (LR2SongDBExtended initialize = databaseScope.OpenSongWrite(songDbPath)) { }
            var files = new UnrestoredDestinationFileMutationService(Path.Combine(destination, "note.wav"));
            var playlist = new ChartFileOperationSynchronizer();
            var configOwner = new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(Path.Combine(root, "LR2"), root));
            library = new TestBmsLibrary(databaseScope, songDbPath, configOwner, null, files,
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
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs(charts);
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
            using LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
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

    /// <summary>
    /// 共通受付Lを外部が保持している間のフォルダ統合・ハッシュ整理は、確認・準備・変更へ進まず、受付使用中の警告（本文・題名・Exclamation・OK）を一度だけ表示して、
    /// 失敗を持たない受付使用中を返す。解放だけでは実行せず、次の明示要求は警告なしで一度実行する。黙った拒否、確認の表示、副作用、自動再実行を検出する。
    /// </summary>
    [TestMethod]
    [DataRow("merge")]
    [DataRow("hash")]
    public async Task BusyAdmission_RejectsExplicitMaintenanceWithOneWarningAndRunsNextRequestAfterRelease(string operation)
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        FakeUiDialogService dialogs = AcceptedDialogs();
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store, gate: gate, activity: activity);
        Assert.IsTrue(gate.TryEnter(out IDisposable held));
        using (held)
        {
            DuplicateMaintenanceMutationResult rejected = await RunDuplicateOperationAsync(owner, operation);

            AssertBusyWarning(dialogs, rejected);
            Assert.IsNull(dialogs.ConfirmationRequest, "使用中の要求では確認を表示しません。");
            Assert.AreEqual(0, store.PhysicalMutationStarts);
            CollectionAssert.AreEqual(Array.Empty<string>(), events);
            Assert.IsFalse(gate.CanEnter);
        }

        Assert.AreEqual(0, store.PhysicalMutationStarts, "解放だけでは拒否した要求を実行しません。");
        dialogs.Messages.Clear();
        DuplicateMaintenanceMutationResult next = await RunDuplicateOperationAsync(owner, operation);
        Assert.IsTrue(next.Succeeded, next.Failure?.ToString());
        Assert.IsFalse(next.Busy);
        Assert.AreEqual(1, store.PhysicalMutationStarts);
        Assert.AreEqual(0, dialogs.Messages.Count(message => message.MessageBoxText == BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy));
        Assert.IsTrue(gate.CanEnter);
    }

    /// <summary>
    /// 確認後に対象別受付Pを取得できないフォルダ統合・ハッシュ整理は、活動・再生停止・変更へ進まず、LとPの解放後に受付使用中の警告を一度だけ表示する。
    /// 結果は失敗を持たない非成功の受付使用中で、成功時の選択・FS/DB報告・一般失敗へ進まない。P拒否を成功として扱う誤りや、解放前の表示を検出する。
    /// </summary>
    [TestMethod]
    [DataRow("merge")]
    [DataRow("hash")]
    public async Task PhysicalAdmissionBusyAfterConfirmation_WarnsOnceAfterReleaseWithoutSuccess(string operation)
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { PhysicalMutationAdmitted = false };
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        FakeUiDialogService dialogs = AcceptedDialogs();
        bool warnedAfterRelease = false;
        dialogs.OnMessage = () => warnedAfterRelease = gate.CanEnter && !activity.IsActive;
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store, gate: gate, activity: activity);

        DuplicateMaintenanceMutationResult result = await RunDuplicateOperationAsync(owner, operation);

        AssertBusyWarning(dialogs, result);
        Assert.IsTrue(warnedAfterRelease, "警告はLとPの解放後に表示します。");
        Assert.IsNotNull(dialogs.ConfirmationRequest);
        Assert.AreEqual(1, store.PhysicalMutationStarts);
        Assert.AreEqual(0, result.RemovedChartCount);
        Assert.IsNull(result.RemovalOutcome);
        CollectionAssert.AreEqual(Array.Empty<string>(), events, "活動・再生停止・更新抑制・変更へ進みません。");
        Assert.IsTrue(gate.CanEnter);
    }

    private static Task<DuplicateMaintenanceMutationResult> RunDuplicateOperationAsync(
        DuplicateMaintenanceWorkflowOwner owner, string operation)
    {
        if (operation == "merge")
        {
            string source = @"C:\Songs\Source";
            string destination = @"C:\Songs\Destination";
            return owner.RunFolderMergeAsync(source, destination, new DuplicateGroup([], [source, destination]));
        }
        ChartFile first = CreateChart(@"C:\Songs\a.bms", "hash");
        ChartFile second = CreateChart(@"C:\Songs\long-name.bms", "hash");
        return owner.RunHashCleanupAsync(new DuplicateGroup([first, second], [@"C:\Songs"]), @"C:\Songs");
    }

    private static void AssertBusyWarning(FakeUiDialogService dialogs, DuplicateMaintenanceMutationResult result)
    {
        Assert.IsTrue(result.Busy);
        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure, "受付使用中は一般の失敗として通知しません。");
        UiMessageRequest warning = dialogs.Messages.Single();
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, warning.MessageBoxText);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warning, warning.Caption);
        Assert.AreEqual(MessageBoxImage.Exclamation, warning.Icon);
        Assert.AreEqual(MessageBoxButton.OK, warning.Button);
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
        return UninitializedBmsLibrary.Create();
    }

}
