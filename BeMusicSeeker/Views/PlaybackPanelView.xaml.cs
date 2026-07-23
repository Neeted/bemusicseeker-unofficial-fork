using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Views;

public partial class PlaybackPanelView : UserControl
{
    private const double CompactPanelHeight = 110d;
    private const double ExpandedPanelHeight = 286d;
    public static readonly DependencyProperty OverlayVisibilityProperty = DependencyProperty.Register(nameof(OverlayVisibility), typeof(Visibility), typeof(PlaybackPanelView), new PropertyMetadata(Visibility.Collapsed));
    public static readonly DependencyProperty EffectivePlayerPanelStateProperty = DependencyProperty.Register(nameof(EffectivePlayerPanelState), typeof(PlayerPanelState), typeof(PlaybackPanelView), new PropertyMetadata(PlayerPanelState.TITLE_LARGE));
    public static readonly DependencyProperty SettingsCommandProperty = DependencyProperty.Register(nameof(SettingsCommand), typeof(ICommand), typeof(PlaybackPanelView), new PropertyMetadata(null));
    private readonly PlaybackPreviousButtonGesture previousButtonGesture = new();
    private DispatcherTimer gridBMSPlayerControlsPreviousButtonClickTimer;
    private PlaybackPanelViewModel subscribedPlaybackPanel;
    private BitmapSource _panelImage;
    private bool isClosingOrClosed;

    public PlaybackPanelView()
    {
        InitializeComponent();
        DataContextChanged += PlaybackPanelDataContextChanged;
        Loaded += PlaybackPanelLoaded;
        Unloaded += PlaybackPanelUnloaded;
    }

    public event RoutedEventHandler PlaybackStarting;
    public event RoutedEventHandler PlaybackStarted;
    public Visibility OverlayVisibility { get => (Visibility)GetValue(OverlayVisibilityProperty); set => SetValue(OverlayVisibilityProperty, value); }
    public PlayerPanelState EffectivePlayerPanelState { get => (PlayerPanelState)GetValue(EffectivePlayerPanelStateProperty); private set => SetValue(EffectivePlayerPanelStateProperty, value); }
    public ICommand SettingsCommand { get => (ICommand)GetValue(SettingsCommandProperty); set => SetValue(SettingsCommandProperty, value); }
    public IntPtr PlayerHostHandle => _panel.Handle;
    private PlaybackPanelViewModel PlaybackPanel => DataContext as PlaybackPanelViewModel ?? throw new InvalidOperationException("Playback panel DataContext is unavailable.");

    private BitmapSource PanelImage
    {
        get
        {
            if (_panelImage != null) return _panelImage;
            if (PlaybackPanel.UseExternalPanelImage)
            {
                try
                {
                    using var stream = new MemoryStream(LongPathFileSystem.ReadAllBytes(PlaybackPanel.StagefilePath));
                    return _panelImage = new WriteableBitmap(BitmapFrame.Create(stream));
                }
                catch { }
            }
            return _panelImage = new BitmapImage(new Uri("pack://application:,,,/resources/default_image.jpg"));
        }
    }

    private void ResetPanelImage() => _panelImage = null;

    private void PlaybackPanelLoaded(object sender, RoutedEventArgs e)
    {
        isClosingOrClosed = false;
        previousButtonGesture.Activate();
        ApplyPlaybackPanelDataContext(DataContext);
    }

    private void PlaybackPanelUnloaded(object sender, RoutedEventArgs e)
    {
        isClosingOrClosed = true;
        CancelPreviousButtonGesture();
        SubscribePlaybackPanel(null);
    }

