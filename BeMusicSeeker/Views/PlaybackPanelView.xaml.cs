using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Livet.EventListeners;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Views;

public partial class PlaybackPanelView : UserControl
{
    public static readonly DependencyProperty OverlayVisibilityProperty = DependencyProperty.Register(nameof(OverlayVisibility), typeof(Visibility), typeof(PlaybackPanelView), new PropertyMetadata(Visibility.Collapsed));
    public static readonly DependencyProperty BrowserHtmlProperty = DependencyProperty.Register(nameof(BrowserHtml), typeof(string), typeof(PlaybackPanelView), new PropertyMetadata(null));
    private DispatcherTimer gridBMSPlayerControlsPreviousButtonClickTimer;
    private readonly PropertyChangedEventListener settingsDefaultEventListener;
    private BitmapSource _panelImage;
    private bool isClosingOrClosed;

    public PlaybackPanelView()
    {
        InitializeComponent();
        settingsDefaultEventListener = new PropertyChangedEventListener(Settings.Default);
        settingsDefaultEventListener.RegisterHandler(() => Settings.Default.UseExternalPanelImage, delegate { ResetPanelImage(); });
        settingsDefaultEventListener.RegisterHandler(() => Settings.Default.StagefilePath, delegate { ResetPanelImage(); });
        gridBMSPlayerImage.Source = PanelImage;
        Unloaded += (_, _) => isClosingOrClosed = true;
        Loaded += (_, _) => isClosingOrClosed = false;
    }

    public event RoutedEventHandler SettingsRequested;
    public Visibility OverlayVisibility { get => (Visibility)GetValue(OverlayVisibilityProperty); set => SetValue(OverlayVisibilityProperty, value); }
    public string BrowserHtml { get => (string)GetValue(BrowserHtmlProperty); set => SetValue(BrowserHtmlProperty, value); }
    public IntPtr PlayerHostHandle => _panel.Handle;
    private PlaybackPanelViewModel PlaybackPanel => DataContext as PlaybackPanelViewModel ?? throw new InvalidOperationException("Playback panel DataContext is unavailable.");

    public PlayerPanelState PanelState
    {
        get => PlaybackPanel.PlayerPanelState;
        set
        {
            if (PlaybackPanel.PlayerPanelState == value) return;
            if (value.HasFlag(PlayerPanelState.BMS_PLAYER)) ShowBmsPlayer(); else CollapseBmsPlayer();
            if (value.HasFlag(PlayerPanelState.MOVIE_PLAYER)) ShowBrowser(); else CollapseBrowser();
            PlaybackPanel.PlayerPanelState = value;
        }
    }

    private BitmapSource PanelImage
    {
        get
        {
            if (_panelImage != null) return _panelImage;
            if (Settings.Default.UseExternalPanelImage)
            {
                try
                {
                    using var stream = new MemoryStream(LongPathFileSystem.ReadAllBytes(Settings.Default.StagefilePath));
                    return _panelImage = new WriteableBitmap(BitmapFrame.Create(stream));
                }
                catch { }
            }
            return _panelImage = new BitmapImage(new Uri("pack://application:,,,/resources/default_image.jpg"));
        }
    }

    private void ResetPanelImage() => _panelImage = null;

