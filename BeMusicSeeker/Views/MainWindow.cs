using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
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
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using Ribbit.Windows;

namespace BeMusicSeeker.Views;

public partial class MainWindow : Window, IComponentConnector, IStyleConnector
{
    // NOTE:
    // TreeView の仮想化 (Recycling) 有効時は、画面外ノードのコンテナが VisualTree から外れる。
    // そのため「VisualTree を再帰して選択状態を判定する」実装は false negative を起こす。
    // ここでは最後に確定した選択ノードの所属セクションを保持し、UIコンテナ有無に依存しない判定を行う。
    private enum TreeSelectionSection
    {
        None,
        Playlist,
        InstallPending,
        FullScanCheck,
        Other
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

    public MainWindow()
    {
        InitializeComponent();

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

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            // キャッシュバスター: GitHub CDN のキャッシュを回避する
            string versionUrl = "https://raw.githubusercontent.com/Neeted/bemusicseeker-unofficial-fork/main/version.txt?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var client = new System.Net.Http.HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(5);
                string latestVersionStr = await client.GetStringAsync(versionUrl);
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
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn("Failed to check for updates: " + ex.Message);
        }
    }

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

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] filePaths)
        {
            installBMSFiles(filePaths);
            newlyInstalledTreeViewItem.IsExpanded = true;
        }
    }

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

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!dataGrid.IsMouseOver)
        {
            DragMove();
        }
    }

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

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        Settings.Default.TreeViewWidth = treeView.ActualWidth + gridSplitter.ActualWidth;
        Win32API.WINDOWPLACEMENT lpwndpl = default(Win32API.WINDOWPLACEMENT);
        Win32API.GetWindowPlacement(new WindowInteropHelper(this).Handle, ref lpwndpl);
        Settings.Default.WindowPlacement = lpwndpl;
        Settings.Default.Save();
    }

    private async void dataGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        ListSortDirection newDir = ((e.Column.SortDirection == ListSortDirection.Ascending) ? ListSortDirection.Descending : ListSortDirection.Ascending);
        string name = e.Column.SortMemberPath;
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        await Task.Run(delegate
        {
            viewModel.ExecSort(name, newDir);
        }).Logging("dataGridSorting");
    }

    public void renewSortIcon(DataGrid dataGrid)
    {
        base.Dispatcher.BeginInvoke((Action)delegate
        {
            MainWindowViewModel mainWindowViewModel = base.DataContext as MainWindowViewModel;
            MainWindowViewModel.cSortParameters parameters = mainWindowViewModel.SortParameters;
            if (parameters != null)
            {
                dataGrid.Columns.First((DataGridColumn c) => c.SortMemberPath == parameters.ColumnsName).SortDirection = parameters.Direction;
            }
        }, DispatcherPriority.ContextIdle);
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

    private void dataGridPlaylistSummary_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            getDisplayIndicesPlaylistSummary();
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

    public void _renewBMSPlayerControlInfo()
    {
        if (base.DataContext is MainWindowViewModel { NowPlayingBMS: not null } mainWindowViewModel)
        {
            _renewBMSPlayerControlInfo(mainWindowViewModel.NowPlayingBMS);
        }
    }

    private void _renewBMSPlayerControlInfo(DataGridRow dataGridRow)
    {
        if (dataGridRow.DataContext is BMSFile bmsFile)
        {
            _renewBMSPlayerControlInfo(bmsFile);
        }
    }

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
        if (bmsFile is VirtualBMSFile)
        {
            BMSFile nonVirtualBMSFile = ((VirtualBMSFile)bmsFile).GetNonVirtualBMSFile();
            if (nonVirtualBMSFile == null)
            {
                gridBMSPlayerControlsTitle.Text = bmsFile.Title;
                gridBMSPlayerControlsSubtitle.Text = string.Empty;
                gridBMSPlayerControlsArtist.Text = bmsFile.Artist;
                return;
            }
            bmsFile = nonVirtualBMSFile;
        }
        gridBMSPlayerControlsTitle.Text = bmsFile.title;
        gridBMSPlayerControlsSubtitle.Text = bmsFile.subtitle;
        gridBMSPlayerControlsArtist.Text = bmsFile.artist;
    }

    private void dataGridCellBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        VirtualBMSFile virtualBMSFile = e.Row.DataContext as VirtualBMSFile;
        BMSFile bMSFile = e.Row.DataContext as BMSFile;
        string path;
        try
        {
            path = ((Binding)((DataGridBoundColumn)e.Column).Binding).Path.Path;
        }
        catch
        {
            return;
        }
        if (virtualBMSFile != null)
        {
            int num = 250;
            if (path == virtualBMSFile.GetName((VirtualBMSFile f) => f.Url))
            {
                if (virtualBMSFile.ToBMSTableEntry().parent.is_external_sync)
                {
                    e.Cancel = true;
                    return;
                }
                dataGridLengthConverterForURL1.IsEditingMode = true;
                e.Column.Width = num;
                e.Column.MaxWidth = double.MaxValue;
            }
            else if (path == virtualBMSFile.GetName((VirtualBMSFile f) => f.Url_diff))
            {
                if (virtualBMSFile.ToBMSTableEntry().parent.is_external_sync)
                {
                    e.Cancel = true;
                    return;
                }
                dataGridLengthConverterForURL2.IsEditingMode = true;
                e.Column.Width = num;
                e.Column.MaxWidth = double.MaxValue;
            }
            else if (path == virtualBMSFile.GetName((VirtualBMSFile f) => f.Level) && virtualBMSFile.ToBMSTableEntry().parent.is_external_sync)
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
        VirtualBMSFile vbmsFile = e.Row.DataContext as VirtualBMSFile;
        BMSFile bmsFile = e.Row.DataContext as BMSFile;
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
        if (vbmsFile != null)
        {
            if (path == vbmsFile.GetName((VirtualBMSFile f) => f.Url))
            {
                dataGridLengthConverterForURL1.IsEditingMode = false;
                Binding binding = BindingOperations.GetBinding(e.Column, DataGridColumn.WidthProperty);
                e.Column.Width = (int)_getValueOfPropertyPath(binding.Source, binding.Path.Path);
                e.Column.MaxWidth = e.Column.MinWidth;
            }
            else if (path == vbmsFile.GetName((VirtualBMSFile f) => f.Url_diff))
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
            if (vbmsFile == null || (vbmsFile.ToBMSTableEntry().parent != null && vbmsFile.ToBMSTableEntry().parent.is_external_sync && path != vbmsFile.GetName((VirtualBMSFile f) => f.memo)))
            {
                bindingExpression.UpdateTarget();
                return;
            }
            if ((path == vbmsFile.GetName((VirtualBMSFile f) => f.Url) || path == vbmsFile.GetName((VirtualBMSFile f) => f.Url_diff)) && !Uri.TryCreate(textBox.Text, UriKind.Absolute, out var _))
            {
                bindingExpression.UpdateTarget();
                return;
            }
            bindingExpression.UpdateSource();
            base.Dispatcher.BeginInvoke((Action)async delegate
            {
                await Task.Run(delegate
                {
                    viewModel.CommitBMSFile(vbmsFile);
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
        if (!(sender is TextBlock { DataContext: VirtualBMSFile vbmsFile }) || vbmsFile.Url == null || !vbmsFile.Url.IsAbsoluteUri)
        {
            return;
        }
        if (!Settings.Default.SkipInitFileCheck && Settings.Default.AutoInstall)
        {
            try
            {
                if (!vbmsFile.Url.ToString().EndsWith("/") && !vbmsFile.Url.ToString().EndsWith(".htm") && !vbmsFile.Url.ToString().EndsWith(".html") && await downloadAndInstall(vbmsFile.Url))
                {
                    newlyInstalledTreeViewItem.IsExpanded = true;
                    return;
                }
            }
            catch
            {
            }
        }
        Process.Start(vbmsFile.Url.ToString());
    }

    private async void dataGridCellOpenURLDiffClick(object sender, MouseButtonEventArgs e)
    {
        if (!(sender is TextBlock { DataContext: VirtualBMSFile vbmsFile }) || vbmsFile.Url_diff == null || !vbmsFile.Url_diff.IsAbsoluteUri)
        {
            return;
        }
        if (!Settings.Default.SkipInitFileCheck && Settings.Default.AutoInstall)
        {
            try
            {
                if (!vbmsFile.Url_diff.ToString().EndsWith("/") && !vbmsFile.Url_diff.ToString().EndsWith(".htm") && !vbmsFile.Url_diff.ToString().EndsWith(".html") && await downloadAndInstall(vbmsFile.Url_diff))
                {
                    newlyInstalledTreeViewItem.IsExpanded = true;
                    return;
                }
            }
            catch
            {
            }
        }
        Process.Start(vbmsFile.Url_diff.ToString());
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

    // 仮想化で子ノードコンテナが未生成でも、選択遷移が無選択で止まらないようにする。
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
            if (!(selectedItem.DataContext is Tuple<string, bool> tuple))
            {
                return;
            }
            folderName = tuple.Item1;
            if (tuple.Item2)
            {
                type = PlaylistTableHeaderSpecialFolderExt.FromDisplayName(folderName).ToPlaylistFilterType();
            }
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

    private void playlistTableFolderkeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && sender is TreeViewItem treeViewItem && treeViewItem.Template.FindName("PART_Header", treeViewItem) is ContentPresenter templatedParent && treeViewItem.HeaderTemplate.FindName("etbPlaylistTableFolder", templatedParent) is EditableTextBlock editableTextBlock)
        {
            editableTextBlock.IsInEditMode = true;
            e.Handled = true;
        }
    }

    private async void playlistTableFolderClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is EditableTextBlock { TemplatedParent: ContentPresenter templatedParent } etb && (templatedParent.TemplatedParent as TreeViewItem).IsSelected)
        {
            await Task.Delay(1000);
            etb.IsInEditMode = true;
        }
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
            string nameBefore = ((Tuple<string, bool>)templatedParent.DataContext).Item1;
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
            if (!(treeViewItem3.DataContext is Tuple<string, bool> tuple))
            {
                return;
            }
            folderName = tuple.Item1;
            if (tuple.Item2)
            {
                type = PlaylistTableHeaderSpecialFolderExt.FromDisplayName(folderName).ToPlaylistFilterType();
            }
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
            await Task.Run(delegate
            {
                viewModel.ResyncPlaylists(selectedPlaylistSummaryRows);
            }).Logging("playlistSummaryContextMenuResyncClick");
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

    private void treeViewPlaylistRootContextMenuItemLoadPlaylistURLClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { IsWriteLockHeldBMSTablesInitializeMin: false })
        {
            loadPlaylistURIDialog.Visibility = Visibility.Visible;
        }
    }

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
            await Task.Run(delegate
            {
                viewModel.RegistrateExternalPlaylistBMSTable(dataContext.url);
            }).Logging("treeViewPlaylistRootContextMenuItemLoadPlaylistCollectionClick");
        }
    }

    private async void treeViewPlaylistRootContextMenuItemLoadWalkureTableClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel != null && sender is MenuItem menuItem && !viewModel.IsWriteLockHeldBMSTablesInitializeMin)
        {
            Uri uri = new Uri((string)menuItem.Tag);
            await Task.Run(delegate
            {
                viewModel.RegistrateExternalPlaylistBMSTable(uri);
            }).Logging("treeViewPlaylistRootContextMenuItemLoadWalkureTableClick");
        }
    }

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
        await Task.Run(delegate
        {
            viewModel.RegistrateExternalPlaylistBMSTable(uri);
        }).Logging("treeViewPlaylistRootContextMenuItemLoadWalkureTableRecommendedClick");
    }

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

    private async void treeViewPlaylistTableContextMenuItemReloadClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null || !(sender is MenuItem { DataContext: BMSTable table }))
        {
            return;
        }
        await Task.Run(delegate
        {
            viewModel.ResyncPlaylists(new BMSTable[1] { table });
        }).Logging("treeViewPlaylistTableContextMenuItemReloadClick");
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
        bool restored = treeViewItemPlaylist.SelectChildTreeViewItemSearchedByDataContext(selectionTarget);
        NLogWrapper.FileLogger?.Info("playlist_selection_restore_single_reload restored=" + restored + " table=" + selectionTarget.name);
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

    private void treeViewPlaylistTableContextMenuItemOpenClearLampClick(object sender, RoutedEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel && sender is MenuItem { DataContext: BMSTable dataContext } && dataContext.Page_url != null && dataContext.is_external_sync && mainWindowViewModel.LR2ID != 0)
        {
            Process.Start(clearlampUri + "?lr2ID=" + Uri.EscapeDataString(mainWindowViewModel.LR2ID.ToString()) + "&table_url=" + Uri.EscapeDataString(dataContext.Page_url.ToString()));
        }
    }

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
        menuItem.IsEnabled = !bMSTable.is_external_sync && !((Tuple<string, bool>)editableTextBlock.DataContext).Item2;
        menuItem2.IsEnabled = !bMSTable.is_external_sync;
    }

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

    private async void treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock == null || ((Tuple<string, bool>)editableTextBlock.DataContext).Item2)
        {
            return;
        }
        BMSTable bmsTable = _getUpperBMSTableForContextMenuClickEvent(sender);
        if (bmsTable == null || bmsTable.is_external_sync || ((Tuple<string, bool>)editableTextBlock.DataContext).Item2)
        {
            return;
        }
        string folderNameDelete = editableTextBlock.Text;
        if (MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_remove_folder, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            await Task.Run(delegate
            {
                viewModel.RemoveFolderBMSTable(bmsTable, folderNameDelete);
            }).Logging("treeViewPlaylistTableFolderContextMenuItemDeleteFolderClick");
        }
    }

    private void treeViewPlaylistTableFolderContextMenuItemChangeFolderNameClick(object sender, RoutedEventArgs e)
    {
        EditableTextBlock editableTextBlock = _getETBFromContextMenuClickEvent(sender);
        if (editableTextBlock != null)
        {
            editableTextBlock.IsInEditMode = true;
        }
    }

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
        if (viewModel == null || (Settings.Default.ShowDiffBMSInstallConfirmMsg && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_manual_installation, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) != MessageBoxResult.OK))
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
        if (!(sender is ContextMenu { PlacementTarget: DataGridRow placementTarget } contextMenu))
        {
            return;
        }
        BMSFile bmsFile = placementTarget.DataContext as BMSFile;
        VirtualBMSFile virtualBMSFile = placementTarget.DataContext as VirtualBMSFile;
        if (bmsFile == null && virtualBMSFile == null)
        {
            return;
        }
        List<BMSFile> list;
        try
        {
            list = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
        }
        catch
        {
            return;
        }
        if (!(base.DataContext is MainWindowViewModel mainWindowViewModel))
        {
            return;
        }
        if (songInfoCache == null || songInfoCache.md5 != bmsFile.hash)
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
        if (virtualBMSFile != null)
        {
            if (menuItem != null)
            {
                if (virtualBMSFile.Url != null && virtualBMSFile.Url.IsAbsoluteUri)
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
                if (virtualBMSFile.Url_diff != null && virtualBMSFile.Url_diff.IsAbsoluteUri)
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
        bool flag3 = _currentTreeSelectionSection == TreeSelectionSection.InstallPending;
        bool flag4 = _currentTreeSelectionSection == TreeSelectionSection.Playlist;
        if (menuItemOpenInstallDestination != null)
        {
            bool flag5 = flag3 && bmsFile != null && !(bmsFile is VirtualBMSFile);
            menuItemOpenInstallDestination.Visibility = (flag5 ? Visibility.Visible : Visibility.Collapsed);
            menuItemOpenInstallDestination.IsEnabled = flag5;
        }
        if (menuItem8 != null)
        {
            bool flag6 = flag3;
            menuItem8.Visibility = ((!flag6) ? Visibility.Collapsed : Visibility.Visible);
            menuItem8.IsEnabled = flag6;
        }
        if (menuItem10 != null)
        {
            bool flag7 = !flag4;
            menuItem10.Visibility = ((!flag7) ? Visibility.Collapsed : Visibility.Visible);
            menuItem10.IsEnabled = flag7;
        }
        if (menuItem13 != null)
        {
            bool flag8 = !flag3;
            menuItem13.Visibility = ((!flag8) ? Visibility.Collapsed : Visibility.Visible);
            menuItem13.IsEnabled = flag8 && list.Any((BMSFile f) => !string.IsNullOrWhiteSpace(f.path) && File.Exists(f.path));
        }
        if (menuItem14 != null)
        {
            bool flag9 = flag4;
            menuItem14.Visibility = ((!flag9) ? Visibility.Collapsed : Visibility.Visible);
            menuItem14.IsEnabled = flag9;
        }
        if (menuItem15 != null)
        {
            bool flag10 = !flag4;
            menuItem15.Visibility = ((!flag10) ? Visibility.Collapsed : Visibility.Visible);
            menuItem15.IsEnabled = flag10;
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
            bool flag11 = !flag4 && !flag3;
            separator.Visibility = ((!flag11) ? Visibility.Collapsed : Visibility.Visible);
            separator.IsEnabled = flag11;
        }
        if (menuItem16 != null)
        {
            bool flag12 = !flag4 && !flag3;
            menuItem16.Visibility = ((!flag12) ? Visibility.Collapsed : Visibility.Visible);
            menuItem16.IsEnabled = flag12;
        }
        if (menuItem17 != null)
        {
            bool flag13 = !flag4;
            menuItem17.Visibility = ((!flag13) ? Visibility.Collapsed : Visibility.Visible);
            menuItem17.IsEnabled = flag13;
        }
        if (menuItem9 != null)
        {
            bool flag14 = _currentTreeSelectionSection == TreeSelectionSection.FullScanCheck;
            menuItem9.Visibility = ((!flag14) ? Visibility.Collapsed : Visibility.Visible);
            menuItem9.IsEnabled = flag14;
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
            bool flag15 = !flag3;
            Separator separator3 = separator2;
            Visibility visibility = (menuItem19.Visibility = ((!flag15) ? Visibility.Collapsed : Visibility.Visible));
            separator3.Visibility = visibility;
            Separator separator4 = separator2;
            bool isEnabled = (menuItem19.IsEnabled = flag15);
            separator4.IsEnabled = isEnabled;
        }
    }

    private void dataGridContextMenuItemOpenExplorerClick(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow { Item: BMSFile { path: var path } } } }) || !File.Exists(path))
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
        List<BMSFile> list = dataGrid.SelectedItems.Cast<BMSFile>().Where((BMSFile f) => !(f is VirtualBMSFile)).ToList();
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
        BMSFile bMSFile = placementTarget.Item as BMSFile;
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
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow { Item: BMSFile item } placementTarget } }))
        {
            return;
        }
        VirtualBMSFile virtualBMSFile = placementTarget.Item as VirtualBMSFile;
        string text;
        if (!string.IsNullOrWhiteSpace(item.hash))
        {
            text = _getLR2IRrankingPageURL(item.hash);
        }
        else
        {
            if (virtualBMSFile == null || string.IsNullOrWhiteSpace(virtualBMSFile.lr2_bmsid))
            {
                return;
            }
            text = _getLR2IRrankingPageURL(virtualBMSFile.lr2_bmsid);
        }
        if (text != null)
        {
            Process.Start(text);
        }
    }

    private void dataGridContextMenuItemOpenURLClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow { Item: VirtualBMSFile item } } })
        {
            Process.Start(item.Url.ToString());
        }
    }

    private void dataGridContextMenuItemOpenURLdiffClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow { Item: VirtualBMSFile item } } })
        {
            Process.Start(item.Url_diff.ToString());
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
        VirtualBMSFile bmsFileVirtual = placementTarget.DataContext as VirtualBMSFile;
        if (bmsFileVirtual == null)
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
                        BMSLibrary.IRSongInfo lR2IRSongInfoCache = viewModel.GetLR2IRSongInfoCache(bmsFileVirtual);
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
        BMSFile bMSFile = dataGrid.SelectedItem as BMSFile;
        if (bMSFile == null)
        {
            return;
        }
        if (bMSFile is VirtualBMSFile)
        {
            BMSFile nonVirtualBMSFile = ((VirtualBMSFile)bMSFile).GetNonVirtualBMSFile();
            if (nonVirtualBMSFile == null)
            {
                gridBMSPlayerControlsTitleForMovie.Text = bMSFile.Title;
                gridBMSPlayerControlsSubtitleForMovie.Text = string.Empty;
                gridBMSPlayerControlsArtistForMovie.Text = bMSFile.Artist;
                return;
            }
            bMSFile = nonVirtualBMSFile;
        }
        gridBMSPlayerControlsTitleForMovie.Text = bMSFile.title;
        gridBMSPlayerControlsSubtitleForMovie.Text = bMSFile.subtitle;
        gridBMSPlayerControlsArtistForMovie.Text = bMSFile.artist;
    }

    private void songInfoCacheToUrlLists(BMSLibrary.IRSongInfo info, Uri original, Uri diff, out List<Uri> urls, out List<Uri> urls_diff)
    {
        if (songInfoCache != null)
        {
            urls = (from s in songInfoCache.url.Split(' ')
                    where !string.IsNullOrWhiteSpace(s)
                    select s).Select(delegate (string s)
                {
                    s = dropBoxRegex.Replace(s, "https://dl.dropboxusercontent.com/$1/$2.$3");
                    s = gdriveRegex.Replace(s, "https://docs.google.com/uc?export=download&id=$2");
                    s = odriveRegex.Replace(s, "https://onedrive.live.com/download?$1");
                    return new Uri(s, UriKind.Absolute);
                }).ToList();
            urls_diff = (from s in songInfoCache.url_diff.Split(' ')
                         where !string.IsNullOrWhiteSpace(s)
                         select new Uri(s, UriKind.Absolute)).ToList();
        }
        else
        {
            urls = new List<Uri>();
            urls_diff = new List<Uri>();
        }
        if (original != null && original.IsAbsoluteUri)
        {
            string input = original.ToString();
            input = dropBoxRegex.Replace(input, "https://dl.dropboxusercontent.com/$1/$2.$3");
            input = gdriveRegex.Replace(input, "https://docs.google.com/uc?export=download&id=$2");
            input = odriveRegex.Replace(input, "https://onedrive.live.com/download?$1");
            urls.Add(new Uri(input, UriKind.Absolute));
        }
        if (diff != null && diff.IsAbsoluteUri)
        {
            string input2 = diff.ToString();
            input2 = dropBoxRegex.Replace(input2, "https://dl.dropboxusercontent.com/$1/$2.$3");
            input2 = gdriveRegex.Replace(input2, "https://docs.google.com/uc?export=download&id=$2");
            input2 = odriveRegex.Replace(input2, "https://onedrive.live.com/download?$1");
            urls_diff.Add(new Uri(input2, UriKind.Absolute));
        }
    }

    private void dataGridContextMenuSearchLinkOpened(object sender, RoutedEventArgs e)
    {
        MenuItem menuItem = sender as MenuItem;
        if (menuItem == null || !(menuItem.Parent is ContextMenu { PlacementTarget: DataGridRow placementTarget }))
        {
            return;
        }
        VirtualBMSFile bmsFileVirtual = placementTarget.DataContext as VirtualBMSFile;
        if (bmsFileVirtual == null)
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
                        BMSLibrary.IRSongInfo lR2IRSongInfoCache = viewModel.GetLR2IRSongInfoCache(bmsFileVirtual, seaarchAggressively: true);
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
                songInfoCacheToUrlLists(songInfoCache, bmsFileVirtual.Url, bmsFileVirtual.Url_diff, out var urls, out var urls_diff);
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
                string input = bmsFileVirtual.comment + Environment.NewLine + ((songInfoCache == null) ? string.Empty : songInfoCache.comment) + Environment.NewLine + bmsFileVirtual.name_diff;
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

    private async Task<bool> downloadAndInstall(Uri uri)
    {
        string tempDirectory = TempDirectoryPublisher.Get();
        string filePath = string.Empty;
        await Task.Run(delegate
        {
            try
            {
                HttpWebResponse httpWebResponse = (HttpWebResponse)((HttpWebRequest)WebRequest.Create(uri.ToString())).GetResponse();
                using Stream stream = httpWebResponse.GetResponseStream();
                if (httpWebResponse.ContentLength != 0L && httpWebResponse.ContentLength <= 536870912)
                {
                    string fileName = ((httpWebResponse.Headers["Content-Disposition"] != null) ? Regex.Replace(httpWebResponse.Headers["Content-Disposition"], ".*filename=\"([^\"]+)\".*", "$1") : ((httpWebResponse.Headers["Location"] != null) ? Path.GetFileName(httpWebResponse.Headers["Location"]) : ((Path.GetFileName(uri.ToString()).Contains('?') || Path.GetFileName(uri.ToString()).Contains('=')) ? Path.GetFileName(httpWebResponse.ResponseUri.ToString()) : Path.GetFileName(uri.ToString()))));
                    if (!BMSFile.bmsExtensions.Concat(new string[4] { ".zip", ".7z", ".rar", "lzh" }).All((string e) => !fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    {
                        filePath = Path.Combine(tempDirectory, fileName);
                        using FileStream destination = File.Create(filePath);
                        stream.CopyTo(destination);
                        return;
                    }
                }
            }
            catch
            {
            }
        });
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            installBMSFiles(new string[1] { filePath });
            return true;
        }
        return false;
    }

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
                    if (!uri.ToString().EndsWith("/") && !uri.ToString().EndsWith(".htm") && !uri.ToString().EndsWith(".html") && await downloadAndInstall(uri))
                    {
                        newlyInstalledTreeViewItem.IsExpanded = true;
                        return;
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
        List<BMSFile> bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
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
        if (!(e.Source is MenuItem { Parent: ContextMenu { PlacementTarget: DataGridRow placementTarget } }) || !(placementTarget.Item is BMSFile))
        {
            return;
        }
        List<BMSFile> bmsFiles;
        try
        {
            bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
        }
        catch
        {
            return;
        }
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
        List<BMSFile> bmsFiles = (from BMSFile f in dataGrid.SelectedItems
                                  select (!(f is VirtualBMSFile)) ? f : (((VirtualBMSFile)f).GetNonVirtualBMSFile() ?? f)).ToList();
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
        List<BMSFile> bmsFiles = (from BMSFile f in dataGrid.SelectedItems
                                  select (!(f is VirtualBMSFile)) ? f : (((VirtualBMSFile)f).GetNonVirtualBMSFile() ?? f)).ToList();
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
        List<BMSFile> bmsFiles = (from BMSFile f in dataGrid.SelectedItems
                                  select (!(f is VirtualBMSFile)) ? f : (((VirtualBMSFile)f).GetNonVirtualBMSFile() ?? f)).ToList();
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
        List<BMSFile> bmsFiles = (from BMSFile f in dataGrid.SelectedItems
                                  select (!(f is VirtualBMSFile)) ? f : (((VirtualBMSFile)f).GetNonVirtualBMSFile() ?? f)).ToList();
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
        List<BMSFile> list = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
        List<VirtualBMSFile> list2 = list.Where((BMSFile f) => f is VirtualBMSFile).Cast<VirtualBMSFile>().ToList();
        list.Except(list2).ToList();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (list2.Count <= 0)
        {
            return;
        }
        foreach (IGrouping<BMSTable, BMSTableEntry> enGrp in from f in list2
                                                             select f.ToBMSTableEntry() into en
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
        List<BMSFile> bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
        List<VirtualBMSFile> second = bmsFiles.Where((BMSFile f) => f is VirtualBMSFile).Cast<VirtualBMSFile>().ToList();
        bmsFiles = bmsFiles.Except(second).ToList();
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
        List<BMSFile> bmsFiles = (from BMSFile f in dataGrid.SelectedItems
                                  where !(f is VirtualBMSFile)
                                  select f).ToList();
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
        List<BMSFile> bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
        List<VirtualBMSFile> second = bmsFiles.Where((BMSFile f) => f is VirtualBMSFile).Cast<VirtualBMSFile>().ToList();
        bmsFiles = bmsFiles.Except(second).ToList();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        bool isPendingSelected = _currentTreeSelectionSection == TreeSelectionSection.InstallPending;
        if (bmsFiles.Count > 0 && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_recycle, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                if (isPendingSelected)
                {
                    viewModel.RemovePendingBMSFiles(bmsFiles);
                }
                else
                {
                    viewModel.RemoveBMSFiles(bmsFiles);
                }
            }).Logging("dataGridContextMenuItemRemoveBMSFileClick");
        }
    }

    private async void dataGridContextMenuItemMoveFileClick(object sender, RoutedEventArgs e)
    {
        List<BMSFile> bmsFiles = (from BMSFile f in dataGrid.SelectedItems
                                  select (!(f is VirtualBMSFile)) ? f : (((VirtualBMSFile)f).GetNonVirtualBMSFile() ?? f) into f
                                  where !string.IsNullOrWhiteSpace(f?.path)
                                  select f).ToList();
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
            List<BMSFile> list = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
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
            List<BMSFile> list = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
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
            List<BMSFile> list = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
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
        List<BMSFile> bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
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

    private async void manualInstallSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
        if (bmsFiles == null || bmsFiles.Count() == 0)
        {
            return;
        }
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        e.Handled = true;
        if (Settings.Default.ShowDiffBMSInstallConfirmMsg && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_manual_installation, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk) != MessageBoxResult.OK)
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
        List<BMSFile> bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
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

    private async void searchMergeDestinationSelectedBMS(object sender, RoutedEventArgs e)
    {
        if (!(e.Source is MenuItem menuItem) || !(((menuItem.Parent as MenuItem).Parent as ContextMenu).PlacementTarget is DataGridRow))
        {
            return;
        }
        List<BMSFile> bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
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
        BMSFile[] bmsFiles = (from BMSFile f in dataGrid.SelectedItems
                              where File.Exists(f.path)
                              select f).ToArray();
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
        List<BMSFile> bmsFiles;
        try
        {
            bmsFiles = ((IList)e.Data.GetData("System.Windows.Controls.SelectedItemCollection")).Cast<BMSFile>().ToList();
        }
        catch
        {
            return;
        }
        if (bmsFiles == null || bmsFiles.Count == 0)
        {
            return;
        }
        string folderName;
        if (!(treeViewItem2.DataContext is Tuple<string, bool> tuple))
        {
            folderName = null;
        }
        else
        {
            if (tuple.Item2)
            {
                return;
            }
            folderName = tuple.Item1;
        }
        Task.Run(delegate
        {
            viewModel.AddEntriesToFolderBMSTable(bmsFiles, table, folderName);
        }).Logging("playlistTableDrop");
    }

    private void playlistTableDragOver(object sender, DragEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: BMSTable { is_external_sync: false } })
        {
            _ = base.DataContext;
            TreeViewItem treeViewItem2 = WPFUtil.FindVisualParent<TreeViewItem>((FrameworkElement)e.OriginalSource);
            if (treeViewItem2 != null && !(treeViewItem2.DataContext is Tuple<string, bool> { Item2: not false }))
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
        if (!bMSTable.is_external_sync && (!(treeViewItem.DataContext is Tuple<string, bool>) || !((Tuple<string, bool>)treeViewItem.DataContext).Item2))
        {
            treeViewItem.Background = SystemColors.HighlightBrush;
        }
        if (!(treeViewItem.DataContext is Tuple<string, bool>))
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
            if (!bMSTable.is_external_sync && (!(treeViewItem2.DataContext is Tuple<string, bool>) || !((Tuple<string, bool>)treeViewItem2.DataContext).Item2))
            {
                treeViewItem2.Background = Brushes.Transparent;
            }
            if (!(treeViewItem2.DataContext is Tuple<string, bool>))
            {
                treeViewItemInstantStoryBoardPlaylistTable.Stop(this);
                treeViewItemInstantStoryBoardPlaylistTable.Children.Clear();
            }
        }
    }

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
                    DataGridRow selectedRow = dataGrid.GetSelectedRow();
                    if (selectedRow == null)
                    {
                        break;
                    }
                    if (selectedRow.IsEditing)
                    {
                        dataGrid.CommitEdit();
                        break;
                    }
                    MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
                    if (selectedRow != null && viewModel != null)
                    {
                        _renewBMSPlayerControlInfo(selectedRow);
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
