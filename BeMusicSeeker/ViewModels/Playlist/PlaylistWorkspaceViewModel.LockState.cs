using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>
    /// Returns whether playlist-table initialization or an active playlist-table write blocks editing.
    /// </summary>
    internal bool IsWriteLockHeldBMSTables
    {
        get
        {
            BMSPlaylist playlistStore = getPlaylistStore();
            return playlistStore?.IsWriteLockHeldBMSTables ?? true;
        }
    }

    /// <summary>
    /// Returns whether the minimum playlist-table initialization lock blocks playlist imports.
    /// </summary>
    internal bool IsWriteLockHeldBMSTablesInitializeMin
    {
        get
        {
            BMSPlaylist playlistStore = getPlaylistStore();
            return playlistStore?.IsWriteLockHeldBMSTablesInitializeMin ?? true;
        }
    }

    /// <summary>
    /// Returns whether any playlist table or playlist body is being written.
    /// </summary>
    internal bool IsWriteLockHeldAnyBMSTable
    {
        get
        {
            BMSPlaylist playlistStore = getPlaylistStore();
            return playlistStore?.IsWriteLockHeldAnyBMSTable ?? true;
        }
    }

    /// <summary>
    /// Returns whether playlist synchronization is currently updating the playlist store.
    /// </summary>
    internal bool IsPlaylistUpdating
    {
        get
        {
            BMSPlaylist playlistStore = getPlaylistStore();
            return playlistStore?.IsPlaylistUpdating ?? false;
        }
    }
}