    public void ConfigureBrowserHost()
    {
        if (webBrowser == null) return;
        object browser = typeof(WebBrowser).GetProperty("AxIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(webBrowser, null);
        browser?.GetType().InvokeMember("Silent", BindingFlags.SetProperty, null, browser, new object[] { true });
        browser?.GetType().InvokeMember("RegisterAsDropTarget", BindingFlags.SetProperty, null, browser, new object[] { false });
    }

    public void RefreshArtwork(BMSFile bmsFile)
    {
        if (bmsFile == null) return;
        WriteableBitmap stage = null;
        try
        {
            string path = string.IsNullOrWhiteSpace(bmsFile.path) || string.IsNullOrWhiteSpace(bmsFile.stagefile) ? string.Empty : Path.Combine(DirectoryExt.GetDirectoryNameSimple(bmsFile.path), bmsFile.stagefile);
            if (!string.IsNullOrWhiteSpace(path) && LongPathFileSystem.FileExists(path))
            {
                using var stream = new MemoryStream(LongPathFileSystem.ReadAllBytes(path));
                stage = new WriteableBitmap(BitmapFrame.Create(stream));
            }
            gridBMSPlayerImage.Source = stage ?? PanelImage;
        }
        catch { gridBMSPlayerImage.Source = PanelImage; }
        try
        {
            string path = string.IsNullOrWhiteSpace(bmsFile.path) || string.IsNullOrWhiteSpace(bmsFile.banner) ? string.Empty : Path.Combine(DirectoryExt.GetDirectoryNameSimple(bmsFile.path), bmsFile.banner);
            ImageSource banner = null;
            if (!string.IsNullOrWhiteSpace(path) && LongPathFileSystem.FileExists(path))
            {
                using var stream = new MemoryStream(LongPathFileSystem.ReadAllBytes(path));
                banner = new WriteableBitmap(BitmapFrame.Create(stream));
            }
            banner ??= stage;
            gridBMSPlayerControlsBanner.Background = banner == null ? null : new ImageBrush(banner) { Stretch = stage == banner ? Stretch.UniformToFill : Stretch.Fill };
        }
        catch
        {
            gridBMSPlayerControlsBanner.Background = stage == null ? null : new ImageBrush(stage) { Stretch = Stretch.UniformToFill };
        }
        gridBMSPlayerControlsBanner.BorderThickness = gridBMSPlayerControlsBanner.Background == null ? new Thickness(0) : new Thickness(1, 0, 1, 0);
    }
    private void settingsButtonClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, e);
    private void ShowBmsPlayer() => RestoreVisibilityBinding(windowsFormsHost, Visibility.Visible);
    private void CollapseBmsPlayer() => RestoreVisibilityBinding(windowsFormsHost, Visibility.Collapsed);
    private void ShowBrowser() => RestoreVisibilityBinding(webBrowser, Visibility.Visible);
    private void CollapseBrowser() => RestoreVisibilityBinding(webBrowser, Visibility.Collapsed);
    private static void RestoreVisibilityBinding(UIElement element, Visibility visibility)
    {
        if (element == null) return;
        MultiBinding binding = BindingOperations.GetMultiBindingExpression(element, UIElement.VisibilityProperty)?.ParentMultiBinding;
        element.Visibility = visibility;
        if (binding != null) BindingOperations.SetBinding(element, UIElement.VisibilityProperty, binding);
    }

