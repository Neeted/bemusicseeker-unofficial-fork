using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
// 実WindowがApplication.Resourcesとactive modal ownerを共有し、言語資源のCultureも復元まで所有するため並列化しません。
[DoNotParallelize]
public sealed class PendingPackageRestoreWindowTests
{
    /// <summary>実Windowの三つのボタンがそれぞれの明示値を返し、×・Escは承認も終了選択も返しません。警告・原因・ボタンのBinding接続も確認します。</summary>
    /// <remarks>実coordinator・同じscopeの親子モーダルを表示し、実ボタンClickとXAMLで接続したEscイベントを操作します。OS入力・フォーカスは保証対象ではありません。</remarks>
    [DataTestMethod]
    [DataRow("Retry")]
    [DataRow("Reset")]
    [DataRow("Exit")]
    [DataRow("Close")]
    [DataRow("Escape")]
    public void RecoveryWindow_OnlyExplicitButtonsReturnAcceptedChoices(string operationKind)
        => TestUiDispatcherHost.RunWindowTest(scope => CheckWindow(scope, operationKind));

    /// <summary>6言語の実Windowで各資源が実Content・本文へ届き、既定言語や固定ラベルの残存を検出します。辞書・getter整合は既存ParityTestsへ分担します。</summary>
    [DataTestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    [DataRow("ja-JP")]
    [DataRow("ko-KR")]
    [DataRow("zh-CN")]
    [DataRow("zh-TW")]
    public void RecoveryWindow_CurrentLanguageReachesButtonsAndWarning(string culture)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo previous = Resources.Culture;
            try { Resources.Culture = CultureInfo.GetCultureInfo(culture); CheckWindow(scope, "Close"); }
            finally { Resources.Culture = previous; }
        });
    }

    private static void CheckWindow(TestWindowPresentationScope scope, string operationKind)
    {
        var owner = new Window { Width = 240, Height = 90, Content = new Border() };
        scope.ShowAndWaitForContentRendered(owner);
        var coordinator = new UiDialogCoordinator(new UiDialogOwnerResolver(), window =>
        {
            scope.PrepareForOwnedPresentation(window);
            return UiDialogOwnerResolver.PushActiveModal(window);
        });
        var operated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingPackageRestoreWindow? window = null;
        var request = new UiWindowDialogRequest<PendingPackageRestoreWindow, PendingPackageRestoreChoice>(() =>
        {
            var created = new PendingPackageRestoreWindow("read failure cause");
            window = created;
            created.ContentRendered += (_, _) => created.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    Assert.AreSame(owner, created.Owner);
                    Assert.AreEqual(Resources.PendingRestoreTitle, created.Title);
                    Assert.AreEqual(Resources.PendingRestoreMessage, ((TextBlock)created.FindName("FailureMessage")).Text);
                    Assert.AreEqual(Resources.PendingRestoreResetWarning, ((TextBlock)created.FindName("ResetWarning")).Text);
                    Assert.AreEqual("read failure cause", ((TextBlock)created.FindName("FailureCause")).Text);
                    Assert.AreEqual(Resources.PendingRestoreRetry, ((Button)created.FindName("RetryButton")).Content);
                    Assert.AreEqual(Resources.PendingRestoreReset, ((Button)created.FindName("ResetButton")).Content);
                    Assert.AreEqual(Resources.PendingRestoreExit, ((Button)created.FindName("ExitButton")).Content);
                    if (operationKind == "Close") { created.Close(); }
                    else if (operationKind == "Escape")
                    {
                        PresentationSource source = PresentationSource.FromVisual(created) ?? throw new InvalidOperationException("PresentationSourceがありません。");
                        created.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    }
                    else { ((Button)created.FindName(operationKind + "Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
                    operated.TrySetResult();
                }
                catch (Exception failure) { operated.TrySetException(failure); if (created.IsVisible) { created.Close(); } }
            }));
            return created;
        }, created => created.Choice, owner);
        Task<UiWindowDialogResult<PendingPackageRestoreChoice>> operation = owner.Dispatcher.InvokeAsync(() => coordinator.ShowWindowAsync(request)).Task.Unwrap();
        Exception? primary = null;
        try
        {
            TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(operated.Task, operation, "pending-recovery-window-operation"), "pending-window-operate");
            TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "pending-window-result");
            UiWindowDialogResult<PendingPackageRestoreChoice> result = operation.GetAwaiter().GetResult();
            bool explicitChoice = operationKind is "Retry" or "Reset" or "Exit";
            Assert.AreEqual(explicitChoice, result.IsAccepted);
            Assert.AreEqual(explicitChoice ? Enum.Parse<PendingPackageRestoreChoice>(operationKind) : PendingPackageRestoreChoice.None, result.Value);
            Assert.IsFalse(window?.IsVisible ?? true);
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            try { if (window?.IsVisible == true) { window.Close(); } TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "pending-window-cleanup"); }
            catch (Exception) when (primary != null) { }
        }
    }
}
