using System;
using System.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 同期 event handler から coordinator dialog route を使うための薄い入口です。
/// 表示失敗を既定ボタンに丸めず、呼び出し側へ例外として返します。
/// </summary>
internal static class UiDialogRoute
{
    internal static MessageBoxResult ShowMessageBox(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None)
    {
        return ShowMessageBox(null, messageBoxText, caption, button, icon, defaultResult, options);
    }

    internal static MessageBoxResult ShowMessageBox(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None)
    {
        UiDialogResult result = button == MessageBoxButton.OK
            ? new UiDialogCoordinator()
                .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, button, icon, defaultResult, options, owner))
                .GetAwaiter()
                .GetResult()
            : new UiDialogCoordinator()
                .ConfirmAsync(new UiConfirmationRequest(messageBoxText, caption, button, icon, defaultResult, options, owner))
                .GetAwaiter()
                .GetResult();
        ThrowIfNotShown(result, caption);
        return result.MessageBoxResult;
    }

    internal static void ThrowIfNotShown(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " failed: no result");
        }
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Exception);
    }
}
