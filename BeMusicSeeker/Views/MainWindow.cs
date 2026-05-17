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
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
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

    private bool _isClosingOrClosed;

    private ContextMenu _lastOpenedContextMenu;

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
        ChartInfoParseError,
        Other
    }

    private enum DownloadAndInstallResult
    {
        Installed,
        OpenInBrowser,
        BlockedBySizeLimit
    }

    private TreeSelectionSection _currentTreeSelectionSection = TreeSelectionSection.None;

    private readonly PropertyChangedEventListener settingsDefaultEventListnener;

    private static readonly string clearlampUri = "http://xyzzz.net/bms/clearlamp";

    private BMSLibrary.IRSongInfo songInfoCache;

    private CancellationTokenSource tableContextMenuTaskTokenSource;

    private Task getSongInfoCacheTask;

    private Task changeSubmenuOpenVideoTask;

    private Task changeSubmenuOpenDocumentTask;

    private Task changeSubmenuOpenSearchLinkTask;

    private static readonly Regex dropBoxRegex = new("https?://(?:(?:www|dl)\\.dropbox\\.com|dl\\.dropboxusercontent\\.com)/(sh?)/([^?]*)\\.([^?]*)(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex gdriveRegex = new("https?://drive\\.google\\.com/(file/d/|open\\?id=)([^/]*)(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex odriveRegex = new("https?://onedrive\\.live\\.com/redir\\?(.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Storyboard treeViewItemInstantStoryBoardPlaylistTable = new();

    private static DispatcherTimer gridBMSPlayerControlsPreviousButtonClickTimer;

    private BitmapSource _panelImage;

    // マージ後に自動選択するDuplicateGroupのHeader（曲名）をキャッシュ
    private string _pendingDuplicateGroupHeader;
    private int _duplicateGroupAutoSelectRequestVersion;
    private PropertyChangedEventHandler _duplicateGroupAutoSelectHandler;
    private MainWindowViewModel _duplicateGroupAutoSelectHandlerOwner;

    private bool startupInitialSelectionApplied;

    private PropertyChangedEventHandler _startupInitialSelectionReadyHandler;

    private readonly Dictionary<BMSFile, PendingInstallDestinationEditState> _pendingInstallDestinationEditStates = [];

    private sealed class PendingInstallDestinationEditState
    {
        public string InstallDestination { get; set; }

        public string InstallDestinationTitle { get; set; }

        public string InstallDestinationArtist { get; set; }

        public IReadOnlyList<ChartWarning> Warnings { get; set; }

        public IReadOnlyList<string> Suggestions { get; set; }
    }

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
                        var memoryStream = new MemoryStream(File.ReadAllBytes(Settings.Default.StagefilePath));
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
        ApplySavedTreeViewWidth();
        AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(keywordSearchWindowPreviewMouseDown), true);
        Deactivated += MainWindow_Deactivated;

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
        if (e.Data.GetData(DataFormats.FileDrop) is string[] filePaths)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            string[] pathSnapshot = [.. filePaths];
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
        if (!isPanelStateValid(NowPanelState))
        {
            gridBMSPlayerControlsRotatePanelStateButtonClicked();
        }
        if (webBrowser != null)
        {
            object value = typeof(WebBrowser).GetProperty("AxIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(webBrowser, null);
            value.GetType().InvokeMember("Silent", BindingFlags.SetProperty, null, value, [true]);
            value.GetType().InvokeMember("RegisterAsDropTarget", BindingFlags.SetProperty, null, value, [false]);
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
        _isClosingOrClosed = true;
        var viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && _startupInitialSelectionReadyHandler != null)
        {
            viewModel.PropertyChanged -= _startupInitialSelectionReadyHandler;
            _startupInitialSelectionReadyHandler = null;
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
            Settings.Default.Save();
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
        long sourceGenerationId = viewModel?.PlaylistSourceGenerationId ?? 0L;
        long viewGenerationId = viewModel?.PlaylistAdoptedViewGenerationId ?? 0L;
        TableFirstVisibleTiming timing = default;
        bool hasPlaylistTiming = viewModel != null && viewModel.TryCreatePlaylistOpenVisibleTiming(sourceGenerationId, viewGenerationId, out timing);
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
            viewModel.TryLogPlaylistOpenVisibleCompleted("custom_onrender", sourceGenerationId, viewGenerationId);
        }
    }

    private async void customTableView_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_sort"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.ExecSort(e.SortMemberPath, e.Direction);
        }).Logging("customTableView_SortRequested");
    }

    private async void customTablePlaylistSummary_SortRequested(object sender, CustomTableSortRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_playlist_summary_sort"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.SortMemberPath))
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.ExecPlaylistSummarySort(e.SortMemberPath, e.Direction);
        }).Logging("customTablePlaylistSummary_SortRequested");
    }

    private void customTableView_SelectionChanged(object sender, CustomTableSelectionChangedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { NowPlayingBMS: null } viewModel)
        {
            if (viewModel.IsPlaylistDetailViewActive)
            {
                NLogWrapper.FileLogger?.Info("custom_table_selection_changed selectedIndex=" + e.SelectedIndex + " selectedCount=" + (e.SelectedRows?.Count ?? 0));
            }
            BMSFile bmsFile = GridRowResolver.GetRealBmsFile(e.SelectedRow);
            if (bmsFile != null)
            {
                _renewBMSPlayerControlInfo(bmsFile);
            }
        }
    }

    private async void customTableView_RowActivated(object sender, CustomTableRowRequestedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_row_activate"))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        BMSFile bmsFile = GridRowResolver.GetRealBmsFile(e.Row);
        if (bmsFile == null)
        {
            return;
        }
        _renewBMSPlayerControlInfo(bmsFile);
        if ((viewModel.NowPlayingBMS == null || viewModel.NowPlayingBMS.status.HasFlag(BMSFile.BMSFileStatus.PAUSE)) && isPanelStateValid(MainWindowViewModel.PanelState.BMS_PLAYER))
        {
            NowPanelState = MainWindowViewModel.PanelState.BMS_PLAYER;
        }
        await Task.Run(delegate
        {
            viewModel.PlayStartBMSfile();
        }).Logging("customTableView_RowActivated");
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
        if (TryFindResource("tableColumnHeaderContextMenu") is not ContextMenu contextMenu)
        {
            return;
        }
        CloseContextMenuIfOpen(contextMenu);
        contextMenu.Tag = null;
        contextMenu.PlacementTarget = customTableView;
        contextMenu.Placement = e.OpenAtMousePosition ? PlacementMode.MousePoint : PlacementMode.Bottom;
        contextMenu.IsOpen = true;
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

    private void customTableView_CellEditBeginning(object sender, CustomTableCellEditBeginningEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("custom_table_cell_edit_beginning"))
        {
            e.Cancel = true;
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || string.IsNullOrWhiteSpace(e.EditPropertyName))
        {
            e.Cancel = true;
            return;
        }
        if (e.Row is PlaylistDetailRow)
        {
            if (!IsCustomTablePlaylistEditableProperty(e.EditPropertyName) || !GridRowResolver.CanEditPlaylistCell(e.Row, e.EditPropertyName))
            {
                e.Cancel = true;
                return;
            }
            viewModel.NotifyPlaylistCellEditStarted();
            return;
        }
        if (string.Equals(e.EditPropertyName, nameof(BMSFile.Folder), StringComparison.Ordinal))
        {
            if (!GridRowResolver.TryGetFolderEditChartOperationTarget(e.Row, GetCurrentChartOperationSourceScope(), out _))
            {
                e.Cancel = true;
                return;
            }
            return;
        }
        BMSFile compatibilityBmsFile = GridRowResolver.GetCompatibilityBmsFile(e.Row, GetCurrentChartOperationSourceScope());
        if (compatibilityBmsFile == null)
        {
            e.Cancel = true;
            return;
        }
        if (string.Equals(e.EditPropertyName, nameof(BMSFile.instl_dst), StringComparison.Ordinal))
        {
            if (!CanEditInstallDestinationInCurrentSection())
            {
                e.Cancel = true;
                return;
            }
            _pendingInstallDestinationEditStates[compatibilityBmsFile] = CapturePendingInstallDestinationEditState(compatibilityBmsFile);
            compatibilityBmsFile.IsInstallDestinationSuggestionPopupOpen = compatibilityBmsFile.HasInstallDestinationSuggestions;
            return;
        }
        e.Cancel = true;
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
        switch (e.Column?.Id)
        {
            case "Link":
                await OpenPlaylistSummaryLinkAsync(playlistSummaryRow).Logging("customTablePlaylistSummary_CellActionRequested_Link");
                break;
            case "IsExternalSync":
                await ApplyPlaylistSummarySyncFromCustomTableAsync(playlistSummaryRow, !(playlistSummaryRow.IsExternalSync)).Logging("customTablePlaylistSummary_CellActionRequested_Sync");
                break;
            case "IsRootFolder":
                await ApplyPlaylistSummaryRootFromCustomTableAsync(playlistSummaryRow, !(playlistSummaryRow.IsRootFolder)).Logging("customTablePlaylistSummary_CellActionRequested_Root");
                break;
        }
    }

    private void customTableView_CellEditEnded(object sender, CustomTableCellEditEndedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(e.EditPropertyName))
            {
                return;
            }
            if (e.Row is PlaylistDetailRow playlistRow)
            {
                if (!e.Commit || !IsCustomTablePlaylistEditableProperty(e.EditPropertyName) || !GridRowResolver.CanEditPlaylistCell(e.Row, e.EditPropertyName))
                {
                    return;
                }
                switch (e.EditPropertyName)
                {
                    case nameof(PlaylistDetailRow.Level):
                        playlistRow.Level = e.Text;
                        break;
                    case nameof(PlaylistDetailRow.Url):
                        if (!Uri.TryCreate(e.Text, UriKind.Absolute, out Uri url))
                        {
                            return;
                        }
                        playlistRow.Url = url;
                        break;
                    case nameof(PlaylistDetailRow.Url_diff):
                        if (!Uri.TryCreate(e.Text, UriKind.Absolute, out Uri urlDiff))
                        {
                            return;
                        }
                        playlistRow.Url_diff = urlDiff;
                        break;
                    case nameof(PlaylistDetailRow.comment):
                        playlistRow.comment = e.Text;
                        break;
                    case nameof(PlaylistDetailRow.memo):
                        playlistRow.memo = e.Text;
                        break;
                    default:
                        return;
                }
                viewModel.SyncPlaylistSourceRowFromEditedViewRow(playlistRow);
                Task.Run(delegate
                {
                    viewModel.CommitPlaylistRow(playlistRow);
                }).Logging("customTableView_CellEditEnded");
                return;
            }
            if (string.Equals(e.EditPropertyName, nameof(BMSFile.Folder), StringComparison.Ordinal))
            {
                if (!GridRowResolver.TryGetFolderEditChartOperationTarget(e.Row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target))
                {
                    return;
                }
                if (!e.Commit)
                {
                    return;
                }
                string newFolder = e.Text;
                Task.Run(delegate
                {
                    try
                    {
                        viewModel.RenameChartFolder(target, newFolder);
                    }
                    finally
                    {
                        RefreshCustomTableViewDisplayAsync();
                    }
                }).Logging("customTableView_CellEditEnded");
                return;
            }
            BMSFile compatibilityBmsFile = GridRowResolver.GetCompatibilityBmsFile(e.Row, GetCurrentChartOperationSourceScope());
            if (compatibilityBmsFile == null)
            {
                return;
            }
            if (string.Equals(e.EditPropertyName, nameof(BMSFile.instl_dst), StringComparison.Ordinal))
            {
                compatibilityBmsFile.IsInstallDestinationSuggestionPopupOpen = false;
                if (!e.Commit)
                {
                    ClearPendingInstallDestinationEditState(compatibilityBmsFile);
                    return;
                }
                if (!CanEditInstallDestinationInCurrentSection())
                {
                    return;
                }
                string destinationDirectory = e.Text;
                PendingInstallDestinationEditState originalState = CaptureOrGetPendingInstallDestinationEditState(compatibilityBmsFile);
                compatibilityBmsFile.instl_dst = destinationDirectory;
                Task.Run(delegate
                {
                    bool succeeded = viewModel.SetPendingInstallDestination(compatibilityBmsFile, destinationDirectory);
                    base.Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (_isClosingOrClosed)
                        {
                            return;
                        }
                        if (!succeeded)
                        {
                            RestorePendingInstallDestinationEditState(compatibilityBmsFile, originalState);
                        }
                        else
                        {
                            ClearPendingInstallDestinationEditState(compatibilityBmsFile);
                        }
                        RefreshCustomTableViewDisplay();
                    }, DispatcherPriority.Background);
                }).Logging("customTableView_CellEditEnded");
            }
        }
        finally
        {
            if (e.Row is PlaylistDetailRow)
            {
                viewModel.NotifyPlaylistCellEditCompleted();
            }
        }
    }

    private static bool IsCustomTablePlaylistEditableProperty(string propertyName)
    {
        return string.Equals(propertyName, nameof(PlaylistDetailRow.Level), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.Url), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.Url_diff), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.comment), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistDetailRow.memo), StringComparison.Ordinal);
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
            if (DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_init_column_settings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
            {
                mainWindowViewModel.LoadColumnSetting();
            }
        }
    }

    public void scrollIntoView()
    {
        customTableView?.ScrollSelectedRowIntoView();
    }

    public void PrepareMainTableSwap()
    {
        var stopwatch = Stopwatch.StartNew();
        customTableView?.PrepareForItemsSourceSwap();
        stopwatch.Stop();
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info("main_table_prepare_swap totalMs=" + stopwatch.ElapsedMilliseconds + " hasCustomTable=" + (customTableView != null));
        }
    }

    /// <summary>
    /// 現在 ViewModel で再生対象になっている BMS ファイルの情報で、プレイヤー UI を更新します。
    /// LivetCallMethodAction から引数なしで呼ばれる entrypoint です。
    /// </summary>
    public void _renewBMSPlayerControlInfo()
    {
        if (base.DataContext is MainWindowViewModel { NowPlayingBMS: not null } mainWindowViewModel)
        {
            _renewBMSPlayerControlInfo(mainWindowViewModel.NowPlayingBMS);
        }
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
    /// BMSPlayerコントロール（プレビュー画像やバナー、曲名などのUI情報）を最新状態に更新します。
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
        return [.. GetSelectedGridRowsSnapshot()
            .Select(delegate (object row)
            {
                return GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target) ? target : null;
            })
            .Where(target => target != null)];
    }

    private List<ChartOperationTarget> GetSelectedChartTargets(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        return [.. GetSelectedChartTargets(isPendingSection).Where(target => HasRequiredCapability(target, capability))];
    }

    private ChartOperationSourceScope GetCurrentChartOperationSourceScope()
    {
        return (base.DataContext as MainWindowViewModel)?.CurrentMainViewChartOperationSourceScope ?? ChartOperationSourceScope.Library;
    }

    private MainWindowViewModel.MainViewOperationSection GetCurrentMainViewOperationSection()
    {
        return (base.DataContext as MainWindowViewModel)?.CurrentMainViewOperationSection ?? MainWindowViewModel.MainViewOperationSection.Library;
    }

    private static bool IsPendingMainViewSection(MainWindowViewModel.MainViewOperationSection section)
    {
        return section == MainWindowViewModel.MainViewOperationSection.InstallPending;
    }

    private static bool IsInstalledMainViewSection(MainWindowViewModel.MainViewOperationSection section)
    {
        return section == MainWindowViewModel.MainViewOperationSection.InstallInstalled;
    }

    private static bool IsPlaylistMainViewSection(MainWindowViewModel.MainViewOperationSection section)
    {
        return section == MainWindowViewModel.MainViewOperationSection.Playlist;
    }

    private static bool IsFullScanMainViewSection(MainWindowViewModel.MainViewOperationSection section)
    {
        return section == MainWindowViewModel.MainViewOperationSection.FullScanCheck;
    }

    private static bool IsChartInfoParseErrorMainViewSection(MainWindowViewModel.MainViewOperationSection section)
    {
        return section == MainWindowViewModel.MainViewOperationSection.ChartInfoParseError;
    }

    private static BMSFile GetChartCompatibilityAdapterFromTarget(ChartOperationTarget target)
    {
        if (target?.Chart == null)
        {
            return null;
        }
        if (target.Chart.CompatibilityBmsFile != null)
        {
            return target.Chart.CompatibilityBmsFile;
        }
        if (target.Chart.BmsonSong != null)
        {
            return PendingChartEntry.CreateFromBmsonSong(target.Chart.BmsonSong);
        }
        return null;
    }

    private static bool HasRequiredCapability(ChartOperationTarget target, ChartOperationCapabilities capability)
    {
        return target != null && (capability == ChartOperationCapabilities.None || target.HasCapability(capability));
    }

    private List<BMSFile> GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        return [.. GetSelectedChartTargets(capability, isPendingSection)
            .Select(GetChartCompatibilityAdapterFromTarget)
            .Where(file => file != null)];
    }

    private List<BMSFile> GetSelectedBmsChartFiles(ChartOperationCapabilities capability, bool isPendingSection = false)
    {
        return [.. GetSelectedChartTargets(isPendingSection)
            .Where(target => HasRequiredCapability(target, capability) && target.Chart.Kind == ChartFileKind.Bms)
            .Select(GetChartCompatibilityAdapterFromTarget)
            .Where(PendingChartEntry.IsBmsChartFile)];
    }

    private List<BMSFile> GetSelectedPendingChartCompatibilityAdapters(ChartOperationCapabilities capability)
    {
        return GetSelectedChartCompatibilityAdapters(capability, isPendingSection: true);
    }

    private List<string> GetSelectedGridHashTargets()
    {
        ChartOperationSourceScope sourceScope = GetCurrentChartOperationSourceScope();
        return [.. GetSelectedGridRowsSnapshot()
            .Where(row => GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target) && target.HasCapability(ChartOperationCapabilities.UseLr2Ir))
            .Select(GridRowResolver.GetHash)
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

    internal static bool ShouldShowChartInfoParseFailureRemovalMenuForTest(bool isChartInfoParseErrorSection, IEnumerable<string> selectedMd5s)
    {
        return isChartInfoParseErrorSection && (selectedMd5s ?? []).Any(md5 => !string.IsNullOrWhiteSpace(md5));
    }

    internal static bool ShouldShowResourceHealthContextMenu(bool isPlaylistContext, IEnumerable<ChartOperationTarget> selectedTargets)
    {
        return !isPlaylistContext
            && (selectedTargets ?? [])
                .Any(target => target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
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
        return new ScoreViewerTarget(hash, target.Chart.CompatibilityBmsFile?.path, target.Chart.Title);
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
        if (row == null)
        {
            usePlaylistMissingContextMenu = false;
            return false;
        }
        usePlaylistMissingContextMenu = GridRowResolver.TryGetChartOperationTarget(row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target)
            ? target.IsPlaylistMissing
            : GridRowResolver.IsPlaylistRow(row) && GridRowResolver.GetRealBmsFile(row) == null;
        string resourceKey = usePlaylistMissingContextMenu ? "tableContextMenuPlaylistMissing" : "tableContextMenu";
        if (TryFindResource(resourceKey) is not ContextMenu foundContextMenu)
        {
            return false;
        }
        contextMenu = foundContextMenu;
        NLogWrapper.FileLogger?.Info(logPrefix + " rowType=" + row?.GetType().FullName + " missing=" + usePlaylistMissingContextMenu + " resourceKey=" + resourceKey);
        return true;
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
                var memoryStream = new MemoryStream(File.ReadAllBytes(text));
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
                var imageBrush = new ImageBrush();
                var memoryStream2 = new MemoryStream(File.ReadAllBytes(text2));
                var imageSource = new WriteableBitmap(BitmapFrame.Create(memoryStream2));
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
            viewModel.RefreshPlaylistSummaryKeywordSearchSuggestions(textBox.Text, textBox.CaretIndex, forceHistory);
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
            viewModel.CommitPlaylistSummaryKeywordSearchHistory(textBox.Text);
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
            && (isPlaylistSummary ? viewModel.IsPlaylistSummaryKeywordSearchSuggestionPopupOpen : viewModel.IsKeywordSearchSuggestionPopupOpen);
    }

    private void CloseKeywordSearchSuggestions(bool isPlaylistSummary)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (isPlaylistSummary)
        {
            viewModel.ClosePlaylistSummaryKeywordSearchSuggestions();
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
    private PendingInstallDestinationEditState CapturePendingInstallDestinationEditState(BMSFile bmsFile)
    {
        if (bmsFile == null)
        {
            return null;
        }
        return new PendingInstallDestinationEditState
        {
            InstallDestination = bmsFile.instl_dst,
            InstallDestinationTitle = bmsFile.InstallDestinationTitle,
            InstallDestinationArtist = bmsFile.InstallDestinationArtist,
            Warnings = bmsFile.Warnings.ToStructuredList(),
            Suggestions = [.. (bmsFile.InstallDestinationSuggestions ?? [])]
        };
    }

    private PendingInstallDestinationEditState CaptureOrGetPendingInstallDestinationEditState(BMSFile bmsFile)
    {
        if (bmsFile == null)
        {
            return null;
        }
        if (!_pendingInstallDestinationEditStates.TryGetValue(bmsFile, out PendingInstallDestinationEditState state) || state == null)
        {
            state = CapturePendingInstallDestinationEditState(bmsFile);
            _pendingInstallDestinationEditStates[bmsFile] = state;
        }
        return state;
    }

    private void ClearPendingInstallDestinationEditState(BMSFile bmsFile)
    {
        if (bmsFile != null)
        {
            _pendingInstallDestinationEditStates.Remove(bmsFile);
        }
    }

    private void RestorePendingInstallDestinationEditState(BMSFile bmsFile, PendingInstallDestinationEditState state)
    {
        if (bmsFile == null || state == null)
        {
            return;
        }
        bmsFile.instl_dst = state.InstallDestination;
        bmsFile.InstallDestinationTitle = state.InstallDestinationTitle;
        bmsFile.InstallDestinationArtist = state.InstallDestinationArtist;
        bmsFile.ReplaceStructuredWarnings(state.Warnings ?? []);
        bmsFile.InstallDestinationSuggestions = state.Suggestions ?? [];
        bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
        ClearPendingInstallDestinationEditState(bmsFile);
    }
    private bool CanEditInstallDestinationInCurrentSection()
    {
        MainWindowViewModel.MainViewOperationSection section = GetCurrentMainViewOperationSection();
        return IsPendingMainViewSection(section) || IsFullScanMainViewSection(section);
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
        Uri url = isDiffUrl ? GridRowResolver.GetUrlDiff(row) : GridRowResolver.GetUrl(row);
        if (url == null || !url.IsAbsoluteUri)
        {
            return;
        }
        if (!Settings.Default.SkipInitFileCheck && Settings.Default.AutoInstall)
        {
            try
            {
                string urlText = url.ToString();
                if (!urlText.EndsWith("/") && !urlText.EndsWith(".htm") && !urlText.EndsWith(".html"))
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
        if (base.DataContext is not MainWindowViewModel viewModel || selectedItem == null)
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
            while (ancestor != null && ancestor.DataContext is not BMSTable)
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
        MainWindowViewModel.PlaylistFilterType type = MainWindowViewModel.PlaylistFilterType.PlaylistFilter;
        string folderName;
        if (e.Source is TreeViewItem treeViewItem2)
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
        if (e.Source is TreeViewItem treeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.DirectoryFilter, treeViewItem.Header.ToString());
            e.Handled = true;
        }
    }

    private void artistFolderSelect(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("tree_artist_folder_select"))
        {
            e.Handled = true;
            return;
        }
        if (e.Source is TreeViewItem treeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.ArtistFilter, treeViewItem.Header.ToString());
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
        if (e.OriginalSource is TreeViewItem)
        {
            (base.DataContext as MainWindowViewModel).ExecFolderFilter(MainWindowViewModel.FolderFilterType.FilterNone);
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
        bool selected = TrySelectPlaylistTreeItem(selectionTarget, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable);
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
        return treeViewItemPlaylist.Items.OfType<BMSTable>().FirstOrDefault(playlistTable => playlistTable != null && string.Equals(playlistTable.name, playlistName, StringComparison.Ordinal));
    }

    private async void playlistSummaryLinkClick(object sender, RoutedEventArgs e)
    {
        if (!(sender is Button { DataContext: PlaylistSummaryRow playlistSummaryRow }) || playlistSummaryRow.LinkUri == null)
        {
            return;
        }
        await OpenPlaylistSummaryLinkAsync(playlistSummaryRow).Logging("playlistSummaryLinkClick");
    }

    private Task OpenPlaylistSummaryLinkAsync(PlaylistSummaryRow playlistSummaryRow)
    {
        if (playlistSummaryRow?.LinkUri == null)
        {
            return Task.CompletedTask;
        }
        return Task.Run(delegate
        {
            try
            {
                Process.Start(playlistSummaryRow.LinkUri.ToString());
            }
            catch
            {
            }
        });
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
        var confirmationMessage = new ConfirmationMessage((!flag) ? ("同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？") : ("同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？"), "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
        (base.DataContext as MainWindowViewModel)?.Messenger.Raise(confirmationMessage);
        if (!confirmationMessage.Response.HasValue || !confirmationMessage.Response.Value)
        {
            checkBox.IsChecked = !flag;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, flag, null);
            }).Logging("playlistSummarySyncCheckBoxClick");
        }
        e.Handled = true;
    }

    private async Task ApplyPlaylistSummarySyncFromCustomTableAsync(PlaylistSummaryRow playlistSummaryRow, bool flag)
    {
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        var confirmationMessage = new ConfirmationMessage((!flag) ? ("同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？") : ("同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？"), "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
        (base.DataContext as MainWindowViewModel)?.Messenger.Raise(confirmationMessage);
        if (!confirmationMessage.Response.HasValue || !confirmationMessage.Response.Value)
        {
            customTablePlaylistSummary?.RefreshDisplay();
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, flag, null);
            });
        }
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
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, null, flag);
            }).Logging("playlistSummaryRootCheckBoxClick");
        }
        e.Handled = true;
    }

    private async Task ApplyPlaylistSummaryRootFromCustomTableAsync(PlaylistSummaryRow playlistSummaryRow, bool flag)
    {
        List<PlaylistSummaryRow> selectedPlaylistSummaryRows = getSelectedPlaylistSummaryRows(playlistSummaryRow);
        if (selectedPlaylistSummaryRows.Count == 0)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.ApplyPlaylistSummaryFlags(selectedPlaylistSummaryRows, null, flag);
            });
        }
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
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable)
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
        if (DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_playlist, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
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
        if (ShouldBlockStartupUiInteraction("tree_full_scan_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.FileMissingFilter);
            }).Logging("fullScanCheckFolderSelect");
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
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.FullScanAllChartsFilter);
            }).Logging("fullScanAllChartsFolderSelect");
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
            await Task.Run(delegate
            {
                viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.FileMissingIgnoredFilter);
            }).Logging("fullScanCheckIgnoredFolderSelect");
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
            if (treeViewItem.DataContext is not List<BMSFile>)
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
        if (ShouldBlockStartupUiInteraction("tree_garbled_check_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
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
        if (ShouldBlockStartupUiInteraction("tree_garble_fixed_select"))
        {
            e.Handled = true;
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && sender is TreeViewItem treeRoot)
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
        if (ShouldBlockStartupUiInteraction("tree_unregistered_to_db_select"))
        {
            e.Handled = true;
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.UnregisteredFilter);
        }).Logging("unregisteredToDBFolderSelect");
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
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.ZeroNoteFilter);
        }).Logging("zeronoteFolderSelect");
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
        await Task.Run(delegate
        {
            viewModel.ExecMaintenanceFilter(MainWindowViewModel.MaintenanceFilterType.ChartInfoParseErrorFilter);
        }).Logging("chartInfoParseErrorFolderSelect");
    }

    private void treeViewZeroNoteContextMenuItemRecheckClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            Task.Run(delegate
            {
                viewModel.RecheckZeroNoteWarnings();
            }).Logging("treeViewZeroNoteContextMenuItemRecheckClick");
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
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter);
            }).Logging("newlyInstalledFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        if (treeViewItem.DataContext is ChartPackage package)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter, package);
            }).Logging("newlyInstalledFolderSelect");
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
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
            }).Logging("pendingInstallFolderSelect");
            treeRoot.IsExpanded = true;
            return;
        }
        if (treeViewItem.DataContext is ChartPackage package)
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
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem || viewModel.IsWriteLockHeldBMSTablesInitializeMin || viewModel.IsWriteLockHeldBMSTables || viewModel.IsWriteLockHeldAnyBMSTable)
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
    private void treeViewPlaylistRootContextMenuItemLoadPlaylistCollectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && menuItem.DataContext is BMSTableSimple dataContext && !(dataContext.url == null) && !viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            viewModel.EnqueueExternalPlaylistBMSTableImport(dataContext.url);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニュー「Walkure/難易度表を読み込む」に関するメニュー項目（各難易度表単位）のアクション。
    /// MenuItemのTagプロパティに格納されたURLへアクセスし、プレイリスト情報を非同期で追加・登録します。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadWalkureTableClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel && sender is MenuItem menuItem && !viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            var uri = new Uri((string)menuItem.Tag);
            viewModel.EnqueueExternalPlaylistBMSTableImport(uri);
        }
    }

    /// <summary>
    /// プレイリストルートのコンテキストメニューから「Walkureのおすすめフォルダ」関連のテーブル読み込みが選択された場合の処理。
    /// LR2IDの設定状況のチェックや、更新モード/閲覧モードに応じたユーザー確認ダイアログを挟んだ後、非同期で登録処理へ進みます。
    /// </summary>
    private void treeViewPlaylistRootContextMenuItemLoadWalkureTableRecommendedClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem || viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            return;
        }
        var uri = new Uri((string)menuItem.Tag);
        if (viewModel.LR2ID == 0)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_error, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return;
        }
        if (Regex.Match((string)menuItem.Tag, "mode=update").Success)
        {
            if (DispatcherMessageBox.Show(Window.GetWindow(this), "LR2ID: " + viewModel.LR2ID + BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_update_mode, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) == MessageBoxResult.Cancel)
            {
                return;
            }
        }
        else if (DispatcherMessageBox.Show(Window.GetWindow(this), "LR2ID: " + viewModel.LR2ID + BeMusicSeeker.Properties.Resources.Msg_load_recommended_tables_readonly_mode, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK) == MessageBoxResult.Cancel)
        {
            return;
        }
        viewModel.EnqueueExternalPlaylistBMSTableImport(uri);
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
        if (base.DataContext is not MainWindowViewModel viewModel || !(sender is MenuItem { DataContext: BMSTable table }))
        {
            return;
        }
        await viewModel.ResyncPlaylistsAsync([table]).Logging("treeViewPlaylistTableContextMenuItemReloadClick");
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
        bool restored = TrySelectPlaylistTreeItem(selectionTarget, out bool usedVirtualizationFallback, out bool realizeByIndexAvailable);
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
            BMSTable byId = source.FirstOrDefault(t => t != null && t.playlist_id.HasValue && t.playlist_id.Value == tableBeforeReload.playlist_id.Value);
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
        return source.FirstOrDefault(t => t != null && string.Equals(t.name, tableBeforeReload.name, StringComparison.Ordinal));
    }

    /// <summary>
    /// テーブル階層コンテキストメニュー「配布ページを開く」実行時の処理。
    /// BMSTableに設定されたURL (Page_url または Header_url) を標準ブラウザ等で開きます。
    /// 特殊スキーム（Walkure難易度表等）の場合は専用のURLへ変換してブラウザ起動します。
    /// </summary>
    private void treeViewPlaylistTableContextMenuItemOpenPageURIClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel mainWindowViewModel || !(sender is MenuItem { DataContext: BMSTable dataContext }))
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
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is BMSTable bmsTable && !bmsTable.is_external_sync)
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
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        var fileDialogHeader = new SaveFileDialog();
        var fileDialogData = new SaveFileDialog();
        fileDialogHeader.Title = BeMusicSeeker.Properties.Resources.Save_header_file;
        fileDialogData.Title = BeMusicSeeker.Properties.Resources.Save_data_file;
        fileDialogHeader.FileName = ((!string.IsNullOrWhiteSpace(bmsTable.header_url)) ? Path.GetFileName(bmsTable.Header_url.ToString()) : "header.json");
        fileDialogData.FileName = ((!string.IsNullOrWhiteSpace(bmsTable.data_url)) ? Path.GetFileName(bmsTable.Data_url.ToString()) : "data.json");
        fileDialogHeader.DefaultExt = ".json";
        fileDialogData.DefaultExt = ".json";
        fileDialogHeader.AddExtension = true;
        fileDialogData.AddExtension = true;
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
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(bmsTable.page_url) && bmsTable.page_url.StartsWith("bmseeker:table.recommended"))
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_override_level_error_recommended, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else if (DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_override_level_warning, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
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
        if (base.DataContext is not MainWindowViewModel viewModel || sender is not MenuItem menuItem)
        {
            return;
        }
        if (menuItem.DataContext is not BMSTable bmsTable || DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_playlist, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
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
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && sender is MenuItem menuItem && menuItem.DataContext is BMSTable table && !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable)
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
    private async void treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
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
        if (DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_folder, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
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
        if (base.DataContext is not MainWindowViewModel)
        {
            return;
        }
        MainWindowViewModel.SettingDialogViewModel viewModel = (base.DataContext as MainWindowViewModel).settingDialog;
        if (Directory.Exists(path) && DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_unregister_root_folder, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            viewModel.RemoveBMSDirectoryFromRootFolderAndSave(path);
        }
    }

    private void treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        string path = placementTarget.Header.ToString();
        if (base.DataContext is MainWindowViewModel viewModel && Directory.Exists(path) && DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_rename_folders, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                try
                {
                    viewModel.AutoRenameAllChartFolders(path);
                }
                finally
                {
                    RefreshCustomTableViewDisplayAsync();
                }
            }).Logging("treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick");
        }
    }

    private void treeViewInstalledContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel && DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_all_installed, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.RemoveInstalledPackageRecordsAll();
            }).Logging("treeViewInstalledContextMenuClearAllClick");
        }
    }

    private void treeViewInstallPendingContextMenuClearAllClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel && DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_all_pendings, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.RemovePendingPackagesAll();
            }).Logging("treeViewInstallPendingContextMenuClearAllClick");
        }
    }

    private async void treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<ChartPackage> list = viewModel.GetPendingPackagesContainingOnlyInstalledCharts();
        if (list.Count == 0)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_delete_pending_installed_only_packages_permanently, list.Count);
        if (DispatcherMessageBox.Show(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
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
        var cancellationTokenSource = new CancellationTokenSource();
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
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<BMSFile> list = viewModel.GetPendingBmsFormatChartFilesSnapshot();
        if (list.Count == 0)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_charts, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_rename_pending_zero_note_to_invalid_ext, list.Count);
        if (DispatcherMessageBox.Show(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        e.Handled = true;
        if (list.Count == 1)
        {
            await Task.Run(delegate
            {
                viewModel.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(list, CancellationToken.None, null);
            }).Logging("treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick");
            return;
        }
        var cancellationTokenSource = new CancellationTokenSource();
        int processedCount = 0;
        int total = list.Count;
        Task task = Task.Run(delegate
        {
            viewModel.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(list, cancellationTokenSource.Token, delegate
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
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        List<ChartPackage> list = viewModel.GetPendingPackagesContainingOnlyInstalledCharts();
        if (list.Count == 0)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Warn_no_pending_installed_only_packages, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return;
        }
        string messageBoxText = string.Format(BeMusicSeeker.Properties.Resources.Msg_overwrite_pending_installed_only_packages_resources, list.Count);
        if (DispatcherMessageBox.Show(Window.GetWindow(this), messageBoxText, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
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
            var cancellationTokenSource = new CancellationTokenSource();
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
            DispatcherMessageBox.Show(Window.GetWindow(this), string.Format(BeMusicSeeker.Properties.Resources.Warn_overwrite_pending_installed_only_packages_summary, pendingInstalledOnlyResourceOverwriteResult.Requested, pendingInstalledOnlyResourceOverwriteResult.Processed, pendingInstalledOnlyResourceOverwriteResult.SucceededInstall, pendingInstalledOnlyResourceOverwriteResult.SucceededCleanupOnly, pendingInstalledOnlyResourceOverwriteResult.SkippedNotPending, pendingInstalledOnlyResourceOverwriteResult.SkippedMissingInstlDst, pendingInstalledOnlyResourceOverwriteResult.SkippedMultiDestination, pendingInstalledOnlyResourceOverwriteResult.SkippedNoComponentTarget, pendingInstalledOnlyResourceOverwriteResult.Failed, pendingInstalledOnlyResourceOverwriteResult.Canceled), BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
    }

    private void treeViewInstallPackageContextMenuOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem { DataContext: ChartPackage dataContext } } }))
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
    /// 「インストール保留中」などのリストから対象のパッケージ (ChartPackage) を一つ取り除きます。
    /// </summary>
    private async void treeViewInstallPackageContextMenuClearFolderClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: TreeViewItem placementTarget } }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_clear_pendings + Environment.NewLine + Environment.NewLine + ((pkg.ChartFiles.Count > 1) ? pkg.ChartFiles[0].title : pkg.ChartFiles[0].Title), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuClearFolderClick");
        await Task.Run(delegate
        {
            viewModel.RemovePendingPackages([pkg]);
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
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        SelectNextSiblingOrRoot(newlyInstalledTreeViewItem, pkg, "treeViewInstalledFolderContextMenuClearFolderClick");
        await Task.Run(delegate
        {
            viewModel.RemoveInstalledPackageRecords([pkg]);
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
            Task.Run(delegate
            {
                viewModel.ClearInstallDestinationForPendingPackages([pkg]);
            }).Logging("treeViewInstallPackageContextMenuRemoveInstallDestinationClick");
        }
    }

    private async void treeViewInstallPackageContextMenuForceInstallClick(object sender, RoutedEventArgs e)
    {
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
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuForceInstallClick");
        await Task.Run(delegate
        {
            viewModel.ForceInstallPendingPackages([pkg]);
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
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel || (Settings.Default.ShowDiffBMSInstallConfirmMsg && DispatcherMessageBox.Show(Window.GetWindow(this), GetManualInstallConfirmationMessage(), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) != MessageBoxResult.OK))
        {
            return;
        }
        SelectNextSiblingOrRoot(treeViewItemInstallPending, pkg, "treeViewInstallPackageContextMenuManualInstallClick");
        await Task.Run(delegate
        {
            viewModel.ManualInstallPendingPackages([pkg]);
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
            Task.Run(delegate
            {
                viewModel.SearchInstallDestinationForPendingPackages([pkg]);
            }).Logging("treeViewInstallPackageContextMenuSearchInstallationDirectoryClick");
        }
    }

    private void treeViewInstallPackageContextMenuSearchMergeDestinationClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menuItem || !((menuItem.Parent as MenuItem).Parent is ContextMenu { PlacementTarget: TreeViewItem placementTarget }))
        {
            return;
        }
        if (placementTarget.DataContext is not ChartPackage pkg || !ConfirmMergeDestinationSearch())
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            Task.Run(delegate
            {
                viewModel.SearchMergeDestinationForPendingPackages([pkg]);
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
        var duplicateGroup = groupTreeItem?.DataContext as DuplicateGroup;
        ExecuteDuplicateFolderMerge(srcPath, dstPath, duplicateGroup);
    }

    /// <summary>
    /// 重複フォルダのマージ処理を実行する共通メソッド。
    /// 確認ダイアログ → マージ実行 → マージ後のグループ自動選択を行う。
    /// </summary>
    private void ExecuteDuplicateFolderMerge(string srcPath, string dstPath, DuplicateGroup duplicateGroup)
    {
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(srcPath) || string.IsNullOrWhiteSpace(dstPath))
        {
            return;
        }

        // 確認ダイアログ
        if (DispatcherMessageBox.Show(Window.GetWindow(this),
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
            viewModel.MergeChartDirectory(srcPath, dstPath);

            // マージ完了ログ（将来のステータスバー通知に備える）
            NLogWrapper.FileLogger?.Info(string.Format(
                BeMusicSeeker.Properties.Resources.Msg_merge_bms_completed, srcFolderName, dstFolderName));

        }).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                return;
            }
            // マージ後にDuplicateChartGroupsの更新を待ってからツリーで自動選択を試みる
            WaitForDuplicateListUpdateAndSelect(_pendingDuplicateGroupHeader, viewModel);
        }, TaskScheduler.FromCurrentSynchronizationContext()).Logging("ExecuteDuplicateFolderMerge");
    }

    /// <summary>
    /// DuplicateChartGroups更新タイミングの競合を吸収しつつ、該当グループを自動選択する。
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
                string lastReason = string.Empty;
                var priorities = new DispatcherPriority[3]
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
        VirtualizingStackPanel panel = WPFUtil.FindVisualChild<VirtualizingStackPanel>(duplicateRootItem);
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
        if (duplicateRootItem.ItemContainerGenerator.ContainerFromIndex(targetIndex) is TreeViewItem targetItem)
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

        // フォルダパスに一致するBMSFileをハッシュでグループ化
        var filesInFolder = duplicateGroup.Files
            .Where(f => f.path.StartsWith(folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filesInFolder.Count == 0)
        {
            return;
        }

        // 主キー(hash)でグループ化し、各グループで削除対象を決定
        var deletionList = new List<BMSFile>();
        foreach (var hashGroup in filesInFolder
            .Select(f => new { File = f, LookupHash = PendingChartEntry.GetPrimaryLookupHash(f) })
            .Where(x => !string.IsNullOrWhiteSpace(x.LookupHash))
            .GroupBy(x => x.LookupHash, StringComparer.OrdinalIgnoreCase))
        {
            var grouped = hashGroup.Select(x => x.File).ToList();
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
        if (DispatcherMessageBox.Show(Window.GetWindow(this),
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
            viewModel.RemoveChartFiles(deletionList);

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
        if (new Task[4] { getSongInfoCacheTask, changeSubmenuOpenVideoTask, changeSubmenuOpenDocumentTask, changeSubmenuOpenSearchLinkTask }.Where(t => t != null).Any(t => !t.IsCompleted) && tableContextMenuTaskTokenSource != null)
        {
            tableContextMenuTaskTokenSource.Cancel();
            NLogWrapper.DebuggerLogger?.Trace("Cancel data grid context menu async tasks");
        }
    }

    private void initContextMenuTasks()
    {
        songInfoCache = null;
        tableContextMenuTaskTokenSource = new CancellationTokenSource();
        getSongInfoCacheTask = null;
        changeSubmenuOpenVideoTask = null;
        changeSubmenuOpenDocumentTask = null;
        changeSubmenuOpenSearchLinkTask = null;
    }

    private void tableContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_opened"))
        {
            e.Handled = true;
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
        MainWindowViewModel.MainViewOperationSection effectiveSection = mainWindowViewModel.CurrentMainViewOperationSection;
        bool isPendingSelected = IsPendingMainViewSection(effectiveSection);
        bool isInstalledSelected = IsInstalledMainViewSection(effectiveSection);
        bool isInstallListSelected = isPendingSelected || isInstalledSelected;
        bool isPlaylistSelected = IsPlaylistMainViewSection(effectiveSection);
        bool isPlaylistContext = isPlaylistSelected || isPlaylistRow;
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
        string chartPath = rowTarget?.Chart?.Path;
        BMSFile bmsFile = rowTarget?.Chart?.CompatibilityBmsFile;
        List<ChartOperationTarget> selectedTargets = GetSelectedChartTargets(isPendingSelected);
        if (rowTarget != null && selectedTargets.Count == 0)
        {
            selectedTargets.Add(rowTarget);
        }
        List<BMSFile> list = [.. selectedTargets.Select(GetChartCompatibilityAdapterFromTarget).Where(file => file != null)];
        bool isBmsonContextRow = rowTarget?.Chart.Kind == ChartFileKind.Bmson;
        bool hasBmsonSelection = selectedTargets.Any(target => target.Chart.Kind == ChartFileKind.Bmson);
        bool hasBmsSelection = selectedTargets.Any(target => target.Chart.Kind == ChartFileKind.Bms);
        string rowHash = rowTarget?.Chart?.Md5 ?? GridRowResolver.GetHash(row);
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
                case "tableContextMenuItemOpenVideo":
                    menuItem5 = item as MenuItem;
                    break;
                case "tableContextMenuItemSearchLink":
                    menuItem6 = item as MenuItem;
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
            if (!string.IsNullOrWhiteSpace(chartPath) && File.Exists(chartPath))
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
                        List<string> list2 = [.. Directory.EnumerateFiles(directoryNameSimple, "*.txt"), .. Directory.EnumerateFiles(directoryNameSimple, "*.htm?")];
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
        bool hasScoreViewerTarget = selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.UseScoreViewer));
        if (menuItem18 != null && list.Count > 1)
        {
            menuItem18.Header = BeMusicSeeker.Properties.Resources.Register_chart_with_viewer;
            flag = (menuItem18.IsEnabled = selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.UseScoreViewer) && !string.IsNullOrWhiteSpace(target.Chart.Path) && File.Exists(target.Chart.Path)));
        }
        else if (menuItem18 != null)
        {
            menuItem18.Header = BeMusicSeeker.Properties.Resources.Open_chart_viewer;
            flag = selectedTargets.Count == 1
                && selectedTargets[0].HasCapability(ChartOperationCapabilities.UseScoreViewer)
                && !string.IsNullOrWhiteSpace(selectedTargets[0].Chart.Path)
                && File.Exists(selectedTargets[0].Chart.Path);
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
            bool canOpenLr2Ir = rowTarget != null && rowTarget.HasCapability(ChartOperationCapabilities.UseLr2Ir);
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
            bool hasRankingTarget = selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.UpdateRanking));
            menuItem7.Visibility = hasRankingTarget ? Visibility.Visible : Visibility.Collapsed;
            if (mainWindowViewModel.LR2ID == 0 || !hasRankingTarget)
            {
                menuItem7.IsEnabled = false;
            }
            else
            {
                menuItem7.IsEnabled = true;
            }
        }
        MenuItem menuItemDeleteInstallPackages = contextMenu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "tableContextMenuItemDeleteInstallPackages");
        List<string> selectedChartInfoParseFailureMd5s = GetSelectedChartInfoParseFailureMd5s();
        if (menuItemRemoveChartInfoParseFailure != null)
        {
            bool canRemoveChartInfoParseFailure = ShouldShowChartInfoParseFailureRemovalMenuForTest(IsChartInfoParseErrorMainViewSection(effectiveSection), selectedChartInfoParseFailureMd5s);
            menuItemRemoveChartInfoParseFailure.Visibility = canRemoveChartInfoParseFailure ? Visibility.Visible : Visibility.Collapsed;
            menuItemRemoveChartInfoParseFailure.IsEnabled = canRemoveChartInfoParseFailure;
        }
        bool isNotOwnedPlaylistRow = rowTarget?.IsPlaylistMissing == true;
        NLogWrapper.FileLogger?.Info("playlist_context_menu rowType=" + row?.GetType().FullName + " isPlaylistRow=" + isPlaylistRow + " isPlaylistContext=" + isPlaylistContext + " isNotOwned=" + isNotOwnedPlaylistRow + " section=" + effectiveSection + " treeSection=" + _currentTreeSelectionSection + " sourceScope=" + sourceScope + " kind=" + rowTarget?.Chart.Kind + " path=" + (chartPath ?? string.Empty));
        if (menuItemOpenInstallDestination != null)
        {
            bool canOpenInstallDestination = rowTarget != null && rowTarget.HasCapability(ChartOperationCapabilities.UpdateInstallDestination) && !isPlaylistRow;
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
            bool isFullScanMenuVisible = ShouldShowResourceHealthContextMenu(isPlaylistContext, selectedTargets);
            menuItem10.Visibility = ((!isFullScanMenuVisible) ? Visibility.Collapsed : Visibility.Visible);
            menuItem10.IsEnabled = isFullScanMenuVisible;
        }
        if (menuItem13 != null)
        {
            bool canMoveSelectedFiles = !isPendingSelected;
            menuItem13.Visibility = ((!canMoveSelectedFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem13.IsEnabled = canMoveSelectedFiles && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.MoveInLibrary) && !string.IsNullOrWhiteSpace(target.Chart.Path) && File.Exists(target.Chart.Path));
        }
        if (menuItem14 != null)
        {
            menuItem14.Visibility = ((!isPlaylistContext) ? Visibility.Collapsed : Visibility.Visible);
            menuItem14.IsEnabled = isPlaylistContext;
        }
        if (menuItem15 != null)
        {
            bool canDeleteFiles = selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary))
                || (isPendingSelected && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination)));
            menuItem15.Visibility = ((!canDeleteFiles) ? Visibility.Collapsed : Visibility.Visible);
            menuItem15.IsEnabled = canDeleteFiles;
            if (menuItemRenameInvalidExt != null)
            {
                bool canRenameInvalidExt = canDeleteFiles && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
                menuItemRenameInvalidExt.Visibility = canRenameInvalidExt ? Visibility.Visible : Visibility.Collapsed;
                menuItemRenameInvalidExt.IsEnabled = canRenameInvalidExt;
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
            bool canAutoRenameFolders = !isPlaylistContext && !isPendingSelected && (hasBmsSelection || hasBmsonSelection);
            menuItem16.Visibility = ((!canAutoRenameFolders) ? Visibility.Collapsed : Visibility.Visible);
            menuItem16.IsEnabled = canAutoRenameFolders;
        }
        if (menuItem17 != null)
        {
            bool canFixEncoding = !isPlaylistContext && hasBmsSelection;
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
            bool hasResourceHealthTarget = selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
            menuItem11.Visibility = ((!isSelected) ? Visibility.Collapsed : Visibility.Visible);
            menuItem11.IsEnabled = isSelected && hasResourceHealthTarget;
        }
        if (menuItem12 != null)
        {
            bool isSelected2 = treeViewItemFullScanCheckIgnored.IsSelected;
            bool hasResourceHealthTarget = selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
            menuItem12.Visibility = ((!isSelected2) ? Visibility.Collapsed : Visibility.Visible);
            menuItem12.IsEnabled = isSelected2 && hasResourceHealthTarget;
        }
        if (menuItemFullScanAllCharts != null)
        {
            bool canRescanAllCharts = menuItem10?.Visibility == Visibility.Visible
                && !mainWindowViewModel.IsMaintenanceRescanProgressActive;
            menuItemFullScanAllCharts.Visibility = Visibility.Visible;
            menuItemFullScanAllCharts.IsEnabled = canRescanAllCharts;
        }
        if (menuItem19 != null && separator2 != null)
        {
            bool canConvertToAudio = !isPendingSelected;
            Separator convertSeparator = separator2;
            Visibility visibility = (menuItem19.Visibility = ((!canConvertToAudio) ? Visibility.Collapsed : Visibility.Visible));
            convertSeparator.Visibility = visibility;
            Separator convertSeparator2 = separator2;
            bool isEnabled = (menuItem19.IsEnabled = canConvertToAudio && selectedTargets.Any(target => target.HasCapability(ChartOperationCapabilities.ConvertToAudio) && !string.IsNullOrWhiteSpace(target.Chart.Path) && File.Exists(target.Chart.Path)));
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
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = rowUrl != null && rowUrl.IsAbsoluteUri;
                    break;
                case "tableContextMenuItemOpenURLdiff":
                    item.Visibility = Visibility.Visible;
                    item.IsEnabled = rowUrlDiff != null && rowUrlDiff.IsAbsoluteUri;
                    break;
                case "tableContextMenuItemOpenVideo":
                case "tableContextMenuItemSearchLink":
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
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && mainWindowViewModel.TryGetInstalledDirectoryByHash(bmsFile.hash, out string installDir2))
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
        foreach (BMSFile item in pkg.ChartFiles.Where(f => f != null))
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

    private void tableContextMenuItemOpenInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is not MainWindowViewModel || !IsPendingMainViewSection(GetCurrentMainViewOperationSection()))
        {
            return;
        }
        List<BMSFile> list = GetSelectedPendingChartCompatibilityAdapters(ChartOperationCapabilities.UpdateInstallDestination);
        if (list.Count == 0)
        {
            return;
        }
        if (list.Count > 1)
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_open_install_destination_multiple_selected, BeMusicSeeker.Properties.Resources.Information, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
        }
        if (!TryResolveInstallDestination(list[0], out string installDir, out string reason))
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), reason, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
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
            DispatcherMessageBox.Show(Window.GetWindow(this), reason, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
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
        string rowHash = target.Chart.Md5;
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

    private void tableContextMenuItemOpenMochaClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out object row))
        {
            OpenRepositoryUrlForRow(row, GetMochaSongUrl);
        }
    }

    private void tableContextMenuItemOpenMinIRClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out object row))
        {
            OpenRepositoryUrlForRow(row, GetMinIrSongUrl);
        }
    }

    private void tableContextMenuItemOpenURLClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out object item))
        {
            Uri url = GridRowResolver.GetUrl(item);
            if (url != null && url.IsAbsoluteUri)
            {
                Process.Start(url.ToString());
            }
        }
    }

    private void tableContextMenuItemOpenURLdiffClick(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out object item))
        {
            Uri urlDiff = GridRowResolver.GetUrlDiff(item);
            if (urlDiff != null && urlDiff.IsAbsoluteUri)
            {
                Process.Start(urlDiff.ToString());
            }
        }
    }

    private void tableContextMenuItemOpenDocumentFileClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { DataContext: string dataContext } && File.Exists(dataContext))
        {
            Process.Start(dataContext);
        }
    }

    private void tableContextMenuOpenVideoSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_open_video"))
        {
            e.Handled = true;
            return;
        }
        if (sender is not MenuItem menuItem || !TryGetContextMenuRow(sender, out object row))
        {
            return;
        }
        if (!GridRowResolver.IsPlaylistRow(row))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
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
                case "tableContextMenuItemOpenVideoSubmenuStatus":
                    menuItemOpenVideoSubmenuStatus = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenVideoSubmenuYouTube":
                    menuItemOpenVideoSubmenuYouTube = item as MenuItem;
                    break;
                case "tableContextMenuItemOpenVideoSubmenuNiconico":
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
            CancellationToken token = tableContextMenuTaskTokenSource.Token;
            getSongInfoCacheTask ??= Task.Run(delegate
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
                            if (!token.IsCancellationRequested && !_isClosingOrClosed)
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
                            if (!token.IsCancellationRequested && !_isClosingOrClosed)
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
                            if (!token.IsCancellationRequested && !_isClosingOrClosed)
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
                        if (!token.IsCancellationRequested && !_isClosingOrClosed)
                        {
                            menuItemOpenVideoSubmenuStatus.Header = BeMusicSeeker.Properties.Resources.Unregistered;
                        }
                    });
                }
            }
        }, tableContextMenuTaskTokenSource.Token).Logging("tableContextMenuOpenVideoSubmenuOpened");
    }

    private void tableContextMenuItemOpenVideoSubmenuClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menuItem || base.DataContext is not MainWindowViewModel mainWindowViewModel || webBrowser == null)
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
        if (!TryGetContextMenuRow(e.Source, out object row))
        {
            return;
        }
        gridBMSPlayerControlsTitleForMovie.Text = GridRowResolver.GetDisplayTitle(row);
        gridBMSPlayerControlsSubtitleForMovie.Text = GridRowResolver.GetDisplaySubtitle(row);
        gridBMSPlayerControlsArtistForMovie.Text = GridRowResolver.GetDisplayArtist(row);
    }

    private void songInfoCacheToUrlLists(BMSLibrary.IRSongInfo info, Uri original, Uri diff, out List<Uri> urls, out List<Uri> urls_diff)
    {
        if (songInfoCache != null)
        {
            urls = [.. (from s in songInfoCache.url.Split(' ')
                    where !string.IsNullOrWhiteSpace(s)
                    let normalized = NormalizeDownloadUrlString(s)
                    where !string.IsNullOrWhiteSpace(normalized)
                    select new Uri(normalized, UriKind.Absolute))];
            urls_diff = [.. (from s in songInfoCache.url_diff.Split(' ')
                         where !string.IsNullOrWhiteSpace(s)
                         let normalized = NormalizeDownloadUrlString(s)
                         where !string.IsNullOrWhiteSpace(normalized)
                         select new Uri(normalized, UriKind.Absolute))];
        }
        else
        {
            urls = [];
            urls_diff = [];
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

    private void tableContextMenuSearchLinkOpened(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_search_link"))
        {
            e.Handled = true;
            return;
        }
        if (sender is not MenuItem menuItem || !TryGetContextMenuRow(sender, out object row))
        {
            return;
        }
        if (!GridRowResolver.IsPlaylistRow(row))
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        e.Handled = true;
        if (changeSubmenuOpenSearchLinkTask != null)
        {
            return;
        }
        menuItem.Items.Clear();
        var menuItemStatus = new MenuItem
        {
            Header = BeMusicSeeker.Properties.Resources.Now_searching,
            IsEnabled = false,
            Visibility = Visibility.Visible
        };
        menuItem.Items.Add(menuItemStatus);
        changeSubmenuOpenSearchLinkTask = Task.Run(delegate
        {
            NLogWrapper.DebuggerLogger?.Trace("Test starts: changeSubmenuOpenSearchLinkTask");
            CancellationToken token = tableContextMenuTaskTokenSource.Token;
            getSongInfoCacheTask ??= Task.Run(delegate
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
            getSongInfoCacheTask.Wait();
            if (!token.IsCancellationRequested)
            {
                List<Func<MenuItem>> subMenuItemCreateFuncs = [];
                songInfoCacheToUrlLists(songInfoCache, GridRowResolver.GetUrl(row), GridRowResolver.GetUrlDiff(row), out List<Uri> urls, out List<Uri> urls_diff);
                foreach (Uri url in urls)
                {
                    MenuItem item()
                    {
                        try
                        {
                            var menuItem2 = new MenuItem
                            {
                                Header = BeMusicSeeker.Properties.Resources.Original_URL,
                                IsEnabled = true,
                                Visibility = Visibility.Visible,
                                Tag = url,
                                ToolTip = url.ToString()
                            };
                            menuItem2.Click += tableContextMenuItemSearchLinkSubmenuClick;
                            return menuItem2;
                        }
                        catch
                        {
                            return (MenuItem)null;
                        }
                    }
                    subMenuItemCreateFuncs.Add(item);
                }
                foreach (Uri url2 in urls_diff)
                {
                    if (!url2.ToString().StartsWith("http://absolute.pv.land.to/", StringComparison.OrdinalIgnoreCase) && !url2.ToString().Equals("http://gnqg.rosx.net/upload/", StringComparison.OrdinalIgnoreCase) && !url2.ToString().Equals("http://gnqg.rosx.net/upload/upload.cgi", StringComparison.OrdinalIgnoreCase) && (!url2.ToString().StartsWith("http://www.ribbit.xyz/bms/mirror/", StringComparison.OrdinalIgnoreCase) || !url2.ToString().EndsWith("/")))
                    {
                        MenuItem item2()
                        {
                            try
                            {
                                var menuItem2 = new MenuItem
                                {
                                    Header = ((url2.ToString().StartsWith("http://www.ribbit.xyz/bms/mirror/", StringComparison.OrdinalIgnoreCase) || url2.ToString().StartsWith("http://gnqg.rosx.net/upload/", StringComparison.OrdinalIgnoreCase)) ? "Uploader" : BeMusicSeeker.Properties.Resources.Diff_URL),
                                    IsEnabled = true,
                                    Visibility = Visibility.Visible,
                                    Tag = url2,
                                    ToolTip = url2.ToString()
                                };
                                menuItem2.Click += tableContextMenuItemSearchLinkSubmenuClick;
                                return menuItem2;
                            }
                            catch
                            {
                                return (MenuItem)null;
                            }
                        }
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
                            MenuItem item3()
                            {
                                try
                                {
                                    var uri = new Uri("h" + match.Groups[1].Value.Trim('\''), UriKind.Absolute);
                                    var menuItem2 = new MenuItem
                                    {
                                        Header = BeMusicSeeker.Properties.Resources.Remarks_URL + (temp + 1),
                                        IsEnabled = true,
                                        Visibility = Visibility.Visible,
                                        Tag = uri,
                                        ToolTip = uri.ToString()
                                    };
                                    menuItem2.Click += tableContextMenuItemSearchLinkSubmenuClick;
                                    return menuItem2;
                                }
                                catch
                                {
                                    return (MenuItem)null;
                                }
                            }
                            subMenuItemCreateFuncs.Add(item3);
                        }
                    }
                }
                if (!token.IsCancellationRequested)
                {
                    base.Dispatcher.BeginInvoke((Action)delegate
                    {
                        if (!token.IsCancellationRequested && !_isClosingOrClosed)
                        {
                            List<MenuItem> list = [.. (from f in subMenuItemCreateFuncs
                                                   select f() into i
                                                   where i != null
                                                   select i)];
                            if (list.Count > 0)
                            {
                                menuItem.Items.Clear();
                                {
                                    foreach (MenuItem item4 in list)
                                    {
                                        if (!menuItem.Items.Cast<MenuItem>().Any(i => i.Tag.ToString().Equals(item4.Tag.ToString(), StringComparison.OrdinalIgnoreCase)))
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
        }, tableContextMenuTaskTokenSource.Token).Logging("tableContextMenuSearchLinkOpened");
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
                if (BMSFile.bmsExtensions.Concat([".zip", ".7z", ".rar", "lzh"]).All(e => !fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
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
            installChartPackages([filePath]);
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
            using FileStream fileStream = File.Create(destinationPath);
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
    private async void installChartPackages(IEnumerable<string> filePaths)
    {
        var viewModel = base.DataContext as MainWindowViewModel;
        int progIdx = 0;
        int failNum = 0;
        List<string> installs = [.. filePaths];
        int total = installs.Count;
        if (total == 0)
        {
            return;
        }
        var cancelTokenSource = new CancellationTokenSource();
        Task task = Task.Run(delegate
        {
            viewModel.InstallChartPackages(installs, cancelTokenSource.Token, delegate (bool s)
            {
                progIdx++;
                if (!s)
                {
                    failNum++;
                }
            });
        }, cancelTokenSource.Token).Logging("MainWindow.installChartPackages");
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

    private void cancelMaintenanceRescanClick(object sender, RoutedEventArgs e)
    {
        (base.DataContext as MainWindowViewModel)?.CancelMaintenanceRescan();
    }

    private async void tableContextMenuItemSearchLinkSubmenuClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menuItem)
        {
            return;
        }
        try
        {
            if (!Settings.Default.SkipInitFileCheck && Settings.Default.AutoInstall && menuItem.Tag is Uri)
            {
                try
                {
                    var uri = (Uri)menuItem.Tag;
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

    private void tableContextMenuItemRegisterBMSFileToScoreViwer(object sender, RoutedEventArgs e)
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
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        Task.Run(delegate
        {
            string fileName = viewModel.RegisterScoreViewerTargets(targets);
            try
            {
                if (targets.Count == 1 && !string.IsNullOrWhiteSpace(fileName))
                {
                    Process.Start(fileName);
                }
            }
            catch
            {
            }
        }).Logging("tableContextMenuItemRegisterBMSFileToScoreViwer");
    }

    private void tableContextMenuItemForceFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<BMSFile> chartFiles = GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunResourceHealthCheck);
        if (chartFiles != null && chartFiles.Count() != 0)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.ForceResourceHealthCheckCharts(chartFiles);
            }).Logging("tableContextMenuItemForceFileScanCheckSelectedCharts");
            e.Handled = true;
        }
    }

    private void tableContextMenuRemoveInstallDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        bool isInstalledLocationRepair = IsFullScanMainViewSection(GetCurrentMainViewOperationSection());
        ChartOperationCapabilities capability = isInstalledLocationRepair
            ? ChartOperationCapabilities.RepairInstalledLocation
            : ChartOperationCapabilities.UpdateInstallDestination;
        List<BMSFile> chartFiles = isInstalledLocationRepair
            ? GetSelectedChartCompatibilityAdapters(capability)
            : GetSelectedPendingChartCompatibilityAdapters(capability);
        if (chartFiles != null && chartFiles.Count() != 0)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            e.Handled = true;
            Task.Run(delegate
            {
                if (isInstalledLocationRepair)
                {
                    viewModel.ClearInstallDestinationForCharts(chartFiles);
                }
                else
                {
                    viewModel.ClearInstallDestinationForPendingCharts(chartFiles);
                }
            }).Logging("tableContextMenuRemoveInstallDestinationClick");
        }
    }

    private void tableContextMenuSearchCorrectInstallationDirectoryChartsClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<BMSFile> chartFiles = GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RepairInstalledLocation);
        if (chartFiles != null && chartFiles.Count() != 0)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.SearchCorrectInstallationDirectoryCharts(chartFiles);
            }).Logging("tableContextMenuSearchCorrectInstallationDirectoryChartsClick");
            e.Handled = true;
        }
    }

    private void tableContextMenuFixInstallationDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<BMSFile> chartFiles = GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RepairInstalledLocation);
        if (chartFiles == null || chartFiles.Count() == 0)
        {
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        if (!chartFiles.Any(f => !string.IsNullOrWhiteSpace(f.instl_dst)))
        {
            DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_fix_installation_warning, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
        else if (DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_fix_installation, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.FixInstallationDirectoryCharts(chartFiles);
            }).Logging("tableContextMenuFixInstallationDirectoryClick");
        }
    }

    private void tableContextMenuItemDeleteEntryClick(object sender, RoutedEventArgs e)
    {
        List<BMSTableEntry> list2 = GetSelectedGridPlaylistEntries();
        var viewModel = base.DataContext as MainWindowViewModel;
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
            }).Logging("tableContextMenuItemDeleteEntryClick");
        }
    }

    private void tableContextMenuItemForceFileScanCheckAllCharts(object sender, RoutedEventArgs e)
    {
        if (ShouldBlockStartupUiInteraction("datagrid_context_menu_full_scan_all_charts"))
        {
            e.Handled = true;
            return;
        }
        if (DispatcherMessageBox.Show(Window.GetWindow(this),
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
        viewModel?.StartRescanAllOwnedChartMaintenance();
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
        if (DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_chart_info_parse_failure_record, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
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
        List<BMSFile> chartFiles = GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.None);
        var viewModel = base.DataContext as MainWindowViewModel;
        if (chartFiles.Count > 0)
        {
            Task.Run(delegate
            {
                try
                {
                    viewModel.AutoRenameChartFolders(chartFiles);
                }
                finally
                {
                    RefreshCustomTableViewDisplayAsync();
                }
            }).Logging("tableContextMenuItemAutoRenameFolderClick");
        }
    }

    private void tableContextMenuItemRenameBMSFileClick(object sender, RoutedEventArgs e)
    {
        List<BMSFile> bmsFiles = GetSelectedBmsChartFiles(ChartOperationCapabilities.RenameInvalidExtension);
        var viewModel = base.DataContext as MainWindowViewModel;
        bool isPendingSelected = IsPendingMainViewSection(GetCurrentMainViewOperationSection());
        if (bmsFiles.Count <= 0 || DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_rename_to_invalid, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        Task.Run(delegate
        {
            List<BMSFile> list = [.. bmsFiles.Where(f => Path.GetExtension(f.path).StartsWith(".b", StringComparison.OrdinalIgnoreCase))];
            List<BMSFile> list2 = [.. bmsFiles.Where(f => Path.GetExtension(f.path).StartsWith(".p", StringComparison.OrdinalIgnoreCase))];
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
        MainWindowViewModel.MainViewOperationSection section = GetCurrentMainViewOperationSection();
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
        else if (DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_recycle, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
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
                bool approved = DispatcherMessageBox.Show(
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
        var pendingDeleteConfirmDialog = new PendingDeleteConfirmDialog
        {
            Owner = this
        };
        bool? flag = pendingDeleteConfirmDialog.ShowDialog();
        deleteContainingPackageFoldersWhenNoBms = pendingDeleteConfirmDialog.DeleteFolderWhenNoBmsChecked;
        return flag == true;
    }

    private async void tableContextMenuItemMoveFileClick(object sender, RoutedEventArgs e)
    {
        List<ChartOperationTarget> targets = [.. GetSelectedChartTargets().Where(target => target.HasCapability(ChartOperationCapabilities.MoveInLibrary) && !string.IsNullOrWhiteSpace(target.Chart?.Path))];
        if (sender is not MenuItem menuItem)
        {
            return;
        }
        if (base.DataContext is MainWindowViewModel viewModel && menuItem.DataContext is string dstDir && targets.Count > 0 && DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_other_root, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            await Task.Run(delegate
            {
                viewModel.MoveLibraryCharts(targets, dstDir);
            }).Logging("tableContextMenuItemMoveFileClick");
        }
    }

    private void fixEncodingSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem menuItem && TryGetContextMenuRow(e.Source, out _))
        {
            List<BMSFile> list = GetSelectedBmsChartFiles(ChartOperationCapabilities.RunBmsEncodingFix);
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
            List<BMSFile> list = GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunResourceHealthCheck);
            if (list != null && list.Count() != 0)
            {
                (base.DataContext as MainWindowViewModel).SetChartResourceWarningsIgnored(list);
                e.Handled = true;
            }
        }
    }

    private void notIgnoredFileScanCheckSelectedCharts(object sender, RoutedEventArgs e)
    {
        if (TryGetContextMenuRow(e.Source, out _))
        {
            List<BMSFile> list = GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunResourceHealthCheck);
            if (list != null && list.Count() != 0)
            {
                (base.DataContext as MainWindowViewModel).SetChartResourceWarningsIgnored(list, unset: true);
                e.Handled = true;
            }
        }
    }

    private async void forceInstallSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<BMSFile> chartFiles = GetSelectedPendingChartCompatibilityAdapters(ChartOperationCapabilities.UpdateInstallDestination);
        if (chartFiles == null || chartFiles.Count() == 0)
        {
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        ClearMainGridSelection();
        if (!treeViewItemInstallPending.IsSelected)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "forceInstallSelectedPendingCharts");
        }
        await Task.Run(delegate
        {
            viewModel.ForceInstallPendingCharts(chartFiles);
        }).Logging("forceInstallSelectedPendingCharts");
    }

    private static string GetManualInstallConfirmationMessage()
    {
        return Settings.Default.DeletePendingPackageSourceAfterInstall
            ? BeMusicSeeker.Properties.Resources.Msg_manual_installation_delete_source
            : BeMusicSeeker.Properties.Resources.Msg_manual_installation;
    }

    private async void manualInstallSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<BMSFile> chartFiles = GetSelectedPendingChartCompatibilityAdapters(ChartOperationCapabilities.UpdateInstallDestination);
        if (chartFiles == null || chartFiles.Count() == 0)
        {
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        if (Settings.Default.ShowDiffBMSInstallConfirmMsg && DispatcherMessageBox.Show(Window.GetWindow(this), GetManualInstallConfirmationMessage(), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) != MessageBoxResult.OK)
        {
            return;
        }
        ClearMainGridSelection();
        if (!treeViewItemInstallPending.IsSelected)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "manualInstallSelectedPendingCharts");
        }
        await Task.Run(delegate
        {
            viewModel.ManualInstallPendingCharts(chartFiles);
        }).Logging("manualInstallSelectedPendingCharts");
    }

    private async void searchInstallDestinationSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<BMSFile> chartFiles = GetSelectedPendingChartCompatibilityAdapters(ChartOperationCapabilities.UpdateInstallDestination);
        if (chartFiles != null && chartFiles.Count() != 0)
        {
            var viewModel = base.DataContext as MainWindowViewModel;
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.SearchInstallDestinationForPendingCharts(chartFiles);
            }).Logging("searchInstallDestinationSelectedPendingCharts");
        }
    }

    private async void tableContextMenuItemDeleteInstallPackagesClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        MainWindowViewModel.MainViewOperationSection section = GetCurrentMainViewOperationSection();
        bool isPendingSelected = IsPendingMainViewSection(section);
        bool isInstalledSelected = IsInstalledMainViewSection(section);
        if (!isPendingSelected && !isInstalledSelected)
        {
            return;
        }
        List<BMSFile> selectedChartFiles = isPendingSelected
            ? GetSelectedPendingChartCompatibilityAdapters(ChartOperationCapabilities.UpdateInstallDestination)
            : GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.None);
        if (selectedChartFiles.Count == 0)
        {
            return;
        }
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        string confirmationMessage = isPendingSelected ? BeMusicSeeker.Properties.Resources.Msg_clear_selected_pendings : BeMusicSeeker.Properties.Resources.Msg_clear_selected_installed;
        if (DispatcherMessageBox.Show(Window.GetWindow(this), confirmationMessage, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.Cancel)
        {
            return;
        }
        NLogWrapper.FileLogger?.Info("table_delete_install_packages requested section=" + section + " treeSection=" + _currentTreeSelectionSection + " selectedRows=" + selectedChartFiles.Count);
        e.Handled = true;
        ClearMainGridSelection();
        if (isPendingSelected)
        {
            SelectNextSiblingOrRoot(treeViewItemInstallPending, treeView.SelectedItem, "tableContextMenuItemDeleteInstallPackagesClick");
            await Task.Run(delegate
            {
                viewModel.RemovePendingPackages(selectedChartFiles);
            }).Logging("tableContextMenuItemDeleteInstallPackagesClick");
            if (treeViewItemInstallPending.IsSelected && treeViewItemInstallPending.Items.Count == 0)
            {
                await Task.Run(delegate
                {
                    viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.PendingInstallFilter);
                }).Logging("tableContextMenuItemDeleteInstallPackagesClick");
            }
            return;
        }
        SelectNextSiblingOrRoot(newlyInstalledTreeViewItem, treeView.SelectedItem, "tableContextMenuItemDeleteInstallPackagesClick");
        await Task.Run(delegate
        {
            viewModel.RemoveInstalledPackageRecords(selectedChartFiles);
        }).Logging("tableContextMenuItemDeleteInstallPackagesClick");
        if (newlyInstalledTreeViewItem.IsSelected && newlyInstalledTreeViewItem.Items.Count == 0)
        {
            await Task.Run(delegate
            {
                viewModel.ExecInstallFilter(MainWindowViewModel.InstallFilterType.NewlyInstalledFilter);
            }).Logging("tableContextMenuItemDeleteInstallPackagesClick");
        }
    }

    private async void searchMergeDestinationSelectedPendingCharts(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextMenuRow(e.Source, out _))
        {
            return;
        }
        List<BMSFile> chartFiles = GetSelectedPendingChartCompatibilityAdapters(ChartOperationCapabilities.UpdateInstallDestination);
        if (chartFiles == null || chartFiles.Count == 0 || !ConfirmMergeDestinationSearch())
        {
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        await Task.Run(delegate
        {
            viewModel.SearchMergeDestinationForPendingCharts(chartFiles);
        }).Logging("searchMergeDestinationSelectedPendingCharts");
    }

    private bool ConfirmMergeDestinationSearch()
    {
        return DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_estimate_merge_confirm, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) == MessageBoxResult.OK;
    }

    private void tableContextMenuItemConvertToAudioFileClick(object sender, RoutedEventArgs e)
    {
        BMSFile[] bmsFiles = [.. GetSelectedBmsChartFiles(ChartOperationCapabilities.ConvertToAudio).Where(f => File.Exists(f.path))];
        if (bmsFiles.Length == 0)
        {
            return;
        }
        var viewModel = base.DataContext as MainWindowViewModel;
        var cancelTokenSource = new CancellationTokenSource();
        int progIdx = 0;
        int failNum = 0;
        var commonOpenFileDialog = new CommonOpenFileDialog
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
        }, cancelTokenSource.Token).Logging("tableContextMenuItemConvertToAudioFileClick");
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
        DispatcherMessageBox.Show(Window.GetWindow(this), ((!cancelTokenSource.IsCancellationRequested) ? BeMusicSeeker.Properties.Resources.Msg_conversion_completed : BeMusicSeeker.Properties.Resources.Msg_conversion_stopped) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Success + ": " + (progIdx - failNum) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Failure + ": " + (bmsFiles.Length - progIdx + failNum), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, cancelTokenSource.IsCancellationRequested ? MessageBoxImage.Exclamation : MessageBoxImage.Asterisk, MessageBoxResult.OK);
    }

    private void playlistTableDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
        treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
        var viewModel = base.DataContext as MainWindowViewModel;
        if (sender is not TreeViewItem treeViewItem)
        {
            return;
        }
        if (treeViewItem.DataContext is not BMSTable table || table.is_external_sync)
        {
            return;
        }
        TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem2 == null)
        {
            return;
        }
        treeViewItem2.Background = Brushes.Transparent;
        if (!CustomTableDataTransfer.TryGetSelectedRows(e.Data, out List<object> selectedRows))
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
            viewModel.AddChartRowsToFolderBMSTable(selectedRows, table, folderName);
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
        if (sender is not TreeViewItem tviTable)
        {
            return;
        }
        TreeViewItem treeViewItem = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
        if (treeViewItem == null || tviTable.DataContext is not BMSTable bMSTable)
        {
            return;
        }
        if (!bMSTable.is_external_sync && (!TryGetPlaylistFolderNode(treeViewItem.DataContext, out PlaylistFolderNode folderNode) || !folderNode.IsSpecial))
        {
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

    /// <summary>
    /// 内蔵プレーヤーの「次の曲へ (Next)」ボタンがクリックされた際のイベントハンドラ。
    /// リスト内で現在選択されている曲の次の曲を非同期で再生開始します。
    /// </summary>
    private async void gridBMSPlayerControlsNextButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
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
        if (base.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }
        gridBMSPlayerControlsPreviousButtonClickTimer ??= new DispatcherTimer(new TimeSpan(0, 0, 0, 0, 500), DispatcherPriority.Background, gridBMSPlayerControlsPreviousButtonSingleClicked, Dispatcher.CurrentDispatcher);
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
        if (base.DataContext is MainWindowViewModel viewModel)
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
        if (base.DataContext is MainWindowViewModel viewModel)
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
        if (base.DataContext is MainWindowViewModel viewModel)
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
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.FastForwardPlayingBMSfileStart();
            }).Logging("gridBMSPlayerControlsFastForwardButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsFastForwardButtonReleased(object sender, MouseButtonEventArgs e)
    {
        var viewModel = base.DataContext as MainWindowViewModel;
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
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.FastForwardPlayingBMSfileEnd();
            }).Logging("gridBMSPlayerControlsFastForwardButtonReleased");
        }
    }

    private async void gridBMSPlayerControlsFastBackwardButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.FastBackwardPlayingBMSfileStart();
            }).Logging("gridBMSPlayerControlsFastBackwardButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsFastBackwardButtonReleased(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
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
        if (base.DataContext is MainWindowViewModel viewModel)
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
            if (base.DataContext is MainWindowViewModel viewModel)
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
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.uBMplayShowEffect();
            }).Logging("gridBMSPlayerControlsShowEffectButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsChangePlaysideButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            await Task.Run(delegate
            {
                viewModel.uBMplayChangePlayside();
            }).Logging("gridBMSPlayerControlsChangePlaysideButtonClicked");
        }
    }

    private async void gridBMSPlayerControlsIncreaseHighSpeedButtonClicked(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel viewModel)
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
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            await Task.Run(delegate
            {
                viewModel.uBMplayDecreaseHighSpeed();
            }).Logging("gridBMSPlayerControlsDecreaseHighSpeedButtonClicked");
        }
    }

    private void showBMSPlayerPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (windowsFormsHost != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(windowsFormsHost, UIElement.VisibilityProperty).ParentMultiBinding;
            windowsFormsHost.Visibility = Visibility.Visible;
            windowsFormsHost.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    public void tryShowBMSPlayerPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (NowPanelState == MainWindowViewModel.PanelState.BMS_PLAYER)
        {
            showBMSPlayerPanel();
        }
        IntPtr handle;
        try
        {
            handle = new WindowInteropHelper(this).Handle;
        }
        catch
        {
            return;
        }
        if (!(handle == Win32API.GetForegroundWindow()))
        {
            return;
        }
        base.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)async delegate
        {
            if (_isClosingOrClosed)
            {
                return;
            }
            for (int i = 1; i <= 10; i++)
            {
                if (_isClosingOrClosed)
                {
                    break;
                }
                NLogWrapper.DebuggerLogger?.Trace("try to set focus on custom table");
                IntPtr currentHandle;
                try
                {
                    currentHandle = new WindowInteropHelper(this).Handle;
                }
                catch
                {
                    break;
                }
                if (!(currentHandle == Win32API.GetForegroundWindow()))
                {
                    break;
                }
                Keyboard.Focus(customTableView);
                await Task.Delay(100);
            }
        });
    }

    private void showBrowserPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (webBrowser != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(webBrowser, UIElement.VisibilityProperty).ParentMultiBinding;
            webBrowser.Visibility = Visibility.Visible;
            webBrowser.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    public void tryShowBrowserPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (NowPanelState == MainWindowViewModel.PanelState.MOVIE_PLAYER)
        {
            showBrowserPanel();
        }
    }

    private void collapseBMSPlayerPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
        if (windowsFormsHost != null)
        {
            MultiBinding parentMultiBinding = BindingOperations.GetMultiBindingExpression(windowsFormsHost, UIElement.VisibilityProperty).ParentMultiBinding;
            windowsFormsHost.Visibility = Visibility.Collapsed;
            windowsFormsHost.SetBinding(UIElement.VisibilityProperty, parentMultiBinding);
        }
    }

    private void collapseBrowserPanel()
    {
        if (_isClosingOrClosed)
        {
            return;
        }
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
            if (_isClosingOrClosed)
            {
                return;
            }
            gridBMSPlayerControlsRotatePanelStateButtonClicked(null, null);
        }, DispatcherPriority.ContextIdle);
    }

    private void gridBMSPlayerControlsRotatePanelStateButtonClicked(object sender = null, RoutedEventArgs e = null)
    {
        if (_isClosingOrClosed)
        {
            return;
        }
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
                && (viewModel?.IsPlaylistUpdating ?? false) && !isManualInteraction)
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

    private async void sliderPlayerMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var slider = (Slider)sender;
        Point position = e.GetPosition(slider);
        double value = slider.Maximum * Math.Max(0.0, Math.Min(1.0, (position.X - 5.0) / (slider.ActualWidth - 10.0)));
        slider.Value = value;
        if (base.DataContext is not MainWindowViewModel viewModel)
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
        if (base.DataContext is not MainWindowViewModel viewModel)
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
        if (base.DataContext is not MainWindowViewModel viewModel)
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
