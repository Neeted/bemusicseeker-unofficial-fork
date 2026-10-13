using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowPendingPackageMutationViewTerminalTests
{
    /// <summary>変更結果がない場合は選択と件数を読まず表示移動もしないことを確認し、未実行操作の後に別の画面へ進む誤りを検出します。</summary>
    [TestMethod]
    public async Task NullResultIsNoOp()
    {
        int selectionReads = 0;
        int itemCountReads = 0;
        int navigationCount = 0;
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () =>
            {
                selectionReads++;
                return true;
            },
            () =>
            {
                itemCountReads++;
                return 0;
            },
            _ =>
            {
                navigationCount++;
                return Task.FromResult(true);
            });

        await terminal.ApplyAsync(
            null!,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(NullResultIsNoOp));

        Assert.AreEqual(0, selectionReads);
        Assert.AreEqual(0, itemCountReads);
        Assert.AreEqual(0, navigationCount);
    }

    /// <summary>物理変更前の失敗は表示反映と移動を省いて一回通知し、通知だけのために再送出したり未変更の表示を更新する誤りを検出します。</summary>
    [TestMethod]
    public async Task ShouldApplyViewFalseSkipsViewAndNavigationAndReportsFailureOnce()
    {
        var mutationFailure = new InvalidOperationException("mutation failed");
        var dialogs = new FileDbReportRecordingDialogs();
        int applyCount = 0;
        int navigationCount = 0;
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => true,
            () => 0,
            _ =>
            {
                navigationCount++;
                return Task.FromResult(true);
            }, dialogs);

        await terminal.ApplyAsync(
                PendingPackageMutationResult.FailedBeforeMutation(mutationFailure),
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                nameof(ShouldApplyViewFalseSkipsViewAndNavigationAndReportsFailureOnce),
                () => applyCount++);

        Assert.AreEqual(1, dialogs.Messages.Count);
        StringAssert.Contains(dialogs.Messages[0].MessageBoxText, mutationFailure.Message);
        Assert.AreEqual(0, applyCount);
        Assert.AreEqual(0, navigationCount);
    }

    /// <summary>反映後だけ保留ルートを選んでも移動せず、反映前から選択して空になった場合だけ一回移動することを確認し、選択の時点を取り違える誤りを検出します。</summary>
    [TestMethod]
    public async Task NavigationRequiresInitialSelectionAndOccursOncePerAppliedMutation()
    {
        bool selected = false;
        int itemCount = 1;
        int applyCount = 0;
        int navigationCount = 0;
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => selected,
            () => itemCount,
            _ =>
            {
                navigationCount++;
                return Task.FromResult(true);
            });
        var result = PendingPackageMutationResult.CompletedFor(PackageCatalogSection.Pending);

        await terminal.ApplyAsync(
            result,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(NavigationRequiresInitialSelectionAndOccursOncePerAppliedMutation),
            () =>
            {
                applyCount++;
                selected = true;
                itemCount = 0;
            });

        Assert.AreEqual(1, applyCount);
        Assert.AreEqual(0, navigationCount, "A root selected only after apply must not trigger navigation.");

        selected = true;
        itemCount = 1;
        await terminal.ApplyAsync(
            result,
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(NavigationRequiresInitialSelectionAndOccursOncePerAppliedMutation),
            () => itemCount = 0);

        Assert.AreEqual(1, navigationCount);
    }

    /// <summary>対象section違い・未選択・残件あり・反映中の選択解除の各条件では移動しないことを確認し、画面状態と無関係に空ルートへ移動する誤りを検出します。</summary>
    [TestMethod]
    public async Task NavigationRequiresMatchingEmptySectionAndRemainingSelection()
    {
        bool selected = true;
        int itemCount = 0;
        int navigationCount = 0;
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => selected,
            () => itemCount,
            _ =>
            {
                navigationCount++;
                return Task.FromResult(true);
            });

        await terminal.ApplyAsync(
            PendingPackageMutationResult.CompletedFor(PackageCatalogSection.Installed),
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(NavigationRequiresMatchingEmptySectionAndRemainingSelection));
        Assert.AreEqual(0, navigationCount);

        selected = false;
        await terminal.ApplyAsync(
            PendingPackageMutationResult.CompletedFor(PackageCatalogSection.Pending),
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(NavigationRequiresMatchingEmptySectionAndRemainingSelection));
        Assert.AreEqual(0, navigationCount);

        selected = true;
        itemCount = 1;
        await terminal.ApplyAsync(
            PendingPackageMutationResult.CompletedFor(PackageCatalogSection.Pending),
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(NavigationRequiresMatchingEmptySectionAndRemainingSelection));
        Assert.AreEqual(0, navigationCount);

        itemCount = 0;
        await terminal.ApplyAsync(
            PendingPackageMutationResult.CompletedFor(PackageCatalogSection.Pending),
            PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(NavigationRequiresMatchingEmptySectionAndRemainingSelection),
            () => selected = false);
        Assert.AreEqual(0, navigationCount);
    }

    /// <summary>物理変更後に表示反映も失敗した場合は未報告の変更失敗と実表示故障を同じ終端へ伝えて移動を止めることを確認します。</summary>
    [TestMethod]
    public async Task ApplyFailureSuppressesNavigationAndPropagatesRealViewFailure()
    {
        var mutationFailure = new InvalidOperationException("mutation failed");
        var applyFailure = new InvalidOperationException("apply failed");
        var dialogs = new FileDbReportRecordingDialogs();
        int navigationCount = 0;
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => true,
            () => 0,
            _ =>
            {
                navigationCount++;
                return Task.FromResult(true);
            }, dialogs);

        AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => terminal.ApplyAsync(
                PendingPackageMutationResult.FailedAfterMutation(
                    mutationFailure,
                    PackageCatalogSection.Pending),
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                nameof(ApplyFailureSuppressesNavigationAndPropagatesRealViewFailure),
                () => ThrowFailure(applyFailure)));

        CollectionAssert.AreEqual(new Exception[] { mutationFailure, applyFailure }, exception.InnerExceptions.ToArray());
        Assert.AreEqual(0, dialogs.Messages.Count, "上位のowning境界が両原因を一度通知します。");
        Assert.AreEqual(0, navigationCount);
    }

    /// <summary>未報告の変更失敗と実navigation故障を同じ終端へ伝え、二重通知する誤りを検出します。</summary>
    [TestMethod]
    public async Task NavigationFailureRemainsSeparateFromHandledMutationFailure()
    {
        var mutationFailure = new InvalidOperationException("mutation failed");
        var dialogs = new FileDbReportRecordingDialogs();
        var navigationFailure = new InvalidOperationException("navigation failed");
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => true,
            () => 0,
            _ => Task.FromException<bool>(navigationFailure), dialogs);

        AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => terminal.ApplyAsync(
                PendingPackageMutationResult.FailedAfterMutation(
                    mutationFailure,
                    PackageCatalogSection.Pending),
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                nameof(NavigationFailureRemainsSeparateFromHandledMutationFailure)));

        CollectionAssert.AreEqual(new Exception[] { mutationFailure, navigationFailure }, exception.InnerExceptions.ToArray());
        Assert.AreEqual(0, dialogs.Messages.Count, "上位のowning境界が両原因を一度通知します。");
    }

    /// <summary>表示反映だけが失敗した場合は同じ例外と発生元のstackを伝えることを確認し、集約や再throwで失敗位置を失う誤りを検出します。</summary>
    [TestMethod]
    public async Task SingleFailurePreservesOriginalStack()
    {
        var applyFailure = new InvalidOperationException("apply failed");
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => false,
            () => 1,
            _ => Task.FromResult(true));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => terminal.ApplyAsync(
                PendingPackageMutationResult.Completed,
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                nameof(SingleFailurePreservesOriginalStack),
                () => ThrowFailure(applyFailure)));

        Assert.AreSame(applyFailure, exception);
        StringAssert.Contains(exception.StackTrace, nameof(ThrowFailure));
    }

    /// <summary>後片付け失敗と終端失敗の有無・通知先例外の各条件でも表示反映とreceipt通知を一回行いcommitを保持することを確認し、画面移動不要時の通知漏れや通知失敗によるcommit取消扱いを検出します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ReceiptReportIsOnceAndDoesNotDependOnNavigation(bool finalizationFailed, bool reporterThrows)
    {
        var cleanup = new IOException("pending-cleanup-marker");
        IOException? primary = finalizationFailed ? new IOException("pending-finalizer-marker") : null;
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\pending-source", @"D:\installed")],
            durableCommit: true,
            finalizationFailure: primary,
            cleanupFailure: cleanup,
            recoveryCandidatePaths: [@"C:\pending-candidate"]);
        var result = PendingPackageMutationResult.FromTerminal(sessionReceipt);
        var dialogs = new FileDbReportRecordingDialogs
        {
            MessageFailure = reporterThrows ? new IOException("optional reporter failed") : null
        };
        int applied = 0;
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => false, () => 1, _ => throw new AssertFailedException("navigation not requested"), dialogs);

        await terminal.ApplyAsync(result, PackageCatalogSection.Pending,
            MainViewUpdateMode.PendingInstallFolderSelected, nameof(ReceiptReportIsOnceAndDoesNotDependOnNavigation),
            () => applied++);

        Assert.AreEqual(1, applied);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(finalizationFailed ? System.Windows.MessageBoxImage.Error : System.Windows.MessageBoxImage.Warning, dialogs.Messages[0].Icon);
        StringAssert.Contains(dialogs.Messages[0].MessageBoxText, cleanup.Message);
        if (primary != null)
        {
            StringAssert.Contains(dialogs.Messages[0].MessageBoxText, primary.Message);
        }

        Assert.AreSame(sessionReceipt, result.SessionReceipt);
        Assert.IsTrue(result.HasDurableCommit);
    }

    /// <summary>commit済みreceiptと別のworkflow失敗を一回のエラー通知へ含め、通知後に表示目的で再送出せず確定factsを保つことを確認します。</summary>
    [TestMethod]
    public async Task ReceiptReportIncludesUnrelatedFailureWithoutRethrow()
    {
        var sessionReceipt = new LibraryMutationSessionReceipt(
            [new LibraryMutationSessionTarget(@"C:\source", @"D:\destination")],
            durableCommit: true);
        var failure = new IOException("unrelated-lifecycle-marker");
        var dialogs = new FileDbReportRecordingDialogs();
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => false, () => 1, _ => Task.FromResult(true), dialogs);
        var result = PendingPackageMutationResult.FailedAfterMutation(
            failure,
            sessionReceipt: sessionReceipt);
        await terminal.ApplyAsync(
            result, PackageCatalogSection.Pending, MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(ReceiptReportIncludesUnrelatedFailureWithoutRethrow));
        Assert.AreSame(failure, result.Failure);
        StringAssert.Contains(dialogs.Messages[0].MessageBoxText, failure.Message);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Error, dialogs.Messages[0].Icon);
    }

    private static void ThrowFailure(Exception failure)
        => throw failure;
}
