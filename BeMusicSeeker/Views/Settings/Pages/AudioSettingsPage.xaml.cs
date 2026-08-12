using System;
using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts audio output and device test settings.
/// </summary>
public partial class AudioSettingsPage : UserControl
{
    /// <summary>
    /// Identifies the playback panel dependency used by the volume controls.
    /// </summary>
    public static readonly DependencyProperty PlaybackPanelProperty = DependencyProperty.Register(
        nameof(PlaybackPanel),
        typeof(PlaybackPanelViewModel),
        typeof(AudioSettingsPage),
        new PropertyMetadata(null));

    /// <summary>
    /// Initializes the audio settings page.
    /// </summary>
    public AudioSettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the playback panel whose volume is edited by this page.
    /// </summary>
    public PlaybackPanelViewModel PlaybackPanel
    {
        get => (PlaybackPanelViewModel)GetValue(PlaybackPanelProperty);
        set => SetValue(PlaybackPanelProperty, value);
    }

    private async void buttonPlayerTestClick(object sender, RoutedEventArgs e)
    {
        SettingsDialogViewModel viewModel = DataContext as SettingsDialogViewModel
            ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
        await viewModel.RunAudioDeviceTestAsync();
    }
}
