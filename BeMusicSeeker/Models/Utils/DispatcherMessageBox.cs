using System.Windows;
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
        return UiDialogLegacyAdapter.ShowMessageBox(owner, messageBoxText, caption, button, icon, defaultResult, options);
    }
}
