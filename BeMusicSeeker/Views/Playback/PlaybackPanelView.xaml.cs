using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Ribbit.Logging;
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
    private PropertyChangedEventHandler subscribedPropertyChangedHandler;
    private EventHandler subscribedPlaybackStartingHandler;
    private EventHandler subscribedPlaybackStartedHandler;
    private Storyboard activePanelStateTransition;
    private bool? appliedCompactPanelState;
    private long playbackPanelSubscriptionVersion;
    private BitmapSource externalPanelImage;
    private bool isClosingOrClosed;

    public PlaybackPanelView()
    {
        InitializeComponent();
        DataContextChanged += PlaybackPanelDataContextChanged;
        Loaded += PlaybackPanelLoaded;
        Unloaded += PlaybackPanelUnloaded;
    }

    private void PlayerVolumeDragCompleted(object sender, DragCompletedEventArgs e) => CompletePlayerVolumeInput(sender);
    private void PlayerVolumeMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CompletePlayerVolumeInput(sender);
    private void PlayerVolumeLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CompletePlayerVolumeInput(sender);
    private void PlayerVolumeUnloaded(object sender, RoutedEventArgs e) => CompletePlayerVolumeInput(sender);

    private void PlayerVolumeKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        { CompletePlayerVolumeInput(sender); }
    }

    private static void CompletePlayerVolumeInput(object sender)
    {
        if (sender is Slider { DataContext: PlaybackPanelViewModel panel } slider)
        {
            slider.GetBindingExpression(Slider.ValueProperty)?.UpdateSource();
            panel.CompleteVolumeAdjustment();
            slider.GetBindingExpression(Slider.ValueProperty)?.UpdateTarget();
        }
    }

    public event RoutedEventHandler PlaybackStarting;
    public event RoutedEventHandler PlaybackStarted;
    public Visibility OverlayVisibility { get => (Visibility)GetValue(OverlayVisibilityProperty); set => SetValue(OverlayVisibilityProperty, value); }
    public PlayerPanelState EffectivePlayerPanelState { get => (PlayerPanelState)GetValue(EffectivePlayerPanelStateProperty); private set => SetValue(EffectivePlayerPanelStateProperty, value); }
    public ICommand SettingsCommand { get => (ICommand)GetValue(SettingsCommandProperty); set => SetValue(SettingsCommandProperty, value); }
    /// <summary>WPFへの接続時に生成される外部プレーヤーの親HWNDを返します。未接続時はゼロです。</summary>
    public IntPtr PlayerHostHandle => externalPlayerHost.Handle;

    /// <summary>外部プレーヤーの終了後、最終ウィンドウ終了時にUIスレッド上でホストを解放します。</summary>
    internal void DisposePlayerHost()
    {
        Dispatcher.VerifyAccess();
        externalPlayerHost.Dispose();
    }
    private PlaybackPanelViewModel PlaybackPanel => DataContext as PlaybackPanelViewModel ?? throw new InvalidOperationException("Playback panel DataContext is unavailable.");

    private void ApplyPanelImage()
    {
        // DataContextの契約違反は、任意画像の不足へ変換しません。
        PlaybackPanelViewModel playbackPanel = PlaybackPanel;
        if (playbackPanel.UseExternalPanelImage)
        {
            externalPanelImage ??= ReadOptionalArtwork(playbackPanel.StagefilePath, "external_panel");
            if (externalPanelImage != null)
            {
                gridBMSPlayerImage.Source = externalPanelImage;
                return;
            }
        }
        gridBMSPlayerImage.SetResourceReference(Image.SourceProperty, "Playback.DefaultImage");
    }

    private void ResetPanelImage() => externalPanelImage = null;

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
        ApplyPlaybackPanelVisualState(
            subscribedPlaybackPanel?.PlayerPanelState.HasFlag(PlayerPanelState.TITLE_SMALL) ?? false,
            useTransitions: false);
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
        SubscribePlaybackPanel(playbackPanel);
        ApplyPlaybackPanelState(
            playbackPanel,
            playbackPanel?.PlayerPanelState ?? PlayerPanelState.TITLE_LARGE,
            useTransitions: false);
        ResetPanelImage();
        ApplyEmptyArtwork(showDefaultImage: dataContext != null);
        if (playbackPanel?.DisplayedChart != null)
        {
            RefreshArtwork(playbackPanel.DisplayedChart);
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
            subscribedPlaybackPanel.PropertyChanged -= subscribedPropertyChangedHandler;
            subscribedPlaybackPanel.PlaybackStarting -= subscribedPlaybackStartingHandler;
            subscribedPlaybackPanel.PlaybackStarted -= subscribedPlaybackStartedHandler;
        }
        playbackPanelSubscriptionVersion++;
        subscribedPlaybackPanel = playbackPanel;
        subscribedPropertyChangedHandler = null;
        subscribedPlaybackStartingHandler = null;
        subscribedPlaybackStartedHandler = null;
        appliedCompactPanelState = null;
        if (subscribedPlaybackPanel != null)
        {
            long subscriptionVersion = playbackPanelSubscriptionVersion;
            subscribedPropertyChangedHandler = (sender, e) => PlaybackPanelPropertyChanged(
                playbackPanel,
                subscriptionVersion,
                sender,
                e);
            subscribedPlaybackStartingHandler = (sender, e) => PlaybackPanelPlaybackStarting(
                playbackPanel,
                subscriptionVersion,
                sender,
                e);
            subscribedPlaybackStartedHandler = (sender, e) => PlaybackPanelPlaybackStarted(
                playbackPanel,
                subscriptionVersion,
                sender,
                e);
            subscribedPlaybackPanel.PropertyChanged += subscribedPropertyChangedHandler;
            subscribedPlaybackPanel.PlaybackStarting += subscribedPlaybackStartingHandler;
            subscribedPlaybackPanel.PlaybackStarted += subscribedPlaybackStartedHandler;
        }
    }

    private void PlaybackPanelPlaybackStarting(
        PlaybackPanelViewModel expectedPanel,
        long subscriptionVersion,
        object sender,
        EventArgs e)
    {
        if (IsCurrentPlaybackPanelSubscription(expectedPanel, subscriptionVersion, sender))
        {
            PlaybackStarting?.Invoke(this, new RoutedEventArgs());
        }
    }

    private void PlaybackPanelPlaybackStarted(
        PlaybackPanelViewModel expectedPanel,
        long subscriptionVersion,
        object sender,
        EventArgs e)
    {
        if (IsCurrentPlaybackPanelSubscription(expectedPanel, subscriptionVersion, sender))
        {
            PlaybackStarted?.Invoke(this, new RoutedEventArgs());
        }
    }

    private void PlaybackPanelPropertyChanged(
        PlaybackPanelViewModel expectedPanel,
        long subscriptionVersion,
        object sender,
        PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() => PlaybackPanelPropertyChanged(
                    expectedPanel,
                    subscriptionVersion,
                    sender,
                    e)));
            return;
        }
        if (!IsCurrentPlaybackPanelSubscription(expectedPanel, subscriptionVersion, sender))
        {
            return;
        }
        if (e.PropertyName == nameof(PlaybackPanelViewModel.PlayerPanelState))
        {
            ApplyPlaybackPanelState(expectedPanel, expectedPanel.PlayerPanelState, useTransitions: true);
        }
        if (e.PropertyName == nameof(PlaybackPanelViewModel.UseExternalPanelImage)
            || e.PropertyName == nameof(PlaybackPanelViewModel.StagefilePath))
        {
            ResetPanelImage();
            RefreshArtwork(expectedPanel.DisplayedChart);
        }
        if (e.PropertyName == nameof(PlaybackPanelViewModel.DisplayedChart))
        {
            RefreshArtwork(expectedPanel.DisplayedChart);
        }
        if (e.PropertyName == nameof(PlaybackPanelViewModel.UsesUbMplay)
            || e.PropertyName == nameof(PlaybackPanelViewModel.UsesLr2Body)
            || e.PropertyName == nameof(PlaybackPanelViewModel.UsesBmiIdxView))
        {
            ApplySelectedSurface(expectedPanel, expectedPanel.PlayerPanelState);
        }
    }

    private bool IsCurrentPlaybackPanelSubscription(
        PlaybackPanelViewModel expectedPanel,
        long subscriptionVersion,
        object sender) =>
        IsLoaded
        && !isClosingOrClosed
        && subscriptionVersion == playbackPanelSubscriptionVersion
        && ReferenceEquals(expectedPanel, subscribedPlaybackPanel)
        && ReferenceEquals(sender, expectedPanel);

    private void ApplyPlaybackPanelState(
        PlaybackPanelViewModel playbackPanel,
        PlayerPanelState requestedState,
        bool useTransitions)
    {
        ApplySelectedSurface(playbackPanel, requestedState);
        ApplyPlaybackPanelVisualState(
            EffectivePlayerPanelState.HasFlag(PlayerPanelState.TITLE_SMALL),
            useTransitions);
    }

    private void ApplyPlaybackPanelVisualState(bool compact, bool useTransitions)
    {
        if (useTransitions && appliedCompactPanelState == compact)
        {
            return;
        }

        BlurEffect blurEffect = GetPlayerImageBlurEffect();
        double currentHeight = Height;
        double currentBlurRadius = blurEffect.Radius;
        Thickness currentTitleMargin = gridBMSPlayerTitlePanel.Margin;
        double currentBannerOpacity = gridBMSPlayerControlsBanner.Opacity;

        StopPlaybackPanelTransition(blurEffect);

        double targetHeight = compact ? CompactPanelHeight : ExpandedPanelHeight;
        double targetBlurRadius = compact ? 20d : 0d;
        Thickness targetTitleMargin = compact ? new Thickness(0d, 0d, 0d, -40d) : new Thickness(0d);
        double targetBannerOpacity = compact ? 1d : 0d;
        Height = targetHeight;
        blurEffect.Radius = targetBlurRadius;
        gridBMSPlayerTitlePanel.Margin = targetTitleMargin;
        gridBMSPlayerControlsBanner.Opacity = targetBannerOpacity;
        gridBMSPlayerControlsTitle2.Opacity = 1d;
        appliedCompactPanelState = compact;

        if (!useTransitions)
        {
            return;
        }

        var transition = new Storyboard { FillBehavior = FillBehavior.Stop };
        AddPlaybackPanelAnimation(
            transition,
            new DoubleAnimation(currentHeight, targetHeight, TimeSpan.FromSeconds(1d))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            },
            this,
            HeightProperty);
        var blurAnimation = new DoubleAnimation(currentBlurRadius, targetBlurRadius, TimeSpan.FromSeconds(1d))
        {
            FillBehavior = FillBehavior.Stop
        };
        AddPlaybackPanelAnimation(
            transition,
            new ThicknessAnimation(currentTitleMargin, targetTitleMargin, TimeSpan.FromSeconds(0.7d))
            {
                FillBehavior = FillBehavior.Stop
            },
            gridBMSPlayerTitlePanel,
            MarginProperty);
        Timeline bannerAnimation;
        if (compact)
        {
            var compactBannerAnimation = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(1.3d),
                FillBehavior = FillBehavior.Stop
            };
            compactBannerAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(currentBannerOpacity, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            compactBannerAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(currentBannerOpacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8d))));
            compactBannerAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(
                targetBannerOpacity,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.3d)),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            bannerAnimation = compactBannerAnimation;
        }
        else
        {
            bannerAnimation = new DoubleAnimation(currentBannerOpacity, targetBannerOpacity, TimeSpan.FromSeconds(0.2d))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
        }
        AddPlaybackPanelAnimation(
            transition,
            bannerAnimation,
            gridBMSPlayerControlsBanner,
            OpacityProperty);
        if (compact)
        {
            var compactTitleAnimation = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(1d),
                FillBehavior = FillBehavior.Stop
            };
            compactTitleAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0d, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            compactTitleAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0d, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8d))));
            compactTitleAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(1d, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1d))));
            AddPlaybackPanelAnimation(
                transition,
                compactTitleAnimation,
                gridBMSPlayerControlsTitle2,
                OpacityProperty);
        }

        activePanelStateTransition = transition;
        transition.Completed += (_, _) => CompletePlaybackPanelTransition(transition);
        transition.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: true);
        blurEffect.BeginAnimation(BlurEffect.RadiusProperty, blurAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AddPlaybackPanelAnimation(
        Storyboard storyboard,
        Timeline animation,
        DependencyObject target,
        DependencyProperty targetProperty)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(targetProperty));
        storyboard.Children.Add(animation);
    }

    private void CompletePlaybackPanelTransition(Storyboard transition)
    {
        if (!ReferenceEquals(activePanelStateTransition, transition))
        {
            return;
        }
        StopPlaybackPanelTransition(GetPlayerImageBlurEffect());
    }

    private void StopPlaybackPanelTransition(BlurEffect blurEffect)
    {
        activePanelStateTransition?.Remove(this);
        activePanelStateTransition = null;
        BeginAnimation(HeightProperty, null);
        blurEffect.BeginAnimation(BlurEffect.RadiusProperty, null);
        gridBMSPlayerTitlePanel.BeginAnimation(MarginProperty, null);
        gridBMSPlayerControlsBanner.BeginAnimation(OpacityProperty, null);
        gridBMSPlayerControlsTitle2.BeginAnimation(OpacityProperty, null);
    }

    private BlurEffect GetPlayerImageBlurEffect() =>
        gridBMSPlayerImage.Effect as BlurEffect
        ?? throw new InvalidOperationException("Playback panel image must use a BlurEffect.");

    /// <summary>現在の共通譜面だけから素材を表示し、欠落・破損時も前曲の画像を残しません。</summary>
    /// <remarks>任意の読取り・復号・診断の失敗は再生開始へ伝えず、同曲stageまたは空表示へ進みます。</remarks>
