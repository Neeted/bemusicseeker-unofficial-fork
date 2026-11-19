using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

/// <summary>
/// 統合候補一覧の表・ツールバー・優先統合先のウィンドウを、管理主体（<see cref="DuplicateMergeCandidateListOwner"/>）へ接続します。
/// 状態と判断は管理主体が持ち、この部分は画面固有の入力（クリック・キー・メニュー・ツリーの選択）の変換だけを行います。
/// </summary>
public partial class MainWindow
{
    private DuplicateMergeCandidateListOwner DuplicateCandidateList
        => (base.DataContext as MainWindowViewModel)?.DuplicateMergeCandidateList;

    /// <summary>管理主体の表示内容の変更（SELECT・STATUS・KEEP）を表の再描画へ接続します。行集合の入替えは、ItemsSourceの束縛が担います。</summary>
    private void ConnectDuplicateMergeCandidateList(MainWindowViewModel viewModel)
    {
        customTableDuplicateCandidates.ConfigureDialogs(dialogService);
        viewModel.DuplicateMergeCandidateList.DisplayInvalidated += DuplicateMergeCandidateListDisplayInvalidated;
    }

    private void DuplicateMergeCandidateListDisplayInvalidated(object sender, EventArgs e)
        => customTableDuplicateCandidates?.RefreshDisplay();

