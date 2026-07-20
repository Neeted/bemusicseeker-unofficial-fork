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

    private async void CancelAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is PlaylistPropertyDialogViewModel playlistPropertyDialogViewModel)
        {
            try
            {
                PlaylistPropertyDialogOperationResult result =
                    await playlistPropertyDialogViewModel.ResetPropertiesAsync();
                if (result == PlaylistPropertyDialogOperationResult.Completed)
                {
                    playlistPropertyDialogViewModel.Dispose();
                    GetDialogHost().ClosePlaylistPropertyDialog(playlistPropertyDialogViewModel);
                }
                else if (result == PlaylistPropertyDialogOperationResult.ValidationFailed)
                {
                    ShowValidationError();
                }
            }
            catch (Exception ex)
            {
                ShowOperationFailure(ex);
            }
        }
    }

    private async void SaveAndClose(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is PlaylistPropertyDialogViewModel playlistPropertyDialogViewModel)
        {
            try
            {
                PlaylistPropertyDialogOperationResult result =
                    await playlistPropertyDialogViewModel.SaveAndApplyAsync();
                if (result == PlaylistPropertyDialogOperationResult.Completed)
                {
                    playlistPropertyDialogViewModel.Dispose();
                    GetDialogHost().ClosePlaylistPropertyDialog(playlistPropertyDialogViewModel);
                }
                else if (result == PlaylistPropertyDialogOperationResult.ValidationFailed)
                {
                    ShowValidationError();
                }
            }
            catch (Exception ex)
            {
                ShowOperationFailure(ex);
            }
        }
    }

    private void ShowValidationError()
    {
        UiDialogRoute.ShowMessageBox(
            Window.GetWindow(this),
            "プレイリスト名・URI・出力先フォルダ名を確認して下さい。",
            "エラー",
            MessageBoxButton.OK,
            MessageBoxImage.Hand);
    }

    private void ShowOperationFailure(Exception exception)
    {
        UiDialogRoute.ShowMessageBox(
            Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand);
    }

    private void folderListUp(object sender, RoutedEventArgs e)
    {
        IList selectedItems = foldersListBox.SelectedItems;
        if (base.DataContext is not PlaylistPropertyDialogViewModel viewModel || selectedItems == null || selectedItems.Count == 0)
        {
            return;
        }
        ObservableCollection<string> folder_order = viewModel.folder_order;
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
        if (base.DataContext is not PlaylistPropertyDialogViewModel viewModel || selectedItems == null || selectedItems.Count == 0)
        {
            return;
        }
        ObservableCollection<string> folder_order = viewModel.folder_order;
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
        if (!(sender is CheckBox { IsChecked: var isChecked }) || isChecked != true || base.DataContext is not PlaylistPropertyDialogViewModel viewModel)
        {
            return;
        }
        ObservableCollection<string> folder_order = viewModel.folder_order;
        using var comparer = new NaturalComparer<string>();
        List<string> list = [.. folder_order];
        list.Sort(comparer);
        viewModel.folder_order = new ObservableCollection<string>(list);
    }
}