#nullable enable annotations
    internal void RefreshArtwork(ChartFile? bmsFile)
    {
        if (DataContext != null)
        {
            _ = PlaybackPanel;
        }
        ClearArtwork();
        if (DataContext == null)
        {
            return;
        }
        if (bmsFile == null)
        {
            ApplyPanelImage();
            return;
        }

        BitmapSource stage = ReadChartArtwork(bmsFile, bmsFile.Stagefile, "stagefile");
        BitmapSource banner = ReadChartArtwork(bmsFile, bmsFile.Banner, "banner");
        if (stage != null) { gridBMSPlayerImage.Source = stage; }
        else { ApplyPanelImage(); }
        BitmapSource bannerImage = banner ?? stage;
        if (bannerImage != null)
        {
            try
            {
                gridBMSPlayerControlsBanner.Background = new ImageBrush(bannerImage)
                {
                    Stretch = banner == null ? Stretch.UniformToFill : Stretch.Fill
                };
            }
            catch (Exception ex)
            {
                // 同曲stageを使うbrush生成も任意です。二次失敗では背景なしを維持します。
                ReportArtworkFailure(ex, "banner_fallback", bmsFile.Path);
            }
        }
        gridBMSPlayerControlsBanner.BorderThickness = gridBMSPlayerControlsBanner.Background == null ? new Thickness(0) : new Thickness(1, 0, 1, 0);
    }