    public void RestoreSelectedSurface() { if (PanelState.HasFlag(PlayerPanelState.BMS_PLAYER)) ShowBmsPlayer(); else if (PanelState.HasFlag(PlayerPanelState.MOVIE_PLAYER)) ShowBrowser(); }
    public void RotatePanelState() => Dispatcher.BeginInvoke((Action)(() => gridBMSPlayerControlsRotatePanelStateButtonClicked()), DispatcherPriority.ContextIdle);
    public bool CanSelectPanelState(PlayerPanelState state) => IsPanelStateValid(state);
    private bool IsPanelStateValid(PlayerPanelState state)
    {
        if (state.HasFlag(PlayerPanelState.BMS_PLAYER))
        {
            if (windowsFormsHost == null || !windowsFormsHost.IsEnabled) return false;
            if (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && PlaybackPanel.UsesUbMplay) return false;
            if (!Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && PlaybackPanel.UsesUbMplay) return true;
            if (PlaybackPanel.UsesLr2Body) return false;
            return PlaybackPanel.UsesBmiIdxView;
        }
        return !state.HasFlag(PlayerPanelState.MOVIE_PLAYER) || (webBrowser != null && webBrowser.IsEnabled && BrowserHtml != null);
    }
    private void gridBMSPlayerControlsRotatePanelStateButtonClicked(object sender = null, RoutedEventArgs e = null)
    {
        if (isClosingOrClosed) return;
        PlayerPanelState state = PanelState;
        do state = state.HasFlag(PlayerPanelState.BMS_PLAYER) ? (state & ~PlayerPanelState.BMS_PLAYER) | PlayerPanelState.MOVIE_PLAYER : !state.HasFlag(PlayerPanelState.MOVIE_PLAYER) ? state | PlayerPanelState.BMS_PLAYER : state & ~PlayerPanelState.MOVIE_PLAYER;
        while (!IsPanelStateValid(state));
        PanelState = state;
    }
    private void gridBMSPlayerControlsRotatePanelStateButtonClicked2(object sender, RoutedEventArgs e) => PanelState ^= PlayerPanelState.TITLE_SMALL;
    private void windowsFormsHostIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!IsPanelStateValid(PanelState)) gridBMSPlayerControlsRotatePanelStateButtonClicked(); }
    private static void forbidNavigating(object sender, NavigatingCancelEventArgs e) => e.Cancel = true;
    private void webBrowserLoadCompleted(object sender, NavigationEventArgs e) { webBrowser.Navigating += forbidNavigating; PanelState = PlayerPanelState.MOVIE_PLAYER; }

    private void gridBMSPlayerControlsNextButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            viewModel.NextCommand.Execute();
        }
    }


    /// <summary>
    /// 内蔵プレーヤーの「前の曲へ (Previous)」ボタンがクリックされた際のイベントハンドラ。
    /// ダブルクリック時は前の曲へ移動し、シングルクリック時は現在の曲を最初から再生し直します。
    /// </summary>
    private void gridBMSPlayerControlsPreviousButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PlaybackPanelViewModel viewModel)
        {
            return;
        }
        gridBMSPlayerControlsPreviousButtonClickTimer ??= new DispatcherTimer(new TimeSpan(0, 0, 0, 0, 500), DispatcherPriority.Background, gridBMSPlayerControlsPreviousButtonSingleClicked, Dispatcher.CurrentDispatcher);
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Stop();
            viewModel.PreviousCommand.Execute();
        }
        else
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Start();
        }
    }

    private void gridBMSPlayerControlsPreviousButtonSingleClicked(object sender, EventArgs e)
    {
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Stop();
            viewModel.RestartCommand.Execute();
        }
    }

    /// <summary>
    /// 内蔵プレーヤーの「再生 (Play)」ボタンがクリックされた際のイベントハンドラ。
    /// 現在の再生状態が停止・一時停止であれば再生を再開または開始します。
    /// </summary>
    private void gridBMSPlayerControlsPlayStartButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            e.Handled = true;
            if (viewModel.IsStoppedOrPaused && IsPanelStateValid(PlayerPanelState.BMS_PLAYER))
            {
                PanelState = PlayerPanelState.BMS_PLAYER;
            }
            viewModel.StartCommand.Execute();
        }
    }

    private void gridBMSPlayerControlsPlayStopButtonClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            viewModel.StopCommand.Execute();
        }
    }

    private void gridBMSPlayerControlsFastForwardButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            viewModel.FastForwardStartCommand.Execute();
        }
    }

    private void gridBMSPlayerControlsFastForwardButtonReleased(object sender, MouseButtonEventArgs e)
    {
        var viewModel = DataContext as PlaybackPanelViewModel;
        viewModel?.FastForwardEndCommand.Execute();
    }

    private void gridBMSPlayerControlsFastForwardButtonReleased(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            viewModel.FastForwardEndCommand.Execute();
        }
    }

    private void gridBMSPlayerControlsFastBackwardButtonClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            viewModel.FastBackwardStartCommand.Execute();
        }
    }

    private void gridBMSPlayerControlsFastBackwardButtonReleased(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            viewModel.FastBackwardEndCommand.Execute();
        }
    }

    private void gridBMSPlayerControlsFastBackwardButtonReleased(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        if (DataContext is PlaybackPanelViewModel viewModel)
        {
            viewModel.FastBackwardEndCommand.Execute();
        }
    }

    private void gridBMSPlayerControlsShowInfoButtonClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlaybackPanelViewModel viewModel && viewModel.UsesUbMplay)
        {
            viewModel.ShowInfoCommand.Execute();
        }
        else
        {
            gridPlayngBmsInfo.Visibility = ((gridPlayngBmsInfo.Visibility != Visibility.Hidden) ? Visibility.Hidden : Visibility.Visible);
        }
    }





    private void sliderPlayerMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var slider = (Slider)sender;
        Point position = e.GetPosition(slider);
        double value = slider.Maximum * Math.Max(0.0, Math.Min(1.0, (position.X - 5.0) / (slider.ActualWidth - 10.0)));
        slider.Value = value;
        if (DataContext is not PlaybackPanelViewModel viewModel)
        {
            return;
        }
        if (viewModel.IsPlaying)
        {
            viewModel.StartCommand.Execute();
        }
    }

    private void sliderPlayerMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PlaybackPanelViewModel viewModel)
        {
            return;
        }
        if (viewModel.IsPaused)
        {
            viewModel.StartCommand.Execute();
        }
    }

    private void sliderPlayerMouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released)
        {
            return;
        }
        if (DataContext is not PlaybackPanelViewModel viewModel)
        {
            return;
        }
        if (viewModel.IsPaused)
        {
            viewModel.StartCommand.Execute();
        }
    }


}
