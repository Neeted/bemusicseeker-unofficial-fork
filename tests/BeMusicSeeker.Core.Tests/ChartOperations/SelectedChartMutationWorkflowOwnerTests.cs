using System.Collections.ObjectModel;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.SelectedChartMutationWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SelectedChartMutationWorkflowOwnerTests
{
    /// <summary>全folderの回答前はLだけを保持し、後続取消・閉鎖・表示失敗で先行Yesを実行しません。明示Noだけは全回答後の譜面削除へ進めます。</summary>
    [TestMethod]
    [DataRow("no")]
    [DataRow("cancel")]
    [DataRow("close")]
    [DataRow("failed")]
    [DataRow("unavailable")]
    [DataRow("null")]
    [DataRow("throw")]
    public async Task DeleteAsync_CollectsAllWholeFolderDecisionsBeforeStartingMutation(string decision)
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { WholeFolderDeletePaths = [@"C:\One", @"C:\Two"] };
        var presentation = new RecordingPresentation(events);
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("second folder failed");
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
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, dialogs, store, gate, activity);
        ChartOperationTarget target = CreateTarget("library.bms", ChartOperationSourceScope.Library, false,
            ChartOperationCapabilities.RemoveFromLibrary);
        Task<SelectedChartMutationResult>? operation = null;
        void AssertNotStarted()
        {
            Assert.AreEqual(0, store.LibraryDeleteCalls);
            Assert.AreEqual(0, store.PhysicalMutationStarts);
            Assert.AreEqual(0, events.Count);
            Assert.IsFalse(activity.IsActive);
        }
        try
        {
            operation = owner.DeleteAsync(new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));
            Assert.AreSame(reached.Task, await Task.WhenAny(reached.Task, operation));
            await reached.Task;
            Assert.IsTrue(gate.IsActive);
            AssertNotStarted();
            if (decision == "throw") { answer.SetException(failure); }
            else
            {
                answer.SetResult(decision switch
                {
                    "no" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.No),
                    "close" => UiDialogResult.ClosedByUser(MessageBoxResult.Yes),
                    "failed" => UiDialogResult.Failed(failure),
                    "unavailable" => UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable),
                    "null" => new object?[] { null }.Cast<UiDialogResult>().Single(),
                    _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
                });
            }
            SelectedChartMutationResult result = await operation;
            Assert.AreEqual(2, requests.Count);
            Assert.IsFalse(gate.IsActive);
            if (decision == "no")
            {
                Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                Assert.AreEqual(1, store.LibraryDeleteCalls);
                Assert.AreEqual(1, store.PhysicalMutationStarts);
                CollectionAssert.AreEqual(store.WholeFolderDeletePaths.Where(path => requests[0].MessageBoxText.Contains(path)).ToArray(),
                    store.ApprovedWholeFolderDeletePaths.ToArray());
                Assert.AreEqual(target.Chart.Path, store.LibraryCharts.Single().Path);
            }
            else
            {
                AssertNotStarted();
                Assert.AreEqual(decision is "cancel" or "close", result.Succeeded);
                if (decision is "cancel" or "close") { Assert.IsNull(result.Failure); }
                if (decision is "failed" or "throw") { Assert.AreSame(failure, result.Failure); }
                if (decision == "unavailable") { StringAssert.Contains(result.Failure.Message, "OwnerUnavailable"); }
                if (decision == "null") { StringAssert.Contains(result.Failure.Message, "returned no result"); }
            }
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
    }

    /// <summary>初回の件数確認が表示不能ならfolder確認・停止・P取得を始めず、元失敗とL解放を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeleteAsync_FirstConfirmationFailureDoesNotStartFolderDecisions(bool unavailable)
    {
        var failure = new IOException("initial confirmation failed");
        var events = new List<string>();
        var store = new RecordingStore(events) { WholeFolderDeletePaths = [@"C:\One"] };
        var presentation = new RecordingPresentation(events);
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        int prompts = 0;
        var dialogs = new FakeUiDialogService
        {
            ConfirmationHandler = request =>
        {
            prompts++;
            Assert.AreEqual(MessageBoxButton.OKCancel, request.Button);
            Assert.AreEqual(MessageBoxResult.Cancel, request.DefaultResult);
            return Task.FromResult(unavailable ? UiDialogResult.NotShown(UiDialogStatus.OwnerUnavailable) : UiDialogResult.Failed(failure));
        }
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, dialogs, store, gate, activity);
        try
        {
            ChartOperationTarget target = CreateTarget("library.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RemoveFromLibrary);
            SelectedChartMutationResult result = await owner.DeleteAsync(new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));
            Assert.IsFalse(result.Succeeded);
            Assert.IsNotNull(result.Failure);
            if (unavailable) { StringAssert.Contains(result.Failure.Message, "OwnerUnavailable"); }
            else { Assert.AreSame(failure, result.Failure); }
            Assert.AreEqual(1, prompts);
            Assert.AreEqual(0, store.PhysicalMutationStarts);
            Assert.AreEqual(0, store.LibraryDeleteCalls);
            Assert.AreEqual(0, events.Count);
            Assert.IsFalse(gate.IsActive);
            Assert.IsFalse(activity.IsActive);
        }
        finally
        {
            owner.WorkflowChanged -= presentation.OnWorkflowChanged;
            activity.ActivityChanged -= presentation.OnActivityChanged;
        }
    }

    /// <summary>確定済み削除のcatalog失敗を受付・活動の解放後に一回報告し、報告例外の有無で元原因と結果が失われる誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task DeleteAsync_ReportsCatalogFailureAfterReleaseAndRetainsFacts(bool reportThrows, bool registrationFails)
    {
        var failure = new IOException("catalog deletion failure");
        Exception? registrationFailure = registrationFails ? new IOException("registration deletion failure") : null;
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget("deleted.bms", string.Empty)],
            durableCommit: true,
            catalogChartRemovalCount: 1,
            applyFailure: failure);
        var outcome = new LibraryChartRemovalOutcome(
            [new("deleted.bms", LibraryChartRemovalState.Confirmed)],
            sessionReceipt,
            catalogApplyAttempted: true, registrationFailure: registrationFailure);
        var store = new RecordingStore { RemovalOutcome = outcome };
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        FakeUiDialogService dialogs = AcceptedMessageDialogs();
        bool releasedAtReport = false;
        dialogs.OnMessage = () =>
        {
            releasedAtReport = gate.TryEnter(out IDisposable lease) && !activity.IsActive;
            lease?.Dispose();
            if (reportThrows)
            {
                throw new IOException("optional report failed");
            }
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store, gate, activity);
        ChartOperationTarget target = CreateTarget("deleted.bms", ChartOperationSourceScope.Library, false,
            ChartOperationCapabilities.RemoveFromLibrary);

        SelectedChartMutationResult result = await owner.DeleteAsync(new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));

        Assert.IsTrue(releasedAtReport);
        Assert.IsFalse(result.Succeeded);
        Assert.AreSame(outcome, result.RemovalOutcome);
        Assert.AreSame(sessionReceipt, result.MutationReceipt);
        if (registrationFails)
        {
            ReadOnlyCollection<Exception> causes = ((AggregateException)result.Failure).Flatten().InnerExceptions;
            CollectionAssert.Contains(causes, failure);
            CollectionAssert.Contains(causes, registrationFailure);
            StringAssert.Contains(dialogs.Messages.Single().MessageBoxText, "registration deletion failure");
        }
        else { Assert.AreSame(failure, result.Failure); }
        Assert.AreEqual(1, store.LibraryDeleteCalls);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Error, dialogs.Messages.Single().Icon);
    }

    /// <summary>保留削除の確認を取消した場合は変更せず正常終端し、取消後の削除を検出します。</summary>
    [TestMethod]
    public async Task DeletePendingAsync_RejectedDialogDoesNotMutate()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        var dialogs = new FakeUiDialogService
        {
            PendingDeleteResult = new UiInteractionResult<bool>(UiInteractionStatus.CancelledByUser)
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget target = CreateTarget("pending.bms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.UpdateInstallDestination);

        SelectedChartMutationResult result = await owner.DeleteAsync(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.InstallPending));

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(0, store.PendingDeleteCalls);
    }

    /// <summary>保留削除の確認失敗は失敗として返し、削除への代替を検出します。</summary>
    [TestMethod]
    public async Task DeletePendingAsync_DialogFailureReturnsFailureWithoutMutation()
    {
        var store = new RecordingStore();
        var dialogs = new FakeUiDialogService
        {
            PendingDeleteResult = new UiInteractionResult<bool>(
                UiInteractionStatus.Failed,
                error: new IOException("pending dialog failed"))
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store);
        ChartOperationTarget target = CreateTarget("pending.bms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.UpdateInstallDestination);

        SelectedChartMutationResult result = await owner.DeleteAsync(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.InstallPending));

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(0, store.PendingDeleteCalls);
    }

    /// <summary>確認結果が欠落した保留削除は失敗として拒否し、承認扱いで変更する誤りを検出します。</summary>
    [TestMethod]
    public async Task DeletePendingAsync_NullDialogResultReturnsFailureWithoutMutation()
    {
        var store = new RecordingStore();
        var dialogs = new FakeUiDialogService();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store);
        ChartOperationTarget target = CreateTarget("pending.bms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.UpdateInstallDestination);

        SelectedChartMutationResult result = await owner.DeleteAsync(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.InstallPending));

        Assert.IsFalse(result.Succeeded);
        Assert.IsInstanceOfType<InvalidOperationException>(result.Failure);
        Assert.AreEqual(0, store.PendingDeleteCalls);
    }

    /// <summary>削除権限のない選択は何も変更せず終え、保留・所有storeへの誤った転送を検出します。</summary>
    [TestMethod]
    public async Task DeleteAsync_WithoutEligibleTargetsDoesNotShowDialogOrMutate()
    {
        var store = new RecordingStore();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget("library.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.None);

        SelectedChartMutationResult result = await owner.DeleteAsync(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, store.LibraryDeleteCalls);
        Assert.AreEqual(0, store.PendingDeleteCalls);
    }

    /// <summary>保留削除のfolder指定をstoreへ渡し、活動・再生停止・更新抑制と各解放の順序欠落を検出します。</summary>
    [TestMethod]
    public async Task DeletePendingAsync_AcceptedPreservesFolderCheckboxAndOperationOrder()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        var dialogs = new FakeUiDialogService
        {
            PendingDeleteResult = new UiInteractionResult<bool>(UiInteractionStatus.Accepted, true)
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget target = CreateTarget("pending.bms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.UpdateInstallDestination);

        SelectedChartMutationResult result = await owner.DeleteAsync(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.InstallPending));

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(store.DeleteContainingPackageFoldersWhenNoBms);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "playback-stop", "pending-refresh-start", "pending-refresh-end", "activity-end" },
            presentation.Events);
    }

    /// <summary>所有譜面だけを削除へ渡して承認済みfolder候補を保持し、保留選択の混入や承認範囲の欠落を検出します。</summary>
    [TestMethod]
    public async Task DeleteLibraryAsync_FiltersScopeAndPassesApprovedWholeFolders()
    {
        var store = new RecordingStore { WholeFolderDeletePaths = [@"C:\Songs\Folder"] };
        var presentation = new RecordingPresentation();
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
            ])
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget libraryTarget = CreateTarget("library.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RemoveFromLibrary);
        ChartOperationTarget pendingTarget = CreateTarget("pending.bms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.UpdateInstallDestination);

        SelectedChartMutationResult result = await owner.DeleteAsync(
            new SelectedChartDeleteRequest([libraryTarget, pendingTarget], libraryTarget, MainViewOperationSection.Library));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, store.LibraryDeleteCalls);
        CollectionAssert.AreEqual(store.WholeFolderDeletePaths.ToArray(), store.ApprovedWholeFolderDeletePaths.ToArray());
        Assert.AreEqual("library.bms", Path.GetFileName(store.LibraryCharts[0].Path));
    }

    /// <summary>folder削除は再生停止と抑制開始後に行い、変更後に抑制・活動を解放する順序違反を検出します。</summary>
    [TestMethod]
    public async Task DeleteLibraryAsync_StopsPlaybackBeforeApprovedFolderDeletion()
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { WholeFolderDeletePaths = [@"C:\Songs\Folder"] };
        var presentation = new RecordingPresentation(events);
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
            ])
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget target = CreateTarget("library.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RemoveFromLibrary);

        SelectedChartMutationResult result = await owner.DeleteAsync(
            new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "playback-stop", "library-refresh-start", "store-library-delete", "library-refresh-end", "activity-end" },
            events);
    }

    /// <summary>BMS・PMSを対応する拡張子へ分けて一回の所有変更へ渡し、種類の取り違えや対象別確定を検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_SplitsBmsAndPmsAndPreservesRouteOrder()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        FakeUiDialogService dialogs = AcceptedMessageDialogs();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, dialogs, store);
        ChartOperationTarget bChart = CreateTarget("alpha.bme", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RenameInvalidExtension);
        ChartOperationTarget pChart = CreateTarget("beta.pms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([bChart, pChart], isPendingSelected: false));

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(new[] { "library:.bmx", "library:.pmx" }, store.RenameOperations);
        Assert.AreEqual(1, store.RenameCallCount);
    }

    /// <summary>保留BMS・PMSは保留の抑制範囲で個別変更し、所有終端storeへの誤転送や活動解放漏れを検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_PendingUsesPendingRouteAndRefreshScope()
    {
        var events = new List<string>();
        var store = new TerminalRecordingStore(LibraryMutationSessionReceipt.Empty, events);
        var presentation = new RecordingPresentation(events);
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, AcceptedMessageDialogs(), store);
        ChartOperationTarget bChart = CreateTarget("pending.bme", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.RenameInvalidExtension);
        ChartOperationTarget pChart = CreateTarget("pending.pms", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([bChart, pChart], isPendingSelected: true));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, store.RenameTerminalCalls);
        CollectionAssert.AreEqual(new[] { "pending:.bmx", "pending:.pmx" }, store.RenameOperations);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "playback-stop", "pending-refresh-start", "store-pending-rename", "store-pending-rename", "pending-refresh-end", "activity-end" },
            events);
    }

    /// <summary>二種類の拡張子を一セッションへまとめ、catalog失敗を解放後に元receiptとともに報告する接続漏れを検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_TerminalStoreBatchesFamiliesIntoOneSession()
    {
        var failure = new IOException("catalog rename finalization failed");
        var receipt = new LibraryMutationSessionReceipt(
            [
                new LibraryMutationSessionTarget("alpha.bme", "alpha.bmx"),
                new LibraryMutationSessionTarget("beta.pms", "beta.pmx")
            ],
            durableCommit: true,
            catalogChartRemovalCount: 2,
            applyFailure: failure);
        var store = new TerminalRecordingStore(receipt);
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        FakeUiDialogService dialogs = AcceptedMessageDialogs();
        bool reportAfterRelease = false;
        dialogs.OnMessage = () =>
        {
            reportAfterRelease = gate.TryEnter(out IDisposable lease) && !activity.IsActive;
            lease?.Dispose();
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store, gate, activity);
        ChartOperationTarget bChart = CreateTarget(
            "alpha.bme",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.RenameInvalidExtension);
        ChartOperationTarget pChart = CreateTarget(
            "beta.pms",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([bChart, pChart], isPendingSelected: false));

        Assert.IsFalse(result.Succeeded);
        Assert.AreSame(failure, result.Failure);
        Assert.AreSame(receipt, result.MutationReceipt);
        Assert.AreEqual(1, store.RenameTerminalCalls);
        Assert.AreEqual(2, store.RenameBatches.Count);
        Assert.AreEqual(".bmx", store.RenameBatches[0].NewExtension);
        Assert.AreEqual(".pmx", store.RenameBatches[1].NewExtension);
        Assert.AreEqual(1, store.RenameBatches[0].Targets.Count);
        Assert.AreEqual(1, store.RenameBatches[1].Targets.Count);
        Assert.IsTrue(reportAfterRelease);
        Assert.AreEqual(1, dialogs.Messages.Count);
    }

    /// <summary>一部の物理変更失敗は確定成功を保持して一回報告し、全体失敗への読み替えや対象通知の欠落を検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_ItemFailuresAreAggregatedOnceWithoutChangingSuccessSemantics()
    {
        var receipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget("alpha.bme", "alpha.bmx")],
            durableCommit: true,
            catalogChartRemovalCount: 1,
            itemFailures:
            [
                new LibraryMutationSessionItemFailure(
                    new LibraryMutationSessionTarget("beta.pms", string.Empty),
                    new IOException("filesystem rename failed"))
            ]);
        var store = new TerminalRecordingStore(receipt);
        FakeUiDialogService dialogs = AcceptedMessageDialogs();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store);
        ChartOperationTarget bChart = CreateTarget(
            "alpha.bme",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.RenameInvalidExtension);
        ChartOperationTarget pChart = CreateTarget(
            "beta.pms",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([bChart, pChart], isPendingSelected: false));

        Assert.IsTrue(result.Succeeded,
            "A per-item physical failure must not discard confirmed success-only refresh semantics.");
        Assert.IsNull(result.Failure);
        Assert.AreSame(receipt, result.MutationReceipt);
        Assert.AreEqual(1, store.RenameTerminalCalls);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(MessageBoxImage.Error, dialogs.Messages[0].Icon);
        StringAssert.Contains(dialogs.Messages[0].MessageBoxText, "beta.pms");
    }

    /// <summary>拡張子修正権限のない選択は変更を開始せず終え、対象の受付漏れを検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_WithoutEligibleTargetsDoesNotShowDialogOrMutate()
    {
        var store = new RecordingStore();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.None);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([target], isPendingSelected: false));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, store.RenameCallCount);
    }

    /// <summary>拡張子修正を取消した場合は変更を開始せず正常終端し、承認なしの実行を検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_RejectedConfirmationDoesNotMutate()
    {
        var store = new RecordingStore();
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            ])
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store);
        ChartOperationTarget target = CreateTarget("alpha.bme", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([target], isPendingSelected: false));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, store.RenameCallCount);
    }

    /// <summary>拡張子修正の確認失敗は失敗を返し、確認なしで実行する代替を検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_DialogFailureReturnsFailureWithoutMutation()
    {
        var store = new RecordingStore();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget("alpha.bme", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([target], isPendingSelected: false));

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(0, store.RenameCallCount);
    }

    /// <summary>所有の操作へ保留対象を渡した場合は失敗として拒否し、別区画の変更を検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_RejectsTargetsFromDifferentOperationSection()
    {
        var store = new RecordingStore();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), new FakeUiDialogService(), store);
        ChartOperationTarget pendingTarget = CreateTarget("pending.bme", ChartOperationSourceScope.PendingPackage, true, ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([pendingTarget], isPendingSelected: false));

        Assert.IsFalse(result.Succeeded);
        Assert.IsInstanceOfType<InvalidOperationException>(result.Failure);
        Assert.AreEqual(0, store.RenameCallCount);
    }

    /// <summary>保留flagと所有scopeが矛盾する対象は拒否し、不整合な要求の実行を検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_RejectsTargetWithPendingFlagOnly()
    {
        var store = new RecordingStore();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), new FakeUiDialogService(), store);
        ChartOperationTarget inconsistentTarget = CreateTarget("alpha.bme", ChartOperationSourceScope.Library, true, ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([inconsistentTarget], isPendingSelected: true));

        Assert.IsFalse(result.Succeeded);
        Assert.IsInstanceOfType<InvalidOperationException>(result.Failure);
        Assert.AreEqual(0, store.RenameCallCount);
    }

    /// <summary>拡張子修正storeの例外後も抑制と活動を終え、変更失敗時に画面の抑制を残す誤りを検出します。</summary>
    [TestMethod]
    public async Task RenameInvalidExtensionsAsync_MutationFailureReleasesSuppressionAndActivity()
    {
        var store = new RecordingStore { Failure = new IOException("mutation failed") };
        var presentation = new RecordingPresentation();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, AcceptedMessageDialogs(), store);
        ChartOperationTarget target = CreateTarget("alpha.bme", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.RenameInvalidExtension);

        SelectedChartMutationResult result = await owner.RenameInvalidExtensionsAsync(
            new SelectedInvalidExtensionRenameRequest([target], isPendingSelected: false));

        Assert.IsFalse(result.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "playback-stop", "library-refresh-start", "library-refresh-end", "activity-end" },
            presentation.Events);
    }

    /// <summary>記録storeで宛先の受渡し、停止・反映・path refresh・活動と抑制の終端順を維持します。本番準備結果とFS/DBは実store代表試験へ分担します。</summary>
    [TestMethod]
    public async Task MoveAsync_AcceptedUsesSelectedLibraryRefsAndPublishesPathRefresh()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, AcceptedMessageDialogs(), store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary);

        SelectedChartMutationResult result = await owner.MoveAsync(
            new SelectedChartMoveRequest([target], @"D:\Moved"));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(@"D:\Moved", store.MovedDirectory);
        Assert.AreEqual(1, presentation.PathRefreshCalls);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "playback-stop", "library-refresh-start", "store-move", "path-refresh", "library-refresh-end", "activity-end" },
            events);
    }

    /// <summary>一部移動の物理失敗とdurable成功を保持して解放後に報告し、任意通知先の例外で元receiptを失う誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MoveAsync_FailedReceiptSurvivesOptionalObserverFailure(bool observerThrows)
    {
        var primary = new IOException("move failed before commit");
        var receipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\Songs\First", @"D:\Moved\First")],
            durableCommit: true,
            catalogChartPathChangeCount: 1,
            folderReferenceMoveCount: 1,
            physicalFailure: primary,
            failedTarget: new LibraryMutationSessionTarget(@"C:\Songs\Second", @"D:\Moved\Second"));
        var store = new TerminalRecordingStore(receipt);
        var presentation = new RecordingPresentation();
        FakeUiDialogService dialogs = AcceptedMessageDialogs();
        var gate = new ChartFileOperationSynchronizer();
        var activity = new ChartMutationActivityOwner();
        bool reportAfterRelease = false;
        dialogs.OnMessage = () =>
        {
            bool gateReleased = gate.TryEnter(out IDisposable lease);
            reportAfterRelease = gateReleased && !activity.IsActive && presentation.Events.Contains("library-refresh-end");
            lease?.Dispose();
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, dialogs, store, gate, activity);
        if (observerThrows)
        {
            owner.WorkflowChanged += (_, change) =>
            {
                if (change is SelectedChartMutationRefreshSuppressionChangedEventArgs { IsSuppressed: false })
                {
                    throw new IOException("optional observer failed");
                }
            };
        }

        SelectedChartMutationResult result = await owner.MoveAsync(new SelectedChartMoveRequest(
            [CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary)],
            @"D:\Moved"));

        Assert.AreSame(receipt, result.MutationReceipt, "Terminal facts must survive optional notification failure.");
        Assert.IsFalse(result.Succeeded, "A session with an unexpected physical failure is not all-success.");
        Assert.AreSame(primary, result.Failure);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.AreEqual(1, store.Calls);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(MessageBoxImage.Error, dialogs.Messages[0].Icon);
        Assert.IsTrue(reportAfterRelease);
    }

    /// <summary>cleanupだけの失敗はdurable成功を保って警告し、表示例外の有無で確定結果を失敗へ戻す誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MoveAsync_CleanupWarningAndReporterFailureKeepDurableSuccess(bool reporterThrows)
    {
        var receipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\Songs\First", @"D:\Moved\First")],
            durableCommit: true,
            catalogChartPathChangeCount: 1,
            folderReferenceMoveCount: 1,
            cleanupFailure: new IOException("cleanup failed"));
        var store = new TerminalRecordingStore(receipt);
        FakeUiDialogService dialogs = AcceptedMessageDialogs();
        if (reporterThrows)
        {
            dialogs.OnMessage = () => throw new IOException("report failed");
        }

        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store);
        SelectedChartMutationResult result = await owner.MoveAsync(new SelectedChartMoveRequest(
            [CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary)], @"D:\Moved"));
        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.AreSame(receipt, result.MutationReceipt);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(MessageBoxImage.Warning, dialogs.Messages[0].Icon);
        Assert.AreEqual(1, store.Calls);
    }

    /// <summary>正常なdurable移動は成功して終端ダイアログを増やさず、余分な通知を検出します。</summary>
    [TestMethod]
    public async Task MoveAsync_NormalTerminalReceiptIsSilent()
    {
        FakeUiDialogService dialogs = AcceptedMessageDialogs();
        var store = new TerminalRecordingStore(new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\Songs\First", @"D:\Moved\First")],
            durableCommit: true,
            catalogChartPathChangeCount: 1,
            folderReferenceMoveCount: 1));
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store);
        SelectedChartMutationResult result = await owner.MoveAsync(new SelectedChartMoveRequest(
            [CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary)], @"D:\Moved"));
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, dialogs.Messages.Count);
    }

    /// <summary>移動確認の取消はstoreを呼ばず正常終端し、承認なしの移動を検出します。</summary>
    [TestMethod]
    public async Task MoveAsync_RejectedConfirmationDoesNotMutate()
    {
        var store = new RecordingStore();
        var dialogs = new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)
            ])
        };
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), dialogs, store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary);

        SelectedChartMutationResult result = await owner.MoveAsync(
            new SelectedChartMoveRequest([target], @"D:\Moved"));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(string.Empty, store.MovedDirectory);
    }

    /// <summary>移動権限不在と宛先空欄は変更なしで終え、不完全な要求をstoreへ渡す誤りを検出します。</summary>
    [TestMethod]
    public async Task MoveAsync_WithoutEligibleTargetsOrDestinationDoesNotShowDialogOrMutate()
    {
        var store = new RecordingStore();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(new RecordingPresentation(), new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.None);
        ChartOperationTarget eligibleTarget = CreateTarget("beta.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary);

        SelectedChartMutationResult noTargetResult = await owner.MoveAsync(
            new SelectedChartMoveRequest([target], @"D:\Moved"));
        Assert.IsTrue(noTargetResult.Succeeded);
        SelectedChartMutationResult noDestinationResult = await owner.MoveAsync(
            new SelectedChartMoveRequest([eligibleTarget], string.Empty));

        Assert.IsTrue(noDestinationResult.Succeeded);
        Assert.AreEqual(string.Empty, store.MovedDirectory);
    }

    /// <summary>文字コード指定が空欄でも維持し、二件のBMSから修正capabilityのある対象だけを要求へ残す選別誤りを検出します。</summary>
    [TestMethod]
    public void SelectedChartEncodingRequest_FiltersBmsCapabilityAndPreservesEncoding()
    {
        ChartOperationTarget eligible = CreateTarget(
            "alpha.bms",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.RunBmsEncodingFix);
        ChartOperationTarget ineligible = CreateTarget(
            "beta.bms",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.None);

        var request = new SelectedChartEncodingRequest([eligible, ineligible], string.Empty);

        Assert.IsTrue(request.HasTargets);
        Assert.AreEqual(string.Empty, request.Encoding);
        Assert.AreEqual(1, request.Charts.Count);
        Assert.AreSame(eligible.Chart.Token, request.Charts[0].Token);
    }

    /// <summary>文字コードは指定と対象を維持して保存後に更新を通知し、保存前の成功公開を検出します。</summary>
    [TestMethod]
    public async Task ApplyEncoding_StoresBeforeRefresh()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget(
            "alpha.bms",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.RunBmsEncodingFix);

        SelectedChartMutationResult result = await owner.ApplyEncodingAsync(new SelectedChartEncodingRequest([target], string.Empty));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, store.EncodingCallCount);
        Assert.AreEqual(string.Empty, store.Encoding);
        Assert.AreSame(target.Chart.Token, store.EncodingFiles[0].Token);
        Assert.IsTrue(events.IndexOf("store-encoding") < events.IndexOf("encoding-refresh"));
    }

    /// <summary>文字コード修正権限がない場合は保存も通知も行わず、不適格な対象の変更を検出します。</summary>
    [TestMethod]
    public async Task ApplyEncoding_WithoutTargetsDoesNotStoreOrRefresh()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var presentation = new RecordingPresentation(events);
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget(
            "alpha.bms",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.None);

        SelectedChartMutationResult result = await owner.ApplyEncodingAsync(new SelectedChartEncodingRequest([target], "utf-8"));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, store.EncodingCallCount);
        Assert.AreEqual(0, presentation.EncodingRefreshCalls);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>文字コード保存の例外は失敗を返して更新を通知せず、未保存の値を画面へ公開する誤りを検出します。</summary>
    [TestMethod]
    public async Task ApplyEncoding_FailureDoesNotRefresh()
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { Failure = new IOException("encoding failed") };
        var presentation = new RecordingPresentation(events);
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget(
            "alpha.bms",
            ChartOperationSourceScope.Library,
            false,
            ChartOperationCapabilities.RunBmsEncodingFix);

        SelectedChartMutationResult result = await owner.ApplyEncodingAsync(new SelectedChartEncodingRequest([target], "utf-8"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsInstanceOfType<IOException>(result.Failure);
        Assert.AreEqual(1, store.EncodingCallCount);
        Assert.AreEqual(0, presentation.EncodingRefreshCalls);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>移動storeの例外ではpath更新を行わず抑制と活動を終端させ、失敗後の更新や変更境界の終端漏れを検出します。</summary>
    [TestMethod]
    public async Task MutationFailure_ReleasesSuppressionAndActivity()
    {
        var store = new RecordingStore { Failure = new IOException("mutation failed") };
        var presentation = new RecordingPresentation();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, AcceptedMessageDialogs(), store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary);

        SelectedChartMutationResult result = await owner.MoveAsync(
            new SelectedChartMoveRequest([target], @"D:\Moved"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, presentation.PathRefreshCalls);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "playback-stop", "library-refresh-start", "library-refresh-end", "activity-end" },
            presentation.Events);
    }

    /// <summary>移動確認の表示失敗は変更せず失敗を返し、確認の代替で実行する誤りを検出します。</summary>
    [TestMethod]
    public async Task DialogFailure_DoesNotFallbackToMutation()
    {
        var store = new RecordingStore();
        var presentation = new RecordingPresentation();
        SelectedChartMutationWorkflowOwner owner = CreateOwner(presentation, new FakeUiDialogService(), store);
        ChartOperationTarget target = CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary);

        SelectedChartMutationResult result = await owner.MoveAsync(
            new SelectedChartMoveRequest([target], @"D:\Moved"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Failure);
        Assert.AreEqual(string.Empty, store.MovedDirectory);
    }

    private sealed class TerminalRecordingStore(
        LibraryMutationSessionReceipt receipt,
        List<string>? events = null)
        : RecordingStore(events)
    {
        internal int Calls { get; private set; }

        internal int RenameTerminalCalls { get; private set; }

        internal IReadOnlyList<LibraryFileExtensionRenameBatch> RenameBatches { get; private set; } = [];

        public override LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(
            BMSLibrary library,
            IReadOnlyList<LibraryFileExtensionRenameBatch> batches,
            LibraryOperationNotices notices)
        {
            RenameTerminalCalls++;
            RenameBatches = batches;
            return receipt;
        }

        public override LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(BMSLibrary library, ChartLibraryMoveRequest request, LibraryOperationNotices notices)
        {
            Calls++;
            return receipt;
        }
    }

}
