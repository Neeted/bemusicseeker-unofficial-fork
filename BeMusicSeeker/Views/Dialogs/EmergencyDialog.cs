using System.Windows;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// 起動前、未処理例外、終了処理中など、通常の UiDialogCoordinator を安全に使えない経路だけが使う OS native dialog 境界です。
/// </summary>
internal static class EmergencyDialog
{
    internal static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None,
        MessageBoxOptions options = MessageBoxOptions.None)
    {
        return MessageBox.Show(messageBoxText, caption, button, icon, defaultResult, options);
    }
}
