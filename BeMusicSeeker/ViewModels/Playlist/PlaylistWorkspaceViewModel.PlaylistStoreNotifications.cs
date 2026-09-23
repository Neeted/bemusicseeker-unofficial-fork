using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private void PlaylistTreeStoreTablesReplaced(object sender, EventArgs e)
    {
        lock (playlistHydrationNotificationDispatchLock)
        {
            PlaylistTreeStoreChangedCore(sender, nameof(BMSPlaylist.BMSTables), 0);
        }
    }

    private void PlaylistTreeStoreHydrationRequested(
        object sender,
        PlaylistHydrationVersionEventArgs e)
    {
        lock (playlistHydrationNotificationDispatchLock)
        {
            PlaylistTreeStoreChangedCore(
                sender,
                nameof(BMSPlaylist.PlaylistEntriesHydrationRequestedVersion),
                e?.Version ?? 0);
        }
    }

    private void PlaylistTreeStoreHydrationCompleted(
        object sender,
        PlaylistHydrationVersionEventArgs e)
    {
        lock (playlistHydrationNotificationDispatchLock)
        {
            PlaylistTreeStoreChangedCore(
                sender,
                nameof(BMSPlaylist.PlaylistEntriesHydrationCompletedVersion),
                e?.Version ?? 0);
        }
    }

    private void PlaylistTreeStoreChangedCore(
        object sender,
        string change,
        int hydrationVersion)
    {
        if (string.Equals(change, nameof(BMSPlaylist.BMSTables), StringComparison.Ordinal))
        {
            BMSPlaylist tablesSourceStore = null;
            ObservableCollection<BMSTable> tablesSourceTables = null;
            long tablesGeneration = 0L;
            MutatePlaylistTreeAfterInvalidatingHydrationReceipt(
                () => ReferenceEquals(sender, playlistTreeStore)
                    && !ReferenceEquals(observedPlaylistTreeTables, playlistTreeStore?.BMSTables),
                () =>
                {
                    AttachObservedPlaylistTreeTablesUnsafe(playlistTreeStore?.BMSTables);
                    tablesSourceStore = playlistTreeStore;
                    tablesSourceTables = observedPlaylistTreeTables;
                    tablesGeneration = Volatile.Read(ref playlistTreeNotificationGeneration);
                });
            if (tablesSourceStore == null)
            {
                lock (playlistTreeStoreSyncRoot)
                {
                    if (!ReferenceEquals(sender, playlistTreeStore))
                    {
                        return;
                    }
                    tablesSourceStore = playlistTreeStore;
                    tablesSourceTables = observedPlaylistTreeTables;
                    tablesGeneration = Volatile.Read(ref playlistTreeNotificationGeneration);
                }
            }
            DispatchPlaylistStoreNotification(() =>
            {
                if (IsCurrentPlaylistTreeNotification(
                    tablesSourceStore,
                    tablesSourceTables,
                    tablesGeneration))
                {
                    RequestPlaylistTreePresentationRefresh("playlist_tables_changed");
                }
            });
            return;
        }
        if (string.Equals(
            change,
            nameof(BMSPlaylist.PlaylistEntriesHydrationRequestedVersion),
            StringComparison.Ordinal))
        {
            BMSPlaylist requestSourceStore = null;
            ObservableCollection<BMSTable> requestSourceTables = null;
            long requestGeneration = 0L;
            int requestedHydrationVersion = 0;
            PlaylistHydrationCompletionReceipt requestReceipt = null;
            bool requestCreated = MutatePlaylistTreeAfterInvalidatingHydrationReceipt(
                () => ReferenceEquals(sender, playlistTreeStore),
                () =>
                {
                    requestSourceStore = playlistTreeStore;
                    requestSourceTables = observedPlaylistTreeTables;
                    requestGeneration = Volatile.Read(ref playlistTreeNotificationGeneration);
                    requestedHydrationVersion = hydrationVersion;
                    requestReceipt = new PlaylistHydrationCompletionReceipt();
                    playlistHydrationCompletionReceipt = requestReceipt;
                });
            if (!requestCreated)
            {
                return;
            }

            PlaylistEntriesHydrationRequested?.Invoke(
                this,
                new PlaylistEntriesHydrationVersionChangedEventArgs(
                    requestedHydrationVersion,
                    requestSourceStore,
                    requestSourceTables,
                    requestGeneration,
                    requestReceipt));
            PlaylistEntriesHydrationVersionChangedEventArgs pendingCompletion =
                CompletePlaylistHydrationRequestPublication(
                    requestSourceStore,
                    requestSourceTables,
                    requestGeneration,
                    requestReceipt);
            if (pendingCompletion != null)
            {
                RequestPlaylistEntriesHydrationCompleted(
                    pendingCompletion.Version,
                    pendingCompletion.SourceStore,
                    pendingCompletion.SourceTables,
                    pendingCompletion.Generation,
                    pendingCompletion.CompletionReceipt);
            }
            return;
        }

        if (!string.Equals(
            change,
            nameof(BMSPlaylist.PlaylistEntriesHydrationCompletedVersion),
            StringComparison.Ordinal))
        {
            return;
        }

        while (true)
        {
            lock (playlistTreeStoreSyncRoot)
            {
                if (!ReferenceEquals(sender, playlistTreeStore))
                {
                    return;
                }
                if (playlistHydrationCompletionReceipt != null
                    && !playlistHydrationCompletionReceipt.IsRequestPublished)
                {
                    pendingPlaylistHydrationCompletion = new PlaylistEntriesHydrationVersionChangedEventArgs(
                        hydrationVersion,
                        playlistTreeStore,
                        observedPlaylistTreeTables,
                        Volatile.Read(ref playlistTreeNotificationGeneration),
                        playlistHydrationCompletionReceipt);
                    return;
                }
            }

            BMSPlaylist completedSourceStore = null;
            ObservableCollection<BMSTable> completedSourceTables = null;
            long completedGeneration = 0L;
            int completedHydrationVersion = 0;
            PlaylistHydrationCompletionReceipt completionReceipt = null;
            bool completionCreated = MutatePlaylistTreeAfterInvalidatingHydrationReceipt(
                () => ReferenceEquals(sender, playlistTreeStore)
                    && (playlistHydrationCompletionReceipt == null
                        || playlistHydrationCompletionReceipt.IsRequestPublished),
                () =>
                {
                    completedSourceStore = playlistTreeStore;
                    completedSourceTables = observedPlaylistTreeTables;
                    completedGeneration = Volatile.Read(ref playlistTreeNotificationGeneration);
                    completedHydrationVersion = hydrationVersion;
                    completionReceipt =
                        new PlaylistHydrationCompletionReceipt(requestAlreadyPublished: true);
                    playlistHydrationCompletionReceipt = completionReceipt;
                });
            if (completionCreated)
            {
                if (IsCurrentPlaylistTreeNotification(
                    completedSourceStore,
                    completedSourceTables,
                    completedGeneration))
                {
                    RequestPlaylistEntriesHydrationCompleted(
                        completedHydrationVersion,
                        completedSourceStore,
                        completedSourceTables,
                        completedGeneration,
                        completionReceipt);
                }
                return;
            }

            lock (playlistTreeStoreSyncRoot)
            {
                if (!ReferenceEquals(sender, playlistTreeStore))
                {
                    return;
                }
            }
        }
    }

    private PlaylistEntriesHydrationVersionChangedEventArgs CompletePlaylistHydrationRequestPublication(
        BMSPlaylist sourceStore,
        ObservableCollection<BMSTable> sourceTables,
        long generation,
        PlaylistHydrationCompletionReceipt requestReceipt)
    {
        PlaylistHydrationCompletionReceipt requestReceiptToInvalidate = null;
        PlaylistEntriesHydrationVersionChangedEventArgs pendingCompletion = null;
        lock (playlistTreeStoreSyncRoot)
        {
            if (!ReferenceEquals(playlistTreeStore, sourceStore)
                || !ReferenceEquals(observedPlaylistTreeTables, sourceTables)
                || Volatile.Read(ref playlistTreeNotificationGeneration) != generation
                || !ReferenceEquals(playlistHydrationCompletionReceipt, requestReceipt)
                || !requestReceipt.MarkRequestPublished())
            {
                return null;
            }

            if (pendingPlaylistHydrationCompletion != null
                && ReferenceEquals(
                    pendingPlaylistHydrationCompletion.CompletionReceipt,
                    requestReceipt))
            {
                pendingCompletion = pendingPlaylistHydrationCompletion;
                pendingPlaylistHydrationCompletion = null;
                requestReceiptToInvalidate = DetachPlaylistHydrationCompletionReceiptUnsafe();
                PlaylistHydrationCompletionReceipt completionReceipt =
                    new(requestAlreadyPublished: true);
                playlistHydrationCompletionReceipt = completionReceipt;
                pendingCompletion = new PlaylistEntriesHydrationVersionChangedEventArgs(
                    pendingCompletion.Version,
                    pendingCompletion.SourceStore,
                    pendingCompletion.SourceTables,
                    pendingCompletion.Generation,
                    completionReceipt);
            }
        }
        requestReceiptToInvalidate?.Invalidate();
        return pendingCompletion;
    }

    private void PlaylistTreeTablesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        BMSPlaylist sourceStore;
        ObservableCollection<BMSTable> sourceTables;
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
        DispatchPlaylistStoreNotification(() =>
        {
            if (IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation))
            {
                RequestPlaylistTreePresentationRefresh("playlist_tables_collection_changed");
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
        }
        PlaylistReferenceApplyWorkflow.ApplyHydrationReceipt(
            sourceStore,
            generation,
            eventArgs.Receipt);
    }

    private bool IsCurrentPlaylistTreeNotification(
        BMSPlaylist sourceStore,
        ObservableCollection<BMSTable> sourceTables,
        long generation)
    {
        lock (playlistTreeStoreSyncRoot)
        {
            return ReferenceEquals(playlistTreeStore, sourceStore)
                && ReferenceEquals(observedPlaylistTreeTables, sourceTables)
                && Volatile.Read(ref playlistTreeNotificationGeneration) == generation;
        }
    }

    internal bool IsCurrentPlaylistTreeNotificationSnapshot(
        BMSPlaylist sourceStore,
        ObservableCollection<BMSTable> sourceTables,
        long generation)
    {
        return IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation);
    }

    internal bool TryBeginPlaylistHydrationNotification(
        BMSPlaylist sourceStore,
        ObservableCollection<BMSTable> sourceTables,
        long generation,
        PlaylistHydrationCompletionReceipt receipt)
    {
        if (sourceStore == null || receipt == null)
        {
            return true;
        }
        lock (playlistTreeStoreSyncRoot)
        {
            return ReferenceEquals(playlistTreeStore, sourceStore)
                && ReferenceEquals(observedPlaylistTreeTables, sourceTables)
                && Volatile.Read(ref playlistTreeNotificationGeneration) == generation
                && ReferenceEquals(playlistHydrationCompletionReceipt, receipt)
                && receipt.TryBegin();
        }
    }

    internal bool ExecuteCurrentPlaylistHydrationNotification(
        BMSPlaylist sourceStore,
        ObservableCollection<BMSTable> sourceTables,
        long generation,
        PlaylistHydrationCompletionReceipt receipt,
        Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        bool current;
        lock (playlistTreeStoreSyncRoot)
        {
            current = sourceStore == null
                || (ReferenceEquals(playlistTreeStore, sourceStore)
                    && ReferenceEquals(observedPlaylistTreeTables, sourceTables)
                    && Volatile.Read(ref playlistTreeNotificationGeneration) == generation);
        }
        if (!current)
        {
            return false;
        }

        if (receipt == null)
        {
            action();
            return true;
        }
        return receipt.ExecuteIfCurrent(action);
    }

    private void DispatchPlaylistStoreNotification(Action action)
    {
        if (action == null)
        {
            return;
        }
        dispatchPresentation(action);
    }

    private void RequestPlaylistTreePresentationRefresh(string reason)
    {
        PublishPlaylistCatalogChanged();
        RaiseRequiredEvent(
            PlaylistPresentationRefreshRequested,
            new PlaylistPresentationRefreshRequestedEventArgs(
                PlaylistPresentationRefreshKind.Tree,
                reason),
            nameof(PlaylistPresentationRefreshRequested));
    }

    internal void ApplyPlaylistTreePresentationRefresh(string reason, bool deferred)
    {
        if (deferred)
        {
            RequestPlaylistSummaryDataRefresh(reason);
            return;
        }

        RefreshPlaylistTreePresentation();
        PlaylistTablesPresentationChanged?.Invoke(this, EventArgs.Empty);
        RequestPlaylistSummaryDataRefresh(reason);
    }

    private void RequestPlaylistEntriesHydrationCompleted(
        int version,
        BMSPlaylist sourceStore,
        ObservableCollection<BMSTable> sourceTables,
        long generation,
        PlaylistHydrationCompletionReceipt receipt)
    {
        RaiseRequiredEvent(
            PlaylistPresentationRefreshRequested,
            new PlaylistPresentationRefreshRequestedEventArgs(
                PlaylistPresentationRefreshKind.HydrationCompleted,
                "playlist_entries_hydration_completed",
                hydrationVersion: version,
                hydrationSourceStore: sourceStore,
                hydrationSourceTables: sourceTables,
                hydrationNotificationGeneration: generation,
                hydrationCompletionReceipt: receipt),
            nameof(PlaylistPresentationRefreshRequested));
    }

    internal bool PublishPlaylistEntriesHydrationCompleted(
        int version,
        BMSPlaylist sourceStore = null,
        ObservableCollection<BMSTable> sourceTables = null,
        long generation = 0L,
        PlaylistHydrationCompletionReceipt receipt = null)
    {
        bool published = false;
        if (!ExecuteCurrentPlaylistHydrationNotification(
            sourceStore,
            sourceTables,
            generation,
            receipt,
            () =>
            {
                PlaylistEntriesHydrationCompleted?.Invoke(
                    this,
                    new PlaylistEntriesHydrationVersionChangedEventArgs(
                        version,
                        sourceStore,
                        sourceTables,
                        generation,
                        receipt));
                published = true;
            }))
        {
            return false;
        }
        return published;
    }

    internal bool ApplyPlaylistEntriesHydrationCompleted(
        int version,
        bool deferred,
        BMSPlaylist sourceStore = null,
        ObservableCollection<BMSTable> sourceTables = null,
        long generation = 0L,
        PlaylistHydrationCompletionReceipt receipt = null)
    {
        bool applied = false;
        void ApplyCore()
        {
            if (sourceStore != null
                && !IsCurrentPlaylistTreeNotificationSnapshot(sourceStore, sourceTables, generation))
            {
                return;
            }
            if (receipt != null && !receipt.IsBegun)
            {
                return;
            }
            RequestPlaylistSummaryDataRefresh(
                "playlist_entries_hydration_completed");
            if (!deferred)
            {
                RequestPlaylistDetailReloadRefresh();
            }
            applied = true;
        }

        if (receipt != null)
        {
            return ExecuteCurrentPlaylistHydrationNotification(
                sourceStore,
                sourceTables,
                generation,
                receipt,
                ApplyCore)
                && applied;
        }

        ApplyCore();
        return applied;
    }
}

internal sealed class PlaylistEntriesHydrationVersionChangedEventArgs : EventArgs
{
    internal PlaylistEntriesHydrationVersionChangedEventArgs(
        int version,
        BMSPlaylist sourceStore = null,
        ObservableCollection<BMSTable> sourceTables = null,
        long generation = 0L,
        PlaylistHydrationCompletionReceipt receipt = null)
    {
        Version = version;
        SourceStore = sourceStore;
        SourceTables = sourceTables;
        Generation = generation;
        CompletionReceipt = receipt;
    }

    internal int Version { get; }

    internal BMSPlaylist SourceStore { get; }

    internal ObservableCollection<BMSTable> SourceTables { get; }

    internal long Generation { get; }

    internal PlaylistHydrationCompletionReceipt CompletionReceipt { get; }
}
