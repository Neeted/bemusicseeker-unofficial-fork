using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using BeMusicSeeker.ViewModels;
using Livet;
using Ribbit.Util;

namespace BeMusicSeeker.Views;

public partial class PlaylistPropertyDialog : UserControl, IComponentConnector
{
	public PlaylistPropertyDialog()
	{
		InitializeComponent();
	}

	private void CancelAndClose(object sender, RoutedEventArgs e)
	{
		if (base.DataContext is MainWindowViewModel { playlistPropertyDialog: { } playlistPropertyDialogViewModel })
		{
			if (playlistPropertyDialogViewModel.ResetProperties())
			{
				playlistPropertyDialogViewModel.Dispose();
				playlistPropertyDialog.Visibility = Visibility.Hidden;
			}
			else
			{
				MessageBox.Show(Window.GetWindow(this), "プレイリスト名・URI・出力先フォルダ名を確認して下さい。", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
		}
	}

    private async void SaveAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { playlistPropertyDialog: { } playlistPropertyDialogViewModel })
        {
            if (playlistPropertyDialogViewModel.SaveProperties())
            {
                playlistPropertyDialogViewModel.Dispose();
                playlistPropertyDialog.Visibility = Visibility.Hidden;
                await playlistPropertyDialogViewModel.ApplyPostSaveUpdatesAsync();
            }
            else
            {
                MessageBox.Show(Window.GetWindow(this), "プレイリスト名・URI・出力先フォルダ名を確認して下さい。", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand);
            }
		}
	}

	private void folderListUp(object sender, RoutedEventArgs e)
	{
		MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
		IList selectedItems = foldersListBox.SelectedItems;
		if (mainWindowViewModel == null || selectedItems == null || selectedItems.Count == 0)
		{
			return;
		}
		DispatcherCollection<string> folder_order = mainWindowViewModel.playlistPropertyDialog.folder_order;
		List<int> list = (from string f in selectedItems
			select folder_order.IndexOf(f) into i
			where i != -1
			orderby i
			select i).ToList();
		for (int num = 0; num < list.Count(); num++)
		{
			if (list[num] > num)
			{
				folder_order.Move(list[num], list[num] - 1);
			}
		}
		foldersListBox.ScrollIntoView(folder_order[Math.Max(list[0] - 1, 0)]);
	}

	private void folderListDown(object sender, RoutedEventArgs e)
	{
		MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
		IList selectedItems = foldersListBox.SelectedItems;
		if (mainWindowViewModel == null || selectedItems == null || selectedItems.Count == 0)
		{
			return;
		}
		DispatcherCollection<string> folder_order = mainWindowViewModel.playlistPropertyDialog.folder_order;
		List<int> list = (from string f in selectedItems
			select folder_order.IndexOf(f) into i
			where i != -1
			orderby i
			select i).ToList();
		list.Reverse();
		for (int num = 0; num < list.Count(); num++)
		{
			if (list[num] < folder_order.Count() - 1 - num)
			{
				folder_order.Move(list[num], list[num] + 1);
			}
		}
		foldersListBox.ScrollIntoView(folder_order[Math.Min(list[0] + 1, folder_order.Count() - 1)]);
	}

	private void folderNaturalSort(object sender, RoutedEventArgs e)
	{
		if (!(sender is CheckBox { IsChecked: var isChecked }) || isChecked != true || !(base.DataContext is MainWindowViewModel mainWindowViewModel))
		{
			return;
		}
		DispatcherCollection<string> folder_order = mainWindowViewModel.playlistPropertyDialog.folder_order;
		using NaturalComparer<string> comparer = new NaturalComparer<string>();
		List<string> list = folder_order.ToList();
		list.Sort(comparer);
		mainWindowViewModel.playlistPropertyDialog.folder_order = new DispatcherCollection<string>(new ObservableCollection<string>(list), DispatcherHelper.UIDispatcher);
	}
}
