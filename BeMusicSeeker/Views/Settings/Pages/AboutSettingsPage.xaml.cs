using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Settings.Pages;

/// <summary>
/// Hosts application identity, version, credits, links, and release-notes access.
/// </summary>
public partial class AboutSettingsPage : UserControl
{
    private readonly string assemblyVersion;

    private readonly string displayedVersion;

    private bool resourceNotificationsAttached;

    /// <summary>
    /// Initializes the about settings page and resolves displayed versions from the entry assembly.
    /// </summary>
    public AboutSettingsPage()
    {
        InitializeComponent();

        var entryAssembly = Assembly.GetEntryAssembly();
        assemblyVersion = entryAssembly?.GetName().Version?.ToString() ?? string.Empty;
        string informationalVersion = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        displayedVersion = string.IsNullOrWhiteSpace(informationalVersion)
            ? assemblyVersion
            : informationalVersion;
        Loaded += AboutSettingsPageLoaded;
        Unloaded += AboutSettingsPageUnloaded;
        RefreshLocalizedVersionPresentation();
    }

    private void AboutSettingsPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!resourceNotificationsAttached)
        {
            ResourceService.Current.PropertyChanged += ResourceServicePropertyChanged;
            resourceNotificationsAttached = true;
        }

        RefreshLocalizedVersionPresentation();
    }

    private void AboutSettingsPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (resourceNotificationsAttached)
        {
            ResourceService.Current.PropertyChanged -= ResourceServicePropertyChanged;
            resourceNotificationsAttached = false;
        }
    }

    private void ResourceServicePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(ResourceService.Resources), StringComparison.Ordinal))
        {
            RefreshLocalizedVersionPresentation();
        }
    }

    private void RefreshLocalizedVersionPresentation()
    {
        textBlockVerNum.Text = string.Format(BeMusicSeeker.Properties.Resources.About_version_format, displayedVersion);
        textBlockBuildNum.Text = string.Format(BeMusicSeeker.Properties.Resources.About_build_format, assemblyVersion);
    }

    private async void releaseNotesButtonClicked(object sender, RoutedEventArgs e)
    {
        SettingsWindow settingsWindow = Window.GetWindow(this) as SettingsWindow
            ?? throw new InvalidOperationException("About settings page is not hosted by SettingsWindow.");
        await settingsWindow.HandleShowReleaseNotesAsync();
    }

    private void externalLinkButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url })
        {
            throw new InvalidOperationException("About link URL is unavailable.");
        }

        OpenExternalUrl(url);
    }

    private void OpenExternalUrl(string url)
    {
        SettingsDialogViewModel viewModel = DataContext as SettingsDialogViewModel
            ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
        viewModel.ExternalShellGateway.Open(ExternalShellRequest.OpenUrl(url));
    }

    private void hyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenExternalUrl(e.Uri.ToString());
        e.Handled = true;
    }
}
