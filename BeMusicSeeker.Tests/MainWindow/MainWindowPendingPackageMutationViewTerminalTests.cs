using System;
using System.IO;
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

    /// <summary>物理変更前の失敗は表示反映と移動を省いて元例外を伝えることを確認し、失敗を空結果として隠したり未変更の表示を更新する誤りを検出します。</summary>
    [TestMethod]
    public async Task ShouldApplyViewFalseSkipsViewAndNavigationButPropagatesFailure()
    {
        var mutationFailure = new InvalidOperationException("mutation failed");
        int applyCount = 0;
        int navigationCount = 0;
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => true,
            () => 0,
            _ =>
            {
                navigationCount++;
                return Task.FromResult(true);
            });

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => terminal.ApplyAsync(
                PendingPackageMutationResult.FailedBeforeMutation(mutationFailure),
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                nameof(ShouldApplyViewFalseSkipsViewAndNavigationButPropagatesFailure),
                () => applyCount++));

        Assert.AreSame(mutationFailure, exception);
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

    /// <summary>物理変更後に表示反映も失敗した場合は物理・表示の順で例外をまとめ移動しないことを確認し、元の物理失敗の上書きや未反映画面からの移動を検出します。</summary>
    [TestMethod]
    public async Task ApplyFailureSuppressesNavigationAndOrdersMutationBeforeApply()
    {
        var mutationFailure = new InvalidOperationException("mutation failed");
        var applyFailure = new InvalidOperationException("apply failed");
        int navigationCount = 0;
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => true,
            () => 0,
            _ =>
            {
                navigationCount++;
                return Task.FromResult(true);
            });

        AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => terminal.ApplyAsync(
                PendingPackageMutationResult.FailedAfterMutation(
                    mutationFailure,
                    PackageCatalogSection.Pending),
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                nameof(ApplyFailureSuppressesNavigationAndOrdersMutationBeforeApply),
                () => ThrowFailure(applyFailure)));

        Assert.AreEqual(2, exception.InnerExceptions.Count);
        Assert.AreSame(mutationFailure, exception.InnerExceptions[0]);
        Assert.AreSame(applyFailure, exception.InnerExceptions[1]);
        Assert.AreEqual(0, navigationCount);
    }

    /// <summary>物理変更後の失敗に続いて画面移動も失敗した場合は物理・移動の順で元例外を保持することを確認し、後段の失敗で前段を隠す誤りを検出します。</summary>
    [TestMethod]
    public async Task NavigationFailureOrdersMutationBeforeNavigation()
    {
        var mutationFailure = new InvalidOperationException("mutation failed");
        var navigationFailure = new InvalidOperationException("navigation failed");
        var terminal = new MainWindowPendingPackageMutationViewTerminal(
            () => true,
            () => 0,
            _ => Task.FromException<bool>(navigationFailure));

        AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => terminal.ApplyAsync(
                PendingPackageMutationResult.FailedAfterMutation(
                    mutationFailure,
                    PackageCatalogSection.Pending),
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                nameof(NavigationFailureOrdersMutationBeforeNavigation)));

        Assert.AreEqual(2, exception.InnerExceptions.Count);
        Assert.AreSame(mutationFailure, exception.InnerExceptions[0]);
        Assert.AreSame(navigationFailure, exception.InnerExceptions[1]);
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

    /// <summary>commit済みreceiptと別の画面終端失敗が共存しても元例外を伝えて一回エラー通知することを確認し、receiptがあるだけで無関係な失敗を成功扱いする誤りを検出します。</summary>
    [TestMethod]
    public async Task ReceiptDoesNotHideUnrelatedLifecycleFailure()
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
        Exception observed = await Assert.ThrowsExceptionAsync<IOException>(() => terminal.ApplyAsync(
            result, PackageCatalogSection.Pending, MainViewUpdateMode.PendingInstallFolderSelected,
            nameof(ReceiptDoesNotHideUnrelatedLifecycleFailure)));
        Assert.AreSame(failure, observed);
        Assert.IsTrue(result.HasDurableCommit);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(System.Windows.MessageBoxImage.Error, dialogs.Messages[0].Icon);
    }

    private static void ThrowFailure(Exception failure)
        => throw failure;
}
