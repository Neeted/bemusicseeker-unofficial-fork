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
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using Ribbit.Windows;

namespace BeMusicSeeker.Views;

public partial class MainWindow : Window, IComponentConnector, IStyleConnector
{
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
                return (int)_getValueOfPropertyPath(binding.Source, binding.Path.Path.Substring(0, binding.Path.Path.LastIndexOf('.')) + ".DisplayIndex");
            }).Select((DataGridColumn v, int i) => new { v, i }))
            {
                item.v.DisplayIndex = item.i;
            }
        });
    }

    private static object _getValueOfPropertyPath(object value, string path)
    {
        Type type = value.GetType();
        string[] array = path.Split('.');
        foreach (string name in array)
        {
            PropertyInfo property = type.GetProperty(name);
            value = property.GetValue(value, null);
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

    private static Action<T> _getSetterOfPropertyPath<T>(object value, string path)
    {
        Type type = value.GetType();
        PropertyInfo propertyInfo = null;
        object firstArgument = null;
        string[] array = path.Split('.');
        foreach (string name in array)
        {
            propertyInfo = type.GetProperty(name);
            firstArgument = value;
            value = propertyInfo.GetValue(value, null);
            type = propertyInfo.PropertyType;
        }
        return Delegate.CreateDelegate(typeof(Action<T>), firstArgument, propertyInfo.GetSetMethod()) as Action<T>;
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
        else if (bMSFile != null && path == bMSFile.GetName((BMSFile f) => f.Folder) && _isTreeViewItemSelectedInclChildren(treeViewItemInstallPending))
        {
            e.Cancel = true;
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
        else if (bmsFile != null && e.EditAction == DataGridEditAction.Commit && path == bmsFile.GetName((BMSFile f) => f.Folder))
        {
            string newFolder = textBox.Text;
            dataGrid.CancelEdit();
            Task.Run(delegate
            {
                viewModel.RenameBMSFolder(bmsFile, newFolder);
            }).Logging("dataGridCellEditEnding");
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
        if (e.Source is TreeViewItem && treeViewItemPlaylist.Items.Count == 0)
        {
            MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
            Task.Run(delegate
            {
                viewModel.ExecPlaylistFilter(null);
            }).Logging("playlistRootSelect");
        }
    }

    private void treeViewLeftClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeView treeView)
        {
            treeView.Focus();
        }
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
        foreach (Control item in (IEnumerable)contextMenu.Items)
        {
            switch (item.Name)
            {
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
        menuItem.IsEnabled = dataContext.Page_url != null || dataContext.GetAbsoluteHeaderUrl() != null;
        menuItem2.IsEnabled = dataContext.Page_url != null && dataContext.Page_url.Scheme != "bmseeker" && dataContext.is_external_sync && mainWindowViewModel.LR2ID != 0;
        menuItem4.IsEnabled = !dataContext.is_external_sync;
        menuItem3.IsEnabled = true;
        menuItem5.IsEnabled = true;
        menuItem6.IsEnabled = !mainWindowViewModel.IsWriteLockHeldBMSTablesInitializeMin && !mainWindowViewModel.IsWriteLockHeldBMSTables && !mainWindowViewModel.IsWriteLockHeldAnyBMSTable;
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
        TreeViewItem treeViewItem = null;
        int num = treeViewItemPlaylist.Items.IndexOf(bmsTable);
        if (num == 0 && treeViewItemPlaylist.Items.Count == 1)
        {
            treeViewItem = treeViewItemPlaylist;
        }
        else if (num != -1)
        {
            int index = ((treeViewItemPlaylist.Items.Count - 1 == num) ? (num - 1) : (num + 1));
            treeViewItem = WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(treeViewItemPlaylist, treeViewItemPlaylist.Items[index]);
        }
        if (treeViewItem != null)
        {
            treeViewItem.IsSelected = true;
            treeViewItem.Focus();
        }
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
        TreeViewItem treeViewItem = null;
        int num = treeViewItemInstallPending.Items.IndexOf(pkg);
        if (num == 0 && treeViewItemInstallPending.Items.Count == 1)
        {
            treeViewItem = treeViewItemInstallPending;
        }
        else if (num != -1)
        {
            int index = ((treeViewItemInstallPending.Items.Count - 1 == num) ? (num - 1) : (num + 1));
            treeViewItem = WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(treeViewItemInstallPending, treeViewItemInstallPending.Items[index]);
        }
        if (treeViewItem != null)
        {
            treeViewItem.IsSelected = true;
            treeViewItem.Focus();
        }
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
        TreeViewItem treeViewItem = null;
        int num = newlyInstalledTreeViewItem.Items.IndexOf(pkg);
        if (num == 0 && newlyInstalledTreeViewItem.Items.Count == 1)
        {
            treeViewItem = newlyInstalledTreeViewItem;
        }
        else if (num != -1)
        {
            int index = ((newlyInstalledTreeViewItem.Items.Count - 1 == num) ? (num - 1) : (num + 1));
            treeViewItem = WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(newlyInstalledTreeViewItem, newlyInstalledTreeViewItem.Items[index]);
        }
        if (treeViewItem != null)
        {
            treeViewItem.IsSelected = true;
            treeViewItem.Focus();
        }
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
        TreeViewItem treeViewItem = null;
        int num = treeViewItemInstallPending.Items.IndexOf(pkg);
        if (num == 0 && treeViewItemInstallPending.Items.Count == 1)
        {
            treeViewItem = treeViewItemInstallPending;
        }
        else if (num != -1)
        {
            int index = ((treeViewItemInstallPending.Items.Count - 1 == num) ? (num - 1) : (num + 1));
            treeViewItem = WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(treeViewItemInstallPending, treeViewItemInstallPending.Items[index]);
        }
        if (treeViewItem != null)
        {
            treeViewItem.IsSelected = true;
            treeViewItem.Focus();
        }
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
        TreeViewItem treeViewItem = null;
        int num = treeViewItemInstallPending.Items.IndexOf(pkg);
        if (num == 0 && treeViewItemInstallPending.Items.Count == 1)
        {
            treeViewItem = treeViewItemInstallPending;
        }
        else if (num != -1)
        {
            int index = ((treeViewItemInstallPending.Items.Count - 1 == num) ? (num - 1) : (num + 1));
            treeViewItem = WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(treeViewItemInstallPending, treeViewItemInstallPending.Items[index]);
        }
        if (treeViewItem != null)
        {
            treeViewItem.IsSelected = true;
            treeViewItem.Focus();
        }
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
        IEnumerable<BMSFile> enumerable = null;
        if (!(treeViewItem.DataContext is List<BMSFile>))
        {
            return;
        }
        enumerable = (List<BMSFile>)treeViewItem.DataContext;
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
            List<string> list = enumerable.Select((BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path)).Distinct(StringComparer.OrdinalIgnoreCase).Except(new string[1] { dataContext }, StringComparer.OrdinalIgnoreCase)
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
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (viewModel == null)
        {
            return;
        }
        string srcPath = tag.DataContext as string;
        if (string.IsNullOrWhiteSpace(srcPath))
        {
            return;
        }
        string dstPath = menuItem.DataContext as string;
        if (!string.IsNullOrWhiteSpace(dstPath) && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_merge_bms_folder + Environment.NewLine + Environment.NewLine + BeMusicSeeker.Properties.Resources.Msg_merge_bms_target + ": " + srcPath + Environment.NewLine + BeMusicSeeker.Properties.Resources.Msg_merge_bms_destination + ": " + dstPath, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.MergeBMSDirectory(srcPath, dstPath);
            }).Logging("treeViewDuplicateFolderContextMenuItemMergeIntoTargetClick");
        }
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
        bool flag3 = _isTreeViewItemSelectedInclChildren(treeViewItemInstallPending);
        bool flag4 = _isTreeViewItemSelectedInclChildren(treeViewItemPlaylist);
        if (menuItem8 != null)
        {
            bool flag5 = flag3;
            menuItem8.Visibility = ((!flag5) ? Visibility.Collapsed : Visibility.Visible);
            menuItem8.IsEnabled = flag5;
        }
        if (menuItem10 != null)
        {
            bool flag6 = !flag4;
            menuItem10.Visibility = ((!flag6) ? Visibility.Collapsed : Visibility.Visible);
            menuItem10.IsEnabled = flag6;
        }
        if (menuItem13 != null)
        {
            bool flag7 = !flag3;
            menuItem13.Visibility = ((!flag7) ? Visibility.Collapsed : Visibility.Visible);
            menuItem13.IsEnabled = flag7 && list.Any((BMSFile f) => !string.IsNullOrWhiteSpace(f.path) && File.Exists(f.path));
        }
        if (menuItem14 != null)
        {
            bool flag8 = flag4;
            menuItem14.Visibility = ((!flag8) ? Visibility.Collapsed : Visibility.Visible);
            menuItem14.IsEnabled = flag8;
        }
        if (menuItem15 != null)
        {
            bool flag9 = !flag4 && !flag3;
            menuItem15.Visibility = ((!flag9) ? Visibility.Collapsed : Visibility.Visible);
            menuItem15.IsEnabled = flag9;
        }
        if (separator != null)
        {
            bool flag10 = !flag4 && !flag3;
            separator.Visibility = ((!flag10) ? Visibility.Collapsed : Visibility.Visible);
            separator.IsEnabled = flag10;
        }
        if (menuItem16 != null)
        {
            bool flag11 = !flag4 && !flag3;
            menuItem16.Visibility = ((!flag11) ? Visibility.Collapsed : Visibility.Visible);
            menuItem16.IsEnabled = flag11;
        }
        if (menuItem17 != null)
        {
            bool flag12 = !flag4;
            menuItem17.Visibility = ((!flag12) ? Visibility.Collapsed : Visibility.Visible);
            menuItem17.IsEnabled = flag12;
        }
        if (menuItem9 != null)
        {
            bool flag13 = _isTreeViewItemSelectedInclChildren(treeViewItemFullScanCheck);
            menuItem9.Visibility = ((!flag13) ? Visibility.Collapsed : Visibility.Visible);
            menuItem9.IsEnabled = flag13;
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
            bool flag14 = !flag3;
            Separator separator3 = separator2;
            Visibility visibility = (menuItem19.Visibility = ((!flag14) ? Visibility.Collapsed : Visibility.Visible));
            separator3.Visibility = visibility;
            Separator separator4 = separator2;
            bool isEnabled = (menuItem19.IsEnabled = flag14);
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
                viewModel.RenameBMSFilesExtensions(list, ".bmx");
            }
            if (list2.Count > 0)
            {
                viewModel.RenameBMSFilesExtensions(list2, ".pmx");
            }
        }).Logging("dataGridContextMenuItemRenameBMSFileClick");
    }

    private void dataGridContextMenuItemRemoveBMSFileClick(object sender, RoutedEventArgs e)
    {
        List<BMSFile> bmsFiles = dataGrid.SelectedItems.Cast<BMSFile>().ToList();
        List<VirtualBMSFile> second = bmsFiles.Where((BMSFile f) => f is VirtualBMSFile).Cast<VirtualBMSFile>().ToList();
        bmsFiles = bmsFiles.Except(second).ToList();
        MainWindowViewModel viewModel = base.DataContext as MainWindowViewModel;
        if (bmsFiles.Count > 0 && MessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_move_to_recycle, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.Cancel)
        {
            Task.Run(delegate
            {
                viewModel.RemoveBMSFiles(bmsFiles);
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

    private bool _isTreeViewItemSelectedInclChildren(TreeViewItem treeViewItem)
    {
        if (treeViewItem.IsSelected)
        {
            return true;
        }
        foreach (TreeViewItem visualChild in WPFUtil.GetVisualChildren<TreeViewItem>(treeViewItem))
        {
            if (_isTreeViewItemSelectedInclChildren(visualChild))
            {
                return true;
            }
        }
        return false;
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
            TreeViewItem treeViewItem = null;
            int num = treeViewItemInstallPending.Items.IndexOf(treeView.SelectedItem);
            if (num == 0 && treeViewItemInstallPending.Items.Count == 1)
            {
                treeViewItem = treeViewItemInstallPending;
            }
            else if (num != -1)
            {
                int index = ((treeViewItemInstallPending.Items.Count - 1 == num) ? (num - 1) : (num + 1));
                treeViewItem = WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(treeViewItemInstallPending, treeViewItemInstallPending.Items[index]);
            }
            if (treeViewItem != null)
            {
                treeViewItem.IsSelected = true;
                treeViewItem.Focus();
            }
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
            TreeViewItem treeViewItem = null;
            int num = treeViewItemInstallPending.Items.IndexOf(treeView.SelectedItem);
            if (num == 0 && treeViewItemInstallPending.Items.Count == 1)
            {
                treeViewItem = treeViewItemInstallPending;
            }
            else if (num != -1)
            {
                int index = ((treeViewItemInstallPending.Items.Count - 1 == num) ? (num - 1) : (num + 1));
                treeViewItem = WPFUtil.FindVisualChildSearchedByDataContext<TreeViewItem>(treeViewItemInstallPending, treeViewItemInstallPending.Items[index]);
            }
            if (treeViewItem != null)
            {
                treeViewItem.IsSelected = true;
                treeViewItem.Focus();
            }
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

    private void treeViewSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is TreeView)
        {
            e.Handled = true;
            if (e.NewValue == null && e.OldValue != null && e.OldValue is BMSTable)
            {
                ((TreeView)sender).SelectTreeViewItemSearchedByHeader(((BMSTable)e.OldValue).name);
            }
        }
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
