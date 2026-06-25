using System;
using System.Windows;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Models.Utils;

internal static class DispatcherMessageBox
{
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.Cancel, MessageBoxOptions options = MessageBoxOptions.None)
    {
        return Show(null, messageBoxText, caption, button, icon, defaultResult, options);
    }

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.Cancel, MessageBoxOptions options = MessageBoxOptions.None)
    {
        UiDialogResult result = CreateCoordinator()
            .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, button, icon, defaultResult, options, owner))
            .GetAwaiter()
            .GetResult();
        return ToLegacyMessageBoxResult(result, button, defaultResult);
    }

    private static UiDialogCoordinator CreateCoordinator()
    {
        return new UiDialogCoordinator();
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
