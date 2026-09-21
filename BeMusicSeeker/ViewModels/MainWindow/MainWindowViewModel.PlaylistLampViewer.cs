using System;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Applies a lamp viewer folder/category intent as one main-view transition.
    /// </summary>
    /// <param name="sender">The playlist workspace owner.</param>
    /// <param name="request">The atomic lamp navigation request.</param>
    private void PlaylistWorkspacePlaylistLampNavigationRequested(
        object sender,
        PlaylistLampNavigationRequestedEventArgs request)
    {
        if (request == null
            || !PlaylistWorkspace.IsCurrentPlaylistLampNavigation(
                request.Revision,
                request.FilterRevision,
                request.Selection,
                request.FilterSnapshot))
        {
            return;
        }

        // Keep the visible filter control synchronized without allowing its individual
        // property events to publish an intermediate, wrong-folder refresh.
        ChartFilters.ApplySnapshotSilently(request.FilterSnapshot);
        RefreshChartRowsView(
            MainViewUpdateMode.PlaylistFilterSelected,
            request.Selection);
    }
}
