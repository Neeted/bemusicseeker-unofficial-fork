using System;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.Views.Dialogs;
using Livet.Behaviors.Messaging;
using Livet.Messaging;

namespace BeMusicSeeker.Views;

internal sealed class ThemedInformationDialogInteractionMessageAction : InteractionMessageAction<FrameworkElement>
{
    protected override void InvokeAction(InteractionMessage message)
    {
        if (message is InformationMessage informationMessage)
        {
            UiDialogResult result = CreateCoordinator()
                .ShowMessageAsync(new UiMessageRequest(
                    informationMessage.Text,
                    informationMessage.Caption,
                    MessageBoxButton.OK,
                    informationMessage.Image,
                    MessageBoxResult.OK,
                    owner: Window.GetWindow(AssociatedObject)))
                .GetAwaiter()
                .GetResult();
            ThrowIfNotShown(result);
        }
    }

    private static UiDialogCoordinator CreateCoordinator()
    {
        return new UiDialogCoordinator();
    }

    private static void ThrowIfNotShown(UiDialogResult result)
    {
        if (result.Status is UiDialogStatus.AppClosing or UiDialogStatus.DispatcherUnavailable or UiDialogStatus.OwnerUnavailable or UiDialogStatus.NotShown)
        {
            throw new InvalidOperationException("Information dialog was not shown: " + result.Status);
        }

        if (result.Status == UiDialogStatus.Failed)
        {
            throw new InvalidOperationException("Information dialog failed.", result.Exception);
        }
    }
}

internal sealed class ThemedConfirmationDialogInteractionMessageAction : InteractionMessageAction<FrameworkElement>
{
    protected override void InvokeAction(InteractionMessage message)
    {
        if (message is ConfirmationMessage confirmationMessage)
        {
            UiDialogResult result = CreateCoordinator()
                .ConfirmAsync(new UiConfirmationRequest(
                    confirmationMessage.Text,
                    confirmationMessage.Caption,
                    confirmationMessage.Button,
                    confirmationMessage.Image,
                    MessageBoxResult.None,
                    owner: Window.GetWindow(AssociatedObject)))
                .GetAwaiter()
                .GetResult();
            confirmationMessage.Response = ToConfirmationResponse(result);
        }
    }

    private static UiDialogCoordinator CreateCoordinator()
    {
        return new UiDialogCoordinator();
    }

    private static bool? ToConfirmationResponse(UiDialogResult result)
    {
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected => false,
            UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser => ThemedMessageBox.ToConfirmationResponse(result.MessageBoxResult),
            UiDialogStatus.Failed => throw new InvalidOperationException("Confirmation dialog failed.", result.Exception),
            _ => throw new InvalidOperationException("Confirmation dialog was not shown: " + result.Status),
        };
    }
}
