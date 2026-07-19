using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Livet;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private void PlaylistTreeStorePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        BMSPlaylist sourceStore;
        DispatcherCollection<BMSTable> sourceTables;
        long generation;
        int hydrationVersion;
        bool tablesChanged = false;
        bool hydrationRequested = false;
        bool hydrationCompleted = false;
        lock (playlistTreeStoreSyncRoot)
        {
            if (!ReferenceEquals(sender, playlistTreeStore))
            {
                return;
            }
            sourceStore = playlistTreeStore;
            if (string.Equals(e?.PropertyName, nameof(BMSPlaylist.BMSTables), StringComparison.Ordinal))
            {
                AttachObservedPlaylistTreeTables(playlistTreeStore?.BMSTables);
                sourceTables = observedPlaylistTreeTables;
                generation = Volatile.Read(ref playlistTreeNotificationGeneration);
                tablesChanged = true;
                hydrationVersion = 0;
            }
            else if (string.Equals(e?.PropertyName, nameof(BMSPlaylist.PlaylistEntriesHydrationRequestedVersion), StringComparison.Ordinal))
            {
                hydrationVersion = playlistTreeStore?.PlaylistEntriesHydrationRequestedVersion ?? 0;
                sourceTables = observedPlaylistTreeTables;
                generation = Volatile.Read(ref playlistTreeNotificationGeneration);
                hydrationRequested = true;
            }
            else if (string.Equals(e?.PropertyName, nameof(BMSPlaylist.PlaylistEntriesHydrationCompletedVersion), StringComparison.Ordinal))
            {
                hydrationVersion = playlistTreeStore?.PlaylistEntriesHydrationCompletedVersion ?? 0;
                sourceTables = observedPlaylistTreeTables;
                generation = Volatile.Read(ref playlistTreeNotificationGeneration);
                hydrationCompleted = true;
            }
            else
            {
                return;
            }
        }

        if (tablesChanged)
        {
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
        if (hydrationRequested)
        {
            lock (playlistTreeStoreSyncRoot)
            {
                if (!IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation))
                {
                    return;
                }
                PlaylistEntriesHydrationRequested?.Invoke(
                    this,
                    new PlaylistEntriesHydrationVersionChangedEventArgs(hydrationVersion));
            }
            return;
        }
        if (hydrationCompleted)
        {
            lock (playlistTreeStoreSyncRoot)
            {
                if (!IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation))
                {
                    return;
                }
                HandlePlaylistEntriesHydrationCompleted(hydrationVersion);
            }
        }
    }

    private void PlaylistTreeTablesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        BMSPlaylist sourceStore;
        DispatcherCollection<BMSTable> sourceTables;
        long generation;
        lock (playlistTreeStoreSyncRoot)
        {
            if (!ReferenceEquals(sender, observedPlaylistTreeTables))
            {
                return;
            }
            sourceStore = playlistTreeStore;
            sourceTables = observedPlaylistTreeTables;
            generation = Volatile.Read(ref playlistTreeNotificationGeneration);
        }
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

    private void PlaylistTreeStoreHydrationReceiptPublished(
        object sender,
        PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceiptEventArgs eventArgs)
    {
        if (!ReferenceEquals(sender, playlistTreeStore) || eventArgs?.Receipt == null)
        {
            return;
        }

        BMSPlaylist sourceStore;
        long generation;
        lock (playlistTreeStoreSyncRoot)
        {
            if (!ReferenceEquals(sender, playlistTreeStore))
            {
                return;
            }
            sourceStore = playlistTreeStore;
            generation = Volatile.Read(ref playlistTreeNotificationGeneration);
            BMSLibrary library = GetPlaylistLibrary();
            library.SynchronizeReferenceBMSTableSnapshots(
                eventArgs.Receipt.Tables
                    .Where(fact => fact?.ReferenceSnapshot != null)
                    .Select(fact => fact.ReferenceSnapshot));
            if (!ReferenceEquals(playlistTreeStore, sourceStore)
                || Volatile.Read(ref playlistTreeNotificationGeneration) != generation)
            {
                return;
            }
        }

        DispatchPlaylistStoreNotification(() =>
        {
            if (!ReferenceEquals(playlistTreeStore, sourceStore)
                || Volatile.Read(ref playlistTreeNotificationGeneration) != generation)
            {
                return;
            }
            RequestPlaylistReferenceSortInvalidation();
            PlaylistReferenceApplyPresentationRequested?.Invoke(
                this,
                new PlaylistReferenceApplyPresentationRequestedEventArgs(
                    eventArgs.Receipt.Reason,
                    eventArgs.Receipt.RequestVersion,
                    operationToken: 0L));
        });
    }

    private bool IsCurrentPlaylistTreeNotification(
        BMSPlaylist sourceStore,
        DispatcherCollection<BMSTable> sourceTables,
        long generation)
    {
        lock (playlistTreeStoreSyncRoot)
        {
            return ReferenceEquals(playlistTreeStore, sourceStore)
                && ReferenceEquals(observedPlaylistTreeTables, sourceTables)
                && Volatile.Read(ref playlistTreeNotificationGeneration) == generation;
        }
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
            RequestPlaylistSummaryDataRefresh(reason);
            return true;
        }
        if (playlistTreeRefreshDeferredProvider(reason))
        {
            RequestPlaylistSummaryDataRefresh(reason);
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
        RequestPlaylistSummaryDataRefresh(reason);
    }

    private void HandlePlaylistEntriesHydrationCompleted(int version)
    {
        PlaylistEntriesHydrationCompleted?.Invoke(
            this,
            new PlaylistEntriesHydrationVersionChangedEventArgs(version));
        if (playlistTreeRefreshDeferredProvider("playlist_entries_hydration_completed"))
        {
            RequestPlaylistSummaryDataRefresh(
                "playlist_entries_hydration_completed");
            return;
        }
        RequestPlaylistSummaryDataRefresh(
            "playlist_entries_hydration_completed");
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
