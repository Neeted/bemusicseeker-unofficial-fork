using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Livet.EventListeners;
using Livet.Messaging;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using NLog;
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Net;
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
    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.MainWindow");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private static readonly AppHttpClient updateCheckHttpClient = AppHttpClient.Create(5000);

    private const long DownloadAndInstallSizeLimitBytes = 536870912L;

    private static readonly MethodInfo playlistTreeBringIndexIntoViewMethod = typeof(System.Windows.Controls.VirtualizingStackPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic) ?? typeof(System.Windows.Controls.VirtualizingPanel).GetMethod("BringIndexIntoView", BindingFlags.Instance | BindingFlags.NonPublic);

    private static long callbackExecSortRequestId;

    private DispatcherOperation _mainDataGridSortGlyphRefreshOperation;

    private DispatcherOperation _playlistSummarySortGlyphRefreshOperation;

    private bool _mainDataGridUsesAsyncBinding = true;

    private MainWindowViewModel _mainWindowViewModelForDataGridBinding;

    private PropertyChangedEventHandler _mainWindowViewModelDataGridBindingHandler;

    private long _mainDataGridLastScheduledSortGlyphGeneration;

    private long _mainDataGridLastCompletedSortGlyphGeneration;

    private const long CallbackExecSortSlowLogThresholdMs = 100L;

    // NOTE:
    // TreeView の仮想化 (Recycling) 有効時は、画面外ノードのコンテナが VisualTree から外れる。
    // そのため「VisualTree を再帰して選択状態を判定する」実装は false negative を起こす。
    // ここでは最後に確定した選択ノードの所属セクションを保持し、UIコンテナ有無に依存しない判定を行う。
    private enum TreeSelectionSection
    {
        None,
        Playlist,
        InstallPending,
        InstallInstalled,
        FullScanCheck,
        Other
    }

    private enum DownloadAndInstallResult
    {
        Installed,
        OpenInBrowser,
        BlockedBySizeLimit
    }

    private TreeSelectionSection _currentTreeSelectionSection = TreeSelectionSection.None;

    private PropertyChangedEventListener settingsDefaultEventListnener;

    private static readonly string clearlampUri = "http://xyzzz.net/bms/clearlamp";

    private BMSLibrary.IRSongInfo songInfoCache;

    private CancellationTokenSource dataGridContextMenuTaskTokenSource;

    private Task getSongInfoCacheTask;

    private Task changeSubmenuOpenVideoTask;

    private Task changeSubmenuOpenDocumentTask;

    private Task changeSubmenuOpenSearchLinkTask;

    private static Regex dropBoxRegex = new Regex("https?://(?:(?:www|dl)\\.dropbox\\.com|dl\\.dropboxusercontent\\.com)/(sh?)/([^?]*)\\.([^?]*)(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex gdriveRegex = new Regex("https?://drive\\.google\\.com/(file/d/|open\\?id=)([^/]*)(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex odriveRegex = new Regex("https?://onedrive\\.live\\.com/redir\\?(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private Storyboard treeViewItemInstantStoryBoardPlaylistTable = new Storyboard();

    private static DispatcherTimer gridBMSPlayerControlsPreviousButtonClickTimer;

    private BitmapSource _panelImage;

    // マージ後に自動選択するDuplicateGroupのHeader（曲名）をキャッシュ
    private string _pendingDuplicateGroupHeader;
    private int _duplicateGroupAutoSelectRequestVersion;
    private PropertyChangedEventHandler _duplicateGroupAutoSelectHandler;
    private MainWindowViewModel _duplicateGroupAutoSelectHandlerOwner;

    private bool startupInitialSelectionApplied;

    private BitmapSource panelImage
    {
        get
        {
            if (_panelImage == null)
            {
                if (Settings.Default.UseExternalPanelImage)
                {
                    try
                    {
                        MemoryStream memoryStream = new MemoryStream(File.ReadAllBytes(Settings.Default.StagefilePath));
                        _panelImage = new WriteableBitmap(BitmapFrame.Create(memoryStream));
                        memoryStream.Close();
                        return _panelImage;
                    }
                    catch
                    {
                    }
                }
                _panelImage = new BitmapImage(new Uri("pack://application:,,,/resources/default_image.jpg"));
            }
            return _panelImage;
        }
    }

    public MainWindowViewModel.PanelState NowPanelState
    {
        get
        {
            return Settings.Default.PlayerPanelState;
        }
        set
        {
            if (Settings.Default.PlayerPanelState != value)
            {
                switch (value)
                {
                    case MainWindowViewModel.PanelState.BMS_PLAYER:
                        showBMSPlayerPanel();
                        break;
                    case MainWindowViewModel.PanelState.MOVIE_PLAYER:
                        showBrowserPanel();
                        break;
                    default:
                        collapseBMSPlayerPanel();
                        collapseBrowserPanel();
                        break;
                }
                Settings.Default.PlayerPanelState = value;
            }
        }
    }

    /// <summary>
    /// <see cref="MainWindow"/> クラスの新しいインスタンスを初期化します。
    /// UIコンポーネントの構築、TreeViewのイベントハンドラ登録、
    /// 設定のプロパティ変更リスナの初期化、および非同期のアップデートチェックを開始します。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += MainWindow_DataContextChanged;

        // Add handler that catches already-handled TreeViewItem.Selected events to synchronize TreeView exclusivity
        gridTreePane.AddHandler(TreeViewItem.SelectedEvent, new RoutedEventHandler(gridTreePane_TreeViewItemSelected), true);

        settingsDefaultEventListnener = new PropertyChangedEventListener(Settings.Default);
        settingsDefaultEventListnener.RegisterHandler(() => Settings.Default.UseExternalPanelImage, delegate
        {
            _panelImage = null;
        });
        settingsDefaultEventListnener.RegisterHandler(() => Settings.Default.StagefilePath, delegate
        {
            _panelImage = null;
        });
        gridBMSPlayerImage.Source = panelImage;

        // Start async update check
        Task.Run(async () => await CheckForUpdatesAsync());
    }

    /// <summary>
    /// GitHub上のバージョン情報ファイルを参照し、現在実行中のアプリケーションよりも
    /// 新しいバージョンがリリースされていないか非同期でチェックします。
    /// 新しいバージョンが利用可能な場合は、ユーザーにメッセージボックスで通知します。
    /// </summary>
    /// <returns>非同期タスクを表す <see cref="Task"/> オブジェクト。</returns>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            // キャッシュバスター: GitHub CDN のキャッシュを回避する
            string versionUrl = "https://raw.githubusercontent.com/Neeted/bemusicseeker-unofficial-fork/main/version.txt?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string latestVersionStr = await updateCheckHttpClient.GetStringAsync(new Uri(versionUrl), Encoding.UTF8);
            latestVersionStr = latestVersionStr?.Trim();

            if (Version.TryParse(latestVersionStr, out Version latestVersion))
            {
                string currentVersionStr = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion ?? "0.0.0.0";
                if (Version.TryParse(currentVersionStr, out Version currentVersion) && latestVersion > currentVersion)
                {
                    base.Dispatcher.Invoke(() =>
                    {
                        DispatcherMessageBox.Show(
                            $"A new version ({latestVersionStr}) is available.\nYour version: {currentVersionStr}\n\nPlease check the repository.",
                            "Update Available",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn("Failed to check for updates: " + ex.Message);
        }
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
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate
        {
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
    /// DataContext 変更時にメイン DataGrid binding 監視先を差し替えます。
    /// </summary>
    private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ReferenceEquals(e.OldValue, e.NewValue))
        {
            return;
        }
        DetachMainDataGridBindingOwner();
        AttachMainDataGridBindingOwner(e.NewValue as MainWindowViewModel);
        if (dataGrid != null && dataGrid.IsLoaded)
        {
            ApplyMainDataGridItemsSourceBinding(forceRebind: true);
        }
    }

    /// <summary>
    /// メイン DataGrid の binding 切り替えを監視する ViewModel を登録します。
    /// </summary>
    /// <param name="viewModel">監視対象 ViewModel。</param>
    private void AttachMainDataGridBindingOwner(MainWindowViewModel viewModel)
    {
        if (viewModel == null)
        {
            return;
        }
        if (ReferenceEquals(_mainWindowViewModelForDataGridBinding, viewModel) && _mainWindowViewModelDataGridBindingHandler != null)
        {
            return;
        }
        DetachMainDataGridBindingOwner();
        _mainWindowViewModelForDataGridBinding = viewModel;
        _mainWindowViewModelDataGridBindingHandler = delegate(object _, PropertyChangedEventArgs args)
        {
            if (args == null)
            {
                return;
            }
            if (args.PropertyName == "UseAsyncBMSFilesViewBinding" || args.PropertyName == "IsPlaylistDetailViewActive")
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                {
                    ApplyMainDataGridItemsSourceBinding(forceRebind: false);
                });
            }
        };
        viewModel.PropertyChanged += _mainWindowViewModelDataGridBindingHandler;
    }

    /// <summary>
    /// 現在登録中の ViewModel 監視を解除します。
    /// </summary>
    private void DetachMainDataGridBindingOwner()
    {
        if (_mainWindowViewModelForDataGridBinding != null && _mainWindowViewModelDataGridBindingHandler != null)
        {
            _mainWindowViewModelForDataGridBinding.PropertyChanged -= _mainWindowViewModelDataGridBindingHandler;
        }
        _mainWindowViewModelForDataGridBinding = null;
        _mainWindowViewModelDataGridBindingHandler = null;
    }

    /// <summary>
    /// 現在の ViewModel 状態に応じてメイン DataGrid の ItemsSource binding を再構成します。
    /// playlist 詳細表示では同期 binding に切り替えて旧 ItemsSource の保持を減らします。
    /// </summary>
    /// <param name="forceRebind">現在の設定と同一でも binding を再構成する場合は <see langword="true"/>。</param>
    private void ApplyMainDataGridItemsSourceBinding(bool forceRebind)
    {
        if (dataGrid == null)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        bool useAsyncBinding = viewModel == null || viewModel.UseAsyncBMSFilesViewBinding;
        if (!forceRebind && _mainDataGridUsesAsyncBinding == useAsyncBinding && BindingOperations.GetBinding(dataGrid, ItemsControl.ItemsSourceProperty) != null)
        {
            return;
        }
        Binding itemsSourceBinding = new Binding("BMSFilesView")
        {
            Mode = BindingMode.OneWay,
            NotifyOnTargetUpdated = true,
            IsAsync = useAsyncBinding
        };
        BindingOperations.SetBinding(dataGrid, ItemsControl.ItemsSourceProperty, itemsSourceBinding);
        _mainDataGridUsesAsyncBinding = useAsyncBinding;
        LogPlaylistDataGridState("binding_applied", dataGrid, "useAsync=" + useAsyncBinding);
    }

    /// <summary>
    /// playlist 詳細表示の差し替え直前に DataGrid の選択・編集状態と旧 ItemsSource を解放します。
    /// </summary>
    /// <param name="targetDataGrid">対象 DataGrid。</param>
    public void PreparePlaylistDataGridSwap(DataGrid targetDataGrid)
    {
        DataGrid effectiveDataGrid = targetDataGrid ?? dataGrid;
        if (effectiveDataGrid == null)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                PreparePlaylistDataGridSwap(effectiveDataGrid);
            });
            return;
        }
        DispatcherOperation pendingSortGlyphRefresh = GetSortGlyphRefreshOperation(effectiveDataGrid);
        if (pendingSortGlyphRefresh != null && (pendingSortGlyphRefresh.Status == DispatcherOperationStatus.Pending || pendingSortGlyphRefresh.Status == DispatcherOperationStatus.Executing))
        {
            pendingSortGlyphRefresh.Abort();
            SetSortGlyphRefreshOperation(effectiveDataGrid, null);
        }
        try
        {
            effectiveDataGrid.CancelEdit(DataGridEditingUnit.Cell);
            effectiveDataGrid.CancelEdit(DataGridEditingUnit.Row);
        }
        catch
        {
        }
        effectiveDataGrid.CurrentCell = default(DataGridCellInfo);
        if (effectiveDataGrid.SelectionMode != DataGridSelectionMode.Single && effectiveDataGrid.SelectedItems != null)
        {
            effectiveDataGrid.SelectedItems.Clear();
        }
        effectiveDataGrid.SelectedItem = null;
        effectiveDataGrid.SelectedIndex = -1;
        effectiveDataGrid.SetCurrentValue(ItemsControl.ItemsSourceProperty, null);
        LogPlaylistDataGridState("prepare_swap", effectiveDataGrid, "useAsync=" + _mainDataGridUsesAsyncBinding);
        SchedulePlaylistRetentionCheckpoint(effectiveDataGrid, "prepare_swap");
        Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
        {
            ApplyMainDataGridItemsSourceBinding(forceRebind: false);
        });
    }

    /// <summary>
    /// playlist 詳細表示中の DataGrid 状態を診断ログへ出力します。
    /// </summary>
    /// <param name="eventName">出力イベント名。</param>
    /// <param name="targetDataGrid">対象 DataGrid。</param>
    /// <param name="details">追加情報。</param>
    private void LogPlaylistDataGridState(string eventName, DataGrid targetDataGrid, string details = null)
    {
        if (!installPerformanceLoggingEnabled)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        bool isPlaylistDetailViewActive = viewModel != null && viewModel.IsPlaylistDetailViewActive;
        int itemCount = 0;
        int selectedCount = 0;
        string itemsSourceType = "(null)";
        int realizedRowCount = 0;
        string generatorStatus = "(unknown)";
        if (targetDataGrid != null)
        {
            itemCount = targetDataGrid.Items?.Count ?? 0;
            selectedCount = targetDataGrid.SelectedItems?.Count ?? 0;
            itemsSourceType = targetDataGrid.ItemsSource?.GetType().FullName ?? "(null)";
            realizedRowCount = CountVisualDescendants<DataGridRow>(targetDataGrid, maxCount: 2000);
            generatorStatus = targetDataGrid.ItemContainerGenerator?.Status.ToString() ?? "(null)";
        }
        string suffix = string.IsNullOrWhiteSpace(details) ? string.Empty : " " + details;
        installPerformanceLogger.Info("playlist_datagrid_state event=" + eventName + " playlistActive=" + isPlaylistDetailViewActive + " useAsync=" + _mainDataGridUsesAsyncBinding + " itemsCount=" + itemCount + " selectedCount=" + selectedCount + " realizedRowCount=" + realizedRowCount + " generatorStatus=" + generatorStatus + " itemsSourceType=" + itemsSourceType + " sourceGenerationId=" + (viewModel?.PlaylistSourceGenerationId ?? 0L) + " viewGenerationId=" + (viewModel?.PlaylistAdoptedViewGenerationId ?? 0L) + suffix);
    }

    /// <summary>
    /// playlist DataGrid の描画完了後に retention 状態を再観測するチェックポイントを遅延投入します。
    /// </summary>
    /// <param name="targetDataGrid">対象 DataGrid。</param>
    /// <param name="eventName">契機名。</param>
    private void SchedulePlaylistRetentionCheckpoint(DataGrid targetDataGrid, string eventName)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (targetDataGrid == null || viewModel == null || !viewModel.IsPlaylistDetailViewActive)
        {
            return;
        }
        long sourceGenerationId = viewModel.PlaylistSourceGenerationId;
        long viewGenerationId = viewModel.PlaylistAdoptedViewGenerationId;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)delegate
        {
            LogPlaylistDataGridState(eventName + "_render", targetDataGrid, "scheduledSourceGenerationId=" + sourceGenerationId + " scheduledViewGenerationId=" + viewGenerationId);
            viewModel.TryLogPlaylistOpenVisibleCompleted(eventName + "_render", sourceGenerationId, viewGenerationId);
            viewModel.LogPlaylistUiRetentionCheckpoint(eventName + "_render", sourceGenerationId, viewGenerationId);
        });
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)delegate
        {
            LogPlaylistDataGridState(eventName + "_idle", targetDataGrid, "scheduledSourceGenerationId=" + sourceGenerationId + " scheduledViewGenerationId=" + viewGenerationId);
            viewModel.LogPlaylistUiRetentionCheckpoint(eventName + "_idle", sourceGenerationId, viewGenerationId);
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
        Queue<DependencyObject> pending = new Queue<DependencyObject>();
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
    /// ドロップされたパス一覧を取得し、BMSファイルのインストール処理を開始します。
    /// </summary>
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] filePaths)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            string[] pathSnapshot = filePaths.ToArray();
            if (pathSnapshot.Length > 0)
            {
                viewModel?.EnqueueDroppedInstallPaths(pathSnapshot);
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
        if (e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: true))
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
    /// DataGrid等の特定の操作可能要素以外をクリックしたと判定された場合、
    /// ウィンドウ全体をドラッグ移動できるようにします (DragMove)。
    /// </summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!dataGrid.IsMouseOver)
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
        if (!isPanelStateValid(NowPanelState))
        {
            gridBMSPlayerControlsRotatePanelStateButtonClicked();
        }
        if (webBrowser != null)
        {
            object value = typeof(WebBrowser).GetProperty("AxIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(webBrowser, null);
            value.GetType().InvokeMember("Silent", BindingFlags.SetProperty, null, value, new object[1] { true });
            value.GetType().InvokeMember("RegisterAsDropTarget", BindingFlags.SetProperty, null, value, new object[1] { false });
        }
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

    /// <summary>
    /// ウィンドウが閉じられる直前に呼び出されます。
    /// 現在のUI状態（TreeViewの幅、ウィンドウの配置や最大化状態など）を
    /// ユーザー設定 (Settings.Default) に保存します。
    /// </summary>
    /// <param name="e">キャンセル可能なイベントデータ。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        DetachMainDataGridBindingOwner();
        Settings.Default.TreeViewWidth = treeView.ActualWidth + gridSplitter.ActualWidth;
        Win32API.WINDOWPLACEMENT lpwndpl = default(Win32API.WINDOWPLACEMENT);
        Win32API.GetWindowPlacement(new WindowInteropHelper(this).Handle, ref lpwndpl);
        Settings.Default.WindowPlacement = lpwndpl;
        Settings.Default.Save();
    }

    /// <summary>
    /// メインプレイリスト一覧 (DataGrid) の列ヘッダクリック時に発生するソート処理をハンドリングします。
    /// 現在のソート方向を反転（未設定時は昇順）させ、非同期でバックグラウンド実行をリクエストします。
    /// 一時的にソートアイコン（Glyph）を即反映させ、実際の並び替え完了後にアイコン状態を同期します。
    /// </summary>
    private async void dataGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!(sender is DataGrid dataGrid))
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        string sortMemberPath = GetSortMemberPath(e.Column);
        if (string.IsNullOrWhiteSpace(sortMemberPath))
        {
            return;
        }
        LogPlaylistDataGridState("sorting", dataGrid, "column=" + sortMemberPath);
        ListSortDirection? effectiveCurrentDirection = e.Column.SortDirection;
        if (!effectiveCurrentDirection.HasValue && viewModel.SortParameters != null && string.Equals(viewModel.SortParameters.ColumnsName, sortMemberPath, StringComparison.Ordinal))
        {
            effectiveCurrentDirection = viewModel.SortParameters.Direction;
        }
        ListSortDirection newDir = ((effectiveCurrentDirection == ListSortDirection.Ascending) ? ListSortDirection.Descending : ListSortDirection.Ascending);
        ApplyImmediateSortGlyph(dataGrid, e.Column, newDir);

        await Task.Run(delegate
        {
            viewModel.ExecSort(sortMemberPath, newDir);
        }).Logging("dataGridSorting");
        RequestSortGlyphRefresh(dataGrid, "sorting");
    }

    /// <summary>
    /// プレイリストサマリー一覧 (DataGrid) の列ヘッダクリック時に発生するソート処理をハンドリングします。
    /// <see cref="dataGridSorting"/> と同様に、ソートの非同期実行とアイコン即時・事後同期を行います。
    /// </summary>
    private async void dataGridPlaylistSummarySorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!(sender is DataGrid dataGrid))
        {
            return;
        }
        if (!(base.DataContext is MainWindowViewModel viewModel))
        {
            return;
        }
        string sortMemberPath = GetSortMemberPath(e.Column);
        if (string.IsNullOrWhiteSpace(sortMemberPath))
        {
            return;
        }
        ListSortDirection? effectiveCurrentDirection = e.Column.SortDirection;
        if (!effectiveCurrentDirection.HasValue && viewModel.PlaylistSummarySortParameters != null && string.Equals(viewModel.PlaylistSummarySortParameters.ColumnsName, sortMemberPath, StringComparison.Ordinal))
        {
            effectiveCurrentDirection = viewModel.PlaylistSummarySortParameters.Direction;
        }
        ListSortDirection newDirection = ((effectiveCurrentDirection == ListSortDirection.Ascending) ? ListSortDirection.Descending : ListSortDirection.Ascending);
        ApplyImmediateSortGlyph(dataGrid, e.Column, newDirection);
        await Task.Run(delegate
        {
            viewModel.ExecPlaylistSummarySort(sortMemberPath, newDirection);
        }).Logging("dataGridPlaylistSummarySorting");
        RequestSortGlyphRefresh(dataGrid, "playlist_summary_sorting");
    }

    /// <summary>
    /// DataGridのItemsSourceなどデータ転送対象が更新された際に実行されます。
    /// コレクション再生成やアイテム群の大幅変更が発生したと見なし、ソートアイコンの再同期をスケジュールします。
    /// </summary>
    private void dataGridTargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (!ReferenceEquals(e.Property, ItemsControl.ItemsSourceProperty))
        {
            return;
        }
        if (sender is DataGrid dataGrid2)
        {
            LogPlaylistDataGridState("target_updated", dataGrid2);
            SchedulePlaylistRetentionCheckpoint(dataGrid2, "target_updated");
            RequestSortGlyphRefresh(dataGrid2, "target_updated");
        }
    }

    /// <summary>
    /// Livet の MethodAction 互換のための単引数エントリです。
    /// </summary>
    /// <param name="dataGrid">対象 DataGrid。</param>
    public void renewSortIcon(DataGrid dataGrid)
    {
        RequestSortGlyphRefresh(dataGrid, "callback");
    }

    /// <summary>
    /// DataGrid のソートアイコン同期を行い、必要に応じて遅延計測ログを出力します。
    /// </summary>
    /// <param name="dataGrid">対象 DataGrid。</param>
    /// <param name="trigger">呼び出し契機。ログ相関用。</param>
    public void renewSortIcon(DataGrid dataGrid, string trigger = "unspecified")
    {
        RequestSortGlyphRefresh(dataGrid, trigger);
    }

    private void RequestSortGlyphRefresh(DataGrid dataGrid, string trigger)
    {
        if (dataGrid == null)
        {
            return;
        }
        if (!base.Dispatcher.CheckAccess())
        {
            base.Dispatcher.BeginInvoke((Action)delegate
            {
                RequestSortGlyphRefresh(dataGrid, trigger);
            }, DispatcherPriority.Normal);
            return;
        }
        DispatcherOperation currentOperation = GetSortGlyphRefreshOperation(dataGrid);
        if (currentOperation != null)
        {
            if (currentOperation.Status == DispatcherOperationStatus.Pending || currentOperation.Status == DispatcherOperationStatus.Executing)
            {
                return;
            }
            SetSortGlyphRefreshOperation(dataGrid, null);
        }
        bool isMainDataGrid = !ReferenceEquals(dataGrid, dataGridPlaylistSummary);
        MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
        long playlistViewGenerationId = (isMainDataGrid && mainWindowViewModel != null && mainWindowViewModel.IsPlaylistDetailViewActive) ? mainWindowViewModel.PlaylistAdoptedViewGenerationId : 0L;
        if (isMainDataGrid && playlistViewGenerationId > 0L && (_mainDataGridLastScheduledSortGlyphGeneration == playlistViewGenerationId || _mainDataGridLastCompletedSortGlyphGeneration == playlistViewGenerationId))
        {
            if (installPerformanceLoggingEnabled)
            {
                installPerformanceLogger.Info("playlist_sortglyph_refresh event=skipped generationId=" + playlistViewGenerationId + " trigger=" + trigger + " completedGenerationId=" + _mainDataGridLastCompletedSortGlyphGeneration + " scheduledGenerationId=" + _mainDataGridLastScheduledSortGlyphGeneration);
            }
            return;
        }
        long requestId = Interlocked.Increment(ref callbackExecSortRequestId);
        Stopwatch queueStopwatch = Stopwatch.StartNew();
        long raiseRequestId = isMainDataGrid ? (mainWindowViewModel?.LastExecSortCallbackRequestId ?? 0L) : 0L;
        long raiseStartTimestamp = isMainDataGrid ? (mainWindowViewModel?.LastExecSortCallbackRaiseStartTimestamp ?? 0L) : 0L;
        int raiseStartThreadId = isMainDataGrid ? (mainWindowViewModel?.LastExecSortCallbackRaiseStartThreadId ?? 0) : 0;
        long mainViewBuildRequestId = isMainDataGrid ? (mainWindowViewModel?.LastMainViewBuildRequestId ?? 0L) : 0L;
        long mainViewBuildEndTimestamp = isMainDataGrid ? (mainWindowViewModel?.LastMainViewBuildEndTimestamp ?? 0L) : 0L;
        int mainViewBuildThreadId = isMainDataGrid ? (mainWindowViewModel?.LastMainViewBuildThreadId ?? 0) : 0;
        int mainViewBuildMode = isMainDataGrid ? (mainWindowViewModel?.LastMainViewBuildMode ?? 0) : 0;
        DispatcherOperation scheduledOperation = null;
        if (isMainDataGrid && playlistViewGenerationId > 0L)
        {
            _mainDataGridLastScheduledSortGlyphGeneration = playlistViewGenerationId;
            if (installPerformanceLoggingEnabled)
            {
                installPerformanceLogger.Info("playlist_sortglyph_refresh event=scheduled generationId=" + playlistViewGenerationId + " trigger=" + trigger + " request=" + requestId);
            }
        }
        scheduledOperation = base.Dispatcher.BeginInvoke((Action)delegate
        {
            if (ReferenceEquals(GetSortGlyphRefreshOperation(dataGrid), scheduledOperation))
            {
                SetSortGlyphRefreshOperation(dataGrid, null);
            }
            long raiseToHandlerMs = (raiseStartTimestamp > 0L) ? ((Stopwatch.GetTimestamp() - raiseStartTimestamp) * 1000L / Stopwatch.Frequency) : (-1L);
            long buildToHandlerMs = (mainViewBuildEndTimestamp > 0L) ? ((Stopwatch.GetTimestamp() - mainViewBuildEndTimestamp) * 1000L / Stopwatch.Frequency) : (-1L);
            if (raiseToHandlerMs >= CallbackExecSortSlowLogThresholdMs || buildToHandlerMs >= CallbackExecSortSlowLogThresholdMs)
            {
                installPerformanceLogger?.Info("callback_exec_sort handler_slow request=" + requestId + " trigger=" + trigger + " raiseRequest=" + raiseRequestId + " raiseToHandlerMs=" + raiseToHandlerMs + " buildRequest=" + mainViewBuildRequestId + " buildToHandlerMs=" + buildToHandlerMs + " handlerThreadId=" + Thread.CurrentThread.ManagedThreadId + " raiseThreadId=" + raiseStartThreadId + " buildThreadId=" + mainViewBuildThreadId + " buildMode=" + mainViewBuildMode + " handlerOnUiThread=" + base.Dispatcher.CheckAccess() + " columns=" + dataGrid.Columns?.Count + " thresholdMs=" + CallbackExecSortSlowLogThresholdMs);
            }
            long queueMs = queueStopwatch.ElapsedMilliseconds;
            Stopwatch runStopwatch = Stopwatch.StartNew();
            long applyStartTimestamp = Stopwatch.GetTimestamp();
            bool appliedAtLoaded = ApplySortGlyphNow(dataGrid, requestId, raiseRequestId, trigger, logWhenTargetMissing: false);
            long runMs = runStopwatch.ElapsedMilliseconds;
            if (queueMs >= CallbackExecSortSlowLogThresholdMs || runMs >= CallbackExecSortSlowLogThresholdMs)
            {
                installPerformanceLogger?.Info("callback_exec_sort run_slow request=" + requestId + " trigger=" + trigger + " raiseRequest=" + raiseRequestId + " queueMs=" + queueMs + " runMs=" + runMs + " runThreadId=" + Thread.CurrentThread.ManagedThreadId + " applied=" + appliedAtLoaded + " grid=" + GetSortGlyphGridName(dataGrid) + " thresholdMs=" + CallbackExecSortSlowLogThresholdMs);
            }
            base.Dispatcher.BeginInvoke((Action)delegate
            {
                bool appliedAtRender = ApplySortGlyphNow(dataGrid, requestId, raiseRequestId, trigger + "_render", logWhenTargetMissing: true);
                if (isMainDataGrid && playlistViewGenerationId > 0L)
                {
                    if (appliedAtRender)
                    {
                        _mainDataGridLastCompletedSortGlyphGeneration = playlistViewGenerationId;
                    }
                    else if (_mainDataGridLastScheduledSortGlyphGeneration == playlistViewGenerationId)
                    {
                        _mainDataGridLastScheduledSortGlyphGeneration = 0L;
                    }
                    if (installPerformanceLoggingEnabled)
                    {
                        installPerformanceLogger.Info("playlist_sortglyph_refresh event=completed generationId=" + playlistViewGenerationId + " trigger=" + trigger + " request=" + requestId + " applied=" + appliedAtRender);
                    }
                }
                long applyToRenderMs = (Stopwatch.GetTimestamp() - applyStartTimestamp) * 1000L / Stopwatch.Frequency;
                long raiseToRenderMs = (raiseStartTimestamp > 0L) ? ((Stopwatch.GetTimestamp() - raiseStartTimestamp) * 1000L / Stopwatch.Frequency) : (-1L);
                long buildToRenderMs = (mainViewBuildEndTimestamp > 0L) ? ((Stopwatch.GetTimestamp() - mainViewBuildEndTimestamp) * 1000L / Stopwatch.Frequency) : (-1L);
                if (applyToRenderMs >= CallbackExecSortSlowLogThresholdMs || raiseToRenderMs >= CallbackExecSortSlowLogThresholdMs || buildToRenderMs >= CallbackExecSortSlowLogThresholdMs)
                {
                    installPerformanceLogger?.Info("callback_exec_sort render_slow request=" + requestId + " trigger=" + trigger + " raiseRequest=" + raiseRequestId + " buildRequest=" + mainViewBuildRequestId + " applyToRenderMs=" + applyToRenderMs + " raiseToRenderMs=" + raiseToRenderMs + " buildToRenderMs=" + buildToRenderMs + " renderThreadId=" + Thread.CurrentThread.ManagedThreadId + " applied=" + appliedAtRender + " grid=" + GetSortGlyphGridName(dataGrid) + " thresholdMs=" + CallbackExecSortSlowLogThresholdMs);
                }
            }, DispatcherPriority.Render);
        }, DispatcherPriority.Loaded);
        SetSortGlyphRefreshOperation(dataGrid, scheduledOperation);
    }

    private static string GetSortMemberPath(DataGridColumn column)
    {
        if (column == null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(column.SortMemberPath))
        {
            return column.SortMemberPath;
        }
        if (column is DataGridBoundColumn dataGridBoundColumn && dataGridBoundColumn.Binding is Binding binding && binding.Path != null && !string.IsNullOrWhiteSpace(binding.Path.Path))
        {
            return binding.Path.Path;
        }
        return null;
    }

    private void ApplyImmediateSortGlyph(DataGrid dataGrid, DataGridColumn targetColumn, ListSortDirection direction)
    {
        if (dataGrid == null || targetColumn == null)
        {
            return;
        }
        foreach (DataGridColumn column in dataGrid.Columns)
        {
            column.SortDirection = ReferenceEquals(column, targetColumn) ? direction : ((ListSortDirection?)null);
        }
    }

    private bool ApplySortGlyphNow(DataGrid dataGrid, long requestId, long raiseRequestId, string trigger, bool logWhenTargetMissing)
    {
        MainWindowViewModel.cSortParameters sortParameters = GetSortParametersForGrid(dataGrid);
        if (sortParameters == null)
        {
            installPerformanceLogger?.Info("callback_exec_sort run request=" + requestId + " trigger=" + trigger + " raiseRequest=" + raiseRequestId + " runThreadId=" + Thread.CurrentThread.ManagedThreadId + " reason=sort_parameters_null grid=" + GetSortGlyphGridName(dataGrid));
            return false;
        }
        if (!TryFindSortColumn(dataGrid, sortParameters.ColumnsName, out var targetColumn))
        {
            if (logWhenTargetMissing)
            {
                installPerformanceLogger?.Warn("callback_exec_sort run request=" + requestId + " trigger=" + trigger + " raiseRequest=" + raiseRequestId + " runThreadId=" + Thread.CurrentThread.ManagedThreadId + " reason=column_not_found column=" + sortParameters.ColumnsName + " grid=" + GetSortGlyphGridName(dataGrid));
            }
            return false;
        }
        foreach (DataGridColumn column in dataGrid.Columns)
        {
            column.SortDirection = ReferenceEquals(column, targetColumn) ? sortParameters.Direction : ((ListSortDirection?)null);
        }
        return true;
    }

    private MainWindowViewModel.cSortParameters GetSortParametersForGrid(DataGrid dataGrid)
    {
        MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
        if (ReferenceEquals(dataGrid, dataGridPlaylistSummary))
        {
            return mainWindowViewModel?.PlaylistSummarySortParameters;
        }
        return mainWindowViewModel?.SortParameters;
    }

    private bool TryFindSortColumn(DataGrid dataGrid, string columnName, out DataGridColumn targetColumn)
    {
        targetColumn = null;
        if (dataGrid?.Columns == null || string.IsNullOrWhiteSpace(columnName))
        {
            return false;
        }
        foreach (DataGridColumn column in dataGrid.Columns)
        {
            if (string.Equals(GetSortMemberPath(column), columnName, StringComparison.Ordinal))
            {
                targetColumn = column;
                return true;
            }
        }
        return false;
    }

    private DispatcherOperation GetSortGlyphRefreshOperation(DataGrid dataGrid)
    {
        if (ReferenceEquals(dataGrid, dataGridPlaylistSummary))
        {
            return _playlistSummarySortGlyphRefreshOperation;
        }
        return _mainDataGridSortGlyphRefreshOperation;
    }

    private void SetSortGlyphRefreshOperation(DataGrid dataGrid, DispatcherOperation operation)
    {
        if (ReferenceEquals(dataGrid, dataGridPlaylistSummary))
        {
            _playlistSummarySortGlyphRefreshOperation = operation;
        }
        else
        {
            _mainDataGridSortGlyphRefreshOperation = operation;
        }
    }

    private string GetSortGlyphGridName(DataGrid dataGrid)
    {
        if (ReferenceEquals(dataGrid, dataGridPlaylistSummary))
        {
            return "playlist_summary";
        }
        return "main";
    }

    private void dataGridInitializeColumnSetting(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            e.Handled = true;
            if (MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_init_column_settings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
            {
                mainWindowViewModel.LoadColumnSetting();
            }
        }
    }

    /// <summary>
    /// メインDataGridにおいて、VirtualizingStackPanel等のUI仮想化が有効な環境下で、
    /// ViewModel上で選択されたBMSファイル（SelectedIndexBMSFilesView）の行が
    /// 表示領域（Viewport）内に収まるようにスクロール位置を調整します。
    /// </summary>
    public void scrollIntoView()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            try
            {
                MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
                dataGrid.UpdateLayout();
                object obj = dataGrid.Items[mainWindowViewModel.SelectedIndexBMSFilesView];
                if (obj != null)
                {
                    dataGrid.ScrollIntoView(obj);
                }
            }
            catch
            {
            }
        });
    }

    /// <summary>
    /// DataGridのカラム表示順序（列入れ替え結果）をViewModelや設定用データソースに書き戻します。
    /// ウィンドウ終了時などに列の順序状態を永続化するための情報を取得します。
    /// </summary>
    public void getDisplayIndices()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            _ = base.DataContext;
            _ = new DataGridColumn[dataGrid.Columns.Count];
            foreach (var item in dataGrid.Columns.Where((DataGridColumn col) => BindingOperations.GetBinding(col, DataGridColumn.WidthProperty) != null).OrderBy(delegate (DataGridColumn col)
            {
                Binding binding = BindingOperations.GetBinding(col, DataGridColumn.WidthProperty);
                object obj = _getValueOfPropertyPath(binding.Source, binding.Path.Path.Substring(0, binding.Path.Path.LastIndexOf('.')) + ".DisplayIndex");
                return (obj is int) ? ((int)obj) : 0;
            }).Select((DataGridColumn v, int i) => new { v, i }))
            {
                item.v.DisplayIndex = item.i;
            }
        });
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

    private void setDisplayIndices(object sender, DataGridColumnEventArgs e)
    {
        setDisplayIndices();
    }

    /// <summary>
    /// ViewModelや設定データソースから取得したカラムの表示順序（DisplayIndex）を、
    /// 現在のDataGridのカラム群に適用してUIレイアウトを復元します。
    /// 最後に配置すべきダミーカラムなどは固定インデックスで調整します。
    /// </summary>
    private void setDisplayIndices()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            _ = base.DataContext;
            dataGridColumnDummyLast.DisplayIndex = dataGrid.Columns.Count - 1;
            dataGridColumnDummyFill.DisplayIndex = dataGrid.Columns.Count - 2;
            foreach (var item in (from c in dataGrid.Columns
                                  where c != null
                                  orderby c.DisplayIndex
                                  select c).Select((DataGridColumn v, int i) => new { v, i }))
            {
                Binding binding = BindingOperations.GetBinding(item.v, DataGridColumn.WidthProperty);
                if (binding != null)
                {
                    _getSetterOfPropertyPath<int>(binding.Source, binding.Path.Path.Substring(0, binding.Path.Path.LastIndexOf('.')) + ".DisplayIndex")(item.i);
                }
            }
        });
    }

    private void setDisplayIndicesPlaylistSummary(object sender, DataGridColumnEventArgs e)
    {
        setDisplayIndicesPlaylistSummary();
    }

    private void setDisplayIndicesPlaylistSummary()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            foreach (var item in (from c in dataGridPlaylistSummary.Columns
                                  where c != null
                                  orderby c.DisplayIndex
                                  select c).Select((DataGridColumn v, int i) => new { v, i }))
            {
                Binding binding = BindingOperations.GetBinding(item.v, DataGridColumn.WidthProperty);
                if (binding != null)
                {
                    _getSetterOfPropertyPath<int>(binding.Source, binding.Path.Path.Substring(0, binding.Path.Path.LastIndexOf('.')) + ".DisplayIndex")(item.i);
                }
            }
        });
    }

    public void getDisplayIndicesPlaylistSummary()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            foreach (var item in dataGridPlaylistSummary.Columns.Where((DataGridColumn col) => BindingOperations.GetBinding(col, DataGridColumn.WidthProperty) != null).OrderBy(delegate (DataGridColumn col)
            {
                Binding binding = BindingOperations.GetBinding(col, DataGridColumn.WidthProperty);
                object obj = _getValueOfPropertyPath(binding.Source, binding.Path.Path.Substring(0, binding.Path.Path.LastIndexOf('.')) + ".DisplayIndex");
                return (obj is int) ? ((int)obj) : 0;
            }).Select((DataGridColumn v, int i) => new { v, i }))
            {
                item.v.DisplayIndex = item.i;
            }
        });
    }

    private void dataGrid_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && sender is DataGrid dataGrid2)
        {
            LogPlaylistDataGridState("visible_changed", dataGrid2);
            RequestSortGlyphRefresh(dataGrid2, "visible_changed");
        }
    }

    /// <summary>
    /// メイン DataGrid のロード完了時に binding と診断状態を初期化します。
    /// </summary>
    private void dataGrid_Loaded(object sender, RoutedEventArgs e)
    {
        AttachMainDataGridBindingOwner(base.DataContext as MainWindowViewModel);
        ApplyMainDataGridItemsSourceBinding(forceRebind: true);
        LogPlaylistDataGridState("loaded", sender as DataGrid);
        SchedulePlaylistRetentionCheckpoint(sender as DataGrid, "loaded");
    }

    /// <summary>
    /// メイン DataGrid のアンロード時に診断ログと監視状態を整理します。
    /// </summary>
    private void dataGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        LogPlaylistDataGridState("unloaded", sender as DataGrid);
    }

    /// <summary>
    /// メイン DataGrid の選択状態変化を診断ログへ出力します。
    /// </summary>
    private void dataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && viewModel.IsPlaylistDetailViewActive)
        {
            LogPlaylistDataGridState("selection_changed", sender as DataGrid);
        }
    }

    private void dataGridPlaylistSummary_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && sender is DataGrid dataGrid2)
        {
            getDisplayIndicesPlaylistSummary();
            RequestSortGlyphRefresh(dataGrid2, "visible_changed");
        }
    }

    private static Action<T> _getSetterOfPropertyPath<T>(object value, string path)
    {
        if (value == null)
        {
            return (T _) => { };
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
                return (T _) => { };
            }
            firstArgument = value;
            value = propertyInfo.GetValue(value, null);
            if (value == null && name != array.Last())
            {
                return (T _) => { };
            }
            type = propertyInfo.PropertyType;
        }
        MethodInfo setMethod = propertyInfo?.GetSetMethod();
        if (setMethod == null)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn($"Set method for property '{propertyInfo?.Name}' not found in path '{path}'");
            return (T _) => { };
        }
        return Delegate.CreateDelegate(typeof(Action<T>), firstArgument, setMethod) as Action<T>;
    }

    private async void dataGridRowDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is DataGridRow dataGridRow))
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        _renewBMSPlayerControlInfo(dataGridRow);
        e.Handled = true;
        if (e.ChangedButton == MouseButton.Left)
        {
            if ((viewModel.NowPlayingBMS == null || viewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE)) && isPanelStateValid(MainWindowViewModel.PanelState.BMS_PLAYER))
            {
                NowPanelState = MainWindowViewModel.PanelState.BMS_PLAYER;
            }
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile();
            }).Logging("dataGridRowDoubleClicked");
        }
    }

    private void dataGridRowSelected(object sender, RoutedEventArgs e)
    {
        if (sender is DataGridRow dataGridRow && base.DataContext is MainWindowViewModel { NowPlayingBMS: null })
        {
            _renewBMSPlayerControlInfo(dataGridRow);
        }
    }

    /// <summary>
    /// 現在 ViewModel で選択されている（再生中の）BMSファイルの情報を取得し、
    /// BMSPlayerコントロール（プレビュー画像やバナー、曲名などのUI情報）を最新状態に更新します。
    /// </summary>
    public void _renewBMSPlayerControlInfo()
    {
        if (base.DataContext is MainWindowViewModel { NowPlayingBMS: not null } mainWindowViewModel)
        {
            _renewBMSPlayerControlInfo(mainWindowViewModel.NowPlayingBMS);
        }
    }

    private List<object> GetSelectedGridRowsSnapshot()
    {
        try
        {
            return dataGrid.SelectedItems.Cast<object>().Where((object row) => row != null).ToList();
        }
        catch
        {
            return new List<object>();
        }
    }

    private List<BMSFile> GetSelectedGridOperationFiles()
    {
        return GetSelectedGridRowsSnapshot().Select(GridRowResolver.GetOperationBmsFile).Where((BMSFile file) => file != null).ToList();
    }

    private List<BMSFile> GetSelectedGridRealFiles()
    {
        return GetSelectedGridRowsSnapshot().Select(GridRowResolver.GetRealBmsFile).Where((BMSFile file) => file != null).ToList();
    }

    private List<BMSTableEntry> GetSelectedGridPlaylistEntries()
    {
        return GetSelectedGridRowsSnapshot().Select(GridRowResolver.GetPlaylistEntry).Where((BMSTableEntry entry) => entry != null).ToList();
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

    private bool TryGetContextMenuRow(object source, out ContextMenu contextMenu, out DataGridRow dataGridRow, out object row)
    {
        contextMenu = GetOwningContextMenu(source);
        dataGridRow = null;
        row = null;
        if (contextMenu?.PlacementTarget is not FrameworkElement placementTarget)
        {
            return false;
        }
        dataGridRow = placementTarget as DataGridRow ?? WPFUtil.FindVisualParent<DataGridRow>(placementTarget);
        row = dataGridRow?.DataContext ?? placementTarget.DataContext;
        return row != null;
    }

    private bool TryGetDataGridRowFromSource(object source, out DataGridRow dataGridRow, out object row)
    {
        dataGridRow = null;
        row = null;
        FrameworkElement frameworkElement = source as FrameworkElement;
        if (frameworkElement == null && source is DependencyObject dependencyObject)
        {
            dataGridRow = WPFUtil.FindVisualParent<DataGridRow>(dependencyObject);
        }
        else
        {
            dataGridRow = frameworkElement as DataGridRow ?? WPFUtil.FindVisualParent<DataGridRow>(frameworkElement);
        }
        row = dataGridRow?.DataContext ?? frameworkElement?.DataContext;
        return row != null;
    }

    private bool TryAssignDataGridContextMenu(DataGridRow dataGridRow, object row, string logPrefix, out bool usePlaylistMissingContextMenu)
    {
        usePlaylistMissingContextMenu = GridRowResolver.IsPlaylistRow(row) && GridRowResolver.GetOperationBmsFile(row) == null;
        string resourceKey = usePlaylistMissingContextMenu ? "dataGridContextMenuPlaylistMissing" : "dataGridContextMenu";
        if (TryFindResource(resourceKey) is not ContextMenu contextMenu)
        {
            return false;
        }
        dataGridRow.ContextMenu = contextMenu;
        NLogWrapper.FileLogger?.Info(logPrefix + " rowType=" + row?.GetType().FullName + " missing=" + usePlaylistMissingContextMenu + " resourceKey=" + resourceKey);
        return true;
    }

    private void dataGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetDataGridRowFromSource(e.OriginalSource, out DataGridRow dataGridRow, out object row))
        {
            return;
        }
        if (!(sender is DataGrid dataGrid))
        {
            return;
        }
        if (!TryAssignDataGridContextMenu(dataGridRow, row, "playlist_context_menu_prepare", out bool usePlaylistMissingContextMenu))
        {
            return;
        }
        if (!usePlaylistMissingContextMenu)
        {
            return;
        }
        dataGrid.SelectedItem = row;
        dataGridRow.IsSelected = true;
        dataGridRow.Focus();
        if (dataGridRow.ContextMenu == null)
        {
            return;
        }
        dataGridRow.ContextMenu.PlacementTarget = dataGridRow;
        dataGridRow.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        dataGridRow.ContextMenu.IsOpen = true;
        e.Handled = true;
        NLogWrapper.FileLogger?.Info("playlist_context_menu_manual_open rowType=" + row?.GetType().FullName + " missing=True");
    }

    private void dataGridRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!(sender is DataGridRow dataGridRow))
        {
            return;
        }
        object row = dataGridRow.DataContext;
        TryAssignDataGridContextMenu(dataGridRow, row, "playlist_context_menu_assign", out _);
    }

    /// <summary>
    /// 指定された DataGridRow にバインドされている BMSFile の情報を用いて、
    /// BMSPlayerコントロールのUI（画像・付帯情報）を更新します。
    /// </summary>
    /// <param name="dataGridRow">対象の BMSFile が存在する DataGridRow。</param>
    private void _renewBMSPlayerControlInfo(DataGridRow dataGridRow)
    {
        BMSFile bmsFile = GridRowResolver.GetOperationBmsFile(dataGridRow?.DataContext);
        if (bmsFile != null)
        {
            _renewBMSPlayerControlInfo(bmsFile);
        }
    }

    /// <summary>
    /// 指定された確定的 BMSFile インスタンス情報を用いて、
    /// BMSPlayerコントロールのUI（バナー画像レイアウト、タイトル文字列等）を同期します。
    /// </summary>
    /// <param name="bmsFile">更新対象となる BMS ファイル要素。</param>
    private void _renewBMSPlayerControlInfo(BMSFile bmsFile)
    {
        if (bmsFile == null)
        {
            return;
        }
        WriteableBitmap writeableBitmap = null;
        try
        {
            string text = ((string.IsNullOrWhiteSpace(bmsFile.path) || string.IsNullOrWhiteSpace(bmsFile.stagefile)) ? string.Empty : Path.Combine(DirectoryExt.GetDirectoryNameSimple(bmsFile.path), bmsFile.stagefile));
            if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
            {
                MemoryStream memoryStream = new MemoryStream(File.ReadAllBytes(text));
                writeableBitmap = new WriteableBitmap(BitmapFrame.Create(memoryStream));
                memoryStream.Close();
                gridBMSPlayerImage.Source = writeableBitmap;
            }
            else
            {
                gridBMSPlayerImage.Source = panelImage;
            }
        }
        catch
        {
            gridBMSPlayerImage.Source = panelImage;
        }
        try
        {
            string text2 = ((string.IsNullOrWhiteSpace(bmsFile.path) || string.IsNullOrWhiteSpace(bmsFile.banner)) ? string.Empty : Path.Combine(DirectoryExt.GetDirectoryNameSimple(bmsFile.path), bmsFile.banner));
            if (!string.IsNullOrWhiteSpace(text2) && File.Exists(text2))
            {
                ImageBrush imageBrush = new ImageBrush();
                MemoryStream memoryStream2 = new MemoryStream(File.ReadAllBytes(text2));
                WriteableBitmap imageSource = new WriteableBitmap(BitmapFrame.Create(memoryStream2));
                memoryStream2.Close();
                imageBrush.ImageSource = imageSource;
                imageBrush.Stretch = Stretch.Fill;
                gridBMSPlayerControlsBanner.Background = imageBrush;
            }
            else if (writeableBitmap == null)
            {
                gridBMSPlayerControlsBanner.Background = null;
            }
            else
            {
                gridBMSPlayerControlsBanner.Background = new ImageBrush(writeableBitmap)
                {
                    Stretch = Stretch.UniformToFill
                };
            }
        }
        catch
        {
            if (writeableBitmap == null)
            {
                gridBMSPlayerControlsBanner.Background = null;
            }
            else
            {
                gridBMSPlayerControlsBanner.Background = new ImageBrush(writeableBitmap)
                {
                    Stretch = Stretch.UniformToFill
                };
            }
        }
        gridBMSPlayerControlsBanner.BorderThickness = ((gridBMSPlayerControlsBanner.Background == null) ? new Thickness(0.0) : new Thickness(1.0, 0.0, 1.0, 0.0));
        gridBMSPlayerControlsTitle.Text = GridRowResolver.GetDisplayTitle(bmsFile);
        gridBMSPlayerControlsSubtitle.Text = GridRowResolver.GetDisplaySubtitle(bmsFile);
        gridBMSPlayerControlsArtist.Text = GridRowResolver.GetDisplayArtist(bmsFile);
    }

    private void dataGridCellBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        object row = e.Row.DataContext;
        BMSTableEntry playlistEntry = GridRowResolver.GetPlaylistEntry(row);
        BMSFile bMSFile = row as BMSFile;
        string path;
        try
        {
            path = ((Binding)((DataGridBoundColumn)e.Column).Binding).Path.Path;
        }
        catch
        {
            return;
        }
        if (playlistEntry != null)
        {
            int num = 250;
            if (path == nameof(PlaylistDetailRow.Url))
            {
                if (!GridRowResolver.CanEditPlaylistCell(row, path))
                {
                    e.Cancel = true;
                    return;
                }
                dataGridLengthConverterForURL1.IsEditingMode = true;
                e.Column.Width = num;
                e.Column.MaxWidth = double.MaxValue;
            }
            else if (path == nameof(PlaylistDetailRow.Url_diff))
            {
                if (!GridRowResolver.CanEditPlaylistCell(row, path))
                {
                    e.Cancel = true;
                    return;
                }
                dataGridLengthConverterForURL2.IsEditingMode = true;
                e.Column.Width = num;
                e.Column.MaxWidth = double.MaxValue;
            }
            else if ((path == nameof(PlaylistDetailRow.Level) || path == nameof(PlaylistDetailRow.comment) || path == nameof(PlaylistDetailRow.memo)) && !GridRowResolver.CanEditPlaylistCell(row, path))
            {
                e.Cancel = true;
            }
        }
        else if (bMSFile != null)
        {
            bool isPendingSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallPending;
            if (path == bMSFile.GetName((BMSFile f) => f.Folder) && isPendingSelected)
            {
                e.Cancel = true;
            }
            else if (path == bMSFile.GetName((BMSFile f) => f.instl_dst) && !isPendingSelected)
            {
                e.Cancel = true;
            }
        }
    }

    private void dataGridCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        object row = e.Row.DataContext;
        PlaylistDetailRow playlistRow = row as PlaylistDetailRow;
        BMSFile bmsFile = row as BMSFile;
        if (!(e.EditingElement is TextBox textBox))
        {
            return;
        }
        BindingExpression bindingExpression = textBox.GetBindingExpression(TextBox.TextProperty);
        if (bindingExpression == null)
        {
            return;
        }
        string path;
        try
        {
            path = ((Binding)((DataGridBoundColumn)e.Column).Binding).Path.Path;
        }
        catch
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        if (playlistRow != null)
        {
            if (path == nameof(PlaylistDetailRow.Url))
            {
                dataGridLengthConverterForURL1.IsEditingMode = false;
                Binding binding = BindingOperations.GetBinding(e.Column, DataGridColumn.WidthProperty);
                e.Column.Width = (int)_getValueOfPropertyPath(binding.Source, binding.Path.Path);
                e.Column.MaxWidth = e.Column.MinWidth;
            }
            else if (path == nameof(PlaylistDetailRow.Url_diff))
            {
                dataGridLengthConverterForURL2.IsEditingMode = false;
                Binding binding2 = BindingOperations.GetBinding(e.Column, DataGridColumn.WidthProperty);
                e.Column.Width = (int)_getValueOfPropertyPath(binding2.Source, binding2.Path.Path);
                e.Column.MaxWidth = e.Column.MinWidth;
            }
            if (e.EditAction != DataGridEditAction.Commit)
            {
                return;
            }
            if (!GridRowResolver.CanEditPlaylistCell(row, path))
            {
                bindingExpression.UpdateTarget();
                return;
            }
            if ((path == nameof(PlaylistDetailRow.Url) || path == nameof(PlaylistDetailRow.Url_diff)) && !Uri.TryCreate(textBox.Text, UriKind.Absolute, out var _))
            {
                bindingExpression.UpdateTarget();
                return;
            }
            bindingExpression.UpdateSource();
            viewModel.SyncPlaylistSourceRowFromEditedViewRow(playlistRow);
            base.Dispatcher.BeginInvoke((Action)async delegate
            {
                await Task.Run(delegate
                {
                    viewModel.CommitPlaylistRow(playlistRow);
                }).Logging("dataGridCellEditEnding");
            }, DispatcherPriority.Background);
        }
        else if (bmsFile != null && e.EditAction == DataGridEditAction.Commit)
        {
            bool isPendingSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallPending;
            if (path == bmsFile.GetName((BMSFile f) => f.Folder))
            {
                string newFolder = textBox.Text;
                dataGrid.CancelEdit();
                Task.Run(delegate
                {
                    viewModel.RenameBMSFolder(bmsFile, newFolder);
                }).Logging("dataGridCellEditEnding");
            }
            else if (path == bmsFile.GetName((BMSFile f) => f.instl_dst) && isPendingSelected)
            {
                string destinationDirectory = textBox.Text;
                dataGrid.CancelEdit();
                Task.Run(delegate
                {
                    viewModel.SetPendingInstallDestination(bmsFile, destinationDirectory);
                }).Logging("dataGridCellEditEnding");
            }
            else if (path == bmsFile.GetName((BMSFile f) => f.instl_dst))
            {
                bindingExpression.UpdateTarget();
            }
        }
    }

    private async void dataGridCellOpenURLClick(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is TextBlock textBlock))
        {
            return;
        }
        Uri url = GridRowResolver.GetUrl(textBlock.DataContext);
        if (url == null || !url.IsAbsoluteUri)
        {
            return;
        }
        if (!Settings.Default.SkipInitFileCheck && Settings.Default.AutoInstall)
        {
            try
            {
                if (!url.ToString().EndsWith("/") && !url.ToString().EndsWith(".htm") && !url.ToString().EndsWith(".html"))
                {
                    switch (await downloadAndInstall(url))
                    {
                        case DownloadAndInstallResult.Installed:
                            newlyInstalledTreeViewItem.IsExpanded = true;
                            return;
                        case DownloadAndInstallResult.BlockedBySizeLimit:
                            return;
                    }
                }
            }
            catch
            {
            }
        }
        Process.Start(url.ToString());
    }

    private async void dataGridCellOpenURLDiffClick(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is TextBlock textBlock))
        {
            return;
        }
        Uri urlDiff = GridRowResolver.GetUrlDiff(textBlock.DataContext);
        if (urlDiff == null || !urlDiff.IsAbsoluteUri)
        {
            return;
        }
        if (!Settings.Default.SkipInitFileCheck && Settings.Default.AutoInstall)
        {
            try
            {
                if (!urlDiff.ToString().EndsWith("/") && !urlDiff.ToString().EndsWith(".htm") && !urlDiff.ToString().EndsWith(".html"))
                {
                    switch (await downloadAndInstall(urlDiff))
                    {
                        case DownloadAndInstallResult.Installed:
                            newlyInstalledTreeViewItem.IsExpanded = true;
                            return;
                        case DownloadAndInstallResult.BlockedBySizeLimit:
                            return;
                    }
                }
            }
            catch
            {
            }
        }
        Process.Start(urlDiff.ToString());
    }

    private void dataGridEditingCellPreviewMouseDoubleClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void playlistRootSelect(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Source is TreeViewItem)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.SelectPlaylistSummary();
            }).Logging("playlistRootSelect");
        }
    }

    /// <summary>
    /// メインツリーやプレイリストツリー上で左クリックが行われた際の処理をハンドリングします。
    /// Node（TreeViewItem）の選択状態を判定し、クリックされた要素に応じてViewModel側に
    /// イベント（曲一覧の再生成等）を透過処理します。
    /// </summary>
    private void treeViewLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is TreeView treeViewControl))
        {
            return;
        }
        // スクロールバーやExpanderトグルのクリックではフォーカス移譲や更新を行わない
        DependencyObject source = e.OriginalSource as DependencyObject;
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
        else if (treeViewControl == this.treeView)
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
            TreeViewItem treeViewItem = parent.ItemContainerGenerator.ContainerFromIndex(i) as TreeViewItem;
            if (treeViewItem == null)
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
        TreeViewItem treeViewItem = rootTreeViewItem.ItemContainerGenerator.ContainerFromItem(targetDataContext) as TreeViewItem;
        if (treeViewItem == null)
        {
            rootTreeViewItem.UpdateLayout();
            treeViewItem = rootTreeViewItem.ItemContainerGenerator.ContainerFromItem(targetDataContext) as TreeViewItem;
        }
        if (treeViewItem == null)
        {
            treeViewItem = WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(rootTreeViewItem, targetDataContext);
        }
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
    /// プレイリストツリー用ノードから、一覧更新に使用するフィルタ種別を解決します。
    /// 特殊ノードは文字列逆変換ではなく、ノード種別を直接見て分岐します。
    /// </summary>
    /// <param name="folderNode">判定対象のプレイリストフォルダノード。</param>
    /// <returns>対応するプレイリストフィルタ種別。</returns>
    private static MainWindowViewModel.PlaylistFilterType GetPlaylistFilterType(PlaylistFolderNode folderNode)
    {
        if (folderNode == null || !folderNode.IsSpecial)
        {
            return MainWindowViewModel.PlaylistFilterType.PlaylistFilter;
        }
        return folderNode.SpecialKind switch
        {
            PlaylistFolderNodeSpecialKind.NotOwned => MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected,
            _ => MainWindowViewModel.PlaylistFilterType.PlaylistFilter
        };
    }

    /// <summary>
    /// プレイリストツリー選択時に ViewModel へ渡すフォルダ識別子を取得します。
    /// 通常ノードは論理フォルダ名、特殊ノードは表示名を返します。
    /// </summary>
    /// <param name="folderNode">対象ノード。</param>
    /// <returns>ViewModel 側で解釈するフォルダキー。</returns>
    private static string GetPlaylistFolderSelectionKey(PlaylistFolderNode folderNode)
    {
        if (folderNode == null)
        {
            return null;
        }
        return folderNode.IsSpecial ? folderNode.DisplayName : folderNode.FolderName;
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
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || selectedItem == null)
        {
            return;
        }
        if (selectedItem == treeViewItemPlaylist)
        {
            Task.Run(delegate
            {
                viewModel.SelectPlaylistSummary();
            }).Logging("ForceRefreshPlaylistTreeSelection");
            return;
        }
        BMSTable bmsTable = null;
        string folderName = null;
        MainWindowViewModel.PlaylistFilterType type = MainWindowViewModel.PlaylistFilterType.PlaylistFilter;
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
            folderName = GetPlaylistFolderSelectionKey(folderNode);
            type = GetPlaylistFilterType(folderNode);
            TreeViewItem ancestor = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(selectedItem));
            while (ancestor != null && !(ancestor.DataContext is BMSTable))
            {
                ancestor = FindAncestor<TreeViewItem>(VisualTreeHelper.GetParent(ancestor));
            }
            bmsTable = ancestor?.DataContext as BMSTable;
        }
        if (bmsTable != null)
        {
            Task.Run(delegate
            {
                viewModel.ExecPlaylistFilter(bmsTable, folderName, type);
            }).Logging("ForceRefreshPlaylistTreeSelection");
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
        TreeViewItem ownerTreeViewItem = contentPresenter.TemplatedParent as TreeViewItem;
        if (ownerTreeViewItem == null || !ownerTreeViewItem.IsSelected)
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
        if (!(sender is EditableTextBlock editableTextBlock) || !editableTextBlock.IsTextChanged())
        {
            return;
        }
        if (!(editableTextBlock.TemplatedParent is ContentPresenter { TemplatedParent: TreeViewItem templatedParent }))
        {
            return;
        }
        DependencyObject parent = VisualTreeHelper.GetParent(templatedParent);
        while (!(parent is TreeViewItem) && !(parent is TreeView) && parent != null)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (parent == null || parent is TreeView)
        {
            return;
        }
        BMSTable bmsTable = (parent as TreeViewItem).DataContext as BMSTable;
        if (bmsTable == null)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            if (!TryGetPlaylistFolderNode(templatedParent.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsEditable)
            {
                return;
            }
            string nameBefore = folderNode.FolderName;
            string nameAfter = editableTextBlock.Text;
            Task.Run(delegate
            {
                viewModel.RenameFolderBMSTable(bmsTable, nameBefore, nameAfter);
            }).Logging("playlistTableFolderNameChanged");
        }
    }

    private void playlistTableSelected(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        TreeViewItem treeViewItem = sender as TreeViewItem;
        TreeViewItem treeViewItem2 = e.Source as TreeViewItem;
        TreeViewItem treeViewItem3 = e.OriginalSource as TreeViewItem;
        if (viewModel == null || treeViewItem == null)
        {
            return;
        }
        BMSTable bmsTable = treeViewItem.DataContext as BMSTable;
        if (bmsTable == null)
        {
            return;
        }
        e.Handled = true;
        MainWindowViewModel.PlaylistFilterType type = MainWindowViewModel.PlaylistFilterType.PlaylistFilter;
        string folderName;
        if (treeViewItem2 != null)
        {
            folderName = null;
        }
        else
        {
            if (!TryGetPlaylistFolderNode(treeViewItem3.DataContext, out PlaylistFolderNode folderNode))
            {
                return;
            }
            folderName = GetPlaylistFolderSelectionKey(folderNode);
            type = GetPlaylistFilterType(folderNode);
        }
        e.Handled = true;
        Task.Run(delegate
        {
            viewModel.ExecPlaylistFilter(bmsTable, folderName, type);
        }).Logging("playlistTableSelected");
    }

    private void treeViewItemOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock reference)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(reference);
            while (!(parent is TreeViewItem) && !(parent is TreeView) && parent != null)
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
        if (e.Source is TreeViewItem treeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.DirectoryFilter, treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private void artistFolderSelect(object sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem treeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.ArtistFilter, treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private void rootFolderSelect(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.FilterNone);
        }
    }

    private List<PlaylistSummaryRow> getSelectedPlaylistSummaryRows(PlaylistSummaryRow fallback = null)
    {
        List<PlaylistSummaryRow> list = new List<PlaylistSummaryRow>();
        if (dataGridPlaylistSummary != null && dataGridPlaylistSummary.SelectedItems != null)
        {
            list = dataGridPlaylistSummary.SelectedItems.Cast<PlaylistSummaryRow>().Where((PlaylistSummaryRow r) => r != null).ToList();
        }
        if ((list == null || list.Count == 0) && fallback != null)
        {
            list = new List<PlaylistSummaryRow> { fallback };
        }
        return list ?? new List<PlaylistSummaryRow>();
    }

    private PlaylistSummaryRow resolvePlaylistSummaryRowFromSender(object sender)
    {
        PlaylistSummaryRow playlistSummaryRow = (sender as FrameworkElement)?.DataContext as PlaylistSummaryRow;
        if (playlistSummaryRow != null)
        {
            return playlistSummaryRow;
        }
        return getSelectedPlaylistSummaryRows().FirstOrDefault();
    }

    /// <summary>
    /// プレイリストサマリー行のダブルクリック時に、対応するプレイリストを左ツリーで選択します。
    /// 既存のツリー選択イベントを再利用し、プレイリスト絞り込み表示への遷移も従来の選択経路に委ねます。
    /// </summary>
    /// <param name="sender">ダブルクリックされた <see cref="DataGridRow"/>。</param>
    /// <param name="e">マウス入力情報。</param>
    private void playlistSummaryRowDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !(sender is DataGridRow { DataContext: PlaylistSummaryRow playlistSummaryRow }) || playlistSummaryRow.TableRef == null)
        {
            return;
        }
        DependencyObject originalSource = e.OriginalSource as DependencyObject;
        // NOTE:
        // サマリー行には Button / CheckBox を含むため、行ダブルクリックがそれらの既存操作を横取りしないように除外します。
        if (FindAncestor<Button>(originalSource) != null || FindAncestor<CheckBox>(originalSource) != null || FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(originalSource) != null || FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(originalSource) != null)
        {
            return;
        }
        e.Handled = true;
        TrySelectPlaylistTreeItemFromSummary(playlistSummaryRow);
    }

    /// <summary>
    /// プレイリストサマリー行に対応するプレイリストをプレイリストツリー上で選択します。
    /// 再読み込み後に <see cref="PlaylistSummaryRow.TableRef"/> が古い参照になっていても、既存の再選択補助ロジックで解決を試みます。
    /// </summary>
    /// <param name="playlistSummaryRow">選択元のプレイリストサマリー行。</param>
    /// <returns>プレイリストツリー項目の選択に成功した場合は <see langword="true"/>。</returns>
    private bool TrySelectPlaylistTreeItemFromSummary(PlaylistSummaryRow playlistSummaryRow)
    {
        if (playlistSummaryRow?.TableRef == null)
        {
            return false;
        }
        BMSTable selectionTarget = FindReloadedPlaylistTable(playlistSummaryRow.TableRef);
        bool restoredByHeader = false;
        if (selectionTarget == null)
        {
            selectionTarget = FindPlaylistTableByName(playlistSummaryRow.Name);
            restoredByHeader = selectionTarget != null;
        }
        string playlistName = selectionTarget?.name ?? playlistSummaryRow.Name ?? string.Empty;
        string playlistId = playlistSummaryRow.PlaylistId?.ToString() ?? string.Empty;
        if (selectionTarget == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=false reason=target_not_found table=" + playlistName + " playlistId=" + playlistId + " fallbackByHeader=" + restoredByHeader);
            return false;
        }
        bool usedVirtualizationFallback;
        bool realizeByIndexAvailable;
        bool selected = TrySelectPlaylistTreeItem(selectionTarget, out usedVirtualizationFallback, out realizeByIndexAvailable);
        if (!selected)
        {
            NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=false reason=container_not_realized table=" + playlistName + " playlistId=" + playlistId + " fallbackByHeader=" + restoredByHeader + " realize_by_index_available=" + realizeByIndexAvailable);
            return false;
        }
        NLogWrapper.FileLogger?.Info("playlist_summary_double_click_select success=true table=" + playlistName + " playlistId=" + playlistId + " fallbackByHeader=" + restoredByHeader + " usedVirtualizationFallback=" + usedVirtualizationFallback + " expanded=true");
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
        TreeViewItem playlistTreeViewItem = treeViewItemPlaylist.ItemContainerGenerator.ContainerFromIndex(playlistIndex) as TreeViewItem;
        if (playlistTreeViewItem != null)
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
            playlistTreeBringIndexIntoViewMethod.Invoke(playlistItemsHostPanel, new object[1] { playlistIndex });
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

    /// <summary>
    /// 指定されたプレイリスト名に一致するトップレベルプレイリストを検索します。
    /// </summary>
    /// <param name="playlistName">検索対象のプレイリスト名。</param>
    /// <returns>一致する <see cref="BMSTable"/>。見つからない場合は <see langword="null"/>。</returns>
    private BMSTable FindPlaylistTableByName(string playlistName)
    {
        if (string.IsNullOrWhiteSpace(playlistName))
        {
            return null;
        }
        return treeViewItemPlaylist.Items.OfType<BMSTable>().FirstOrDefault((BMSTable playlistTable) => playlistTable != null && string.Equals(playlistTable.name, playlistName, StringComparison.Ordinal));
    }

    private async void playlistSummaryLinkClick(object sender, RoutedEventArgs e)
    {
        if (!(sender is Button { DataContext: PlaylistSummaryRow playlistSummaryRow }) || playlistSummaryRow.LinkUri == null)
        {
            return;
        }
        await Task.Run(delegate
        {
            try
            {
                Process.Start(playlistSummaryRow.LinkUri.ToString());
            }
            catch
            {
            }
        }).Logging("playlistSummaryLinkClick");
    }

    private async void playlistSummarySyncCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if (!(sender is CheckBox { DataContext: PlaylistSummaryRow playlistSummaryRow } checkBox))
        {
            return;
        }
        bool flag = checkBox.IsChecked == true;
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        ConfirmationMessage confirmationMessage = new ConfirmationMessage((!flag) ? ("同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？") : ("同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？"), "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
        (base.DataContext as MainWindowViewModel)?.Messenger.Raise(confirmationMessage);
        if (!confirmationMessage.Response.HasValue || !confirmationMessage.Response.Value)
        {
            checkBox.IsChecked = !flag;
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, flag, null);
            }).Logging("playlistSummarySyncCheckBoxClick");
        }
        e.Handled = true;
    }

    private async void playlistSummaryRootCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if (!(sender is CheckBox { DataContext: PlaylistSummaryRow playlistSummaryRow } checkBox))
        {
            return;
        }
        bool flag = checkBox.IsChecked == true;
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, null, flag);
            }).Logging("playlistSummaryRootCheckBoxClick");
        }
        e.Handled = true;
    }

    private async void playlistSummaryContextMenuResyncClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow == null)
        {
            return;
        }
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && selectedPlaylistSummaryRows.Count > 0)
        {
            await viewModel.ResyncPlaylistsAsync(selectedPlaylistSummaryRows).Logging("playlistSummaryContextMenuResyncClick");
        }
    }

    private void playlistSummaryContextMenuOpenPageClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow?.LinkUri != null)
        {
            try
            {
                Process.Start(playlistSummaryRow.LinkUri.ToString());
            }
            catch
            {
            }
        }
    }

    private void playlistSummaryContextMenuOpenPropertyClick(object sender, RoutedEventArgs e)
    {
        PlaylistSummaryRow playlistSummaryRow = resolvePlaylistSummaryRowFromSender(sender);
        if (playlistSummaryRow?.TableRef == null)
        {
            return;
        }
        MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
        if (mainWindowViewModel != null && !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable)
        {
            mainWindowViewModel.playlistPropertyDialog = new MainWindowViewModel.PlaylistPropertyDialogViewModel(mainWindowViewModel, playlistSummaryRow.TableRef);
            playlistPropertyDialog.Visibility = Visibility.Visible;
        }
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
        if (MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_playlist, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                foreach (PlaylistSummaryRow item in selectedPlaylistSummaryRows)
                {
                    if (item?.TableRef != null)
                    {
                        viewModel.RemoveBMSTable(item.TableRef);
                    }
                }
                viewModel.RebuildPlaylistSummaryView(runAsync: false);
            }).Logging("playlistSummaryContextMenuRemoveClick");
        }
    }

    private async void fullScanCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        TreeViewItem treeRoot = sender as TreeViewItem;
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && treeRoot != null)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.FileMissingFilter);
            }).Logging("fullScanCheckFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void fullScanCheckIgnoredFolderSelect(object sender, RoutedEventArgs e)
    {
        TreeViewItem treeViewItem = sender as TreeViewItem;
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && treeViewItem != null)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.FileMissingIgnoredFilter);
            }).Logging("fullScanCheckIgnoredFolderSelect");
        }
    }

    private async void dupulicateFileCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        TreeViewItem treeRoot = sender as TreeViewItem;
        TreeViewItem treeViewItem = e.OriginalSource as TreeViewItem;
        if (viewModel == null || treeRoot == null || treeViewItem == null)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.DuplicateFilter);
            }).Logging("dupulicateFileCheckFolderSelect");
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
            if (!(treeViewItem.DataContext is List<BMSFile>))
            {
                return;
            }
            parameter = (List<BMSFile>)treeViewItem.DataContext;
        }
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.DuplicateFilter, parameter);
        }).Logging("dupulicateFileCheckFolderSelect");
    }

    private async void garbledCheckFolderSelect(object sender, RoutedEventArgs e)
    {
        TreeViewItem treeRoot = sender as TreeViewItem;
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && treeRoot != null)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.GarbledFilter);
            }).Logging("garbledCheckFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void garbleFixedFolderSelect(object sender, RoutedEventArgs e)
    {
        TreeViewItem treeRoot = sender as TreeViewItem;
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && treeRoot != null)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.GarbleFixedFilter);
            }).Logging("garbleFixedFolderSelect");
            treeRoot.IsExpanded = true;
        }
    }

    private async void unregisteredToDBFolderSelect(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.UnregisteredFilter);
        }).Logging("unregisteredToDBFolderSelect");
    }

    private async void zeronoteFolderSelect(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.ZeroNoteFilter);
        }).Logging("zeronoteFolderSelect");
    }

    private void treeViewZeroNoteContextMenuItemRecheckClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            Task.Run(delegate
            {
                viewModel.RecheckZeroNoteWarnings();
            }).Logging("treeViewZeroNoteContextMenuItemRecheckClick");
        }
    }

    private async void newlyInstalledFolderSelect(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        TreeViewItem treeRoot = sender as TreeViewItem;
        TreeViewItem treeViewItem = e.OriginalSource as TreeViewItem;
        if (viewModel == null || treeRoot == null || treeViewItem == null)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter);
            }).Logging("newlyInstalledFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        BMSPackage package = treeViewItem.DataContext as BMSPackage;
        if (package != null)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter, package);
            }).Logging("newlyInstalledFolderSelect");
        }
    }

    private async void pendingInstallFolderSelect(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        TreeViewItem treeRoot = sender as TreeViewItem;
        TreeViewItem treeViewItem = e.OriginalSource as TreeViewItem;
        if (viewModel == null || treeRoot == null || treeViewItem == null)
        {
            return;
        }
        e.Handled = true;
        if (treeRoot == treeViewItem)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("pendingInstallFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        BMSPackage package = treeViewItem.DataContext as BMSPackage;
        if (package != null)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter, package);
            }).Logging("pendingInstallFolderSelect");
        }
    }

    /// <summary>
    /// プレイリストツリーの「ルート（プレイリスト一覧）」に対するコンテキストメニューが開かれた際の処理。
    /// 現在の書き込みロック状態やバックグラウンド処理状況（IsWriteLockHeld等）に応じ、
    /// 新規作成やURL読み込みなどの各種メニュー項目の有効/無効 (IsEnabled) を動的に制御します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (!(sender is ContextMenu contextMenu) || !(base.DataContext is MainWindowViewModel mainWindowViewModel))
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
            if (!(item is MenuItem))
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
            menuItem.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable;
        }
        if (menuItem2 != null)
        {
            menuItem2.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin;
        }
        if (menuItem3 != null)
        {
            menuItem3.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsLoadingExternalCollectionBMSTables;
        }
        if (menuItem4 != null)
        {
            menuItem4.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin;
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「新しいプレイリストを作成」がクリックされた際の処理。
    /// 非同期で空のBMSTable（プレイリスト）を生成し、直後にプロパティ変更ダイアログを表示させます。
    /// </summary>
    private async void treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || !(sender is MenuItem) || viewModel.IsWriteLockHeldBMSTablesInitializeMin || viewModel.IsWriteLockHeldBMSTables || viewModel.IsWriteLockHeldAnyBMSTable)
        {
            return;
        }
        try
        {
            BMSTable bMSTable = await Task.Run(() => viewModel.CreateBMSTable()).Logging("treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick");
            if (bMSTable != null)
            {
                viewModel.playlistPropertyDialog = new MainWindowViewModel.PlaylistPropertyDialogViewModel(viewModel, bMSTable, _isForNewTable: true);
                playlistPropertyDialog.Visibility = Visibility.Visible;
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
        if (base.DataContext is MainWindowViewModel { IsWriteLockHeldBMSTablesInitializeMin: false })
        {
            loadPlaylistURIDialog.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「プレイリストコレクションを読み込む」がクリックされた際の処理。
    /// 指定されたコレクションURLをもとに、ViewModelへプレイリスト群の非同期登録を要求します。
    /// </summary>
    private async void treeViewPlaylistRootContextMenuItemLoadPlaylistCollectionClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (!(sender is MenuItem menuItem))
        {
            return;
        }
        BMSTableSimple dataContext = menuItem.DataContext as BMSTableSimple;
        if (viewModel != null && dataContext != null && !(dataContext.url == null) && !viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            await viewModel.RegistrateExternalPlaylistBMSTableAsync(dataContext.url).Logging("treeViewPlaylistRootContextMenuItemLoadPlaylistCollectionClick");
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「Walkure/難易度表を読み込む」に関するメニュー項目（各難易度表単位）のアクション。
    /// MenuItemのTagプロパティに格納されたURLへアクセスし、プレイリスト情報を非同期で追加・登録します。
    /// </summary>
    private async void treeViewPlaylistRootContextMenuItemLoadWalkureTableClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && sender is MenuItem menuItem && !viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            Uri uri = new Uri((string)menuItem.Tag);
            await viewModel.RegistrateExternalPlaylistBMSTableAsync(uri).Logging("treeViewPlaylistRootContextMenuItemLoadWalkureTableClick");
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニューから「Walkureのおすすめフォルダ」関連のテーブル読み込みが選択された場合の処理。
    /// LR2IDの設定状況のチェックや、更新モード/閲覧モードに応じたユーザー確認ダイアログを挟んだ後、非同期で登録処理へ進みます。
    /// </summary>
    private async void treeViewPlaylistRootContextMenuItemLoadWalkureTableRecommendedClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || !(sender is MenuItem menuItem) || viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            return;
        }
        Uri uri = new Uri((string)menuItem.Tag);
        if (viewModel.LR2ID == 0)
        {
            MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_error, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return;
        }
        if (Regex.Match((string)menuItem.Tag, "mode=update").Success)
        {
            if (MessageBox.Show(Window.GetWindow(this), "LR2ID: " + viewModel.LR2ID + BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_update_mode, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) == MessageBoxResult.Cancel)
            {
                return;
            }
        }
        else if (MessageBox.Show(Window.GetWindow(this), "LR2ID: " + viewModel.LR2ID + BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_readonly_mode, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) == MessageBoxResult.Cancel)
        {
            return;
        }
        await viewModel.RegistrateExternalPlaylistBMSTableAsync(uri).Logging("treeViewPlaylistRootContextMenuItemLoadWalkureTableRecommendedClick");
    }

    /// <summary>
    /// プレイリスト（難易度表等の直下にある上位階層）のコンテキストメニューが開かれた際の処理。
    /// 現在の選択要素 (BMSTable) の属性（外部同期するか否か、URLの有無など）や
    /// アプリ状態に応じて、メニュー各項目の有効化状態 (IsEnabled) を切り替えます。
    /// </summary>
    private void treeViewPlaylistTableContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (!(sender is ContextMenu contextMenu) || !(base.DataContext is MainWindowViewModel mainWindowViewModel) || !(contextMenu.PlacementTarget is TreeViewItem { DataContext: BMSTable dataContext }))
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
        Uri uri = dataContext.Page_url ?? dataContext.Header_url;
        bool flag = uri != null && uri.IsAbsoluteUri;
        menuItem7.IsEnabled = flag;
        menuItem.IsEnabled = dataContext.Page_url != null || dataContext.GetAbsoluteHeaderUrl() != null;
        menuItem2.IsEnabled = dataContext.Page_url != null && dataContext.Page_url.Scheme != "bmseeker" && dataContext.is_external_sync && mainWindowViewModel.LR2ID != 0;
        menuItem4.IsEnabled = !dataContext.is_external_sync;
        menuItem3.IsEnabled = true;
        menuItem5.IsEnabled = true;
        menuItem6.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable;
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「最新の情報に更新（リロード）」実行時の処理。
    /// 対象プレイリスト単体の同期更新を非同期で実行し、完了後に選択状態の復元を試みます。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemReloadClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || !(sender is MenuItem { DataContext: BMSTable table }))
        {
            return;
        }
        await viewModel.ResyncPlaylistsAsync(new BMSTable[1] { table }).Logging("treeViewPlaylistTableContextMenuItemReloadClick");
        RestorePlaylistTableSelectionAfterReload(table);
    }

    private void RestorePlaylistTableSelectionAfterReload(BMSTable tableBeforeReload)
    {
        if (tableBeforeReload == null)
        {
            return;
        }
        BMSTable selectionTarget = FindReloadedPlaylistTable(tableBeforeReload);
        if (selectionTarget == null)
        {
            NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload skipped reason=target_not_found");
            return;
        }
        bool usedVirtualizationFallback;
        bool realizeByIndexAvailable;
        bool restored = TrySelectPlaylistTreeItem(selectionTarget, out usedVirtualizationFallback, out realizeByIndexAvailable);
        if (!restored)
        {
            NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=false reason=container_not_realized table=" + selectionTarget.name + " realize_by_index_available=" + realizeByIndexAvailable);
            return;
        }
        NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=true table=" + selectionTarget.name + " expanded=true usedVirtualizationFallback=" + usedVirtualizationFallback);
    }

    private BMSTable FindReloadedPlaylistTable(BMSTable tableBeforeReload)
    {
        IEnumerable<BMSTable> source = treeViewItemPlaylist.Items.OfType<BMSTable>();
        if (tableBeforeReload.playlist_id.HasValue)
        {
            BMSTable byId = source.FirstOrDefault((BMSTable t) => t != null && t.playlist_id.HasValue && t.playlist_id.Value == tableBeforeReload.playlist_id.Value);
            if (byId != null)
            {
                return byId;
            }
        }
        string pageUrl = tableBeforeReload.Page_url?.AbsoluteUri ?? string.Empty;
        string headerUrl = tableBeforeReload.GetAbsoluteHeaderUrl()?.AbsoluteUri ?? string.Empty;
        BMSTable byUrl = source.FirstOrDefault(delegate (BMSTable t)
        {
            if (t == null)
            {
                return false;
            }
            string text = t.Page_url?.AbsoluteUri ?? string.Empty;
            string text2 = t.GetAbsoluteHeaderUrl()?.AbsoluteUri ?? string.Empty;
            return string.Equals(text, pageUrl, StringComparison.OrdinalIgnoreCase) && string.Equals(text2, headerUrl, StringComparison.OrdinalIgnoreCase);
        });
        if (byUrl != null)
        {
            return byUrl;
        }
        return source.FirstOrDefault((BMSTable t) => t != null && string.Equals(t.name, tableBeforeReload.name, StringComparison.Ordinal));
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「配布ページを開く」実行時の処理。
    /// BMSTableに設定されたURL (Page_url または Header_url) を標準ブラウザ等で開きます。
    /// 特殊スキーム（Walkure難易度表等）の場合は専用のURLへ変換してブラウザ起動します。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOpenPageURIClick(object sender, RoutedEventArgs e)
    {
        if (!(base.DataContext is MainWindowViewModel mainWindowViewModel) || !(sender is MenuItem { DataContext: BMSTable dataContext }))
        {
            return;
        }
        Uri uri = dataContext.Page_url ?? dataContext.GetAbsoluteHeaderUrl();
        if (uri.Scheme != "bmseeker")
        {
            if (uri != null)
            {
                Process.Start(uri.ToString());
            }
        }
        else if (uri.ToString().StartsWith("bmseeker:table.estimation"))
        {
            Process.Start("http://walkure.net/hakkyou/bms.html");
        }
        else if (uri.ToString().StartsWith("bmseeker:table.recommended") && mainWindowViewModel.LR2ID != 0)
        {
            Process.Start("http://walkure.net/hakkyou/recommended_mypage.html?playerid=" + mainWindowViewModel.LR2ID);
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「クリア状況ページを開く」実行時の処理。
    /// ユーザーのLR2IDと対象難易度表URLをパラメータにし、外部連携サイト（通常はWalkureのクリアランプページ）を表示します。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOpenClearLampClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && sender is MenuItem { DataContext: BMSTable dataContext } && dataContext.Page_url != null && dataContext.is_external_sync && mainWindowViewModel.LR2ID != 0)
        {
            Process.Start(clearlampUri + "?lr2ID=" + Uri.EscapeDataString(mainWindowViewModel.LR2ID.ToString()) + "&table_url=" + Uri.EscapeDataString(dataContext.Page_url.ToString()));
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「フォルダを作成」実行時の処理。
    /// 選択中のプレイリスト配下に新しいサブフォルダ用BMSTable要素を非同期で追加します（自作プレイリスト用）。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemCreateNewFolderClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || !(sender is MenuItem menuItem))
        {
            return;
        }
        BMSTable bmsTable = menuItem.DataContext as BMSTable;
        if (bmsTable != null && !bmsTable.is_external_sync)
        {
            await Task.Run(delegate
            {
                viewModel.CreateNewFolderBMSTable(bmsTable);
            }).Logging("treeViewPlaylistTableContextMenuItemCreateNewFolderClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「一覧をエキスポート (JSON)」実行時の処理。
    /// 保存用ファイルダイアログを開き、指定されたパスに header.json と data.json 形式で
    /// プレイリスト情報をファイル書き出し（出力）します。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemExportTableClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || !(sender is MenuItem menuItem))
        {
            return;
        }
        BMSTable bmsTable = menuItem.DataContext as BMSTable;
        if (bmsTable == null)
        {
            return;
        }
        SaveFileDialog fileDialogHeader = new SaveFileDialog();
        SaveFileDialog fileDialogData = new SaveFileDialog();
        fileDialogHeader.Title = BeMusicSeeker.Properties.Resources.Save_header_file;
        fileDialogData.Title = BeMusicSeeker.Properties.Resources.Save_data_file;
        fileDialogHeader.FileName = ((!string.IsNullOrWhiteSpace(bmsTable.header_url)) ? Path.GetFileName(bmsTable.Header_url.ToString()) : "header.json");
        fileDialogData.FileName = ((!string.IsNullOrWhiteSpace(bmsTable.data_url)) ? Path.GetFileName(bmsTable.Data_url.ToString()) : "data.json");
        SaveFileDialog saveFileDialog = fileDialogHeader;
        string filter = (fileDialogData.Filter = BeMusicSeeker.Properties.Resources.Json_file_exts);
        saveFileDialog.Filter = filter;
        if (fileDialogHeader.ShowDialog() == true && fileDialogData.ShowDialog() == true)
        {
            await Task.Run(delegate
            {
                viewModel.ExportBMSTable(bmsTable, fileDialogHeader.FileName, fileDialogData.FileName);
            }).Logging("treeViewPlaylistTableContextMenuItemExportTableClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「ローカルBMSの難易度をこの表で上書き」実行時の処理。
    /// ユーザー確認ダイアログ表示後、このプレイリストに登録されている各楽曲のレベル情報を用いて
    /// メインDB（ローカルの全BMS情報）の同等楽曲のレベル値を書き換えます。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOverwriteLevelClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || !(sender is MenuItem menuItem))
        {
            return;
        }
        BMSTable bmsTable = menuItem.DataContext as BMSTable;
        if (bmsTable == null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(bmsTable.page_url) && bmsTable.page_url.StartsWith("bmseeker:table.recommended"))
        {
            MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_override_level_error_recommended, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else if (MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_override_level_warning, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.ReplaceBMSFileLevelByTableEntryLevel(bmsTable);
            }).Logging("treeViewPlaylistTableContextMenuItemOverwriteLevelClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「この表を削除」実行時の処理。
    /// ユーザー確認を取った上で選択中のプレイリストをツリーから除外し、非同期でメイン側からも削除・破棄します。
    /// UI仮想化による選択ロストを防ぐためのフォールバック遷移も併せて行います。
    /// </summary>
    private async void treeViewPlaylistTableContextMenuItemRemoveTableClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || !(sender is MenuItem menuItem))
        {
            return;
        }
        BMSTable bmsTable = menuItem.DataContext as BMSTable;
        if (bmsTable == null || MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_playlist, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemPlaylist, bmsTable, "treeViewPlaylistTableContextMenuItemRemoveTableClick");
        await Task.Run(delegate
        {
            viewModel.RemoveBMSTable(bmsTable);
        }).Logging("treeViewPlaylistTableContextMenuItemRemoveTableClick");
        if (treeViewItemPlaylist.IsSelected && treeViewItemPlaylist.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecPlaylistFilter(null);
            }).Logging("treeViewPlaylistTableContextMenuItemRemoveTableClick");
        }
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「プロパティ」実行時の処理。
    /// 選択中の難易度表（BMSTable）の詳細情報や同期URLなどを確認・編集できる専用ダイアログを開きます。
    /// </summary>
    private void treeViewPlaylistTableCcontextMenuItemOpenPropertyDialogClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
        MenuItem menuItem = sender as MenuItem;
        if (mainWindowViewModel != null && menuItem != null && menuItem.DataContext is BMSTable table && !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable)
        {
            mainWindowViewModel.playlistPropertyDialog = new MainWindowViewModel.PlaylistPropertyDialogViewModel(mainWindowViewModel, table);
            playlistPropertyDialog.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// プレイリスト配下の「フォルダ（自作/自動生成）」のコンテキストメニューが開かれた際の処理。
    /// ツリーのVisualTreeを遡り、現在選択しているアイテムが外部同期中のものかを判別して、
    /// 「フォルダ名変更」や「削除」といった編集メニューの有効化状態を制御します。
    /// </summary>
    private void treeViewPlaylistTableFolderContextMenuOpend(object sender, RoutedEventArgs e)
    {
        if (!(sender is ContextMenu contextMenu) || !(base.DataContext is MainWindowViewModel))
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
        if (!(contextMenu.PlacementTarget is TreeViewItem reference))
        {
            return null;
        }
        DependencyObject parent = VisualTreeHelper.GetParent(reference);
        while ((!(parent is TreeViewItem) || !((parent as TreeViewItem).DataContext is BMSTable)) && parent != null)
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
        if (!(contextMenu.PlacementTarget is TreeViewItem treeViewItem))
        {
            return null;
        }
        return _findTypeFromVisualChildren<EditableTextBlock>(new TreeViewItem[1] { treeViewItem });
    }

    private static Type _findTypeFromVisualChildren<Type>(IEnumerable<DependencyObject> _objs) where Type : class
    {
        IEnumerable<DependencyObject> enumerable = _objs.SelectMany((DependencyObject obj) => _getVisualChildren(obj));
        if (enumerable.Count() == 0)
        {
            return null;
        }
        DependencyObject dependencyObject = enumerable.FirstOrDefault((DependencyObject c) => c is Type);
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
    private async void treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock == null || !TryGetPlaylistFolderNode(editableTextBlock.DataContext, out PlaylistFolderNode folderNode) || folderNode.IsSpecial)
        {
            return;
        }
        BMSTable bmsTable = _getUpperBMSTableForContextMenuClickEvent(sender);
        if (bmsTable == null || bmsTable.is_external_sync)
        {
            return;
        }
        string folderNameDelete = folderNode.FolderName;
        if (MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_folder, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            await Task.Run(delegate
            {
                viewModel.RemoveFolderBMSTable(bmsTable, folderNameDelete);
            }).Logging("treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick");
        }
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
        if (!Directory.Exists(text))
        {
            return;
        }
        try
        {
            Process.Start("EXPLORER.EXE", "\"" + text + "\"");
        }
        catch
        {
        }
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
        if (!(base.DataContext is MainWindowViewModel))
        {
            return;
        }
        MainWindowViewModel.SettingDialogViewModel viewModel = (base.DataContext as MainWindowViewModel).settingDialog;
        if (Directory.Exists(path) && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_unregister_root_folder, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.RemoveBMSDirectoryFromRootFolderAndSave(path);
            }).Logging("treeViewLibraryFolderContextMenuItemUnregisterRootFolder");
        }
    }

    private void treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string path = placementTarget.Header.ToString();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && Directory.Exists(path) && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_rename_folders, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.AutoRenameAllBMSFolder(path);
            }).Logging("treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick");
        }
    }

    private void treeViewInstalledContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_all_installed, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.RemoveBMSPackagesInstalledAll();
            }).Logging("treeViewInstalledContextMenuClearAllClick");
        }
    }

    private void treeViewInstallPendingContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_all_pendings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.RemoveBMSPackagesPendingAll();
            }).Logging("treeViewInstallPendingContextMenuClearAllClick");
        }
    }

    private async void treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        List<BMSPackage> list = viewModel.GetPendingPackagesContainingOnlyInstalledCharts();
        if (list.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_delete_pending_installed_only_packages_permanently, list.Count);
        if (MessageBox.Show(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        if (list.Count == 1)
        {
            await Task.Run(delegate
            {
                viewModel.DeletePendingPackageSources(list, sendToRecycleBin: false);
            }).Logging("treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick");
            return;
        }
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        int processedCount = 0;
        int total = list.Count;
        Task task = Task.Run(delegate
        {
            viewModel.DeletePendingPackageSources(list, sendToRecycleBin: false, cancellationTokenSource.Token, delegate
            {
                processedCount++;
            });
        }, cancellationTokenSource.Token).Logging("treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick");
        ProgressDialog.Execute(this, BeMusicSeeker.Properties.Resources.Remove, "", delegate
        {
            while (task.Status != TaskStatus.RanToCompletion)
            {
                try
                {
                    ProgressDialog.Current.ReportWithCancellationCheck(100 * processedCount / total, "[{0}/{1}] {2}", Math.Min(processedCount + 1, total), total, list[Math.Min(processedCount, total - 1)].path);
                }
                catch
                {
                    cancellationTokenSource.Cancel();
                    while (task.Status != TaskStatus.RanToCompletion)
                    {
                        Thread.Sleep(100);
                    }
                    break;
                }
                Thread.Sleep(100);
            }
        }, new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false));
        await task;
    }

    private async void treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        List<BMSFile> list = viewModel.GetPendingBMSFilesSnapshot();
        if (list.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_charts, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_rename_pending_zero_note_to_invalid_ext, list.Count);
        if (MessageBox.Show(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        if (list.Count == 1)
        {
            await Task.Run(delegate
            {
                viewModel.RenamePendingZeroNoteChartsToInvalidExtensions(list, CancellationToken.None, null);
            }).Logging("treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick");
            return;
        }
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        int processedCount = 0;
        int total = list.Count;
        Task task = Task.Run(delegate
        {
            viewModel.RenamePendingZeroNoteChartsToInvalidExtensions(list, cancellationTokenSource.Token, delegate
            {
                processedCount++;
            });
        }, cancellationTokenSource.Token).Logging("treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick");
        ProgressDialog.Execute(this, BeMusicSeeker.Properties.Resources.Rename_invalid_ext, "", delegate
        {
            while (task.Status != TaskStatus.RanToCompletion)
            {
                try
                {
                    ProgressDialog.Current.ReportWithCancellationCheck(100 * processedCount / total, "[{0}/{1}] {2}", Math.Min(processedCount + 1, total), total, list[Math.Min(processedCount, total - 1)].path ?? "(null)");
                }
                catch
                {
                    cancellationTokenSource.Cancel();
                    while (task.Status != TaskStatus.RanToCompletion)
                    {
                        Thread.Sleep(100);
                    }
                    break;
                }
                Thread.Sleep(100);
            }
        }, new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false));
        await task;
    }

    private async void treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        List<BMSPackage> list = viewModel.GetPendingPackagesContainingOnlyInstalledCharts();
        if (list.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_overwrite_pending_installed_only_packages_resources, list.Count);
        if (MessageBox.Show(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        PendingInstalledOnlyResourceOverwriteResult pendingInstalledOnlyResourceOverwriteResult;
        if (list.Count == 1)
        {
            pendingInstalledOnlyResourceOverwriteResult = await Task.Run(delegate
            {
                return viewModel.OverwritePendingInstalledOnlyPackagesResources(list, CancellationToken.None, null);
            }).Logging("treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick");
        }
        else
        {
            CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
            int processedCount = 0;
            int total = list.Count;
            Task<PendingInstalledOnlyResourceOverwriteResult> task = Task.Run(delegate
            {
                return viewModel.OverwritePendingInstalledOnlyPackagesResources(list, cancellationTokenSource.Token, delegate
                {
                    processedCount++;
                });
            }, cancellationTokenSource.Token).Logging("treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick");
            ProgressDialog.Execute(this, BeMusicSeeker.Properties.Resources.Install_to_estimation, "", delegate
            {
                while (task.Status != TaskStatus.RanToCompletion)
                {
                    try
                    {
                        ProgressDialog.Current.ReportWithCancellationCheck(100 * processedCount / total, "[{0}/{1}] {2}", Math.Min(processedCount + 1, total), total, list[Math.Min(processedCount, total - 1)].path ?? "(null)");
                    }
                    catch
                    {
                        cancellationTokenSource.Cancel();
                        while (task.Status != TaskStatus.RanToCompletion)
                        {
                            Thread.Sleep(100);
                        }
                        break;
                    }
                    Thread.Sleep(100);
                }
            }, new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false));
            pendingInstalledOnlyResourceOverwriteResult = await task;
        }
        if (pendingInstalledOnlyResourceOverwriteResult != null)
        {
            MessageBox.Show(Window.GetWindow(this), string.Format(BeMusicSeeker.Properties.Resources.Warn_overwrite_pending_installed_only_packages_summary, pendingInstalledOnlyResourceOverwriteResult.Requested, pendingInstalledOnlyResourceOverwriteResult.Processed, pendingInstalledOnlyResourceOverwriteResult.SucceededInstall, pendingInstalledOnlyResourceOverwriteResult.SucceededCleanupOnly, pendingInstalledOnlyResourceOverwriteResult.SkippedNotPending, pendingInstalledOnlyResourceOverwriteResult.SkippedMissingInstlDst, pendingInstalledOnlyResourceOverwriteResult.SkippedMultiDestination, pendingInstalledOnlyResourceOverwriteResult.SkippedNoComponentTarget, pendingInstalledOnlyResourceOverwriteResult.Failed, pendingInstalledOnlyResourceOverwriteResult.Canceled), BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
    }

    private void treeViewInstallPackageContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: BMSPackage dataContext } } }))
        {
            return;
        }
        if (Directory.Exists(dataContext.path))
        {
            try
            {
                Process.Start("EXPLORER.EXE", "\"" + dataContext.path + "\"");
                return;
            }
            catch
            {
                return;
            }
        }
        if (!File.Exists(dataContext.path))
        {
            return;
        }
        try
        {
            Process.Start("EXPLORER.EXE", "/select,\"" + dataContext.path + "\"");
        }
        catch
        {
        }
    }

    /// <summary>
    /// インストール関連ツリーのコンテキストメニュー「このフォルダの履歴を消去」実行時の処理。
    /// 「インストール保留中」などのリストから対象のパッケージ (BMSPackage) を一つ取り除きます。
    /// </summary>
    private async void treeViewInstallPackageContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        BMSPackage pkg = placementTarget.DataContext as BMSPackage;
        if (pkg == null)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_pendings + Environment.NewLine + Environment.NewLine + ((pkg.BMSFiles.Count > 1) ? pkg.BMSFiles[0].title : pkg.BMSFiles[0].Title), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuClearFolderClick");
        await Task.Run(delegate
        {
            viewModel.RemoveBMSPackagesPending(new BMSPackage[1] { pkg });
        }).Logging("treeViewInstallPackageContextMenuClearFolderClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("treeViewInstallPackageContextMenuClearFolderClick");
        }
    }

    private async void treeViewInstalledFolderContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        BMSPackage pkg = placementTarget.DataContext as BMSPackage;
        if (pkg == null)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        SelectNextSiblingOrRoot(newlyInstalledTreeViewItem, pkg, "treeViewInstalledFolderContextMenuClearFolderClick");
        await Task.Run(delegate
        {
            viewModel.RemoveBMSPackagesInstalled(new BMSPackage[1] { pkg });
        }).Logging("treeViewInstalledFolderContextMenuClearFolderClick");
        if (newlyInstalledTreeViewItem.IsSelected && newlyInstalledTreeViewItem.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter);
            }).Logging("treeViewInstalledFolderContextMenuClearFolderClick");
        }
    }

    private void treeViewInstallPackageContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        BMSPackage pkg = placementTarget.DataContext as BMSPackage;
        if (pkg == null)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            Task.Run(delegate
            {
                viewModel.RemoveInstallDestination(new BMSPackage[1] { pkg });
            }).Logging("treeViewInstallPackageContextMenuRemoveInstallDestinationClick");
        }
    }

    private async void treeViewInstallPackageContextMenuForceInstallClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        BMSPackage pkg = placementTarget.DataContext as BMSPackage;
        if (pkg == null)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuForceInstallClick");
        await Task.Run(delegate
        {
            viewModel.ForceInstallBMSFiles(new BMSPackage[1] { pkg });
        }).Logging("treeViewInstallPackageContextMenuForceInstallClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("treeViewInstallPackageContextMenuForceInstallClick");
        }
    }

    private async void treeViewInstallPackageContextMenuManualInstallClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        BMSPackage pkg = placementTarget.DataContext as BMSPackage;
        if (pkg == null)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || (Settings.Default.ShowDiffBMSInstallConfirmMsg && MessageBox.Show(Window.GetWindow(this), GetManualInstallConfirmationMessage(), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) != MessageBoxResult.OK))
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuManualInstallClick");
        await Task.Run(delegate
        {
            viewModel.ManualInstallBMSFiles(new BMSPackage[1] { pkg });
        }).Logging("treeViewInstallPackageContextMenuManualInstallClick");
        if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("treeViewInstallPackageContextMenuManualInstallClick");
        }
    }

    private void treeViewInstallPackageContextMenuSearchInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        BMSPackage pkg = placementTarget.DataContext as BMSPackage;
        if (pkg == null)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            Task.Run(delegate
            {
                viewModel.SearchInstallationDirectoryBMSFiles(new BMSPackage[1] { pkg });
            }).Logging("treeViewInstallPackageContextMenuSearchInstallationDirectoryClick");
        }
    }

    private void treeViewInstallPackageContextMenuSearchMergeDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        BMSPackage pkg = placementTarget.DataContext as BMSPackage;
        if (pkg == null || !ConfirmMergeDestinationSearch())
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            Task.Run(delegate
            {
                viewModel.SearchMergeDestinationBMSFiles(new BMSPackage[1] { pkg });
            }).Logging("treeViewInstallPackageContextMenuSearchMergeDestinationClick");
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
        DuplicateGroup duplicateGroup = treeViewItem.DataContext as DuplicateGroup;
        if (duplicateGroup == null)
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
            List<string> list = duplicateGroup.Folders.Except(new string[1] { dataContext }, StringComparer.OrdinalIgnoreCase)
                .ToList();
            menuItem.IsEnabled = list.Count > 0;
            if (menuItem.IsEnabled)
            {
                menuItem.ItemsSource = list;
            }
        }
    }

    private void treeViewDuplicateFolderContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: string dataContext } } }) || !Directory.Exists(dataContext))
        {
            return;
        }
        try
        {
            Process.Start("EXPLORER.EXE", "\"" + dataContext + "\"");
        }
        catch
        {
        }
    }

    private void treeViewDuplicateFolderContextMenuItemMergeIntoTargetClick(object sender, RoutedEventArgs e)
    {
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
        DuplicateGroup duplicateGroup = groupTreeItem?.DataContext as DuplicateGroup;
        ExecuteDuplicateFolderMerge(srcPath, dstPath, duplicateGroup);
    }

    /// <summary>
    /// 重複フォルダのマージ処理を実行する共通メソッド。
    /// 確認ダイアログ → マージ実行 → マージ後のグループ自動選択を行う。
    /// </summary>
    private void ExecuteDuplicateFolderMerge(string srcPath, string dstPath, DuplicateGroup duplicateGroup)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath))
        {
            return;
        }

        // 確認ダイアログ
        if (MessageBox.Show(Window.GetWindow(this),
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_folder + Environment.NewLine + Environment.NewLine +
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_target + ": " + srcPath + Environment.NewLine +
            BeMusicSeeker.Properties.Resources.Msg_merge_bms_destination + ": " + dstPath,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }

        // マージ後に自動選択するグループのHeaderをキャッシュ
        _pendingDuplicateGroupHeader = null;
        if (duplicateGroup != null && viewModel.BMSFilesDuplicated != null)
        {
            int folderCount = duplicateGroup.Folders.Count;
            if (folderCount == 2)
            {
                // フォルダが2つの場合: マージでグループ消滅 → 次のグループを選択
                int currentIndex = viewModel.BMSFilesDuplicated.IndexOf(duplicateGroup);
                if (currentIndex >= 0 && currentIndex + 1 < viewModel.BMSFilesDuplicated.Count)
                {
                    _pendingDuplicateGroupHeader = viewModel.BMSFilesDuplicated[currentIndex + 1].Header;
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
            viewModel.MergeBMSDirectory(srcPath, dstPath);

            // マージ完了ログ（将来のステータスバー通知に備える）
            NLogWrapper.FileLogger?.Info(string.Format(
                BeMusicSeeker.Properties.Resources.Msg_merge_bms_completed, srcFolderName, dstFolderName));

        }).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                return;
            }
            // マージ後にBMSFilesDuplicatedの更新を待ってからツリーで自動選択を試みる
            WaitForDuplicateListUpdateAndSelect(_pendingDuplicateGroupHeader, viewModel);
        }, TaskScheduler.FromCurrentSynchronizationContext()).Logging("ExecuteDuplicateFolderMerge");
    }

    /// <summary>
    /// BMSFilesDuplicated更新タイミングの競合を吸収しつつ、該当グループを自動選択する。
    /// 基本はPropertyChanged契機で選択し、通知不達時のみ遅延フォールバックを1回試行する。
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
        Action completeSelection = () =>
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
        };
        async Task AttemptAutoSelectAsync(string trigger)
        {
            try
            {
                if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                {
                    return;
                }
                string lastReason = string.Empty;
                DispatcherPriority[] priorities = new DispatcherPriority[3]
                {
                    DispatcherPriority.Loaded,
                    DispatcherPriority.Render,
                    DispatcherPriority.ContextIdle
                };
                for (int retry = 0; retry < priorities.Length; retry++)
                {
                    await Dispatcher.Yield(priorities[retry]);
                    if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                    {
                        return;
                    }
                    if (TrySelectDuplicateGroupByHeader(header, viewModel, out lastReason))
                    {
                        NLogWrapper.FileLogger?.Info("duplicate_group_autoselect success trigger=" + trigger + " retry=" + retry + " header=" + header + " request=" + requestVersion);
                        completeSelection();
                        return;
                    }
                }
                NLogWrapper.FileLogger?.Warn("duplicate_group_autoselect pending trigger=" + trigger + " header=" + header + " request=" + requestVersion + " reason=" + lastReason);
            }
            catch (Exception ex)
            {
                NLogWrapper.FileLogger?.Warn("duplicate_group_autoselect failed trigger=" + trigger + " header=" + header + " request=" + requestVersion + " message=" + ex.Message);
            }
        }
        handler = (s, e) =>
        {
            if (e.PropertyName != nameof(viewModel.BMSFilesDuplicated))
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
                    _ = AttemptAutoSelectAsync("property_changed");
                }));
            }
        };
        viewModel.PropertyChanged += handler;
        _duplicateGroupAutoSelectHandler = handler;
        _duplicateGroupAutoSelectHandlerOwner = viewModel;
        NLogWrapper.FileLogger?.Info("duplicate_group_autoselect queued header=" + header + " request=" + requestVersion + " mode=property_changed");
        Task.Run(async delegate
        {
            await Task.Delay(1500).ConfigureAwait(continueOnCapturedContext: false);
            if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
            {
                return;
            }
            await Dispatcher.InvokeAsync(async delegate
            {
                if (completed || requestVersion != _duplicateGroupAutoSelectRequestVersion)
                {
                    return;
                }
                NLogWrapper.FileLogger?.Info("duplicate_group_autoselect trigger=timeout_fallback header=" + header + " request=" + requestVersion);
                await AttemptAutoSelectAsync("timeout_fallback");
            }, DispatcherPriority.Background);
        });
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

        var duplicatedList = viewModel.BMSFilesDuplicated;
        if (duplicatedList == null)
        {
            failReason = "duplicated_list_null";
            return false;
        }

        // データリストからターゲットのインデックスを検索
        int targetIndex = -1;
        for (int i = 0; i < duplicatedList.Count; i++)
        {
            if (duplicatedList[i].Header == header)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex < 0)
        {
            failReason = "group_not_found";
            return false;
        }

        // 親TreeViewItemが展開されていることを確認
        treeViewItemFullScanCheck.IsExpanded = true;
        duplicateRootItem.IsExpanded = true;
        duplicateRootItem.BringIntoView();
        duplicateRootItem.UpdateLayout();

        // 仮想化パネルのBringIndexIntoViewPublicでコンテナ生成を強制
        var panel = WPFUtil.FindVisualChild<VirtualizingStackPanel>(duplicateRootItem);
        if (panel != null)
        {
            try
            {
                panel.BringIndexIntoViewPublic(targetIndex);
                duplicateRootItem.UpdateLayout();
            }
            catch (ArgumentOutOfRangeException)
            {
                failReason = "bring_index_out_of_range";
                return false;
            }
        }

        // コンテナを取得して選択
        TreeViewItem targetItem = duplicateRootItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) as TreeViewItem;
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

        TreeViewItem folderItem = sender as TreeViewItem;
        if (folderItem == null)
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
        DuplicateGroup duplicateGroup = groupItem?.DataContext as DuplicateGroup;
        if (duplicateGroup == null)
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
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }

        // フォルダパスに一致するBMSFileをハッシュでグループ化
        var filesInFolder = duplicateGroup.Files
            .Where(f => f.path.StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filesInFolder.Count == 0)
        {
            return;
        }

        // ハッシュでグループ化し、各グループで削除対象を決定
        var deletionList = new List<BMSFile>();
        foreach (var hashGroup in filesInFolder.GroupBy(f => f.hash, StringComparer.OrdinalIgnoreCase))
        {
            var grouped = hashGroup.ToList();
            if (grouped.Count <= 1)
            {
                continue;
            }

            // 保持対象: 更新日時が最も古い → ファイル名が最も短い
            BMSFile keeper = grouped
                .OrderBy(f =>
                {
                    try { return File.GetLastWriteTime(f.path); }
                    catch { return DateTime.MaxValue; }
                })
                .ThenBy(f => Path.GetFileName(f.path).Length)
                .First();

            deletionList.AddRange(grouped.Where(f => f != keeper));
        }

        if (deletionList.Count == 0)
        {
            return;
        }

        // 確認ダイアログ
        if (MessageBox.Show(Window.GetWindow(this),
            string.Format(BeMusicSeeker.Properties.Resources.Msg_cleanup_duplicate_hash, deletionList.Count),
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }

        // 処理後の自動選択用: フォルダ1つなのでグループは消滅 → 次のグループを自動選択
        _pendingDuplicateGroupHeader = null;
        if (viewModel.BMSFilesDuplicated != null)
        {
            int currentIndex = viewModel.BMSFilesDuplicated.IndexOf(duplicateGroup);
            if (currentIndex >= 0 && currentIndex + 1 < viewModel.BMSFilesDuplicated.Count)
            {
                _pendingDuplicateGroupHeader = viewModel.BMSFilesDuplicated[currentIndex + 1].Header;
            }
        }

        Task.Run(delegate
        {
            viewModel.RemoveBMSFiles(deletionList);

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
        if (new Task[4] { getSongInfoCacheTask, changeSubmenuOpenVideoTask, changeSubmenuOpenDocumentTask, changeSubmenuOpenSearchLinkTask }.Where((Task t) => t != null).Any((Task t) => !t.IsCompleted) && dataGridContextMenuTaskTokenSource != null)
        {
            dataGridContextMenuTaskTokenSource.Cancel();
            NLogWrapper.DebuggerLogger?.Trace("Cancel data grid context menu async tasks");
        }
    }

    private void initContextMenuTasks()
    {
        songInfoCache = null;
        dataGridContextMenuTaskTokenSource = new CancellationTokenSource();
        getSongInfoCacheTask = null;
        changeSubmenuOpenVideoTask = null;
        changeSubmenuOpenDocumentTask = null;
        changeSubmenuOpenSearchLinkTask = null;
    }

    private void dataGridContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out DataGridRow placementTarget, out object row))
        {
            NLogWrapper.FileLogger?.Info("playlist_context_menu rowResolve=False sourceType=" + sender?.GetType().FullName);
            return;
        }
        BMSFile bmsFile = GridRowResolver.GetOperationBmsFile(row);
        bool isPlaylistRow = GridRowResolver.IsPlaylistRow(row);
        Uri rowUrl = GridRowResolver.GetUrl(row);
        Uri rowUrlDiff = GridRowResolver.GetUrlDiff(row);
        if (bmsFile == null && !isPlaylistRow)
        {
            NLogWrapper.FileLogger?.Info("playlist_context_menu rowResolve=True but unsupported rowType=" + row?.GetType().FullName);
            return;
        }
        List<BMSFile> list = GetSelectedGridOperationFiles();
        if (!(base.DataContext is MainWindowViewModel mainWindowViewModel))
        {
            return;
        }
        string rowHash = bmsFile?.hash ?? GridRowResolver.GetHash(row);
        if (songInfoCache == null || songInfoCache.md5 != rowHash)
        {
            calcelAllContextMenuTasks();
            initContextMenuTasks();
        }
        MenuItem menuItem = null;
        MenuItem menuItem2 = null;
        MenuItem menuItem3 = null;
        MenuItem menuItem4 = null;
        MenuItem menuItem5 = null;
        MenuItem menuItem6 = null;
        MenuItem menuItem7 = null;
        MenuItem menuItem8 = null;
        MenuItem menuItem9 = null;
        MenuItem menuItem10 = null;
        MenuItem menuItem11 = null;
        MenuItem menuItem12 = null;
        MenuItem menuItem13 = null;
        MenuItem menuItem14 = null;
        MenuItem menuItem15 = null;
        MenuItem menuItem16 = null;
        MenuItem menuItem17 = null;
        MenuItem menuItemOpenInstallDestination = null;
        MenuItem menuItemOpenDocument = null;
        MenuItem menuItem18 = null;
        Separator separator = null;
        MenuItem menuItem19 = null;
        Separator separator2 = null;
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "dataGridContextMenuItemOpenURL":
                    menuItem = item as MenuItem;
                    break;
                case "dataGridContextMenuItemOpenURLdiff":
                    menuItem2 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemOpenExplorer":
                    menuItem3 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemOpenInstallDestination":
                    menuItemOpenInstallDestination = item as MenuItem;
                    break;
                case "dataGridContextMenuItemOpenBMSFile":
                    menuItem4 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemRegisterScore":
                    menuItem18 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemOpenDocument":
                    menuItemOpenDocument = item as MenuItem;
                    break;
                case "dataGridContextMenuItemOpenVideo":
                    menuItem5 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemSearchLink":
                    menuItem6 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemUpdateRankingDataClick":
                    menuItem7 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemInstall":
                    menuItem8 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemFixInstall":
                    menuItem9 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemFullScanCheck":
                    menuItem10 = item as MenuItem;
                    foreach (Control item2 in (IEnumerable)menuItem10.Items)
                    {
                        string name = item2.Name;
                        if (!(name == "dataGridContextMenuItemIgnoreFileScanCheck"))
                        {
                            if (name == "dataGridContextMenuItemNotIgnoreFileScanCheck")
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
                case "dataGridContextMenuItemMoveFile":
                    menuItem13 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemDeleteEntry":
                    menuItem14 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemDeleteFile":
                    menuItem15 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemAutoRenameFolder":
                    menuItem16 = item as MenuItem;
                    break;
                case "dataGridContextMenuItemFixEncoding":
                    menuItem17 = item as MenuItem;
                    break;
                case "dataGridContextMenuSeparatorForFolderview":
                    separator = item as Separator;
                    break;
                case "dataGridContextMenuItemConvertToAudioFile":
                    menuItem19 = item as MenuItem;
                    break;
                case "dataGridContextMenuSeparatorForConvert":
                    separator2 = item as Separator;
                    break;
            }
        }
        if (isPlaylistRow)
        {
            if (menuItem != null)
            {
                if (rowUrl != null && rowUrl.IsAbsoluteUri)
                {
                    menuItem.Visibility = Visibility.Visible;
                    menuItem.IsEnabled = true;
                }
                else
                {
                    menuItem.Visibility = Visibility.Visible;
                    menuItem.IsEnabled = false;
                }
            }
            if (menuItem2 != null)
            {
                if (rowUrlDiff != null && rowUrlDiff.IsAbsoluteUri)
                {
                    menuItem2.Visibility = Visibility.Visible;
                    menuItem2.IsEnabled = true;
                }
                else
                {
                    menuItem2.Visibility = Visibility.Visible;
                    menuItem2.IsEnabled = false;
                }
            }
            if (menuItem5 != null)
            {
                menuItem5.Visibility = Visibility.Visible;
                menuItem5.IsEnabled = true;
            }
            if (menuItem6 != null)
            {
                menuItem6.Visibility = Visibility.Visible;
                menuItem6.IsEnabled = true;
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
            if (menuItem5 != null)
            {
                menuItem5.Visibility = Visibility.Collapsed;
            }
            if (menuItem6 != null)
            {
                menuItem6.Visibility = Visibility.Collapsed;
            }
        }
        if (menuItem3 != null && menuItem4 != null && menuItemOpenDocument != null && changeSubmenuOpenDocumentTask == null)
        {
            menuItemOpenDocument.IsEnabled = false;
            if (!string.IsNullOrWhiteSpace(bmsFile.path) && File.Exists(bmsFile.path))
            {
                menuItem3.IsEnabled = true;
                menuItem4.IsEnabled = true;
                menuItemOpenDocument.Visibility = Visibility.Visible;
                changeSubmenuOpenDocumentTask = Task.Run(delegate
                {
                    NLogWrapper.DebuggerLogger?.Trace("Test starts: changeSubmenuOpenDocumentTask");
                    CancellationToken token = dataGridContextMenuTaskTokenSource.Token;
                    string directoryNameSimple = DirectoryExt.GetDirectoryNameSimple(bmsFile.path);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                    try
                    {
                        List<string> list2 = Directory.EnumerateFiles(directoryNameSimple, "*.txt").Concat(Directory.EnumerateFiles(directoryNameSimple, "*.htm?")).ToList();
                        if (list2.Count > 0)
                        {
                            base.Dispatcher.BeginInvoke((Action)delegate
                            {
                                if (!token.IsCancellationRequested)
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
                            if (!token.IsCancellationRequested)
                            {
                                menuItemOpenDocument.Visibility = Visibility.Collapsed;
                            }
                        });
                    }
                }, dataGridContextMenuTaskTokenSource.Token).Logging("dataGridContextMenuOpened");
            }
            else
            {
                menuItem3.IsEnabled = false;
                menuItem4.IsEnabled = false;
                menuItemOpenDocument.Visibility = Visibility.Collapsed;
            }
        }
        bool flag = false;
        if (list.Count > 1)
        {
            menuItem18.Header = BeMusicSeeker.Properties.Resources.Register_chart_with_viewer;
            flag = (menuItem18.IsEnabled = list.Any((BMSFile f) => !string.IsNullOrWhiteSpace(f.path) && File.Exists(f.path)));
        }
        else
        {
            menuItem18.Header = BeMusicSeeker.Properties.Resources.Open_chart_viewer;
            flag = !string.IsNullOrWhiteSpace(bmsFile.path) && File.Exists(bmsFile.path);
            menuItem18.IsEnabled = true;
        }
        menuItem19.IsEnabled = flag;
        if (menuItem7 != null)
        {
            if (mainWindowViewModel.LR2ID == 0)
            {
                menuItem7.IsEnabled = false;
            }
            else
            {
                menuItem7.IsEnabled = true;
            }
        }
        MenuItem menuItemDeleteInstallPackages = contextMenu.Items.OfType<MenuItem>().FirstOrDefault((MenuItem item) => item.Name == "dataGridContextMenuItemDeleteInstallPackages");
        bool isPendingSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallPending;
        bool isInstalledSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallInstalled;
        bool isInstallListSelected = isPendingSelected || isInstalledSelected;
        bool isPlaylistSelected = _currentTreeSelectionSection == TreeSelectionSection.Playlist;
        bool isPlaylistContext = isPlaylistSelected || isPlaylistRow;
        bool isNotOwnedPlaylistRow = isPlaylistRow && (bmsFile == null || string.IsNullOrWhiteSpace(bmsFile.path));
        NLogWrapper.FileLogger?.Info("playlist_context_menu rowType=" + row?.GetType().FullName + " isPlaylistRow=" + isPlaylistRow + " isPlaylistContext=" + isPlaylistContext + " isNotOwned=" + isNotOwnedPlaylistRow + " section=" + _currentTreeSelectionSection + " path=" + (bmsFile?.path ?? string.Empty));
        if (menuItemOpenInstallDestination != null)
        {
            bool canOpenInstallDestination = isPendingSelected && bmsFile != null && !isPlaylistRow;
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
            bool isFullScanMenuVisible = !isPlaylistContext;
            menuItem10.Visibility = ((!isFullScanMenuVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem10.IsEnabled = isFullScanMenuVisible;
        }
        if (menuItem13 != null)
        {
            bool canMoveSelectedFiles = !isPendingSelected && !isPlaylistContext;
            menuItem13.Visibility = ((!canMoveSelectedFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem13.IsEnabled = canMoveSelectedFiles && list.Any((BMSFile f) => !string.IsNullOrWhiteSpace(f.path) && File.Exists(f.path));
        }
        if (menuItem14 != null)
        {
            menuItem14.Visibility = ((!isPlaylistContext) ? Visibility.Collapsed : Visibility.Visible);
            menuItem14.IsEnabled = isPlaylistContext;
        }
        if (menuItem15 != null)
        {
            bool canDeleteFiles = !isPlaylistContext;
            menuItem15.Visibility = ((!canDeleteFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem15.IsEnabled = canDeleteFiles;
            Ribbit.Logging.NLogWrapper.FileLogger?.Info(
                $"[ContextMenu] DeleteFile Header='{menuItem15.Header}', HasItems={menuItem15.HasItems}, Items.Count={menuItem15.Items.Count}");

            // 診断ログ: サブアイテム消失の調査用
            if (menuItem15.Items.Count == 0)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn(
                    "dataGridContextMenuItemDeleteFile has lost its child items. " +
                    "Culture=" + System.Threading.Thread.CurrentThread.CurrentUICulture.Name +
                    " Lang=" + Settings.Default.Lang +
                    " Header=" + menuItem15.Header);
            }
        }
        if (separator != null)
        {
            bool isFolderViewSeparatorVisible = !isPlaylistContext && !isPendingSelected;
            separator.Visibility = ((!isFolderViewSeparatorVisible) ? Visibility.Collapsed : Visibility.Visible);
            separator.IsEnabled = isFolderViewSeparatorVisible;
        }
        if (menuItem16 != null)
        {
            bool canAutoRenameFolders = !isPlaylistContext && !isPendingSelected;
            menuItem16.Visibility = ((!canAutoRenameFolders) ? Visibility.Collapsed : Visibility.Visible);
            menuItem16.IsEnabled = canAutoRenameFolders;
        }
        if (menuItem17 != null)
        {
            bool canFixEncoding = !isPlaylistContext;
            menuItem17.Visibility = ((!canFixEncoding) ? Visibility.Collapsed : Visibility.Visible);
            menuItem17.IsEnabled = canFixEncoding;
        }
        if (menuItem9 != null)
        {
            bool isInstalledLocationFixVisible = _currentTreeSelectionSection == TreeSelectionSection.FullScanCheck && !isPlaylistContext;
            menuItem9.Visibility = ((!isInstalledLocationFixVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem9.IsEnabled = isInstalledLocationFixVisible;
        }
        if (menuItem11 != null)
        {
            bool isSelected = treeViewItemFullScanCheck.IsSelected;
            menuItem11.Visibility = ((!isSelected) ? Visibility.Collapsed : Visibility.Visible);
            menuItem11.IsEnabled = isSelected;
        }
        if (menuItem12 != null)
        {
            bool isSelected2 = treeViewItemFullScanCheckIgnored.IsSelected;
            menuItem12.Visibility = ((!isSelected2) ? Visibility.Collapsed : Visibility.Visible);
            menuItem12.IsEnabled = isSelected2;
        }
        if (menuItem19 != null && separator2 != null)
        {
            bool canConvertToAudio = !isPendingSelected && !isPlaylistContext;
            Separator convertSeparator = separator2;
            Visibility visibility = (menuItem19.Visibility = ((!canConvertToAudio) ? Visibility.Collapsed : Visibility.Visible));
            convertSeparator.Visibility = visibility;
            Separator convertSeparator2 = separator2;
            bool isEnabled = (menuItem19.IsEnabled = canConvertToAudio);
            convertSeparator2.IsEnabled = isEnabled;
        }
        if (menuItemDeleteInstallPackages != null)
        {
            menuItemDeleteInstallPackages.Visibility = (isInstallListSelected ? Visibility.Visible : Visibility.Collapsed);
            menuItemDeleteInstallPackages.IsEnabled = isInstallListSelected && list.Count > 0;
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

    private void dataGridContextMenuPlaylistMissingOpened(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(sender, out ContextMenu contextMenu, out DataGridRow placementTarget, out object row))
        {
            NLogWrapper.FileLogger?.Info("playlist_missing_context_menu rowResolve=False sourceType=" + sender?.GetType().FullName);
            return;
        }
        BMSTableEntry entry = GridRowResolver.GetPlaylistEntry(row);
        Uri rowUrl = GridRowResolver.GetUrl(row);
        Uri rowUrlDiff = GridRowResolver.GetUrlDiff(row);
        bool canOpenLr2Ir = !string.IsNullOrWhiteSpace(GridRowResolver.GetHash(row)) || !string.IsNullOrWhiteSpace(GridRowResolver.GetLr2BmsId(row));
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
                case "dataGridContextMenuItemOpenLR2IR":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = canOpenLr2Ir;
                    break;
                case "dataGridContextMenuItemOpenURL":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = rowUrl != null && rowUrl.IsAbsoluteUri;
                    break;
                case "dataGridContextMenuItemOpenURLdiff":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = rowUrlDiff != null && rowUrlDiff.IsAbsoluteUri;
                    break;
                case "dataGridContextMenuItemOpenVideo":
                case "dataGridContextMenuItemSearchLink":
                case "dataGridContextMenuItemDeleteEntry":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = entry != null;
                    break;
            }
        }
        NLogWrapper.FileLogger?.Info("playlist_missing_context_menu rowType=" + row?.GetType().FullName + " entryParent=" + entry?.parent?.name + " url=" + (rowUrl != null) + " urlDiff=" + (rowUrlDiff != null) + " canOpenLr2Ir=" + canOpenLr2Ir);
    }

    private void dataGridContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow { Item: var row } } }))
        {
            return;
        }
        string path = GridRowResolver.GetOperationBmsFile(row)?.path;
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            Process.Start("EXPLORER.EXE", "/select,\"" + path + "\"");
        }
        catch
        {
        }
    }

    private bool TryResolveInstallDestination(BMSFile bmsFile, out string installDir, out string reason)
    {
        installDir = null;
        reason = null;
        if (bmsFile == null)
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(bmsFile.instl_dst))
        {
            if (Directory.Exists(bmsFile.instl_dst))
            {
                installDir = bmsFile.instl_dst;
                return true;
            }
            reason = string.Format(BeMusicSeeker.Properties.Resources.Msg_open_install_destination_not_found, bmsFile.instl_dst);
            return false;
        }
        MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
        if (mainWindowViewModel != null && mainWindowViewModel.TryGetInstalledDirectoryByHash(bmsFile.hash, out var installDir2))
        {
            installDir = installDir2;
            return true;
        }
        reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
        return false;
    }

    private bool TryResolveInstallDestination(BMSPackage pkg, out string installDir, out string reason)
    {
        installDir = null;
        reason = null;
        if (pkg == null)
        {
            reason = BeMusicSeeker.Properties.Resources.Msg_open_install_destination_missing;
            return false;
        }
        foreach (BMSFile item in pkg.BMSFiles.Where((BMSFile f) => f != null))
        {
            if (TryResolveInstallDestination(item, out installDir, out reason))
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
        try
        {
            Process.Start("EXPLORER.EXE", "\"" + installDir + "\"");
        }
        catch
        {
        }
    }

    private void dataGridContextMenuItemOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!(base.DataContext is MainWindowViewModel) || _currentTreeSelectionSection != TreeSelectionSection.InstallPending)
        {
            return;
        }
        List<BMSFile> list = GetSelectedGridRealFiles();
        if (list.Count == 0)
        {
            return;
        }
        if (list.Count > 1)
        {
            MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_open_install_destination_multiple_selected, BeMusicSeeker.Properties.Resources.Information, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
        }
        if (!TryResolveInstallDestination(list[0], out var installDir, out var reason))
        {
            MessageBox.Show(Window.GetWindow(this), reason, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        OpenInstallDestinationInExplorer(installDir);
    }

    private void treeViewInstallPackageContextMenuOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: BMSPackage dataContext } } }))
        {
            return;
        }
        if (!TryResolveInstallDestination(dataContext, out var installDir, out var reason))
        {
            MessageBox.Show(Window.GetWindow(this), reason, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        OpenInstallDestinationInExplorer(installDir);
    }

    private string _getLR2IRrankingPageURL(string md5_or_bmsid)
    {
        if (Regex.IsMatch(md5_or_bmsid, "^[A-F0-9]{32}$", RegexOptions.IgnoreCase))
        {
            return "http://www.dream-pro.info/~lavalse/LR2IR/search.cgi?mode=ranking&bmsmd5=" + md5_or_bmsid;
        }
        if (Regex.IsMatch(md5_or_bmsid, "^\\d+$"))
        {
            return "http://www.dream-pro.info/~lavalse/LR2IR/search.cgi?mode=ranking&bmsid=" + md5_or_bmsid;
        }
        return null;
    }

    private void dataGridContextMenuItemOpenBMSFileClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow placementTarget } }))
        {
            return;
        }
        BMSFile bMSFile = GridRowResolver.GetOperationBmsFile(placementTarget.Item);
        if (string.IsNullOrWhiteSpace(bMSFile?.path))
        {
            return;
        }
        string path = bMSFile.path;
        if (!File.Exists(path))
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

    private void dataGridContextMenuItemOpenLR2IRClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow placementTarget } }))
        {
            return;
        }
        object row = placementTarget.Item;
        string rowHash = GridRowResolver.GetHash(row);
        string text;
        if (!string.IsNullOrWhiteSpace(rowHash))
        {
            text = _getLR2IRrankingPageURL(rowHash);
        }
        else
        {
            string lr2BmsId = GridRowResolver.GetLr2BmsId(row);
            if (string.IsNullOrWhiteSpace(lr2BmsId))
            {
                return;
            }
            text = _getLR2IRrankingPageURL(lr2BmsId);
        }
        if (text != null)
        {
            Process.Start(text);
        }
    }

    private void dataGridContextMenuItemOpenURLClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow { Item: var item } } })
        {
            Uri url = GridRowResolver.GetUrl(item);
            if (url != null && url.IsAbsoluteUri)
            {
                Process.Start(url.ToString());
            }
        }
    }

    private void dataGridContextMenuItemOpenURLdiffClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow { Item: var item } } })
        {
            Uri urlDiff = GridRowResolver.GetUrlDiff(item);
            if (urlDiff != null && urlDiff.IsAbsoluteUri)
            {
                Process.Start(urlDiff.ToString());
            }
        }
    }

    private void dataGridContextMenuItemOpenDocumentFileClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { DataContext: string dataContext } && File.Exists(dataContext))
        {
            Process.Start(dataContext);
        }
    }

    private void dataGridContextMenuOpenVideoSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!(sender is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow placementTarget } } menuItem))
        {
            return;
        }
        object row = placementTarget.DataContext;
        if (!GridRowResolver.IsPlaylistRow(row))
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        e.Handled = true;
        if (changeSubmenuOpenVideoTask != null)
        {
            return;
        }
        MenuItem menuItemOpenVideoSubmenuStatus = null;
        MenuItem menuItemOpenVideoSubmenuYouTube = null;
        MenuItem menuItemOpenVideoSubmenuNiconico = null;
        foreach (Control item in (IEnumerable)menuItem.Items)
        {
            switch (item.Name)
            {
                case "dataGridContextMenuItemOpenVideoSubmenuStatus":
                    menuItemOpenVideoSubmenuStatus = item as MenuItem;
                    break;
                case "dataGridContextMenuItemOpenVideoSubmenuYouTube":
                    menuItemOpenVideoSubmenuYouTube = item as MenuItem;
                    break;
                case "dataGridContextMenuItemOpenVideoSubmenuNiconico":
                    menuItemOpenVideoSubmenuNiconico = item as MenuItem;
                    break;
            }
        }
        if (menuItemOpenVideoSubmenuStatus != null)
        {
            menuItemOpenVideoSubmenuStatus.IsEnabled = false;
            menuItemOpenVideoSubmenuStatus.Visibility = Visibility.Visible;
            menuItemOpenVideoSubmenuStatus.Header = BeMusicSeeker.Properties.Resources.Now_searching;
        }
        if (menuItemOpenVideoSubmenuYouTube != null)
        {
            menuItemOpenVideoSubmenuYouTube.IsEnabled = false;
            menuItemOpenVideoSubmenuYouTube.Visibility = Visibility.Collapsed;
        }
        if (menuItemOpenVideoSubmenuNiconico != null)
        {
            menuItemOpenVideoSubmenuNiconico.IsEnabled = false;
            menuItemOpenVideoSubmenuNiconico.Visibility = Visibility.Collapsed;
        }
        changeSubmenuOpenVideoTask = Task.Run(delegate
        {
            NLogWrapper.DebuggerLogger?.Trace("Test starts: changeSubmenuOpenVideoTask");
            CancellationToken token = dataGridContextMenuTaskTokenSource.Token;
            if (getSongInfoCacheTask == null)
            {
                getSongInfoCacheTask = Task.Run(delegate
                {
                    NLogWrapper.DebuggerLogger?.Trace("Test starts: getSongInfoCacheTask");
                    try
                    {
                        BMSLibrary.IRSongInfo lR2IRSongInfoCache = viewModel.GetLR2IRSongInfoCache(row);
                        if (!token.IsCancellationRequested)
                        {
                            songInfoCache = lR2IRSongInfoCache;
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                }, token);
            }
            getSongInfoCacheTask.Wait();
            if (!token.IsCancellationRequested)
            {
                if (songInfoCache != null)
                {
                    bool found = !string.IsNullOrWhiteSpace(songInfoCache.youtube_url) || !string.IsNullOrWhiteSpace(songInfoCache.niconico_url);
                    if (menuItemOpenVideoSubmenuStatus != null)
                    {
                        base.Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (!token.IsCancellationRequested)
                            {
                                if (found)
                                {
                                    menuItemOpenVideoSubmenuStatus.Visibility = Visibility.Collapsed;
                                }
                                else
                                {
                                    menuItemOpenVideoSubmenuStatus.Header = BeMusicSeeker.Properties.Resources.Unregistered;
                                }
                            }
                        });
                    }
                    if (menuItemOpenVideoSubmenuYouTube != null && !string.IsNullOrWhiteSpace(songInfoCache.youtube_url))
                    {
                        base.Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (!token.IsCancellationRequested)
                            {
                                menuItemOpenVideoSubmenuYouTube.IsEnabled = true;
                                menuItemOpenVideoSubmenuYouTube.Visibility = Visibility.Visible;
                                menuItemOpenVideoSubmenuYouTube.Tag = songInfoCache.youtube_url;
                            }
                        });
                    }
                    if (menuItemOpenVideoSubmenuNiconico != null && !string.IsNullOrWhiteSpace(songInfoCache.niconico_url))
                    {
                        base.Dispatcher.BeginInvoke((Action)delegate
                        {
                            if (!token.IsCancellationRequested)
                            {
                                menuItemOpenVideoSubmenuNiconico.IsEnabled = true;
                                menuItemOpenVideoSubmenuNiconico.Visibility = Visibility.Visible;
                                menuItemOpenVideoSubmenuNiconico.Tag = songInfoCache.niconico_url;
                            }
                        });
                    }
                }
                else if (menuItemOpenVideoSubmenuStatus != null)
                {
                    base.Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (!token.IsCancellationRequested)
                        {
                            menuItemOpenVideoSubmenuStatus.Header = BeMusicSeeker.Properties.Resources.Unregistered;
                        }
                    });
                }
            }
        }, dataGridContextMenuTaskTokenSource.Token).Logging("dataGridContextMenuOpenVideoSubmenuOpened");
    }

    private void dataGridContextMenuItemOpenVideoSubmenuClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(base.DataContext is MainWindowViewModel mainWindowViewModel) || webBrowser == null)
        {
            return;
        }
        try
        {
            Uri uri;
            if (!webBrowser.IsEnabled)
            {
                uri = new Uri(menuItem.Tag.ToString(), UriKind.Absolute);
                Match match = Regex.Match(uri.ToString(), "^http[s]?://www\\.youtube\\.com/v/([^&]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    Process.Start("https://www.youtube.com/watch?v=" + match.Groups[1].Value);
                }
                else
                {
                    Process.Start(uri.ToString());
                }
                return;
            }
            uri = new Uri(menuItem.Tag.ToString(), UriKind.Absolute);
            Match match2;
            if ((match2 = Regex.Match(uri.ToString(), "^http[s]?://www\\.youtube\\.com/v/([^&]+)", RegexOptions.IgnoreCase)).Success)
            {
                webBrowser.Navigating -= forbidNavigating;
                mainWindowViewModel.SetYoutubeToBrowserSource(match2.Groups[1].Value);
            }
            else
            {
                if (!(match2 = Regex.Match(uri.ToString(), "^http[s]?://[^.]+\\.nicovideo\\.jp/watch/(.+)", RegexOptions.IgnoreCase)).Success)
                {
                    throw new InvalidOperationException();
                }
                webBrowser.Navigating -= forbidNavigating;
                mainWindowViewModel.SetNiconicoToBrowserSource(match2.Groups[1].Value);
            }
        }
        catch
        {
            return;
        }
        finally
        {
            e.Handled = true;
        }
        if (!webBrowser.IsEnabled)
        {
            return;
        }
        BMSFile bMSFile = GridRowResolver.GetOperationBmsFile(dataGrid.SelectedItem);
        if (bMSFile == null)
        {
            return;
        }
        gridBMSPlayerControlsTitleForMovie.Text = GridRowResolver.GetDisplayTitle(bMSFile);
        gridBMSPlayerControlsSubtitleForMovie.Text = GridRowResolver.GetDisplaySubtitle(bMSFile);
        gridBMSPlayerControlsArtistForMovie.Text = GridRowResolver.GetDisplayArtist(bMSFile);
    }

    private void songInfoCacheToUrlLists(BMSLibrary.IRSongInfo info, Uri original, Uri diff, out List<Uri> urls, out List<Uri> urls_diff)
    {
        if (songInfoCache != null)
        {
            urls = (from s in songInfoCache.url.Split(' ')
                    where !string.IsNullOrWhiteSpace(s)
                    let normalized = NormalizeDownloadUrlString(s)
                    where !string.IsNullOrWhiteSpace(normalized)
                    select new Uri(normalized, UriKind.Absolute)).ToList();
            urls_diff = (from s in songInfoCache.url_diff.Split(' ')
                         where !string.IsNullOrWhiteSpace(s)
                         let normalized = NormalizeDownloadUrlString(s)
                         where !string.IsNullOrWhiteSpace(normalized)
                         select new Uri(normalized, UriKind.Absolute)).ToList();
        }
        else
        {
            urls = new List<Uri>();
            urls_diff = new List<Uri>();
        }
        if (original != null && original.IsAbsoluteUri)
        {
            urls.Add(NormalizeDownloadUri(original));
        }
        if (diff != null && diff.IsAbsoluteUri)
        {
            urls_diff.Add(NormalizeDownloadUri(diff));
        }
    }

    private void dataGridContextMenuSearchLinkOpened(object sender, RoutedEventArgs e)
    {
        MenuItem menuItem = sender as MenuItem;
        if (menuItem == null || !(menuItem.Parent is ContextMenu { PlacementTarget: DataGridRow placementTarget }))
        {
            return;
        }
        object row = placementTarget.DataContext;
        if (!GridRowResolver.IsPlaylistRow(row))
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        e.Handled = true;
        if (changeSubmenuOpenSearchLinkTask != null)
        {
            return;
        }
        menuItem.Items.Clear();
        MenuItem menuItemStatus = new MenuItem
        {
            Header = BeMusicSeeker.Properties.Resources.Now_searching,
            IsEnabled = false,
            Visibility = Visibility.Visible
        };
        menuItem.Items.Add(menuItemStatus);
        changeSubmenuOpenSearchLinkTask = Task.Run(delegate
        {
            NLogWrapper.DebuggerLogger?.Trace("Test starts: changeSubmenuOpenSearchLinkTask");
            CancellationToken token = dataGridContextMenuTaskTokenSource.Token;
            if (getSongInfoCacheTask == null)
            {
                getSongInfoCacheTask = Task.Run(delegate
                {
                    NLogWrapper.DebuggerLogger?.Trace("Test starts: getSongInfoCacheTask");
                    try
                    {
                        BMSLibrary.IRSongInfo lR2IRSongInfoCache = viewModel.GetLR2IRSongInfoCache(row, seaarchAggressively: true);
                        if (!token.IsCancellationRequested)
                        {
                            songInfoCache = lR2IRSongInfoCache;
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                }, token);
            }
            getSongInfoCacheTask.Wait();
            if (!token.IsCancellationRequested)
            {
                List<Func<MenuItem>> subMenuItemCreateFuncs = new List<Func<MenuItem>>();
                    songInfoCacheToUrlLists(songInfoCache, GridRowResolver.GetUrl(row), GridRowResolver.GetUrlDiff(row), out var urls, out var urls_diff);
                foreach (Uri url in urls)
                {
                    Func<MenuItem> item = delegate
                    {
                        try
                        {
                            MenuItem menuItem2 = new MenuItem();
                            menuItem2.Header = BeMusicSeeker.Properties.Resources.Original_URL;
                            menuItem2.IsEnabled = true;
                            menuItem2.Visibility = Visibility.Visible;
                            menuItem2.Tag = url;
                            menuItem2.ToolTip = url.ToString();
                            menuItem2.Click += dataGridContextMenuItemSearchLinkSubmenuClick;
                            return menuItem2;
                        }
                        catch
                        {
                            return (MenuItem)null;
                        }
                    };
                    subMenuItemCreateFuncs.Add(item);
                }
                foreach (Uri url2 in urls_diff)
                {
                    if (!url2.ToString().StartsWith("http://absolute.pv.land.to/", StringComparison.OrdinalIgnoreCase) && !url2.ToString().Equals("http://gnqg.rosx.net/upload/", StringComparison.OrdinalIgnoreCase) && !url2.ToString().Equals("http://gnqg.rosx.net/upload/upload.cgi", StringComparison.OrdinalIgnoreCase) && (!url2.ToString().StartsWith("http://www.ribbit.xyz/bms/mirror/", StringComparison.OrdinalIgnoreCase) || !url2.ToString().EndsWith("/")))
                    {
                        Func<MenuItem> item2 = delegate
                        {
                            try
                            {
                                MenuItem menuItem2 = new MenuItem();
                                menuItem2.Header = ((url2.ToString().StartsWith("http://www.ribbit.xyz/bms/mirror/", StringComparison.OrdinalIgnoreCase) || url2.ToString().StartsWith("http://gnqg.rosx.net/upload/", StringComparison.OrdinalIgnoreCase)) ? "Uploader" : BeMusicSeeker.Properties.Resources.Diff_URL);
                                menuItem2.IsEnabled = true;
                                menuItem2.Visibility = Visibility.Visible;
                                menuItem2.Tag = url2;
                                menuItem2.ToolTip = url2.ToString();
                                menuItem2.Click += dataGridContextMenuItemSearchLinkSubmenuClick;
                                return menuItem2;
                            }
                            catch
                            {
                                return (MenuItem)null;
                            }
                        };
                        subMenuItemCreateFuncs.Add(item2);
                    }
                }
                string input = ((GridRowResolver.GetPlaylistEntry(row)?.comment) ?? string.Empty) + Environment.NewLine + ((songInfoCache == null) ? string.Empty : songInfoCache.comment) + Environment.NewLine + GridRowResolver.GetNameDiff(row);
                string pattern = "h?(ttps?://[\\-_.!~*\\\\'()A-Z0-9;/?:@&=+$,%#]+)";
                MatchCollection matchCollection = Regex.Matches(input, pattern, RegexOptions.IgnoreCase);
                if (matchCollection.Count > 0)
                {
                    for (int num = 0; num < matchCollection.Count; num++)
                    {
                        Match match = matchCollection[num];
                        if (match.Success)
                        {
                            int temp = num;
                            Func<MenuItem> item3 = delegate
                            {
                                try
                                {
                                    Uri uri = new Uri("h" + match.Groups[1].Value.Trim('\''), UriKind.Absolute);
                                    MenuItem menuItem2 = new MenuItem();
                                    menuItem2.Header = BeMusicSeeker.Properties.Resources.Remarks_URL + (temp + 1);
                                    menuItem2.IsEnabled = true;
                                    menuItem2.Visibility = Visibility.Visible;
                                    menuItem2.Tag = uri;
                                    menuItem2.ToolTip = uri.ToString();
                                    menuItem2.Click += dataGridContextMenuItemSearchLinkSubmenuClick;
                                    return menuItem2;
                                }
                                catch
                                {
                                    return (MenuItem)null;
                                }
                            };
                            subMenuItemCreateFuncs.Add(item3);
                        }
                    }
                }
                if (!token.IsCancellationRequested)
                {
                    base.Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (!token.IsCancellationRequested)
                        {
                            List<MenuItem> list = (from f in subMenuItemCreateFuncs
                                                   select f() into i
                                                   where i != null
                                                   select i).ToList();
                            if (list.Count > 0)
                            {
                                menuItem.Items.Clear();
                                {
                                    foreach (MenuItem item4 in list)
                                    {
                                        if (!menuItem.Items.Cast<MenuItem>().Any((MenuItem i) => i.Tag.ToString().Equals(item4.Tag.ToString(), StringComparison.OrdinalIgnoreCase)))
                                        {
                                            menuItem.Items.Add(item4);
                                        }
                                    }
                                    return;
                                }
                            }
                            menuItemStatus.Header = BeMusicSeeker.Properties.Resources.Not_found;
                        }
                    });
                }
            }
        }, dataGridContextMenuTaskTokenSource.Token).Logging("dataGridContextMenuSearchLinkOpened");
    }

    /// <summary>
    /// 指定されたURI（ファイルURL等）からBMSの圧縮アーカイブファイルを非同期でダウンロードし、一時フォルダへ保存した上でインストール処理へと繋ぎます。
    /// ファイルサイズが大きすぎる場合（約500MB超）や非対応フォーマットの場合は処理を中断します。
    /// </summary>
    /// <param name="uri">ダウンロード対象のURL。</param>
    /// <returns>ダウンロード試行結果。</returns>
    private async Task<DownloadAndInstallResult> downloadAndInstall(Uri uri)
    {
        string tempDirectory = TempDirectoryPublisher.Get();
        string filePath = string.Empty;
        DownloadAndInstallResult result = DownloadAndInstallResult.OpenInBrowser;
        await Task.Run(delegate
        {
            try
            {
                Uri normalizedUri = NormalizeDownloadUri(uri);
                using AppHttpResponse response = AppHttpClient.Shared.OpenRead(normalizedUri);
                if (response.ContentLength.HasValue)
                {
                    if (response.ContentLength.Value == 0L)
                    {
                        return;
                    }
                    if (response.ContentLength.Value > DownloadAndInstallSizeLimitBytes)
                    {
                        result = DownloadAndInstallResult.BlockedBySizeLimit;
                        return;
                    }
                }
                string fileName = ResolveDownloadedArchiveFileName(normalizedUri, response);
                if (BMSFile.bmsExtensions.Concat(new string[4] { ".zip", ".7z", ".rar", "lzh" }).All((string e) => !fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }
                filePath = Path.Combine(tempDirectory, fileName);
                if (!TryCopyStreamToFileWithLimit(response.ResponseStream, filePath, DownloadAndInstallSizeLimitBytes))
                {
                    result = DownloadAndInstallResult.BlockedBySizeLimit;
                    filePath = string.Empty;
                    return;
                }
                result = DownloadAndInstallResult.Installed;
            }
            catch
            {
            }
        });
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            installBMSFiles(new string[1] { filePath });
            return DownloadAndInstallResult.Installed;
        }
        return result;
    }

    private static string NormalizeDownloadUrlString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }
        string text = dropBoxRegex.Replace(input, "https://dl.dropboxusercontent.com/$1/$2.$3");
        text = gdriveRegex.Replace(text, "https://docs.google.com/uc?export=download&id=$2");
        text = odriveRegex.Replace(text, "https://onedrive.live.com/download?$1");
        return text;
    }

    private static Uri NormalizeDownloadUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return uri;
        }
        try
        {
            return new Uri(NormalizeDownloadUrlString(uri.ToString()), UriKind.Absolute);
        }
        catch
        {
            return uri;
        }
    }

    private static bool TryCopyStreamToFileWithLimit(Stream source, string destinationPath, long maxBytes)
    {
        const int bufferSize = 81920;
        byte[] array = new byte[bufferSize];
        long num = 0L;
        try
        {
            using (FileStream fileStream = File.Create(destinationPath))
            {
                int count;
                while ((count = source.Read(array, 0, array.Length)) > 0)
                {
                    num += count;
                    if (num > maxBytes)
                    {
                        return false;
                    }
                    fileStream.Write(array, 0, count);
                }
            }
            return true;
        }
        finally
        {
            if (num > maxBytes)
            {
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static string ResolveDownloadedArchiveFileName(Uri requestedUri, AppHttpResponse response)
    {
        ContentDispositionHeaderValue contentDisposition = response.ContentHeaders?.ContentDisposition;
        string fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return fileName.Trim().Trim('"');
        }
        if (response.ContentHeaders != null && response.ContentHeaders.TryGetValues("Content-Disposition", out IEnumerable<string> contentDispositionValues))
        {
            string rawContentDisposition = contentDispositionValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(rawContentDisposition))
            {
                string parsedFileName = Regex.Replace(rawContentDisposition, ".*filename=\"([^\"]+)\".*", "$1");
                if (!string.IsNullOrWhiteSpace(parsedFileName) && !string.Equals(parsedFileName, rawContentDisposition, StringComparison.Ordinal))
                {
                    return parsedFileName;
                }
            }
        }
        if (response.Headers?.Location != null)
        {
            return Path.GetFileName(response.Headers.Location.ToString());
        }
        if (Path.GetFileName(requestedUri.ToString()).Contains('?') || Path.GetFileName(requestedUri.ToString()).Contains('='))
        {
            return Path.GetFileName(response.ResponseUri.ToString());
        }
        return Path.GetFileName(requestedUri.ToString());
    }

    /// <summary>
    /// 取得された複数のBMSファイル（またはアーカイブのパス）の一覧をもとに、
    /// ViewModelのインストールモジュールを非同期で呼び出し、アプリケーションのデータベースやフォルダへ導入します。
    /// 導入件数が複数の場合は進捗表示付きのポップアップダイアログ (ProgressDialog) を表示します。
    /// </summary>
    /// <param name="filePaths">インストール対象の一連のファイルシステムパス群。</param>
    private async void installBMSFiles(IEnumerable<string> filePaths)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        int progIdx = 0;
        int failNum = 0;
        List<string> installs = filePaths.ToList();
        int total = installs.Count;
        if (total == 0)
        {
            return;
        }
        CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
        Task task = Task.Run(delegate
        {
            viewModel.InstallBMSFiles(installs, cancelTokenSource.Token, delegate (bool s)
            {
                progIdx++;
                if (!s)
                {
                    failNum++;
                }
            });
        }, cancelTokenSource.Token).Logging("installBMSFiles");
        if (total == 1)
        {
            await task;
            return;
        }
        ProgressDialog.Execute(this, BeMusicSeeker.Properties.Resources.Install, "", delegate
        {
            while (task.Status != TaskStatus.RanToCompletion)
            {
                try
                {
                    ProgressDialog.Current.ReportWithCancellationCheck(100 * progIdx / total, "[{0}/{1}] {2}", progIdx + 1, total, installs[progIdx]);
                }
                catch
                {
                    cancelTokenSource.Cancel();
                    while (task.Status != TaskStatus.RanToCompletion)
                    {
                        Thread.Sleep(100);
                    }
                    break;
                }
                Thread.Sleep(100);
            }
        }, new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false));
    }

    private void cancelDropInstallQueueClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.CancelDroppedInstallQueue();
    }

    private async void dataGridContextMenuItemSearchLinkSubmenuClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem))
        {
            return;
        }
        try
        {
            if (!Settings.Default.SkipInitFileCheck && Settings.Default.AutoInstall && menuItem.Tag is Uri)
            {
                try
                {
                    Uri uri = (Uri)menuItem.Tag;
                    if (!uri.ToString().EndsWith("/") && !uri.ToString().EndsWith(".htm") && !uri.ToString().EndsWith(".html"))
                    {
                        switch (await downloadAndInstall(uri))
                        {
                            case DownloadAndInstallResult.Installed:
                                newlyInstalledTreeViewItem.IsExpanded = true;
                                return;
                            case DownloadAndInstallResult.BlockedBySizeLimit:
                                return;
                        }
                    }
                }
                catch
                {
                }
            }
            Process.Start(menuItem.Tag.ToString());
        }
        catch
        {
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void dataGridContextMenuItemUpdateRankingDataClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !((menuItem.Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridOperationFiles();
        if (bmsFiles != null && bmsFiles.Count() != 0)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.GetLR2IRCacheBMSFiles(bmsFiles);
            }).Logging("dataGridContextMenuItemUpdateRankingDataClick");
            e.Handled = true;
        }
    }

    private void dataGridContextMenuItemRegisterBMSFileToScoreViwer(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow } }))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridRealFiles();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        Task.Run(delegate
        {
            string fileName = viewModel.RegisterBMSFilesToScoreViewer(bmsFiles);
            try
            {
                if (bmsFiles.Count == 1)
                {
                    Process.Start(fileName);
                }
            }
            catch
            {
            }
        }).Logging("dataGridContextMenuItemRegisterBMSFileToScoreViwer");
    }

    private void dataGridContextMenuItemForceFileScanCheckSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridOperationFiles();
        if (bmsFiles != null && bmsFiles.Count() != 0)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.ForceFileScanCheckBMSFiles(bmsFiles);
            }).Logging("dataGridContextMenuItemForceFileScanCheckSelectedBMS");
            e.Handled = true;
        }
    }

    private void dataGridContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridOperationFiles();
        if (bmsFiles != null && bmsFiles.Count() != 0)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            e.Handled = true;
            Task.Run(delegate
            {
                viewModel.RemoveInstallDestination(bmsFiles);
            }).Logging("dataGridContextMenuRemoveInstallDestinationClick");
        }
    }

    private void dataGridContextMenuSearchCorrectInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridOperationFiles();
        if (bmsFiles != null && bmsFiles.Count() != 0)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.SearchCorrectInstallationDirectoryBMSFiles(bmsFiles);
            }).Logging("dataGridContextMenuSearchCorrectInstallationDirectoryClick");
            e.Handled = true;
        }
    }

    private void dataGridContextMenuFixInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridOperationFiles();
        if (bmsFiles == null || bmsFiles.Count() == 0)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        if (!bmsFiles.Any((BMSFile f) => !string.IsNullOrWhiteSpace(f.instl_dst)))
        {
            MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_fix_installation_warning, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
        else if (MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_fix_installation, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.FixInstallationDirectoryBMSFiles(bmsFiles);
            }).Logging("dataGridContextMenuFixInstallationDirectoryClick");
        }
    }

    private void dataGridContextMenuItemDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        List<BMSTableEntry> list2 = GetSelectedGridPlaylistEntries();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (list2.Count <= 0)
        {
            return;
        }
        foreach (IGrouping<BMSTable, BMSTableEntry> enGrp in from en in list2
                                                             group en by en.parent)
        {
            Task.Run(delegate
            {
                viewModel.DeleteBMSTableEntries(enGrp.AsEnumerable(), enGrp.Key);
            }).Logging("dataGridContextMenuItemDeleteEntryClick");
        }
    }

    private void dataGridContextMenuItemAutoRenameFolderClick(object sender, RoutedEventArgs e)
    {
        List<BMSFile> bmsFiles = GetSelectedGridRealFiles();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (bmsFiles.Count > 0)
        {
            Task.Run(delegate
            {
                viewModel.AutoRenameBMSFolder(bmsFiles);
            }).Logging("dataGridContextMenuItemAutoRenameFolderClick");
        }
    }

    private void dataGridContextMenuItemRenameBMSFileClick(object sender, RoutedEventArgs e)
    {
        List<BMSFile> bmsFiles = GetSelectedGridRealFiles();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        bool isPendingSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallPending;
        if (bmsFiles.Count <= 0 || MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_rename_to_invalid, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        Task.Run(delegate
        {
            List<BMSFile> list = bmsFiles.Where((BMSFile f) => Path.GetExtension(f.path).StartsWith(".b", StringComparison.OrdinalIgnoreCase)).ToList();
            List<BMSFile> list2 = bmsFiles.Where((BMSFile f) => Path.GetExtension(f.path).StartsWith(".p", StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count > 0)
            {
                if (isPendingSelected)
                {
                    viewModel.RenamePendingBMSFilesExtensions(list, ".bmx");
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
                    viewModel.RenamePendingBMSFilesExtensions(list2, ".pmx");
                }
                else
                {
                    viewModel.RenameBMSFilesExtensions(list2, ".pmx");
                }
            }
        }).Logging("dataGridContextMenuItemRenameBMSFileClick");
    }

    private void dataGridContextMenuItemRemoveBMSFileClick(object sender, RoutedEventArgs e)
    {
        List<BMSFile> bmsFiles = GetSelectedGridRealFiles();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        bool isPendingSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallPending;
        if (viewModel == null || bmsFiles.Count == 0)
        {
            return;
        }
        bool deleteContainingPackageFoldersWhenNoBms = false;
        if (isPendingSelected)
        {
            if (!ShowPendingDeleteConfirmDialog(out deleteContainingPackageFoldersWhenNoBms))
            {
                return;
            }
        }
        else if (MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_recycle, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        Task.Run(delegate
        {
            if (isPendingSelected)
            {
                viewModel.RemovePendingBMSFiles(bmsFiles, sendToRecycleBin: true, deleteContainingPackageFoldersWhenNoBms: deleteContainingPackageFoldersWhenNoBms);
            }
            else
            {
                viewModel.RemoveBMSFiles(bmsFiles);
            }
        }).Logging("dataGridContextMenuItemRemoveBMSFileClick");
    }

    private bool ShowPendingDeleteConfirmDialog(out bool deleteContainingPackageFoldersWhenNoBms)
    {
        PendingDeleteConfirmDialog pendingDeleteConfirmDialog = new PendingDeleteConfirmDialog
        {
            Owner = this
        };
        bool? flag = pendingDeleteConfirmDialog.ShowDialog();
        deleteContainingPackageFoldersWhenNoBms = pendingDeleteConfirmDialog.DeleteFolderWhenNoBmsChecked;
        return flag == true;
    }

    private async void dataGridContextMenuItemMoveFileClick(object sender, RoutedEventArgs e)
    {
        List<BMSFile> bmsFiles = GetSelectedGridOperationFiles().Where((BMSFile f) => !string.IsNullOrWhiteSpace(f?.path)).ToList();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (!(sender is MenuItem menuItem))
        {
            return;
        }
        string dstDir = menuItem.DataContext as string;
        if (viewModel != null && dstDir != null && bmsFiles.Count > 0 && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_other_root, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            await Task.Run(delegate
            {
                viewModel.MoveBMSFolder(bmsFiles, dstDir);
            }).Logging("dataGridContextMenuItemMoveFileClick");
        }
    }

    private void fixEncodingSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem menuItem && ((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow)
        {
            List<BMSFile> list = GetSelectedGridRealFiles();
            if (list != null && list.Count() != 0)
            {
                (base.DataContext as MainWindowViewModel).FixEncodingBMSFiles(list, menuItem.Tag.ToString());
                e.Handled = true;
            }
        }
    }

    private void ignoreFileScanCheckSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem menuItem && ((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow)
        {
            List<BMSFile> list = GetSelectedGridRealFiles();
            if (list != null && list.Count() != 0)
            {
                (base.DataContext as MainWindowViewModel).IgnoreFileScanCheckBMSFiles(list);
                e.Handled = true;
            }
        }
    }

    private void notIgnoredFileScanCheckSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem menuItem && ((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow)
        {
            List<BMSFile> list = GetSelectedGridRealFiles();
            if (list != null && list.Count() != 0)
            {
                (base.DataContext as MainWindowViewModel).NotIgnoreFileScanCheckBMSFiles(list);
                e.Handled = true;
            }
        }
    }

    private async void forceInstallSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridRealFiles();
        if (bmsFiles == null || bmsFiles.Count() == 0)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        if (dataGrid.SelectionMode == DataGridSelectionMode.Extended)
        {
            dataGrid.SelectedItems.Clear();
        }
        else
        {
            dataGrid.SelectedItem = null;
        }
        if (!treeViewItemInstallPending.IsSelected)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "forceInstallSelectedBMS");
        }
        await Task.Run(delegate
        {
            viewModel.ForceInstallBMSFiles(bmsFiles);
        }).Logging("forceInstallSelectedBMS");
    }

    private static string GetManualInstallConfirmationMessage()
    {
        return Settings.Default.DeletePendingPackageSourceAfterInstall
            ? BeMusicSeeker.Properties.Resources.Msg_manual_installation_delete_source
            : BeMusicSeeker.Properties.Resources.Msg_manual_installation;
    }

    private async void manualInstallSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridRealFiles();
        if (bmsFiles == null || bmsFiles.Count() == 0)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        if (Settings.Default.ShowDiffBMSInstallConfirmMsg && MessageBox.Show(Window.GetWindow(this), GetManualInstallConfirmationMessage(), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) != MessageBoxResult.OK)
        {
            return;
        }
        if (dataGrid.SelectionMode == DataGridSelectionMode.Extended)
        {
            dataGrid.SelectedItems.Clear();
        }
        else
        {
            dataGrid.SelectedItem = null;
        }
        if (!treeViewItemInstallPending.IsSelected)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "manualInstallSelectedBMS");
        }
        await Task.Run(delegate
        {
            viewModel.ManualInstallBMSFiles(bmsFiles);
        }).Logging("manualInstallSelectedBMS");
    }

    private async void searchInstallationDirectorySelectedBMS(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridRealFiles();
        if (bmsFiles != null && bmsFiles.Count() != 0)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.SearchInstallationDirectoryBMSFiles(bmsFiles);
            }).Logging("searchInstallationDirectorySelectedBMS");
        }
    }

    private async void dataGridContextMenuItemDeleteInstallPackagesClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow } }))
        {
            return;
        }
        List<BMSFile> selectedBmsFiles = GetSelectedGridRealFiles();
        if (selectedBmsFiles.Count == 0)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        bool isPendingSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallPending;
        bool isInstalledSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallInstalled;
        if (!isPendingSelected && !isInstalledSelected)
        {
            return;
        }
        string confirmationMessage = isPendingSelected ? BeMusicSeeker.Properties.Resources.Msg_clear_selected_pendings : BeMusicSeeker.Properties.Resources.Msg_clear_selected_installed;
        if (MessageBox.Show(Window.GetWindow(this), confirmationMessage, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        NLogWrapper.FileLogger?.Info("dataGrid_delete_install_packages requested section=" + _currentTreeSelectionSection + " selectedRows=" + selectedBmsFiles.Count);
        e.Handled = true;
        if (dataGrid.SelectionMode == DataGridSelectionMode.Extended)
        {
            dataGrid.SelectedItems.Clear();
        }
        else
        {
            dataGrid.SelectedItem = null;
        }
        if (isPendingSelected)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "dataGridContextMenuItemDeleteInstallPackagesClick");
            await Task.Run(delegate
            {
                viewModel.RemoveBMSPackagesPending(selectedBmsFiles);
            }).Logging("dataGridContextMenuItemDeleteInstallPackagesClick");
            if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
            {
                await Task.Run(delegate
                {
                    viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
                }).Logging("dataGridContextMenuItemDeleteInstallPackagesClick");
            }
            return;
        }
        SelectNextSiblingOrRoot(newlyInstalledTreeViewItem, treeView.SelectedItem, "dataGridContextMenuItemDeleteInstallPackagesClick");
        await Task.Run(delegate
        {
            viewModel.RemoveBMSPackagesInstalled(selectedBmsFiles);
        }).Logging("dataGridContextMenuItemDeleteInstallPackagesClick");
        if (newlyInstalledTreeViewItem.IsSelected && newlyInstalledTreeViewItem.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter);
            }).Logging("dataGridContextMenuItemDeleteInstallPackagesClick");
        }
    }

    private async void searchMergeDestinationSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = GetSelectedGridRealFiles();
        if (bmsFiles == null || bmsFiles.Count == 0 || !ConfirmMergeDestinationSearch())
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.SearchMergeDestinationBMSFiles(bmsFiles);
        }).Logging("searchMergeDestinationSelectedBMS");
    }

    private bool ConfirmMergeDestinationSearch()
    {
        return MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_estimate_merge_confirm, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) == MessageBoxResult.OK;
    }

    private void dataGridContextMenuItemConvertToAudioFileClick(object sender, RoutedEventArgs e)
    {
        BMSFile[] bmsFiles = GetSelectedGridRealFiles().Where((BMSFile f) => File.Exists(f.path)).ToArray();
        if (bmsFiles.Length == 0)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        CancellationTokenSource cancelTokenSource = new CancellationTokenSource();
        int progIdx = 0;
        int failNum = 0;
        CommonOpenFileDialog commonOpenFileDialog = new CommonOpenFileDialog
        {
            Title = BeMusicSeeker.Properties.Resources.Save_to,
            IsFolderPicker = true
        };
        if (commonOpenFileDialog.ShowDialog() != CommonFileDialogResult.Ok)
        {
            return;
        }
        string saveDir = commonOpenFileDialog.FileName;
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
        }, cancelTokenSource.Token).Logging("dataGridContextMenuItemConvertToAudioFileClick");
        ProgressDialog.Execute(this, BeMusicSeeker.Properties.Resources.Converting, viewModel.settingDialog.EncoderNames[(int)Settings.Default.Encoder] + " - " + BeMusicSeeker.Properties.Resources.Sampling_rate + ":" + viewModel.settingDialog.PlayerSampleRateNames[Settings.Default.EncoderSampleRate] + " " + BeMusicSeeker.Properties.Resources.Sampling_format + ":" + viewModel.settingDialog.PlayerFormatNames[Settings.Default.EncoderFormat], delegate
        {
            while (task.Status != TaskStatus.RanToCompletion)
            {
                try
                {
                    ProgressDialog.Current.ReportWithCancellationCheck(100 * (progIdx + 1) / (bmsFiles.Length + 1), "[{0}/{1}] {2}", progIdx + 1, bmsFiles.Length, bmsFiles[Math.Min(progIdx, bmsFiles.Length - 1)].path);
                }
                catch
                {
                    cancelTokenSource.Cancel();
                    while (task.Status != TaskStatus.RanToCompletion)
                    {
                        Thread.Sleep(100);
                    }
                    break;
                }
                Thread.Sleep(100);
            }
        }, new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false));
        MessageBox.Show(Window.GetWindow(this), ((!cancelTokenSource.IsCancellationRequested) ? BeMusicSeeker.Properties.Resources.Msg_conversion_completed : BeMusicSeeker.Properties.Resources.Msg_conversion_stopped) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Success + ": " + (progIdx - failNum) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Failure + ": " + (bmsFiles.Length - progIdx + failNum), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, cancelTokenSource.IsCancellationRequested ? MessageBoxImage.Exclamation : MessageBoxImage.Asterisk, MessageBoxResult.OK);
    }

    private void playlistTableDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
        treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (!(sender is TreeViewItem treeViewItem))
        {
            return;
        }
        BMSTable table = treeViewItem.DataContext as BMSTable;
        if (table == null || table.is_external_sync)
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 == null)
        {
            return;
        }
        treeViewItem2.Background = Brushes.Transparent;
        if (!e.Data.GetDataPresent("System.Windows.Controls.SelectedItemCollection"))
        {
            return;
        }
        List<object> selectedRows;
        try
        {
            selectedRows = ((IList)e.Data.GetData("System.Windows.Controls.SelectedItemCollection")).Cast<object>().Where((object row) => row != null).ToList();
        }
        catch
        {
            return;
        }
        if (selectedRows == null || selectedRows.Count == 0)
        {
            return;
        }
        string folderName;
        if (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode))
        {
            folderName = null;
        }
        else
        {
            if (folderNode.IsSpecial)
            {
                return;
            }
            folderName = folderNode.FolderName;
        }
        Task.Run(delegate
        {
            viewModel.AddEntriesToFolderBMSTable(selectedRows, table, folderName);
        }).Logging("playlistTableDrop");
    }

    private void playlistTableDragOver(object sender, DragEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: BMSTable { is_external_sync: false } })
        {
            _ = base.DataContext;
            TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
            if (treeViewItem2 != null && (!TryGetPlaylistFolderNode(treeViewItem2.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsSpecial))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }
    }

    private void playlistTableDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        TreeViewItem tviTable = sender as TreeViewItem;
        if (tviTable == null)
        {
            return;
        }
        TreeViewItem treeViewItem = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem == null || !(tviTable.DataContext is BMSTable bMSTable))
        {
            return;
        }
        if (!bMSTable.is_external_sync && (!TryGetPlaylistFolderNode(treeViewItem.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsSpecial))
        {
            treeViewItem.Background = SystemColors.HighlightBrush;
        }
        if (!TryGetPlaylistFolderNode(treeViewItem.DataContext, out _))
        {
            BooleanAnimationUsingKeyFrames booleanAnimationUsingKeyFrames = new BooleanAnimationUsingKeyFrames();
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

    private void playlistTableDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!(sender is TreeViewItem treeViewItem))
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

    /// <summary>
    /// 内蔵プレーヤーの「次の曲へ (Next)」ボタンがクリックされた際のイベントハンドラ。
    /// リスト内で現在選択されている曲の次の曲を非同期で再生開始します。
    /// </summary>
    private async void gridBMSPlayerControlsNextButtonClicked(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.PlayNextBMSfile();
            }).Logging("gridBMSPlayerControlsNextButtonClicked");
        }
    }

    /// <summary>
    /// 内蔵プレーヤーの「前の曲へ (Previous)」ボタンがクリックされた際のイベントハンドラ。
    /// ダブルクリック時は前の曲へ移動し、シングルクリック時は現在の曲を最初から再生し直します。
    /// </summary>
    private async void gridBMSPlayerControlsPreviousButtonClicked(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        if (gridBMSPlayerControlsPreviousButtonClickTimer == null)
        {
            gridBMSPlayerControlsPreviousButtonClickTimer = new DispatcherTimer(new TimeSpan(0, 0, 0, 0, 500), DispatcherPriority.Background, gridBMSPlayerControlsPreviousButtonSingleClicked, Dispatcher.CurrentDispatcher);
        }
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Stop();
            await Task.Run(delegate
            {
                viewModel.PlayPreviousBMSfile();
            }).Logging("gridBMSPlayerControlsPreviousButtonClicked");
        }
        else
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Start();
        }
    }

    private async void gridBMSPlayerControlsPreviousButtonSingleClicked(object sender, EventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Stop();
            await Task.Run(delegate
            {
                viewModel.RestartPlayingBMSfileStart();
            }).Logging("gridBMSPlayerControlsPreviousButtonSingleClicked");
        }
    }

    /// <summary>
    /// 内蔵プレーヤーの「再生 (Play)」ボタンがクリックされた際のイベントハンドラ。
    /// 現在の再生状態が停止・一時停止であれば再生を再開または開始します。
    /// </summary>
    private async void gridBMSPlayerControlsPlayStartButtonClicked(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            e.Handled = true;
            if ((viewModel.NowPlayingBMS == null || viewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE)) && isPanelStateValid(MainWindowViewModel.PanelState.BMS_PLAYER))
            {
                NowPanelState = MainWindowViewModel.PanelState.BMS_PLAYER;
            }
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile(forceNewPlay: false);
            }).Logging("gridBMSPlayerControlsPlayStartButtonClicked");
        }
    }

    /// <summary>
    /// 内蔵プレーヤーの「停止 (Stop)」ボタンがクリックされた際のイベントハンドラ。
    /// 実行中の再生プロセスを終了し、再生状態をクリアします。
    /// </summary>
    private async void gridBMSPlayerControlsPlayStopButtonClicked(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.PlayEndBMSFile(closeProcess: true);
            }).Logging("gridBMSPlayerControlsPlayStopButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsFastForwardButtonClicked(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.FastForwardPlayingBMSfileStart();
            }).Logging("gridBMSPlayerControlsFastForwardButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsFastForwardButtonReleased(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        await Task.Run(delegate
        {
            viewModel.FastForwardPlayingBMSfileEnd();
        }).Logging("gridBMSPlayerControlsFastForwardButtonReleased");
    }

    private async void gridBMSPlayerControlsFastForwardButtonReleased(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.FastForwardPlayingBMSfileEnd();
            }).Logging("gridBMSPlayerControlsFastForwardButtonReleased");
        }
    }

    private async void gridBMSPlayerControlsFastBackwardButtonClicked(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.FastBackwardPlayingBMSfileStart();
            }).Logging("gridBMSPlayerControlsFastBackwardButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsFastBackwardButtonReleased(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.FastBackwardPlayingBMSfileEnd();
            }).Logging("gridBMSPlayerControlsFastBackwardButtonReleased");
        }
    }

    private async void gridBMSPlayerControlsFastBackwardButtonReleased(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.FastBackwardPlayingBMSfileEnd();
            }).Logging("gridBMSPlayerControlsFastBackwardButtonReleased");
        }
    }

    private async void gridBMSPlayerControlsShowInfoButtonClicked(object sender, RoutedEventArgs e)
    {
        if (Settings.Default.UsePlayeruBMplay)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            if (viewModel != null)
            {
                await Task.Run(delegate
                {
                    viewModel.uBMplayShowInfo();
                }).Logging("gridBMSPlayerControlsShowInfoButtonClicked");
            }
        }
        else
        {
            gridPlayngBmsInfo.Visibility = ((gridPlayngBmsInfo.Visibility != Visibility.Hidden) ? Visibility.Hidden : Visibility.Visible);
        }
    }

    private async void gridBMSPlayerControlsShowEffectButtonClicked(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.uBMplayShowEffect();
            }).Logging("gridBMSPlayerControlsShowEffectButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsChangePlaysideButtonClicked(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            await Task.Run(delegate
            {
                viewModel.uBMplayChangePlayside();
            }).Logging("gridBMSPlayerControlsChangePlaysideButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsIncreaseHighSpeedButtonClicked(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.uBMplayIncreaseHighSpeed();
            }).Logging("gridBMSPlayerControlsIncreaseHighSpeedButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsDecreaseHighSpeedButtonClicked(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.uBMplayDecreaseHighSpeed();
            }).Logging("gridBMSPlayerControlsDecreaseHighSpeedButtonClicked");
        }
    }

    /// <summary>
    /// メインリスト (DataGrid) 上でのキー入力イベントを処理します。
    /// Enterキー押下時に、選択中のBMS楽曲のプレビュー再生（BMSPlayerパネル展開および再生開始）を開始します。
    /// 編集モード中の場合は変更の確定のみ行います。UI仮想化でコンテナが未生成でも再生可能なフォールバックを含みます。
    /// </summary>
    private async void dataGridKeyDown(object sender, KeyEventArgs e)
    {
        if (!(sender is DataGrid dataGrid))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Return:
                {
                    e.Handled = true;
                    bool hasSelectedRow = dataGrid.TryGetSelectedRowRealized(out DataGridRow selectedRow);
                    BMSFile selectedBmsFile = GridRowResolver.GetOperationBmsFile(dataGrid.SelectedItem);
                    if (!hasSelectedRow && selectedBmsFile == null)
                    {
                        break;
                    }
                    if (hasSelectedRow && selectedRow.IsEditing)
                    {
                        dataGrid.CommitEdit();
                        break;
                    }
                    MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
                    if (viewModel != null)
                    {
                        if (hasSelectedRow)
                        {
                            _renewBMSPlayerControlInfo(selectedRow);
                        }
                        else
                        {
                            // NOTE:
                            // 行仮想化により選択行のコンテナが未実体化でも、SelectedItem が有効なら再生処理は継続する。
                            // Enter が無反応になるのを防ぐため、BMSFileベースでプレイヤー情報を更新する。
                            _renewBMSPlayerControlInfo(selectedBmsFile);
                            NLogWrapper.FileLogger?.Info("datagrid_selected_row_realize_fallback used=True selectedIndex=" + dataGrid.SelectedIndex);
                        }
                        if ((viewModel.NowPlayingBMS == null || viewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE)) && isPanelStateValid(MainWindowViewModel.PanelState.BMS_PLAYER))
                        {
                            NowPanelState = MainWindowViewModel.PanelState.BMS_PLAYER;
                        }
                        await Task.Run(delegate
                        {
                            viewModel.PlayStartBMSfile();
                        }).Logging("dataGridKeyDown");
                    }
                    break;
                }
            case Key.LeftShift:
            case Key.RightShift:
            case Key.LeftCtrl:
            case Key.RightCtrl:
                e.Handled = true;
                dataGrid.SelectionMode = DataGridSelectionMode.Extended;
                break;
        }
    }

    private void showBMSPlayerPanel()
    {
        if (windowsFormsHost != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(windowsFormsHost, UIElement.VisibilityProperty).ParentMultiBinding;
            windowsFormsHost.Visibility = Visibility.Visible;
            windowsFormsHost.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    public void tryShowBMSPlayerPanel()
    {
        if (NowPanelState == MainWindowViewModel.PanelState.BMS_PLAYER)
        {
            showBMSPlayerPanel();
        }
        if (!(new WindowInteropHelper(this).Handle == Win32API.GetForegroundWindow()))
        {
            return;
        }
        base.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)async delegate
        {
            for (int i = 1; i <= 10; i++)
            {
                NLogWrapper.DebuggerLogger?.Trace("try to set focus on datagrid row");
                if (!(new WindowInteropHelper(this).Handle == Win32API.GetForegroundWindow()))
                {
                    break;
                }
                Keyboard.Focus(dataGrid);
                await Task.Delay(100);
            }
        });
    }

    private void showBrowserPanel()
    {
        if (webBrowser != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(webBrowser, UIElement.VisibilityProperty).ParentMultiBinding;
            webBrowser.Visibility = Visibility.Visible;
            webBrowser.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    public void tryShowBrowserPanel()
    {
        if (NowPanelState == MainWindowViewModel.PanelState.MOVIE_PLAYER)
        {
            showBrowserPanel();
        }
    }

    private void collapseBMSPlayerPanel()
    {
        if (windowsFormsHost != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(windowsFormsHost, UIElement.VisibilityProperty).ParentMultiBinding;
            windowsFormsHost.Visibility = Visibility.Collapsed;
            windowsFormsHost.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    private void collapseBrowserPanel()
    {
        if (webBrowser != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(webBrowser, UIElement.VisibilityProperty).ParentMultiBinding;
            webBrowser.Visibility = Visibility.Collapsed;
            webBrowser.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    private void dialogIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue && (bool)e.OldValue)
        {
            switch (NowPanelState)
            {
                case MainWindowViewModel.PanelState.BMS_PLAYER:
                    showBMSPlayerPanel();
                    break;
                case MainWindowViewModel.PanelState.MOVIE_PLAYER:
                    showBrowserPanel();
                    break;
            }
            if (sender is PlaylistPropertyDialog)
            {
                BindingOperations.GetMultiBindingExpression(gridBMSPlayerControlsFolderPath, TextBlock.TextProperty).UpdateTarget();
            }
        }
    }

    public void gridBMSPlayerControlsRotatePanelStateButtonClicked()
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            gridBMSPlayerControlsRotatePanelStateButtonClicked(null, null);
        }, DispatcherPriority.ContextIdle);
    }

    private void gridBMSPlayerControlsRotatePanelStateButtonClicked(object sender = null, RoutedEventArgs e = null)
    {
        MainWindowViewModel.PanelState panelState = NowPanelState;
        do
        {
            panelState = (panelState.HasFlag(MainWindowViewModel.PanelState.BMS_PLAYER) ? ((panelState & ~MainWindowViewModel.PanelState.BMS_PLAYER) | MainWindowViewModel.PanelState.MOVIE_PLAYER) : ((!panelState.HasFlag(MainWindowViewModel.PanelState.MOVIE_PLAYER)) ? (panelState | MainWindowViewModel.PanelState.BMS_PLAYER) : (panelState & ~MainWindowViewModel.PanelState.MOVIE_PLAYER)));
        }
        while (!isPanelStateValid(panelState));
        NowPanelState = panelState;
    }

    private void gridBMSPlayerControlsRotatePanelStateButtonClicked2(object sender = null, RoutedEventArgs e = null)
    {
        NowPanelState ^= MainWindowViewModel.PanelState.TITLE_SMALL;
    }

    private bool isPanelStateValid(MainWindowViewModel.PanelState state)
    {
        if (state.HasFlag(MainWindowViewModel.PanelState.BMS_PLAYER))
        {
            if (windowsFormsHost == null || !windowsFormsHost.IsEnabled)
            {
                return false;
            }
            if (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && Settings.Default.UsePlayeruBMplay)
            {
                return false;
            }
            if (!Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && Settings.Default.UsePlayeruBMplay)
            {
                return true;
            }
            if (Settings.Default.UsePlayerLR2body)
            {
                return false;
            }
            if (Settings.Default.UsePlayerBMIIDXView)
            {
                return true;
            }
            return false;
        }
        if (state.HasFlag(MainWindowViewModel.PanelState.MOVIE_PLAYER) && (webBrowser == null || !webBrowser.IsEnabled || ((MainWindowViewModel)base.DataContext).BrowserHtml == null))
        {
            return false;
        }
        return true;
    }

    private void windowsFormsHostIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!isPanelStateValid(NowPanelState))
        {
            gridBMSPlayerControlsRotatePanelStateButtonClicked();
        }
    }

    private static void forbidNavigating(object s, NavigatingCancelEventArgs e)
    {
        e.Cancel = true;
    }

    private void webBrowserLoadCompleted(object sender, NavigationEventArgs e)
    {
        webBrowser.Navigating += forbidNavigating;
        NowPanelState = MainWindowViewModel.PanelState.MOVIE_PLAYER;
    }

    private TreeViewItem _lastSelectedTreeViewItem;
    private bool _isCrossTreeDeselecting = false;

    private void gridTreePane_TreeViewItemSelected(object sender, RoutedEventArgs e)
    {
        TreeViewItem tvi = e.OriginalSource as TreeViewItem;
        if (tvi == null) tvi = e.Source as TreeViewItem;

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
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            bool isManualInteraction = treeView.IsKeyboardFocusWithin || treeView.IsMouseOver;
            if (!_isCrossTreeDeselecting && e.NewValue == null && e.OldValue != null && e.OldValue is BMSTable
                && (viewModel?.IsPlaylistUpdating ?? false) && !isManualInteraction)
            {
                if (!treeView.SelectTreeViewItemSearchedByDataContext(e.OldValue))
                {
                    BMSTable table = (BMSTable)e.OldValue;
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
        if (IsSameOrDescendantOf(selectedTreeViewItem, treeViewItemFullScanCheck))
        {
            return TreeSelectionSection.FullScanCheck;
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

    private async void sliderPlayerMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        Slider slider = (Slider)sender;
        Point position = e.GetPosition(slider);
        double value = slider.Maximum * Math.Max(0.0, Math.Min(1.0, (position.X - 5.0) / (slider.ActualWidth - 10.0)));
        slider.Value = value;
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        MainWindowViewModel mainWindowViewModel = viewModel;
        if (mainWindowViewModel != null && mainWindowViewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PLAY))
        {
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile(forceNewPlay: false);
            }).Logging("sliderPlayerMouseMove");
        }
    }

    private async void sliderPlayerMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        MainWindowViewModel mainWindowViewModel = viewModel;
        if (mainWindowViewModel != null && mainWindowViewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE))
        {
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile(forceNewPlay: false);
            }).Logging("sliderPlayerMouseLeftButtonUp");
        }
    }

    private async void sliderPlayerMouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        MainWindowViewModel mainWindowViewModel = viewModel;
        if (mainWindowViewModel != null && mainWindowViewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE))
        {
            await Task.Run(delegate
            {
                viewModel.PlayStartBMSfile(forceNewPlay: false);
            }).Logging("sliderPlayerMouseLeave");
        }
    }



}
