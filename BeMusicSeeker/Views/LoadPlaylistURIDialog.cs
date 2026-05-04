using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.Win32;

namespace BeMusicSeeker.Views;

public partial class LoadPlaylistURIDialog : UserControl, IComponentConnector
{
	public LoadPlaylistURIDialog()
	{
		InitializeComponent();
	}

	private void CancelAndClose(object sender, RoutedEventArgs e)
	{
		if (base.DataContext is MainWindowViewModel)
		{
			settingDialog.Visibility = Visibility.Hidden;
			textBoxURIInput.Text = string.Empty;
		}
	}

	private async void SaveAndClose(object sender, RoutedEventArgs e)
	{
		MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
		if (viewModel != null)
		{
			Uri targetURI;
			try
			{
				targetURI = new Uri(textBoxURIInput.Text, UriKind.Absolute);
			}
			catch
			{
				DispatcherMessageBox.Show(Window.GetWindow(this), "入力された値が正しくありません。" + Environment.NewLine + "絶対URIであることを確認してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
				return;
			}
			textBoxURIInput.Text = string.Empty;
			settingDialog.Visibility = Visibility.Hidden;
            await viewModel.RegistrateExternalPlaylistBMSTableAsync(targetURI).Logging("SaveAndClose");
        }
    }

	private void OpenLocalFile(object sender, RoutedEventArgs e)
	{
		OpenFileDialog openFileDialog = new OpenFileDialog();
		openFileDialog.Title = "ヘッダーファイルを開く";
		string filter = (openFileDialog.Filter = "Jsonファイル(*.json)|*.json");
		openFileDialog.Filter = filter;
		if (openFileDialog.ShowDialog() == true)
		{
			textBoxURIInput.Text = openFileDialog.FileName;
		}
	}
}
