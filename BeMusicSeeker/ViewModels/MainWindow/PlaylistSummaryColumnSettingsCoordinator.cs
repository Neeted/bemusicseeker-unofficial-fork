using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistSummaryColumnSettingsCoordinator
{
    private readonly PlaylistWorkspaceViewModel playlistWorkspace;

    internal PlaylistSummaryColumnSettingsCoordinator(PlaylistWorkspaceViewModel playlistWorkspace)
    {
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
    }

    internal void ResetToDefault()
    {
        Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        Settings.Default.PlaylistSummaryColumnsSettings.EnsureCompatibility();
        playlistWorkspace.PlaylistSummaryColumnsSettings = Settings.Default.PlaylistSummaryColumnsSettings;
    }
}
