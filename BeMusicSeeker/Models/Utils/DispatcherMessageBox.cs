using System;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Models.Utils;

internal static class DispatcherMessageBox
{
    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.Cancel, MessageBoxOptions options = MessageBoxOptions.None)
    {
        return Show(null, messageBoxText, caption, button, icon, defaultResult, options);
    }

    public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.Cancel, MessageBoxOptions options = MessageBoxOptions.None)
    {
        try
        {
            if (Application.Current == null)
            {
                return MessageBox.Show(messageBoxText, caption, button, icon, defaultResult, options);
            }
            if (Application.Current.Dispatcher.CheckAccess())
            {
                return ThemedMessageBox.Show(ResolveOwner(owner), messageBoxText, caption, button, icon, defaultResult, options);
            }
            MessageBoxResult result = MessageBoxResult.None;
            Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, (Action)delegate
            {
                result = ThemedMessageBox.Show(ResolveOwner(owner), messageBoxText, caption, button, icon, defaultResult, options);
            });
            return result;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            return MessageBox.Show(messageBoxText, caption, button, icon, defaultResult, options);
        }
    }

    private static Window ResolveOwner(Window owner)
    {
        return owner ?? Application.Current?.MainWindow;
    }
}
