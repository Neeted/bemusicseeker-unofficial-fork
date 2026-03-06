using System.Windows;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IBmsLibraryDialogService
{
    MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None);
}