    private void PlaybackPanelDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            CancelPreviousButtonGesture();
            previousButtonGesture.Activate();
            ApplyPlaybackPanelDataContext(e.NewValue);
        }
    }

    private void ApplyPlaybackPanelDataContext(object dataContext)
    {
        if (dataContext != null && dataContext is not PlaybackPanelViewModel)
        {
            throw new InvalidOperationException("Playback panel DataContext must be PlaybackPanelViewModel.");
        }
        var playbackPanel = dataContext as PlaybackPanelViewModel;
        ApplyPlaybackPanelHeight(playbackPanel?.PlayerPanelState ?? PlayerPanelState.TITLE_LARGE, false);
        ApplySelectedSurface(playbackPanel?.PlayerPanelState ?? PlayerPanelState.TITLE_LARGE);
        SubscribePlaybackPanel(playbackPanel);
        ResetPanelImage();
        ApplyEmptyArtwork(showDefaultImage: dataContext != null);
        if (playbackPanel?.DisplayedBmsPlayerFile != null)
        {
            RefreshArtwork(playbackPanel.DisplayedBmsPlayerFile);
        }
    }

    private void SubscribePlaybackPanel(PlaybackPanelViewModel playbackPanel)
    {
        if (ReferenceEquals(subscribedPlaybackPanel, playbackPanel))
        {
            return;
        }
        if (subscribedPlaybackPanel != null)
        {
            subscribedPlaybackPanel.PropertyChanged -= PlaybackPanelPropertyChanged;
            subscribedPlaybackPanel.PlaybackStarting -= PlaybackPanelPlaybackStarting;
            subscribedPlaybackPanel.PlaybackStarted -= PlaybackPanelPlaybackStarted;
        }
        subscribedPlaybackPanel = playbackPanel;
        if (subscribedPlaybackPanel != null)
        {
            subscribedPlaybackPanel.PropertyChanged += PlaybackPanelPropertyChanged;
            subscribedPlaybackPanel.PlaybackStarting += PlaybackPanelPlaybackStarting;
            subscribedPlaybackPanel.PlaybackStarted += PlaybackPanelPlaybackStarted;
        }
    }

    private void PlaybackPanelPlaybackStarting(object sender, EventArgs e)
    {
        if (IsLoaded && !isClosingOrClosed && ReferenceEquals(sender, subscribedPlaybackPanel))
        {
            PlaybackStarting?.Invoke(this, new RoutedEventArgs());
        }
    }

    private void PlaybackPanelPlaybackStarted(object sender, EventArgs e)
    {
        if (IsLoaded && !isClosingOrClosed && ReferenceEquals(sender, subscribedPlaybackPanel))
        {
            PlaybackStarted?.Invoke(this, new RoutedEventArgs());
        }
    }

    private void PlaybackPanelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() => PlaybackPanelPropertyChanged(sender, e)));
            return;
        }
        if (!IsLoaded || isClosingOrClosed || !ReferenceEquals(sender, subscribedPlaybackPanel))
        {
            return;
        }
        if (e.PropertyName == nameof(PlaybackPanelViewModel.PlayerPanelState)
            && subscribedPlaybackPanel != null)
        {
            ApplyPlaybackPanelHeight(subscribedPlaybackPanel.PlayerPanelState, IsLoaded && !isClosingOrClosed);
            ApplySelectedSurface(subscribedPlaybackPanel.PlayerPanelState);
        }
        if (e.PropertyName == nameof(PlaybackPanelViewModel.UseExternalPanelImage)
            || e.PropertyName == nameof(PlaybackPanelViewModel.StagefilePath))
        {
            ResetPanelImage();
        }
        if (e.PropertyName == nameof(PlaybackPanelViewModel.DisplayedBmsPlayerFile)
            && subscribedPlaybackPanel?.DisplayedBmsPlayerFile != null)
        {
            RefreshArtwork(subscribedPlaybackPanel.DisplayedBmsPlayerFile);
        }
        if (e.PropertyName == nameof(PlaybackPanelViewModel.UsesUbMplay)
            || e.PropertyName == nameof(PlaybackPanelViewModel.UsesLr2Body)
            || e.PropertyName == nameof(PlaybackPanelViewModel.UsesBmiIdxView))
        {
            ApplySelectedSurface(subscribedPlaybackPanel.PlayerPanelState);
        }
    }

    private void ApplyPlaybackPanelHeight(PlayerPanelState panelState, bool animate)
    {
        double targetHeight = panelState.HasFlag(PlayerPanelState.TITLE_SMALL)
            ? CompactPanelHeight
            : ExpandedPanelHeight;
        double currentHeight = ActualHeight;
        BeginAnimation(HeightProperty, null);
        if (!animate)
        {
            Height = targetHeight;
            return;
        }
        Height = targetHeight;
        if (Math.Abs(currentHeight - targetHeight) < 0.5d)
        {
            return;
        }
        var animation = new DoubleAnimation(currentHeight, targetHeight, TimeSpan.FromSeconds(1d))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    public void ConfigureBrowserHost()
    {
        if (webBrowser == null) return;
        object browser = typeof(WebBrowser).GetProperty("AxIWebBrowser2", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(webBrowser, null);
        browser?.GetType().InvokeMember("Silent", BindingFlags.SetProperty, null, browser, new object[] { true });
        browser?.GetType().InvokeMember("RegisterAsDropTarget", BindingFlags.SetProperty, null, browser, new object[] { false });
    }

    public void RefreshArtwork(BMSFile bmsFile)
    {
        if (bmsFile == null)
        {
            ApplyEmptyArtwork(showDefaultImage: DataContext != null);
            return;
        }
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

    private void ApplyEmptyArtwork(bool showDefaultImage)
    {
        gridBMSPlayerImage.Source = showDefaultImage ? PanelImage : null;
        gridBMSPlayerControlsBanner.Background = null;
        gridBMSPlayerControlsBanner.BorderThickness = new Thickness(0);
    }
    private void ShowBmsPlayer() => RestoreVisibilityBinding(windowsFormsHost, Visibility.Visible);
    private void CollapseBmsPlayer() => RestoreVisibilityBinding(windowsFormsHost, Visibility.Collapsed);
    private void CollapseBrowser() => RestoreVisibilityBinding(webBrowser, Visibility.Collapsed);
    private static void RestoreVisibilityBinding(UIElement element, Visibility visibility)
    {
        if (element == null) return;
        MultiBinding binding = BindingOperations.GetMultiBindingExpression(element, UIElement.VisibilityProperty)?.ParentMultiBinding;
        element.Visibility = visibility;
        if (binding != null) BindingOperations.SetBinding(element, UIElement.VisibilityProperty, binding);
    }

    public void RestoreSelectedSurface() => ApplySelectedSurface(PlaybackPanel.PlayerPanelState);
    public void EnsureSelectedSurfaceAvailable()
    {
        ApplySelectedSurface(PlaybackPanel.PlayerPanelState);
    }

    public bool TrySelectBmsPlayerSurface() => PlaybackPanel.TrySelectPanelState(
        PlayerPanelState.BMS_PLAYER,
        IsBmsPlayerSurfaceAvailable(),
        IsMoviePlayerSurfaceAvailable());

    private bool IsBmsPlayerSurfaceAvailable()
    {
        if (windowsFormsHost == null || !windowsFormsHost.IsEnabled) return false;
        if (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && PlaybackPanel.UsesUbMplay) return false;
        if (!Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && PlaybackPanel.UsesUbMplay) return true;
        if (PlaybackPanel.UsesLr2Body) return false;
        return PlaybackPanel.UsesBmiIdxView;
    }

    private bool IsMoviePlayerSurfaceAvailable() => false;

    internal static PlayerPanelState ResolveSurfaceState(
        PlayerPanelState requestedState,
        bool bmsPlayerSurfaceAvailable,
        bool moviePlayerSurfaceAvailable)
    {
        PlayerPanelState resolvedState = requestedState;
        if (resolvedState.HasFlag(PlayerPanelState.MOVIE_PLAYER) && !moviePlayerSurfaceAvailable)
        {
            resolvedState &= ~PlayerPanelState.MOVIE_PLAYER;
            if (bmsPlayerSurfaceAvailable)
            {
                resolvedState |= PlayerPanelState.BMS_PLAYER;
            }
        }
        if (resolvedState.HasFlag(PlayerPanelState.BMS_PLAYER) && !bmsPlayerSurfaceAvailable)
        {
            resolvedState &= ~PlayerPanelState.BMS_PLAYER;
        }
        return resolvedState;
    }

    private void ApplySelectedSurface(PlayerPanelState state)
    {
        PlayerPanelState resolvedState = ResolveSurfaceState(
            state,
            IsBmsPlayerSurfaceAvailable(),
            IsMoviePlayerSurfaceAvailable());
        EffectivePlayerPanelState = resolvedState;
        if (resolvedState.HasFlag(PlayerPanelState.BMS_PLAYER)) ShowBmsPlayer(); else CollapseBmsPlayer();
        CollapseBrowser();
    }
    private void gridBMSPlayerControlsRotatePanelStateButtonClicked(object sender = null, RoutedEventArgs e = null)
    {
        if (isClosingOrClosed) return;
        PlaybackPanel.RotatePanelState(IsBmsPlayerSurfaceAvailable(), IsMoviePlayerSurfaceAvailable());
    }
    private void gridBMSPlayerControlsRotatePanelStateButtonClicked2(object sender, RoutedEventArgs e) => PlaybackPanel.ToggleCompactPanel();
    private void windowsFormsHostIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ApplySelectedSurface(PlaybackPanel.PlayerPanelState);
    }
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
        e.Handled = true;
        HandlePreviousButtonClick(e.ClickCount);
    }

    internal void HandlePreviousButtonClick(int clickCount)
    {
        if (DataContext is not PlaybackPanelViewModel viewModel)
        {
            return;
        }
        PlaybackPreviousButtonAction action = previousButtonGesture.HandleClick(clickCount);
        if (action == PlaybackPreviousButtonAction.Previous)
        {
            StopPreviousButtonTimer();
            viewModel.PreviousCommand.Execute();
        }
        else
        {
            EnsurePreviousButtonTimer();
            gridBMSPlayerControlsPreviousButtonClickTimer.Start();
        }
    }

    private void gridBMSPlayerControlsPreviousButtonSingleClicked(object sender, EventArgs e)
    {
        StopPreviousButtonTimer();
        if (previousButtonGesture.HandleDelayElapsed() == PlaybackPreviousButtonAction.Restart
            && DataContext is PlaybackPanelViewModel viewModel)
        {
            viewModel.RestartCommand.Execute();
        }
    }

    private void EnsurePreviousButtonTimer()
    {
        if (gridBMSPlayerControlsPreviousButtonClickTimer != null)
        {
            return;
        }
        gridBMSPlayerControlsPreviousButtonClickTimer = new DispatcherTimer(
            new TimeSpan(0, 0, 0, 0, 500),
            DispatcherPriority.Background,
            gridBMSPlayerControlsPreviousButtonSingleClicked,
            Dispatcher);
    }

    private void StopPreviousButtonTimer()
    {
        gridBMSPlayerControlsPreviousButtonClickTimer?.Stop();
    }

    private void CancelPreviousButtonGesture()
    {
        if (gridBMSPlayerControlsPreviousButtonClickTimer != null)
        {
            gridBMSPlayerControlsPreviousButtonClickTimer.Stop();
            gridBMSPlayerControlsPreviousButtonClickTimer.Tick -= gridBMSPlayerControlsPreviousButtonSingleClicked;
            gridBMSPlayerControlsPreviousButtonClickTimer = null;
        }
        previousButtonGesture.Deactivate();
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
            if (viewModel.IsStoppedOrPaused)
            {
                TrySelectBmsPlayerSurface();
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
