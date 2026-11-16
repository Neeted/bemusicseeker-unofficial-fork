using System.Diagnostics;
using BeMusicSeeker.TestSupport.Processes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using static BeMusicSeeker.Desktop.E2E.Tests.Fixtures.DesktopScenarioUi;

namespace BeMusicSeeker.Desktop.E2E.Tests.Update;

/// <summary>起動時の更新失敗の通知を、所有modalの存在・OKボタンのAutomationId・本文のText要素だけで観測・操作します。</summary>
/// <remarks>見出し・前置き・言語では特定しません。予期しない所有modalは閉じず、失敗として報告します。</remarks>
internal static class UpdateFailureNotificationUi
{
    private const string OkId = "ThemedMessageBoxOK";
    // 内容が分からない単独のmodalを、他の通知として失敗にするまでの猶予です。表示の構築中を誤検出しないための上限で、不在の判定には使いません。
    private static readonly TimeSpan UnrecognizedModalGrace = TimeSpan.FromSeconds(3);

    /// <summary>所有MainWindowに、OKボタンを持つ所有modalがちょうど1つ現れるのを期限内に待ちます。</summary>
    /// <param name="shouldAbort">真を返したら待機を失敗として打ち切る条件です。起動の失敗など、待っても出現しない場合に使います。</param>
    internal static Window WaitForNotification(Window mainWindow, ProcessDeadline deadline, Action<string> step, Func<bool>? shouldAbort = null)
    {
        step("before failure notification wait");
        Stopwatch? unrecognized = null;
        Window result = Retry.WhileNull(() =>
        {
            if (shouldAbort?.Invoke() == true) { throw new InvalidOperationException("失敗通知の出現前に、待機を打ち切る条件が成立しました。"); }
            Window[] modals = mainWindow.ModalWindows;
            if (modals.Length == 0) { return null; }
            if (modals.Length > 1) { throw new InvalidOperationException("所有modalが複数あります: " + modals.Length + " 件"); }
            Window modal = modals[0];
            if (modal.AutomationId == UpdateDialogUi.DialogId) { throw new InvalidOperationException("失敗通知ではなく更新ダイアログが現れています。"); }
            if (modal.FindAllDescendants(cf => cf.ByAutomationId(OkId)).Length == 1) { return modal; }
            unrecognized ??= Stopwatch.StartNew();
            if (unrecognized.Elapsed > UnrecognizedModalGrace) { throw new InvalidOperationException("OKボタンを持たない所有modalがあります: AutomationId=" + modal.AutomationId); }
            return null;
        }, timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "所有MainWindowの失敗通知が現れませんでした。").Result
            ?? throw new InvalidOperationException("失敗通知がありません。");
        step("after failure notification wait");
        return result;
    }

    /// <summary>
    /// 通知の本文を読みます。本文は、modal内のScrollViewerの子孫にあるText要素とし、ちょうど1個であることを要求します。
    /// ボタン内のText要素は含めません。
    /// </summary>
    internal static string ReadMessage(Window notification)
    {
        AutomationElement[] scrolls = notification.FindAllDescendants(cf => cf.ByClassName("ScrollViewer"));
        Assert.AreEqual(1, scrolls.Length, "通知内のScrollViewerが一意に見つかりません。");
        AutomationElement[] texts = scrolls[0].FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
        Assert.AreEqual(1, texts.Length, "通知本文のText要素が一意に見つかりません: " + string.Join(" | ", texts.Select(text => text.Name)));
        return texts[0].Name;
    }

    /// <summary>OKボタンをInvokeします。入力の前に期限を確認します。</summary>
    internal static void Acknowledge(Window notification, ProcessDeadline deadline, Action<string> step)
    {
        AutomationElement ok = FindUnique(notification, OkId, deadline, step);
        EnsureInputDeadline(deadline);
        step("before Invoke " + OkId);
        ok.Patterns.Invoke.Pattern.Invoke();
        step("after Invoke " + OkId);
    }

    /// <summary>所有modalが0件になり、MainWindowが有効に戻るのを期限内に待ちます。</summary>
    internal static void WaitForDismissed(Window mainWindow, ProcessDeadline deadline, Action<string> step)
    {
        step("before notification dismissed wait");
        _ = Retry.WhileFalse(() => mainWindow.ModalWindows.Length == 0 && mainWindow.IsEnabled,
            timeout: deadline.Remaining, throwOnTimeout: true, ignoreException: false,
            timeoutMessage: "失敗通知が閉じてMainWindowが有効に戻りませんでした。");
        step("after notification dismissed wait");
    }
}