#nullable restore annotations

    private static BitmapSource ReadChartArtwork(ChartFile chart, string relativePath, string material)
    {
        if (string.IsNullOrWhiteSpace(chart.Path) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }
        string path = relativePath;
        try
        {
            path = Path.Combine(DirectoryExt.GetDirectoryNameSimple(chart.Path), relativePath);
            return ReadOptionalArtwork(path, material);
        }
        catch (Exception ex)
        {
            ReportArtworkFailure(ex, material, path);
            return null;
        }
    }

    private static BitmapSource ReadOptionalArtwork(string path, string material)
    {
        try
        {
            using var stream = new MemoryStream(LongPathFileSystem.ReadAllBytes(path));
            return new WriteableBitmap(BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
        }
        catch (Exception ex)
        {
            // 素材は再生の必須入力ではありません。元pathと復号原因を診断し、現在曲のfallbackへ進みます。
            ReportArtworkFailure(ex, material, path);
            return null;
        }
    }

    private static void ReportArtworkFailure(Exception exception, string material, string path)
    {
        try
        {
            NLogWrapper.GetLogger(typeof(PlaybackPanelView)).Warn(exception, "playback_artwork failed material=" + material + " path=" + path);
        }
        catch (Exception)
        {
            // 任意診断の二次故障は素材不足や選曲・開始の結果を変えません。
        }
    }

    private void ClearArtwork()
    {
        gridBMSPlayerImage.Source = null;
        gridBMSPlayerControlsBanner.Background = null;
        gridBMSPlayerControlsBanner.BorderThickness = new Thickness(0);
    }

    private void ApplyEmptyArtwork(bool showDefaultImage)
    {
        ClearArtwork();
        if (showDefaultImage) { ApplyPanelImage(); }
    }
    private void ShowBmsPlayer() => RestoreVisibilityBinding(externalPlayerHost, Visibility.Visible);
    private void CollapseBmsPlayer() => RestoreVisibilityBinding(externalPlayerHost, Visibility.Collapsed);
    private static void RestoreVisibilityBinding(UIElement element, Visibility visibility)
    {
        if (element == null)
        {
            return;
        }

        MultiBinding binding = BindingOperations.GetMultiBindingExpression(element, UIElement.VisibilityProperty)?.ParentMultiBinding;
        element.Visibility = visibility;
        if (binding != null)
        {
            BindingOperations.SetBinding(element, UIElement.VisibilityProperty, binding);
        }
    }

    public void RestoreSelectedSurface() => ApplySelectedSurface(PlaybackPanel, PlaybackPanel.PlayerPanelState);
    public void EnsureSelectedSurfaceAvailable()
    {
        ApplySelectedSurface(PlaybackPanel, PlaybackPanel.PlayerPanelState);
    }

    /// <summary>入力時の接続期間を捕捉し、受付後も同じ接続が生存する場合だけ面を選択します。</summary>
    internal Action CaptureBmsPlayerSurfaceRequest()
    {
        PlaybackPanelViewModel expectedPanel = PlaybackPanel;
        long subscriptionVersion = playbackPanelSubscriptionVersion;
        return () =>
        {
            if (IsCurrentPlaybackPanelSubscription(expectedPanel, subscriptionVersion, expectedPanel))
            {
                TrySelectBmsPlayerSurface();
            }
        };
    }

    public bool TrySelectBmsPlayerSurface() => PlaybackPanel.TrySelectPanelState(
        PlayerPanelState.BMS_PLAYER,
        IsBmsPlayerSurfaceAvailable(PlaybackPanel));

    private bool IsBmsPlayerSurfaceAvailable(PlaybackPanelViewModel playbackPanel)
    {
        if (playbackPanel == null || externalPlayerHost == null || !externalPlayerHost.IsEnabled)
        {
            return false;
        }

        if (Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && playbackPanel.UsesUbMplay)
        {
            return false;
        }

        if (!Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012) && playbackPanel.UsesUbMplay)
        {
            return true;
        }

        if (playbackPanel.UsesLr2Body)
        {
            return false;
        }

        return playbackPanel.UsesBmiIdxView;
    }

    /// <summary>
    /// Resolves a requested panel state to a surface that can be materialized by this view.
    /// </summary>
    /// <param name="requestedState">State requested by the playback panel owner.</param>
    /// <param name="bmsPlayerSurfaceAvailable">Whether the BMS player surface is usable.</param>
    /// <returns>The requested state with unavailable BMS surface selection removed.</returns>
    internal static PlayerPanelState ResolveSurfaceState(
        PlayerPanelState requestedState,
        bool bmsPlayerSurfaceAvailable)
    {
        PlayerPanelState resolvedState = requestedState;
        if (resolvedState.HasFlag(PlayerPanelState.BMS_PLAYER) && !bmsPlayerSurfaceAvailable)
        {
            resolvedState &= ~PlayerPanelState.BMS_PLAYER;
        }
        return resolvedState;
    }

    private void ApplySelectedSurface(PlaybackPanelViewModel playbackPanel, PlayerPanelState state)
    {
        PlayerPanelState resolvedState = ResolveSurfaceState(
            state,
            IsBmsPlayerSurfaceAvailable(playbackPanel));
        EffectivePlayerPanelState = resolvedState;
        if (resolvedState.HasFlag(PlayerPanelState.BMS_PLAYER))
        {
            ShowBmsPlayer();
        }
        else
        {
            CollapseBmsPlayer();
        }
    }
    private void gridBMSPlayerControlsRotatePanelStateButtonClicked(object sender = null, RoutedEventArgs e = null)
    {
        if (isClosingOrClosed)
        {
            return;
        }

        PlaybackPanel.RotatePanelState(IsBmsPlayerSurfaceAvailable(PlaybackPanel));
    }
    private void gridBMSPlayerControlsRotatePanelStateButtonClicked2(object sender, RoutedEventArgs e) => PlaybackPanel.ToggleCompactPanel();
    private void externalPlayerHostIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is PlaybackPanelViewModel playbackPanel)
        {
            ApplySelectedSurface(playbackPanel, playbackPanel.PlayerPanelState);
        }
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
        if (DataContext is PlaybackPanelViewModel)
        {
            e.Handled = true;
            RequestPlaybackFromPlayButton().ObserveFault("PlaybackPanelView.PlayButton");
        }
    }

    /// <summary>再生ボタンの要求を現在のownerへ渡し、面選択は受付後に現在のView接続へ適用します。</summary>
    /// <returns>転送した再生要求の実終端。面要求のUI配送は別に進みます。</returns>
    internal Task RequestPlaybackFromPlayButton() => DataContext is PlaybackPanelViewModel viewModel
        ? viewModel.HandlePlayButton(CaptureBmsPlayerSurfaceRequest())
        : Task.CompletedTask;

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
        SeekFromPoint(e.GetPosition(sliderPlayer));
    }

    /// <summary>シークSlider相対の入力座標を実Trackとつまみの配置で再生時刻へ換算し、双方向Bindingを保って反映します。</summary>
    /// <param name="position">シークSliderを基準にしたポインター座標。</param>
    /// <remarks>範囲外はSliderの上下限へ丸め、再生中のドラッグでは既存の一時停止操作へ渡します。</remarks>
    internal void SeekFromPoint(Point position)
    {
        if (sliderPlayer.Template.FindName("PART_Track", sliderPlayer) is not Track track)
        {
            return;
        }
        // ValueFromPointは現在値と配置済みのつまみ中心を併用するため、連続入力でも同じ値の配置へ揃えます。
        track.UpdateLayout();
        Point trackPosition = sliderPlayer.TranslatePoint(position, track);
        double value = track.ValueFromPoint(trackPosition);
        sliderPlayer.SetCurrentValue(Slider.ValueProperty, Math.Clamp(value, sliderPlayer.Minimum, sliderPlayer.Maximum));
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
