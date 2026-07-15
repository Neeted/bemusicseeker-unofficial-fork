using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using BeMusicSeeker.Models;
using Livet;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private void PlaylistTreeStorePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, playlistTreeStore))
        {
            return;
        }
        if (string.Equals(e?.PropertyName, nameof(BMSPlaylist.BMSTables), StringComparison.Ordinal))
        {
            AttachObservedPlaylistTreeTables(playlistTreeStore?.BMSTables);
            BMSPlaylist sourceStore = playlistTreeStore;
            DispatcherCollection<BMSTable> sourceTables = observedPlaylistTreeTables;
            long generation = Volatile.Read(ref playlistTreeNotificationGeneration);
            if (TryHoldPlaylistTreePresentation("playlist_tables_changed"))
            {
                return;
            }
            DispatchPlaylistStoreNotification(() =>
            {
                if (IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation))
                {
                    HandlePlaylistTablesChanged("playlist_tables_changed");
                }
            });
            return;
        }

        if (string.Equals(e?.PropertyName, nameof(BMSPlaylist.PlaylistEntriesHydrationRequestedVersion), StringComparison.Ordinal))
        {
            int version = playlistTreeStore?.PlaylistEntriesHydrationRequestedVersion ?? 0;
            PlaylistEntriesHydrationRequested?.Invoke(
                this,
                new PlaylistEntriesHydrationVersionChangedEventArgs(version));
            return;
        }

        if (string.Equals(e?.PropertyName, nameof(BMSPlaylist.PlaylistEntriesHydrationCompletedVersion), StringComparison.Ordinal))
        {
            int version = playlistTreeStore?.PlaylistEntriesHydrationCompletedVersion ?? 0;
            HandlePlaylistEntriesHydrationCompleted(version);
        }
    }

    private void PlaylistTreeTablesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, observedPlaylistTreeTables))
        {
            return;
        }
        BMSPlaylist sourceStore = playlistTreeStore;
        DispatcherCollection<BMSTable> sourceTables = observedPlaylistTreeTables;
        long generation = Volatile.Read(ref playlistTreeNotificationGeneration);
        if (TryHoldPlaylistTreePresentation("playlist_tables_collection_changed"))
        {
            return;
        }
        DispatchPlaylistStoreNotification(() =>
        {
            if (IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation))
            {
                HandlePlaylistTablesChanged("playlist_tables_collection_changed");
            }
        });
    }

    private bool IsCurrentPlaylistTreeNotification(
        BMSPlaylist sourceStore,
        DispatcherCollection<BMSTable> sourceTables,
        long generation)
    {
        return ReferenceEquals(playlistTreeStore, sourceStore)
            && ReferenceEquals(observedPlaylistTreeTables, sourceTables)
            && Volatile.Read(ref playlistTreeNotificationGeneration) == generation;
    }

    private void DispatchPlaylistStoreNotification(Action action)
    {
        if (action == null)
        {
            return;
        }
        dispatchPresentation(action);
    }

    private bool TryHoldPlaylistTreePresentation(string reason)
    {
        if (playlistTreeRefreshSuppressedProvider())
        {
            RequestPlaylistSummaryDataRefresh(reason, invalidateTableCountCache: true);
            return true;
        }
        if (playlistTreeRefreshDeferredProvider(reason))
        {
            RequestPlaylistSummaryDataRefresh(reason, invalidateTableCountCache: true);
            return true;
        }
        return false;
    }

    private void HandlePlaylistTablesChanged(string reason)
    {
        if (TryHoldPlaylistTreePresentation(reason))
        {
            return;
        }

        RefreshPlaylistTreePresentation();
        PlaylistTablesPresentationChanged?.Invoke(this, EventArgs.Empty);
        RequestPlaylistSummaryDataRefresh(reason, invalidateTableCountCache: true);
    }

    private void HandlePlaylistEntriesHydrationCompleted(int version)
    {
        PlaylistEntriesHydrationCompleted?.Invoke(
            this,
            new PlaylistEntriesHydrationVersionChangedEventArgs(version));
        if (playlistTreeRefreshDeferredProvider("playlist_entries_hydration_completed"))
        {
            RequestPlaylistSummaryDataRefresh(
                "playlist_entries_hydration_completed",
                invalidateTableCountCache: true);
            return;
        }
        RequestPlaylistSummaryDataRefresh(
            "playlist_entries_hydration_completed",
            invalidateTableCountCache: true);
        RequestPlaylistDetailReloadRefresh();
    }
}

internal sealed class PlaylistEntriesHydrationVersionChangedEventArgs : EventArgs
{
    internal PlaylistEntriesHydrationVersionChangedEventArgs(int version)
    {
        Version = version;
    }

    internal int Version { get; }
}
