using System;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    private void PlaylistWorkspacePlaylistReferenceSortInvalidationRequested(object sender, EventArgs e)
    {
        InvalidateNormalLibraryReferenceTableSortKeys();
    }

}
