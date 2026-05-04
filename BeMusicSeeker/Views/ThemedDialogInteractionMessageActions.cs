using System.Windows;
using System.Windows.Controls;
using Livet.Behaviors.Messaging;
using Livet.Messaging;

namespace BeMusicSeeker.Views;

internal sealed class ThemedInformationDialogInteractionMessageAction : InteractionMessageAction<FrameworkElement>
{
    protected override void InvokeAction(InteractionMessage message)
    {
        if (message is InformationMessage informationMessage)
        {
            ThemedMessageBox.Show(
                Window.GetWindow(AssociatedObject),
                informationMessage.Text,
                informationMessage.Caption,
                MessageBoxButton.OK,
                informationMessage.Image,
                MessageBoxResult.OK);
        }
    }
}

internal sealed class ThemedConfirmationDialogInteractionMessageAction : InteractionMessageAction<FrameworkElement>
{
    protected override void InvokeAction(InteractionMessage message)
    {
        if (message is ConfirmationMessage confirmationMessage)
        {
            MessageBoxResult result = ThemedMessageBox.Show(
                Window.GetWindow(AssociatedObject),
                confirmationMessage.Text,
                confirmationMessage.Caption,
                confirmationMessage.Button,
                confirmationMessage.Image,
                MessageBoxResult.None);
            confirmationMessage.Response = ThemedMessageBox.ToConfirmationResponse(result);
        }
    }
}
