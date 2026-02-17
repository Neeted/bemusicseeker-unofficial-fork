using System;
using System.Windows;
using System.Windows.Threading;

namespace BeMusicSeeker.Models.Utils;

internal static class DispatcherMessageBox
{
	public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.Cancel, MessageBoxOptions options = MessageBoxOptions.None)
	{
		try
		{
			if (Application.Current.Dispatcher.CheckAccess())
			{
				if (Application.Current.MainWindow == null)
				{
					throw new InvalidOperationException("MainWindowが表示されていません。");
				}
				return MessageBox.Show(Application.Current.MainWindow, messageBoxText, caption, button, icon, defaultResult, options);
			}
			MessageBoxResult result = MessageBoxResult.None;
			Application.Current.Dispatcher.Invoke(DispatcherPriority.Background, (Action)delegate
			{
				if (Application.Current.MainWindow == null)
				{
					result = MessageBox.Show(messageBoxText, caption, button, icon, defaultResult, options);
				}
				else
				{
					result = MessageBox.Show(Application.Current.MainWindow, messageBoxText, caption, button, icon, defaultResult, options);
				}
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
}
