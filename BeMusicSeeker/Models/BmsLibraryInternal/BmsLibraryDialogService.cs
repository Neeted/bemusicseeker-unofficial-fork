using System;
using System.Windows;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDialogService : IBmsLibraryDialogService
{
    public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        UiDialogResult result = button == MessageBoxButton.OK
            ? new UiDialogCoordinator()
                .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, button, icon, defaultResult))
                .GetAwaiter()
                .GetResult()
            : new UiDialogCoordinator()
                .ConfirmAsync(new UiConfirmationRequest(messageBoxText, caption, button, icon, defaultResult))
                .GetAwaiter()
                .GetResult();
        return ToMessageBoxResult(result, caption);
    }

    private static MessageBoxResult ToMessageBoxResult(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException((routeName ?? "BMS library dialog") + " failed: no result");
        }
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return result.MessageBoxResult;
        }
        if (result.Status == UiDialogStatus.Failed)
        {
            throw new InvalidOperationException((routeName ?? "BMS library dialog") + " failed.", result.Exception);
        }
        throw new InvalidOperationException((routeName ?? "BMS library dialog") + " was not shown: " + result.Status);
    }
}