    private void customTableDuplicateCandidates_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_duplicate_candidates_sort")
            || DuplicateCandidateList == null || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        DuplicateCandidateList.RequestSort(e.SortMemberPath, e.Direction);
    }

    /// <summary>SELECT・KEEPのセルのクリックです。結合列では、先頭でない行のセルでも同じ行の値として扱います。</summary>
    private void customTableDuplicateCandidates_CellActionRequested(object sender, CustomTableCellActionRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_duplicate_candidates_cell_action")
            || DuplicateCandidateList == null || e.Row is not DuplicateMergeCandidateRow row)
        {
            return;
        }
        switch (e.Column?.Id)
        {
            case "Select":
                DuplicateCandidateList.ToggleSelect(row);
                break;
            case "Keep":
                DuplicateCandidateList.SetKeep(row);
                break;
        }
    }

    /// <summary>
    /// 有効なSELECT・KEEP以外のセルのダブルクリック（とEnter）です。重複ツリーの対応するグループを展開し、そのフォルダノードを選択して、既存のフォルダ表示へ移ります。
    /// </summary>
    private void customTableDuplicateCandidates_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_duplicate_candidates_row_activated")
            || base.DataContext is not MainWindowViewModel viewModel || e.Row is not DuplicateMergeCandidateRow row)
        {
            return;
        }
        SelectDuplicateFolderInTree(viewModel, row);
    }

    /// <summary>
    /// 行のフォルダに対応する重複ツリーのフォルダノードを、仮想化で未生成でも公開APIで生成して選択します。
    /// 現在の結果でグループやフォルダを解決できなければ何もしません。選択はツリーの通常の選択として、フォルダ表示への移動を起こします。
    /// </summary>
    private void SelectDuplicateFolderInTree(MainWindowViewModel viewModel, DuplicateMergeCandidateRow row)
    {
        DuplicateGroup group = row.State.Group;
        List<DuplicateGroup> currentGroups = viewModel.MaintenanceTree.DuplicateChartGroups;
        if (currentGroups == null || !currentGroups.Contains(group)) { return; }
        int folderIndex = group.Folders.FindIndex(folder => string.Equals(folder, row.Path, StringComparison.OrdinalIgnoreCase));
        if (folderIndex < 0) { return; }
        viewModel.MaintenanceTree.SynchronizeDuplicateTreeItems();
        TreeViewItem groupItem = RealizeDuplicateGroupTreeItem(group);
        if (groupItem == null) { return; }
        groupItem.IsExpanded = true;
        groupItem.UpdateLayout();
        var folderItem = groupItem.ItemContainerGenerator.ContainerFromIndex(folderIndex) as TreeViewItem;
        if (folderItem == null)
        {
            TryGetTreeViewItemItemsHostPanel(groupItem)?.BringIndexIntoViewPublic(folderIndex);
            groupItem.UpdateLayout();
            folderItem = groupItem.ItemContainerGenerator.ContainerFromIndex(folderIndex) as TreeViewItem;
        }
        if (folderItem == null) { return; }
        folderItem.IsSelected = true;
        folderItem.BringIntoView();
    }

    private void customTableDuplicateCandidates_RowContextMenuRequested(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_duplicate_candidates_context_menu")
            || e.Row is not DuplicateMergeCandidateRow
            || TryFindResource("duplicateCandidateContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.PlacementTarget = customTableDuplicateCandidates;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private IEnumerable<DuplicateMergeCandidateRow> GetSelectedDuplicateCandidateRows()
        => customTableDuplicateCandidates.GetSelectedRowsSnapshot().OfType<DuplicateMergeCandidateRow>();

    private void duplicateCandidateContextMenuCheckOnClick(object sender, RoutedEventArgs e)
        => DuplicateCandidateList?.SetSelectOfRows(GetSelectedDuplicateCandidateRows(), selected: true);

    private void duplicateCandidateContextMenuCheckOffClick(object sender, RoutedEventArgs e)
        => DuplicateCandidateList?.SetSelectOfRows(GetSelectedDuplicateCandidateRows(), selected: false);

    private async void duplicateCandidateContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            DuplicateCandidateList?.OpenInExplorer(GetSelectedDuplicateCandidateRows());
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "duplicateCandidateContextMenuOpenExplorerClick");
        }
    }

    /// <summary>右クリックメニューの「このグループを確認」です。選択行が属するグループを、SELECTの状態によらず、グループごとに一度確認します。</summary>
    private async void duplicateCandidateContextMenuVerifyClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("duplicate_candidate_verify_group")
            || (base.DataContext as MainWindowViewModel)?.DuplicateMergeVerification is not { } workflow)
        {
            return;
        }
        try
        {
            await workflow.VerifyGroupsOfRowsAsync(GetSelectedDuplicateCandidateRows(), this);
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "duplicateCandidateContextMenuVerifyClick");
        }
    }

    /// <summary>ツールバーの「選択グループを確認」です。表示中でSELECTがオンのグループを確認します。対象が0件なら何もしません。</summary>
    private async void duplicateCandidateVerifyClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("duplicate_candidate_verify_selected")
            || (base.DataContext as MainWindowViewModel)?.DuplicateMergeVerification is not { } workflow)
        {
            return;
        }
        try
        {
            await workflow.VerifySelectedAsync(this);
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "duplicateCandidateVerifyClick");
        }
    }

    /// <summary>
    /// 「サイズと更新日時が同じファイルは読まずに同一とみなす」のチェックです。値の保持は管理主体が担い、オンにしたときの案内もそちらが一件出します。
    /// </summary>
    private async void duplicateCandidateQuickCompareClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox
            || (base.DataContext as MainWindowViewModel)?.DuplicateMergeVerification is not { } workflow)
        {
            return;
        }
        try
        {
            await workflow.SetQuickCompareAsync(checkBox.IsChecked == true);
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "duplicateCandidateQuickCompareClick");
        }
    }

    /// <summary>Spaceキーは、選択中の行が属するグループのSELECTを切り替えます。複数行選択ではグループごとに一度です。</summary>
    private void customTableDuplicateCandidates_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || DuplicateCandidateList == null
            || ShouldBlockStartupUiInteraction("custom_table_duplicate_candidates_space"))
        {
            return;
        }
        e.Handled = true;
        DuplicateCandidateList.ToggleSelectOfRows(GetSelectedDuplicateCandidateRows());
    }

    private void duplicateCandidateSelectVisibleClick(object sender, RoutedEventArgs e)
        => DuplicateCandidateList?.SelectVisible();

    private void duplicateCandidateClearVisibleClick(object sender, RoutedEventArgs e)
        => DuplicateCandidateList?.ClearVisible();

    /// <summary>「優先統合先…」のウィンドウを開き、OKで確定した登録内容を一覧の全グループへ適用します。キャンセルは登録内容もKEEPも変えません。</summary>
    private async void duplicateCandidatePreferredDestinationsClick(object sender, RoutedEventArgs e)
    {
        DuplicateMergeCandidateListOwner owner = DuplicateCandidateList;
        if (owner == null || ShouldBlockStartupUiInteraction("duplicate_candidate_preferred_destinations")) { return; }
        try
        {
            var input = new DuplicatePreferredDestinationDialogViewModel(owner.PreferredDestinations);
            UiWindowDialogResult<IReadOnlyList<string>> result = await dialogService.ShowWindowAsync(
                new UiWindowDialogRequest<DuplicatePreferredDestinationDialog, IReadOnlyList<string>>(
                    () => new DuplicatePreferredDestinationDialog(input, dialogService),
                    _ => input.Capture(),
                    this));
            if (result?.Status is UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser) { return; }
            if (result?.Status != UiDialogStatus.Accepted || result.Value == null)
            {
                throw new InvalidOperationException("Preferred destination window failed: " + result?.Status, result?.Error);
            }
            owner.ApplyPreferredDestinations(result.Value);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "duplicateCandidatePreferredDestinationsClick");
        }
    }
}
