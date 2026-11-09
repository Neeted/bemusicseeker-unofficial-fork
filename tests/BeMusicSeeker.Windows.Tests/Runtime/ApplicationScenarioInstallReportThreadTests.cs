using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>手動導入の報告をarmしている間の通知が、呼出しスレッドによって失敗を保持することを検証します。</summary>
/// <remarks>実 Application の Dispatcher スレッドと別スレッドの区別、および所有者の解決には共有ホストの表示範囲が必要です。照合の条件網羅は Core の ApplicationScenarioInstallReportTests、正当な報告の E2E は SC-05 が担当します。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class ApplicationScenarioInstallReportThreadTests
{
    private const string Destination = @"C:\scenario\missing";

    /// <summary>
    /// 条件：報告をarmし、UIスレッドから宣言どおりの報告を通知します。
    /// 期待結果：完了済みの Accepted が返り、報告の終了は例外になりません。
    /// 検出する誤り：UIスレッドの正当な報告まで拒否します。
    /// </summary>
    [TestMethod]
    public void ShowMessageAsync_DuringInstallReport_AnswersDeclaredReportOnUiThread()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var window = new Window { Width = 200, Height = 120, Content = new System.Windows.Controls.Grid() };
            scope.ShowAndWaitForContentRendered(window);
            var dialogs = new ApplicationScenarioDialogs();
            dialogs.ArmInstallFailureReport(1, window, Destination);
            bool completed = false;
            try
            {
                Task<UiDialogResult> answer = dialogs.ShowMessageAsync(Request(window));
                Assert.IsTrue(answer.IsCompleted);
                Assert.AreEqual(UiDialogStatus.Accepted, answer.Result.Status);

                // 報告の終了は本体が成功した後に行い、本体の失敗の表示を置き換えません。
                completed = true;
                dialogs.CompleteInstallFailureReport(1);
            }
            finally
            {
                // 本体が途中で失敗した場合の後片付けだけを行い、その失敗は本体の失敗を置き換えません。
                if (!completed)
                {
                    try { dialogs.CompleteInstallFailureReport(1); }
                    catch (InvalidOperationException) { }
                }
            }
        });
    }

    /// <summary>
    /// 条件：報告をarmし、プールスレッドから宣言と完全一致の報告を通知した後、UIスレッドからも正しい報告を通知します。
    /// 期待結果：プールスレッドの通知は同期例外にならず、すでに失敗した Task になります。失敗は保持され、後のUIスレッドの通知も失敗し、報告の終了は例外になります。
    /// 検出する誤り：別スレッドの通知を委譲・成功・保留にする、失敗を立てず例外 Task だけを返す、所有者解決の後で同期例外が残る、常に真のスレッド判定をします。
    /// </summary>
    [TestMethod]
    public void ShowMessageAsync_DuringInstallReport_FromNonUiThreadFailsAndRetainsFailure()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var window = new Window { Width = 200, Height = 120, Content = new System.Windows.Controls.Grid() };
            scope.ShowAndWaitForContentRendered(window);
            var dialogs = new ApplicationScenarioDialogs();
            dialogs.ArmInstallFailureReport(2, window, Destination);
            bool completed = false;
            try
            {
                bool dispatcherAccessible = true;
                Exception? synchronousFailure = null;
                Task<UiDialogResult>? offThread = null;
                UiMessageRequest request = Request(window);
                var worker = Task.Run(() =>
                {
                    dispatcherAccessible = TestUiDispatcherHost.Dispatcher.CheckAccess();
                    try { offThread = dialogs.ShowMessageAsync(request); }
                    catch (Exception failure) { synchronousFailure = failure; }
                });
                TestUiDispatcherHost.AwaitTaskOnDispatcher(worker, "off-thread install report");

                Assert.IsFalse(dispatcherAccessible);
                Assert.IsNull(synchronousFailure, synchronousFailure?.ToString());
                Assert.IsNotNull(offThread);
                Assert.IsTrue(offThread.IsFaulted);
                Assert.IsInstanceOfType<InvalidOperationException>(offThread.Exception!.InnerException);

                Task<UiDialogResult> later = dialogs.ShowMessageAsync(Request(window));
                Assert.IsTrue(later.IsFaulted);
                Assert.IsInstanceOfType<InvalidOperationException>(later.Exception!.InnerException);

                // 報告の終了の表明は本体が成功した後に行い、本体の失敗の表示を置き換えません。
                completed = true;
                Assert.ThrowsExactly<InvalidOperationException>(() => dialogs.CompleteInstallFailureReport(2));
            }
            finally
            {
                // 本体が途中で失敗した場合の後片付けだけを行い、その失敗は本体の失敗を置き換えません。
                if (!completed)
                {
                    try { dialogs.CompleteInstallFailureReport(2); }
                    catch (InvalidOperationException) { }
                }
            }
        });
    }

    private static UiMessageRequest Request(Window owner)
        => new(
            string.Format(Resources.FileDbMutationReport_Operation, Resources.Install) + Environment.NewLine
                + Resources.Error_InstallDestinationMissing + Environment.NewLine + Destination,
            Resources.FileDbMutationReport_Title,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK,
            owner: owner);
}
