using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// 重複音源チェックのツリー・整理候補一覧の表・ツールバーを、管理主体へ接続します。探索・確認・整理の手順と状態は管理主体が持ち、
/// この部分は画面固有の入力（クリック・キー・メニュー・ツリーの選択）の変換と、想定外の例外の通知だけを行います。
/// </summary>
public partial class MainWindow
{
    private int audioDuplicateSelectionsInFlight;

    private AudioDuplicateCandidateListOwner AudioDuplicateCandidates
        => (base.DataContext as MainWindowViewModel)?.AudioDuplicateCandidateList;

    /// <summary>表の再描画を管理主体へ接続します。</summary>
    private void ConnectAudioDuplicateCandidateList(MainWindowViewModel viewModel)
    {
        customTableAudioDuplicateCandidates.ConfigureDialogs(dialogService);
        viewModel.AudioDuplicateCandidateList.DisplayInvalidated += AudioDuplicateCandidateListDisplayInvalidated;
    }

    private void AudioDuplicateCandidateListDisplayInvalidated(object sender, EventArgs e)
    {
        if (ReferenceEquals(sender, AudioDuplicateCandidates)) { customTableAudioDuplicateCandidates?.RefreshDisplay(); }
    }

    /// <summary>
    /// 重複音源のルート・グループ・フォルダ・整理候補一覧の選択です。探索と遷移の手順は <see cref="AudioDuplicateSearchWorkflowOwner"/> が持ち、ここでは接続と、画面固有の選択の区別だけを行います。
    /// 選択中の子が結果の失効や入れ替えで除去されると、画面が親のルートを自動で選び直します。この選び直しは利用者の選択ではないため、探索もBusy案内も行いません。
    /// </summary>
    private async void audioDuplicateCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_audio_duplicate_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        object parameter;
        if (treeRoot == treeViewItem)
        {
            if (viewModel.MaintenanceTree.IsSynchronizingAudioDuplicateTree)
            {
                await NavigateAudioDuplicateRootAfterAutomaticSelectionAsync(viewModel);
                return;
            }
            parameter = null;
        }
        else if (treeViewItem.DataContext is string or DuplicateGroup)
        {
            parameter = treeViewItem.DataContext;
        }
        else if (treeViewItem.DataContext is DuplicateMergeCandidateListNode)
        {
            // 整理候補一覧のノードも、他の子と同じ入口（探索の要否の判断を含む）へ、専用の表を出す文脈で入る。
            parameter = DuplicateViewContext.ForCandidateList();
        }
        else
        {
            return;
        }
        await RunAudioDuplicateSelectionAsync(viewModel, treeRoot, treeViewItem, parameter);
    }

    /// <summary>選択の手順を呼び、探索中に子が入れ替わって選択がルートへ移った場合は、終端でルートの一覧へ合わせます（探索は行いません）。</summary>
    private async Task RunAudioDuplicateSelectionAsync(MainWindowViewModel viewModel, TreeViewItem treeRoot, TreeViewItem selected, object parameter)
    {
        audioDuplicateSelectionsInFlight++;
        try
        {
            await viewModel.AudioDuplicateSearch.SelectAsync(parameter, this);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "audioDuplicateCheckFolderSelect");
        }
        finally { audioDuplicateSelectionsInFlight--; }
        try
        {
            if (treeRoot != selected && !selected.IsSelected && treeRoot.IsSelected)
            {
                await NavigateAudioDuplicateRootAfterAutomaticSelectionAsync(viewModel);
            }
            if (treeRoot.IsSelected && viewModel.MaintenanceTree.AudioDuplicateTreeChildren.Count > 0)
            {
                treeRoot.IsExpanded = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "audioDuplicateCheckFolderSelect");
        }
    }

    /// <summary>
    /// 自動で選び直されたルートの一覧を、探索せずに表示します（結果がなければ空、有効な結果があればその全件）。
    /// 利用者の選択による探索がすでに進行中なら、その終端で合わせるため何もしません。
    /// </summary>
    private async Task NavigateAudioDuplicateRootAfterAutomaticSelectionAsync(MainWindowViewModel viewModel)
    {
        if (audioDuplicateSelectionsInFlight > 0) { return; }
        try
        {
            await viewModel.RegularChartList.NavigateMaintenanceAsync(MainViewUpdateMode.AudioDuplicateFilterSelected, DuplicateViewContext.ForRoot());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "audioDuplicateCheckFolderSelect");
        }
    }

    /// <summary>選択済みのルートのクリックです。有効な結果がなければ探索します（選択の変化を伴うクリックは選択のイベントが扱います）。</summary>
    private async void audioDuplicateRootPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem treeRoot || !treeRoot.IsSelected
            || e.OriginalSource is not DependencyObject source || (source as TreeViewItem ?? WPFUtil.FindVisualParent<TreeViewItem>(source)) != treeRoot
            || WPFUtil.FindVisualParent<ToggleButton>(source) != null
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await ReselectAudioDuplicateRootAsync(viewModel, treeRoot);
    }

    /// <summary>ルートでのEnterキーです。未選択なら選択し（選択のイベントが扱います）、選択済みで有効な結果がなければ探索します。</summary>
    private async void audioDuplicateRootKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TreeViewItem treeRoot || !ReferenceEquals(e.OriginalSource, treeRoot)
            || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        if (!treeRoot.IsSelected)
        {
            treeRoot.IsSelected = true;
            return;
        }
        await ReselectAudioDuplicateRootAsync(viewModel, treeRoot);
    }

    private async Task ReselectAudioDuplicateRootAsync(MainWindowViewModel viewModel, TreeViewItem treeRoot)
    {
        if (ShouldBlockStartupUiInteraction("tree_audio_duplicate_check_select") || viewModel.AudioDuplicateSearch.HasCurrentResult) { return; }
        await RunAudioDuplicateSelectionAsync(viewModel, treeRoot, treeRoot, null);
    }

    private void customTableAudioDuplicateCandidates_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_audio_duplicate_candidates_sort")
            || AudioDuplicateCandidates == null || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        AudioDuplicateCandidates.RequestSort(e.SortMemberPath, e.Direction);
    }

    /// <summary>SELECTのセルのクリックです。</summary>
    private void customTableAudioDuplicateCandidates_CellActionRequested(object sender, CustomTableCellActionRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_audio_duplicate_candidates_cell_action")
            || AudioDuplicateCandidates == null || e.Row is not AudioDuplicateCandidateRow row)
        {
            return;
        }
        if (e.Column?.Id == "Select") { AudioDuplicateCandidates.ToggleSelect(row); }
    }

    /// <summary>
    /// 有効なSELECT以外のセルのダブルクリック（とEnter）です。重複音源ツリーの対応するグループを展開し、そのフォルダノードを選択して、既存のフォルダ表示へ移ります。
    /// 現在の結果で解決できなければ何もしません。
    /// </summary>
    private void customTableAudioDuplicateCandidates_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_audio_duplicate_candidates_row_activated")
            || base.DataContext is not MainWindowViewModel viewModel || e.Row is not AudioDuplicateCandidateRow row)
        {
            return;
        }
        SelectAudioDuplicateFolderInTree(viewModel, row);
    }

    /// <summary>行のフォルダに対応する重複音源ツリーのフォルダノードを、仮想化で未生成でも公開APIで生成して選択します。</summary>
    private void SelectAudioDuplicateFolderInTree(MainWindowViewModel viewModel, AudioDuplicateCandidateRow row)
    {
        DuplicateGroup group = row.State.Group;
        List<DuplicateGroup> currentGroups = viewModel.MaintenanceTree.AudioDuplicateGroups;
        if (currentGroups == null || !currentGroups.Contains(group)) { return; }
        int folderIndex = group.Folders.FindIndex(folder => string.Equals(folder, row.Path, StringComparison.OrdinalIgnoreCase));
        if (folderIndex < 0) { return; }
        viewModel.MaintenanceTree.SynchronizeAudioDuplicateTreeItems();
        TreeViewItem groupItem = RealizeGroupTreeItem(treeViewItemSearchAudioDuplicate, group);
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

    private void customTableAudioDuplicateCandidates_RowContextMenuRequested(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_audio_duplicate_candidates_context_menu")
            || e.Row is not AudioDuplicateCandidateRow
            || TryFindResource("audioDuplicateCandidateContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.PlacementTarget = customTableAudioDuplicateCandidates;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private IEnumerable<AudioDuplicateCandidateRow> GetSelectedAudioDuplicateCandidateRows()
        => customTableAudioDuplicateCandidates.GetSelectedRowsSnapshot().OfType<AudioDuplicateCandidateRow>();

    private void audioDuplicateCandidateContextMenuCheckOnClick(object sender, RoutedEventArgs e)
        => AudioDuplicateCandidates?.SetSelectOfRows(GetSelectedAudioDuplicateCandidateRows(), selected: true);

    private void audioDuplicateCandidateContextMenuCheckOffClick(object sender, RoutedEventArgs e)
        => AudioDuplicateCandidates?.SetSelectOfRows(GetSelectedAudioDuplicateCandidateRows(), selected: false);

    private async void audioDuplicateCandidateContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AudioDuplicateCandidates?.OpenInExplorer(GetSelectedAudioDuplicateCandidateRows());
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "audioDuplicateCandidateContextMenuOpenExplorerClick");
        }
    }

    /// <summary>右クリックメニューの「このフォルダを確認」です。選択行のフォルダを、SELECTの状態によらず、フォルダごとに一度確認します。</summary>
    private async void audioDuplicateCandidateContextMenuVerifyClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("audio_duplicate_candidate_verify_folder")
            || (base.DataContext as MainWindowViewModel)?.AudioDuplicateVerification is not { } workflow)
        {
            return;
        }
        try
        {
            await workflow.VerifyFoldersOfRowsAsync(GetSelectedAudioDuplicateCandidateRows(), this);
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "audioDuplicateCandidateContextMenuVerifyClick");
        }
    }

    /// <summary>ツールバーの「選択フォルダを確認」です。表示中でSELECTがオンのフォルダを確認します。対象が0件なら何もしません。</summary>
    private async void audioDuplicateCandidateVerifyClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("audio_duplicate_candidate_verify_selected")
            || (base.DataContext as MainWindowViewModel)?.AudioDuplicateVerification is not { } workflow)
        {
            return;
        }
        try
        {
            await workflow.VerifySelectedAsync(this);
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "audioDuplicateCandidateVerifyClick");
        }
    }

    /// <summary>
    /// ツールバーの「選択フォルダを整理」です。表示中でSELECTがオンのフォルダを整理します。対象が0件なら何もしません。
    /// 確認・受付・進捗・結果の案内と失敗の報告は手順の管理主体が一度ずつ行い、この部分は呼出しの接続と、想定外の例外の通知だけを担います。
    /// </summary>
    private async void audioDuplicateCandidateOrganizeClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("audio_duplicate_candidate_organize_selected")
            || (base.DataContext as MainWindowViewModel)?.AudioDuplicateOrganize is not { } workflow)
        {
            return;
        }
        try
        {
            await workflow.OrganizeSelectedAsync(this);
        }
        catch (Exception exception)
        {
            await NotifyMainWindowOperationFailureAsync(exception, "audioDuplicateCandidateOrganizeClick");
        }
    }

    /// <summary>Spaceキーは、選択中の行のフォルダのSELECTを切り替えます。複数行選択ではフォルダごとに一度です。</summary>
    private void customTableAudioDuplicateCandidates_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || AudioDuplicateCandidates == null
            || ShouldBlockStartupUiInteraction("custom_table_audio_duplicate_candidates_space"))
        {
            return;
        }
        e.Handled = true;
        AudioDuplicateCandidates.ToggleSelectOfRows(GetSelectedAudioDuplicateCandidateRows());
    }

    private void audioDuplicateCandidateSelectVisibleClick(object sender, RoutedEventArgs e)
        => AudioDuplicateCandidates?.SelectVisible();

    private void audioDuplicateCandidateClearVisibleClick(object sender, RoutedEventArgs e)
        => AudioDuplicateCandidates?.ClearVisible();
}
