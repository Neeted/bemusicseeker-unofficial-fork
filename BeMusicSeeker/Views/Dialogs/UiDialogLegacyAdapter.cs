using System;
using System.Windows;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// legacy call site の同期 MessageBox API を coordinator route に接続します。
/// 置換途中だけの境界であり、新規 dialog route の実装先にはしません。
/// </summary>
internal static class UiDialogLegacyAdapter
{
    /// <summary>
    /// legacy MessageBox signature の呼び出しを coordinator route で表示します。
    /// </summary>
    /// <param name="owner">呼び出し側が把握している owner window。</param>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <param name="button">表示するボタン。</param>
    /// <param name="icon">表示する icon。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <param name="options">WPF message box option。</param>
    /// <returns>legacy MessageBoxResult。</returns>
    internal static MessageBoxResult ShowMessageBox(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.Cancel,
        MessageBoxOptions options = MessageBoxOptions.None)
    {
        UiDialogResult result = new UiDialogCoordinator()
            .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, button, icon, defaultResult, options, owner))
            .GetAwaiter()
            .GetResult();
        return ToLegacyMessageBoxResult(result, button, defaultResult);
    }

    private static MessageBoxResult ToLegacyMessageBoxResult(UiDialogResult result, MessageBoxButton button, MessageBoxResult defaultResult)
    {
        if (result == null)
        {
            throw new InvalidOperationException("UI dialog route returned no result.");
        }

        return result.Status switch
        {
            UiDialogStatus.Accepted or UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => result.MessageBoxResult,
            UiDialogStatus.ClosedByUser => ThemedMessageBox.NormalizeDefaultResult(button, defaultResult),
            UiDialogStatus.AppClosing => throw new InvalidOperationException("UI dialog was not shown because the application is closing."),
            UiDialogStatus.DispatcherUnavailable => throw new InvalidOperationException("UI dialog dispatcher is unavailable."),
            UiDialogStatus.OwnerUnavailable => throw new InvalidOperationException("UI dialog owner is unavailable."),
            UiDialogStatus.NotShown => throw new InvalidOperationException("UI dialog was not shown."),
            UiDialogStatus.Failed => throw new InvalidOperationException("UI dialog failed.", result.Exception),
            _ => throw new InvalidOperationException("UI dialog returned an unknown result: " + result.Status),
        };
    }
}
