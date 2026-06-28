using System;
using System.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 同期 event handler から coordinator dialog route を使うための薄い入口です。
/// 表示失敗を既定ボタンに丸めず、呼び出し側へ例外として返します。
/// </summary>
internal static class UiDialogRoute
{
    /// <summary>
    /// coordinator-backed message box を owner 自動解決で同期表示します。同期 event handler から同じ route を使うための互換入口です。
    /// </summary>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <param name="button">表示するボタン。</param>
    /// <param name="icon">表示する icon。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <param name="options">WPF message box option。</param>
    /// <param name="warningMessageBoxText">本文とは別に警告色で表示する補助本文。</param>
    /// <returns>legacy message box 互換の結果。</returns>
    internal static MessageBoxResult ShowMessageBox(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None,
        string warningMessageBoxText = null)
    {
        return ShowMessageBox(null, messageBoxText, caption, button, icon, defaultResult, options, warningMessageBoxText);
    }

    /// <summary>
    /// coordinator-backed message box を指定 owner で同期表示します。表示不能をユーザーのキャンセルとして扱わないために使います。
    /// </summary>
    /// <param name="owner">呼び出し側が既に把握している owner window。</param>
    /// <param name="messageBoxText">表示する本文。</param>
    /// <param name="caption">dialog title。</param>
    /// <param name="button">表示するボタン。</param>
    /// <param name="icon">表示する icon。</param>
    /// <param name="defaultResult">既定の結果。</param>
    /// <param name="options">WPF message box option。</param>
    /// <param name="warningMessageBoxText">本文とは別に警告色で表示する補助本文。</param>
    /// <returns>legacy message box 互換の結果。</returns>
    internal static MessageBoxResult ShowMessageBox(
        Window owner,
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None,
        string warningMessageBoxText = null)
    {
        UiDialogResult result = button == MessageBoxButton.OK
            ? new UiDialogCoordinator()
                .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, button, icon, defaultResult, options, owner, warningMessageBoxText))
                .GetAwaiter()
                .GetResult()
            : new UiDialogCoordinator()
                .ConfirmAsync(new UiConfirmationRequest(messageBoxText, caption, button, icon, defaultResult, options, owner, warningMessageBoxText))
                .GetAwaiter()
                .GetResult();
        ThrowIfNotShown(result, caption);
        return result.MessageBoxResult;
    }

    /// <summary>
    /// dialog が表示されなかった状態を例外として扱います。確認失敗をキャンセルとして隠さないための境界です。
    /// </summary>
    /// <param name="result">coordinator から返された dialog 結果。</param>
    /// <param name="routeName">例外 message に含める route 名。</param>
    /// <exception cref="InvalidOperationException">dialog が表示されなかった、または表示中に失敗した場合。</exception>
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
