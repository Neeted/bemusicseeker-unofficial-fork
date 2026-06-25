using System.Windows;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDialogService : IBmsLibraryDialogService
{
    public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        return UiDialogLegacyAdapter.ShowMessageBox(null, messageBoxText, caption, button, icon, defaultResult);
    }
}
