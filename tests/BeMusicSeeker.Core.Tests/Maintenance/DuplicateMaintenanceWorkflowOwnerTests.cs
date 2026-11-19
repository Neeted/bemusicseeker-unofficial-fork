using System.ComponentModel;
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
using Lists = BeMusicSeeker.TestSupport.Components.DuplicateMergeCandidateListTestSupport;

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
    /// 同一ハッシュで更新日時が古い譜面（同日時なら最短ファイル名）を残し、確認済み削除だけの件数と保存後失敗を返して、未実行対象の成功集計や保持対象の削除、名前の短さを古さより優先する選定を検出する。
    /// </summary>
    // catalogFailure: falseは正常保存反映、trueは削除確定後の必須カタログ反映失敗。
    // longerIsOlder: trueは名前が長い方が更新日時の古い実ファイルで、名前の短さより古さを優先して長い方を残す行。
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task RunHashCleanupAsync_ChoosesShortestNameAndReturnsRemovalCount(bool catalogFailure, bool longerIsOlder)
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
            if (longerIsOlder)
            {
                // 更新日時が古い方を残す。名前が長い方が古ければ、短い名前を優先せず長い方を残す。
                File.WriteAllText(keeper.Path, "short name, newer");
                File.WriteAllText(duplicate.Path, "long name, older");
                File.WriteAllText(missing.Path, "longest name, newest");
                File.SetLastWriteTimeUtc(missing.Path, new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(keeper.Path, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(duplicate.Path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
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
            CollectionAssert.AreEqual(longerIsOlder ? new[] { keeper, missing } : new[] { duplicate, missing }, (System.Collections.ICollection)store.Charts);
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
            var progress = new List<DuplicateMaintenanceProgressSnapshot>();
            bool progressClosedAtExit = false;
            FakeUiDialogService dialogs = AcceptedDialogs();
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, gate, activity,
                new NoOpChartMutationPlaybackPort(), dialogs, () => false, Directory.Exists,
                _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), new BmsLibraryDuplicateMaintenanceStore(), failure =>
                {
                    exits++;
                    exitFailure = failure;
                    releasedAtExit = !gate.IsActive && !playlist.IsActive && !activity.IsActive;
                    lock (progress) { progressClosedAtExit = progress.Count == 2 && !progress[1].IsActive; }
                    return Task.CompletedTask;
                });
            owner.ProgressChanged += snapshot => { lock (progress) { progress.Add(snapshot); } };
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
            Assert.IsTrue(progressClosedAtExit, "進捗行は終了処理の呼出しより前に閉じます。");
            Assert.AreEqual(source, progress[0].TargetPath);
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

    /// <summary>
    /// フォルダ統合の確認文が、統合元の配下にフォルダがある場合だけ件数と配下も移す旨を添え、配下がなければ従来の文のままであることを確認します。
    /// 件数を数えられなかった場合は配下の存在を断定しない文を添え、確認を止めずに統合を続けます。
    /// 配下も移す事実を操作前に知らせない誤りと、配下がないのに案内する誤り、数えられない場合に確認や統合が失われる誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(0, DisplayName = "配下なしは追記なし")]
    [DataRow(3, DisplayName = "配下の件数を追記")]
    [DataRow(-1, DisplayName = "数えられない場合は件数なしで追記")]
    public async Task RunFolderMergeAsync_ConfirmationMentionsNestedFolders(int nestedCount)
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK) };
        var requestedSources = new List<string>();
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store,
            nestedFolderCount: source =>
            {
                requestedSources.Add(source);
                return nestedCount < 0
                    ? Task.FromException<int>(new IOException("count failed"))
                    : Task.FromResult(nestedCount);
            });
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(
            source, destination, new DuplicateGroup([], [source, destination]));

        string baseMessage = BeMusicSeeker.Properties.Resources.Msg_merge_bms_folder
            + Environment.NewLine + Environment.NewLine
            + BeMusicSeeker.Properties.Resources.Msg_merge_bms_target + ": " + source + Environment.NewLine
            + BeMusicSeeker.Properties.Resources.Msg_merge_bms_destination + ": " + destination;
        string expected = nestedCount switch
        {
            0 => baseMessage,
            > 0 => baseMessage + Environment.NewLine + Environment.NewLine
                + string.Format(BeMusicSeeker.Properties.Resources.Msg_merge_bms_nested_folders, nestedCount),
            _ => baseMessage + Environment.NewLine + Environment.NewLine
                + BeMusicSeeker.Properties.Resources.Msg_merge_bms_nested_folders_unknown,
        };
        Assert.AreEqual(expected, dialogs.ConfirmationRequest!.MessageBoxText);
        CollectionAssert.AreEqual(new[] { source }, requestedSources, "件数は統合元だけを数える。");
        Assert.IsTrue(result.Succeeded, "件数を数えられなくても統合は続く。");
        Assert.AreEqual(1, events.Count(value => value == "store-merge"));
    }

    /// <summary>
    /// 確認を表示しない設定では、配下のフォルダの件数を数えず追記もせず、そのまま統合することを確認します。
    /// 利用者が確認を無効にしたのに無駄な列挙を行う誤りと、確認なしの統合が止まる誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task RunFolderMergeAsync_ConfirmationDisabled_SkipsCount()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var dialogs = new FakeUiDialogService();
        int countCalls = 0;
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store,
            showConfirmation: false,
            nestedFolderCount: _ =>
            {
                countCalls++;
                return Task.FromResult(5);
            });
        string source = @"C:\Songs\Source";
        string destination = @"C:\Songs\Destination";

        DuplicateMaintenanceMutationResult result = await owner.RunFolderMergeAsync(
            source, destination, new DuplicateGroup([], [source, destination]));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, countCalls);
        Assert.IsNull(dialogs.ConfirmationRequest);
        Assert.AreEqual(1, events.Count(value => value == "store-merge"));
    }

    /// <summary>
    /// 実ファイルシステムの数え方が、統合元の配下の全階層のフォルダ（隠しフォルダを含む）だけを数え、ファイルを数えないことを確認します。
    /// 配下が一階層だけ・ファイルを含めて数える・隠しフォルダを落とす誤りと、存在しない起点を件数0として隠す誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task NestedFolderCounter_CountsAllLevelsIncludingHiddenFoldersButNotFiles()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string source = Directory.CreateDirectory(workspace.GetPath("Source")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "A", "B"));
        Directory.CreateDirectory(Path.Combine(source, "C"));
        DirectoryInfo hidden = Directory.CreateDirectory(Path.Combine(source, "A", ".hidden"));
        hidden.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(Path.Combine(source, "root.bms"), "x");
        File.WriteAllText(Path.Combine(source, "A", "B", "nested.bms"), "x");
        string empty = Directory.CreateDirectory(workspace.GetPath("Empty")).FullName;

        Assert.AreEqual(4, await NestedFolderCounter.CountAsync(source));
        Assert.AreEqual(0, await NestedFolderCounter.CountAsync(empty));
        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(
            () => NestedFolderCounter.CountAsync(workspace.GetPath("Missing")));
    }

    /// <summary>
    /// フォルダ統合と重複削除の進捗の事実は、全確認の承認後・背景の変更処理（対象別受付・活動・再生停止・抑制・物理変更）の開始前に一度始まり、
    /// 背景処理の終端（成功・対象別受付の使用中・失敗）の後、結果の報告と返却より前に一度終わる。詳細は統合では統合元、削除では対象フォルダ。
    /// 確認中の開始、統合先や譜面パスの表示、終端による行の残留、報告後の終了を検出する。確認を表示しない設定でも同じ開始・終了を通知する。
    /// </summary>
    [TestMethod]
    [DataRow("merge", "success", true)]
    [DataRow("hash", "success", true)]
    [DataRow("merge", "physicalBusy", true)]
    [DataRow("hash", "physicalBusy", true)]
    [DataRow("merge", "failure", true)]
    [DataRow("hash", "failure", true)]
    [DataRow("merge", "success", false)]
    [DataRow("hash", "success", false)]
    public async Task ProgressFacts_StartAfterApprovalAndEndBeforeReportOnEveryTerminal(string operation, string terminal, bool showConfirmation)
    {
        var events = new List<string>();
        RecordingStore store = terminal == "failure" ? new ThrowingStore(events) : new RecordingStore(events);
        store.PhysicalMutationAdmitted = terminal != "physicalBusy";
        store.WholeFolderCandidates = operation == "hash" ? [@"C:\One", @"C:\Two"] : [];
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        int answered = 0;
        var dialogs = new FakeUiDialogService
        {
            // 件数確認はOK、フォルダ単位の確認は一件目Yes・二件目Noで、最後の回答まで開始しないことを確かめる。
            ConfirmationHandler = request => Task.FromResult(UiDialogResult.FromMessageBoxResult(
                request.Button == MessageBoxButton.OKCancel ? MessageBoxResult.OK
                    : ++answered == 1 ? MessageBoxResult.Yes : MessageBoxResult.No))
        };
        int prompts = 0;
        Func<UiConfirmationRequest, Task<UiDialogResult>> answer = dialogs.ConfirmationHandler;
        dialogs.ConfirmationHandler = request => { Interlocked.Increment(ref prompts); return answer(request); };
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store,
            showConfirmation: showConfirmation, gate: gate, activity: activity);
        int expectedPrompts = (showConfirmation ? 1 : 0) + (operation == "hash" ? 2 : 0);
        var progress = new List<(DuplicateMaintenanceProgressSnapshot Snapshot, int Prompts, int PhysicalStarts, string[] Events, int Messages)>();
        owner.ProgressChanged += snapshot =>
        {
            string[] seen;
            lock (events) { seen = [.. events]; }
            lock (progress) { progress.Add((snapshot, Volatile.Read(ref prompts), store.PhysicalMutationStarts, seen, dialogs.Messages.Count)); }
        };

        DuplicateMaintenanceMutationResult result = await RunDuplicateOperationAsync(owner, operation);

        Assert.AreEqual(2, progress.Count, "開始と終了を一度ずつ通知します。");
        (DuplicateMaintenanceProgressSnapshot Snapshot, int Prompts, int PhysicalStarts, string[] Events, int Messages) start = progress[0];
        Assert.IsTrue(start.Snapshot.IsActive);
        Assert.AreEqual(operation == "merge" ? @"C:\Songs\Source" : @"C:\Songs", start.Snapshot.TargetPath);
        Assert.AreEqual(expectedPrompts, start.Prompts, "全確認への回答後に開始します。");
        Assert.AreEqual(0, start.PhysicalStarts, "対象別受付の取得より前に開始します。");
        Assert.AreEqual(0, start.Events.Length, "活動・再生停止・抑制・変更より前に開始します。");
        (DuplicateMaintenanceProgressSnapshot Snapshot, int Prompts, int PhysicalStarts, string[] Events, int Messages) end = progress[1];
        Assert.IsFalse(end.Snapshot.IsActive);
        Assert.AreEqual(1, end.PhysicalStarts);
        Assert.AreEqual(0, end.Messages, "結果の報告より前に終了します。");
        if (terminal == "physicalBusy")
        {
            Assert.AreEqual(0, end.Events.Length);
            AssertBusyWarning(dialogs, result);
        }
        else
        {
            CollectionAssert.Contains(end.Events, "suppression-end");
            CollectionAssert.Contains(end.Events, "activity-end");
            Assert.AreEqual(terminal == "success", result.Succeeded, result.Failure?.ToString());
        }
        Assert.IsTrue(gate.CanEnter);
    }

    /// <summary>
    /// 共通受付の拒否（使用中・受付閉鎖）、確認の取消・表示失敗（件数確認とフォルダ単位の確認）、重複削除の事前準備の失敗、削除対象なしでは、
    /// 進捗の開始も終了も通知しない。背景処理を始めない経路での行の表示と、開始のない終了通知を検出する。
    /// </summary>
    [TestMethod]
    [DataRow("merge", "busy")]
    [DataRow("hash", "busy")]
    [DataRow("merge", "closed")]
    [DataRow("hash", "closed")]
    [DataRow("merge", "cancel")]
    [DataRow("hash", "cancel")]
    [DataRow("merge", "confirmFailed")]
    [DataRow("hash", "confirmFailed")]
    [DataRow("hash", "folderCancel")]
    [DataRow("hash", "folderFailed")]
    [DataRow("hash", "prepareFailed")]
    [DataRow("hash", "noWork")]
    public async Task ProgressFacts_NotPublishedWhenOperationEndsBeforeBackgroundMutation(string operation, string condition)
    {
        var events = new List<string>();
        RecordingStore store = condition == "prepareFailed" ? new ThrowingStore(events) { ThrowOnPrepare = true } : new RecordingStore(events);
        store.WholeFolderCandidates = condition.StartsWith("folder", StringComparison.Ordinal) ? [@"C:\One"] : [];
        var gate = new ChartFileOperationSynchronizer();
        var dialogs = new FakeUiDialogService
        {
            ConfirmationHandler = request =>
            {
                bool countConfirmation = request.Button == MessageBoxButton.OKCancel;
                return Task.FromResult(condition switch
                {
                    "cancel" when countConfirmation => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel),
                    "confirmFailed" when countConfirmation => UiDialogResult.Failed(new IOException("confirmation failed")),
                    "folderCancel" when !countConfirmation => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel),
                    "folderFailed" when !countConfirmation => UiDialogResult.Failed(new IOException("folder confirmation failed")),
                    _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
                });
            }
        };
        DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store, gate: gate);
        int notifications = 0;
        owner.ProgressChanged += _ => Interlocked.Increment(ref notifications);
        IDisposable? held = null;
        if (condition == "busy") { Assert.IsTrue(gate.TryEnter(out held)); }
        if (condition == "closed") { gate.CloseAdmission(); }

        DuplicateMaintenanceMutationResult result;
        using (held)
        {
            result = condition == "noWork"
                ? await owner.RunHashCleanupAsync(new DuplicateGroup([CreateChart(@"C:\Songs\a.bms", "hash")], [@"C:\Songs"]), @"C:\Songs")
                : await RunDuplicateOperationAsync(owner, operation);
        }

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, Volatile.Read(ref notifications));
        Assert.AreEqual(0, store.PhysicalMutationStarts);
        Assert.AreEqual(0, events.Count);
    }

    /// <summary>
    /// 進捗の受け手が開始時・終了時に例外を投げても、操作の結果・受付と活動の解放・報告の回数は変わらず、他の受け手は開始と終了の両方を受け取る。
    /// 任意の表示の失敗で処理が止まる誤りと、開始の例外で終了通知が欠けて行が残る誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("merge", "success", true, true)]
    [DataRow("hash", "physicalBusy", false, true)]
    [DataRow("merge", "failure", true, false)]
    public async Task ProgressSubscriberFailure_KeepsResultReleaseAndReport(string operation, string terminal, bool throwOnStart, bool throwOnEnd)
    {
        async Task<(DuplicateMaintenanceMutationResult Result, int Messages, int Received, bool Released)> RunAsync(bool subscriberThrows)
        {
            var events = new List<string>();
            RecordingStore store = terminal == "failure" ? new ThrowingStore(events) : new RecordingStore(events);
            store.PhysicalMutationAdmitted = terminal != "physicalBusy";
            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            FakeUiDialogService dialogs = AcceptedDialogs();
            DuplicateMaintenanceWorkflowOwner owner = CreateOwner(events, new RecordingPresentation(events), dialogs, store, gate: gate, activity: activity);
            int received = 0;
            if (subscriberThrows)
            {
                owner.ProgressChanged += snapshot =>
                {
                    if (snapshot.IsActive ? throwOnStart : throwOnEnd) { throw new InvalidOperationException("progress presentation failed"); }
                };
            }
            owner.ProgressChanged += _ => Interlocked.Increment(ref received);
            DuplicateMaintenanceMutationResult result = await RunDuplicateOperationAsync(owner, operation);
            Assert.AreEqual(1, store.PhysicalMutationStarts);
            return (result, dialogs.Messages.Count, Volatile.Read(ref received), gate.CanEnter && !activity.IsActive);
        }

        (DuplicateMaintenanceMutationResult Result, int Messages, int Received, bool Released) expected = await RunAsync(subscriberThrows: false);
        (DuplicateMaintenanceMutationResult Result, int Messages, int Received, bool Released) actual = await RunAsync(subscriberThrows: true);

        Assert.AreEqual(2, actual.Received, "例外を投げる受け手の後ろの受け手も開始と終了を受け取ります。");
        Assert.AreEqual(expected.Result.Succeeded, actual.Result.Succeeded);
        Assert.AreEqual(expected.Result.Busy, actual.Result.Busy);
        Assert.AreEqual(expected.Result.Failure?.GetType(), actual.Result.Failure?.GetType());
        Assert.AreEqual(expected.Messages, actual.Messages);
        Assert.IsTrue(actual.Released);
    }

    /// <summary>保存処理または事前準備の例外で、背景の変更処理の失敗終端と準備失敗を再現します。</summary>
    private sealed class ThrowingStore(List<string> events) : RecordingStore(events)
    {
        internal bool ThrowOnPrepare { get; init; }

        public override DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
            BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId, LibraryOperationNotices notices)
            => throw new IOException("merge failed");

        public override LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared,
            IReadOnlyList<string> approvedWholeFolderPaths, LibraryOperationNotices notices, out Action? publication)
            => throw new IOException("remove failed");

        public override LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts)
            => ThrowOnPrepare ? throw new IOException("prepare failed") : base.PrepareChartRemoval(library, charts);
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
        ChartMutationActivityOwner? activity = null,
        Func<string, Task<int>>? nestedFolderCount = null)
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
            store,
            nestedFolderCountProvider: nestedFolderCount ?? (_ => Task.FromResult(0)));
        owner.WorkflowChanged += presentation.OnWorkflowChanged;
        activity.ActivityChanged += presentation.OnActivityChanged;
        return owner;
    }

    private static BMSLibrary CreateLibrary()
    {
        return UninitializedBmsLibrary.Create();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // 統合候補一覧からの一括整理（OrganizeSelectedGroupsAsync）。実際の一覧の管理主体（メモリ上のグループを採用）とワークフローの管理主体を組み、
    // ツールバーが呼ぶのと同じ入口を呼ぶ。保存窓口は記録用の代替を継承して要求と結果を差し替え、進捗は実際の背景実行（BackgroundWorker）で動かす。
    // 期待値の文言は、役割ごとのリソースの書式へ入力値を当てて作る。
    // ---------------------------------------------------------------------------------------------------------------

    private static readonly TimeSpan BatchDeadline = TimeSpan.FromSeconds(30);

    /// <summary>要求と進捗を記録し、結果を差し替えられる保存窓口の代替です。既定は、全グループの開始と反映段階への移行を通知して、全グループを完了として返します。</summary>
    private sealed class BatchStore(List<string> events) : RecordingStore(events)
    {
        internal List<DuplicateBatchOrganizeRequest> Requests { get; } = [];

        internal List<bool> OnPoolThread { get; } = [];

        internal Func<DuplicateBatchOrganizeRequest, Action<DuplicateBatchOrganizeProgress>?, CancellationToken, LibraryOperationNotices, DuplicateBatchOrganizeResult>? Handler { get; set; }

        public override DuplicateBatchOrganizeResult OrganizeDuplicateGroups(
            BMSLibrary library, DuplicateBatchOrganizeRequest request, long operationId,
            LibraryOperationNotices notices, Action<DuplicateBatchOrganizeProgress>? progress, CancellationToken cancellationToken)
        {
            AddEvent("store-organize");
            Requests.Add(request);
            OnPoolThread.Add(Thread.CurrentThread.IsThreadPoolThread && SynchronizationContext.Current == null);
            return Handler != null ? Handler(request, progress, cancellationToken, notices) : Complete(request, progress);
        }

        internal static DuplicateBatchOrganizeResult Complete(DuplicateBatchOrganizeRequest request, Action<DuplicateBatchOrganizeProgress>? progress)
        {
            for (int index = 0; index < request.Groups.Count; index++)
            {
                progress?.Invoke(new DuplicateBatchOrganizeProgress(index + 1, request.Groups.Count, request.Groups[index].KeepDirectory));
            }
            progress?.Invoke(new DuplicateBatchOrganizeProgress(request.Groups.Count, request.Groups.Count, null!, DuplicateBatchOrganizeProgressKind.Applying));
            return Result(request.Groups.Select(group => GroupResult(group.KeepDirectory, DuplicateBatchOrganizeGroupStatus.Completed,
                merged: group.SourceDirectories, single: group.IsSingleFolder)));
        }
    }

    private static DuplicateBatchOrganizeGroupResult GroupResult(
        string keep,
        DuplicateBatchOrganizeGroupStatus status,
        DuplicateBatchOrganizeSkipReason skip = DuplicateBatchOrganizeSkipReason.None,
        IEnumerable<string>? merged = null,
        IEnumerable<DuplicateBatchOrganizeRetainedSource>? retained = null,
        int trashed = 0,
        int trashFailed = 0,
        bool single = false)
        => new(keep, single, status, skip, null, merged, retained, trashed, trashFailed);

    private static LibraryMutationSessionReceipt DurableSession(
        Exception? applyFailure = null, Exception? finalizationFailure = null, Exception? cleanupFailure = null, bool durable = true,
        bool unrestored = false)
        => new([new LibraryMutationSessionTarget(@"C:\S\source", @"C:\S\keep")], durableCommit: durable,
            applyFailure: applyFailure, finalizationFailure: finalizationFailure, cleanupFailure: cleanupFailure, hasUnrestoredExistingDestination: unrestored);

    private static DuplicateBatchOrganizeResult Result(
        IEnumerable<DuplicateBatchOrganizeGroupResult> groups, bool canceled = false, LibraryMutationSessionReceipt? session = null, bool mergeApplied = true)
        => new(
            new DuplicateMergeMaintenanceReceipt(mergeApplied, ResourceHealthIndexUpdateMode.DeferOnUpdates, MaintenanceWorkflowResultFacts.From(null),
                false, false, false, false, false, session ?? DurableSession()),
            groups, canceled, admitted: true);

    /// <summary>一括整理の入口を、本番の受付・活動・再生・一覧の管理主体と同じ接続で組んだ検証用の一式です。</summary>
    private sealed class BatchFixture : IDisposable
    {
        private readonly SingleThreadSynchronizationContext ui = new();

        internal BatchFixture(DuplicateGroup[] groups, bool showConfirmation = true, Func<Exception, Task>? exitForUnrestoredDestination = null)
        {
            Groups = groups;
            Store = new BatchStore(Events);
            Presentation = new RecordingPresentation(Events);
            Dialogs = new FakeUiDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK), Events = Events };
            Dialogs.Progress.ReceiverContext = ui;
            Lists.Adopt(List, Library, groups);
            Owner = new DuplicateMaintenanceWorkflowOwner(
                CreateLibrary, Gate, Activity, Presentation, Dialogs, () => showConfirmation, _ => true, _ => new ExplorerOpenResult(), _ => null, Store,
                exitForUnrestoredDestination, candidateList: List);
            Owner.WorkflowChanged += Presentation.OnWorkflowChanged;
            Activity.ActivityChanged += Presentation.OnActivityChanged;
            Owner.ProgressChanged += snapshot => { lock (BottomProgress) { BottomProgress.Add(snapshot); } };
        }

        internal object Library { get; } = new();

        internal DuplicateGroup[] Groups { get; }

        internal List<string> Events { get; } = [];

        internal List<DuplicateMaintenanceProgressSnapshot> BottomProgress { get; } = [];

        internal DuplicateMergeCandidateListOwner List { get; } = new(_ => false, _ => { });

        internal BatchStore Store { get; }

        internal RecordingPresentation Presentation { get; }

        internal FakeUiDialogService Dialogs { get; }

        internal ChartFileOperationSynchronizer Gate { get; } = new();

        internal ChartMutationActivityOwner Activity { get; } = new();

        internal DuplicateMaintenanceWorkflowOwner Owner { get; }

        /// <summary>指定したグループのSELECTだけをオンにします。</summary>
        internal void Select(params DuplicateGroup[] groups)
        {
            List.ClearVisible();
            foreach (DuplicateGroup group in groups)
            {
                int index = List.RowSet.Select(row => row.State.Group).Distinct().ToList().IndexOf(group);
                Assert.IsTrue(index >= 0, "選択するグループが表示中ではありません。");
                Assert.IsTrue(List.SetSelectOfRows([List.RowSet.GetFirstRowOfGroup(index)], selected: true));
            }
        }

        internal DuplicateMergeCandidateGroupState State(DuplicateGroup group)
        {
            Assert.IsTrue(List.TryGetState(group, out DuplicateMergeCandidateGroupState? state));
            return state!;
        }

        /// <summary>各グループのSELECT・KEEP・確認結果（同一性）を捕捉します。</summary>
        internal (bool Selected, string? Keep, DuplicateMergeVerificationResult Verification)[] Snapshot()
            => [.. Groups.Select(group => { DuplicateMergeCandidateGroupState state = State(group); return (state.IsSelected, state.KeepFolder, state.Verification); })];

        public void Dispose() => ui.Dispose();
    }

    /// <summary>受付・確認・案内・進捗・再生停止・保存窓口・画面更新の抑制のどれにも触れていないことを確認します。</summary>
    private static void AssertNothingHappened(BatchFixture fixture)
    {
        Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "案内を出してはいけません。");
        Assert.IsNull(fixture.Dialogs.ConfirmationRequest, "確認を表示してはいけません。");
        Assert.AreEqual(0, fixture.Dialogs.Progress.Runs, "進捗を開いてはいけません。");
        Assert.AreEqual(0, fixture.Store.Requests.Count, "モデルを呼んではいけません。");
        CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Events, "再生停止・抑制・活動・確認・案内のどれも起きてはいけません。");
        Assert.AreEqual(0, fixture.BottomProgress.Count, "画面下部の進捗行を出してはいけません。");
    }

    private static string[] Lines(string message) => message.Split(Environment.NewLine);

    /// <summary>
    /// 対象は表示中でSELECTがオンのグループだけで、0件なら受付・確認・Busy案内・再生停止・進捗・モデル呼出しのどれも行わない。
    /// 受付を外部が保持していても案内を出さない。選択なし・検索で全件が非表示・グループなしのそれぞれを確認する。
    /// 検出する誤り：0件でも受付を取る・Busyを案内する・確認を出す・進捗を開く、非表示のグループを対象にする。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_NoTargets_DoesNothingEvenWhileAdmissionIsHeld()
    {
        DuplicateGroup first = Lists.Group(@"C:\S\Visible1", @"C:\S\Visible2"), second = Lists.Group(@"C:\S\Visible3");
        using var fixture = new BatchFixture([first, second]);
        Assert.IsTrue(fixture.Gate.TryEnter(out IDisposable? held));
        using (held)
        {
            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.NoTargets, (await fixture.Owner.OrganizeSelectedGroupsAsync()).Status, "選択なし");
            fixture.List.SelectVisible();
            fixture.List.SearchText = "no-such-folder-text";
            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.NoTargets, (await fixture.Owner.OrganizeSelectedGroupsAsync()).Status, "全件が非表示");
        }
        AssertNothingHappened(fixture);
        Assert.IsTrue(fixture.Gate.CanEnter, "受付を取らないので、外部の保持の解放後は取得できます。");
        fixture.List.SearchText = string.Empty;
        Assert.IsTrue(fixture.State(first).IsSelected && fixture.State(second).IsSelected, "非表示の間も、SELECTは変わりません。");

        using var empty = new BatchFixture([]);
        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.NoTargets, (await empty.Owner.OrganizeSelectedGroupsAsync()).Status, "グループなし");
        AssertNothingHappened(empty);
    }

    /// <summary>
    /// 対象は表示中でSELECTがオンのグループで、未確認・要注意も承認後に対象にし、確認結果とSELECTを書き換えない。検索で非表示のSELECTオンは対象にしない。
    /// 検出する誤り：非表示のグループを対象にする、未確認・要注意を除外する、整理の前後で確認結果・SELECT・KEEPを書き換える。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_TargetsVisibleSelectedGroupsIncludingUnverifiedAndCautionWithoutChangingState()
    {
        DuplicateGroup unverified = Lists.Group(@"C:\S\Visible-u1", @"C:\S\Visible-u2");
        DuplicateGroup caution = Lists.Group(@"C:\S\Visible-c1", @"C:\S\Visible-c2");
        DuplicateGroup safe = Lists.Group(@"C:\S\Visible-s1", @"C:\S\Visible-s2");
        DuplicateGroup hiddenSelected = Lists.Group(@"C:\S\Other-h1", @"C:\S\Other-h2");
        DuplicateGroup notSelected = Lists.Group(@"C:\S\Visible-n1", @"C:\S\Visible-n2");
        using var fixture = new BatchFixture([unverified, caution, safe, hiddenSelected, notSelected]);
        var cautionResult = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Caution,
            [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.AudioExtension, [new DuplicateMergeFinding(@"C:\S\Visible-c1", "a.wav")])]);
        var safeResult = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, []);
        fixture.List.WriteVerifications([new DuplicateMergeVerificationWrite(caution, cautionResult), new DuplicateMergeVerificationWrite(safe, safeResult)]);
        fixture.Select(unverified, caution, safe, hiddenSelected);
        fixture.List.SearchText = "Visible";
        (bool Selected, string? Keep, DuplicateMergeVerificationResult Verification)[] before = fixture.Snapshot();

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, run.Failure?.ToString());
        CollectionAssert.AreEqual(
            new[] { unverified, caution, safe }.Select(group => group.Folders[0]).ToArray(),
            fixture.Store.Requests.Single().Groups.Select(group => group.KeepDirectory).ToArray(),
            "表示中でSELECTがオンの三グループだけが、表示順で対象です。");
        (bool Selected, string? Keep, DuplicateMergeVerificationResult Verification)[] after = fixture.Snapshot();
        CollectionAssert.AreEqual(before, after, "整理の前後で、SELECT・KEEP・確認結果は変わりません。");
        Assert.AreSame(cautionResult, fixture.State(caution).Verification);
        Assert.AreSame(DuplicateMergeVerificationResult.Unverified, fixture.State(unverified).Verification);
    }

    /// <summary>
    /// 要求のグループ順は並べ替えと検索を当てた後の表示順で、各グループはKEEPと、KEEP以外をPATH昇順（大文字小文字を無視）に並べた移動元を持つ。
    /// KEEPが中間にある場合と、大文字小文字の混在（序数順だと逆になる）、一フォルダのグループ（移動元が空）を確認する。
    /// 検出する誤り：結果順での要求、序数順の移動元、KEEPを移動元へ含める、KEEPの位置による取り違え、一フォルダのグループの移動元の誤り。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_RequestFollowsDisplayOrderWithKeepAndCaseInsensitiveSources()
    {
        DuplicateGroup middleKeep = Lists.Group((@"C:\S\zeta", 1), (@"C:\S\Alpha", 1), (@"C:\S\beta", 3));
        DuplicateGroup mixedCase = Lists.Group((@"C:\S\a", 1), (@"C:\S\b", 2), (@"C:\S\C", 1));
        DuplicateGroup single = Lists.Group(@"C:\S\solo");
        using var fixture = new BatchFixture([middleKeep, mixedCase, single]);
        fixture.List.SelectVisible();
        fixture.List.RequestSort("Path", ListSortDirection.Descending);
        // 表示順の期待値は、状態表の行の並びから導く（先頭フォルダのPATH降順：solo、Alpha、a）。
        DuplicateGroup[] displayed = [.. fixture.List.RowSet.Select(row => row.State.Group).Distinct()];
        CollectionAssert.AreEqual(new[] { single, middleKeep, mixedCase }, displayed);

        await fixture.Owner.OrganizeSelectedGroupsAsync();

        CollectionAssert.AreEqual(
            new[] { @"C:\S\solo|", @"C:\S\beta|C:\S\Alpha,C:\S\zeta", @"C:\S\b|C:\S\a,C:\S\C" },
            fixture.Store.Requests.Single().Groups.Select(group => group.KeepDirectory + "|" + string.Join(",", group.SourceDirectories)).ToArray());
        Assert.IsTrue(fixture.Store.Requests.Single().Groups[0].IsSingleFolder);

        // 検索で絞った後の表示順だけが対象になる。
        fixture.Store.Requests.Clear();
        fixture.List.SearchText = "beta";
        await fixture.Owner.OrganizeSelectedGroupsAsync();
        Assert.AreEqual(@"C:\S\beta", fixture.Store.Requests.Single().Groups.Single().KeepDirectory);
    }

    /// <summary>
    /// 受付は待たずに取得し、競合時はBusy案内を一回だけ出して、確認・停止・進捗・モデル呼出しをせず、SELECT・KEEP・確認結果を変えない。
    /// 予約せず、解放後に自動実行しない。次の明示要求はBusyなしで実行する。
    /// 検出する誤り：受付の待機・予約、Busyの案内漏れ・重複、競合時の確認表示、状態の書換え、解放後の自動実行。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_BusyAdmission_WarnsOnceWithoutConfirmationOrStateChangeAndRunsNextRequest()
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        (bool Selected, string? Keep, DuplicateMergeVerificationResult Verification)[] before = fixture.Snapshot();
        Assert.IsTrue(fixture.Gate.TryEnter(out IDisposable? held));
        using (held)
        {
            DuplicateBatchOrganizeRunResult rejected = await fixture.Owner.OrganizeSelectedGroupsAsync();

            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Busy, rejected.Status);
            UiMessageRequest warning = fixture.Dialogs.Messages.Single();
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, warning.MessageBoxText);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warning, warning.Caption);
            Assert.AreEqual(MessageBoxImage.Exclamation, warning.Icon);
            Assert.AreEqual(MessageBoxButton.OK, warning.Button);
            Assert.IsNull(fixture.Dialogs.ConfirmationRequest);
            Assert.AreEqual(0, fixture.Dialogs.Progress.Runs);
            Assert.AreEqual(0, fixture.Store.Requests.Count);
            CollectionAssert.AreEqual(new[] { "message" }, fixture.Events, "Busyの案内以外の操作は起きません。");
            CollectionAssert.AreEqual(before, fixture.Snapshot());
            Assert.IsFalse(fixture.Gate.CanEnter);
        }
        Assert.AreEqual(0, fixture.Store.Requests.Count, "解放だけでは、拒否した要求を実行しません。");

        DuplicateBatchOrganizeRunResult next = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, next.Status, next.Failure?.ToString());
        Assert.AreEqual(1, fixture.Store.Requests.Count);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count(message => message.MessageBoxText == BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy), "次の要求はBusyを案内しません。");
        Assert.IsTrue(fixture.Gate.CanEnter);
    }

    /// <summary>
    /// 受付が閉じている（アプリの終了処理中）場合は、Busyを案内せず、確認・停止・進捗・モデル呼出し・状態の変更のどれもなく未開始で返す。
    /// 検出する誤り：終了処理中のBusy案内、受付が閉じていても確認や進捗へ進む。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_ClosedAdmission_ReturnsNotStartedWithoutNoticeOrSideEffect()
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        (bool Selected, string? Keep, DuplicateMergeVerificationResult Verification)[] before = fixture.Snapshot();
        fixture.Gate.CloseAdmission();

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.NotStarted, run.Status);
        AssertNothingHappened(fixture);
        CollectionAssert.AreEqual(before, fixture.Snapshot());
    }

    /// <summary>
    /// 受付の取得後に確認を必ず一回（OK／キャンセル、既定はキャンセル）出し、確認を表示しない設定でも出す。
    /// キャンセル・閉じる・表示失敗・確認の例外では、モデル・再生停止・進捗・画面更新の抑制へ進まず、受付を解放し、状態を変えない。
    /// 表示失敗と例外は失敗として一回だけ報告する。OKだけが実行へ進む。
    /// 検出する誤り：確認の省略（設定オフ時を含む）、キャンセル・閉じる・表示失敗の承認扱い、表示失敗の握りつぶし・重複報告、受付の解放漏れ、状態の書換え。
    /// </summary>
    [TestMethod]
    [DataRow("accept", true)]
    [DataRow("accept", false)]
    [DataRow("cancel", true)]
    [DataRow("cancel", false)]
    [DataRow("close", true)]
    [DataRow("notshown", true)]
    [DataRow("throw", false)]
    public async Task OrganizeSelectedGroups_ConfirmationDecidesWhetherToRun(string decision, bool showConfirmationSetting)
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group], showConfirmationSetting);
        fixture.List.SelectVisible();
        (bool Selected, string? Keep, DuplicateMergeVerificationResult Verification)[] before = fixture.Snapshot();
        switch (decision)
        {
            case "cancel": fixture.Dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel); break;
            case "close": fixture.Dialogs.ConfirmationResult = UiDialogResult.ClosedByUser(MessageBoxResult.None); break;
            case "notshown": fixture.Dialogs.ConfirmationResult = UiDialogResult.NotShown(UiDialogStatus.DispatcherUnavailable); break;
            case "throw": fixture.Dialogs.ConfirmationHandler = _ => throw new InvalidOperationException("confirmation display failure"); break;
        }

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        UiConfirmationRequest request = fixture.Dialogs.ConfirmationRequest!;
        Assert.IsNotNull(request, "確認は、設定によらず常に一回出します。");
        Assert.AreEqual(MessageBoxButton.OKCancel, request.Button);
        Assert.AreEqual(MessageBoxResult.Cancel, request.DefaultResult, "既定はキャンセルです。");
        Assert.AreEqual(1, fixture.Events.Count(entry => entry == "confirm"));
        Assert.IsTrue(fixture.Gate.CanEnter, "受付は解放します。");
        if (decision == "accept")
        {
            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, run.Failure?.ToString());
            Assert.AreEqual(1, fixture.Store.Requests.Count);
            return;
        }
        Assert.AreEqual(0, fixture.Store.Requests.Count);
        Assert.AreEqual(0, fixture.Dialogs.Progress.Runs);
        CollectionAssert.DoesNotContain(fixture.Events, "playback-stop");
        CollectionAssert.DoesNotContain(fixture.Events, "suppression-start");
        CollectionAssert.AreEqual(before, fixture.Snapshot());
        if (decision is "cancel" or "close")
        {
            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Declined, run.Status);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "利用者の取消は案内しません。");
            return;
        }
        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Failed, run.Status);
        Assert.IsNotNull(run.Failure);
        UiMessageRequest report = fixture.Dialogs.Messages.Single();
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.FileDbMutationReport_Title, report.Caption, "確認の表示失敗は、一回だけ報告します。");
        Assert.AreEqual(MessageBoxImage.Hand, report.Icon);
    }

    /// <summary>
    /// 確認の表示中は、モデルを呼ばず、受付は保持し、進捗も開かない。確認が済んでから実行へ進む。
    /// 検出する誤り：確認の前にモデルを呼ぶ、確認中に受付を手放す（別の変更が割り込める）、確認の前に進捗を開く。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_WhileConfirmationIsShown_HoldsAdmissionWithoutCallingModel()
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        var pending = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dialogs.PendingConfirmation = pending;
        fixture.Dialogs.ConfirmationReached = reached;
        Task<DuplicateBatchOrganizeRunResult> run = fixture.Owner.OrganizeSelectedGroupsAsync();
        try
        {
            await reached.Task.WaitAsync(BatchDeadline);
            Assert.IsFalse(fixture.Gate.CanEnter, "確認中も受付を保持します。");
            Assert.AreEqual(0, fixture.Store.Requests.Count);
            Assert.AreEqual(0, fixture.Dialogs.Progress.Runs);
            CollectionAssert.AreEqual(new[] { "confirm" }, fixture.Events);
        }
        finally
        {
            pending.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }
        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, (await run.WaitAsync(BatchDeadline)).Status);
        Assert.AreEqual(1, fixture.Store.Requests.Count);
    }

    /// <summary>
    /// 確認文は、グループ数の行とごみ箱の行を常に含み、二フォルダ以上のグループがあるときだけ統合の行（m：そのグループ数、k：移動元の合計）、
    /// 未確認・要注意があるときだけ0件でない側の件数の行（このときアイコンは警告、どちらも0なら質問）を加える。
    /// 検出する誤り：行の欠落・過剰、0件の側を出す、件数の取り違え、アイコンの取り違え。
    /// </summary>
    [TestMethod]
    [DataRow(0, 2, 0, 0, DisplayName = "二フォルダ以上なし・全て安全")]
    [DataRow(2, 1, 0, 0, DisplayName = "統合あり・全て安全")]
    [DataRow(1, 1, 1, 0, DisplayName = "未確認だけ")]
    [DataRow(1, 1, 0, 1, DisplayName = "要注意だけ")]
    [DataRow(2, 1, 1, 2, DisplayName = "未確認と要注意")]
    public async Task OrganizeSelectedGroups_ConfirmationMessageComposition(int mergeGroups, int singleGroups, int unverified, int caution)
    {
        var groups = new List<DuplicateGroup>();
        for (int index = 0; index < mergeGroups; index++) { groups.Add(Lists.Group($@"C:\S\m{index}\a", $@"C:\S\m{index}\b", $@"C:\S\m{index}\c")); }
        for (int index = 0; index < singleGroups; index++) { groups.Add(Lists.Group($@"C:\S\s{index}")); }
        using var fixture = new BatchFixture([.. groups]);
        var cautionResult = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Caution,
            [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.AudioExtension, [new DuplicateMergeFinding(@"C:\S", "a.wav")])]);
        var safeResult = new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, []);
        // 先頭から、未確認・要注意・安全の順に確認結果を割り当てる。未確認は初期値のままにする。
        var writes = new List<DuplicateMergeVerificationWrite>();
        for (int index = 0; index < groups.Count; index++)
        {
            if (index < unverified) { continue; }
            writes.Add(new DuplicateMergeVerificationWrite(groups[index], index < unverified + caution ? cautionResult : safeResult));
        }
        fixture.List.WriteVerifications(writes);
        fixture.List.SelectVisible();

        await fixture.Owner.OrganizeSelectedGroupsAsync();

        var expected = new List<string> { string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_confirm_groups_format, groups.Count) };
        if (mergeGroups > 0)
        {
            expected.Add(string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_confirm_merge_format, mergeGroups, mergeGroups * 2));
        }
        expected.Add(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_confirm_trash);
        if (unverified > 0 && caution > 0) { expected.Add(string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_confirm_unverified_caution_format, unverified, caution)); }
        else if (unverified > 0) { expected.Add(string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_confirm_unverified_format, unverified)); }
        else if (caution > 0) { expected.Add(string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_confirm_caution_format, caution)); }
        UiConfirmationRequest request = fixture.Dialogs.ConfirmationRequest!;
        CollectionAssert.AreEqual(expected, Lines(request.MessageBoxText));
        Assert.AreEqual(unverified + caution > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, request.Icon);
    }

    /// <summary>
    /// 確認結果に別内容ファイルの衝突（要注意の理由）または保護対象名の衝突（安全への付記）を持つグループがあるときだけ、衝突の行を加える。
    /// 検出する誤り：衝突の行の欠落（どちらの種類でも）、衝突のないグループでの過剰表示。
    /// </summary>
    [TestMethod]
    [DataRow("none", false)]
    [DataRow("content", true)]
    [DataRow("protected", true)]
    public async Task OrganizeSelectedGroups_ConfirmationMentionsOverwriteOnlyWhenACollisionIsRecorded(string kind, bool expectLine)
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        DuplicateMergeVerificationResult result = kind switch
        {
            "content" => new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Caution,
                [new DuplicateMergeVerificationReason(DuplicateMergeVerificationReasonKind.ContentConflict, [new DuplicateMergeFinding(@"C:\S\one", "a.wav")])]),
            "protected" => new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, [], ["BMSEARCH.exe"]),
            _ => new DuplicateMergeVerificationResult(DuplicateMergeVerificationStage.Safe, [])
        };
        fixture.List.WriteVerifications([new DuplicateMergeVerificationWrite(group, result)]);
        fixture.List.SetSelectOfRows(fixture.List.RowSet, selected: true);

        await fixture.Owner.OrganizeSelectedGroupsAsync();

        string[] lines = Lines(fixture.Dialogs.ConfirmationRequest!.MessageBoxText);
        Assert.AreEqual(expectLine, lines.Contains(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_confirm_collision));
    }

    /// <summary>
    /// 承認後は 進捗を開く→（活動）→再生停止→画面更新の抑制→モデルの呼出し（UIの文脈の外、スレッドプール）の順に進み、モデルの呼出しは進捗表示の操作の内側に収まる。
    /// 進捗画面は取消ボタンつきで、取消の扱いを操作へ任せる設定を使い、画面下部の進捗行は出さない。結果は進捗が閉じて受付を解放した後に一回案内する。
    /// 検出する誤り：停止・抑制・モデルの順序違い、進捗の外でのモデル呼出し、UIの文脈でのモデル呼出し、下部の進捗行の表示、取消ボタンの欠落、案内の重複。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_RunsStopThenSuppressionThenModelInsideProgressOffTheCallerContext()
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        bool? gateFreeAtResult = null;
        fixture.Dialogs.OnMessage = () => gateFreeAtResult ??= fixture.Gate.CanEnter && !fixture.Activity.IsActive;

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, run.Failure?.ToString());
        CollectionAssert.AreEqual(
            new[]
            {
                "confirm", "progress-start", "activity-start", "playback-stop", "suppression-start", "store-organize",
                "suppression-end", "activity-end", "progress-end", "message"
            },
            fixture.Events);
        Assert.IsTrue(fixture.Store.OnPoolThread.Single(), "モデルはUIの文脈の外（スレッドプール）で呼びます。");
        Assert.AreEqual(0, fixture.BottomProgress.Count, "画面下部の進捗行は出しません。");
        UiProgressRequest progress = fixture.Dialogs.Progress.Request!;
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_title, progress.Title);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_progress_label, progress.Label);
        Assert.IsTrue(progress.Settings.ShowCancelButton);
        Assert.IsTrue(progress.Settings.ShowSubLabel);
        Assert.IsTrue(progress.Settings.OperationOwnsCancellation, "反映段階で取消できず、取消の要求中も例外と結果を失わない設定です。");
        Assert.AreEqual(true, gateFreeAtResult, "結果の案内の時点では、受付も活動も解放済みです。");
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count, "結果は一回だけ案内します。");
    }

    /// <summary>
    /// 再生停止が失敗すると、画面更新の抑制もモデルの呼出しもせず、活動と受付を解放して、失敗を一回だけ報告する（結果の案内は出さない）。
    /// 検出する誤り：停止の失敗の後の物理変更、失敗の握りつぶし・重複報告、受付・活動の解放漏れ、抑制の解除漏れ。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_PlaybackStopFailureSkipsModelAndReportsOnce()
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        fixture.Presentation.StopFailure = new IOException("playback stop failed");

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Failed, run.Status);
        Assert.AreEqual(0, fixture.Store.Requests.Count);
        CollectionAssert.AreEqual(
            new[] { "confirm", "progress-start", "activity-start", "playback-stop", "activity-end", "progress-end", "message" },
            fixture.Events);
        UiMessageRequest report = fixture.Dialogs.Messages.Single();
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.FileDbMutationReport_Title, report.Caption);
        StringAssert.Contains(report.MessageBoxText, "playback stop failed");
        Assert.IsTrue(fixture.Gate.CanEnter);
        Assert.IsFalse(fixture.Activity.IsActive);
    }

    /// <summary>
    /// 進捗は「i／総数」とKEEPのフォルダ名（割合は開始したグループの手前までの割合）で示し、全グループの物理処理の後は「ライブラリへ反映中」を示して取消ボタンを無効にする。
    /// 反映段階で取消を要求しても、結果に「取り消されました。」を付けない。
    /// 検出する誤り：KEEPでない代表名、番号の取り違え、反映段階の表示の欠落、反映中に取消できる、反映中の取消要求を取消と案内する。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_ProgressShowsNumberAndKeepNameThenApplyingAndCannotBeCancelledThen()
    {
        DuplicateGroup first = Lists.Group((@"C:\S\alpha1", 1), (@"C:\S\alpha2", 2)), second = Lists.Group(@"C:\S\beta1", @"C:\S\beta2");
        using var fixture = new BatchFixture([first, second]);
        fixture.List.SelectVisible();
        fixture.Store.Handler = (request, progress, _, _) =>
        {
            DuplicateBatchOrganizeResult result = BatchStore.Complete(request, progress);
            // 反映段階の通知の後で取消が要求されても、取消として扱わない。
            fixture.Dialogs.Progress.RequestCancel();
            return result;
        };

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, run.Failure?.ToString());
        // 画面へは最新の値だけを間引いて渡すため、途中のグループ開始の値は欠けてよい。最後は必ず反映中で、反映中の後にグループ開始の値は来ない。
        (int Percent, string Message)[] reports = [.. fixture.Dialogs.Progress.Reports];
        Assert.AreEqual((100, BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_progress_applying), reports[^1], "最後の値は「ライブラリへ反映中」です。");
        Assert.AreEqual(1, reports.Count(report => report.Message == BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_progress_applying));
        string[] groupMessages =
        [
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_progress_group_format, 1, 2, "alpha2"),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_progress_group_format, 2, 2, "beta1")
        ];
        int[] indexes = [.. reports[..^1].Select(report => Array.IndexOf(groupMessages, report.Message))];
        Assert.IsTrue(indexes.All(index => index >= 0) && indexes.Zip(indexes.Skip(1), (a, b) => a < b).All(ok => ok), "グループ開始の値は、番号が後退しない順です。");
        Assert.AreEqual(1, fixture.Dialogs.Progress.CancellationDisabledCount, "反映段階へ入ったら、取消ボタンを無効にします。");
        Assert.IsFalse(Lines(fixture.Dialogs.Messages.Single().MessageBoxText).Contains(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_canceled));
    }

    /// <summary>
    /// 進捗表示の取消がモデルの取消の合図へ届き、モデルの結果が取消なら、結果に「取り消されました。」を出す（未処理の件数は見出しの「未処理」で示す）。
    /// 進捗の仕組みが取消を返しても、結果の案内を省かない。取消の要求中にモデルが例外を出しても、失敗を捨てず一回だけ報告する。
    /// 検出する誤り：取消の合図が届かない、取消の結果の案内漏れ、取消の状態を結果の省略に使う、取消の要求中の例外の握りつぶし。
    /// </summary>
    [TestMethod]
    [DataRow("canceled")]
    [DataRow("progress-reports-cancel")]
    [DataRow("throws-while-canceled")]
    public async Task OrganizeSelectedGroups_CancellationReachesModelAndIsReportedWithoutLosingResultOrFailure(string scenario)
    {
        DuplicateGroup first = Lists.Group(@"C:\S\a1", @"C:\S\a2"), second = Lists.Group(@"C:\S\b1", @"C:\S\b2"), third = Lists.Group(@"C:\S\c1", @"C:\S\c2");
        using var fixture = new BatchFixture([first, second, third]);
        fixture.List.SelectVisible();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool tokenCanceled = false;
        if (scenario == "progress-reports-cancel") { fixture.Dialogs.Progress.ForcedStatus = UiDialogStatus.CancelledByUser; }
        fixture.Store.Handler = (request, progress, token, _) =>
        {
            progress?.Invoke(new DuplicateBatchOrganizeProgress(1, 3, request.Groups[0].KeepDirectory));
            started.TrySetResult();
            if (scenario == "progress-reports-cancel")
            {
                progress?.Invoke(new DuplicateBatchOrganizeProgress(3, 3, null!, DuplicateBatchOrganizeProgressKind.Applying));
                return Result(request.Groups.Select(group => GroupResult(group.KeepDirectory, DuplicateBatchOrganizeGroupStatus.Completed, merged: group.SourceDirectories)));
            }
            tokenCanceled = token.WaitHandle.WaitOne(BatchDeadline);
            if (scenario == "throws-while-canceled") { throw new IOException("model failure while cancellation is pending"); }
            progress?.Invoke(new DuplicateBatchOrganizeProgress(1, 3, null!, DuplicateBatchOrganizeProgressKind.Applying));
            return Result(
                [
                    GroupResult(request.Groups[0].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Completed, merged: request.Groups[0].SourceDirectories),
                    GroupResult(request.Groups[1].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Unprocessed),
                    GroupResult(request.Groups[2].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Unprocessed)
                ],
                canceled: true);
        };

        Task<DuplicateBatchOrganizeRunResult> operation = fixture.Owner.OrganizeSelectedGroupsAsync();
        await started.Task.WaitAsync(BatchDeadline);
        if (scenario != "progress-reports-cancel") { fixture.Dialogs.Progress.RequestCancel(); }
        DuplicateBatchOrganizeRunResult run = await operation.WaitAsync(BatchDeadline);

        switch (scenario)
        {
            case "canceled":
                Assert.IsTrue(tokenCanceled, "進捗表示の取消が、モデルの取消の合図へ届きます。");
                Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status);
                string[] lines = Lines(fixture.Dialogs.Messages.Single().MessageBoxText);
                Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_format, 3, 1, 0, 0, 0, 2), lines[0]);
                CollectionAssert.Contains(lines, BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_canceled);
                break;
            case "progress-reports-cancel":
                Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status, "進捗の仕組みが取消を返しても、結果を省きません。");
                Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
                break;
            default:
                Assert.IsTrue(tokenCanceled);
                Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Failed, run.Status, "取消の要求中の例外も、結果として返します。");
                UiMessageRequest report = fixture.Dialogs.Messages.Single();
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.FileDbMutationReport_Title, report.Caption);
                StringAssert.Contains(report.MessageBoxText, "model failure while cancellation is pending");
                break;
        }
        Assert.IsTrue(fixture.Gate.CanEnter);
    }

    /// <summary>
    /// モデルが操作全体を受理しなかった場合は、モデルが積んだ案内だけを一回出し、結果の案内も報告も出さない。受付と活動は解放する。
    /// 検出する誤り：未受理でも結果を案内する、案内の重複・欠落、未受理の報告（失敗扱い）。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_NotAdmittedByModelShowsOnlyTheModelNotice()
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        fixture.Store.Handler = (request, _, _, notices) =>
        {
            notices.Add(new BMSLibrary.OperationDialogMessage("model notice", "model caption", UiDialogButton.OK, UiDialogIcon.Exclamation, UiDialogDefaultResult.OK));
            return DuplicateBatchOrganizeResult.NotAdmitted(request);
        };

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.NotAdmitted, run.Status);
        UiMessageRequest notice = fixture.Dialogs.Messages.Single();
        Assert.AreEqual("model notice", notice.MessageBoxText);
        Assert.IsTrue(fixture.Gate.CanEnter);
        Assert.IsFalse(fixture.Activity.IsActive);
    }

    /// <summary>
    /// 確定・反映・保守の失敗では、単体の統合と同じ報告（FileDbMutationReport）を一回出し、その後に結果を一回出す。移動元ごとの事前拒否（型の衝突、移動元がない、事前確認で拒否など）だけでは報告を出さず、結果の理由で案内する。
    /// 検出する誤り：失敗の報告漏れ・重複、結果と報告の順序違い、事前拒否のたびの報告、失敗時の結果の欠落。
    /// </summary>
    [TestMethod]
    [DataRow("finalization", true)]
    [DataRow("apply", true)]
    [DataRow("cleanup", true)]
    [DataRow("type-conflict-refusal", false)]
    [DataRow("source-missing-refusal", false)]
    [DataRow("preflight-refusal", false)]
    public async Task OrganizeSelectedGroups_RequiredFailuresAreReportedOnceBeforeTheSingleResult(string kind, bool expectReport)
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        LibraryMutationSessionReceipt session = kind switch
        {
            "finalization" => DurableSession(finalizationFailure: new IOException("finalization failed")),
            "apply" => DurableSession(applyFailure: new IOException("apply failed"), durable: false),
            "cleanup" => DurableSession(cleanupFailure: new IOException("cleanup failed")),
            "type-conflict-refusal" => new LibraryMutationSessionReceipt([], durableCommit: false,
                itemFailures: [new LibraryMutationSessionItemFailure(
                    new LibraryMutationSessionTarget(@"C:\S\two", @"C:\S\one"), new IOException("type conflict refusal"),
                    destinationTypeConflicts: [new FileDbMutationDestinationTypeConflict(@"C:\S\two\a", @"C:\S\one\a", true, false)])]),
            // 型の衝突以外の移動元ごとの事前拒否は、変更セッションの必須の失敗として残るが、報告ではなく結果の理由として案内する。
            _ => new LibraryMutationSessionReceipt([], durableCommit: false,
                itemFailures: [new LibraryMutationSessionItemFailure(
                    new LibraryMutationSessionTarget(@"C:\S\two", @"C:\S\one"), new IOException("source refusal"))])
        };
        DuplicateBatchOrganizeSourceReason sourceReason = kind switch
        {
            "source-missing-refusal" => DuplicateBatchOrganizeSourceReason.SourceMissing,
            "preflight-refusal" => DuplicateBatchOrganizeSourceReason.PreflightRefused,
            _ => DuplicateBatchOrganizeSourceReason.TypeConflict
        };
        fixture.Store.Handler = (request, progress, _, _) =>
        {
            progress?.Invoke(new DuplicateBatchOrganizeProgress(1, 1, request.Groups[0].KeepDirectory));
            progress?.Invoke(new DuplicateBatchOrganizeProgress(1, 1, null!, DuplicateBatchOrganizeProgressKind.Applying));
            return Result([GroupResult(request.Groups[0].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Failed,
                retained: [new DuplicateBatchOrganizeRetainedSource(request.Groups[0].SourceDirectories[0], sourceReason)])],
                session: session, mergeApplied: false);
        };

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Completed, run.Status);
        UiMessageRequest[] messages = [.. fixture.Dialogs.Messages];
        if (expectReport)
        {
            Assert.AreEqual(2, messages.Length, "報告を一回、その後に結果を一回出します。");
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.FileDbMutationReport_Title, messages[0].Caption);
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_title, messages[1].Caption);
        }
        else
        {
            Assert.AreEqual(1, messages.Length, "移動元の事前拒否は、結果の理由で案内し、報告は出しません。");
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_title, messages[0].Caption);
            StringAssert.Contains(messages[0].MessageBoxText, DuplicateBatchOrganizePresentation.SourceReasonText(sourceReason));
        }
    }

    /// <summary>
    /// モデルが例外を出した場合は、単体の統合と同じ報告を一回出し、結果の案内は出さない。受付・活動・画面更新の抑制は解除する。
    /// 検出する誤り：例外の握りつぶし、報告の重複、モデルの結果がないのに結果を案内する、解放漏れ。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_ModelExceptionIsReportedOnceWithoutResult()
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        fixture.Store.Handler = (_, _, _, _) => throw new IOException("model exploded");

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Failed, run.Status);
        Assert.IsNull(run.Result);
        UiMessageRequest report = fixture.Dialogs.Messages.Single();
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.FileDbMutationReport_Title, report.Caption);
        StringAssert.Contains(report.MessageBoxText, "model exploded");
        CollectionAssert.AreEqual(
            new[] { "confirm", "progress-start", "activity-start", "playback-stop", "suppression-start", "store-organize", "suppression-end", "activity-end", "progress-end", "message" },
            fixture.Events);
        Assert.IsTrue(fixture.Gate.CanEnter);
    }

    /// <summary>
    /// 既存宛先を復元できなかった場合は、結果の案内も報告も出さず、受付・活動の解放と進捗を閉じた後に、単体と同じ終了手順を一回だけ呼ぶ。
    /// 検出する誤り：解放前の終了処理、復元不能の結果の案内・報告、終了処理の重複呼出し。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_UnrestoredDestinationExitsOnceAfterReleaseWithoutReportOrResult()
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        int exits = 0;
        bool releasedAtExit = false, progressClosedAtExit = false;
        Exception? exitFailure = null;
        BatchFixture? fixture = null;
        fixture = new BatchFixture([group], exitForUnrestoredDestination: failure =>
        {
            exits++;
            exitFailure = failure;
            releasedAtExit = fixture!.Gate.CanEnter && !fixture.Activity.IsActive;
            progressClosedAtExit = fixture.Events.Contains("progress-end");
            return Task.CompletedTask;
        });
        using (fixture)
        {
            fixture.List.SelectVisible();
            var failure = new IOException("destination could not be restored");
            fixture.Store.Handler = (request, progress, _, _) =>
            {
                progress?.Invoke(new DuplicateBatchOrganizeProgress(1, 1, null!, DuplicateBatchOrganizeProgressKind.Applying));
                return Result([GroupResult(request.Groups[0].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Failed)],
                    session: new LibraryMutationSessionReceipt([], durableCommit: false, physicalFailure: failure, hasUnrestoredExistingDestination: true), mergeApplied: false);
            };

            DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

            Assert.AreEqual(DuplicateBatchOrganizeRunStatus.UnrestoredDestination, run.Status);
            Assert.AreEqual(1, exits);
            Assert.IsTrue(releasedAtExit, "受付と活動を解放してから呼びます。");
            Assert.IsTrue(progressClosedAtExit, "進捗を閉じてから呼びます。");
            Assert.AreSame(failure, exitFailure);
            Assert.AreEqual(0, fixture.Dialogs.Messages.Count, "報告も結果も出しません。");
        }
    }

    /// <summary>
    /// 結果の見出しは五区分を0件の区分も含めて出し、全件が完了なら詳細・ごみ箱の行・取消の行は出さない（全件完了でも結果は案内し、アイコンは情報）。
    /// 検出する誤り：全件完了での案内省略、0件の区分の省略、不要な行の表示。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_AllCompletedShowsOnlyTheHeadingWithZeroCategories()
    {
        DuplicateGroup first = Lists.Group(@"C:\S\a1", @"C:\S\a2"), second = Lists.Group(@"C:\S\solo");
        using var fixture = new BatchFixture([first, second]);
        fixture.List.SelectVisible();

        await fixture.Owner.OrganizeSelectedGroupsAsync();

        UiMessageRequest result = fixture.Dialogs.Messages.Single();
        Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_format, 2, 2, 0, 0, 0, 0), result.MessageBoxText);
        Assert.AreEqual(MessageBoxImage.Asterisk, result.Icon);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_title, result.Caption);
    }

    /// <summary>
    /// 結果は、ごみ箱の行を合計が一以上のときだけ（確定に失敗したグループの分も数える）、取消の行を取消のときだけ出し、完了以外のグループを要求順に
    /// 「KEEP：状態（理由）」と残った移動元ごとの「移動元：理由」で並べる。理由は、スキップは拒否の理由、削除の失敗は「削除の失敗」、確定の失敗による失敗は「確定の失敗」、それ以外は括弧なし。
    /// 検出する誤り：ごみ箱の合計の漏れ（確定失敗の分）、理由の取り違え・括弧の誤り、並びの違い、完了グループの詳細表示。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_MixedResultListsIncompleteGroupsInRequestOrderWithReasons()
    {
        DuplicateGroup[] groups = [.. Enumerable.Range(0, 6).Select(index => Lists.Group($@"C:\S\g{index}\a", $@"C:\S\g{index}\b"))];
        using var fixture = new BatchFixture(groups);
        fixture.List.SelectVisible();
        fixture.Store.Handler = (request, progress, _, _) =>
        {
            DuplicateBatchOrganizeGroup[] g = [.. request.Groups];
            progress?.Invoke(new DuplicateBatchOrganizeProgress(6, 6, null!, DuplicateBatchOrganizeProgressKind.Applying));
            return Result(
                [
                    GroupResult(g[0].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Completed, merged: g[0].SourceDirectories, trashed: 2),
                    GroupResult(g[1].KeepDirectory, DuplicateBatchOrganizeGroupStatus.PartiallyCompleted, trashed: 1, trashFailed: 1,
                        retained: [new(g[1].SourceDirectories[0], DuplicateBatchOrganizeSourceReason.SourceMissing)]),
                    GroupResult(g[2].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Skipped, DuplicateBatchOrganizeSkipReason.LinkFound),
                    GroupResult(g[3].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Failed,
                        retained: [new(g[3].SourceDirectories[0], DuplicateBatchOrganizeSourceReason.PhysicalFailure)]),
                    GroupResult(g[4].KeepDirectory, DuplicateBatchOrganizeGroupStatus.PartiallyCompleted,
                        retained: [new(g[4].SourceDirectories[0], DuplicateBatchOrganizeSourceReason.NotProcessed)]),
                    GroupResult(g[5].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Unprocessed)
                ],
                canceled: true);
        };

        await fixture.Owner.OrganizeSelectedGroupsAsync();

        string keepOf(int index) => groups[index].Folders[0];
        string sourceOf(int index) => groups[index].Folders[1];
        // KEEPは譜面数が同じなら先頭（PATH昇順の先頭）。
        string[] expected =
        [
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_format, 6, 1, 2, 1, 1, 1),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_trashed_format, 3),
            BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_canceled,
            string.Empty,
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_reason_format, keepOf(1),
                BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_partial, BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_reason_delete_failure),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_source_format, sourceOf(1), BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_reason_source_missing),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_reason_format, keepOf(2),
                BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_skipped, BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_reason_link),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_format, keepOf(3), BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_failed),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_source_format, sourceOf(3), BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_reason_physical_failure),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_format, keepOf(4), BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_partial),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_source_format, sourceOf(4), BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_reason_not_processed),
            string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_format, keepOf(5), BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_unprocessed)
        ];
        UiMessageRequest result = fixture.Dialogs.Messages.Single();
        CollectionAssert.AreEqual(expected, Lines(result.MessageBoxText));
        Assert.AreEqual(MessageBoxImage.Exclamation, result.Icon, "完了以外があれば、アイコンは警告です。");
    }

    /// <summary>
    /// 確定に失敗した操作では、物理的に成功していたグループが「確定の失敗」を理由とする失敗になり、ごみ箱へ送った譜面の数は確定失敗のグループの分も合計へ数える。
    /// 失敗の報告を出した後に、結果を一回出す。
    /// 検出する誤り：確定失敗のグループのごみ箱の数の欠落、確定の失敗の理由の欠落、報告と結果の順序。
    /// </summary>
    [TestMethod]
    public async Task OrganizeSelectedGroups_CommitFailureCountsTrashedChartsAndShowsCommitFailureReason()
    {
        DuplicateGroup merge = Lists.Group(@"C:\S\m\a", @"C:\S\m\b"), single = Lists.Group(@"C:\S\solo"), plain = Lists.Group(@"C:\S\p\a", @"C:\S\p\b"), both = Lists.Group(@"C:\S\q");
        using var fixture = new BatchFixture([merge, single, plain, both]);
        fixture.List.SelectVisible();
        fixture.Store.Handler = (request, progress, _, _) =>
        {
            progress?.Invoke(new DuplicateBatchOrganizeProgress(2, 2, null!, DuplicateBatchOrganizeProgressKind.Applying));
            return Result(
                [
                    GroupResult(request.Groups[0].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Failed, trashed: 2,
                        retained: [new(request.Groups[0].SourceDirectories[0], DuplicateBatchOrganizeSourceReason.NotCommitted)]),
                    GroupResult(request.Groups[1].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Failed, trashed: 1, single: true),
                    // もともと統合できずに失敗したグループ（ごみ箱へ送った譜面もなし）には、確定の失敗を付けない。
                    GroupResult(request.Groups[2].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Failed,
                        retained: [new(request.Groups[2].SourceDirectories[0], DuplicateBatchOrganizeSourceReason.PhysicalFailure)]),
                    GroupResult(request.Groups[3].KeepDirectory, DuplicateBatchOrganizeGroupStatus.Failed, trashed: 1, trashFailed: 1, single: true)
                ],
                session: DurableSession(applyFailure: new IOException("commit failed"), durable: false), mergeApplied: false);
        };

        await fixture.Owner.OrganizeSelectedGroupsAsync();

        UiMessageRequest[] messages = [.. fixture.Dialogs.Messages];
        Assert.AreEqual(2, messages.Length);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.FileDbMutationReport_Title, messages[0].Caption, "報告が先です。");
        string[] lines = Lines(messages[1].MessageBoxText);
        Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_format, 4, 0, 0, 0, 4, 0), lines[0]);
        Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_trashed_format, 4), lines[1]);
        string commitFailed = BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_reason_commit_failure;
        CollectionAssert.Contains(lines, string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_reason_format,
            fixture.Store.Requests.Single().Groups[0].KeepDirectory, BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_failed, commitFailed));
        CollectionAssert.Contains(lines, string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_reason_format,
            fixture.Store.Requests.Single().Groups[1].KeepDirectory, BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_failed, commitFailed));
        CollectionAssert.Contains(lines, string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_source_format,
            fixture.Store.Requests.Single().Groups[0].SourceDirectories[0], commitFailed));
        // 確定の失敗と個別の削除の失敗が重なるグループは、確定の失敗を優先して表示する。
        CollectionAssert.Contains(lines, string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_reason_format,
            fixture.Store.Requests.Single().Groups[3].KeepDirectory, BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_failed, commitFailed));
        CollectionAssert.Contains(lines, string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_format,
            fixture.Store.Requests.Single().Groups[2].KeepDirectory, BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_failed),
            "もともと失敗したグループには、確定の失敗を付けません。");
    }

    /// <summary>
    /// 進捗画面が操作を呼ばずに失敗する（ディスパッチャーや所有者の不在・アプリの終了中・表示の例外）場合は、確認で承認していても、再生の停止・画面更新の抑制・モデルの呼出しをせず、
    /// 受付を解放し（再取得できる）、失敗を一回だけ報告して、結果の案内を出さない。
    /// 検出する誤り：受付の解放漏れ、報告の欠落・重複、操作が走らないのに結果を案内する、停止・抑制・モデルの呼出し。
    /// </summary>
    [TestMethod]
    [DataRow("OwnerUnavailable")]
    [DataRow("DispatcherUnavailable")]
    [DataRow("AppClosing")]
    [DataRow("Failed")]
    public async Task OrganizeSelectedGroups_ProgressRouteFailingWithoutRunningReleasesAdmissionAndReportsOnce(string statusName)
    {
        DuplicateGroup group = Lists.Group(@"C:\S\one", @"C:\S\two");
        using var fixture = new BatchFixture([group]);
        fixture.List.SelectVisible();
        fixture.Dialogs.Progress.FailWithoutRunning = Enum.Parse<UiDialogStatus>(statusName);

        DuplicateBatchOrganizeRunResult run = await fixture.Owner.OrganizeSelectedGroupsAsync();

        Assert.AreEqual(DuplicateBatchOrganizeRunStatus.Failed, run.Status);
        Assert.IsNotNull(run.Failure);
        Assert.IsTrue(fixture.Gate.TryEnter(out IDisposable? again), "受付を解放します。");
        again.Dispose();
        UiMessageRequest report = fixture.Dialogs.Messages.Single();
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.FileDbMutationReport_Title, report.Caption);
        Assert.AreEqual(0, fixture.Store.Requests.Count);
        CollectionAssert.AreEqual(new[] { "confirm", "progress-start", "progress-end", "message" }, fixture.Events);
    }

    /// <summary>
    /// 詳細は完了以外のグループを最大20件まで出し、21件目以降は「ほか{n}グループ…」に省略する（20件ちょうどなら省略の行は出ない）。完了のグループは数えない。
    /// 検出する誤り：上限の取り違え（19・21件表示）、20件ちょうどでの省略の行、完了グループの詳細表示。
    /// </summary>
    [TestMethod]
    [DataRow(20, 0)]
    [DataRow(21, 1)]
    [DataRow(25, 5)]
    public async Task OrganizeSelectedGroups_DetailsAreLimitedToTwentyGroups(int incompleteGroups, int expectedOmitted)
    {
        DuplicateGroup[] groups = [.. Enumerable.Range(0, incompleteGroups + 2).Select(index => Lists.Group($@"C:\S\g{index:00}"))];
        using var fixture = new BatchFixture(groups);
        fixture.List.SelectVisible();
        fixture.Store.Handler = (request, progress, _, _) =>
        {
            progress?.Invoke(new DuplicateBatchOrganizeProgress(request.Groups.Count, request.Groups.Count, null!, DuplicateBatchOrganizeProgressKind.Applying));
            // 先頭の二グループは完了、残りは拒否（スキップ）です。
            return Result(request.Groups.Select((group, index) => GroupResult(group.KeepDirectory,
                index < 2 ? DuplicateBatchOrganizeGroupStatus.Completed : DuplicateBatchOrganizeGroupStatus.Skipped,
                index < 2 ? DuplicateBatchOrganizeSkipReason.None : DuplicateBatchOrganizeSkipReason.NestedFolders, single: true)));
        };

        await fixture.Owner.OrganizeSelectedGroupsAsync();

        string[] lines = Lines(fixture.Dialogs.Messages.Single().MessageBoxText);
        string[] expectedDetails = [.. groups.Skip(2).Select(group => string.Format(
            BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_group_reason_format,
            group.Folders[0],
            BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_status_skipped,
            BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_reason_nested))];
        string[] detailLines = [.. lines.Where(expectedDetails.Contains)];
        CollectionAssert.AreEqual(expectedDetails.Take(20).ToArray(), detailLines, "要求順の先頭20件だけを出します。");
        string more = string.Format(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_result_more_format, expectedOmitted);
        Assert.AreEqual(expectedOmitted > 0, lines.Contains(more), "超えた分だけ、省略の行を出します。");
        Assert.AreEqual(0, lines.Count(line => line.Contains(@"C:\S\g00") || line.Contains(@"C:\S\g01")), "完了のグループは詳細に出しません。");
    }

    /// <summary>
    /// モデルの分類値（グループの状態・拒否の理由・移動元の理由）のすべてに、空でなく互いに異なる文言を対応させる。
    /// 検出する誤り：分類値の追加時の文言の漏れ（例外・空文字）、別の理由との文言の混同。
    /// </summary>
    [TestMethod]
    public void BatchOrganizePresentation_EveryClassificationHasItsOwnText()
    {
        string[] statuses = [.. Enum.GetValues<DuplicateBatchOrganizeGroupStatus>().Select(DuplicateBatchOrganizePresentation.StatusText)];
        string[] skips = [.. Enum.GetValues<DuplicateBatchOrganizeSkipReason>().Where(reason => reason != DuplicateBatchOrganizeSkipReason.None).Select(DuplicateBatchOrganizePresentation.SkipReasonText)];
        string[] sources = [.. Enum.GetValues<DuplicateBatchOrganizeSourceReason>().Select(DuplicateBatchOrganizePresentation.SourceReasonText)];
        Assert.IsNull(DuplicateBatchOrganizePresentation.SkipReasonText(DuplicateBatchOrganizeSkipReason.None));
        foreach (string[] texts in new[] { statuses, skips, sources })
        {
            Assert.IsTrue(texts.All(text => !string.IsNullOrWhiteSpace(text)));
            Assert.AreEqual(texts.Length, texts.Distinct().Count(), "文言が重複しています。");
        }
        Assert.AreEqual(5, statuses.Length);
        Assert.AreEqual(7, skips.Length);
        // 移動元の理由「確定の失敗」は、グループの理由「確定の失敗」と同じ文言を使う。
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Duplicate_batch_organize_reason_commit_failure, DuplicateBatchOrganizePresentation.SourceReasonText(DuplicateBatchOrganizeSourceReason.NotCommitted));
    }

}
