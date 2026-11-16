using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

/// <summary>三つの入力元を共通選択一覧と一回の受付へ接続する所有モーダル画面です。</summary>
public partial class PlaylistImportDialog : ThemedWindow
{
    private readonly IUiDialogService dialogService;
    private readonly PlaylistImportDialogViewModel session;

    /// <summary>画面寿命を持つセッションと既存のdialog serviceを接続します。</summary>
    internal PlaylistImportDialog(PlaylistImportDialogViewModel session, IUiDialogService dialogService)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        InitializeComponent();
        DataContext = session;
    }

    private void CandidateRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: PlaylistImportItem { IsGroup: true } item } row) { return; }
        // チェックと矢印は自身のToggleを使い、ラベルと行余白だけで一度開閉します。
        for (var current = e.OriginalSource as DependencyObject; current != null && current != row; current = current is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(current))
        {
            if (current is ToggleButton) { return; }
        }
        if (e.ClickCount == 1)
        {
            item.IsExpanded = !item.IsExpanded;
            if (row.TemplatedParent is TreeViewItem container) { container.IsSelected = true; container.Focus(); }
        }
        e.Handled = true;
    }
    private void AddUri(object sender, RoutedEventArgs e) => session.AddUriInput();
    private void RemoveSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PlaylistImportItem { Candidate: { } c } })
        {
            session.SetSelected(c, false);
        }
    }
    private void ClearSelected(object sender, RoutedEventArgs e) => session.ClearSelection();
    private void StartImport(object sender, RoutedEventArgs e)
    {
        if (session.Start().Status == PlaylistImportStartStatus.Started)
        {
            Close();
        }
    }
    private void CancelImport(object sender, RoutedEventArgs e) => Close();
    private void DialogKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } }
    private async void OpenLocalFile(object sender, RoutedEventArgs e)
    {
        try { await HandleOpenLocalFileAsync(); }
        catch (Exception exception)
        {
            TryLogPickerFailure(exception);
            if (DataContext == null)
            {
                return;
            }

            try
            {
                UiDialogResult result = await dialogService.ShowMessageAsync(new UiMessageRequest(
                    BeMusicSeeker.Properties.Resources.Msg_error_unexpected,
                    BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, owner: this));
                if (result == null || result.Status is not (UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
                {
                    TryLogPickerFailure(result?.Exception);
                }
            }
            catch (Exception notificationFailure) { TryLogPickerFailure(notificationFailure); }
        }
    }

    private static void TryLogPickerFailure(Exception exception)
    {
        try { Ribbit.Logging.NLogWrapper.FileLogger?.Error(exception, "Playlist import JSON picker failed."); }
        catch { /* 診断先の失敗で元の入力やpicker結果を変更しません。 */ }
    }

    /// <summary>JSON pickerの受理結果だけを入力へ追記し、失敗原因を呼出元へ返します。</summary>
    internal async Task HandleOpenLocalFileAsync()
    {
        UiFilePickerResult result = await dialogService.PickFileAsync(new UiFilePickerRequest(
            BeMusicSeeker.Properties.Resources.Open_PlaylistHeader,
            filter: BeMusicSeeker.Properties.Resources.Json_file_exts,
            defaultExtension: ".json", owner: this));
        if (result == null)
        {
            throw new InvalidOperationException("Playlist JSON picker returned no result.");
        }

        if (result.Status is UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        if (result.Status != UiDialogStatus.Accepted)
        {
            throw new InvalidOperationException("Playlist JSON picker failed: " + result.Status, result.Error);
        }

        if (DataContext == null)
        {
            return;
        }

        session.AppendInputLine(result.FileName);
        textBoxURIInput.CaretIndex = textBoxURIInput.Text.Length;
    }
    /// <summary>閉鎖時に一時セッションの購読と画面参照を回収します。</summary>
    protected override void OnClosed(EventArgs e)
    {
        DataContext = null;
        session.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>カテゴリと候補の共通選択へ、マウス・Space・AutomationのToggleを接続します。</summary>
/// <remarks>中間状態から全選択する取込み固有の操作であり、共通CheckBoxの三状態循環は変更しません。</remarks>
public sealed class PlaylistImportSelectionCheckBox : CheckBox
{
    /// <summary>表示条件に一致するURIだけを一括操作します。</summary>
    protected override void OnToggle()
    {
        if (DataContext is PlaylistImportItem item) { item.ToggleSelection(); }
        else if (DataContext is PlaylistImportDialogViewModel session) { session.ToggleExternalSelection(); }
    }
}
