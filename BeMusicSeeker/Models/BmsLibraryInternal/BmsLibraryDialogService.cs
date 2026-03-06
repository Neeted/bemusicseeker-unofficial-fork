using System.Windows;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryDialogService : IBmsLibraryDialogService
{
    public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        return DispatcherMessageBox.Show(messageBoxText, caption, button, icon, defaultResult);
    }
}
