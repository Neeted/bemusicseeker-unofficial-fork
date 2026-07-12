using System;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistSummaryColumnSettingsCoordinator
{
    private readonly PlaylistWorkspaceViewModel playlistWorkspace;

    private readonly IMainChartColumnSettingsStore columnSettingsStore;

    internal PlaylistSummaryColumnSettingsCoordinator(
        PlaylistWorkspaceViewModel playlistWorkspace,
        IMainChartColumnSettingsStore columnSettingsStore = null)
    {
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        this.columnSettingsStore = columnSettingsStore
            ?? new SettingsMainChartColumnSettingsStore();
    }

    internal void ResetToDefault()
    {
        playlistWorkspace.PlaylistSummaryColumnsSettings = columnSettingsStore.ResetPlaylistSummary();
    }
}
