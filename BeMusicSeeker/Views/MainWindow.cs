using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using NLog;
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using Ribbit.Windows;

namespace BeMusicSeeker.Views;

/// <summary>
/// アプリケーションのメインウィンドウを表すクラスです。
/// UIの初期化、主要なイベントハンドリング（ドラッグ＆ドロップ、ウィンドウ状態の変更、閉じる処理など）、
/// および非同期のアップデートチェッカー等のグローバルな制御を統括します。
/// </summary>
public partial class MainWindow : Window, IComponentConnector, IStyleConnector
{
    public static readonly DependencyProperty PlaybackOverlayVisibilityProperty = DependencyProperty.Register(
        nameof(PlaybackOverlayVisibility),
        typeof(Visibility),
        typeof(MainWindow),
        new PropertyMetadata(Visibility.Collapsed));

    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.MainWindow");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private static readonly MethodInfo playlistTreeBringIndexIntoViewMethod = typeof(System.Windows.Controls.VirtualizingStackPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic) ?? typeof(System.Windows.Controls.VirtualizingPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic);

    private bool _isClosingOrClosed;

    private bool _shutdownPrepared;

    private bool _shutdownPreparationRunning;

    private readonly object shutdownPreparationLock = new();

    private Task<ShutdownPreparationResult> shutdownPreparationTask;

    private FrameworkElement activeOverlayDialog;

    private MainWindowViewModel subscribedViewModel;

    public Visibility PlaybackOverlayVisibility
    {
        get => (Visibility)GetValue(PlaybackOverlayVisibilityProperty);
        private set => SetValue(PlaybackOverlayVisibilityProperty, value);
    }

    private async Task RunProgressUntilTaskCompletesAsync(Task task, CancellationTokenSource cancellationTokenSource, string title, string label, Action<UiProgressContext> reportProgress)
    {
        UiProgressResult progressResult = await new UiDialogCoordinator().RunWithProgressAsync(
            new UiProgressRequest(title, label, new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false), this),
            context =>
            {
                while (!task.IsCompleted)
                {
                    try
                    {
                        reportProgress(context);
                    }
                    catch
                    {
                        cancellationTokenSource.Cancel();
                        WaitForTaskCompletion(task);
                        break;
                    }

                    Thread.Sleep(100);
                }

                return Task.CompletedTask;
            });
        if (progressResult.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
        {
            await task;
            return;
        }

        cancellationTokenSource.Cancel();
        try
        {
            await task;
        }
        catch
        {
        }

        throw new InvalidOperationException("Progress dialog route failed: " + progressResult.Status, progressResult.Error);
    }

    private static void WaitForTaskCompletion(Task task)
    {
        while (!task.IsCompleted)
        {
            Thread.Sleep(100);
        }
    }

