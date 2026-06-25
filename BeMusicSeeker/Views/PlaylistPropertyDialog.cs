using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Ribbit.Util;

namespace BeMusicSeeker.Views;

public partial class PlaylistPropertyDialog : UserControl, IComponentConnector
{
    public PlaylistPropertyDialog()
    {
        InitializeComponent();
    }

    private MainWindow GetDialogHost()
    {
        return Window.GetWindow(this) as MainWindow
            ?? throw new InvalidOperationException("Playlist property dialog is not hosted by MainWindow.");
    }

    private void CancelAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { playlistPropertyDialog: { } playlistPropertyDialogViewModel })
        {
            if (playlistPropertyDialogViewModel.ResetProperties())
            {
                playlistPropertyDialogViewModel.Dispose();
                GetDialogHost().HideOverlayDialog(playlistPropertyDialog);
            }
            else
            {
                UiDialogRoute.ShowMessageBox(Window.GetWindow(this), "プレイリスト名・URI・出力先フォルダ名を確認して下さい。", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand);
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
                GetDialogHost().HideOverlayDialog(playlistPropertyDialog);
                await playlistPropertyDialogViewModel.ApplyPostSaveUpdatesAsync();
            }
            else
            {
                UiDialogRoute.ShowMessageBox(Window.GetWindow(this), "プレイリスト名・URI・出力先フォルダ名を確認して下さい。", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand);
            }
        }
    }

    private void folderListUp(object sender, RoutedEventArgs e)
    {
        IList selectedItems = foldersListBox.SelectedItems;
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel || selectedItems == null || selectedItems.Count == 0)
        {
            return;
        }
        DispatcherCollection<string> folder_order = mainWindowViewModel.playlistPropertyDialog.folder_order;
        List<int> list = [.. (from string f in selectedItems
                          select folder_order.IndexOf(f) into i
                          where i != -1
                          orderby i
                          select i)];
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
        IList selectedItems = foldersListBox.SelectedItems;
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel || selectedItems == null || selectedItems.Count == 0)
        {
            return;
        }
        DispatcherCollection<string> folder_order = mainWindowViewModel.playlistPropertyDialog.folder_order;
        List<int> list = [.. (from string f in selectedItems
                          select folder_order.IndexOf(f) into i
                          where i != -1
                          orderby i
                          select i)];
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
        if (!(sender is CheckBox { IsChecked: var isChecked }) || isChecked != true || base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        DispatcherCollection<string> folder_order = mainWindowViewModel.playlistPropertyDialog.folder_order;
        using var comparer = new NaturalComparer<string>();
        List<string> list = [.. folder_order];
        list.Sort(comparer);
        mainWindowViewModel.playlistPropertyDialog.folder_order = new DispatcherCollection<string>(new ObservableCollection<string>(list), DispatcherHelper.UIDispatcher);
    }
}
