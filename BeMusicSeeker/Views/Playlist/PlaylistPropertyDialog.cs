using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Util;

namespace BeMusicSeeker.Views;

/// <summary>
/// Presents one playlist property edit session as a native, owner-modal window.
/// The workspace owns the session; this window only gates its terminal operation and
/// reports the result to <see cref="UiDialogCoordinator"/>.
/// </summary>
public partial class PlaylistPropertyDialog : ThemedWindow, IComponentConnector
{
    private int operationInProgress;

    private Task operationTask = Task.CompletedTask;

    private readonly IUiDialogService dialogService;

    private bool allowClose;

    private bool closeRequestInProgress;

    private bool terminalCloseScheduled;

    private bool terminalCloseEntered;

    private bool ownerShutdownCloseRequested;

    private bool terminalOutcome;

    private bool closed;

    /// <summary>XAML用の未接続画面と既定coordinatorを構成します。</summary>
    public PlaylistPropertyDialog()
    {
        dialogService = new UiDialogCoordinator();
        InitializeComponent();
    }

    /// <summary>所有workspaceの編集sessionと同じcoordinatorで、保存・必要通知の終端を待つ画面を作ります。</summary>
    /// <param name="viewModel">画面が表示する編集session。</param>
    /// <param name="dialogService">親画面と同じ所有者解決・通知窓口。省略時は既定coordinatorを使います。</param>
    internal PlaylistPropertyDialog(PlaylistPropertyDialogViewModel viewModel, IUiDialogService dialogService = null)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.dialogService = dialogService ?? this.dialogService;
    }

    /// <summary>Gets whether a completed save/reset terminal operation closed this window.</summary>
    internal bool HasTerminalOutcome => terminalOutcome;

    /// <summary>Gets whether the owner forced this window closed as part of application shutdown.</summary>
    internal bool IsOwnerShutdownClose => ownerShutdownCloseRequested;

    /// <summary>保存・リセット・通知の試行・閉鎖・再有効化の終端までを返します。任意通知の失敗は次の操作へ持ち越しません。</summary>
    internal Task WaitForOperationCompletionAsync() => operationTask;

    /// <summary>
    /// Closes this window for owner shutdown without initiating save or reset.
    /// Repeated calls are intentionally idempotent.
    /// </summary>
    internal void CloseForOwnerShutdown()
    {
        ownerShutdownCloseRequested = true;
        allowClose = true;
        if (!closed)
        {
            Close();
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        base.OnClosed(e);
    }

    private void CancelAndClose(object sender, RoutedEventArgs e)
    {
        StartTerminalOperation(save: false);
    }

    private void SaveAndClose(object sender, RoutedEventArgs e)
    {
        StartTerminalOperation(save: true);
    }

    private void DialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        StartTerminalOperation(save: false);
    }

    private void PropertyNavigationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || propertyContentScrollViewer == null)
        {
            return;
        }

        // Each category is a separate draft view in one shared viewport.  Resetting
        // the viewport at the view boundary prevents the newly selected category
        // from inheriting the previous category's scroll position.
        propertyContentScrollViewer.ScrollToTop();
        propertyContentScrollViewer.ScrollToLeftEnd();
    }

    /// <inheritdoc />
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (allowClose || ownerShutdownCloseRequested)
        {
            terminalCloseEntered = true;
            base.OnClosing(e);
            return;
        }

        if (DataContext is not PlaylistPropertyDialogViewModel)
        {
            // An unbound tooling/presentation instance has no edit session to reset.
            allowClose = true;
            base.OnClosing(e);
            return;
        }

        // A user close (including the title-bar X) has the same reset lifecycle as Cancel.
        // Keep the native window open until the workspace confirms the reset completed.
        e.Cancel = true;
        if (Volatile.Read(ref operationInProgress) == 0
            && !terminalOutcome
            && !terminalCloseScheduled)
        {
            closeRequestInProgress = true;
            try
            {
                StartTerminalOperation(save: false);
            }
            finally
            {
                closeRequestInProgress = false;
            }
        }
    }

    private void StartTerminalOperation(bool save)
    {
        if (ownerShutdownCloseRequested || Interlocked.CompareExchange(ref operationInProgress, 1, 0) != 0)
        {
            return;
        }

        if (DataContext is not PlaylistPropertyDialogViewModel viewModel)
        {
            Interlocked.Exchange(ref operationInProgress, 0);
            return;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        operationTask = completion.Task;
        _ = RunTerminalOperationAsync(viewModel, save, completion);
    }

    private async Task RunTerminalOperationAsync(
        PlaylistPropertyDialogViewModel viewModel,
        bool save,
        TaskCompletionSource<bool> completion)
    {
        Exception failure = null;
        try
        {
            IsEnabled = false;
            PlaylistPropertyDialogOperationResult result;
            try
            {
                result = save
                    ? await viewModel.SaveAndApplyAsync()
                    : await viewModel.ResetPropertiesAsync();
            }
            catch (Exception operationFailure)
            {
                LogTerminalFailure(operationFailure, "operation");
                await NotifyBestEffortAsync(() => ShowOperationFailureAsync(operationFailure));
                return;
            }

            if (ownerShutdownCloseRequested) { return; }
            if (result == PlaylistPropertyDialogOperationResult.Completed)
            {
                await CloseWithResultAsync(save);
                terminalOutcome = true;
            }
            else if (result == PlaylistPropertyDialogOperationResult.ValidationFailed)
            {
                await NotifyBestEffortAsync(ShowValidationErrorAsync);
            }
        }
        catch (Exception terminalFailure)
        {
            // 閉鎖・操作受付の失敗は通知だけの失敗ではなく、画面寿命の未完了として返す。
            failure = terminalFailure;
            LogTerminalFailure(terminalFailure, "terminal");
            await NotifyBestEffortAsync(() => ShowOperationFailureAsync(terminalFailure));
        }
        finally
        {
            try { IsEnabled = true; }
            catch (Exception cleanupFailure)
            {
                failure = failure == null ? cleanupFailure : new AggregateException(failure, cleanupFailure);
            }
            finally { Interlocked.Exchange(ref operationInProgress, 0); }
            if (failure is OperationCanceledException cancelled) { completion.TrySetCanceled(cancelled.CancellationToken); }
            else if (failure != null) { completion.TrySetException(failure); }
            else { completion.TrySetResult(true); }
        }
    }

    private async Task CloseWithResultAsync(bool result)
    {
        if (closed) { return; }
        if (closeRequestInProgress)
        {
            // 同期リセットからの閉鎖は元のClosingを抜けてから行い、その完了も操作Taskで待つ。
            terminalCloseScheduled = true;
            try { await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Normal); }
            finally { terminalCloseScheduled = false; }
            if (closed) { return; }
        }
        allowClose = true;
        terminalCloseEntered = false;
        try { DialogResult = result; }
        catch (InvalidOperationException) when (!terminalCloseEntered)
        {
            // Show()で開いた非モーダル画面にはDialogResultがない。実Closeの失敗は呼出元へ返す。
            Close();
        }
        if (!closed)
        {
            throw new InvalidOperationException("Playlist property terminal close did not complete.");
        }
    }

    private static async Task NotifyBestEffortAsync(Func<Task> notification)
    {
        try { await notification(); }
        catch (Exception failure) { LogTerminalFailure(failure, "notification"); }
    }

    private static void LogTerminalFailure(Exception failure, string stage)
    {
        try { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(failure, "playlist_property_dialog_failed stage=" + stage); }
        catch { /* 任意診断を保存結果や次の操作へ持ち越さない。 */ }
    }

    private async Task ShowValidationErrorAsync()
    {
        UiDialogResult notification = await dialogService.ShowMessageAsync(new UiMessageRequest(
            BeMusicSeeker.Properties.Resources.Error_PlaylistPropertiesInvalid,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            owner: this));
        UiDialogRoute.ThrowIfNotShown(notification, "Playlist property validation notification");
    }

    private async Task ShowOperationFailureAsync(Exception exception)
    {
        UiDialogResult notification = await dialogService.ShowMessageAsync(new UiMessageRequest(
            BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            owner: this));
        UiDialogRoute.ThrowIfNotShown(notification, "Playlist property failure notification");
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
        using var comparer = new NaturalComparer();
        List<string> list = [.. folder_order];
        list.Sort(comparer);
        viewModel.folder_order = new ObservableCollection<string>(list);
    }
}