    private static void ThrowIfPickerFailed(UiDialogStatus status, Exception exception, string routeName)
    {
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + status, exception);
    }

    private static void ThrowIfWindowDialogFailed(UiDialogStatus status, Exception exception, string routeName)
    {
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + status, exception);
    }

    private static void ThrowIfUiDialogNotShown(UiDialogResult result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " failed: no result");
        }
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Exception);
    }

    internal void ShowOverlayDialog(FrameworkElement dialog)
    {
        if (dialog == null)
        {
            throw new ArgumentNullException(nameof(dialog));
        }
        if (activeOverlayDialog != null
            && !ReferenceEquals(activeOverlayDialog, dialog)
            && activeOverlayDialog.Visibility == Visibility.Visible)
        {
            throw new InvalidOperationException("Another overlay dialog is already visible: " + activeOverlayDialog.GetType().Name);
        }

        activeOverlayDialog = dialog;
        if (HidesPlaybackSurface(dialog))
        {
            PlaybackOverlayVisibility = Visibility.Visible;
        }
        dialog.Visibility = Visibility.Visible;
        dialog.Focus();
    }

    internal void HideOverlayDialog(FrameworkElement dialog)
    {
        if (dialog == null)
        {
            return;
        }

        dialog.Visibility = Visibility.Hidden;
        if (ReferenceEquals(activeOverlayDialog, dialog))
        {
            activeOverlayDialog = null;
            if (HidesPlaybackSurface(dialog))
            {
                PlaybackOverlayVisibility = Visibility.Collapsed;
            }
        }
    }

    private bool HidesPlaybackSurface(FrameworkElement dialog)
    {
        return ReferenceEquals(dialog, settingDialog)
            || ReferenceEquals(dialog, playlistPropertyDialog)
            || ReferenceEquals(dialog, playlistSummaryBulkEditDialog)
            || ReferenceEquals(dialog, loadPlaylistURIDialog);
    }

    private void addRootFolderMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel { settingDialog: { } settingDialogViewModel } viewModel)
        {
            return;
        }

        UiFolderPickerResult result = new UiDialogCoordinator()
            .PickFolderAsync(new UiFolderPickerRequest(
                selectedPath: viewModel.BMSParentFolderList?.FirstOrDefault(),
                multiselect: false,
                ensurePathExists: true,
                owner: this))
            .GetAwaiter()
            .GetResult();
        ThrowIfPickerFailed(result.Status, result.Error, "Main window add root folder picker");
        if (result.Status == UiDialogStatus.Accepted)
        {
            settingDialogViewModel.AddBmsSearchRootPathFromMainWindowPicker(result.FolderPath);
        }
    }

    private ContextMenu _lastOpenedContextMenu;

    // NOTE:
    // TreeView の仮想化 (Recycling) 有効時は、画面外ノードのコンテナが VisualTree から外れる。
    // そのため「VisualTree を再帰して選択状態を判定する」実装は false negative を起こす。
    // ここでは最後に確定した選択ノードの所属セクションを保持し、UIコンテナ有無に依存しない判定を行う。
    private enum TreeSelectionSection
    {
        None,
        Playlist,
        PlayHistory,
        InstallPending,
        InstallInstalled,
        FullScanCheck,
        ChartInfoParseError,
        Other
    }

    private TreeSelectionSection _currentTreeSelectionSection = TreeSelectionSection.None;


    private CancellationTokenSource tableContextMenuTaskTokenSource;

    private Task changeSubmenuOpenDocumentTask;

    private readonly Storyboard treeViewItemInstantStoryBoardPlaylistTable = new();


    // マージ後に自動選択するDuplicateGroupのHeader（曲名）をキャッシュ
    private string _pendingDuplicateGroupHeader;
    private int _duplicateGroupAutoSelectRequestVersion;
    private PropertyChangedEventHandler _duplicateGroupAutoSelectHandler;
    private MainWindowViewModel _duplicateGroupAutoSelectHandlerOwner;

    private bool startupInitialSelectionApplied;

    private PropertyChangedEventHandler _startupInitialSelectionReadyHandler;

    /// <summary>
    /// <see cref="MainWindow"/> クラスの新しいインスタンスを初期化します。
    /// UIコンポーネントの構築、TreeViewのイベントハンドラ登録、
    /// 設定のプロパティ変更リスナの初期化、および非同期のアップデートチェックを開始します。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        ApplySavedTreeViewWidth();
        AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(keywordSearchWindowPreviewMouseDown), true);
        Deactivated += MainWindow_Deactivated;
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PlaylistSummarySelectionRestoreRequested += MainWindowViewModel_PlaylistSummarySelectionRestoreRequested;
            SubscribeViewModelUiInteractions(viewModel);
        }
        Closed += delegate
        {
            UnsubscribeViewModelUiInteractions();
        };
        ContentRendered += MainWindow_ContentRendered;

        // Add handler that catches already-handled TreeViewItem.Selected events to synchronize TreeView exclusivity
        gridTreePane.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler(gridTreePane_TreeViewItemSelected), true);

        if (base.DataContext is MainWindowViewModel startupViewModel)
        {
            startupViewModel.StartupUpdateWorkflow.Start();
        }
    }

    private void MainWindow_ContentRendered(object sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
        {
            if (base.DataContext is MainWindowViewModel viewModel)
            {
                viewModel.ElevatedProcessWarningWorkflow.Start(CanPresentElevatedProcessWarning);
            }
        }));
    }

    private bool CanPresentElevatedProcessWarning()
    {
        if (_isClosingOrClosed || !IsLoaded || Visibility != Visibility.Visible)
        {
            return false;
        }

        Application application = Application.Current;
        if (application == null)
        {
            return false;
        }

        Dispatcher applicationDispatcher = application.Dispatcher;
        return applicationDispatcher != null
            && !applicationDispatcher.HasShutdownStarted
            && !applicationDispatcher.HasShutdownFinished
            && !Dispatcher.HasShutdownStarted
            && !Dispatcher.HasShutdownFinished;
    }

    private bool IsPlaylistUrlDownloadRunning
    {
        get
        {
            return (base.DataContext as MainWindowViewModel)?.PlaylistWorkspace.IsPlaylistUrlDownloadRunning == true;
        }
    }

    private void SubscribeViewModelUiInteractions(MainWindowViewModel viewModel)
    {
        if (viewModel == null || ReferenceEquals(subscribedViewModel, viewModel))
        {
            return;
        }
        UnsubscribeViewModelUiInteractions();
        subscribedViewModel = viewModel;
        viewModel.settingDialog.OpenRequested += MainWindowViewModel_SettingDialogOpenRequested;
        viewModel.settingDialog.PresentationRequested += MainWindowViewModel_SettingDialogPresentationRequested;
        viewModel.InitialSetupLanguageDialogRequested += MainWindowViewModel_InitialSetupLanguageDialogRequested;
        viewModel.InitializationSucceeded += MainWindowViewModel_InitializationSucceeded;
        viewModel.PlaylistUrlInstallTreeExpansionRequested += MainWindowViewModel_PlaylistUrlInstallTreeExpansionRequested;
        viewModel.FolderAutoRenameWorkflow.TerminalPublished += MainWindowViewModel_FolderAutoRenameTerminalPublished;
        viewModel.StartupUpdateWorkflow.PresentationRequested += MainWindowViewModel_StartupUpdatePresentationRequested;
        viewModel.StartupUpdateWorkflow.ShutdownPreparationRequested += MainWindowViewModel_StartupUpdateShutdownPreparationRequested;
        viewModel.StartupUpdateWorkflow.FailurePresentationRequested += MainWindowViewModel_StartupUpdateFailurePresentationRequested;
        viewModel.StartupUpdateWorkflow.ApplicationShutdownRequested += MainWindowViewModel_StartupUpdateApplicationShutdownRequested;
        viewModel.ElevatedProcessWarningWorkflow.PresentationRequested += MainWindowViewModel_ElevatedProcessWarningPresentationRequested;
    }

    private void UnsubscribeViewModelUiInteractions()
    {
        if (subscribedViewModel == null)
        {
            return;
        }
        subscribedViewModel.settingDialog.OpenRequested -= MainWindowViewModel_SettingDialogOpenRequested;
        subscribedViewModel.settingDialog.PresentationRequested -= MainWindowViewModel_SettingDialogPresentationRequested;
        subscribedViewModel.InitialSetupLanguageDialogRequested -= MainWindowViewModel_InitialSetupLanguageDialogRequested;
        subscribedViewModel.InitializationSucceeded -= MainWindowViewModel_InitializationSucceeded;
        subscribedViewModel.PlaylistUrlInstallTreeExpansionRequested -= MainWindowViewModel_PlaylistUrlInstallTreeExpansionRequested;
        subscribedViewModel.FolderAutoRenameWorkflow.TerminalPublished -= MainWindowViewModel_FolderAutoRenameTerminalPublished;
        subscribedViewModel.StartupUpdateWorkflow.PresentationRequested -= MainWindowViewModel_StartupUpdatePresentationRequested;
        subscribedViewModel.StartupUpdateWorkflow.ShutdownPreparationRequested -= MainWindowViewModel_StartupUpdateShutdownPreparationRequested;
        subscribedViewModel.StartupUpdateWorkflow.FailurePresentationRequested -= MainWindowViewModel_StartupUpdateFailurePresentationRequested;
        subscribedViewModel.StartupUpdateWorkflow.ApplicationShutdownRequested -= MainWindowViewModel_StartupUpdateApplicationShutdownRequested;
        subscribedViewModel.ElevatedProcessWarningWorkflow.PresentationRequested -= MainWindowViewModel_ElevatedProcessWarningPresentationRequested;
        subscribedViewModel = null;
    }

    private void MainWindowViewModel_FolderAutoRenameTerminalPublished()
    {
        RefreshCustomTableViewDisplayAsync();
    }

    private void MainWindowViewModel_ElevatedProcessWarningPresentationRequested(ElevatedProcessWarningPresentationRequest request)
    {
        if (request == null)
        {
            return;
        }
        try
        {
            if (!CanPresentElevatedProcessWarning())
            {
                request.Complete(false);
                return;
            }
            UiDialogRoute.ShowMessageBox(
                this,
                BeMusicSeeker.Properties.Resources.Warn_ElevatedProcessDragDropLimited,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
            request.Complete(true);
        }
        catch (Exception exception)
        {
            request.Fail(exception);
        }
    }

    private void MainWindowViewModel_StartupUpdatePresentationRequested(StartupUpdatePresentationRequest request)
    {
        _ = PresentStartupUpdateAsync(request);
    }

    private async Task PresentStartupUpdateAsync(StartupUpdatePresentationRequest request)
    {
        if (request == null)
        {
            return;
        }
        try
        {
            if (_isClosingOrClosed)
            {
                request.Complete(null);
                return;
            }
            UiWindowDialogResult<UpdateAssetInfo> dialogResult = await new UiDialogCoordinator()
                .ShowWindowAsync(new UiWindowDialogRequest<UpdateAvailableDialog, UpdateAssetInfo>(
                    () => new UpdateAvailableDialog(request.Result, (base.DataContext as MainWindowViewModel)?.ProgressHub),
                    dialog => dialog.SelectedAsset,
                    this));
            ThrowIfWindowDialogFailed(dialogResult.Status, dialogResult.Error, "Update available dialog");
            request.Complete(dialogResult.IsAccepted ? dialogResult.Value : null);
        }
        catch (Exception exception)
        {
            request.Fail(exception);
        }
    }

    private void MainWindowViewModel_StartupUpdateShutdownPreparationRequested(StartupUpdateShutdownPreparationRequest request)
    {
        _ = CompleteStartupUpdateShutdownPreparationAsync(request);
    }

    private async Task CompleteStartupUpdateShutdownPreparationAsync(StartupUpdateShutdownPreparationRequest request)
    {
        if (request == null)
        {
            return;
        }
        try
        {
            request.Complete(await EnsureShutdownPreparedAsync(request.Reason).ConfigureAwait(true));
        }
        catch (Exception exception)
        {
            request.Fail(exception);
        }
    }

    private void MainWindowViewModel_StartupUpdateFailurePresentationRequested(Exception exception)
    {
        bool updateShutdownPreparationFailed = _shutdownPreparationRunning
            && !_shutdownPrepared
            && (base.DataContext as MainWindowViewModel)?.StartupUpdateWorkflow.IsShutdownPreparationStarted == true;
        if (_isClosingOrClosed && !updateShutdownPreparationFailed)
        {
            return;
        }
        if (updateShutdownPreparationFailed)
        {
            // Shutdown cancellation has already been issued by the ViewModel and
            // cannot be rolled back safely.  Keep the window in a terminal
            // shutdown-safe state, show the existing failure contract, and let
            // the user close it without attempting the faulted preparation again.
            _shutdownPreparationRunning = false;
            _shutdownPrepared = true;
        }
        UiDialogRoute.ShowMessageBox(
            "Failed to download or start the update.\n" + (exception?.Message ?? string.Empty),
            "Update Failed",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void MainWindowViewModel_StartupUpdateApplicationShutdownRequested()
    {
        _shutdownPrepared = true;
        if (Application.Current != null)
        {
            Application.Current.Shutdown();
        }
        else
        {
            Close();
        }
    }

    private void MainWindowViewModel_SettingDialogOpenRequested(object sender, EventArgs e)
    {
        if (ReferenceEquals(activeOverlayDialog, initialSetupLanguageDialog))
        {
            HideOverlayDialog(initialSetupLanguageDialog);
        }

        ShowOverlayDialog(settingDialog);
    }

    private void MainWindowViewModel_SettingDialogPresentationRequested(
        object sender,
        MainWindowViewModel.SettingDialogViewModel.PresentationRequestedEventArgs request)
    {
        if (request == null)
        {
            return;
        }

        if (request.Kind == MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.CloseOverlay)
        {
            HideOverlayDialog(settingDialog);
        }
        else if (request.Kind == MainWindowViewModel.SettingDialogViewModel.PresentationRequestKind.RefreshAppearanceSelection
            && base.DataContext is MainWindowViewModel viewModel)
        {
            settingDialog.RefreshAppearanceThemeSelection(viewModel.settingDialog);
        }
    }

    private void MainWindowViewModel_InitialSetupLanguageDialogRequested(object sender, EventArgs e)
    {
        ShowOverlayDialog(initialSetupLanguageDialog);
    }

    private void MainWindowViewModel_InitializationSucceeded(object sender, EventArgs e)
    {
        if (sender is MainWindowViewModel viewModel)
        {
            viewModel.PlaybackPanel.AttachParentHandle(playbackPanelView.PlayerHostHandle);
        }
        playbackPanelView.RotatePanelState();
    }

    private void MainWindowViewModel_PlaylistUrlInstallTreeExpansionRequested()
    {
        newlyInstalledTreeViewItem.IsExpanded = true;
    }

    private void playbackPanelViewPlaybackStarting(object sender, RoutedEventArgs e) => scrollIntoView();

    private void playbackPanelViewPlaybackStarted(object sender, RoutedEventArgs e) => RestorePlaybackSurfaceAndFocusTable();

    private void ApplySavedTreeViewWidth()
    {
        double normalizedWidth = Settings.NormalizeTreeViewWidth(Settings.Default.TreeViewWidth);
        Settings.Default.TreeViewWidth = normalizedWidth;
        gridColumn0.Width = new GridLength(normalizedWidth);
    }

    internal static double ResolveTreeViewWidthForSave(double actualColumnWidth, double assignedColumnWidth, double currentSettingWidth)
    {
        if (IsUsableTreeViewWidth(actualColumnWidth))
        {
            return actualColumnWidth;
        }
        if (IsUsableTreeViewWidth(assignedColumnWidth))
        {
            return assignedColumnWidth;
        }
        return Settings.NormalizeTreeViewWidth(currentSettingWidth);
    }

    private static bool IsUsableTreeViewWidth(double width)
    {
        return !double.IsNaN(width) && !double.IsInfinity(width) && width >= Settings.MinTreeViewWidth;
    }

    /// <summary>
    /// アプリケーション起動時に、設定 (StartupSelectInstallPending) に基づいて
    /// プレイリストツリーの「インストール待ち（保留）」ノードを自動的に展開・選択します。
    /// </summary>
    public void ApplyStartupInitialSelectionRequest()
    {
        if (startupInitialSelectionApplied)
        {
            return;
        }
        startupInitialSelectionApplied = true;
        if (!Settings.Default.StartupSelectInstallPending)
        {
            return;
        }
        if (treeViewItemInstallPending != null && treeViewItemInstallPending.IsSelected)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && viewModel.IsStartupUiInteractionBlocked)
        {
            QueueStartupInitialSelectionUntilOperable(viewModel);
            return;
        }
        ApplyStartupInitialSelectionNow();
    }

    private void QueueStartupInitialSelectionUntilOperable(MainWindowViewModel viewModel)
    {
        if (_startupInitialSelectionReadyHandler != null)
        {
            return;
        }
        _startupInitialSelectionReadyHandler = delegate (object _, PropertyChangedEventArgs args)
        {
            if (args == null || args.PropertyName != "IsStartupUiInteractionBlocked" || viewModel.IsStartupUiInteractionBlocked)
            {
                return;
            }
            viewModel.PropertyChanged -= _startupInitialSelectionReadyHandler;
            _startupInitialSelectionReadyHandler = null;
            ApplyStartupInitialSelectionNow();
        };
        viewModel.PropertyChanged += _startupInitialSelectionReadyHandler;
    }

    private void ApplyStartupInitialSelectionNow()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate
        {
            if (_isClosingOrClosed)
            {
                return;
            }
            if (treeViewItemInstall != null)
            {
                treeViewItemInstall.IsExpanded = true;
            }
            if (treeViewItemInstallPending != null)
            {
                treeViewItemInstallPending.IsExpanded = true;
                treeViewItemInstallPending.IsSelected = true;
            }
        });
    }

    /// <summary>
    /// 指定 Visual 配下に存在する特定型の子要素数を数えます。
    /// </summary>
    /// <typeparam name="T">数えたい Visual 型。</typeparam>
    /// <param name="root">探索開始要素。</param>
    /// <param name="maxCount">上限件数。</param>
    /// <returns>見つかった要素数。</returns>
    private static int CountVisualDescendants<T>(DependencyObject root, int maxCount) where T : DependencyObject
    {
        if (root == null || maxCount <= 0)
        {
            return 0;
        }
        int count = 0;
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        while (pending.Count > 0 && count < maxCount)
        {
            DependencyObject current = pending.Dequeue();
            int childCount = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < childCount && count < maxCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(current, i);
                if (child is T)
                {
                    count++;
                }
                if (child != null)
                {
                    pending.Enqueue(child);
                }
            }
        }
        return count;
    }

    private void CloseWindow(object sender, ExecutedRoutedEventArgs e)
    {
        Close();
    }

    private void MinimizeWindow(object sender, ExecutedRoutedEventArgs e)
    {
        base.WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow(object sender, ExecutedRoutedEventArgs e)
    {
        base.WindowState = ((base.WindowState != WindowState.Maximized) ? WindowState.Maximized : WindowState.Normal);
    }

    /// <summary>
    /// メインウィンドウに対してファイルやフォルダーがドラッグ＆ドロップされた際の完了処理イベント。
    /// ドロップされたパス一覧を取得し、譜面ファイルのインストール処理を開始します。
    /// </summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            UiDialogRoute.ShowMessageBox(this, BeMusicSeeker.Properties.Resources.Warn_DropInstallBlockedByPlaylistUrlDownload, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] filePaths)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            string[] pathSnapshot = [.. filePaths];
            if (pathSnapshot.Length > 0)
            {
                viewModel?.PackageInstallWorkflow.Enqueue(pathSnapshot);
                newlyInstalledTreeViewItem.IsExpanded = true;
            }
        }
    }
    /// <summary>
    /// メインウィンドウ内へファイルをドラッグ中（ホバー中）のイベント。
    /// ドロップされたデータがファイル(FileDrop)形式である場合のみ、カーソルエフェクトを「Copy（追加）」に変更します。
    /// </summary>
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            e.Effects = DragDropEffects.None;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }
    /// <summary>
    /// メインウィンドウ上でマウスの左ボタンが押し込まれた際の処理。
    /// 一覧やツリーなどの操作可能要素以外をクリックしたと判定された場合、
    /// ウィンドウ全体をドラッグ移動できるようにします (DragMove)。
    /// </summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!(customTableView?.IsMouseOver ?? false) && !(customTablePlaylistSummary?.IsMouseOver ?? false) && !(treeView?.IsMouseOver ?? false))
        {
            DragMove();
        }
    }
    /// <summary>
    /// ウィンドウの表示状態（最大化、最小化、通常）が変更された際に呼び出され、
    /// ウィンドウ境界のマージンを調整します（最大化時の見切れ防止）。
    /// </summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (((Window)sender).WindowState == WindowState.Maximized)
        {
            windowBorder.Margin = new Thickness(8.0);
        }
        else
        {
            windowBorder.Margin = new Thickness(0.0);
        }
    }

    /// <summary>
    /// ウィンドウの内部リソースとHWNDが初期化された直後に呼び出されます。
    /// パネル状態の整合性確認、WebBrowserコントロールの設定（サイレント化、ドロップ無効化）、
    /// および前回終了時のウィンドウ配置（最大化状態や座標）の復元を行います。
    /// </summary>
    /// <param name="e">イベントデータ。</param>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        playbackPanelView.EnsureSelectedSurfaceAvailable();
        playbackPanelView.ConfigureBrowserHost();
        try
        {
            Win32API.WINDOWPLACEMENT lpwndpl = Settings.Default.WindowPlacement;
            lpwndpl.Length = Marshal.SizeOf(typeof(Win32API.WINDOWPLACEMENT));
            lpwndpl.Flags = 0;
            lpwndpl.ShowCmd = ((lpwndpl.ShowCmd == Win32API.ShowWindowCommands.ShowMinimized) ? Win32API.ShowWindowCommands.Normal : lpwndpl.ShowCmd);
            Win32API.SetWindowPlacement(new WindowInteropHelper(this).Handle, ref lpwndpl);
        }
        catch
        {
        }
    }

    private Task<ShutdownPreparationResult> EnsureShutdownPreparedAsync(string reason)
    {
        lock (shutdownPreparationLock)
        {
            shutdownPreparationTask ??= PrepareShutdownCoreAsync(reason ?? "shutdown");
            return shutdownPreparationTask;
        }
    }

    private async Task<ShutdownPreparationResult> PrepareShutdownCoreAsync(string reason)
    {
        _shutdownPreparationRunning = true;
        _isClosingOrClosed = true;
        App.MarkCoordinatedShutdownStarted(reason);
        MainWindowViewModel viewModel = null;
        if (base.Dispatcher.CheckAccess())
        {
            viewModel = base.DataContext as MainWindowViewModel;
        }
        else
        {
            await base.Dispatcher.InvokeAsync((Action)delegate
            {
                viewModel = base.DataContext as MainWindowViewModel;
            }).Task.ConfigureAwait(false);
        }
        if (viewModel != null)
        {
            ShutdownPreparationResult result = await viewModel.PrepareShutdownAsync(reason).ConfigureAwait(false);
            _shutdownPrepared = true;
            return result;
        }
        _shutdownPrepared = true;
        return new ShutdownPreparationResult(
            reason ?? "shutdown",
            0L,
            slowWaitLogged: false,
            sqliteCloseFailureCount: ShutdownOperationTracker.SqliteCloseFailureCount);
    }

    private async Task CompleteCloseAfterShutdownPreparedAsync(string reason)
    {
        try
        {
            await EnsureShutdownPreparedAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Error(ex, "Failed to prepare shutdown.");
            _shutdownPrepared = true;
        }
        await base.Dispatcher.InvokeAsync((Action)delegate
        {
            if (Application.Current != null)
            {
                Application.Current.Shutdown();
            }
            else
            {
                Close();
            }
        }).Task.ConfigureAwait(false);
    }

    private async Task CompleteCloseAfterStartupUpdateWorkflowAsync(StartupUpdateWorkflowOwner startupUpdateWorkflow)
    {
        try
        {
            await startupUpdateWorkflow.WaitForIdleAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            NLogWrapper.FileLogger?.Error(exception, "Failed to drain startup update workflow before closing.");
        }
        await CompleteCloseAfterShutdownPreparedAsync("window_close").ConfigureAwait(true);
    }

    /// <summary>
    /// ウィンドウが閉じられる直前に呼び出されます。
    /// 現在のUI状態（TreeViewの幅、ウィンドウの配置や最大化状態など）を
    /// ユーザー設定 (Settings.Default) に保存します。
    /// </summary>
    /// <param name="e">キャンセル可能なイベントデータ。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shutdownPrepared)
        {
            e.Cancel = true;
            MainWindowViewModel closingViewModel = base.DataContext as MainWindowViewModel;
            closingViewModel?.ElevatedProcessWarningWorkflow.NotifyClosing();
            if (closingViewModel?.StartupUpdateWorkflow.NotifyClosing() == true)
            {
                _shutdownPreparationRunning = true;
                _isClosingOrClosed = true;
                calcelAllContextMenuTasks();
                CloseContextMenuIfOpen(_lastOpenedContextMenu);
                _ = CompleteCloseAfterStartupUpdateWorkflowAsync(closingViewModel.StartupUpdateWorkflow);
                return;
            }
            if (!_shutdownPreparationRunning)
            {
                _shutdownPreparationRunning = true;
                _isClosingOrClosed = true;
                calcelAllContextMenuTasks();
                CloseContextMenuIfOpen(_lastOpenedContextMenu);
                _ = CompleteCloseAfterShutdownPreparedAsync("window_close");
            }
            return;
        }
        _isClosingOrClosed = true;
        var viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && _startupInitialSelectionReadyHandler != null)
        {
            viewModel.PropertyChanged -= _startupInitialSelectionReadyHandler;
            _startupInitialSelectionReadyHandler = null;
        }
        if (viewModel != null)
        {
            viewModel.PlaylistSummarySelectionRestoreRequested -= MainWindowViewModel_PlaylistSummarySelectionRestoreRequested;
        }
        viewModel?.SetStartupUiInteractionBlocked(false);
        calcelAllContextMenuTasks();
        CloseContextMenuIfOpen(_lastOpenedContextMenu);
        base.OnClosing(e);
        try
        {
            Settings.Default.TreeViewWidth = ResolveTreeViewWidthForSave(
                gridColumn0.ActualWidth,
                gridColumn0.Width.IsAbsolute ? gridColumn0.Width.Value : double.NaN,
                Settings.Default.TreeViewWidth);
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to save tree view width: " + ex.Message);
        }
        try
        {
            Win32API.WINDOWPLACEMENT lpwndpl = default;
            Win32API.GetWindowPlacement(new WindowInteropHelper(this).Handle, ref lpwndpl);
            Settings.Default.WindowPlacement = lpwndpl;
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to save window placement: " + ex.Message);
        }
        try
        {
            if (viewModel != null)
            {
                viewModel.SaveSettingsForShutdown();
            }
            else
            {
                ApplicationComposition.CreateDefault().SaveSettings();
            }
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn("Failed to save settings on closing: " + ex.Message);
        }
    }
    private static void CloseContextMenuIfOpen(ContextMenu contextMenu)
    {
        if (contextMenu != null && contextMenu.IsOpen)
        {
            contextMenu.IsOpen = false;
        }
    }

    private bool ShouldBlockStartupUiInteraction(string action)
    {
        if (_isClosingOrClosed)
        {
            LogStartupUiBlocked(action, "closing");
            return true;
        }
        if (base.DataContext is MainWindowViewModel viewModel && viewModel.IsStartupUiInteractionBlocked)
        {
            LogStartupUiBlocked(action, "startup");
            return true;
        }
        return false;
    }

    private bool ShouldBlockChartPackageMutationInteraction(string action)
    {
        if (base.DataContext is MainWindowViewModel viewModel && viewModel.IsChartPackageMutationInProgress)
        {
            installPerformanceLogger?.Info("chart_package_mutation_ui_blocked action=" + action);
            return true;
        }
        return false;
    }

    private static void LogStartupUiBlocked(string action, string reason)
    {
        installPerformanceLogger?.Info("startup_ui_blocked action=" + action + " reason=" + reason);
    }

    private void customTableView_FirstRenderCompleted(object sender, CustomTableFirstRenderCompletedEventArgs e)
    {
        if (!installPerformanceLoggingEnabled)
        {
            return;
        }
        var stateLogStopwatch = Stopwatch.StartNew();
        var viewModel = base.DataContext as MainWindowViewModel;
        long sourceGenerationId = viewModel?.PlaylistWorkspace.DetailSourceGenerationId ?? 0L;
        long viewGenerationId = viewModel?.PlaylistWorkspace.DetailViewGenerationId ?? 0L;
        TableFirstVisibleTiming timing = default;
        bool hasPlaylistTiming = viewModel != null
            && viewModel.PlaylistWorkspace.TryCreateDetailOpenVisibleTiming(
                sourceGenerationId,
                viewGenerationId,
                out timing);
        if (!hasPlaylistTiming)
        {
            timing = new TableFirstVisibleTiming(-1, -1L, -1L, e.FirstRenderMs, -1L, e.RowCount);
        }
        long stateLogMs = stateLogStopwatch.ElapsedMilliseconds;
        var metrics = new TableFirstVisibleMetrics(
            "CustomTableView",
            "custom_onrender",
            sourceGenerationId,
            viewGenerationId,
            e.RowCount,
            e.VisibleRowCount,
            e.VisibleColumnCount,
            e.VisibleCellCount,
            e.FirstRenderMs,
            e.RenderWorkMs,
            e.TextCacheHitRate,
            stateLogMs,
            timing,
            e.IsPreparationRender);
        installPerformanceLogger.Info(TableFirstVisibleLogFormatter.Format(metrics));
        if (hasPlaylistTiming && !(e.IsPreparationRender && timing.ViewCount > 0))
        {
            viewModel.PlaylistWorkspace.TryLogDetailOpenVisibleCompleted(
                "custom_onrender",
                sourceGenerationId,
                viewGenerationId);
        }
    }

    private void customTableView_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_sort"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        viewModel.MainChartList.RequestSort(e.SortMemberPath, e.Direction);
    }

    private void customTablePlaylistSummary_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_sort"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        viewModel.PlaylistWorkspace.RequestPlaylistSummarySort(e.SortMemberPath, e.Direction);
    }

    private void customTablePlaylistSummary_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!IsPlaylistSummaryBmtSortDropAllowed(e, out _, out _, out int visibleInsertIndex))
        {
            ClearPlaylistSummaryBmtSortDropPreview();
            if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
            return;
        }
        customTablePlaylistSummary?.SetRowDropInsertPreview(visibleInsertIndex);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private async void customTablePlaylistSummary_Drop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        try
        {
            if (!IsPlaylistSummaryBmtSortDropAllowed(e, out List<PlaylistSummaryRow> draggedRows, out PlaylistSummaryRow primaryDraggedRow, out int visibleInsertIndex))
            {
                if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
                {
                    e.Handled = true;
                }
                return;
            }
            e.Handled = true;
            List<PlaylistSummaryRow> visibleRows = GetVisiblePlaylistSummaryRowsSnapshot();
            int? currentPlaylistId = primaryDraggedRow?.PlaylistId ?? draggedRows.FirstOrDefault(row => row?.PlaylistId != null)?.PlaylistId;
            if (base.DataContext is MainWindowViewModel viewModel)
            {
                await viewModel.PlaylistWorkspace.DropSummaryRowsInBmtOrderAsync(
                    visibleRows,
                    draggedRows,
                    visibleInsertIndex,
                    currentPlaylistId).Logging("customTablePlaylistSummary_Drop");
            }
            e.Effects = DragDropEffects.Move;
        }
        finally
        {
            ClearPlaylistSummaryBmtSortDropPreview();
        }
    }

    private void customTablePlaylistSummary_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows))
        {
            ClearPlaylistSummaryBmtSortDropPreview();
        }
    }

    private void ClearPlaylistSummaryBmtSortDropPreview()
    {
        customTablePlaylistSummary?.ClearRowDropInsertPreview();
    }

    private void MainWindowViewModel_PlaylistSummarySelectionRestoreRequested(PlaylistSummarySelectionRestoreRequest request)
    {
        if (request == null)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                new Action(() => ApplyPlaylistSummarySelectionRestoreToView(request)),
                DispatcherPriority.Background);
            return;
        }
        ApplyPlaylistSummarySelectionRestoreToView(request);
    }

    private void ApplyPlaylistSummarySelectionRestoreToView(PlaylistSummarySelectionRestoreRequest request)
    {
        if (customTablePlaylistSummary == null || request == null)
        {
            return;
        }
        HashSet<int> playlistIdSet = new(request.PlaylistIds);
        int? currentPlaylistId = request.CurrentPlaylistId;
        customTablePlaylistSummary.SelectRowsByPredicate(
            row => row is PlaylistSummaryRow playlistSummaryRow
                && playlistSummaryRow.PlaylistId.HasValue
                && playlistIdSet.Contains(playlistSummaryRow.PlaylistId.Value),
            row => currentPlaylistId.HasValue
                && row is PlaylistSummaryRow playlistSummaryRow
                && playlistSummaryRow.PlaylistId == currentPlaylistId);
    }

    private bool IsPlaylistSummaryBmtSortDropAllowed(DragEventArgs e, out List<PlaylistSummaryRow> draggedRows, out PlaylistSummaryRow primaryDraggedRow, out int visibleInsertIndex)
    {
        draggedRows = [];
        primaryDraggedRow = null;
        visibleInsertIndex = -1;
        if (e?.Data == null
            || !CustomTableDataTransfer.HasRowDragKind(e.Data, CustomTableRowDragKind.PlaylistSummaryRows)
            || base.DataContext is not MainWindowViewModel viewModel
            || !CustomTableDataTransfer.TryGetSelectedRows(e.Data, out List<object> selectedRows))
        {
            return false;
        }
        draggedRows = [.. selectedRows.OfType<PlaylistSummaryRow>().Where(row => row?.TableRef != null && row.PlaylistId.HasValue)];
        if (!viewModel.PlaylistWorkspace.CanDropSummaryRowsInBmtOrder(draggedRows))
        {
            return false;
        }
        if (CustomTableDataTransfer.TryGetPrimaryRow(e.Data, out object primaryRow)
            && primaryRow is PlaylistSummaryRow primaryPlaylistSummaryRow
            && draggedRows.Contains(primaryPlaylistSummaryRow))
        {
            primaryDraggedRow = primaryPlaylistSummaryRow;
        }
        primaryDraggedRow ??= draggedRows.FirstOrDefault();
        visibleInsertIndex = ResolvePlaylistSummaryVisibleInsertIndex(e);
        return visibleInsertIndex >= 0;
    }

    private int ResolvePlaylistSummaryVisibleInsertIndex(DragEventArgs e)
    {
        IList rows = customTablePlaylistSummary?.ItemsSource;
        if (rows == null)
        {
            return -1;
        }
        if (rows.Count == 0)
        {
            return 0;
        }
        Point point = e.GetPosition(customTablePlaylistSummary);
        CustomTableHitTestResult hit = customTablePlaylistSummary.HitTestTable(point);
        if (hit.Kind == CustomTableHitKind.Header)
        {
            return 0;
        }
        if (hit.Kind != CustomTableHitKind.Cell)
        {
            if (customTablePlaylistSummary == null || point.X < 0d || point.X >= customTablePlaylistSummary.SurfaceWidth)
            {
                return -1;
            }
            return ResolvePlaylistSummaryVisibleInsertIndexFromPointY(point.Y, rows.Count);
        }
        bool insertAfterTarget = point.Y >= hit.CellRect.Top + (hit.CellRect.Height / 2d);
        return Math.Max(0, Math.Min(rows.Count, hit.RowIndex + (insertAfterTarget ? 1 : 0)));
    }

    private int ResolvePlaylistSummaryVisibleInsertIndexFromPointY(double y, int rowCount)
    {
        if (customTablePlaylistSummary == null || rowCount <= 0)
        {
            return 0;
        }
        if (y < customTablePlaylistSummary.HeaderHeight)
        {
            return 0;
        }
        int firstVisibleRowIndex = customTablePlaylistSummary.FirstVisibleRowIndex;
        int drawableRowCapacity = customTablePlaylistSummary.CalculateDrawableRowCapacity();
        int lastVisibleRowExclusive = Math.Min(rowCount, firstVisibleRowIndex + drawableRowCapacity);
        double rowHeight = Math.Max(1d, customTablePlaylistSummary.RowHeight);
        int rowIndex = firstVisibleRowIndex + (int)Math.Floor((y - customTablePlaylistSummary.HeaderHeight) / rowHeight);
        if (rowIndex < firstVisibleRowIndex)
        {
            return firstVisibleRowIndex;
        }
        if (rowIndex >= lastVisibleRowExclusive)
        {
            return rowCount;
        }
        double rowTop = customTablePlaylistSummary.HeaderHeight + (rowIndex - firstVisibleRowIndex) * rowHeight;
        bool insertAfterTarget = y >= rowTop + rowHeight / 2d;
        return Math.Max(0, Math.Min(rowCount, rowIndex + (insertAfterTarget ? 1 : 0)));
    }

    private List<PlaylistSummaryRow> GetVisiblePlaylistSummaryRowsSnapshot()
    {
        return [.. (customTablePlaylistSummary?.ItemsSource?.OfType<PlaylistSummaryRow>() ?? Enumerable.Empty<PlaylistSummaryRow>())
            .Where(row => row != null)];
    }

    private void customTableView_SelectionChanged(object sender, CustomTableSelectionChangedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            if (viewModel.PlaylistWorkspace.IsPlaylistDetailViewActive)
            {
                NLogWrapper.FileLogger?.Info("custom_table_selection_changed selectedIndex=" + e.SelectedIndex + " selectedCount=" + (e.SelectedRows?.Count ?? 0));
            }
            viewModel.PlaybackPanel.HandleTableSelection(e.SelectedRow);
        }
    }

    private void customTableView_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_row_activate"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (viewModel.PlaybackPanel.HandleTableRowActivation(e.RowIndex, e.Row))
        {
            playbackPanelView.TrySelectBmsPlayerSurface();
        }
    }

    private void customTablePlaylistSummary_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_row_activate"))
        {
            return;
        }
        if (e.Row is PlaylistSummaryRow playlistSummaryRow)
        {
            TrySelectPlaylistTreeItemFromSummary(playlistSummaryRow);
        }
    }

    private void customTableView_RowContextMenuRequested(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_row_context_menu"))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("custom_table_row_context_menu"))
        {
            return;
        }
        if (e.Row == null || !TryGetTableContextMenuResource(e.Row, "custom_table_context_menu_assign", out ContextMenu contextMenu, out _))
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = new CustomTableContextMenuContext(e.Row, e.RowIndex);
        contextMenu.PlacementTarget = customTableView;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTablePlaylistSummary_RowContextMenuRequested(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_context_menu"))
        {
            return;
        }
        if (e.Row is not PlaylistSummaryRow)
        {
            return;
        }
        if (TryFindResource("playlistSummaryContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = new CustomTableContextMenuContext(e.Row, e.RowIndex);
        contextMenu.PlacementTarget = customTablePlaylistSummary;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTableView_HeaderContextMenuRequested(object sender, CustomTableHeaderRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_column_header_context_menu"))
        {
            return;
        }
        CustomTableColumnSettings columnSettings = (customTableView.DataContext as MainChartListViewModel)?.ColumnsSettings;
        if (TryFindResource(ResolveMainColumnHeaderContextMenuResourceKey(columnSettings)) is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = null;
        contextMenu.PlacementTarget = customTableView;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    internal static string ResolveMainColumnHeaderContextMenuResourceKey(CustomTableColumnSettings columnSettings)
    {
        return columnSettings?.Kind == CustomTableColumnSettings.ViewKind.PLAY_HISTORY
            ? "playHistoryColumnHeaderContextMenu"
            : "tableColumnHeaderContextMenu";
    }

    private void customTablePlaylistSummary_HeaderContextMenuRequested(object sender, CustomTableHeaderRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_column_header_context_menu"))
        {
            return;
        }
        if (TryFindResource("playlistSummaryColumnHeaderContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = null;
        contextMenu.PlacementTarget = customTablePlaylistSummary;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void customTablePlaylistSummary_CellEditBeginning(object sender, CustomTableCellEditBeginningEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_cell_edit_beginning"))
        {
            e.Cancel = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel
            || e.Row is not PlaylistSummaryRow playlistSummaryRow
            || playlistSummaryRow.TableRef == null
            || !viewModel.PlaylistWorkspace.CanBeginSummaryPropertyEdit(playlistSummaryRow, e.EditPropertyName))
        {
            e.Cancel = true;
        }
    }

    private void customTableView_CellEditBeginning(object sender, CustomTableCellEditBeginningEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_cell_edit_beginning"))
        {
            e.Cancel = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = !viewModel.MainChartList.TryBeginCellEdit(CreateMainChartListCellEditContext(viewModel, e.Row, e.EditPropertyName));
    }

    private void customTableView_CellEditStarted(object sender, CustomTableCellEditStartedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.MainChartList.NotifyCellEditStarted(e.Row, e.EditPropertyName);
        }
    }

    private async void customTableView_CellActionRequested(object sender, CustomTableCellActionRequestedEventArgs e)
    {
        bool isUrlDiff = string.Equals(e.Column?.Id, "Url2", StringComparison.Ordinal);
        bool isUrl = isUrlDiff || string.Equals(e.Column?.Id, "Url1", StringComparison.Ordinal);
        if (!isUrl)
        {
            return;
        }
        await OpenUrlFromRowAsync(e.Row, isUrlDiff, isUrlDiff ? "custom_table_open_url_diff" : "custom_table_open_url");
    }

    private async void customTablePlaylistSummary_CellActionRequested(object sender, CustomTableCellActionRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_cell_action"))
        {
            return;
        }
        if (e.Row is not PlaylistSummaryRow playlistSummaryRow)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.PlaylistWorkspace.HandlePlaylistSummaryCellActionAsync(
                getSelectedPlaylistSummaryRows(playlistSummaryRow),
                playlistSummaryRow,
                e.Column?.Id).Logging("customTablePlaylistSummary_CellActionRequested");
        }
    }

    private async void customTablePlaylistSummary_CellEditEnded(object sender, CustomTableCellEditEndedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!e.Commit
            || e.Row is not PlaylistSummaryRow playlistSummaryRow
            || playlistSummaryRow.TableRef == null)
        {
            return;
        }

        try
        {
            bool applied = await viewModel.PlaylistWorkspace.ApplySummaryPropertyEditAsync(
                playlistSummaryRow,
                e.EditPropertyName,
                e.Text);
            if (!applied)
            {
                UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_invalid_setting, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                customTablePlaylistSummary?.RefreshDisplay();
            }
        }
        catch (Exception ex)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            customTablePlaylistSummary?.RefreshDisplay();
        }
    }

    private void customTableView_CellEditEnded(object sender, CustomTableCellEditEndedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        viewModel.MainChartList.RequestCellEditEnded(e.Row, e.EditPropertyName, e.Text, e.Commit);
    }

    private static MainChartListCellEditContext CreateMainChartListCellEditContext(
        MainWindowViewModel viewModel,
        object row,
        string propertyName)
    {
        return new MainChartListCellEditContext(
            row,
            propertyName,
            viewModel.CurrentMainViewChartOperationSourceScope,
            viewModel.CurrentMainViewOperationSection);
    }

    private void RefreshCustomTableViewDisplayAsync()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            RefreshCustomTableViewDisplay();
        }, DispatcherPriority.Background);
    }

    private void RefreshCustomTableViewDisplay()
    {
        if (_isClosingOrClosed || customTableView == null)
        {
            return;
        }
        customTableView.RefreshDisplay();
    }
    private void tableInitializeColumnSetting(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("column_setting_initialize"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            e.Handled = true;
            if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_init_column_settings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
            {
                mainWindowViewModel.LoadColumnSetting();
            }
        }
    }

    private void playlistSummaryInitializeColumnSetting(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("playlist_summary_column_setting_initialize"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            e.Handled = true;
            mainWindowViewModel.PlaylistWorkspace.TryResetPlaylistSummaryColumnsToDefault();
        }
    }

    public void scrollIntoView()
    {
        customTableView?.ScrollSelectedRowIntoView();
    }

    private void ClearMainGridSelection()
    {
        customTableView?.ClearSelection();
    }

    private static object _getValueOfPropertyPath(object value, string path)
    {
        if (value == null)
        {
            return null;
        }
        Type type = value.GetType();
        string[] array = path.Split('.');
        foreach (string name in array)
        {
            PropertyInfo property = type.GetProperty(name);
            if (property == null)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Property '{name}' not found on type '{type.Name}' in path '{path}'");
                return null;
            }
            value = property.GetValue(value, null);
            if (value == null)
            {
                return null;
            }
            type = property.PropertyType;
        }
        return value;
    }
    private static Action<T> _getSetterOfPropertyPath<T>(object value, string path)
    {
        if (value == null)
        {
            return _ => { };
        }
        Type type = value.GetType();
        PropertyInfo propertyInfo = null;
        object firstArgument = null;
        string[] array = path.Split('.');
        foreach (string name in array)
        {
            propertyInfo = type.GetProperty(name);
            if (propertyInfo == null)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Property '{name}' not found on type '{type.Name}' in path '{path}'");
                return _ => { };
            }
            firstArgument = value;
            value = propertyInfo.GetValue(value, null);
            if (value == null && name != array.Last())
            {
                return _ => { };
            }
            type = propertyInfo.PropertyType;
        }
        MethodInfo setMethod = propertyInfo?.GetSetMethod();
        if (setMethod == null)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Set method for property '{propertyInfo?.Name}' not found in path '{path}'");
            return _ => { };
        }
        return Delegate.CreateDelegate(typeof(Action<T>), firstArgument, setMethod) as Action<T>;
    }
    /// <summary>
    /// 現在 ViewModel で選択されている（再生中の）BMSファイルの情報を取得し、
    /// BMSPlayerコントロールのプレビュー画像やバナーを最新状態に更新します。
    /// </summary>
    private List<object> GetSelectedGridRowsSnapshot()
    {
        try
        {
            return customTableView?.GetSelectedRowsSnapshot().Where(row => row != null).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private List<ChartOperationTarget> GetSelectedChartTargets(bool isPendingSection = false)
    {
        ChartOperationSourceScope sourceScope = isPendingSection
            ? ChartOperationSourceScope.PendingPackage
            : GetCurrentChartOperationSourceScope();
        return ResolveSelectedChartTargets(sourceScope, ChartOperationCapabilities.None);
    }

    private List<ChartOperationTarget> GetSelectedChartTargets(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        ChartOperationSourceScope sourceScope = isPendingSection
            ? ChartOperationSourceScope.PendingPackage
            : GetCurrentChartOperationSourceScope();
        return ResolveSelectedChartTargets(sourceScope, capability);
    }

    private List<ChartOperationTarget> ResolveSelectedChartTargets(ChartOperationSourceScope sourceScope, ChartOperationCapabilities capability)
    {
        return ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            GetSelectedGridRowsSnapshot(),
            sourceScope,
            capability));
    }

    private ChartOperationSourceScope GetCurrentChartOperationSourceScope()
    {
        return (base.DataContext as MainWindowViewModel)?.CurrentMainViewChartOperationSourceScope ?? ChartOperationSourceScope.Library;
    }

    private MainViewOperationSection GetCurrentMainViewOperationSection()
    {
        return (base.DataContext as MainWindowViewModel)?.CurrentMainViewOperationSection ?? MainViewOperationSection.Library;
    }

    private static bool IsPendingMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.InstallPending;
    }

    private static bool IsInstalledMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.InstallInstalled;
    }

    private static bool IsPlaylistMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.Playlist;
    }

    private static bool IsFullScanMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.FullScanCheck;
    }

    private static bool IsChartInfoParseErrorMainViewSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.ChartInfoParseError;
    }

    private List<ChartFile> GetSelectedBmsFormatCharts(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        return [.. GetSelectedChartTargets(capability, isPendingSection)
            .Select(target => target.Chart)
            .Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private List<BMSFile> GetSelectedBmsFiles(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        return [.. GetSelectedChartTargets(capability, isPendingSection)
            .Where(target => ChartFileKindResolver.IsBmsChartFile(target.Chart))
            .Select(target => target.Chart.GetBmsStorageOwner())
            .Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private List<string> GetSelectedGridHashTargets()
    {
        ChartOperationSourceScope sourceScope = GetCurrentChartOperationSourceScope();
        return [.. GetSelectedGridRowsSnapshot()
            .Select(row =>
            {
                GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target);
                return target;
            })
            .Where(target => target?.HasCapability(ChartOperationCapabilities.UseLr2Ir) == true)
            .Select(target => target.Chart?.Md5)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private List<string> GetSelectedChartInfoParseFailureMd5s()
    {
        return [.. GetSelectedChartTargets()
            .Select(target => target.Chart?.Md5)
            .Where(md5 => !string.IsNullOrWhiteSpace(md5))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static bool ShouldShowChartInfoParseFailureRemovalMenu(bool isChartInfoParseErrorSection, IEnumerable<string> selectedMd5s)
    {
        return isChartInfoParseErrorSection && (selectedMd5s ?? []).Any(md5 => !string.IsNullOrWhiteSpace(md5));
    }

    internal static bool ShouldShowResourceHealthContextMenu(bool isPlaylistContext, IEnumerable<ChartOperationTarget> selectedTargets)
    {
        return ChartContextMenuStateBuilder.ShouldShowResourceHealthContextMenu(isPlaylistContext, selectedTargets);
    }

    internal static bool ShouldUsePlaylistMissingContextMenu(object row, ChartOperationSourceScope sourceScope)
    {
        return TryResolveTableContextMenuPolicy(row, sourceScope, out bool usePlaylistMissingContextMenu)
            && usePlaylistMissingContextMenu;
    }

    internal static bool TryResolveTableContextMenuPolicy(object row, ChartOperationSourceScope sourceScope, out bool usePlaylistMissingContextMenu)
    {
        usePlaylistMissingContextMenu = false;
        if (row == null || row is PlayHistoryRow)
        {
            return false;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target))
        {
            return false;
        }
        usePlaylistMissingContextMenu = target.IsPlaylistMissing;
        return true;
    }

    private List<ScoreViewerTarget> GetSelectedGridScoreViewerTargets()
    {
        return [.. GetSelectedGridRowsSnapshot().Select(TryCreateScoreViewerTarget).Where(target => target != null)];
    }

    private ScoreViewerTarget TryCreateScoreViewerTarget(object row)
    {
        if (!GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target) || !target.HasCapability(ChartOperationCapabilities.UseScoreViewer))
        {
            return null;
        }
        string hash = target.Chart.Md5;
        if (string.IsNullOrWhiteSpace(hash))
        {
            return null;
        }
        return new ScoreViewerTarget(hash, target.Chart.Path, target.Chart.Title);
    }

    private List<BMSTableEntry> GetSelectedGridPlaylistEntries()
    {
        return [.. GetSelectedGridRowsSnapshot().Select(GridRowResolver.GetPlaylistEntry).Where(entry => entry != null)];
    }

    private static ContextMenu GetOwningContextMenu(object source)
    {
        object current = source;
        while (current != null)
        {
            if (current is ContextMenu contextMenu)
            {
                return contextMenu;
            }
            if (current is MenuItem menuItem)
            {
                current = menuItem.Parent;
                continue;
            }
            if (current is FrameworkElement frameworkElement)
            {
                current = frameworkElement.Parent;
                continue;
            }
            break;
        }
        return null;
    }

    private bool TryGetContextMenuRow(object source, out ContextMenu contextMenu, out object row)
    {
        contextMenu = GetOwningContextMenu(source);
        row = null;
        if (contextMenu?.PlacementTarget is not FrameworkElement placementTarget)
        {
            return false;
        }
        if (contextMenu.Tag is CustomTableContextMenuContext customTableContext)
        {
            row = customTableContext.Row;
            return row != null;
        }
        row = placementTarget.DataContext;
        return row != null;
    }

    private bool TryGetContextMenuRow(object source, out object row)
    {
        return TryGetContextMenuRow(source, out _, out row);
    }

    private bool TryGetPlayHistoryContextMenuState(object row, out PlayHistoryContextMenuState state)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            return viewModel.PlayHistory.TryCreateContextMenuState(row, out state);
        }
        state = null;
        return false;
    }

    private bool TryGetPlayHistoryContextMenuAction(
        object source,
        PlayHistoryContextMenuActionKind actionKind,
        out PlayHistoryContextMenuAction action)
    {
        if (TryGetContextMenuRow(source, out object row)
            && base.DataContext is MainWindowViewModel viewModel)
        {
            return viewModel.PlayHistory.TryCreateContextMenuAction(row, actionKind, out action);
        }
        action = null;
        return false;
    }

    private bool TryGetContextMenuChartTarget(object primarySource, object fallbackSource, out ChartOperationTarget target)
    {
        if (TryGetContextMenuRow(primarySource, out object row)
            && GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out target))
        {
            return true;
        }
        if (!ReferenceEquals(primarySource, fallbackSource)
            && TryGetContextMenuRow(fallbackSource, out row)
            && GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out target))
        {
            return true;
        }
        target = null;
        return false;
    }

    private bool TryGetTableContextMenuResource(object row, string logPrefix, out ContextMenu contextMenu, out bool usePlaylistMissingContextMenu)
    {
        contextMenu = null;
        usePlaylistMissingContextMenu = false;
        if (row == null)
        {
            return false;
        }
        string resourceKey;
        if (TryGetPlayHistoryContextMenuState(row, out _))
        {
            resourceKey = "playHistoryContextMenu";
            usePlaylistMissingContextMenu = false;
        }
        else if (row is PlayHistoryRow)
        {
            return false;
        }
        else if (!TryResolveTableContextMenuPolicy(row, GetCurrentChartOperationSourceScope(), out usePlaylistMissingContextMenu))
        {
            return false;
        }
        else
        {
            resourceKey = usePlaylistMissingContextMenu ? "tableContextMenuPlaylistMissing" : "tableContextMenu";
        }
        if (TryFindResource(resourceKey) is not ContextMenu foundContextMenu)
        {
            return false;
        }
        contextMenu = foundContextMenu;
        NLogWrapper.FileLogger?.Info(logPrefix + " rowType=" + row?.GetType().FullName + " missing=" + usePlaylistMissingContextMenu + " resourceKey=" + resourceKey);
        return true;
    }
    private void keywordSearchBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            RefreshKeywordSearchSuggestions(textBox, forceHistory: false);
        }
    }

    private void keywordSearchBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox && string.IsNullOrWhiteSpace(textBox.Text))
        {
            // NOTE:
            // Popup を focus event の処理中に開くと、StaysOpen=false の外部 focus 判定で
            // 即時に閉じる環境があるため、TextBox の focus が確定してから履歴を表示します。
            Dispatcher.BeginInvoke((Action)delegate
            {
                if (textBox.IsKeyboardFocusWithin && string.IsNullOrWhiteSpace(textBox.Text))
                {
                    RefreshKeywordSearchSuggestions(textBox, forceHistory: true);
                }
            }, DispatcherPriority.Input);
        }
    }

    private void keywordSearchBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            CommitKeywordSearchHistory(textBox);
            CloseKeywordSearchSuggestions(IsPlaylistSummaryKeywordSearchBox(textBox));
        }
    }

    private void keywordSearchWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var clickedElement = e.OriginalSource as DependencyObject;
        CloseKeywordSearchSuggestionsIfOutside(clickedElement, false);
        CloseKeywordSearchSuggestionsIfOutside(clickedElement, true);
    }

    private void MainWindow_Deactivated(object sender, EventArgs e)
    {
        CloseKeywordSearchSuggestions(false);
        CloseKeywordSearchSuggestions(true);
    }

    private void keywordSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }
        bool isPlaylistSummary = IsPlaylistSummaryKeywordSearchBox(textBox);
        bool isPopupOpen = IsKeywordSearchSuggestionPopupOpen(isPlaylistSummary);
        if (e.Key == Key.Escape && isPopupOpen)
        {
            CloseKeywordSearchSuggestions(isPlaylistSummary);
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.Space)
        {
            RefreshKeywordSearchSuggestions(textBox, forceHistory: true);
            FocusFirstKeywordSearchSuggestion(isPlaylistSummary);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (!isPopupOpen)
            {
                RefreshKeywordSearchSuggestions(textBox, forceHistory: true);
            }
            NavigateKeywordSearchSuggestion(isPlaylistSummary, e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Enter || e.Key == Key.Tab) && isPopupOpen)
        {
            if (ApplySelectedKeywordSearchSuggestion(textBox))
            {
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Enter)
        {
            CommitKeywordSearchHistory(textBox);
        }
    }

    private void keywordSearchSuggestionPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }
        var listBoxItem = ItemsControl.ContainerFromElement(listBox, e.OriginalSource as DependencyObject) as ListBoxItem;
        KeywordSearchSuggestionItem suggestion = listBoxItem?.DataContext as KeywordSearchSuggestionItem ?? listBox.SelectedItem as KeywordSearchSuggestionItem;
        TextBox textBox = ReferenceEquals(listBox, PlaylistSummaryKeywordSearchSuggestionListBox) ? KeywordSearchBoxPlaylistSummary : KeywordSearchBox;
        if (suggestion != null && ApplyKeywordSearchSuggestion(textBox, suggestion))
        {
            e.Handled = true;
        }
    }

    private void keywordSearchSuggestionPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }
        bool isPlaylistSummary = ReferenceEquals(listBox, PlaylistSummaryKeywordSearchSuggestionListBox);
        TextBox textBox = isPlaylistSummary ? KeywordSearchBoxPlaylistSummary : KeywordSearchBox;
        if (e.Key == Key.Enter || e.Key == Key.Tab)
        {
            if (ApplySelectedKeywordSearchSuggestion(textBox))
            {
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            CloseKeywordSearchSuggestions(isPlaylistSummary);
            textBox?.Focus();
            e.Handled = true;
        }
    }

    private void RefreshKeywordSearchSuggestions(TextBox textBox, bool forceHistory)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || textBox == null)
        {
            return;
        }
        if (IsPlaylistSummaryKeywordSearchBox(textBox))
        {
            viewModel.PlaylistWorkspace.RefreshPlaylistSummaryKeywordSearchSuggestions(
                textBox.Text,
                textBox.CaretIndex,
                forceHistory);
        }
        else
        {
            viewModel.RefreshKeywordSearchSuggestions(textBox.Text, textBox.CaretIndex, forceHistory);
        }
    }

    private void CommitKeywordSearchHistory(TextBox textBox)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || textBox == null)
        {
            return;
        }
        if (IsPlaylistSummaryKeywordSearchBox(textBox))
        {
            viewModel.PlaylistWorkspace.CommitPlaylistSummaryKeywordSearchHistory(textBox.Text);
        }
        else
        {
            viewModel.CommitKeywordSearchHistory(textBox.Text);
        }
    }

    private bool ApplySelectedKeywordSearchSuggestion(TextBox textBox)
    {
        bool isPlaylistSummary = IsPlaylistSummaryKeywordSearchBox(textBox);
        ListBox listBox = GetKeywordSearchSuggestionListBox(isPlaylistSummary);
        var suggestion = listBox?.SelectedItem as KeywordSearchSuggestionItem;
        if (suggestion == null && listBox?.Items.Count > 0)
        {
            suggestion = listBox.Items[0] as KeywordSearchSuggestionItem;
        }
        return suggestion != null && ApplyKeywordSearchSuggestion(textBox, suggestion);
    }

    private bool ApplyKeywordSearchSuggestion(TextBox textBox, KeywordSearchSuggestionItem suggestion)
    {
        if (textBox == null || suggestion == null)
        {
            return false;
        }
        string appliedText = suggestion.Apply(textBox.Text, out int caretIndex);
        textBox.Text = appliedText;
        textBox.CaretIndex = Math.Max(0, Math.Min(caretIndex, textBox.Text.Length));
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        bool isPlaylistSummary = IsPlaylistSummaryKeywordSearchBox(textBox);
        if (suggestion.Kind == KeywordSearchSuggestionKind.History)
        {
            CommitKeywordSearchHistory(textBox);
        }
        CloseKeywordSearchSuggestions(isPlaylistSummary);
        textBox.Focus();
        return true;
    }

    private void NavigateKeywordSearchSuggestion(bool isPlaylistSummary, int delta)
    {
        ListBox listBox = GetKeywordSearchSuggestionListBox(isPlaylistSummary);
        if (listBox == null || listBox.Items.Count == 0)
        {
            return;
        }
        int selectedIndex = listBox.SelectedIndex;
        if (selectedIndex < 0)
        {
            selectedIndex = delta >= 0 ? 0 : listBox.Items.Count - 1;
        }
        else
        {
            selectedIndex += delta;
            if (selectedIndex < 0)
            {
                selectedIndex = listBox.Items.Count - 1;
            }
            else if (selectedIndex >= listBox.Items.Count)
            {
                selectedIndex = 0;
            }
        }
        listBox.SelectedIndex = selectedIndex;
        listBox.ScrollIntoView(listBox.SelectedItem);
        listBox.Focus();
    }

    private void FocusFirstKeywordSearchSuggestion(bool isPlaylistSummary)
    {
        ListBox listBox = GetKeywordSearchSuggestionListBox(isPlaylistSummary);
        if (listBox == null || listBox.Items.Count == 0)
        {
            return;
        }
        listBox.SelectedIndex = 0;
        listBox.Focus();
    }

    private ListBox GetKeywordSearchSuggestionListBox(bool isPlaylistSummary)
    {
        return isPlaylistSummary ? PlaylistSummaryKeywordSearchSuggestionListBox : KeywordSearchSuggestionListBox;
    }

    private bool IsPlaylistSummaryKeywordSearchBox(TextBox textBox)
    {
        return ReferenceEquals(textBox, KeywordSearchBoxPlaylistSummary);
    }

    private bool IsKeywordSearchSuggestionPopupOpen(bool isPlaylistSummary)
    {
        return base.DataContext is MainWindowViewModel viewModel
            && (isPlaylistSummary ? viewModel.PlaylistWorkspace.IsPlaylistSummaryKeywordSearchSuggestionPopupOpen : viewModel.IsKeywordSearchSuggestionPopupOpen);
    }

    private void CloseKeywordSearchSuggestions(bool isPlaylistSummary)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (isPlaylistSummary)
        {
            viewModel.PlaylistWorkspace.ClosePlaylistSummaryKeywordSearchSuggestions();
        }
        else
        {
            viewModel.CloseKeywordSearchSuggestions();
        }
    }

    private void CloseKeywordSearchSuggestionsIfOutside(DependencyObject clickedElement, bool isPlaylistSummary)
    {
        if (!IsKeywordSearchSuggestionPopupOpen(isPlaylistSummary))
        {
            return;
        }
        TextBox textBox = isPlaylistSummary ? KeywordSearchBoxPlaylistSummary : KeywordSearchBox;
        ListBox listBox = GetKeywordSearchSuggestionListBox(isPlaylistSummary);
        if (IsDescendantOf(clickedElement, textBox) || IsDescendantOf(clickedElement, listBox))
        {
            return;
        }
        CloseKeywordSearchSuggestions(isPlaylistSummary);
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        if (child == null || ancestor == null)
        {
            return false;
        }
        DependencyObject current = child;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
            DependencyObject visualParent = current is Visual || current is Visual3D
                ? VisualTreeHelper.GetParent(current)
                : null;
            current = visualParent ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
    private static T FindTemplateElement<T>(FrameworkElement source, string elementName) where T : class
    {
        FrameworkElement current = source;
        while (current != null)
        {
            if (current.FindName(elementName) is T found)
            {
                return found;
            }
            current = current.Parent as FrameworkElement;
        }
        return null;
    }
    private static T FindNamedDescendant<T>(FrameworkElement root, string elementName) where T : class
    {
        if (root == null)
        {
            return null;
        }
        if (root.FindName(elementName) is T foundByName)
        {
            return foundByName;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childrenCount; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement child)
            {
                T found = FindNamedDescendant<T>(child, elementName);
                if (found != null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static T FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null)
        {
            return null;
        }
        int childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childrenCount; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typedChild)
            {
                return typedChild;
            }
            T descendant = FindVisualDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    private async Task OpenUrlFromRowAsync(object row, bool isDiffUrl, string blockReason, MouseButtonEventArgs mouseEventArgs = null)
    {
        if (ShouldBlockStartupUiInteraction(blockReason))
        {
            if (mouseEventArgs != null)
            {
                mouseEventArgs.Handled = true;
            }
            return;
        }
        await OpenSinglePlaylistUrlAsync(row, isDiffUrl);
    }

    private async Task OpenSinglePlaylistUrlAsync(object row, bool isDiffUrl)
    {
        Uri url = isDiffUrl ? GridRowResolver.GetUrlDiff(row) : GridRowResolver.GetUrl(row);
        if (url == null || !url.IsAbsoluteUri)
        {
            return;
        }
        await OpenSinglePlaylistUrlAsync(url);
    }

    private async Task OpenSinglePlaylistUrlAsync(Uri url)
    {
        if (url == null || !url.IsAbsoluteUri)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.PlaylistWorkspace.OpenSinglePlaylistUrlAsync(url);
        }
    }

    private void playlistRootSelect(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ShouldBlockStartupUiInteraction("tree_playlist_root_select"))
        {
            return;
        }
        if (e.Source is TreeViewItem)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            viewModel.PlaylistWorkspace.RequestSummarySelection();
        }
    }

    /// <summary>
    /// メインツリーやプレイリストツリー上で左クリックが行われた際の処理をハンドリングします。
    /// Node（TreeViewItem）の選択状態を判定し、クリックされた要素に応じてViewModel側に
    /// イベント（曲一覧の再生成等）を透過処理します。
    /// </summary>
    private void treeViewLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeView treeViewControl)
        {
            return;
        }
        // スクロールバーやExpanderトグルのクリックではフォーカス移譲や更新を行わない
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) != null ||
            FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) != null)
        {
            return;
        }
        treeViewControl.Focus();
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        TreeViewItem treeViewItem = FindAncestor<TreeViewItem>(source);
        if (treeViewItem == null || !treeViewItem.IsSelected)
        {
            return;
        }
        if (treeViewControl == treeViewPlaylist)
        {
            ForceRefreshPlaylistTreeSelection(treeViewItem);
        }
        else if (treeViewControl == treeView)
        {
            ForceRefreshMainTreeSelection(treeViewItem);
        }
    }

    private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T result)
            {
                return result;
            }
            current = GetParentObject(current);
        }
        return null;
    }

    /// <summary>
    /// VisualTree を子方向に探索し、最初に見つかった指定型の要素を返します。
    /// </summary>
    /// <typeparam name="T">検索対象の <see cref="DependencyObject"/> 型。</typeparam>
    /// <param name="parent">探索開始位置。</param>
    /// <returns>最初に見つかった要素。見つからない場合は <see langword="null"/>。</returns>
    private static T FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            return null;
        }
        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(parent);
        }
        catch
        {
            return null;
        }
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, childIndex);
            if (child is T result)
            {
                return result;
            }
            T descendant = FindDescendant<T>(child);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    private static DependencyObject GetParentObject(DependencyObject current)
    {
        if (current == null)
        {
            return null;
        }
        if (current is FrameworkContentElement frameworkContentElement)
        {
            return frameworkContentElement.Parent;
        }
        if (current is ContentElement)
        {
            return LogicalTreeHelper.GetParent(current);
        }
        try
        {
            return VisualTreeHelper.GetParent(current);
        }
        catch
        {
            return LogicalTreeHelper.GetParent(current);
        }
    }

    private static TreeViewItem GetSelectedTreeViewItem(ItemsControl parent)
    {
        if (parent == null)
        {
            return null;
        }
        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem treeViewItem)
            {
                continue;
            }
            if (treeViewItem.IsSelected)
            {
                return treeViewItem;
            }
            TreeViewItem selectedTreeViewItem = GetSelectedTreeViewItem(treeViewItem);
            if (selectedTreeViewItem != null)
            {
                return selectedTreeViewItem;
            }
        }
        return null;
    }

    /// <summary>
    /// UI仮想化などの影響で対象となる子要素(TreeViewItem)のコンテナが未生成の場合でも、
    /// ツリーの選択状態が無選択（LostFocus）で停止してしまわないように、最も近い兄弟ノードまたは
    /// ルートノードへ選択状態をフォールバック遷移させます。
    /// </summary>
    /// <param name="rootTreeViewItem">フォールバックの起点となる親（ルート）要素。</param>
    /// <param name="currentItem">現在選択されていたが削除等により遷移が必要なデータ項目。</param>
    /// <param name="logScope">ログ出力用のスコープ名。</param>
    private void SelectNextSiblingOrRoot(TreeViewItem rootTreeViewItem, object currentItem, string logScope)
    {
        if (rootTreeViewItem == null)
        {
            return;
        }
        int count = rootTreeViewItem.Items.Count;
        if (count == 0)
        {
            SelectTreeViewItemWithFocus(rootTreeViewItem);
            return;
        }
        int currentIndex = rootTreeViewItem.Items.IndexOf(currentItem);
        if (currentIndex == 0 && count == 1)
        {
            SelectTreeViewItemWithFocus(rootTreeViewItem);
            return;
        }
        if (currentIndex == -1)
        {
            NLogWrapper.FileLogger?.Info(logScope + " selection_fallback reason=current_not_found root=" + rootTreeViewItem.Header);
            SelectTreeViewItemWithFocus(rootTreeViewItem);
            return;
        }
        int targetIndex = ((count - 1 == currentIndex) ? (currentIndex - 1) : (currentIndex + 1));
        object targetDataContext = rootTreeViewItem.Items[targetIndex];
        if (!TrySelectChildTreeViewItemByDataContext(rootTreeViewItem, targetDataContext, logScope))
        {
            SelectTreeViewItemWithFocus(rootTreeViewItem);
        }
    }

    private bool TrySelectChildTreeViewItemByDataContext(TreeViewItem rootTreeViewItem, object targetDataContext, string logScope)
    {
        if (rootTreeViewItem == null || targetDataContext == null)
        {
            return false;
        }
        if (rootTreeViewItem.ItemContainerGenerator.ContainerFromItem(targetDataContext) is not TreeViewItem treeViewItem)
        {
            rootTreeViewItem.UpdateLayout();
            treeViewItem = rootTreeViewItem.ItemContainerGenerator.ContainerFromItem(targetDataContext) as TreeViewItem;
        }
        treeViewItem ??= WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(rootTreeViewItem, targetDataContext);
        if (treeViewItem == null)
        {
            NLogWrapper.FileLogger?.Info(logScope + " selection_fallback reason=container_not_realized root=" + rootTreeViewItem.Header + " target=" + targetDataContext);
            return false;
        }
        SelectTreeViewItemWithFocus(treeViewItem);
        return true;
    }

    private static void SelectTreeViewItemWithFocus(TreeViewItem treeViewItem)
    {
        if (treeViewItem == null)
        {
            return;
        }
        treeViewItem.IsSelected = true;
        treeViewItem.Focus();
    }

    /// <summary>
    /// プレイリストツリー項目の <c>DataContext</c> を、
    /// <see cref="PlaylistFolderNode"/> として安全に扱えるか判定します。
    /// </summary>
    /// <param name="dataContext">判定対象の DataContext。</param>
    /// <param name="folderNode">変換に成功した場合のノード。</param>
    /// <returns><see cref="PlaylistFolderNode"/> として扱える場合は <c>true</c>。</returns>
    private static bool TryGetPlaylistFolderNode(object dataContext, out PlaylistFolderNode folderNode)
    {
        folderNode = dataContext as PlaylistFolderNode;
        return folderNode != null;
    }

    private void ForceRefreshPlaylistTreeSelection(TreeViewItem selectedItem)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || selectedItem == null)
        {
            return;
        }
        if (selectedItem == treeViewItemPlaylist)
        {
            viewModel.PlaylistWorkspace.RequestSummarySelection();
            return;
        }
        BMSTable bmsTable = null;
        PlaylistFolderNode selectedFolderNode = null;
        if (selectedItem.DataContext is BMSTable bMSTable2)
        {
            bmsTable = bMSTable2;
        }
        else
        {
            if (!TryGetPlaylistFolderNode(selectedItem.DataContext, out PlaylistFolderNode folderNode))
            {
                return;
            }
            selectedFolderNode = folderNode;
            TreeViewItem ancestor = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(selectedItem));
            while (ancestor != null && ancestor.DataContext is not BMSTable)
            {
                ancestor = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(ancestor));
            }
            bmsTable = ancestor?.DataContext as BMSTable;
        }
        if (bmsTable != null)
        {
            viewModel.PlaylistWorkspace.RequestDetailSelection(bmsTable, selectedFolderNode);
        }
    }

    private void ForceRefreshMainTreeSelection(TreeViewItem selectedItem)
    {
        if (selectedItem == null)
        {
            return;
        }

        // 下段ツリーはノード種別ごとの Selected ハンドラを再利用して更新する
        // (ライブラリ/インストール/メンテナンスを一律にカバーする)
        selectedItem.RaiseEvent(new RoutedEventArgs(TreeViewItem.SelectedEvent, selectedItem));
    }

    /// <summary>
    /// プレイリストツリー内の「フォルダ」上でキーボード操作が行われた際のイベントハンドラ。
    /// F2キー押下時に、該当フォルダの名称編集モード (EditableTextBlockの切り替え) を起動します。
    /// </summary>
    private void playlistTableFolderkeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && sender is TreeViewItem treeViewItem && treeViewItem.Template.FindName("PART_Header", treeViewItem) is ContentPresenter templatedParent && treeViewItem.HeaderTemplate.FindName("etbPlaylistTableFolder", templatedParent) is EditableTextBlock editableTextBlock)
        {
            if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) && !folderNode.IsEditable)
            {
                return;
            }
            editableTextBlock.IsInEditMode = true;
            e.Handled = true;
        }
    }

    private async void playlistTableFolderClicked(object sender, MouseButtonEventArgs e)
    {
        if (!(e.ChangedButton == MouseButton.Left) || !(sender is EditableTextBlock { TemplatedParent: ContentPresenter contentPresenter } editableTextBlock))
        {
            return;
        }
        if (contentPresenter.TemplatedParent is not TreeViewItem ownerTreeViewItem || !ownerTreeViewItem.IsSelected)
        {
            return;
        }
        if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) && !folderNode.IsEditable)
        {
            return;
        }

        object originalEditableDataContext = editableTextBlock.DataContext;
        object originalHeaderDataContext = contentPresenter.DataContext;
        await Task.Delay(1000);

        // NOTE:
        // Recycling有効時は待機中にコンテナ再利用が起きるため、編集開始前に同一対象か再検証する。
        // 再利用済みなら誤った行の編集開始を避けるため何もしない。
        if (!ReferenceEquals(editableTextBlock.DataContext, originalEditableDataContext) || !ReferenceEquals(contentPresenter.DataContext, originalHeaderDataContext) || !ownerTreeViewItem.IsSelected)
        {
            return;
        }
        if (TryGetPlaylistFolderNode(editableTextBlock.DataContext, out folderNode) && !folderNode.IsEditable)
        {
            return;
        }
        editableTextBlock.IsInEditMode = true;
    }

    private void playlistTableFolderNameChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not EditableTextBlock editableTextBlock || !editableTextBlock.IsTextChanged())
        {
            return;
        }
        if (!(editableTextBlock.TemplatedParent is ContentPresenter { TemplatedParent: TreeViewItem templatedParent }))
        {
            return;
        }
        DependencyObject parent = VisualTreeHelper.GetParent(templatedParent);
        while (parent is not TreeViewItem && parent is not TreeView && parent != null)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (parent == null || parent is TreeView)
        {
            return;
        }
        if ((parent as TreeViewItem).DataContext is not BMSTable bmsTable)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            if (!TryGetPlaylistFolderNode(templatedParent.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsEditable)
            {
                return;
            }
            string nameAfter = editableTextBlock.Text;
            viewModel.PlaylistWorkspace
                .RenameFolderAsync(bmsTable, folderNode, nameAfter)
                .Logging("playlistTableFolderNameChanged");
        }
    }

    private void playlistTableSelected(object sender, RoutedEventArgs e)
    {
        var treeViewItem3 = e.OriginalSource as TreeViewItem;
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        if (treeViewItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        e.Handled = true;
        PlaylistFolderNode selectedFolderNode;
        if (e.Source is TreeViewItem treeViewItem2)
        {
            selectedFolderNode = null;
        }
        else
        {
            if (!TryGetPlaylistFolderNode(treeViewItem3.DataContext, out PlaylistFolderNode folderNode))
            {
                return;
            }
            selectedFolderNode = folderNode;
        }
        e.Handled = true;
        viewModel.PlaylistWorkspace.RequestDetailSelection(bmsTable, selectedFolderNode);
    }

    private void treeViewItemOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock reference)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(reference);
            while (parent is not TreeViewItem && parent is not TreeView && parent != null)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            if (parent != null && parent is TreeViewItem && ((TreeViewItem)parent).Focusable && !((TreeViewItem)parent).IsSelected)
            {
                ((TreeViewItem)parent).IsSelected = true;
            }
        }
    }

    private void mouseRightButtonDown(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem treeViewItem)
        {
            treeViewItem.IsSelected = true;
        }
    }

    private void directoryFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_directory_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel
            && e.Source is TreeViewItem treeViewItem)
        {
            viewModel.RegularChartList.NavigateTree(
                RegularChartFolderFilterKind.Directory,
                treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private void playHistoryPeriodSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_play_history_period_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel
            || sender is not TreeViewItem treeRoot
            || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        PlayHistoryPeriodRequest request;
        if (treeViewItem.DataContext is PlayHistoryPeriodTreeItem periodTreeItem)
        {
            request = periodTreeItem.Request;
        }
        else if (ReferenceEquals(treeRoot, treeViewItem))
        {
            request = PlayHistoryPeriodRequest.All();
        }
        else if (treeViewItem.Tag is string tag)
        {
            request = PlayHistoryPeriodRequest.FromTag(tag);
        }
        else
        {
            treeRoot.IsSelected = true;
            treeRoot.IsExpanded = true;
            return;
        }
        if (request == null)
        {
            return;
        }
        viewModel.PlayHistory.ActivatePeriod(
            request,
            viewModel.ChartFilters.KeywordFilter);
        treeRoot.IsExpanded = true;
    }

    private void artistFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_artist_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel
            && e.Source is TreeViewItem treeViewItem)
        {
            viewModel.RegularChartList.NavigateTree(
                RegularChartFolderFilterKind.Artist,
                treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private void rootFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_root_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel
            && e.OriginalSource is TreeViewItem)
        {
            viewModel.RegularChartList.NavigateTree(filterKind: null);
        }
    }

    private List<PlaylistSummaryRow> getSelectedPlaylistSummaryRows(PlaylistSummaryRow fallback = null)
    {
        List<PlaylistSummaryRow> list = [];
        if (customTablePlaylistSummary != null && customTablePlaylistSummary.IsVisible)
        {
            list = [.. customTablePlaylistSummary.GetSelectedRowsSnapshot().OfType<PlaylistSummaryRow>().Where(r => r != null)];
        }
        if ((list == null || list.Count == 0) && fallback != null)
        {
            list = [fallback];
        }
        return list ?? [];
    }

    private PlaylistSummaryRow resolvePlaylistSummaryRowFromSender(object sender)
    {
        if (TryGetContextMenuRow(sender, out object row) && row is PlaylistSummaryRow contextRow)
        {
            return contextRow;
        }
        if (sender is FrameworkElement { DataContext: PlaylistSummaryRow playlistSummaryRow })
        {
            return playlistSummaryRow;
        }
        return getSelectedPlaylistSummaryRows().FirstOrDefault();
    }

    /// <summary>
    /// プレイリストサマリー行のダブルクリック時に、対応するプレイリストを左ツリーで選択します。
    /// 既存のツリー選択イベントを再利用し、プレイリスト絞り込み表示への遷移も従来の選択経路に委ねます。
    /// </summary>
    /// <summary>
    /// プレイリストサマリー行に対応するプレイリストをプレイリストツリー上で選択します。
    /// 再読み込み後に <see cref="PlaylistSummaryRow.TableRef"/> が古い参照になっていても、既存の再選択補助ロジックで解決を試みます。
    /// </summary>
    /// <param name="playlistSummaryRow">選択元のプレイリストサマリー行。</param>
    /// <returns>プレイリストツリー項目の選択に成功した場合は <see langword="true"/>。</returns>
    private bool TrySelectPlaylistTreeItemFromSummary(PlaylistSummaryRow playlistSummaryRow)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return false;
        }
        BMSTable selectionTarget =
            viewModel.PlaylistWorkspace.ResolveActivePlaylistSummaryTable(playlistSummaryRow);
        string playlistName = selectionTarget?.name ?? playlistSummaryRow.Name ?? string.Empty;
        string playlistId = playlistSummaryRow.PlaylistId?.ToString() ?? string.Empty;
        if (selectionTarget == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=false reason=target_not_found table=" + playlistName + " playlistId=" + playlistId);
            return false;
        }
        bool selected = TrySelectPlaylistTreeItem(selectionTarget, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable);
        if (!selected)
        {
            NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=false reason=container_not_realized table=" + playlistName + " playlistId=" + playlistId + " realize_by_index_available=" + realizeByIndexAvailable);
            return false;
        }
        NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=true table=" + playlistName + " playlistId=" + playlistId + " usedVirtualizationFallback=" + usedVirtualizationFallback + " expanded=true");
        return true;
    }

    /// <summary>
    /// 指定されたプレイリストをプレイリストツリー上で展開・選択します。
    /// 仮想化で未生成のトップレベル項目については、インデックス指定での実体化を試みます。
    /// </summary>
    /// <param name="targetTable">選択対象のトップレベルプレイリスト。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>プレイリストツリー上で選択できた場合は <see langword="true"/>。</returns>
    private bool TrySelectPlaylistTreeItem(BMSTable targetTable, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (targetTable == null)
        {
            return false;
        }
        treeViewItemPlaylist.IsExpanded = true;
        treeViewPlaylist.UpdateLayout();
        treeViewItemPlaylist.UpdateLayout();
        TreeViewItem playlistTreeViewItem = TryGetPlaylistTreeViewItem(targetTable, out usedVirtualizationFallback, out realizeByIndexAvailable);
        if (playlistTreeViewItem == null)
        {
            return false;
        }
        playlistTreeViewItem.IsExpanded = true;
        playlistTreeViewItem.UpdateLayout();
        playlistTreeViewItem.BringIntoView();
        playlistTreeViewItem.IsSelected = true;
        playlistTreeViewItem.Focus();
        return true;
    }

    /// <summary>
    /// 指定されたトップレベルプレイリストに対応する <see cref="TreeViewItem"/> を取得します。
    /// </summary>
    /// <param name="targetTable">プレイリストツリー直下の <see cref="BMSTable"/>。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>対応する <see cref="TreeViewItem"/>。取得できない場合は <see langword="null"/>。</returns>
    private TreeViewItem TryGetPlaylistTreeViewItem(BMSTable targetTable, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (targetTable == null)
        {
            return null;
        }
        int playlistIndex = treeViewItemPlaylist.Items.IndexOf(targetTable);
        if (playlistIndex < 0)
        {
            return null;
        }
        return TryGetPlaylistTreeViewItemByIndex(playlistIndex, out usedVirtualizationFallback, out realizeByIndexAvailable);
    }

    /// <summary>
    /// 指定インデックスのトップレベルプレイリスト項目コンテナを取得します。
    /// 画面外で未生成の場合は、仮想化回避の実体化を試みます。
    /// </summary>
    /// <param name="playlistIndex">プレイリストルート直下のインデックス。</param>
    /// <param name="usedVirtualizationFallback">仮想化回避の実体化経路を使用した場合は <see langword="true"/>。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>生成済みの <see cref="TreeViewItem"/>。取得できない場合は <see langword="null"/>。</returns>
    private TreeViewItem TryGetPlaylistTreeViewItemByIndex(int playlistIndex, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable)
    {
        usedVirtualizationFallback = false;
        realizeByIndexAvailable = true;
        if (playlistIndex < 0)
        {
            return null;
        }
        if (treeViewItemPlaylist.ItemContainerGenerator.ContainerFromIndex(playlistIndex) is TreeViewItem playlistTreeViewItem)
        {
            return playlistTreeViewItem;
        }
        usedVirtualizationFallback = TryRealizeVirtualizedPlaylistItem(playlistIndex, out realizeByIndexAvailable);
        if (!usedVirtualizationFallback)
        {
            return null;
        }
        treeViewPlaylist.UpdateLayout();
        treeViewItemPlaylist.UpdateLayout();
        return treeViewItemPlaylist.ItemContainerGenerator.ContainerFromIndex(playlistIndex) as TreeViewItem;
    }

    /// <summary>
    /// 仮想化で未生成のトップレベルプレイリスト項目を、インデックス指定で可視領域へ移動して実体化させます。
    /// </summary>
    /// <param name="playlistIndex">実体化したいプレイリストルート直下のインデックス。</param>
    /// <param name="realizeByIndexAvailable">インデックス指定での実体化 API が利用可能な場合は <see langword="true"/>。</param>
    /// <returns>可視化要求を実行できた場合は <see langword="true"/>。</returns>
    private bool TryRealizeVirtualizedPlaylistItem(int playlistIndex, out bool realizeByIndexAvailable)
    {
        realizeByIndexAvailable = playlistTreeBringIndexIntoViewMethod != null;
        if (playlistIndex < 0)
        {
            return false;
        }
        VirtualizingStackPanel playlistItemsHostPanel = TryGetPlaylistItemsHostPanel();
        if (playlistItemsHostPanel == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=panel_not_found index=" + playlistIndex);
            return false;
        }
        if (playlistTreeBringIndexIntoViewMethod == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=bring_index_method_not_found index=" + playlistIndex);
            return false;
        }
        try
        {
            // NOTE:
            // TreeView の仮想化が有効だと、画面外のトップレベル項目は ContainerFromIndex で null のままになります。
            // 公開 API にはインデックス単位で実体化を促す手段がないため、WPF 内部の BringIndexIntoView を局所的に利用します。
            playlistTreeBringIndexIntoViewMethod.Invoke(playlistItemsHostPanel, [playlistIndex]);
            treeViewPlaylist.UpdateLayout();
            treeViewItemPlaylist.UpdateLayout();
            return true;
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Info("playlist_tree_item_realize success=false reason=invoke_failed index=" + playlistIndex + " exception=" + ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// プレイリストルート配下の items host となる <see cref="VirtualizingStackPanel"/> を取得します。
    /// </summary>
    /// <returns>プレイリストのトップレベル項目を管理する <see cref="VirtualizingStackPanel"/>。取得できない場合は <see langword="null"/>。</returns>
    private VirtualizingStackPanel TryGetPlaylistItemsHostPanel()
    {
        treeViewItemPlaylist.ApplyTemplate();
        treeViewItemPlaylist.UpdateLayout();
        ItemsPresenter playlistItemsPresenter = FindDescendant<ItemsPresenter>(treeViewItemPlaylist);
        if (playlistItemsPresenter == null)
        {
            return null;
        }
        playlistItemsPresenter.ApplyTemplate();
        return FindDescendant<VirtualizingStackPanel>(playlistItemsPresenter);
    }

    private async void playlistSummaryLinkClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel
            || sender is not Button { DataContext: PlaylistSummaryRow playlistSummaryRow })
        {
            return;
        }
        await viewModel.PlaylistWorkspace.OpenPlaylistSummaryUriAsync(playlistSummaryRow.LinkUri)
            .Logging("playlistSummaryLinkClick");
    }

    private async void playlistSummaryContextMenuResyncClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow == null)
        {
            return;
        }
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (base.DataContext is MainWindowViewModel viewModel && selectedPlaylistSummaryRows.Count > 0)
        {
            await viewModel.PlaylistWorkspace.ResyncPlaylistsAsync(selectedPlaylistSummaryRows).Logging("playlistSummaryContextMenuResyncClick");
        }
    }

    private async void playlistSummaryContextMenuOpenPageClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (base.DataContext is MainWindowViewModel viewModel && playlistSummaryRow != null)
        {
            await viewModel.PlaylistWorkspace.OpenPlaylistSummaryUriAsync(playlistSummaryRow.LinkUri)
                .Logging("playlistSummaryContextMenuOpenPageClick");
        }
    }

    private async void playlistSummaryContextMenuApplyCurrentOrderToBmtSortClick(object sender, RoutedEventArgs e)
    {
        List<PlaylistSummaryRow> visibleRows = GetVisiblePlaylistSummaryRowsSnapshot();
        if (visibleRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await viewModel.PlaylistWorkspace.ApplyCurrentVisibleBmtOrderAsync(visibleRows)
            .Logging("playlistSummaryContextMenuApplyCurrentOrderToBmtSortClick");
    }

    private async void playlistSummaryContextMenuMoveToBmtSortTopClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await viewModel.PlaylistWorkspace.MoveSummaryRowsToBmtTopAsync(selectedPlaylistSummaryRows)
            .Logging("playlistSummaryContextMenuMoveToBmtSortTopClick");
    }

    private async void playlistSummaryContextMenuMoveToBmtSortBottomClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0 || base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        await viewModel.PlaylistWorkspace.MoveSummaryRowsToBmtBottomAsync(selectedPlaylistSummaryRows)
            .Logging("playlistSummaryContextMenuMoveToBmtSortBottomClick");
    }

    private void playlistSummaryContextMenuOpenBulkEditClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = [.. getSelectedPlaylistSummaryRows(playlistSummaryRow).Where(row => row?.TableRef != null)];
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            PlaylistWorkspaceViewModel.PlaylistSummaryBulkEditDialogViewModel dialog =
                mainWindowViewModel.PlaylistWorkspace.OpenSummaryBulkEditDialog(selectedPlaylistSummaryRows);
            if (dialog == null)
            {
                return;
            }
            playlistSummaryBulkEditDialog.DataContext = dialog;
            ShowOverlayDialog(playlistSummaryBulkEditDialog);
        }
    }

    private async void playlistSummaryContextMenuOpenPropertyClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow?.TableRef == null)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            await OpenPlaylistPropertyDialogAsync(
                mainWindowViewModel,
                playlistSummaryRow.TableRef);
        }
    }

    private async Task OpenPlaylistPropertyDialogAsync(
        MainWindowViewModel viewModel,
        BMSTable table)
    {
        PlaylistPropertyDialogViewModel dialog =
            await viewModel.PlaylistWorkspace.OpenPropertyDialogAsync(table);
        if (dialog == null)
        {
            return;
        }
        playlistPropertyDialog.DataContext = dialog;
        ShowOverlayDialog(playlistPropertyDialog);
    }

    internal void ClosePlaylistPropertyDialog(PlaylistPropertyDialogViewModel dialog)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PlaylistWorkspace.ClosePropertyDialog(dialog);
        }
        playlistPropertyDialog.DataContext = null;
        HideOverlayDialog(playlistPropertyDialog);
    }

    private async void playlistSummaryContextMenuRemoveClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow == null)
        {
            return;
        }
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.PlaylistWorkspace
                .RemovePlaylistSummaryRowsAsync(selectedPlaylistSummaryRows)
                .Logging("playlistSummaryContextMenuRemoveClick");
        }
    }

    private async void fullScanCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            await viewModel.RegularChartList
                .NavigateMaintenanceAsync(MainViewUpdateMode.FileMissingFilterSelected)
                .Logging("fullScanCheckFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void fullScanAllChartsFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_all_charts_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeViewItem)
        {
            e.Handled = true;
            await viewModel.RegularChartList
                .NavigateMaintenanceAsync(MainViewUpdateMode.FullScanAllChartsFilterSelected)
                .Logging("fullScanAllChartsFolderSelect");
        }
    }

    private async void fullScanCheckIgnoredFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_full_scan_ignored_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeViewItem)
        {
            e.Handled = true;
            await viewModel.RegularChartList
                .NavigateMaintenanceAsync(MainViewUpdateMode.FileMissingIgnoredFilterSelected)
                .Logging("fullScanCheckIgnoredFolderSelect");
        }
    }

    private async void dupulicateFileCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_duplicate_file_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await viewModel.RegularChartList
                .NavigateMaintenanceAsync(MainViewUpdateMode.DuplicateFilterSelected)
                .Logging("dupulicateFileCheckFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        object parameter;
        if (treeViewItem.DataContext is string)
        {
            parameter = treeViewItem.DataContext;
        }
        else if (treeViewItem.DataContext is DuplicateGroup)
        {
            parameter = treeViewItem.DataContext;
        }
        else
        {
            return;
        }
        await viewModel.RegularChartList
            .NavigateMaintenanceAsync(MainViewUpdateMode.DuplicateFilterSelected, parameter)
            .Logging("dupulicateFileCheckFolderSelect");
    }

    private async void garbledCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_garbled_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            await viewModel.RegularChartList
                .NavigateMaintenanceAsync(MainViewUpdateMode.GarbledFilterSelected)
                .Logging("garbledCheckFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void garbleFixedFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_garble_fixed_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            await viewModel.RegularChartList
                .NavigateMaintenanceAsync(MainViewUpdateMode.GarbleFixedFilterSelected)
                .Logging("garbleFixedFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void unregisteredToDBFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_unregistered_to_db_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await viewModel.RegularChartList
            .NavigateMaintenanceAsync(MainViewUpdateMode.UnregisteredFilterSelected)
            .Logging("unregisteredToDBFolderSelect");
    }

    private async void zeronoteFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_zero_note_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await viewModel.RegularChartList
            .NavigateMaintenanceAsync(MainViewUpdateMode.ZeroNoteFilterSelected)
            .Logging("zeronoteFolderSelect");
    }

    private async void chartInfoParseErrorFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_chart_info_parse_error_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await viewModel.RegularChartList
            .NavigateMaintenanceAsync(MainViewUpdateMode.ChartInfoParseErrorFilterSelected)
            .Logging("chartInfoParseErrorFolderSelect");
    }

    private async void treeViewZeroNoteContextMenuItemRecheckClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ZeroNoteMaintenance
                .RecheckAsync()
                .Logging("treeViewZeroNoteContextMenuItemRecheckClick");
        }
    }

    private async void newlyInstalledFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_newly_installed_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected)
                .Logging("newlyInstalledFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        if (treeViewItem.DataContext is ChartPackage package)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected, package)
                .Logging("newlyInstalledFolderSelect");
        }
    }

    private async void pendingInstallFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_pending_install_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not TreeViewItem treeRoot || e.OriginalSource is not TreeViewItem treeViewItem)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected)
                .Logging("pendingInstallFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        if (treeViewItem.DataContext is ChartPackage package)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected, package)
                .Logging("pendingInstallFolderSelect");
        }
    }

    /// <summary>
    /// プレイリストツリーの「ルート（プレイリスト一覧）」に対するコンテキストメニューが開かれた際の処理。
    /// 現在の書き込みロック状態やバックグラウンド処理状況（IsWriteLockHeld等）に応じ、
    /// 新規作成やURL読み込みなどの各種メニュー項目の有効/無効 (IsEnabled) を動的に制御します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (name == "treeViewPlaylistRootContextMenuItemCreateNewPlaylist")
            {
                menuItem = item as MenuItem;
            }
            if (item is not MenuItem)
            {
                continue;
            }
            foreach (Control item2 in (IEnumerable)((MenuItem)item).Items)
            {
                switch (item2.Name)
                {
                    case "treeViewPlaylistRootContextMenuItemLoadPlaylistURL":
                        menuItem2 = item2 as MenuItem;
                        break;
                    case "treeViewPlaylistRootContextMenuItemLoadPlaylistCollection":
                        menuItem3 = item2 as MenuItem;
                        break;
                    case "treeViewPlaylistRootContextMenuItemLoadWalkureTable":
                        menuItem4 = item2 as MenuItem;
                        break;
                }
            }
        }
        if (menuItem != null)
        {
            menuItem.IsEnabled = mainWindowViewModel.PlaylistWorkspace.CanOpenPlaylistEditDialog;
        }
        if (menuItem2 != null)
        {
            menuItem2.IsEnabled = !mainWindowViewModel.PlaylistWorkspace.IsWriteLockHeldBMSTablesInitializeMin;
        }
        if (menuItem3 != null)
        {
            menuItem3.IsEnabled = !mainWindowViewModel.PlaylistWorkspace.IsWriteLockHeldBMSTablesInitializeMin
                && !mainWindowViewModel.PlaylistWorkspace.IsLoadingExternalCollectionBMSTables;
        }
        if (menuItem4 != null)
        {
            menuItem4.IsEnabled = !mainWindowViewModel.PlaylistWorkspace.IsWriteLockHeldBMSTablesInitializeMin;
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「新しいプレイリストを作成」がクリックされた際の処理。
    /// 非同期で空のBMSTable（プレイリスト）を生成し、直後にプロパティ変更ダイアログを表示させます。
    /// </summary>
    private async void treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem)
        {
            return;
        }
        try
        {
            PlaylistPropertyDialogViewModel dialog = await viewModel.PlaylistWorkspace
                .CreatePlaylistPropertyDialogAsync()
                .Logging("treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick");
            if (dialog != null)
            {
                playlistPropertyDialog.DataContext = dialog;
                ShowOverlayDialog(playlistPropertyDialog);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「URLからプレイリストを読み込む」がクリックされた際の処理。
    /// カスタムURL入力用のダイアログ (loadPlaylistURIDialog) を画面に表示します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadPlaylistURLClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel
            && viewModel.PlaylistWorkspace != null
            && !viewModel.PlaylistWorkspace.IsWriteLockHeldBMSTablesInitializeMin)
        {
            ShowOverlayDialog(loadPlaylistURIDialog);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「プレイリストコレクションを読み込む」がクリックされた際の処理。
    /// 指定されたコレクションURLをもとに、ViewModelへプレイリスト群の非同期登録を要求します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadPlaylistCollectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel
            && viewModel.PlaylistWorkspace != null
            && menuItem.DataContext is BMSTableSimple dataContext
            && !(dataContext.url == null)
            && !viewModel.PlaylistWorkspace.IsWriteLockHeldBMSTablesInitializeMin)
        {
            viewModel.PlaylistWorkspace.EnqueueExternalPlaylistBMSTableImport(dataContext.url);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「Walkure/難易度表を読み込む」に関するメニュー項目（各難易度表単位）のアクション。
    /// MenuItemのTagプロパティに格納されたURLへアクセスし、プレイリスト情報を非同期で追加・登録します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadWalkureTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel
            && viewModel.PlaylistWorkspace != null
            && sender is MenuItem menuItem
            && !viewModel.PlaylistWorkspace.IsWriteLockHeldBMSTablesInitializeMin)
        {
            var uri = new Uri((string)menuItem.Tag);
            viewModel.PlaylistWorkspace.EnqueueExternalPlaylistBMSTableImport(uri);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニューから「Walkureのおすすめフォルダ」関連のテーブル読み込みが選択された場合の処理。
    /// LR2IDの設定状況のチェックや、更新モード/閲覧モードに応じたユーザー確認ダイアログを挟んだ後、非同期で登録処理へ進みます。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadWalkureTableRecommendedClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel
            || viewModel.PlaylistWorkspace == null
            || sender is not MenuItem menuItem)
        {
            return;
        }
        viewModel.PlaylistWorkspace.TryEnqueueRecommendedPlaylistImport((string)menuItem.Tag);
    }

    /// <summary>
    /// プレイリスト（難易度表等の直下にある上位階層）のコンテキストメニューが開かれた際の処理。
    /// 現在の選択要素 (BMSTable) の属性（外部同期するか否か、URLの有無など）や
    /// アプリ状態に応じて、メニュー各項目の有効化状態 (IsEnabled) を切り替えます。
    /// </summary>
    private void treeViewPlaylistTableContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || base.DataContext is not MainWindowViewModel mainWindowViewModel || !(contextMenu.PlacementTarget is TreeViewItem { DataContext: BMSTable dataContext }))
        {
            return;
        }
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        MenuItem menuItem5 = null;
        MenuItem menuItem6 = null;
        MenuItem menuItem7 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "treeViewPlaylistTableContextMenuItemReload":
                    menuItem7 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOpenPageURI":
                    menuItem = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOpenClearLamp":
                    menuItem2 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemOverwriteLevel":
                    menuItem3 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemCreateNewFolder":
                    menuItem4 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableContextMenuItemRemoveTable":
                    menuItem5 = item as MenuItem;
                    break;
                case "treeViewPlaylistTableCcontextMenuItemOpenPropertyDialog":
                    menuItem6 = item as MenuItem;
                    break;
            }
        }
        menuItem7.IsEnabled = mainWindowViewModel.PlaylistWorkspace.CanReloadPlaylistTable(dataContext);
        menuItem.IsEnabled = mainWindowViewModel.PlaylistWorkspace.CanOpenPlaylistTablePage(dataContext);
        menuItem2.IsEnabled = mainWindowViewModel.PlaylistWorkspace.CanOpenPlaylistTableClearLamp(dataContext);
        menuItem4.IsEnabled = !dataContext.is_external_sync;
        menuItem3.IsEnabled = true;
        menuItem5.IsEnabled = true;
        menuItem6.IsEnabled = mainWindowViewModel.PlaylistWorkspace.CanOpenPlaylistEditDialog;
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「最新の情報に更新（リロード）」実行時の処理。
    /// 対象プレイリスト単体の同期更新を非同期で実行し、完了後に選択状態の復元を試みます。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemReloadClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || !(sender is MenuItem { DataContext: BMSTable table }))
        {
            return;
        }
        BMSTable selectionTarget = await viewModel.PlaylistWorkspace
            .ResyncPlaylistTableAsync(table)
            .Logging("treeViewPlaylistTableContextMenuItemReloadClick");
        RestorePlaylistTableSelectionAfterReload(selectionTarget);
    }

    private void RestorePlaylistTableSelectionAfterReload(BMSTable selectionTarget)
    {
        if (selectionTarget == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload skipped reason=target_not_found");
            return;
        }
        bool restored = TrySelectPlaylistTreeItem(selectionTarget, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable);
        if (!restored)
        {
            NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=false reason=container_not_realized table=" + selectionTarget.name + " realize_by_index_available=" + realizeByIndexAvailable);
            return;
        }
        NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=true table=" + selectionTarget.name + " expanded=true usedVirtualizationFallback=" + usedVirtualizationFallback);
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「配布ページを開く」実行時の処理。
    /// BMSTableに設定されたURL (Page_url または Header_url) を標準ブラウザ等で開きます。
    /// 特殊スキーム（Walkure難易度表等）の場合は専用のURLへ変換してブラウザ起動します。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOpenPageURIClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel
            || sender is not MenuItem { DataContext: BMSTable dataContext })
        {
            return;
        }
        mainWindowViewModel.PlaylistWorkspace.OpenPlaylistTablePage(dataContext);
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「クリア状況ページを開く」実行時の処理。
    /// ユーザーのLR2IDと対象難易度表URLをパラメータにし、外部連携サイト（通常はWalkureのクリアランプページ）を表示します。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOpenClearLampClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel
            && sender is MenuItem { DataContext: BMSTable dataContext })
        {
            mainWindowViewModel.PlaylistWorkspace.OpenPlaylistTableClearLamp(dataContext);
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「フォルダを作成」実行時の処理。
    /// 選択中のプレイリスト配下に新しいサブフォルダ用BMSTable要素を非同期で追加します（自作プレイリスト用）。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemCreateNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is BMSTable bmsTable && !bmsTable.is_external_sync)
        {
            viewModel.PlaylistWorkspace
                .CreateFolderAsync(bmsTable)
                .Logging("treeViewPlaylistTableContextMenuItemCreateNewFolderClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「一覧をエキスポート (JSON)」実行時の処理。
    /// 保存用ファイルダイアログを開き、指定されたパスに header.json と data.json 形式で
    /// プレイリスト情報をファイル書き出し（出力）します。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemExportTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        var dialogCoordinator = new UiDialogCoordinator();
        UiSaveFilePickerResult headerResult = await dialogCoordinator.PickSaveFileAsync(new UiSaveFilePickerRequest(
            BeMusicSeeker.Properties.Resources.Save_header_file,
            (!string.IsNullOrWhiteSpace(bmsTable.header_url)) ? Path.GetFileName(bmsTable.Header_url.ToString()) : "header.json",
            ".json",
            BeMusicSeeker.Properties.Resources.Json_file_exts,
            addExtension: true,
            this));
        ThrowIfPickerFailed(headerResult.Status, headerResult.Error, "Header export save picker");
        if (headerResult.Status != UiDialogStatus.Accepted)
        {
            return;
        }
        UiSaveFilePickerResult dataResult = await dialogCoordinator.PickSaveFileAsync(new UiSaveFilePickerRequest(
            BeMusicSeeker.Properties.Resources.Save_data_file,
            (!string.IsNullOrWhiteSpace(bmsTable.data_url)) ? Path.GetFileName(bmsTable.Data_url.ToString()) : "data.json",
            ".json",
            BeMusicSeeker.Properties.Resources.Json_file_exts,
            addExtension: true,
            this));
        ThrowIfPickerFailed(dataResult.Status, dataResult.Error, "Data export save picker");
        if (dataResult.Status != UiDialogStatus.Accepted)
        {
            return;
        }
        await viewModel.PlaylistWorkspace
            .ExportPlaylistTableAsync(bmsTable, headerResult.FileName, dataResult.FileName)
            .Logging("treeViewPlaylistTableContextMenuItemExportTableClick");
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「ローカルBMSの難易度をこの表で上書き」実行時の処理。
    /// ユーザー確認ダイアログ表示後、このプレイリストに登録されている各楽曲のレベル情報を用いて
    /// メインDB（ローカルの全BMS情報）の同等楽曲のレベル値を書き換えます。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOverwriteLevelClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        if (viewModel.PlaylistWorkspace.ConfirmPlaylistOverwriteLevel(bmsTable))
        {
            _ = viewModel.PlaylistWorkspace.ReplaceBmsFileLevelByTableEntryLevelAsync(bmsTable)
                .Logging("treeViewPlaylistTableContextMenuItemOverwriteLevelClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「この表を削除」実行時の処理。
    /// ユーザー確認を取った上で選択中のプレイリストをツリーから除外し、非同期でメイン側からも削除・破棄します。
    /// UI仮想化による選択ロストを防ぐためのフォールバック遷移も併せて行います。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemRemoveTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable
            || !viewModel.PlaylistWorkspace.ConfirmPlaylistTableRemoval(bmsTable))
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemPlaylist, bmsTable, "treeViewPlaylistTableContextMenuItemRemoveTableClick");
        await viewModel.PlaylistWorkspace.RemoveTableAsync(bmsTable).Logging("treeViewPlaylistTableContextMenuItemRemoveTableClick");
        if (treeViewItemPlaylist.IsSelected && treeViewItemPlaylist.Items.Count == 0)
        {
            viewModel.PlaylistWorkspace.RequestDetailSelection(null);
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「プロパティ」実行時の処理。
    /// 選択中の難易度表（BMSTable）の詳細情報や同期URLなどを確認・編集できる専用ダイアログを開きます。
    /// </summary>
    private async void treeViewPlaylistTableCcontextMenuItemOpenPropertyDialogClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel
            && sender is MenuItem { DataContext: BMSTable table })
        {
            await OpenPlaylistPropertyDialogAsync(mainWindowViewModel, table);
        }
    }

    /// <summary>
    /// プレイリスト配下の「フォルダ（自作/自動生成）」のコンテキストメニューが開かれた際の処理。
    /// ツリーのVisualTreeを遡り、現在選択しているアイテムが外部同期中のものかを判別して、
    /// 「フォルダ名変更」や「削除」といった編集メニューの有効化状態を制御します。
    /// </summary>
    private void treeViewPlaylistTableFolderContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || base.DataContext is not MainWindowViewModel)
        {
            return;
        }
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock == null)
        {
            return;
        }
        BMSTable bMSTable = _getUpperBMSTableForContextMenuClickEvent(sender);
        if (bMSTable == null)
        {
            return;
        }
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (!(name == "treeViewPlaylistTableFolderContextMenuItemDeleteFolder"))
            {
                if (name == "treeViewPlaylistTableFolderContextMenuItemChangeFolderName")
                {
                    menuItem2 = item as MenuItem;
                }
            }
            else
            {
                menuItem = item as MenuItem;
            }
        }
        if (!TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode))
        {
            return;
        }
        menuItem.IsEnabled = !bMSTable.is_external_sync && folderNode.IsEditable;
        menuItem2.IsEnabled = !bMSTable.is_external_sync && folderNode.IsEditable;
    }

    /// <summary>
    /// 右クリックやコンテキストメニュー操作の起点となった要素から、
    /// VisualTreeを親方向へ遡及して、所属する上位の <see cref="BMSTable"/>（プレイリスト大枠）を探索して返却します。
    /// </summary>
    /// <param name="sender">ContextMenu もしくは MenuItem。</param>
    /// <returns>該当する上位の <see cref="BMSTable"/> 要素。見つからない場合は null。</returns>
    private BMSTable _getUpperBMSTableForContextMenuClickEvent(object sender)
    {
        ContextMenu contextMenu = ((sender is MenuItem menuItem) ? (menuItem.Parent as ContextMenu) : (sender as ContextMenu));
        if (contextMenu == null)
        {
            return null;
        }
        if (contextMenu.PlacementTarget is not TreeViewItem reference)
        {
            return null;
        }
        DependencyObject parent = VisualTreeHelper.GetParent(reference);
        while ((parent is not TreeViewItem || (parent as TreeViewItem).DataContext is not BMSTable) && parent != null)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (parent == null)
        {
            return null;
        }
        return (parent as TreeViewItem).DataContext as BMSTable;
    }

    /// <summary>
    /// 右クリックやコンテキストメニュー操作の起点となったTreeViewItem内から、
    /// VisualTreeを子方向へ探索し、フォルダ名編集などに用いられる <see cref="EditableTextBlock"/> を取得します。
    /// </summary>
    /// <param name="sender">ContextMenu もしくは MenuItem。</param>
    /// <returns>該当する <see cref="EditableTextBlock"/> コンポーネント。存在しない場合は null。</returns>
    private EditableTextBlock _getETBFromContextMenuClickEvent(object sender)
    {
        ContextMenu contextMenu = ((sender is MenuItem menuItem) ? (menuItem.Parent as ContextMenu) : (sender as ContextMenu));
        if (contextMenu == null)
        {
            return null;
        }
        if (contextMenu.PlacementTarget is not TreeViewItem treeViewItem)
        {
            return null;
        }
        return _findTypeFromVisualChildren<EditableTextBlock>([treeViewItem]);
    }

    private static Type _findTypeFromVisualChildren<Type>(IEnumerable<DependencyObject> _objs) where Type : class
    {
        IEnumerable<DependencyObject> enumerable = _objs.SelectMany(obj => _getVisualChildren(obj));
        if (enumerable.Count() == 0)
        {
            return null;
        }
        DependencyObject dependencyObject = enumerable.FirstOrDefault(c => c is Type);
        if (dependencyObject == null)
        {
            return _findTypeFromVisualChildren<Type>(enumerable);
        }
        return dependencyObject as Type;
    }

    private static IEnumerable<DependencyObject> _getVisualChildren(DependencyObject obj)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            yield return VisualTreeHelper.GetChild(obj, i);
        }
    }

    /// <summary>
    /// プレイリストフォルダ階層コンテキストメニュー「フォルダを削除」実行時の処理。
    /// 指定されたカスタムフォルダ名を持つ仮想要素を、所属するプレイリスト (BMSTable) 内から抹消します。
    /// </summary>
    private void treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock == null || !TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode))
        {
            return;
        }
        BMSTable bmsTable = _getUpperBMSTableForContextMenuClickEvent(sender);
        if (bmsTable == null)
        {
            return;
        }
        viewModel.PlaylistWorkspace
            .RemovePlaylistFolderAsync(bmsTable, folderNode)
            .Logging("treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick");
    }

    /// <summary>
    /// プレイリストフォルダ階層コンテキストメニュー「フォルダ名を変更」実行時の処理。
    /// 該当のTreeViewItem内に存在する EditableTextBlock の編集モードをアクティブ (IsInEditMode = true) にします。
    /// </summary>
    private void treeViewPlaylistTableFolderContextMenuItemChangeFolderNameClick(object sender, RoutedEventArgs e)
    {
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock != null && (!TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) || folderNode.IsEditable))
        {
            editableTextBlock.IsInEditMode = true;
        }
    }

    /// <summary>
    /// メインツリー（ライブラリ/フォルダ等）コンテキストメニュー「エクスプローラで開く」実行時の処理。
    /// Explorer.exe を介して、選択中のローカルファイルシステム上の絶対パスを開きます。
    /// </summary>
    private void treeViewLibraryFolderContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string text = placementTarget.Header.ToString();
        if (!LongPathFileSystem.DirectoryExists(text))
        {
            return;
        }
        ExplorerOpenService.OpenDirectory(text);
    }

    /// <summary>
    /// BMS検索フォルダコンテキストメニュー「BMS検索フォルダから除外」実行時の処理。
    /// ユーザー確認ダイアログ表示後、アプリケーション設定のBMSルートフォルダー一覧から該当のパスを除外して保存します。
    /// </summary>
    private void treeViewLibraryFolderContextMenuItemUnregisterRootFolder(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string path = placementTarget.Header.ToString();
        if (base.DataContext is not MainWindowViewModel)
        {
            return;
        }
        MainWindowViewModel.SettingDialogViewModel viewModel = (base.DataContext as MainWindowViewModel).settingDialog;
        if (LongPathFileSystem.DirectoryExists(path) && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_unregister_root_folder, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            viewModel.RemoveBMSDirectoryFromRootFolderAndSave(path);
        }
    }

    private void treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_library_auto_rename_all"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string path = placementTarget.Header.ToString();
        if (base.DataContext is MainWindowViewModel viewModel
            && viewModel.FolderAutoRenameWorkflow?.IsActive != true
            && LongPathFileSystem.DirectoryExists(path)
            && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_rename_folders, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            viewModel.FolderAutoRenameWorkflow.StartAll(path);
        }
        e.Handled = true;
    }

    private async void treeViewInstalledContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_installed_clear_all"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_all_installed, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            await viewModel.PackageRecords
                .RemoveAllAsync(DeleteInstallPackageRecordsKind.Installed)
                .Logging("treeViewInstalledContextMenuClearAllClick");
        }
    }

    private async void treeViewInstallPendingContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_clear_all"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_all_pendings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            await viewModel.PackageRecords
                .RemoveAllAsync(DeleteInstallPackageRecordsKind.Pending)
                .Logging("treeViewInstallPendingContextMenuClearAllClick");
        }
    }

    private async void treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_delete_installed_only"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.PendingPackages
            .DeleteInstalledOnlyPendingPackageSourcesAsync()
            .LoggingAndPropagate("treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick");
    }

    private async void treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_rename_zero_note"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.PendingPackages
            .RenamePendingZeroNoteChartsAsync()
            .LoggingAndPropagate("treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick");
    }

    private async void treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_pending_overwrite_installed_only_resources"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.PendingPackages
            .OverwriteInstalledOnlyPendingPackageResourcesAsync()
            .LoggingAndPropagate("treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick");
    }

    private void treeViewInstallPackageContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: ChartPackage dataContext } } }))
        {
            return;
        }
        if (LongPathFileSystem.DirectoryExists(dataContext.path))
        {
            ExplorerOpenService.OpenDirectory(dataContext.path);
            return;
        }
        if (!LongPathFileSystem.FileExists(dataContext.path))
        {
            return;
        }
        ExplorerOpenService.OpenFileAndSelect(dataContext.path);
    }

    /// <summary>
    /// インストール関連ツリーのコンテキストメニュー「このフォルダの履歴を消去」実行時の処理。
    /// 「インストール保留中」などのリストから対象のパッケージ (ChartPackage) を一つ取り除きます。
    /// </summary>
    private async void treeViewInstallPackageContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_clear_folder"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_pendings + Environment.NewLine + Environment.NewLine + GetPendingPackageClearConfirmationTarget(pkg), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuClearFolderClick");
        await viewModel.PackageRecords
            .RemovePackagesAsync(DeleteInstallPackageRecordsKind.Pending, [pkg])
            .Logging("treeViewInstallPackageContextMenuClearFolderClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected)
                .Logging("treeViewInstallPackageContextMenuClearFolderClick");
        }
    }

    private static string GetPendingPackageClearConfirmationTarget(ChartPackage pkg)
    {
        return pkg?.DisplayTitle ?? string.Empty;
    }

    private async void treeViewInstalledFolderContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_installed_clear_folder"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        SelectNextSiblingOrRoot(newlyInstalledTreeViewItem, pkg, "treeViewInstalledFolderContextMenuClearFolderClick");
        await viewModel.PackageRecords
            .RemovePackagesAsync(DeleteInstallPackageRecordsKind.Installed, [pkg])
            .Logging("treeViewInstalledFolderContextMenuClearFolderClick");
        if (newlyInstalledTreeViewItem.IsSelected && newlyInstalledTreeViewItem.Items.Count == 0)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected)
                .Logging("treeViewInstalledFolderContextMenuClearFolderClick");
        }
    }

    private async void treeViewInstallPackageContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_remove_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await viewModel.PendingPackages
                .ClearPackagesAsync([pkg])
                .LoggingAndPropagate("treeViewInstallPackageContextMenuRemoveInstallDestinationClick");
        }
    }

    private async void treeViewInstallPackageContextMenuForceInstallClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_force_install"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.PendingPackages
            .ForceInstallPackagesAsync(
                [pkg],
                () => SelectNextSiblingOrRoot(
                    treeViewItemInstallPending,
                    pkg,
                    "treeViewInstallPackageContextMenuForceInstallClick"))
            .LoggingAndPropagate("treeViewInstallPackageContextMenuForceInstallClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected)
                .LoggingAndPropagate("treeViewInstallPackageContextMenuForceInstallClick");
        }
    }

    private async void treeViewInstallPackageContextMenuManualInstallClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_manual_install"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.PendingPackages
            .ManualInstallPackagesAsync(
                [pkg],
                () => SelectNextSiblingOrRoot(
                    treeViewItemInstallPending,
                    pkg,
                    "treeViewInstallPackageContextMenuManualInstallClick"))
            .LoggingAndPropagate("treeViewInstallPackageContextMenuManualInstallClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected)
                .LoggingAndPropagate("treeViewInstallPackageContextMenuManualInstallClick");
        }
    }

    private async void treeViewInstallPackageContextMenuSearchInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_search_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await viewModel.PendingPackages
                .SearchPackagesAsync(PendingInstallDestinationSearchKind.InstallDestination, [pkg])
                .LoggingAndPropagate("treeViewInstallPackageContextMenuSearchInstallationDirectoryClick");
        }
    }

    private async void treeViewInstallPackageContextMenuSearchMergeDestinationClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_package_search_merge_destination"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await viewModel.PendingPackages
                .SearchPackagesAsync(PendingInstallDestinationSearchKind.MergeDestination, [pkg])
                .LoggingAndPropagate("treeViewInstallPackageContextMenuSearchMergeDestinationClick");
        }
    }

    private void treeViewDuplicateFolderContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!(sender is ContextMenu { PlacementTarget: TreeViewItem { DataContext: string dataContext } placementTarget } contextMenu))
        {
            return;
        }
        TreeViewItem treeViewItem = WPFUtil.FindVisualParent<TreeViewItem>(placementTarget);
        if (treeViewItem == null)
        {
            return;
        }
        if (treeViewItem.DataContext is not DuplicateGroup duplicateGroup)
        {
            return;
        }
        MenuItem menuItem = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            string name = item.Name;
            if (name == "treeViewDuplicateFolderContextMenuItemMergeInto")
            {
                menuItem = item as MenuItem;
            }
        }
        if (menuItem != null)
        {
            List<string> list = [.. duplicateGroup.Folders.Except([dataContext], StringComparer.OrdinalIgnoreCase)];
            menuItem.IsEnabled = list.Count > 0;
            if (menuItem.IsEnabled)
            {
                menuItem.ItemsSource = list;
            }
        }
    }

    private void treeViewDuplicateFolderContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: string dataContext } } }) || !LongPathFileSystem.DirectoryExists(dataContext))
        {
            return;
        }
        ExplorerOpenService.OpenDirectory(dataContext);
    }

    private void treeViewDuplicateFolderContextMenuItemMergeIntoTargetClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("tree_duplicate_merge_into"))
        {
            e.Handled = true;
            return;
        }
        if (!(e.Source is MenuItem { Tag: TreeViewItem tag } menuItem))
        {
            return;
        }
        string srcPath = tag.DataContext as string;
        if (string.IsNullOrWhiteSpace(srcPath))
        {
            return;
        }
        string dstPath = menuItem.DataContext as string;
        if (string.IsNullOrWhiteSpace(dstPath))
        {
            return;
        }
        // 親DuplicateGroupを取得
        TreeViewItem groupTreeItem = WPFUtil.FindVisualParent<TreeViewItem>(tag);
        var duplicateGroup = groupTreeItem?.DataContext as DuplicateGroup;
        ExecuteDuplicateFolderMerge(srcPath, dstPath, duplicateGroup);
    }

    /// <summary>
    /// 重複フォルダのマージ処理を実行する共通メソッド。
    /// 確認ダイアログ → マージ実行 → マージ後のグループ自動選択を行う。
    /// </summary>
    private void ExecuteDuplicateFolderMerge(string srcPath, string dstPath, DuplicateGroup duplicateGroup)
    {
        if (ShouldBlockChartPackageMutationInteraction("duplicate_merge_execute"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath))
        {
            return;
        }

        // 確認ダイアログ
        if (Settings.Default.ShowDuplicateFileCheckConfirmMsg && UiDialogRoute.ShowMessageBox(Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_folder + Environment.NewLine + Environment.NewLine +
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_target + ": " + srcPath + Environment.NewLine +
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_destination + ": " + dstPath,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }

        long operationId = Stopwatch.GetTimestamp();
        var okToTaskStopwatch = Stopwatch.StartNew();
        var totalStopwatch = Stopwatch.StartNew();
        LogDuplicateMergePerformance("duplicate_merge_ui confirmed op=" + operationId + " src=" + srcPath + " dst=" + dstPath);

        // マージ後に自動選択するグループのHeaderをキャッシュ
        _pendingDuplicateGroupHeader = null;
        if (duplicateGroup != null && viewModel.DuplicateChartGroups != null)
        {
            int folderCount = duplicateGroup.Folders.Count;
            if (folderCount == 2)
            {
                // フォルダが2つの場合: マージでグループ消滅 → 次のグループを選択
                int currentIndex = viewModel.DuplicateChartGroups.IndexOf(duplicateGroup);
                if (currentIndex >= 0 && currentIndex + 1 < viewModel.DuplicateChartGroups.Count)
                {
                    _pendingDuplicateGroupHeader = viewModel.DuplicateChartGroups[currentIndex + 1].Header;
                }
            }
            else if (folderCount >= 3)
            {
                // フォルダが3つ以上の場合: マージ後もグループが残る → 同じグループを再選択
                _pendingDuplicateGroupHeader = duplicateGroup.Header;
            }
        }

        string srcFolderName = Path.GetFileName(srcPath);
        string dstFolderName = Path.GetFileName(dstPath);

        Task.Run(delegate
        {
            LogDuplicateMergePerformance("duplicate_merge_task start op=" + operationId + " okToTaskStartMs=" + okToTaskStopwatch.ElapsedMilliseconds + " src=" + srcPath + " dst=" + dstPath);
            var taskStopwatch = Stopwatch.StartNew();
            viewModel.MergeChartDirectory(srcPath, dstPath, operationId);
            LogDuplicateMergePerformance("duplicate_merge_task viewModel_done op=" + operationId + " taskMs=" + taskStopwatch.ElapsedMilliseconds + " totalSinceOkMs=" + totalStopwatch.ElapsedMilliseconds);

            // マージ完了ログ（将来のステータスバー通知に備える）
            NLogWrapper.FileLogger?.Info(string.Format(
                BeMusicSeeker.Properties.Resources.Msg_merge_bms_completed, srcFolderName, dstFolderName));

        }).ContinueWith(t =>
        {
            LogDuplicateMergePerformance("duplicate_merge_ui continuation op=" + operationId + " faulted=" + (t.Exception != null) + " totalSinceOkMs=" + totalStopwatch.ElapsedMilliseconds);
            if (t.Exception != null)
            {
                return;
            }
            // マージ後にDuplicateChartGroupsの更新を待ってからツリーで自動選択を試みる
            WaitForDuplicateListUpdateAndSelect(_pendingDuplicateGroupHeader, viewModel);
        }, TaskScheduler.FromCurrentSynchronizationContext()).Logging("ExecuteDuplicateFolderMerge");
    }

    private static void LogDuplicateMergePerformance(string message)
    {
        if (CommandLineSwitches.IsInfoLoggingEnabled)
        {
            NLogWrapper.GetLogger("InstallPerformance.DuplicateMerge").Info(message);
        }
    }

    /// <summary>
    /// DuplicateChartGroups更新タイミングの競合を吸収しつつ、該当グループを自動選択する。
    /// PropertyChangedと遅延フォールバックの両方から、データ更新後のTreeView反映を待って選択を試みる。
    /// </summary>
    private void WaitForDuplicateListUpdateAndSelect(string header, MainWindowViewModel viewModel)
    {
        if (string.IsNullOrEmpty(header) || viewModel == null)
        {
            return;
        }
        if (_duplicateGroupAutoSelectHandler != null && _duplicateGroupAutoSelectHandlerOwner != null)
        {
            _duplicateGroupAutoSelectHandlerOwner.PropertyChanged -= _duplicateGroupAutoSelectHandler;
            _duplicateGroupAutoSelectHandler = null;
            _duplicateGroupAutoSelectHandlerOwner = null;
        }
        int requestVersion = Interlocked.Increment(ref _duplicateGroupAutoSelectRequestVersion);
        bool completed = false;
        PropertyChangedEventHandler handler = null;
        void completeSelection()
        {
            if (completed)
            {
                return;
            }
            completed = true;
            if (handler != null)
            {
                viewModel.PropertyChanged -= handler;
            }
            if (ReferenceEquals(_duplicateGroupAutoSelectHandler, handler))
            {
                _duplicateGroupAutoSelectHandler = null;
                _duplicateGroupAutoSelectHandlerOwner = null;
            }
        }
        async Task AttemptAutoSelectAsync(string trigger)
        {
            try
            {
                if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                {
                    return;
                }
                if (viewModel.DuplicateChartGroups == null)
                {
                    NLogWrapper.FileLogger?.Info("duplicate_group_autoselect wait_for_groups trigger=" + trigger + " header=" + header + " request=" + requestVersion);
                    if (trigger == "timeout_fallback")
                    {
                        completeSelection();
                    }
                    return;
                }

                string lastReason = string.Empty;
                DispatcherPriority[] retryPriorities = [DispatcherPriority.Loaded, DispatcherPriority.Render, DispatcherPriority.ContextIdle];
                for (int retryIndex = 0; retryIndex < retryPriorities.Length; retryIndex++)
                {
                    await Dispatcher.Yield(retryPriorities[retryIndex]);
                    if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                    {
                        return;
                    }
                    if (TrySelectDuplicateGroupByHeader(header, viewModel, out lastReason))
                    {
                        NLogWrapper.FileLogger?.Info("duplicate_group_autoselect success trigger=" + trigger + " retry=" + retryIndex + " header=" + header + " request=" + requestVersion);
                        completeSelection();
                        return;
                    }
                    if (!ShouldRetryDuplicateGroupAutoSelect(lastReason))
                    {
                        break;
                    }
                }
                NLogWrapper.FileLogger?.Warn("duplicate_group_autoselect pending trigger=" + trigger + " header=" + header + " request=" + requestVersion + " reason=" + lastReason);
                if (trigger == "timeout_fallback")
                {
                    completeSelection();
                }
            }
            catch (Exception ex)
            {
                NLogWrapper.FileLogger?.Warn("duplicate_group_autoselect failed trigger=" + trigger + " header=" + header + " request=" + requestVersion + " message=" + ex.Message);
            }
        }
        handler = (s, e) =>
        {
            if (e.PropertyName != nameof(viewModel.DuplicateChartGroups))
            {
                return;
            }
            NLogWrapper.FileLogger?.Info("duplicate_group_autoselect trigger=property_changed header=" + header + " request=" + requestVersion);
            if (Dispatcher.CheckAccess())
            {
                _ = AttemptAutoSelectAsync("property_changed");
            }
            else
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (_isClosingOrClosed)
                    {
                        return;
                    }
                    _ = AttemptAutoSelectAsync("property_changed");
                }));
            }
        };
        viewModel.PropertyChanged += handler;
        _duplicateGroupAutoSelectHandler = handler;
        _duplicateGroupAutoSelectHandlerOwner = viewModel;
        NLogWrapper.FileLogger?.Info("duplicate_group_autoselect queued header=" + header + " request=" + requestVersion + " mode=property_changed");
        if (viewModel.DuplicateChartGroups != null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                if (_isClosingOrClosed)
                {
                    return;
                }
                NLogWrapper.FileLogger?.Info("duplicate_group_autoselect trigger=already_ready header=" + header + " request=" + requestVersion);
                _ = AttemptAutoSelectAsync("already_ready");
            }));
        }
        Task.Run(async delegate
        {
            await Task.Delay(1500).ConfigureAwait(continueOnCapturedContext: false);
            if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
            {
                return;
            }
            await Dispatcher.InvokeAsync(async delegate
            {
                if (_isClosingOrClosed)
                {
                    return;
                }
                if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                {
                    return;
                }
                NLogWrapper.FileLogger?.Info("duplicate_group_autoselect trigger=timeout_fallback header=" + header + " request=" + requestVersion);
                await AttemptAutoSelectAsync("timeout_fallback");
            }, DispatcherPriority.Background);
        });
    }

    private static bool ShouldRetryDuplicateGroupAutoSelect(string failReason)
    {
        return failReason == "duplicate_tree_items_not_updated" ||
            failReason == "container_not_realized" ||
            failReason == "duplicate_items_host_not_found" ||
            failReason == "bring_index_out_of_range";
    }

    private bool TrySelectDuplicateGroupByHeader(string header, MainWindowViewModel viewModel, out string failReason)
    {
        failReason = string.Empty;
        // XAML上で Name="treeViewItemSearchDuplicated" を持つTreeViewItem
        TreeViewItem duplicateRootItem = treeViewItemSearchDuplicated;
        if (duplicateRootItem == null)
        {
            failReason = "root_not_found";
            return false;
        }

        List<DuplicateGroup> duplicatedList = viewModel.DuplicateChartGroups;
        if (duplicatedList == null)
        {
            failReason = "duplicated_list_null";
            return false;
        }

        DuplicateGroup targetGroup = null;
        for (int i = 0; i < duplicatedList.Count; i++)
        {
            if (duplicatedList[i].Header == header)
            {
                targetGroup = duplicatedList[i];
                break;
            }
        }
        if (targetGroup == null)
        {
            failReason = "group_not_found";
            return false;
        }

        // 親TreeViewItemが展開されていることを確認
        treeViewItemFullScanCheck.IsExpanded = true;
        duplicateRootItem.IsExpanded = true;
        duplicateRootItem.BringIntoView();
        duplicateRootItem.UpdateLayout();

        int targetIndex = FindDuplicateGroupTreeItemIndex(duplicateRootItem, targetGroup);
        if (targetIndex < 0)
        {
            failReason = "duplicate_tree_items_not_updated";
            return false;
        }

        TreeViewItem targetItem = duplicateRootItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) as TreeViewItem;
        if (targetItem == null)
        {
            if (!TryRealizeVirtualizedDuplicateGroupItem(duplicateRootItem, targetIndex, out failReason))
            {
                return false;
            }
            targetItem = duplicateRootItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) as TreeViewItem;
        }

        // コンテナを取得して選択
        if (targetItem != null)
        {
            targetItem.IsSelected = true;
            targetItem.IsExpanded = true;
            targetItem.BringIntoView();
            return true;
        }
        failReason = "container_not_realized";
        return false;
    }

    private static int FindDuplicateGroupTreeItemIndex(TreeViewItem duplicateRootItem, DuplicateGroup targetGroup)
    {
        ItemCollection treeItems = duplicateRootItem.Items;
        for (int i = 0; i < treeItems.Count; i++)
        {
            if (ReferenceEquals(treeItems[i], targetGroup))
            {
                return i;
            }
        }
        return -1;
    }

    private bool TryRealizeVirtualizedDuplicateGroupItem(TreeViewItem duplicateRootItem, int targetIndex, out string failReason)
    {
        failReason = string.Empty;
        if (playlistTreeBringIndexIntoViewMethod == null)
        {
            failReason = "bring_index_method_not_found";
            return false;
        }
        VirtualizingStackPanel duplicateItemsHostPanel = TryGetTreeViewItemItemsHostPanel(duplicateRootItem);
        if (duplicateItemsHostPanel == null)
        {
            failReason = "duplicate_items_host_not_found";
            return false;
        }
        try
        {
            playlistTreeBringIndexIntoViewMethod.Invoke(duplicateItemsHostPanel, [targetIndex]);
            duplicateRootItem.UpdateLayout();
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException)
        {
            failReason = "bring_index_out_of_range";
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            failReason = "bring_index_out_of_range";
            return false;
        }
        catch (Exception ex)
        {
            failReason = "bring_index_failed_" + ex.GetType().Name;
            return false;
        }
    }

    private static VirtualizingStackPanel TryGetTreeViewItemItemsHostPanel(TreeViewItem treeViewItem)
    {
        treeViewItem.ApplyTemplate();
        treeViewItem.UpdateLayout();
        return FindItemsHostPanelForOwner(treeViewItem, treeViewItem);
    }

    private static VirtualizingStackPanel FindItemsHostPanelForOwner(DependencyObject parent, ItemsControl owner)
    {
        if (parent == null)
        {
            return null;
        }
        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(parent);
        }
        catch
        {
            return null;
        }
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, childIndex);
            if (child is VirtualizingStackPanel panel && ReferenceEquals(ItemsControl.GetItemsOwner(panel), owner))
            {
                return panel;
            }
            VirtualizingStackPanel descendant = FindItemsHostPanelForOwner(child, owner);
            if (descendant != null)
            {
                return descendant;
            }
        }
        return null;
    }

    /// <summary>
    /// 重複フォルダのキーボードショートカットハンドラ。
    /// Ctrl+G: フォルダが2つの場合、もう一方のフォルダへマージを実行する。
    /// </summary>
    private void duplicateFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.G || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (sender is not TreeViewItem folderItem)
        {
            return;
        }

        string srcPath = folderItem.DataContext as string;
        if (string.IsNullOrWhiteSpace(srcPath))
        {
            return;
        }

        // 親TreeViewItemからDuplicateGroupを取得
        TreeViewItem groupItem = WPFUtil.FindVisualParent<TreeViewItem>(folderItem);
        if (groupItem?.DataContext is not DuplicateGroup duplicateGroup)
        {
            return;
        }

        int folderCount = duplicateGroup.Folders.Count;

        if (folderCount == 2)
        {
            // フォルダが2つの場合: 自分以外の唯一のフォルダへマージ
            string dstPath = duplicateGroup.Folders
                .FirstOrDefault(f => !f.Equals(srcPath, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(dstPath))
            {
                ExecuteDuplicateFolderMerge(srcPath, dstPath, duplicateGroup);
            }
        }
        else if (folderCount == 1)
        {
            // フォルダが1つの場合: ハッシュ重複BMSファイルの整理
            ExecuteDuplicateHashCleanup(duplicateGroup, srcPath);
        }
        else if (folderCount >= 3)
        {
            // フォルダが3つ以上の場合: コンテキストメニューを開いてマージ先を選択
            OpenDuplicateFolderContextMenu(folderItem);
        }

        e.Handled = true;
    }

    /// <summary>
    /// フォルダが1つの重複グループで、ハッシュ重複BMSファイルを整理する。
    /// 各ハッシュグループごとに1つだけ残し、残りをごみ箱へ移動する。
    /// 保持ルール: 更新日時が最も古いものを優先、同日時ならファイル名が最も短いものを優先。
    /// </summary>
    private void ExecuteDuplicateHashCleanup(DuplicateGroup duplicateGroup, string folderPath)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var chartsInFolder = duplicateGroup.ChartFiles
            .Where(chart => !string.IsNullOrWhiteSpace(chart.Path) && chart.Path.StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (chartsInFolder.Count == 0)
        {
            return;
        }

        // 主キー(hash)でグループ化し、各グループで削除対象を決定
        var deletionList = new List<ChartFile>();
        foreach (var hashGroup in chartsInFolder
            .Select(chart => new { Chart = chart, LookupHash = ChartLookupKey.GetPrimaryHash(chart) })
            .Where(x => !string.IsNullOrWhiteSpace(x.LookupHash))
            .GroupBy(x => x.LookupHash, StringComparer.OrdinalIgnoreCase))
        {
            var grouped = hashGroup.Select(x => x.Chart).ToList();
            if (grouped.Count <= 1)
            {
                continue;
            }

            // 保持対象: 更新日時が最も古い → ファイル名が最も短い
            ChartFile keeper = grouped
                .OrderBy(chart =>
                {
                    try { return LongPathFileSystem.GetLastWriteTime(chart.Path, isDirectory: false); }
                    catch { return DateTime.MaxValue; }
                })
                .ThenBy(chart => Path.GetFileName(chart.Path).Length)
                .First();

            deletionList.AddRange(grouped.Where(f => f != keeper));
        }

        if (deletionList.Count == 0)
        {
            return;
        }

        // 確認ダイアログ
        if (Settings.Default.ShowDuplicateFileCheckConfirmMsg && UiDialogRoute.ShowMessageBox(Window.GetWindow(this),
            string.Format(BeMusicSeeker.Properties.Resources.Msg_cleanup_duplicate_hash, deletionList.Count),
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }

        // 処理後の自動選択用: フォルダ1つなのでグループは消滅 → 次のグループを自動選択
        _pendingDuplicateGroupHeader = null;
        if (viewModel.DuplicateChartGroups != null)
        {
            int currentIndex = viewModel.DuplicateChartGroups.IndexOf(duplicateGroup);
            if (currentIndex >= 0 && currentIndex + 1 < viewModel.DuplicateChartGroups.Count)
            {
                _pendingDuplicateGroupHeader = viewModel.DuplicateChartGroups[currentIndex + 1].Header;
            }
        }

        Task.Run(delegate
        {
            viewModel.RemoveLibraryCharts(deletionList);

            NLogWrapper.FileLogger?.Info(string.Format(
                "Cleaned up {0} duplicate hash BMS file(s) in folder: {1}",
                deletionList.Count, Path.GetFileName(folderPath)));

        }).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                return;
            }
            WaitForDuplicateListUpdateAndSelect(_pendingDuplicateGroupHeader, viewModel);
        }, TaskScheduler.FromCurrentSynchronizationContext()).Logging("ExecuteDuplicateHashCleanup");
    }

    /// <summary>
    /// フォルダが3つ以上の場合にコンテキストメニューをプログラムから開き、
    /// 「マージ先」サブメニューを展開した状態にする。
    /// </summary>
    private void OpenDuplicateFolderContextMenu(TreeViewItem folderItem)
    {
        ContextMenu contextMenu = folderItem.ContextMenu;
        if (contextMenu == null)
        {
            return;
        }

        contextMenu.PlacementTarget = folderItem;
        contextMenu.IsOpen = true;

        // コンテキストメニューのOpenedイベントでマージ先リストが生成されるため、
        // メニューが開いた後にサブメニューを展開する
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_isClosingOrClosed)
            {
                return;
            }
            foreach (Control item in (IEnumerable)contextMenu.Items)
            {
                if (item.Name == "treeViewDuplicateFolderContextMenuItemMergeInto" && item is MenuItem mergeMenuItem)
                {
                    mergeMenuItem.IsSubmenuOpen = true;
                    break;
                }
            }
        }));
    }

    private void calcelAllContextMenuTasks()
    {
        if (changeSubmenuOpenDocumentTask?.IsCompleted == false && tableContextMenuTaskTokenSource != null)
        {
            tableContextMenuTaskTokenSource.Cancel();
            NLogWrapper.DebuggerLogger?.Trace("Cancel data grid context menu async tasks");
        }
    }

    private void initContextMenuTasks()
    {
        tableContextMenuTaskTokenSource = new CancellationTokenSource();
        changeSubmenuOpenDocumentTask = null;
    }

    private void tableContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("datagrid_context_menu_opened"))
        {
            e.Handled = true;
            if (sender is ContextMenu blockedContextMenu)
            {
                CloseContextMenuIfOpen(blockedContextMenu);
            }
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row))
        {
            NLogWrapper.FileLogger?.Info("playlist_context_menu rowResolve=False sourceType=" + sender?.GetType().FullName);
            return;
        }
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel)
        {
            return;
        }
        _lastOpenedContextMenu = contextMenu;
        bool isPlaylistRow = GridRowResolver.IsPlaylistRow(row);
        Uri rowUrl = GridRowResolver.GetUrl(row);
        Uri rowUrlDiff = GridRowResolver.GetUrlDiff(row);
        MainViewOperationSection effectiveSection = mainWindowViewModel.CurrentMainViewOperationSection;
        bool isPendingSelected = IsPendingMainViewSection(effectiveSection);
        bool isInstalledSelected = IsInstalledMainViewSection(effectiveSection);
        bool isPlaylistSelected = IsPlaylistMainViewSection(effectiveSection);
        ChartOperationSourceScope sourceScope = mainWindowViewModel.CurrentMainViewChartOperationSourceScope;
        if (!GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget rowTarget))
        {
            rowTarget = null;
            if (!isPlaylistRow)
            {
                NLogWrapper.FileLogger?.Info("playlist_context_menu rowResolve=True but unsupported rowType=" + row?.GetType().FullName);
                return;
            }
        }
        ChartContextMenuState contextMenuState = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow,
            rowUrl,
            rowUrlDiff,
            isPendingSelected,
            isInstalledSelected,
            isPlaylistSelected,
            rowTarget,
            GetSelectedChartTargets(isPendingSelected)));
        bool isInstallListSelected = contextMenuState.IsInstallListSelected;
        bool isPlaylistContext = contextMenuState.IsPlaylistContext;
        string chartPath = contextMenuState.ChartPath;
        IReadOnlyList<ChartOperationTarget> selectedTargets = contextMenuState.SelectedTargets;
        bool isBmsonContextRow = contextMenuState.IsBmsonContextRow;
        bool hasBmsonSelection = contextMenuState.HasBmsonSelection;
        bool hasBmsSelection = contextMenuState.HasBmsSelection;
        calcelAllContextMenuTasks();
        initContextMenuTasks();
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItemFindExternalPackage = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        MenuItem menuItem7 = null;
        MenuItem menuItem8 = null;
        MenuItem menuItem9 = null;
        MenuItem menuItem10 = null;
        MenuItem menuItem11 = null;
        MenuItem menuItem12 = null;
        MenuItem menuItemFullScanAllCharts = null;
        MenuItem menuItem13 = null;
        MenuItem menuItem14 = null;
        MenuItem menuItem15 = null;
        MenuItem menuItem16 = null;
        MenuItem menuItem17 = null;
        MenuItem menuItemOpenLr2Ir = null;
        MenuItem menuItemOpenMocha = null;
        MenuItem menuItemOpenMinIr = null;
        MenuItem menuItemOpenInstallDestination = null;
        MenuItem menuItemOpenDocument = null;
        MenuItem menuItem18 = null;
        MenuItem menuItemRenameInvalidExt = null;
        Separator separator = null;
        MenuItem menuItem19 = null;
        Separator separator2 = null;
        MenuItem menuItemRemoveChartInfoParseFailure = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "tableContextMenuItemOpenLR2IR":
                    menuItemOpenLr2Ir = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenMocha":
                    menuItemOpenMocha = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenMinIR":
                    menuItemOpenMinIr = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenURL":
                    menuItem = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenURLdiff":
                    menuItem2 = item as MenuItem;
                    break;
                case "tableContextMenuItemFindExternalPackage":
                    menuItemFindExternalPackage = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenExplorer":
                    menuItem3 = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenInstallDestination":
                    menuItemOpenInstallDestination = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenBMSFile":
                    menuItem4 = item as MenuItem;
                    break;
                case "tableContextMenuItemRegisterScore":
                    menuItem18 = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenDocument":
                    menuItemOpenDocument = item as MenuItem;
                    break;
                case "tableContextMenuItemUpdateRankingData":
                    menuItem7 = item as MenuItem;
                    break;
                case "tableContextMenuItemInstall":
                    menuItem8 = item as MenuItem;
                    break;
                case "tableContextMenuItemFixInstall":
                    menuItem9 = item as MenuItem;
                    break;
                case "tableContextMenuItemFullScanCheck":
                    menuItem10 = item as MenuItem;
                    foreach (Control item2 in (IEnumerable)menuItem10.Items)
                    {
                        string name = item2.Name;
                        if (name == "tableContextMenuItemFullScanCheckAllCharts")
                        {
                            menuItemFullScanAllCharts = item2 as MenuItem;
                        }
                        else if (!(name == "tableContextMenuItemIgnoreFileScanCheck"))
                        {
                            if (name == "tableContextMenuItemNotIgnoreFileScanCheck")
                            {
                                menuItem12 = item2 as MenuItem;
                            }
                        }
                        else
                        {
                            menuItem11 = item2 as MenuItem;
                        }
                    }
                    break;
                case "tableContextMenuItemMoveFile":
                    menuItem13 = item as MenuItem;
                    break;
                case "tableContextMenuItemDeleteEntry":
                    menuItem14 = item as MenuItem;
                    break;
                case "tableContextMenuItemDeleteFile":
                    menuItem15 = item as MenuItem;
                    foreach (Control item3 in (IEnumerable)menuItem15.Items)
                    {
                        if (item3.Name == "tableContextMenuItemRenameInvalidExt")
                        {
                            menuItemRenameInvalidExt = item3 as MenuItem;
                        }
                    }
                    break;
                case "tableContextMenuItemAutoRenameFolder":
                    menuItem16 = item as MenuItem;
                    break;
                case "tableContextMenuItemFixEncoding":
                    menuItem17 = item as MenuItem;
                    break;
                case "tableContextMenuSeparatorForFolderview":
                    separator = item as Separator;
                    break;
                case "tableContextMenuItemConvertToAudioFile":
                    menuItem19 = item as MenuItem;
                    break;
                case "tableContextMenuSeparatorForConvert":
                    separator2 = item as Separator;
                    break;
                case "tableContextMenuItemRemoveChartInfoParseFailure":
                    menuItemRemoveChartInfoParseFailure = item as MenuItem;
                    break;
            }
        }
        List<object> effectivePlaylistUrlRows = GetEffectiveContextMenuRows(row);
        bool isBulkPlaylistUrlContext = effectivePlaylistUrlRows.Count > 1;
        if (isPlaylistRow)
        {
            if (menuItem != null)
            {
                menuItem.Header = isBulkPlaylistUrlContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url : BeMusicSeeker.Properties.Resources.Open_Url;
                menuItem.Visibility = Visibility.Visible;
                menuItem.IsEnabled = !IsPlaylistUrlDownloadRunning && (isBulkPlaylistUrlContext
                    ? PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(effectivePlaylistUrlRows, isDiffUrl: false).Count > 0
                    : rowUrl != null && rowUrl.IsAbsoluteUri);
            }
            if (menuItem2 != null)
            {
                menuItem2.Header = isBulkPlaylistUrlContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url_diff : BeMusicSeeker.Properties.Resources.Open_Url_diff;
                menuItem2.Visibility = Visibility.Visible;
                menuItem2.IsEnabled = !IsPlaylistUrlDownloadRunning && (isBulkPlaylistUrlContext
                    ? PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(effectivePlaylistUrlRows, isDiffUrl: true).Count > 0
                    : rowUrlDiff != null && rowUrlDiff.IsAbsoluteUri);
            }
            if (menuItemFindExternalPackage != null)
            {
                menuItemFindExternalPackage.Visibility = Visibility.Visible;
                menuItemFindExternalPackage.IsEnabled = CanStartPlaylistExternalPackageLookup(effectivePlaylistUrlRows);
            }
        }
        else
        {
            if (menuItem != null)
            {
                menuItem.Visibility = Visibility.Collapsed;
            }
            if (menuItem2 != null)
            {
                menuItem2.Visibility = Visibility.Collapsed;
            }
            if (menuItemFindExternalPackage != null)
            {
                menuItemFindExternalPackage.Visibility = Visibility.Collapsed;
            }
        }
        if (menuItem3 != null && menuItem4 != null && menuItemOpenDocument != null && changeSubmenuOpenDocumentTask == null)
        {
            menuItemOpenDocument.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(chartPath) && LongPathFileSystem.FileExists(chartPath))
            {
                menuItem3.IsEnabled = true;
                menuItem4.IsEnabled = true;
                menuItemOpenDocument.Visibility = Visibility.Visible;
                changeSubmenuOpenDocumentTask = Task.Run(delegate
                {
                    NLogWrapper.DebuggerLogger?.Trace("Test starts: changeSubmenuOpenDocumentTask");
                    CancellationToken token = tableContextMenuTaskTokenSource.Token;
                    string directoryNameSimple = DirectoryExt.GetDirectoryNameSimple(chartPath);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                    try
                    {
                        List<string> list2 = [.. LongPathFileSystem.EnumerateFiles(directoryNameSimple, "*.txt"), .. LongPathFileSystem.EnumerateFiles(directoryNameSimple, "*.htm?")];
                        if (list2.Count > 0)
                        {
                            base.Dispatcher.BeginInvoke((Action)delegate
                            {
                                if (!token.IsCancellationRequested && !_isClosingOrClosed)
                                {
                                    menuItemOpenDocument.ItemsSource = list2;
                                    menuItemOpenDocument.IsEnabled = true;
                                }
                            });
                        }
                    }
                    catch
                    {
                        base.Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (!token.IsCancellationRequested && !_isClosingOrClosed)
                            {
                                menuItemOpenDocument.Visibility = Visibility.Collapsed;
                            }
                        });
                    }
                }, tableContextMenuTaskTokenSource.Token).Logging("tableContextMenuOpened");
            }
            else
            {
                menuItem3.IsEnabled = false;
                menuItem4.IsEnabled = false;
                menuItemOpenDocument.Visibility = Visibility.Collapsed;
            }
        }
        bool flag = false;
        bool hasScoreViewerTarget = contextMenuState.HasScoreViewerTarget;
        if (menuItem18 != null && selectedTargets.Count > 1)
        {
            menuItem18.Header = BeMusicSeeker.Properties.Resources.Register_chart_with_viewer;
            flag = (menuItem18.IsEnabled = selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.UseScoreViewer) && !string.IsNullOrWhiteSpace(target.Chart.Path) && LongPathFileSystem.FileExists(target.Chart.Path)));
        }
        else if (menuItem18 != null)
        {
            menuItem18.Header = BeMusicSeeker.Properties.Resources.Open_chart_viewer;
            flag = selectedTargets.Count == 1
                && selectedTargets[0].HasCapability(ChartOperationCapabilities.UseScoreViewer)
                && !string.IsNullOrWhiteSpace(selectedTargets[0].Chart.Path)
                && LongPathFileSystem.FileExists(selectedTargets[0].Chart.Path);
            menuItem18.IsEnabled = flag;
        }
        if (menuItem18 != null)
        {
            menuItem18.Visibility = hasScoreViewerTarget ? Visibility.Visible : Visibility.Collapsed;
        }
        if (menuItem19 != null)
        {
            menuItem19.IsEnabled = flag;
        }
        if (menuItemOpenLr2Ir != null)
        {
            bool canOpenLr2Ir = contextMenuState.CanOpenLr2Ir;
            menuItemOpenLr2Ir.Visibility = canOpenLr2Ir ? Visibility.Visible : Visibility.Collapsed;
            menuItemOpenLr2Ir.IsEnabled = canOpenLr2Ir;
        }
        string repositorySha256 = GridRowResolver.GetRepositorySha256(row);
        bool canOpenRepository = !string.IsNullOrWhiteSpace(repositorySha256);
        if (menuItemOpenMocha != null)
        {
            menuItemOpenMocha.Visibility = canOpenRepository ? Visibility.Visible : Visibility.Collapsed;
            menuItemOpenMocha.IsEnabled = canOpenRepository;
        }
        if (menuItemOpenMinIr != null)
        {
            menuItemOpenMinIr.Visibility = canOpenRepository ? Visibility.Visible : Visibility.Collapsed;
            menuItemOpenMinIr.IsEnabled = canOpenRepository;
        }
        if (menuItem7 != null)
        {
            bool hasRankingTarget = contextMenuState.HasRankingTarget;
            menuItem7.Visibility = hasRankingTarget ? Visibility.Visible : Visibility.Collapsed;
            menuItem7.IsEnabled = mainWindowViewModel.LR2ID != 0 && hasRankingTarget;
        }
        MenuItem menuItemDeleteInstallPackages = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "tableContextMenuItemDeleteInstallPackages");
        List<string> selectedChartInfoParseFailureMd5s = GetSelectedChartInfoParseFailureMd5s();
        if (menuItemRemoveChartInfoParseFailure != null)
        {
            bool canRemoveChartInfoParseFailure = ShouldShowChartInfoParseFailureRemovalMenu(IsChartInfoParseErrorMainViewSection(effectiveSection), selectedChartInfoParseFailureMd5s);
            menuItemRemoveChartInfoParseFailure.Visibility = canRemoveChartInfoParseFailure ? Visibility.Visible : Visibility.Collapsed;
            menuItemRemoveChartInfoParseFailure.IsEnabled = canRemoveChartInfoParseFailure;
        }
        bool isNotOwnedPlaylistRow = rowTarget?.IsPlaylistMissing == true;
        NLogWrapper.FileLogger?.Info("playlist_context_menu rowType=" + row?.GetType().FullName + " isPlaylistRow=" + isPlaylistRow + " isPlaylistContext=" + isPlaylistContext + " isNotOwned=" + isNotOwnedPlaylistRow + " section=" + effectiveSection + " treeSection=" + _currentTreeSelectionSection + " sourceScope=" + sourceScope + " kind=" + rowTarget?.Chart.Kind + " path=" + (chartPath ?? string.Empty));
        if (menuItemOpenInstallDestination != null)
        {
            bool canOpenInstallDestination = contextMenuState.CanOpenInstallDestination;
            menuItemOpenInstallDestination.Visibility = (canOpenInstallDestination ? Visibility.Visible : Visibility.Collapsed);
            menuItemOpenInstallDestination.IsEnabled = canOpenInstallDestination;
        }
        if (menuItem8 != null)
        {
            menuItem8.Visibility = ((!isPendingSelected || isPlaylistContext) ? Visibility.Collapsed : Visibility.Visible);
            menuItem8.IsEnabled = isPendingSelected && !isPlaylistContext;
        }
        if (menuItem10 != null)
        {
            bool isFullScanMenuVisible = contextMenuState.CanShowResourceHealthMenu;
            menuItem10.Visibility = ((!isFullScanMenuVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem10.IsEnabled = isFullScanMenuVisible;
        }
        if (menuItem13 != null)
        {
            bool canMoveSelectedFiles = contextMenuState.CanMoveSelectedFiles;
            menuItem13.Visibility = ((!canMoveSelectedFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem13.IsEnabled = canMoveSelectedFiles && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.MoveInLibrary) && !string.IsNullOrWhiteSpace(target.Chart.Path) && LongPathFileSystem.FileExists(target.Chart.Path));
        }
        if (menuItem14 != null)
        {
            menuItem14.Visibility = ((!isPlaylistContext) ? Visibility.Collapsed : Visibility.Visible);
            menuItem14.IsEnabled = isPlaylistContext;
        }
        if (menuItem15 != null)
        {
            bool canDeleteFiles = contextMenuState.CanDeleteFiles;
            menuItem15.Visibility = ((!canDeleteFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem15.IsEnabled = canDeleteFiles;
            if (menuItemRenameInvalidExt != null)
            {
                bool canRenameInvalidExt = contextMenuState.CanRenameInvalidExtension;
                menuItemRenameInvalidExt.Visibility = canRenameInvalidExt ? Visibility.Visible : Visibility.Collapsed;
                menuItemRenameInvalidExt.IsEnabled = canRenameInvalidExt;
            }
        }
        if (separator != null)
        {
            bool isFolderViewSeparatorVisible = contextMenuState.CanShowFolderViewSeparator;
            separator.Visibility = ((!isFolderViewSeparatorVisible) ? Visibility.Collapsed : Visibility.Visible);
            separator.IsEnabled = isFolderViewSeparatorVisible;
        }
        if (menuItem16 != null)
        {
            bool canAutoRenameFolders = contextMenuState.CanAutoRenameFolders
                && mainWindowViewModel.FolderAutoRenameWorkflow?.IsActive != true;
            menuItem16.Visibility = ((!canAutoRenameFolders) ? Visibility.Collapsed : Visibility.Visible);
            menuItem16.IsEnabled = canAutoRenameFolders;
        }
        if (menuItem17 != null)
        {
            bool canFixEncoding = contextMenuState.CanFixEncoding;
            menuItem17.Visibility = ((!canFixEncoding) ? Visibility.Collapsed : Visibility.Visible);
            menuItem17.IsEnabled = canFixEncoding;
        }
        if (menuItem9 != null)
        {
            bool isInstalledLocationFixVisible = IsFullScanMainViewSection(effectiveSection) && !isPlaylistContext;
            menuItem9.Visibility = ((!isInstalledLocationFixVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem9.IsEnabled = isInstalledLocationFixVisible && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        }
        if (menuItem11 != null)
        {
            bool isSelected = treeViewItemFullScanCheck.IsSelected;
            bool hasResourceHealthTarget = contextMenuState.HasResourceHealthTarget;
            menuItem11.Visibility = ((!isSelected) ? Visibility.Collapsed : Visibility.Visible);
            menuItem11.IsEnabled = isSelected && hasResourceHealthTarget;
        }
        if (menuItem12 != null)
        {
            bool isSelected2 = treeViewItemFullScanCheckIgnored.IsSelected;
            bool hasResourceHealthTarget = contextMenuState.HasResourceHealthTarget;
            menuItem12.Visibility = ((!isSelected2) ? Visibility.Collapsed : Visibility.Visible);
            menuItem12.IsEnabled = isSelected2 && hasResourceHealthTarget;
        }
        if (menuItemFullScanAllCharts != null)
        {
            bool canRescanAllCharts = menuItem10?.Visibility == Visibility.Visible
                && mainWindowViewModel.MaintenanceRescanWorkflow?.IsActive != true;
            menuItemFullScanAllCharts.Visibility = Visibility.Visible;
            menuItemFullScanAllCharts.IsEnabled = canRescanAllCharts;
        }
        if (menuItem19 != null && separator2 != null)
        {
            bool canConvertToAudio = contextMenuState.CanConvertToAudio;
            Separator convertSeparator = separator2;
            Visibility visibility = (menuItem19.Visibility = ((!canConvertToAudio) ? Visibility.Collapsed : Visibility.Visible));
            convertSeparator.Visibility = visibility;
            Separator convertSeparator2 = separator2;
            bool isEnabled = (menuItem19.IsEnabled = canConvertToAudio && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.ConvertToAudio) && !string.IsNullOrWhiteSpace(target.Chart.Path) && LongPathFileSystem.FileExists(target.Chart.Path)));
            convertSeparator2.IsEnabled = isEnabled;
        }
        if (hasBmsonSelection)
        {
            if (!hasBmsSelection)
            {
                if (menuItemOpenLr2Ir != null)
                {
                    menuItemOpenLr2Ir.Visibility = Visibility.Collapsed;
                    menuItemOpenLr2Ir.IsEnabled = false;
                }
                if (menuItem18 != null)
                {
                    menuItem18.Visibility = Visibility.Collapsed;
                    menuItem18.IsEnabled = false;
                }
                if (menuItem7 != null)
                {
                    menuItem7.Visibility = Visibility.Collapsed;
                    menuItem7.IsEnabled = false;
                }
                if (menuItem19 != null)
                {
                    menuItem19.Visibility = Visibility.Collapsed;
                    menuItem19.IsEnabled = false;
                }
                if (menuItemRenameInvalidExt != null)
                {
                    menuItemRenameInvalidExt.Visibility = Visibility.Collapsed;
                    menuItemRenameInvalidExt.IsEnabled = false;
                }
                if (menuItem17 != null)
                {
                    menuItem17.Visibility = Visibility.Collapsed;
                    menuItem17.IsEnabled = false;
                }
            }
        }
        if (menuItemDeleteInstallPackages != null)
        {
            menuItemDeleteInstallPackages.Visibility = (isInstallListSelected ? Visibility.Visible : Visibility.Collapsed);
            menuItemDeleteInstallPackages.IsEnabled = contextMenuState.CanDeleteInstallPackages;
        }
        if (isNotOwnedPlaylistRow)
        {
            if (menuItem3 != null)
            {
                menuItem3.Visibility = Visibility.Collapsed;
                menuItem3.IsEnabled = false;
            }
            if (menuItem4 != null)
            {
                menuItem4.Visibility = Visibility.Collapsed;
                menuItem4.IsEnabled = false;
            }
            if (menuItemOpenDocument != null)
            {
                menuItemOpenDocument.Visibility = Visibility.Collapsed;
                menuItemOpenDocument.IsEnabled = false;
            }
            if (menuItem18 != null)
            {
                menuItem18.Visibility = Visibility.Collapsed;
                menuItem18.IsEnabled = false;
            }
            if (menuItem7 != null)
            {
                menuItem7.Visibility = Visibility.Collapsed;
                menuItem7.IsEnabled = false;
            }
            if (menuItem8 != null)
            {
                menuItem8.Visibility = Visibility.Collapsed;
                menuItem8.IsEnabled = false;
            }
            if (menuItem9 != null)
            {
                menuItem9.Visibility = Visibility.Collapsed;
                menuItem9.IsEnabled = false;
            }
            if (menuItem10 != null)
            {
                menuItem10.Visibility = Visibility.Collapsed;
                menuItem10.IsEnabled = false;
            }
            if (menuItem13 != null)
            {
                menuItem13.Visibility = Visibility.Collapsed;
                menuItem13.IsEnabled = false;
            }
            if (menuItem15 != null)
            {
                menuItem15.Visibility = Visibility.Collapsed;
                menuItem15.IsEnabled = false;
            }
            if (menuItem16 != null)
            {
                menuItem16.Visibility = Visibility.Collapsed;
                menuItem16.IsEnabled = false;
            }
            if (menuItem17 != null)
            {
                menuItem17.Visibility = Visibility.Collapsed;
                menuItem17.IsEnabled = false;
            }
            if (menuItem19 != null)
            {
                menuItem19.Visibility = Visibility.Collapsed;
                menuItem19.IsEnabled = false;
            }
            if (separator != null)
            {
                separator.Visibility = Visibility.Collapsed;
                separator.IsEnabled = false;
            }
            if (separator2 != null)
            {
                separator2.Visibility = Visibility.Collapsed;
                separator2.IsEnabled = false;
            }
            if (menuItemDeleteInstallPackages != null)
            {
                menuItemDeleteInstallPackages.Visibility = Visibility.Collapsed;
                menuItemDeleteInstallPackages.IsEnabled = false;
            }
        }
    }

    private void tableContextMenuPlaylistMissingOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_playlist_missing_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row))
        {
            NLogWrapper.FileLogger?.Info("playlist_missing_context_menu rowResolve=False sourceType=" + sender?.GetType().FullName);
            return;
        }
        _lastOpenedContextMenu = contextMenu;
        BMSTableEntry entry = GridRowResolver.GetPlaylistEntry(row);
        Uri rowUrl = GridRowResolver.GetUrl(row);
        Uri rowUrlDiff = GridRowResolver.GetUrlDiff(row);
        GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget rowTarget);
        bool isBmsonContextRow = rowTarget?.Chart.Kind == ChartFileKind.Bmson;
        string repositorySha256 = GridRowResolver.GetRepositorySha256(row);
        bool canOpenRepository = !string.IsNullOrWhiteSpace(repositorySha256);
        bool canOpenScoreViewer = rowTarget?.HasCapability(ChartOperationCapabilities.UseScoreViewer) == true;
        bool canUpdateRanking = rowTarget?.HasCapability(ChartOperationCapabilities.UpdateRanking) == true && base.DataContext is MainWindowViewModel viewModel && viewModel.LR2ID != 0;
        bool canOpenLr2Ir = rowTarget?.HasCapability(ChartOperationCapabilities.UseLr2Ir) == true;
        List<object> effectivePlaylistUrlRows = GetEffectiveContextMenuRows(row);
        bool isBulkPlaylistUrlContext = effectivePlaylistUrlRows.Count > 1;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "tableContextMenuItemOpenLR2IR":
                    item.Visibility = canOpenLr2Ir ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canOpenLr2Ir;
                    break;
                case "tableContextMenuItemOpenMocha":
                case "tableContextMenuItemOpenMinIR":
                    item.Visibility = canOpenRepository ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canOpenRepository;
                    break;
                case "tableContextMenuItemOpenURL":
                    if (item is MenuItem openUrlMenuItem)
                    {
                        openUrlMenuItem.Header = isBulkPlaylistUrlContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url : BeMusicSeeker.Properties.Resources.Open_Url;
                    }
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = !IsPlaylistUrlDownloadRunning && (isBulkPlaylistUrlContext
                        ? PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(effectivePlaylistUrlRows, isDiffUrl: false).Count > 0
                        : rowUrl != null && rowUrl.IsAbsoluteUri);
                    break;
                case "tableContextMenuItemOpenURLdiff":
                    if (item is MenuItem openUrlDiffMenuItem)
                    {
                        openUrlDiffMenuItem.Header = isBulkPlaylistUrlContext ? BeMusicSeeker.Properties.Resources.Import_Selected_Url_diff : BeMusicSeeker.Properties.Resources.Open_Url_diff;
                    }
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = !IsPlaylistUrlDownloadRunning && (isBulkPlaylistUrlContext
                        ? PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(effectivePlaylistUrlRows, isDiffUrl: true).Count > 0
                        : rowUrlDiff != null && rowUrlDiff.IsAbsoluteUri);
                    break;
                case "tableContextMenuItemFindExternalPackage":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = CanStartPlaylistExternalPackageLookup(effectivePlaylistUrlRows);
                    break;
                case "tableContextMenuItemDeleteEntry":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = entry != null;
                    break;
                case "tableContextMenuItemRegisterScore":
                    item.Visibility = canOpenScoreViewer ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canOpenScoreViewer;
                    break;
                case "tableContextMenuItemUpdateRankingData":
                    item.Visibility = canUpdateRanking ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = canUpdateRanking;
                    break;
            }
        }
        NLogWrapper.FileLogger?.Info("playlist_missing_context_menu rowType=" + row?.GetType().FullName + " entryParent=" + entry?.parent?.name + " isBmson=" + isBmsonContextRow + " url=" + (rowUrl != null) + " urlDiff=" + (rowUrlDiff != null) + " canOpenLr2Ir=" + canOpenLr2Ir + " canOpenRepository=" + canOpenRepository + " canOpenScoreViewer=" + canOpenScoreViewer + " canUpdateRanking=" + canUpdateRanking);
    }

    private void playHistoryContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_play_history_context_menu_opened"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out object row)
            || !TryGetPlayHistoryContextMenuState(row, out PlayHistoryContextMenuState state))
        {
            return;
        }

        calcelAllContextMenuTasks();
        _lastOpenedContextMenu = contextMenu;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "playHistoryContextMenuItemOpenBMSIR":
                    item.Visibility = state.CanOpenBmsIr ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanOpenBmsIr;
                    break;
                case "playHistoryContextMenuItemOpenMocha":
                case "playHistoryContextMenuItemOpenMinIR":
                    item.Visibility = state.CanOpenRepository ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanOpenRepository;
                    break;
                case "playHistoryContextMenuSeparatorLocal":
                    item.Visibility = state.HasExternalLinkItem && state.HasLocalChartItem ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "playHistoryContextMenuItemOpenExplorer":
                    item.Visibility = state.CanOpenExplorer ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanOpenExplorer && LongPathFileSystem.FileExists(state.ChartPath);
                    break;
                case "playHistoryContextMenuItemRegisterScore":
                    item.Visibility = state.CanOpenScoreViewer ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanOpenScoreViewer && LongPathFileSystem.FileExists(state.ChartPath);
                    break;
                case "playHistoryContextMenuSeparatorHash":
                    item.Visibility = (state.HasExternalLinkItem || state.HasLocalChartItem) && state.HasHashCopyItem ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "playHistoryContextMenuItemCopyMd5":
                    item.Visibility = state.CanCopyMd5 ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanCopyMd5;
                    break;
                case "playHistoryContextMenuItemCopySha256":
                    item.Visibility = state.CanCopySha256 ? Visibility.Visible : Visibility.Collapsed;
                    item.IsEnabled = state.CanCopySha256;
                    break;
            }
        }
    }

    private void playHistoryContextMenuItemOpenBMSIRClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPlayHistoryContextMenuAction(
                e.Source,
                PlayHistoryContextMenuActionKind.OpenBmsIr,
                out PlayHistoryContextMenuAction action)
            || string.IsNullOrWhiteSpace(action.Url))
        {
            return;
        }

        Process.Start(action.Url);
        e.Handled = true;
    }

    private void playHistoryContextMenuItemOpenMochaClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPlayHistoryContextMenuAction(
                e.Source,
                PlayHistoryContextMenuActionKind.OpenMocha,
                out PlayHistoryContextMenuAction action)
            || string.IsNullOrWhiteSpace(action.Url))
        {
            return;
        }

        Process.Start(action.Url);
        e.Handled = true;
    }

    private void playHistoryContextMenuItemOpenMinIRClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPlayHistoryContextMenuAction(
                e.Source,
                PlayHistoryContextMenuActionKind.OpenMinIr,
                out PlayHistoryContextMenuAction action)
            || string.IsNullOrWhiteSpace(action.Url))
        {
            return;
        }

        Process.Start(action.Url);
        e.Handled = true;
    }

    private void playHistoryContextMenuItemCopyHashClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string copyKind })
        {
            return;
        }

        PlayHistoryContextMenuActionKind? actionKind = copyKind switch
        {
            PlayHistoryContextMenuState.CopyMd5Kind => PlayHistoryContextMenuActionKind.CopyMd5,
            PlayHistoryContextMenuState.CopySha256Kind => PlayHistoryContextMenuActionKind.CopySha256,
            _ => null
        };
        if (!actionKind.HasValue
            || !TryGetPlayHistoryContextMenuAction(sender, actionKind.Value, out PlayHistoryContextMenuAction action)
            || string.IsNullOrWhiteSpace(action.Value))
        {
            return;
        }

        Clipboard.SetText(action.Value);
        e.Handled = true;
    }

    private void playHistoryContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPlayHistoryContextMenuAction(
                e.Source,
                PlayHistoryContextMenuActionKind.OpenExplorer,
                out PlayHistoryContextMenuAction action)
            || string.IsNullOrWhiteSpace(action.Path)
            || !LongPathFileSystem.FileExists(action.Path))
        {
            return;
        }

        ExplorerOpenService.OpenFileAndSelect(action.Path);
        e.Handled = true;
    }

    private async void playHistoryContextMenuItemRegisterScoreViewerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetPlayHistoryContextMenuAction(
                e.Source,
                PlayHistoryContextMenuActionKind.RegisterScoreViewer,
                out PlayHistoryContextMenuAction action)
            || action.ScoreViewerTarget == null)
        {
            return;
        }

        var targets = new List<ScoreViewerTarget> { action.ScoreViewerTarget };
        e.Handled = true;
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ScoreViewerRegistration.RunAsync(
                targets,
                openSingleViewerOnSuccess: true,
                "playHistoryContextMenuItemRegisterScoreViewerClick");
        }
    }

    private void tableContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row))
        {
            return;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target) || !target.HasCapability(ChartOperationCapabilities.OpenFolder))
        {
            return;
        }
        string path = target.Chart.Path;
        if (!LongPathFileSystem.FileExists(path))
        {
            return;
        }
        ExplorerOpenService.OpenFileAndSelect(path);
    }

    private bool TryResolveInstallDestination(ChartFile chart, out string installDir, out string reason)
    {
        installDir = null;
        reason = null;
        if (chart == null)
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(chart.InstallDestination))
        {
            if (LongPathFileSystem.DirectoryExists(chart.InstallDestination))
            {
                installDir = chart.InstallDestination;
                return true;
            }
            reason = string.Format(BeMusicSeeker.Properties.Resources.Msg_open_install_destination_not_found, chart.InstallDestination);
            return false;
        }
        string lookupHash = ChartLookupKey.GetPrimaryHash(chart);
        if (!string.IsNullOrWhiteSpace(lookupHash)
            && base.DataContext is MainWindowViewModel mainWindowViewModel
            && mainWindowViewModel.TryGetInstalledDirectoryByHash(lookupHash, out string installDir2))
        {
            installDir = installDir2;
            return true;
        }
        reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
        return false;
    }

    private bool TryResolveInstallDestination(ChartPackage pkg, out string installDir, out string reason)
    {
        installDir = null;
        reason = null;
        if (pkg == null)
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
            return false;
        }
        foreach (var entry in pkg.ChartEntries)
        {
            if (TryResolveInstallDestination(entry?.Chart, out installDir, out reason))
            {
                return true;
            }
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
        }
        return false;
    }

    private void OpenInstallDestinationInExplorer(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return;
        }
        ExplorerOpenService.OpenDirectory(installDir);
    }

    private void tableContextMenuItemOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel || !IsPendingMainViewSection(GetCurrentMainViewOperationSection()))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets.Count == 0)
        {
            return;
        }
        if (targets.Count > 1)
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_open_install_destination_multiple_selected, BeMusicSeeker.Properties.Resources.Information, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
        }
        if (!TryResolveInstallDestination(targets[0].Chart, out string installDir, out string reason))
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), reason, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        OpenInstallDestinationInExplorer(installDir);
    }

    private void treeViewInstallPackageContextMenuOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: ChartPackage dataContext } } }))
        {
            return;
        }
        if (!TryResolveInstallDestination(dataContext, out string installDir, out string reason))
        {
            UiDialogRoute.ShowMessageBox(Window.GetWindow(this), reason, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        OpenInstallDestinationInExplorer(installDir);
    }

    private static string GetBmsIrSongUrl(string md5)
    {
        if (string.IsNullOrWhiteSpace(md5) || !Regex.IsMatch(md5.Trim(), "^[A-F0-9]{32}$", RegexOptions.IgnoreCase))
        {
            return null;
        }
        return "https://bms-ir.org/new/song?songmd5=" + md5.Trim() + "&view=both";
    }

    private static string GetMochaSongUrl(string sha256)
    {
        return string.IsNullOrWhiteSpace(sha256) ? null : "https://mocha-repository.info/song.php?sha256=" + sha256;
    }

    private static string GetMinIrSongUrl(string sha256)
    {
        return string.IsNullOrWhiteSpace(sha256) ? null : "https://www.gaftalk.com/minir/#/viewer/song/" + sha256 + "/0";
    }

    private void OpenRepositoryUrlForRow(object row, Func<string, string> urlFactory)
    {
        string sha256 = GridRowResolver.GetRepositorySha256(row);
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }
        string url = urlFactory?.Invoke(sha256);
        if (!string.IsNullOrWhiteSpace(url))
        {
            Process.Start(url);
        }
    }

    private void tableContextMenuItemOpenBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row))
        {
            return;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target) || !target.HasCapability(ChartOperationCapabilities.OpenFile))
        {
            return;
        }
        string path = target.Chart.Path;
        if (!LongPathFileSystem.FileExists(path))
        {
            return;
        }
        try
        {
            Process.Start(path);
        }
        catch
        {
        }
    }

    private void tableContextMenuItemOpenLR2IRClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out object row))
        {
            return;
        }
        if (!GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target) || !target.HasCapability(ChartOperationCapabilities.UseLr2Ir))
        {
            return;
        }
        string text = GetBmsIrSongUrl(target.Chart.Md5);
        if (text != null)
        {
            Process.Start(text);
        }
    }

    private void tableContextMenuItemOpenMochaClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out object row) && row is not PlayHistoryRow)
        {
            OpenRepositoryUrlForRow(row, GetMochaSongUrl);
        }
    }

    private void tableContextMenuItemOpenMinIRClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out object row) && row is not PlayHistoryRow)
        {
            OpenRepositoryUrlForRow(row, GetMinIrSongUrl);
        }
    }

    private async void tableContextMenuItemOpenURLClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await OpenPlaylistUrlFromContextMenuAsync(e.Source, isDiffUrl: false, "datagrid_context_menu_open_url").Logging("tableContextMenuItemOpenURLClick");
    }

    private async void tableContextMenuItemOpenURLdiffClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await OpenPlaylistUrlFromContextMenuAsync(e.Source, isDiffUrl: true, "datagrid_context_menu_open_url_diff").Logging("tableContextMenuItemOpenURLdiffClick");
    }

    private async void tableContextMenuItemFindExternalPackageClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await FindExternalPackageFromContextMenuAsync(e.Source).Logging("tableContextMenuItemFindExternalPackageClick");
    }

    private async Task FindExternalPackageFromContextMenuAsync(object source)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_find_external_package"))
        {
            return;
        }
        if (!TryGetContextMenuRow(source, out object contextRow))
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.PlaylistWorkspace.DownloadSelectedPlaylistExternalPackagesAsync(
                PlaylistContextMenuTargetResolver.BuildPlaylistExternalPackageMd5Targets(GetEffectiveContextMenuRows(contextRow)));
        }
    }

    private async Task OpenPlaylistUrlFromContextMenuAsync(object source, bool isDiffUrl, string blockReason)
    {
        if (ShouldBlockStartupUiInteraction(blockReason))
        {
            return;
        }
        if (!TryGetContextMenuRow(source, out object contextRow))
        {
            return;
        }
        List<object> rows = GetEffectiveContextMenuRows(contextRow);
        if (rows.Count <= 1)
        {
            Uri url = isDiffUrl ? GridRowResolver.GetUrlDiff(contextRow) : GridRowResolver.GetUrl(contextRow);
            if (base.DataContext is MainWindowViewModel viewModel && url != null && url.IsAbsoluteUri)
            {
                await viewModel.PlaylistWorkspace.OpenSinglePlaylistUrlAsync(url);
            }
            return;
        }
        if (base.DataContext is MainWindowViewModel bulkViewModel)
        {
            await bulkViewModel.PlaylistWorkspace.DownloadSelectedPlaylistUrlsAsync(
                PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets(rows, isDiffUrl),
                isDiffUrl);
        }
    }

    private List<object> GetEffectiveContextMenuRows(object contextRow)
    {
        if (contextRow == null)
        {
            return [];
        }
        List<object> selectedRows = GetSelectedGridRowsSnapshot();
        if (selectedRows.Any(row => ReferenceEquals(row, contextRow)))
        {
            return selectedRows;
        }
        return [contextRow];
    }

    private bool CanStartPlaylistExternalPackageLookup(IEnumerable<object> rows)
    {
        return !IsPlaylistUrlDownloadRunning
            && base.DataContext is MainWindowViewModel viewModel
            && !viewModel.PackageInstallWorkflow.IsActive
            && PlaylistContextMenuTargetResolver.BuildPlaylistExternalPackageMd5Targets(rows).Count > 0;
    }


    private void tableContextMenuItemOpenDocumentFileClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { DataContext: string dataContext } && LongPathFileSystem.FileExists(dataContext))
        {
            Process.Start(dataContext);
        }
    }

    private void cancelDropInstallQueueClick(object sender, RoutedEventArgs e)
    {
        if (IsPlaylistUrlDownloadRunning)
        {
            (base.DataContext as MainWindowViewModel)?.PlaylistWorkspace.CancelPlaylistUrlDownload();
            return;
        }
        (base.DataContext as MainWindowViewModel)?.PackageInstallWorkflow.CancelAll();
    }

    private void cancelMaintenanceRescanClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.MaintenanceRescanWorkflow?.Cancel();
    }

    private void retryLr2SongDbSyncClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.RetryLr2SongDbSync();
    }

    private void cancelLr2SongDbSyncClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.CancelLr2SongDbSync();
    }

    private void cleanupLr2SongDbSyncStartupScanBlockersClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.CleanupLr2SongDbSyncStartupScanBlockersAndRetry();
    }

    private void tableContextMenuItemUpdateRankingDataClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<string> hashes = GetSelectedGridHashTargets();
        if (hashes != null && hashes.Count != 0)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.GetLR2IRCacheHashes(hashes);
            }).Logging("tableContextMenuItemUpdateRankingDataClick");
            e.Handled = true;
        }
    }

    private async void tableContextMenuItemRegisterBMSFileToScoreViwer(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ScoreViewerTarget> targets = GetSelectedGridScoreViewerTargets();
        if (targets.Count == 0)
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ScoreViewerRegistration.RunAsync(
                targets,
                openSingleViewerOnSuccess: targets.Count == 1,
                "tableContextMenuItemRegisterBMSFileToScoreViwer");
        }
    }

    private void tableContextMenuItemForceFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
        if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
        {
            Task.Run(delegate
            {
                viewModel.ForceResourceHealthCheckCharts(request);
            }).Logging("tableContextMenuItemForceFileScanCheckSelectedCharts");
            e.Handled = true;
        }
    }

    private async void tableContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("table_context_menu_remove_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        bool isInstalledLocationRepair = IsFullScanMainViewSection(GetCurrentMainViewOperationSection());
        ChartOperationCapabilities capability = isInstalledLocationRepair
            ? ChartOperationCapabilities.RepairInstalledLocation
            : ChartOperationCapabilities.UpdateInstallDestination;
        RepairInstalledLocationRequest repairRequest = null;
        if (isInstalledLocationRepair)
        {
            List<ChartOperationTarget> targets = GetSelectedChartTargets(capability);
            RepairInstalledLocationRequest.TryCreate(targets, out repairRequest);
        }
        PendingInstallDestinationClearRequest pendingInstallRequest = null;
        if (!isInstalledLocationRepair)
        {
            List<ChartOperationTarget> pendingTargets = GetSelectedChartTargets(capability, isPendingSection: true);
            PendingInstallDestinationClearRequest.TryCreate(pendingTargets, out pendingInstallRequest);
        }
        if ((repairRequest?.HasTargets == true) || (pendingInstallRequest?.HasTargets == true))
        {
            e.Handled = true;
            if (isInstalledLocationRepair)
            {
                await viewModel.PendingPackages
                    .ClearCorrectAsync(repairRequest)
                    .LoggingAndPropagate("tableContextMenuRemoveInstallDestinationClick");
            }
            else
            {
                await viewModel.PendingPackages
                    .ClearPendingAsync(pendingInstallRequest)
                    .LoggingAndPropagate("tableContextMenuRemoveInstallDestinationClick");
            }
        }
    }

    private async void tableContextMenuSearchCorrectInstallationDirectoryChartsClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        if (ShouldBlockChartPackageMutationInteraction("table_context_menu_search_correct_installation_directory"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation);
        if (RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            e.Handled = true;
            await viewModel.PendingPackages
                .SearchCorrectAsync(request)
                .LoggingAndPropagate("tableContextMenuSearchCorrectInstallationDirectoryChartsClick");
        }
    }

    private async void tableContextMenuFixInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_fix_installation_directory"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation);
        if (!RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.PendingPackages
            .FixInstalledLocationsAsync(request)
            .LoggingAndPropagate("tableContextMenuFixInstallationDirectoryClick");
    }

    private void tableContextMenuItemDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        List<BMSTableEntry> list2 = GetSelectedGridPlaylistEntries();
        var viewModel = base.DataContext as MainWindowViewModel;
        if (list2.Count <= 0)
        {
            return;
        }
        Task[] deleteTasks = [.. (from entry in list2
                                  group entry by entry.parent)
            .Select(group => viewModel.PlaylistWorkspace.DeleteEntriesAsync(group.AsEnumerable(), group.Key))];
        Task.WhenAll(deleteTasks).Logging("tableContextMenuItemDeleteEntryClick");
    }

    private void tableContextMenuItemForceFileScanCheckAllCharts(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_full_scan_all_charts"))
        {
            e.Handled = true;
            return;
        }
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_rescan_all_charts_confirm,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        viewModel?.MaintenanceRescanWorkflow?.Start();
        e.Handled = true;
    }

    private void tableContextMenuItemRemoveChartInfoParseFailureClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _) || !IsChartInfoParseErrorMainViewSection(GetCurrentMainViewOperationSection()))
        {
            return;
        }
        List<string> md5s = GetSelectedChartInfoParseFailureMd5s();
        if (base.DataContext is not MainWindowViewModel viewModel || md5s.Count == 0)
        {
            return;
        }
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_chart_info_parse_failure_record, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        Task.Run(delegate
        {
            viewModel.RemoveChartInfoParseFailuresByMd5(md5s);
        }).Logging("tableContextMenuItemRemoveChartInfoParseFailureClick");
    }

    private void tableContextMenuItemAutoRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_auto_rename_folder"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.MoveInLibrary);
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (ChartFolderAutoRenameRequest.TryCreate(targets, out ChartFolderAutoRenameRequest request)
            && viewModel.FolderAutoRenameWorkflow?.IsActive != true)
        {
            viewModel.FolderAutoRenameWorkflow.StartSelected(request);
        }
        e.Handled = true;
    }

    private void tableContextMenuItemRenameBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_rename_invalid_extension"))
        {
            e.Handled = true;
            return;
        }
        List<ChartFile> charts = GetSelectedBmsFormatCharts(ChartOperationCapabilities.RenameInvalidExtension);
        var viewModel = base.DataContext as MainWindowViewModel;
        bool isPendingSelected = IsPendingMainViewSection(GetCurrentMainViewOperationSection());
        if (charts.Count <= 0 || UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_rename_to_invalid, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        Task.Run(delegate
        {
            List<ChartFile> list = [.. charts.Where(chart => (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".b", StringComparison.OrdinalIgnoreCase))];
            List<ChartFile> list2 = [.. charts.Where(chart => (Path.GetExtension(chart.Path) ?? string.Empty).StartsWith(".p", StringComparison.OrdinalIgnoreCase))];
            if (list.Count > 0)
            {
                if (isPendingSelected)
                {
                    viewModel.RenamePendingBmsFormatChartFileExtensions(list, ".bmx");
                }
                else
                {
                    viewModel.RenameBMSFilesExtensions(list, ".bmx");
                }
            }
            if (list2.Count > 0)
            {
                if (isPendingSelected)
                {
                    viewModel.RenamePendingBmsFormatChartFileExtensions(list2, ".pmx");
                }
                else
                {
                    viewModel.RenameBMSFilesExtensions(list2, ".pmx");
                }
            }
        }).Logging("tableContextMenuItemRenameBMSFileClick");
    }

    private void tableContextMenuItemRemoveBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_remove_chart"))
        {
            e.Handled = true;
            return;
        }
        MainViewOperationSection section = GetCurrentMainViewOperationSection();
        bool isPendingSelected = IsPendingMainViewSection(section);
        List<ChartOperationTarget> selectedTargets = GetSelectedChartTargets(isPendingSelected);
        TryGetContextMenuChartTarget(sender, e.Source, out ChartOperationTarget contextTarget);
        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(selectedTargets, contextTarget, section);
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info("delete_chart_request section=" + section
                + " contextScope=" + (resolution.ContextScope?.ToString() ?? "None")
                + " selected=" + resolution.SelectedInputCount
                + " fallback=" + resolution.UsedContextFallback
                + " route=" + resolution.Route.ToString().ToLowerInvariant()
                + " targetCount=" + resolution.Targets.Count
                + " droppedMixedScope=" + resolution.MixedScopeDroppedCount);
        }
        if (base.DataContext is not MainWindowViewModel viewModel || resolution.Targets.Count == 0)
        {
            return;
        }
        bool deleteContainingPackageFoldersWhenNoBms = false;
        if (resolution.Route == ChartDeleteRoute.Pending)
        {
            if (!ShowPendingDeleteConfirmDialog(out deleteContainingPackageFoldersWhenNoBms))
            {
                return;
            }
        }
        else if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_recycle, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            if (installPerformanceLoggingEnabled)
            {
                installPerformanceLogger.Info("delete_chart_confirm route=" + resolution.Route.ToString().ToLowerInvariant() + " accepted=False");
            }
            return;
        }
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info("delete_chart_confirm route=" + resolution.Route.ToString().ToLowerInvariant() + " accepted=True");
        }
        List<string> approvedWholeFolderDeletePaths = null;
        if (resolution.Route == ChartDeleteRoute.Library)
        {
            approvedWholeFolderDeletePaths = [];
            foreach (string folderPath in viewModel.GetLibraryWholeFolderDeleteConfirmationPaths(resolution.Targets))
            {
                bool approved = UiDialogRoute.ShowMessageBox(
                    Window.GetWindow(this),
                    string.Format(BeMusicSeeker.Properties.Resources.Confirm_DeleteFolderWithNoBms, folderPath),
                    BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes) == MessageBoxResult.Yes;
                if (installPerformanceLoggingEnabled)
                {
                    installPerformanceLogger.Info("delete_chart_folder_confirm path=" + folderPath + " accepted=" + approved);
                }
                if (approved)
                {
                    approvedWholeFolderDeletePaths.Add(folderPath);
                }
            }
        }
        Task.Run(delegate
        {
            if (resolution.Route == ChartDeleteRoute.Pending)
            {
                viewModel.RemovePendingCharts(resolution.Targets, sendToRecycleBin: true, deleteContainingPackageFoldersWhenNoBms: deleteContainingPackageFoldersWhenNoBms);
            }
            else
            {
                viewModel.RemoveLibraryCharts(resolution.Targets, approvedWholeFolderDeletePaths);
            }
        }).Logging("tableContextMenuItemRemoveBMSFileClick");
    }

    private bool ShowPendingDeleteConfirmDialog(out bool deleteContainingPackageFoldersWhenNoBms)
    {
        UiWindowDialogResult<bool> dialogResult = new UiDialogCoordinator()
            .ShowWindowAsync(new UiWindowDialogRequest<PendingDeleteConfirmDialog, bool>(
                () => new PendingDeleteConfirmDialog(),
                dialog => dialog.DeleteFolderWhenNoBmsChecked,
                this))
            .GetAwaiter()
            .GetResult();
        ThrowIfWindowDialogFailed(dialogResult.Status, dialogResult.Error, "Pending delete confirmation dialog");
        deleteContainingPackageFoldersWhenNoBms = dialogResult.Value;
        return dialogResult.IsAccepted;
    }

    private async void tableContextMenuItemMoveFileClick(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_move_chart"))
        {
            e.Handled = true;
            return;
        }
        List<ChartOperationTarget> targets = [.. GetSelectedChartTargets().Where(target => target.HasCapability(ChartOperationCapabilities.MoveInLibrary) && !string.IsNullOrWhiteSpace(target.Chart?.Path))];
        if (sender is not MenuItem menuItem)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && menuItem.DataContext is string dstDir && targets.Count > 0 && UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_other_root, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            if (!ChartLibraryMoveRequest.TryCreate(targets, dstDir, out ChartLibraryMoveRequest request))
            {
                return;
            }
            await Task.Run(delegate
            {
                viewModel.MoveLibraryCharts(request);
            }).Logging("tableContextMenuItemMoveFileClick");
        }
    }

    private void fixEncodingSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem menuItem && TryGetContextMenuRow(e.Source, out _))
        {
            List<BMSFile> list = GetSelectedBmsFiles(ChartOperationCapabilities.RunBmsEncodingFix);
            if (list != null && list.Count() != 0)
            {
                (base.DataContext as MainWindowViewModel).FixEncodingBMSFiles(list, menuItem.Tag.ToString());
                e.Handled = true;
            }
        }
    }

    private void ignoreFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out _))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
            if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
            {
                viewModel.SetChartResourceWarningsIgnored(request);
                e.Handled = true;
            }
        }
    }

    private void notIgnoredFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out _))
        {
            if (base.DataContext is not MainWindowViewModel viewModel)
            {
                return;
            }
            List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck);
            if (ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request))
            {
                viewModel.SetChartResourceWarningsIgnored(request, unset: true);
                e.Handled = true;
            }
        }
    }

    private async void forceInstallSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await ForceInstallSelectedPendingChartsAsync(e);
    }

    private async Task ForceInstallSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_force_install_pending"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallPackageOperationRequest request = PendingInstallPackageOperationRequest.CreateForceInstall(targets);
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.PendingPackages
            .InstallPendingAsync(request, () =>
            {
                ClearMainGridSelection();
                if (!treeViewItemInstallPending.IsSelected)
                {
                    SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "forceInstallSelectedPendingCharts");
                }
            })
            .LoggingAndPropagate("forceInstallSelectedPendingCharts");
    }

    private async void manualInstallSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await ManualInstallSelectedPendingChartsAsync(e);
    }

    private async Task ManualInstallSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_manual_install_pending"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallPackageOperationRequest request = PendingInstallPackageOperationRequest.CreateManualInstall(targets);
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        await viewModel.PendingPackages
            .InstallPendingAsync(request, () =>
            {
                ClearMainGridSelection();
                if (!treeViewItemInstallPending.IsSelected)
                {
                    SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "manualInstallSelectedPendingCharts");
                }
            })
            .LoggingAndPropagate("manualInstallSelectedPendingCharts");
    }

    private async void searchInstallDestinationSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await SearchInstallDestinationSelectedPendingChartsAsync(e);
    }

    private async Task SearchInstallDestinationSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_search_install_destination"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallDestinationSearchRequest request = PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch(targets);
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await viewModel.PendingPackages
            .SearchPendingAsync(request)
            .LoggingAndPropagate("searchInstallDestinationSelectedPendingCharts");
    }

    private async void tableContextMenuItemDeleteInstallPackagesClick(object sender, RoutedEventArgs e)
    {
        await DeleteInstallPackageRecordsFromContextMenuAsync(e);
    }

    private async Task DeleteInstallPackageRecordsFromContextMenuAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_delete_install_package_records"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        MainViewOperationSection section = GetCurrentMainViewOperationSection();
        bool isPendingSelected = IsPendingMainViewSection(section);
        bool isInstalledSelected = IsInstalledMainViewSection(section);
        if (!isPendingSelected && !isInstalledSelected)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (!TryCreateDeleteInstallPackageRecordsRequest(isPendingSelected, isInstalledSelected, out DeleteInstallPackageRecordsRequest request))
        {
            return;
        }
        string confirmationMessage = request.IsPending ? BeMusicSeeker.Properties.Resources.Msg_clear_selected_pendings : BeMusicSeeker.Properties.Resources.Msg_clear_selected_installed;
        if (UiDialogRoute.ShowMessageBox(Window.GetWindow(this), confirmationMessage, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        NLogWrapper.FileLogger?.Info("table_delete_install_packages requested section=" + section + " treeSection=" + _currentTreeSelectionSection + " selectedRows=" + request.SelectedRowCount);
        e.Handled = true;
        ClearMainGridSelection();
        if (request.IsPending)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "tableContextMenuItemDeleteInstallPackagesClick");
            await viewModel.PackageRecords
                .RemoveSelectionAsync(request)
                .Logging("tableContextMenuItemDeleteInstallPackagesClick");
            if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
            {
                await viewModel.RegularChartList
                    .NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected)
                    .Logging("tableContextMenuItemDeleteInstallPackagesClick");
            }
            return;
        }
        SelectNextSiblingOrRoot(newlyInstalledTreeViewItem, treeView.SelectedItem, "tableContextMenuItemDeleteInstallPackagesClick");
        await viewModel.PackageRecords
            .RemoveSelectionAsync(request)
            .Logging("tableContextMenuItemDeleteInstallPackagesClick");
        if (newlyInstalledTreeViewItem.IsSelected && newlyInstalledTreeViewItem.Items.Count == 0)
        {
            await viewModel.RegularChartList
                .NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected)
                .Logging("tableContextMenuItemDeleteInstallPackagesClick");
        }
    }

    private bool TryCreateDeleteInstallPackageRecordsRequest(bool isPendingSelected, bool isInstalledSelected, out DeleteInstallPackageRecordsRequest request)
    {
        request = null;
        if (isPendingSelected)
        {
            List<ChartOperationTarget> selectedPendingTargets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
            if (selectedPendingTargets.Count == 0)
            {
                return false;
            }

            request = DeleteInstallPackageRecordsRequest.CreatePending(selectedPendingTargets);
            return true;
        }
        if (isInstalledSelected)
        {
            List<ChartOperationTarget> selectedInstalledTargets = GetSelectedChartTargets(ChartOperationCapabilities.None);
            if (selectedInstalledTargets.Count == 0)
            {
                return false;
            }

            request = DeleteInstallPackageRecordsRequest.CreateInstalled(selectedInstalledTargets);
            return true;
        }

        return false;
    }

    private async void searchMergeDestinationSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        await SearchMergeDestinationSelectedPendingChartsAsync(e);
    }

    private async Task SearchMergeDestinationSelectedPendingChartsAsync(RoutedEventArgs e)
    {
        if (ShouldBlockChartPackageMutationInteraction("datagrid_search_merge_destination"))
        {
            e.Handled = true;
            return;
        }
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<ChartOperationTarget> targets = GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true);
        if (targets == null || targets.Count == 0)
        {
            return;
        }
        PendingInstallDestinationSearchRequest request = PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch(targets);
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await viewModel.PendingPackages
            .SearchPendingAsync(request)
            .LoggingAndPropagate("searchMergeDestinationSelectedPendingCharts");
    }

    private async void tableContextMenuItemConvertToAudioFileClick(object sender, RoutedEventArgs e)
    {
        BMSFile[] bmsFiles = [.. GetSelectedBmsFiles(ChartOperationCapabilities.ConvertToAudio).Where(f => LongPathFileSystem.FileExists(f.path))];
        if (bmsFiles.Length == 0)
        {
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        var cancelTokenSource = new CancellationTokenSource();
        int progIdx = 0;
        int failNum = 0;
        UiFolderPickerResult folderResult = await new UiDialogCoordinator().PickFolderAsync(new UiFolderPickerRequest(
            BeMusicSeeker.Properties.Resources.Save_to,
            owner: this));
        ThrowIfPickerFailed(folderResult.Status, folderResult.Error, "Audio conversion output folder picker");
        if (folderResult.Status != UiDialogStatus.Accepted)
        {
            return;
        }
        string saveDir = folderResult.FolderPath;
        Task task = Task.Run(delegate
        {
            viewModel.ConvertBMSToAudioFiles(bmsFiles, saveDir, cancelTokenSource.Token, delegate (bool s)
            {
                progIdx++;
                if (!s)
                {
                    failNum++;
                }
            });
        }, cancelTokenSource.Token).Logging("tableContextMenuItemConvertToAudioFileClick");
        await RunProgressUntilTaskCompletesAsync(
            task,
            cancelTokenSource,
            BeMusicSeeker.Properties.Resources.Converting,
            viewModel.settingDialog.EncoderNames[(int)Settings.Default.Encoder] + " - " + BeMusicSeeker.Properties.Resources.Sampling_rate + ":" + viewModel.settingDialog.PlayerSampleRateNames[Settings.Default.EncoderSampleRate] + " " + BeMusicSeeker.Properties.Resources.Sampling_format + ":" + viewModel.settingDialog.PlayerFormatNames[Settings.Default.EncoderFormat],
            context => context.ReportWithCancellationCheck(100 * (progIdx + 1) / (bmsFiles.Length + 1), "[{0}/{1}] {2}", Math.Min(progIdx + 1, bmsFiles.Length), bmsFiles.Length, bmsFiles[Math.Min(progIdx, bmsFiles.Length - 1)].path));
        UiDialogRoute.ShowMessageBox(Window.GetWindow(this), ((!cancelTokenSource.IsCancellationRequested) ? BeMusicSeeker.Properties.Resources.Msg_conversion_completed : BeMusicSeeker.Properties.Resources.Msg_conversion_stopped) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Success + ": " + (progIdx - failNum) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Failure + ": " + (bmsFiles.Length - progIdx + failNum), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, cancelTokenSource.IsCancellationRequested ? MessageBoxImage.Exclamation : MessageBoxImage.Asterisk, MessageBoxResult.OK);
    }

    private void playlistTableDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
        treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
        var viewModel = base.DataContext as MainWindowViewModel;
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows))
        {
            return;
        }
        if (sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        if (viewModel == null || treeViewItem.DataContext is not BMSTable table || table.is_external_sync)
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 == null)
        {
            return;
        }
        treeViewItem2.Background = Brushes.Transparent;
        PlaylistFolderNode targetFolder;
        if (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode))
        {
            targetFolder = null;
        }
        else
        {
            if (folderNode.IsSpecial)
            {
                return;
            }
            targetFolder = folderNode;
        }
        if (!viewModel.PlaylistWorkspace.CanAcceptDrop(selectedRows, table, targetFolder))
        {
            return;
        }
        e.Effects = DragDropEffects.Copy;
        viewModel.PlaylistWorkspace
            .AddRowsToFolderAsync(selectedRows, table, targetFolder)
            .Logging("playlistTableDrop");
    }

    private void playlistTableDragOver(object sender, DragEventArgs e)
    {
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows))
        {
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (sender is not TreeViewItem { DataContext: BMSTable table })
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        PlaylistFolderNode targetFolder = null;
        if (treeViewItem2 != null
            && (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out targetFolder) || !targetFolder.IsSpecial)
            && (base.DataContext as MainWindowViewModel)?.PlaylistWorkspace.CanAcceptDrop(selectedRows, table, targetFolder) == true)
        {
            e.Effects = DragDropEffects.Copy;
        }
    }

    private void playlistTableDragEnter(object sender, DragEventArgs e)
    {
        if (!IsPlaylistDropCandidateDrag(e.Data, out List<object> selectedRows))
        {
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (sender is not TreeViewItem tviTable)
        {
            return;
        }
        TreeViewItem treeViewItem = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem == null || tviTable.DataContext is not BMSTable bMSTable)
        {
            return;
        }
        PlaylistFolderNode targetFolder = null;
        if ((!TryGetPlaylistFolderNode(treeViewItem.DataContext, out targetFolder) || !targetFolder.IsSpecial)
            && (base.DataContext as MainWindowViewModel)?.PlaylistWorkspace.CanAcceptDrop(selectedRows, bMSTable, targetFolder) == true)
        {
            e.Effects = DragDropEffects.Copy;
            treeViewItem.Background = SystemColors.HighlightBrush;
        }
        if (!TryGetPlaylistFolderNode(treeViewItem.DataContext, out _))
        {
            var booleanAnimationUsingKeyFrames = new BooleanAnimationUsingKeyFrames();
            Storyboard.SetTargetProperty(booleanAnimationUsingKeyFrames, new PropertyPath(TreeViewItem.IsExpandedProperty));
            Storyboard.SetTarget(booleanAnimationUsingKeyFrames, tviTable);
            booleanAnimationUsingKeyFrames.KeyFrames.Add(new DiscreteBooleanKeyFrame(!tviTable.IsExpanded, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.0))));
            booleanAnimationUsingKeyFrames.FillBehavior = FillBehavior.Stop;
            booleanAnimationUsingKeyFrames.Completed += delegate
            {
                tviTable.IsExpanded = !tviTable.IsExpanded;
            };
            treeViewItemInstantStoryBoardPlaylistTable.Children.Add(booleanAnimationUsingKeyFrames);
            treeViewItemInstantStoryBoardPlaylistTable.Begin(this, isControllable: true);
        }
    }

    private static bool IsPlaylistDropCandidateDrag(IDataObject dataObject, out List<object> selectedRows)
    {
        selectedRows = null;
        return CustomTableDataTransfer.HasRowDragKind(dataObject, CustomTableRowDragKind.PlaylistDropCandidateRows)
            && CustomTableDataTransfer.TryGetSelectedRows(dataObject, out selectedRows);
    }

    private void playlistTableDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 != null && treeViewItem.DataContext is BMSTable bMSTable)
        {
            if (!bMSTable.is_external_sync && (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsSpecial))
            {
                treeViewItem2.Background = Brushes.Transparent;
            }
            if (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out _))
            {
                treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
                treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
            }
        }
    }

    private void RestorePlaybackSurfaceAndFocusTable()
    {
        if (_isClosingOrClosed) return;
        playbackPanelView.RestoreSelectedSurface();
        IntPtr handle;
        try { handle = new WindowInteropHelper(this).Handle; }
        catch { return; }
        if (handle != Win32API.GetForegroundWindow()) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)async delegate
        {
            if (_isClosingOrClosed) return;
            for (int i = 1; i <= 10; i++)
            {
                if (_isClosingOrClosed) break;
                NLogWrapper.DebuggerLogger?.Trace("try to set focus on custom table");
                IntPtr currentHandle;
                try { currentHandle = new WindowInteropHelper(this).Handle; }
                catch { break; }
                if (currentHandle != Win32API.GetForegroundWindow()) break;
                Keyboard.Focus(customTableView);
                await Task.Delay(100);
            }
        });
    }

    private void dialogIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement overlayDialog && (bool)e.NewValue)
        {
            activeOverlayDialog = overlayDialog;
            if (HidesPlaybackSurface(overlayDialog)) PlaybackOverlayVisibility = Visibility.Visible;
        }
        if (!(bool)e.NewValue && (bool)e.OldValue)
        {
            if (ReferenceEquals(activeOverlayDialog, sender))
            {
                activeOverlayDialog = null;
                if (sender is FrameworkElement hiddenDialog && HidesPlaybackSurface(hiddenDialog)) PlaybackOverlayVisibility = Visibility.Collapsed;
            }
            playbackPanelView.RestoreSelectedSurface();
            if (sender is PlaylistPropertyDialog)
            {
                BindingOperations.GetMultiBindingExpression(gridBMSPlayerControlsFolderPath, TextBlock.TextProperty).UpdateTarget();
            }
        }
    }

    private TreeViewItem _lastSelectedTreeViewItem;
    private bool _isCrossTreeDeselecting = false;

    private void gridTreePane_TreeViewItemSelected(object sender, RoutedEventArgs e)
    {
        TreeViewItem tvi = e.OriginalSource as TreeViewItem ?? e.Source as TreeViewItem;
        if (tvi != null && tvi.IsSelected)
        {
            if (_lastSelectedTreeViewItem != null && _lastSelectedTreeViewItem != tvi)
            {
                _isCrossTreeDeselecting = true;
                _lastSelectedTreeViewItem.IsSelected = false;
                _isCrossTreeDeselecting = false;
            }
            _lastSelectedTreeViewItem = tvi;
            TreeSelectionSection previousSection = _currentTreeSelectionSection;
            _currentTreeSelectionSection = ResolveTreeSelectionSection(tvi);
            if (previousSection != _currentTreeSelectionSection)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Info("tree_selection_section_changed section=" + _currentTreeSelectionSection + " header=" + tvi.Header);
            }
        }
    }

    private void treeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is TreeView treeView)
        {
            e.Handled = true;
            if (e.NewValue == null && !_isCrossTreeDeselecting)
            {
                _currentTreeSelectionSection = TreeSelectionSection.None;
            }
            var viewModel = base.DataContext as MainWindowViewModel;
            bool isManualInteraction = treeView.IsKeyboardFocusWithin || treeView.IsMouseOver;
            if (!_isCrossTreeDeselecting && e.NewValue == null && e.OldValue != null && e.OldValue is BMSTable
                && (viewModel?.PlaylistWorkspace?.IsPlaylistUpdating ?? false) && !isManualInteraction)
            {
                if (!treeView.SelectTreeViewItemSearchedByDataContext(e.OldValue))
                {
                    var table = (BMSTable)e.OldValue;
                    bool restoredByHeader = treeView.SelectTreeViewItemSearchedByHeader(table.name);
                    NLogWrapper.FileLogger?.Info("playlist_selection_restore fallback_by_header=" + restoredByHeader + " table=" + table.name);
                }
            }
        }
    }

    private TreeSelectionSection ResolveTreeSelectionSection(TreeViewItem selectedTreeViewItem)
    {
        if (selectedTreeViewItem == null)
        {
            return TreeSelectionSection.None;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemInstallPending))
        {
            return TreeSelectionSection.InstallPending;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, newlyInstalledTreeViewItem))
        {
            return TreeSelectionSection.InstallInstalled;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemPlaylist))
        {
            return TreeSelectionSection.Playlist;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemPlayHistory))
        {
            return TreeSelectionSection.PlayHistory;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemFullScanCheck))
        {
            return TreeSelectionSection.FullScanCheck;
        }
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemChartInfoParseError))
        {
            return TreeSelectionSection.ChartInfoParseError;
        }
        return TreeSelectionSection.Other;
    }

    private static bool IsSameOrDescendantOf(TreeViewItem targetTreeViewItem, TreeViewItem ancestorTreeViewItem)
    {
        if (targetTreeViewItem == null || ancestorTreeViewItem == null)
        {
            return false;
        }
        DependencyObject current = targetTreeViewItem;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestorTreeViewItem))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }




}
