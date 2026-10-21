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

using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SelectedChartMutationWorkflowOwnerTests
{
    /// <summary>確定済み削除のcatalog失敗を受付・活動の解放後に一回報告し、報告例外の有無で元原因と結果が失われる誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeleteAsync_ReportsCatalogFailureAfterReleaseAndRetainsFacts(bool reportThrows)
    {
        var failure = new IOException("catalog deletion failure");
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget("deleted.bms", string.Empty)],
            durableCommit: true,
            catalogChartRemovalCount: 1,
            applyFailure: failure);
        var outcome = new LibraryChartRemovalOutcome(
            [new("deleted.bms", LibraryChartRemovalState.Confirmed)],
            sessionReceipt,
            catalogApplyAttempted: true);
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
        Assert.AreSame(failure, result.Failure);
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

    /// <summary>本番storeの準備から同じ実対象のP・停止・FS/SQLite/current/事前構築lookup・通知・次要求までを接続します。停止中に空の除外先を消しても対象を復活させません。真の交差Busyと複数対象・失敗は既存上位／下位へ分担し、記録storeの成功順序保証も維持します。</summary>
    [TestMethod]
    public async Task MoveAsync_RealPreparedRootMovePreservesTargetsAndAcceptsFreshRequest()
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException("Fixture root is missing.");
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            string blockedDestination = Path.Combine(destination, "Blocked");
            string[] directories = [Path.Combine(source, "A"), Path.Combine(source, "Blocked"), Path.Combine(root, "Background")];
            var charts = new List<ChartFile>();
            for (int index = 0; index < directories.Length; index++)
            {
                Directory.CreateDirectory(directories[index]);
                string path = Path.Combine(directories[index], "chart.bms");
                File.WriteAllText(path, "chart bytes " + index);
                charts.Add(CreateFile(new string((char)('a' + index), 32), path));
            }
            Directory.CreateDirectory(blockedDestination);
            string lr2Root = Path.Combine(root, "LR2");
            LR2Config config = BmsPlaylistTestSupport.CreateLr2Config(lr2Root, source);
            var library = new TestBmsLibrary(songDbPath, () => config, null, null,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                    LR2RootPath = lr2Root,
                    LR2CustomFolderOutputBaseDir = blockedDestination
                })
            { BmsCharts = charts, BmsonCharts = [] };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(charts);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            foreach (ChartFile chart in charts)
            {
                CollectionAssert.AreEqual(new[] { Path.GetDirectoryName(chart.Path) }, initialLookup.GetDistinctDirectoriesByPrimaryHash(chart.Md5).ToArray());
            }
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var presentation = new RecordingPresentation { BeforeStop = () => { stopped.TrySetResult(); return releaseStop.Task; } };
            var activity = new ChartMutationActivityOwner();
            FakeUiDialogService dialogs = AcceptedMessageDialogs();
            dialogs.ConfirmationResults.Enqueue(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
            var owner = new SelectedChartMutationWorkflowOwner(() => library, library.OperationAdmission,
                activity, presentation, dialogs, dialogs, new BmsLibrarySelectedChartMutationStore());
            int appliedNotifications = 0;
            Exception? notificationFailure = null;
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            owner.WorkflowChanged += ObserveApplied;
            activity.ActivityChanged += presentation.OnActivityChanged;
            Task<SelectedChartMutationResult> first = Task.FromResult(SelectedChartMutationResult.Completed);
            Task<SelectedChartMutationResult> next = Task.FromResult(SelectedChartMutationResult.Completed);
            IDisposable? heldPlaylist = null;
            try
            {
                Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out heldPlaylist));
                first = owner.MoveAsync(new SelectedChartMoveRequest([Target(charts[0]), Target(charts[1])], destination));
                await TestUiDispatcherHost.AwaitNotificationAsync(stopped.Task, first, "root-move-playback-stop");
                Assert.IsFalse(first.IsCompleted);
                Assert.IsTrue(library.OperationAdmission.IsActive);
                Assert.IsTrue(activity.IsActive);
                Assert.IsFalse(library.OperationAdmission.TryEnter(out IDisposable competing));
                competing?.Dispose();
                CollectionAssert.AreEqual(new[] { "activity-start", "playback-stop" }, presentation.Events);
                AssertState(aMoved: false, blockedMoved: false);
                Assert.IsFalse(Directory.Exists(Path.Combine(destination, "A")));
                Assert.IsTrue(Directory.Exists(blockedDestination));
                Assert.AreEqual(0, Directory.GetFileSystemEntries(blockedDestination).Length);
                // 自分の空fixtureだけを消し、準備済み対象と適用時の存在条件を意図的に違わせます。
                Directory.Delete(blockedDestination, recursive: false);
                releaseStop.TrySetResult();
                SelectedChartMutationResult result = await first;
                AssertSuccessfulReceipt(result);
                Assert.IsNull(notificationFailure, notificationFailure?.ToString());
                Assert.AreEqual(1, appliedNotifications);
                Assert.AreEqual(1, presentation.PathRefreshCalls);
                CollectionAssert.AreEqual(new[] { "activity-start", "playback-stop", "library-refresh-start", "path-refresh", "library-refresh-end", "activity-end" }, presentation.Events);
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(activity.IsActive);
                AssertState(aMoved: true, blockedMoved: false);
                Assert.IsFalse(Directory.Exists(blockedDestination));
                heldPlaylist.Dispose();
                heldPlaylist = null;
                AssertState(aMoved: true, blockedMoved: false);
                Assert.IsFalse(Directory.Exists(blockedDestination), "Pの解放だけでは除外した対象を再実行しません。");
                presentation.BeforeStop = null;
                next = owner.MoveAsync(new SelectedChartMoveRequest([Target(charts[1])], destination));
                AssertSuccessfulReceipt(await next);
                Assert.IsNull(notificationFailure, notificationFailure?.ToString());
                Assert.AreEqual(2, appliedNotifications);
                Assert.AreEqual(2, presentation.PathRefreshCalls);
                CollectionAssert.AreEqual(Enumerable.Repeat(new[] { "activity-start", "playback-stop", "library-refresh-start", "path-refresh", "library-refresh-end", "activity-end" }, 2).SelectMany(events => events).ToArray(), presentation.Events);
                AssertState(aMoved: true, blockedMoved: true);
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable fresh));
                fresh.Dispose();
            }
            finally
            {
                releaseStop.TrySetResult();
                heldPlaylist?.Dispose();
                try { await Task.WhenAll(first, next); }
                finally
                {
                    owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                    owner.WorkflowChanged -= ObserveApplied;
                    activity.ActivityChanged -= presentation.OnActivityChanged;
                    library.RequestShutdown("root-move-entry-test");
                }
            }

            void ObserveApplied(object? sender, SelectedChartMutationWorkflowChangedEventArgs args)
            {
                if (args is not SelectedChartMutationAppliedEventArgs applied) { return; }
                appliedNotifications++;
                try
                {
                    Assert.IsTrue(applied.LibraryPathChanged);
                    Assert.IsTrue(library.OperationAdmission.IsActive, "実保存結果の必須公開までLを保持します。");
                    AssertState(aMoved: true, blockedMoved: appliedNotifications == 2);
                }
                catch (Exception failure) { notificationFailure ??= failure; }
            }

            void AssertState(bool aMoved, bool blockedMoved)
            {
                using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                Assert.AreEqual(3, read.Table<LR2SongDB.song>().Count());
                InstalledChartLookupIndexSnapshot lookup = InvokeCreateInstalledChartLookupSnapshot(library);
                for (int index = 0; index < charts.Count; index++)
                {
                    bool moved = index == 0 ? aMoved : index == 1 && blockedMoved;
                    string expectedDirectory = moved ? Path.Combine(destination, Path.GetFileName(directories[index])) : directories[index];
                    string expectedPath = Path.Combine(expectedDirectory, "chart.bms");
                    CollectionAssert.AreEqual(System.Text.Encoding.UTF8.GetBytes("chart bytes " + index), File.ReadAllBytes(expectedPath));
                    Assert.IsNotNull(read.Find<LR2SongDB.song>(expectedPath));
                    Assert.AreEqual(expectedPath, library.BmsCharts.Single(current => ReferenceEquals(current.Token, charts[index].Token)).Path);
                    CollectionAssert.AreEqual(new[] { expectedDirectory }, lookup.GetDistinctDirectoriesByPrimaryHash(charts[index].Md5).ToArray());
                    CollectionAssert.AreEqual(new[] { directories[index] }, initialLookup.GetDistinctDirectoriesByPrimaryHash(charts[index].Md5).ToArray());
                    if (moved)
                    {
                        Assert.IsFalse(Directory.Exists(directories[index]));
                        Assert.IsFalse(File.Exists(charts[index].Path));
                        Assert.IsNull(read.Find<LR2SongDB.song>(charts[index].Path));
                    }
                }
            }

            static ChartOperationTarget Target(ChartFile chart) => new(chart, null, ChartOperationSourceScope.Library,
                true, false, false, ChartOperationCapabilities.MoveInLibrary);

            static void AssertSuccessfulReceipt(SelectedChartMutationResult result)
            {
                Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                Assert.IsNull(result.Failure);
                Assert.IsNotNull(result.MutationReceipt);
                Assert.IsTrue(result.MutationReceipt.DurableCommit);
                Assert.AreEqual(1, result.MutationReceipt.ConfirmedChangeCount);
                Assert.AreEqual(1, result.MutationReceipt.CatalogChartPathChangeCount);
            }
        });
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

    /// <summary>文字コードは保存後に専用更新を通知し、一般変更の停止・活動・抑制経路へ誤って入ることを検出します。</summary>
    [TestMethod]
    public void ApplyEncoding_StoresBeforeRefreshWithoutGeneralMutationLifecycle()
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

        SelectedChartMutationResult result = owner.ApplyEncoding(new SelectedChartEncodingRequest([target], string.Empty));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, store.EncodingCallCount);
        Assert.AreEqual(string.Empty, store.Encoding);
        Assert.AreSame(target.Chart.Token, store.EncodingFiles[0].Token);
        CollectionAssert.AreEqual(new[] { "store-encoding", "encoding-refresh" }, events);
    }

    /// <summary>文字コード修正権限がない場合は保存も通知も行わず、不適格な対象の変更を検出します。</summary>
    [TestMethod]
    public void ApplyEncoding_WithoutTargetsDoesNotStoreOrRefresh()
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

        SelectedChartMutationResult result = owner.ApplyEncoding(new SelectedChartEncodingRequest([target], "utf-8"));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(0, store.EncodingCallCount);
        Assert.AreEqual(0, presentation.EncodingRefreshCalls);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    /// <summary>文字コード保存の例外は失敗を返して更新を通知せず、未保存の値を画面へ公開する誤りを検出します。</summary>
    [TestMethod]
    public void ApplyEncoding_FailureDoesNotRefresh()
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

        SelectedChartMutationResult result = owner.ApplyEncoding(new SelectedChartEncodingRequest([target], "utf-8"));

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

    /// <summary>実storeと実DBを通し、最初の確認前の固定対象が削除・通常拡張子変更へ届くことを確認します。</summary>
    [DataTestMethod]
    // 0: 初期hashと現hashが違う削除、2: 確認取消、3: 現path変更後の拡張子修正。
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task PreparedTargets_RealStorePreservesFirstConfirmationBoundary(int scenario)
    {

        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Pack");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "chart.bms");
            File.WriteAllText(path, "#PLAYER 1");
            File.WriteAllText(Path.Combine(folder, "keep.wav"), "keep folder");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [CreateFile(new string('a', 32), path)], BmsonCharts = [] };
            ChartFile selected = library.BmsCharts.Single();
            CatalogOwnedCollectionOwner collection = GetOwnedCollectionOwner(library);
            void ChangeCurrent(ChartFile value)
            {
                using (collection.WriteGate.GetWriterGuard())
                {
                    lock (collection.Gate) { ApplyCapturedCurrentValues(collection.Collection, value); }
                }
            }
            if (scenario == 0) { ChangeCurrent(selected with { Md5 = new string('b', 32) }); }
            if (scenario == 3)
            {
                string moved = Path.Combine(folder, "moved.bms");
                File.Move(path, moved);
                ChangeCurrent(selected with { Path = moved });
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(library.BmsCharts);
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var activity = new ChartMutationActivityOwner();
            var presentation = new RecordingPresentation();
            var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = new FakeUiDialogService
            {
                ConfirmationResults = new([UiDialogResult.FromMessageBoxResult(MessageBoxResult.No)]),
                FirstConfirmation = scenario < 3 ? confirmation : null,
                ConfirmationReached = reached
            };
            if (scenario >= 3)
            {
                dialogs.ConfirmationResults = new([UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)]);
                dialogs.OnConfirmation = () =>
                {
                    Assert.IsTrue(gate.IsActive);

                };
            }
            var owner = new SelectedChartMutationWorkflowOwner(() => library, gate, activity,
                presentation, dialogs, dialogs, new BmsLibrarySelectedChartMutationStore());
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            activity.ActivityChanged += presentation.OnActivityChanged;
            var target = new ChartOperationTarget(selected, null, ChartOperationSourceScope.Library,
                isOwned: true, isPending: false, isPlaylistMissing: false,
                scenario < 3 ? ChartOperationCapabilities.RemoveFromLibrary : ChartOperationCapabilities.RenameInvalidExtension);
            Task<SelectedChartMutationResult>? operation = null;
            try
            {
                if (scenario < 3)
                {
                    operation = owner.DeleteAsync(new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));
                    Task arrived = await Task.WhenAny(reached.Task, operation);
                    Assert.AreSame(reached.Task, arrived, "確認到達前にTaskが終端しました。");
                    await reached.Task;
                    Assert.IsTrue(gate.IsActive);
                    Assert.IsFalse(gate.TryEnter(out IDisposable competingLease));
                    competingLease?.Dispose();
                    confirmation.SetResult(UiDialogResult.FromMessageBoxResult(scenario == 2 ? MessageBoxResult.Cancel : MessageBoxResult.OK));
                }
                else
                {
                    operation = owner.RenameInvalidExtensionsAsync(new SelectedInvalidExtensionRenameRequest([target], false));
                    Task arrived = await Task.WhenAny(reached.Task, operation);
                    Assert.AreSame(reached.Task, arrived, "確認到達前にTaskが終端しました。");
                    await reached.Task;
                }
                SelectedChartMutationResult result = await operation;
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.IsTrue(gate.TryEnter(out IDisposable probe));
                probe.Dispose();
                Assert.AreEqual(new string('a', 32), selected.Md5);
                Assert.AreEqual(path, selected.Path);
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
                if (scenario == 0)
                {
                    Assert.IsTrue(result.Succeeded);
                    Assert.AreEqual(1, result.RemovalOutcome.ConfirmedChartCount);
                    Assert.AreEqual(1, filesystem.FileDeleteCalls);
                    Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
                    Assert.IsFalse(File.Exists(path));
                }
                else if (scenario == 3)
                {
                    Assert.IsTrue(result.Succeeded);
                    Assert.AreEqual(1, result.MutationReceipt.ConfirmedChangeCount);
                    Assert.AreEqual(1, filesystem.FileMoveCalls);
                    Assert.IsTrue(File.Exists(Path.Combine(folder, "moved.bmx")));
                    Assert.IsFalse(File.Exists(path));
                    Assert.AreEqual(0, readback.Table<LR2SongDB.song>().Count());
                }
                else
                {
                    Assert.AreEqual(0, filesystem.FileMoveCalls);
                    Assert.AreEqual(0, filesystem.FileDeleteCalls);
                    Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
                    Assert.IsTrue(File.Exists(path));
                    LR2SongDB.song row = readback.Table<LR2SongDB.song>().Single();
                    Assert.AreEqual(path, row.path);
                    Assert.AreEqual(selected.Md5, row.hash);
                    Assert.AreEqual(1, library.BmsCharts.Count);
                    Assert.IsTrue(result.Succeeded);
                    Assert.IsNull(result.RemovalOutcome);
                    Assert.IsNull(result.MutationReceipt);
                    Assert.AreEqual(0, presentation.Events.Count);
                }
            }
            finally
            {
                confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
                try { if (operation != null) { await operation; } }
                finally
                {
                    owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                    activity.ActivityChanged -= presentation.OnActivityChanged;
                    library.RequestShutdown("prepared-selected-test");
                }
            }
        });
    }

    /// <summary>準備を保持している間もDispatcherは選択変更を処理でき、成功・失敗とも初期対象と受付終端を保つことからUI停止や後の選択混入を検出します。</summary>
    /// <remarks>実Dispatcherから受付し、準備portだけを保持して同じUIキューの応答を観測します。DB保存の接続はRealStoreの代表ケースが担います。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeleteAsync_PrepareKeepsDispatcherResponsiveAndRetainsCopiedInput(bool prepareFails)
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            var scheduler = new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs(), scheduler,
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false });
            var presentation = new RecordingPresentation();
            var store = new HeldPrepareStore(presentation.Events);
            var gate = new ChartFileOperationSynchronizer();
            var activity = new ChartMutationActivityOwner();
            FakeUiDialogService dialogs = AcceptedMessageDialogs();
            int confirmationCount = 0;
            dialogs.OnConfirmation = () => confirmationCount++;
            var owner = new SelectedChartMutationWorkflowOwner(() => library, gate, activity,
                presentation, dialogs, dialogs, store);
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            activity.ActivityChanged += presentation.OnActivityChanged;
            ChartOperationTarget first = CreateTarget("first.bms", ChartOperationSourceScope.Library,
                false, ChartOperationCapabilities.RemoveFromLibrary);
            ChartOperationTarget later = CreateTarget("later.bms", ChartOperationSourceScope.Library,
                false, ChartOperationCapabilities.RemoveFromLibrary);
            var selection = new List<ChartOperationTarget> { first };
            var request = new SelectedChartDeleteRequest(selection, first, MainViewOperationSection.Library);
            var failure = new IOException("prepare failed");
            SelectedChartMutationResult? result = null;
            Task? operation = null;
            try
            {
                // workflow自体を背景へ移さず、実Dispatcherから呼び出します。
                operation = scheduler.InvokeAsync(async () => result = await owner.DeleteAsync(request));
                Task arrived = await Task.WhenAny(store.Reached.Task, operation);
                if (arrived == operation) { await operation; }
                Assert.AreSame(store.Reached.Task, arrived, "準備到達前に操作が終了しました。");
                await store.Reached.Task;
                await scheduler.InvokeAsync(() =>
                {
                    selection.Clear();
                    selection.Add(later);
                    Assert.IsTrue(gate.IsActive);
                    Assert.IsFalse(gate.TryEnter(out IDisposable competing));
                    competing?.Dispose();
                    Assert.AreEqual(0, confirmationCount);
                    Assert.AreEqual(0, store.LibraryDeleteCalls);
                    Assert.IsFalse(activity.IsActive);
                    Assert.AreEqual(0, presentation.Events.Count);
                    Assert.AreEqual(first.Chart.Path, store.PreparedInput.Single().Path);
                });
                if (prepareFails) { store.Release.TrySetException(failure); }
                else { store.Release.TrySetResult(true); }
                await operation;
                Assert.IsNotNull(result);
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.IsTrue(gate.TryEnter(out IDisposable probe));
                probe.Dispose();
                if (prepareFails)
                {
                    Assert.AreSame(failure, result.Failure);
                    Assert.AreEqual(0, confirmationCount);
                    Assert.AreEqual(0, store.LibraryDeleteCalls);
                    Assert.AreEqual(0, presentation.Events.Count);
                }
                else
                {
                    Assert.IsTrue(result.Succeeded);
                    Assert.AreEqual(1, confirmationCount);
                    Assert.AreEqual(1, store.LibraryDeleteCalls);
                    Assert.AreEqual(first.Chart.Path, store.LibraryCharts.Single().Path);
                }
            }
            finally
            {
                store.Release.TrySetResult(true);
                try { if (operation != null) { await operation; } }
                finally
                {
                    owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                    activity.ActivityChanged -= presentation.OnActivityChanged;
                    library.RequestShutdown("selected-ui-prepare-test");
                }
            }
        });
    }

    /// <summary>実ownerから本番adapter・モデル・DBへ同じLを渡し、文字コードと修正印の保存、Busy、公開終端を確認します。</summary>
    [TestMethod]
    public async Task ApplyEncoding_RealStorePersistsEncodingAndFixedMarkUnderSameAdmission()
    {
        await WithTemporarySongDbAsync(songDbPath =>
        {
            string path = Path.Combine(Path.GetDirectoryName(songDbPath)!, "encoding.bms");
            File.WriteAllText(path, "#TITLE encoding");
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [CreateFile(new string('c', 32), path)], BmsonCharts = [] };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(library.BmsCharts);
            ChartFile chart = library.BmsCharts.Single();
            var target = new ChartOperationTarget(chart, null, ChartOperationSourceScope.Library,
                true, false, false, ChartOperationCapabilities.RunBmsEncodingFix);
            var owner = new SelectedChartMutationWorkflowOwner(() => library, library.OperationAdmission,
                new ChartMutationActivityOwner(), new RecordingPresentation(), AcceptedMessageDialogs(),
                AcceptedMessageDialogs(), new BmsLibrarySelectedChartMutationStore());
            int published = 0;
            owner.WorkflowChanged += (_, eventArgs) =>
            {
                if (eventArgs is not SelectedChartMutationAppliedEventArgs) { return; }
                ObservePublication();
            };
            void ObservePublication()
            {
                Assert.IsTrue(library.OperationAdmission.IsActive, "必須公開の実終端までLを保持します。");
                published++;
            }
            try
            {
                Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable busy));
                using (busy)
                {
                    Assert.IsFalse(owner.ApplyEncoding(new SelectedChartEncodingRequest([target], "utf-8")).Succeeded);
                    using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                    Assert.IsFalse(read.Table<LR2SongDBExtended.maintenance>().Any(row => row.path == path));
                }
                Assert.IsTrue(owner.ApplyEncoding(new SelectedChartEncodingRequest([target], "utf-8")).Succeeded);
                using (LR2SongDBExtended read = gateway.OpenSongDbReadOnly())
                {
                    LR2SongDBExtended.maintenance row = read.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == path);
                    Assert.AreEqual("utf-8", row.encoding);
                    Assert.IsTrue(row.is_encoding_fixed);
                }
                Assert.AreEqual(1, published);
                Assert.IsFalse(library.OperationAdmission.IsActive);
            }
            finally { library.RequestShutdown("encoding-entry-test"); }
            return Task.CompletedTask;
        });
    }

    /// <summary>保留だけ・所持混在で明示再復号と修正印確定を実DBまで確認します。同一path/hash行の非文字コード列を維持し、未登録・別hashでは未判定行を保存して、推定先健康度の永続化・song新設・元配置結果と所属の変更を検出します。</summary>
    [TestMethod]
    [DataRow(true, false, "existing")]
    [DataRow(false, false, "absent")]
    [DataRow(true, true, "different_hash")]
    [DataRow(false, true, "existing")]
    [DataRow(true, false, "absent")]
    [DataRow(false, false, "different_hash")]
    public async Task ApplyEncoding_RealStoreUpdatesSelectedPendingEntriesWithoutChangingPlacement(bool decode, bool includeOwned, string storedState)
    {
        await WithTemporarySongDbAsync(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Pending");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            const string title = "日本語の譜面";
            string pendingPath = Path.Combine(source, "pending.bms");
            File.WriteAllText(pendingPath, "#TITLE " + title + "\n#ARTIST 作曲者\n#WAV01 tone.wav\n#00111:01\n", System.Text.Encoding.GetEncoding("shift_jis"));
            ChartFile parsed = PackageChartEntry.FromPath(pendingPath).Chart;
            ResourceHealthMaintenanceSnapshot initial = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(parsed) with
            { Encoding = decode ? "utf-8" : "shift_jis", EncodingFixed = false };
            ChartFile pending = ChartFileProjection.WithMaintenance(parsed, initial);
            if (decode) { pending = pending with { Title = "before", RawTitle = "before" }; }
            var entry = PackageChartEntry.FromChart(pending);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            ResourceHealthMaintenanceSnapshot original = entry.GetOriginalResourceHealth();
            Assert.AreEqual(0, original.GetWavHealth());
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = pendingPath;
            entry.ApplyCurrentChart(entry.Chart with { InstallDestinationSuggestions = [destination] });
            entry.ApplyInstallDestinationMetadata(destination, "配置先", "配置先作者");
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(destination, ["tone.wav"]);
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], package, cache);
            ResourceHealthMaintenanceSnapshot currentHealth = entry.Chart.ResourceHealthMaintenanceSnapshot with
            { Encoding = decode ? "utf-8" : "shift_jis", EncodingFixed = false };
            entry.ApplyCurrentChart(ChartFileProjection.WithMaintenance(entry.Chart, currentHealth));
            entry.ReplaceResourceHealthProjection(currentHealth, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(currentHealth), entry.ResourcePlacement);
            ChartFile before = entry.Chart;
            Assert.AreEqual(100, before.WAVHealth);
            ChartResourcePlacement placement = entry.ResourcePlacement;
            string ownedPath = Path.Combine(root, "owned.bms");
            File.Copy(pendingPath, ownedPath);
            ChartFile owned = ChartFileProjection.WithMaintenance(parsed with { Path = ownedPath }, initial with { Path = ownedPath });
            if (decode) { owned = owned with { Title = "before", RawTitle = "before" }; }
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(),
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = includeOwned ? [owned] : [], BmsonCharts = [], ChartPackagesPending = [package] };
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                db.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
            });
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs(library.BmsCharts);
            ResourceHealthMaintenanceSnapshot stored = initial with
            {
                Hash = storedState == "different_hash" ? new string('f', 32) : initial.Hash.ToUpperInvariant(),
                WavFilesDefined = 3,
                WavFilesExisting = 1,
                BgaFilesDefined = 2,
                BgaFilesExisting = 0,
                MovieFilesDefined = 5,
                MovieFilesExisting = 4,
                StagefileDefined = true,
                StagefileExisting = false,
                BannerDefined = true,
                BannerExisting = true,
                BackbmpDefined = false,
                BackbmpExisting = null,
                FilesWarningIgnored = true,
                Lr2WarningFlags = (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong,
                Lr2ResourceMaxRelativeCp932Bytes = 300,
                Lr2ResourceHasParentTraversal = true
            };
            if (storedState != "absent") { gateway.UpsertMaintenanceInfos([stored]); }
            var targets = new List<ChartOperationTarget>
            {
                new(before, null, ChartOperationSourceScope.PendingPackage, false, true, false,
                    ChartOperationCapabilities.RunBmsEncodingFix, entry)
            };
            if (includeOwned)
            {
                targets.Add(new(library.BmsCharts.Single(), null, ChartOperationSourceScope.Library, true, false, false,
                    ChartOperationCapabilities.RunBmsEncodingFix));
            }
            var owner = new SelectedChartMutationWorkflowOwner(() => library, library.OperationAdmission,
                new ChartMutationActivityOwner(), new RecordingPresentation(), AcceptedMessageDialogs(),
                AcceptedMessageDialogs(), new BmsLibrarySelectedChartMutationStore());
            try
            {
                SelectedChartMutationResult result = owner.ApplyEncoding(new SelectedChartEncodingRequest(targets, decode ? "shift_jis" : string.Empty));
                Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                ChartFile current = entry.Chart;
                Assert.AreEqual(title, current.RawTitle);
                Assert.AreEqual("shift_jis", current.EncodingName);
                Assert.IsTrue(current.ResourceHealthMaintenanceSnapshot.EncodingFixed);
                Assert.AreSame(original, entry.GetOriginalResourceHealth());
                Assert.AreSame(placement, entry.ResourcePlacement);
                Assert.AreEqual(before.WAVHealth, current.WAVHealth);
                CollectionAssert.AreEqual(before.Warnings.ToArray(), current.Warnings.ToArray());
                Assert.AreEqual(before.InstallDestination, current.InstallDestination);
                CollectionAssert.AreEqual(before.InstallDestinationSuggestions.ToArray(), current.InstallDestinationSuggestions.ToArray());
                Assert.AreSame(package, library.ChartPackagesPending.Single());
                Assert.AreSame(entry, package.ChartEntries.Single());
                Assert.IsNull(current.Token);
                Assert.AreEqual(0, library.ChartPackagesInstalled.Count);
                Assert.AreEqual(includeOwned ? 1 : 0, library.BmsCharts.Count);
                using LR2SongDBExtended read = gateway.OpenSongDbReadOnly();
                LR2SongDBExtended.maintenance saved = read.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == pendingPath);
                Assert.AreEqual("shift_jis", saved.encoding);
                Assert.IsTrue(saved.is_encoding_fixed);
                ResourceHealthMaintenanceSnapshot expectedStored = storedState == "existing"
                    ? stored with { Encoding = "shift_jis", EncodingFixed = true }
                    : new ResourceHealthMaintenanceSnapshot
                    {
                        Path = pendingPath,
                        Hash = parsed.Md5,
                        Encoding = "shift_jis",
                        EncodingFixed = true,
                        Origin = MaintenanceInfoOrigin.Calculated
                    };
                Assert.AreEqual(expectedStored, MaintenanceStorageMapping.ToCommon(saved),
                    "同一内容の既存行は非文字コード列を保持し、未登録・別内容は健康度と警告を未判定にします。");
                Assert.AreEqual(includeOwned ? 1 : 0, read.Table<LR2SongDB.song>().Count());
                Assert.AreEqual(pendingPath, read.Table<LR2SongDBExtended.install>().Single().path);
                if (includeOwned)
                {
                    Assert.AreEqual(title, library.BmsCharts.Single().RawTitle);
                    Assert.IsTrue(library.BmsCharts.Single().ResourceHealthMaintenanceSnapshot.EncodingFixed);
                    Assert.IsTrue(read.Table<LR2SongDBExtended.maintenance>().Single(row => row.path == ownedPath).is_encoding_fixed);
                }
            }
            finally { library.RequestShutdown("pending-encoding-test"); }
            return Task.CompletedTask;
        });
    }

    private static SelectedChartMutationWorkflowOwner CreateOwner(
        RecordingPresentation presentation,
        FakeUiDialogService dialogs,
        RecordingStore store,
        ChartFileOperationSynchronizer? gate = null,
        ChartMutationActivityOwner? activity = null)
    {
        activity ??= new();
        var owner = new SelectedChartMutationWorkflowOwner(
            () => (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary)),
            gate ?? new ChartFileOperationSynchronizer(),
            activity,
            presentation,
            dialogs,
            dialogs,
            store);
        owner.WorkflowChanged += presentation.OnWorkflowChanged;
        activity.ActivityChanged += presentation.OnActivityChanged;
        return owner;
    }

    private static FakeUiDialogService AcceptedMessageDialogs()
    {
        return new FakeUiDialogService
        {
            ConfirmationResults = new Queue<UiDialogResult>([
                UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            ])
        };
    }

    private static ChartOperationTarget CreateTarget(
        string fileName,
        ChartOperationSourceScope sourceScope,
        bool isPending,
        ChartOperationCapabilities capabilities)
    {
        return new ChartOperationTarget(
            CreateChart(Path.Combine(@"C:\Songs", fileName)),
            null,
            sourceScope,
            isOwned: true,
            isPending,
            isPlaylistMissing: false,
            capabilities);
    }

    private static ChartFile CreateChart(string path)
    {
        return new ChartFile(ChartFileKind.Bms, path, "hash-" + Path.GetFileNameWithoutExtension(path), null, "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    private sealed class RecordingPresentation : IChartMutationPlaybackPort
    {
        internal List<string> Events { get; }

        internal RecordingPresentation(List<string>? events = null)
        {
            Events = events ?? [];
        }

        internal int PathRefreshCalls { get; private set; }

        internal int EncodingRefreshCalls { get; private set; }

        /// <summary>本番store接続試験で停止の実終端だけを保留し、準備済み対象とFSの条件差を観測します。</summary>
        internal Func<Task>? BeforeStop { get; set; }

        internal void OnActivityChanged(object? sender, EventArgs e)
        {
            var activity = (ChartMutationActivityOwner)sender!;
            Events.Add(activity.IsActive ? "activity-start" : "activity-end");
        }

        internal void OnWorkflowChanged(
            object? sender,
            SelectedChartMutationWorkflowChangedEventArgs e)
        {
            switch (e)
            {
                case SelectedChartMutationRefreshSuppressionChangedEventArgs suppressionChanged:
                    if (suppressionChanged.IsSuppressed)
                    {
                        Events.Add(
                            suppressionChanged.Scope == SelectedChartMutationRefreshScope.Pending
                                ? "pending-refresh-start"
                                : "library-refresh-start");
                    }
                    else
                    {
                        Events.Add(
                            Events.Contains("pending-refresh-start") && !Events.Contains("library-refresh-end")
                                ? "pending-refresh-end"
                                : "library-refresh-end");
                    }
                    break;
                case SelectedChartMutationAppliedEventArgs mutationApplied:
                    if (mutationApplied.LibraryPathChanged)
                    {
                        PathRefreshCalls++;
                        Events.Add("path-refresh");
                    }
                    if (mutationApplied.EncodingChanged)
                    {
                        EncodingRefreshCalls++;
                        Events.Add("encoding-refresh");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(e),
                        e,
                        "Unsupported selected chart mutation workflow change.");
            }
        }

        public Task StopPlaybackForMutationAsync()
        {
            Events.Add("playback-stop");
            return BeforeStop?.Invoke() ?? Task.CompletedTask;
        }
    }

    /// <summary>不変入力の準備だけを保留し、Dispatcherの応答と実行順序を検査します。</summary>
    private sealed class HeldPrepareStore(List<string> events) : RecordingStore(events)
    {
        internal TaskCompletionSource<bool> Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<LibraryChartRef> PreparedInput { get; private set; } = [];

        public override LibraryChartRemovalPreflight PrepareLibraryChartRemoval(BMSLibrary library, IReadOnlyList<LibraryChartRef> charts)
        {
            PreparedInput = charts.ToArray();
            LibraryChartRemovalPreflight prepared = base.PrepareLibraryChartRemoval(library, PreparedInput);
            Reached.TrySetResult(true);
            // 同期store契約の背景処理だけを止め、試験側から確定または例外を渡します。
            Release.Task.GetAwaiter().GetResult();
            return prepared;
        }
    }

    private class RecordingStore : ISelectedChartMutationStore
    {
        private readonly List<string>? events;

        internal RecordingStore(List<string>? events = null)
        {
            this.events = events;
        }

        internal IReadOnlyList<string> WholeFolderDeletePaths { get; set; } = [];

        internal IReadOnlyList<string> ApprovedWholeFolderDeletePaths { get; private set; } = [];

        internal IReadOnlyList<LibraryChartRef> LibraryCharts { get; private set; } = [];

        internal int LibraryDeleteCalls { get; private set; }

        internal int PendingDeleteCalls { get; private set; }

        internal bool DeleteContainingPackageFoldersWhenNoBms { get; private set; }

        internal List<string> RenameOperations { get; } = [];

        internal int RenameCallCount { get; private set; }

        internal string MovedDirectory { get; private set; } = string.Empty;

        internal int EncodingCallCount { get; private set; }

        internal Exception Failure { get; set; } = null!;

        public virtual LibraryChartRemovalPreflight PrepareLibraryChartRemoval(BMSLibrary library, IReadOnlyList<LibraryChartRef> charts)
            => new(charts.Select(chart => chart.ToChartFileIdentity()),
                WholeFolderDeletePaths, [], charts.Count, charts.Count(chart => chart.Token == null));

        public LibraryFileExtensionRenameBatch PrepareLibraryFileExtensionRenameBatch(
            BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension)
            => new(charts, newExtension);

        internal LibraryChartRemovalOutcome RemovalOutcome { get; set; } = null!;

        public LibraryChartRemovalOutcome RemoveLibraryCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderDeletePaths)
        {
            IReadOnlyList<LibraryChartRef> charts = prepared.Targets.Select(chart => LibraryChartRef.FromChartFile(chart)).ToArray();
            ThrowIfConfigured();
            LibraryDeleteCalls++;
            events?.Add("store-library-delete");
            LibraryCharts = charts;
            ApprovedWholeFolderDeletePaths = approvedWholeFolderDeletePaths;
            return RemovalOutcome ?? new LibraryChartRemovalOutcome(charts.Select(chart => new LibraryChartRemovalTarget(chart.Path, LibraryChartRemovalState.Confirmed)), true, true);
        }

        public void RemovePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, bool sendToRecycleBin, bool deleteContainingPackageFoldersWhenNoBms)
        {
            ThrowIfConfigured();
            PendingDeleteCalls++;
            events?.Add("store-pending-delete");
            DeleteContainingPackageFoldersWhenNoBms = deleteContainingPackageFoldersWhenNoBms;
        }

        public virtual LibraryMutationSessionReceipt RenameLibraryChartsWithReceipt(
            BMSLibrary library,
            IReadOnlyList<LibraryFileExtensionRenameBatch> batches)
        {
            ThrowIfConfigured();
            events?.Add("store-library-rename");
            RenameOperations.AddRange(batches.Select(batch => "library:" + batch.NewExtension));
            RenameCallCount++;
            return LibraryMutationSessionReceipt.Empty;
        }

        public void RenamePendingCharts(BMSLibrary library, IReadOnlyList<ChartFile> charts, string newExtension)
        {
            ThrowIfConfigured();
            events?.Add("store-pending-rename");
            RenameOperations.Add("pending:" + newExtension);
            RenameCallCount++;
        }

        public virtual LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(
            BMSLibrary library, ChartLibraryMoveRequest request)
        {
            ThrowIfConfigured();
            events?.Add("store-move");
            MovedDirectory = request.NewParentDirectory;
            return new LibraryMutationSessionReceipt(
                [new LibraryMutationSessionTarget(request.Charts[0].Path, request.NewParentDirectory)],
                durableCommit: true,
                catalogChartPathChangeCount: 1,
                folderReferenceMoveCount: 1);
        }

        public void SetBMSFilesEncoding(
            BMSLibrary library,
            IReadOnlyList<ChartFile> bmsFiles,
            string encoding, IReadOnlyList<PackageChartEntry> pendingEntries)
        {
            EncodingCallCount++;
            ThrowIfConfigured();
            events?.Add("store-encoding");
            EncodingFiles = bmsFiles;
            Encoding = encoding;
        }

        internal IReadOnlyList<ChartFile> EncodingFiles { get; private set; } = [];

        internal string Encoding { get; private set; } = string.Empty;

        private void ThrowIfConfigured()
        {
            if (Failure != null)
            {
                throw Failure;
            }
        }
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
            IReadOnlyList<LibraryFileExtensionRenameBatch> batches)
        {
            RenameTerminalCalls++;
            RenameBatches = batches;
            return receipt;
        }

        public override LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(BMSLibrary library, ChartLibraryMoveRequest request)
        {
            Calls++;
            return receipt;
        }
    }

    private sealed class FakeUiDialogService : IUiDialogService, IPendingDeleteConfirmationDialogPort
    {
        internal Queue<UiDialogResult> ConfirmationResults { get; set; } = new();

        internal UiInteractionResult<bool> PendingDeleteResult { get; set; } = null!;

        internal List<UiMessageRequest> Messages { get; } = [];
        internal Action? OnMessage { get; set; }
        internal Action? OnConfirmation { get; set; }
        internal TaskCompletionSource<UiDialogResult>? FirstConfirmation { get; set; }
        internal TaskCompletionSource<bool>? ConfirmationReached { get; set; }
        private int confirmationCount;

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            Messages.Add(request);
            OnMessage?.Invoke();
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            confirmationCount++;
            if (confirmationCount == 1)
            {
                OnConfirmation?.Invoke();
                ConfirmationReached?.TrySetResult(true);
                if (FirstConfirmation != null) { return FirstConfirmation.Task; }
            }
            if (ConfirmationResults.Count == 0)
            {
                throw new InvalidOperationException("No confirmation result configured.");
            }
            return Task.FromResult(ConfirmationResults.Dequeue());
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(UiWindowDialogRequest<TWindow, TResult> request, CancellationToken cancellationToken = default)
            where TWindow : Window
        {
            throw new NotSupportedException();
        }

        public Task<UiInteractionResult<bool>> ShowAsync()
        {
            return Task.FromResult(PendingDeleteResult);
        }

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(UiFolderPickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(UiProgressRequest request, Func<UiProgressContext, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
