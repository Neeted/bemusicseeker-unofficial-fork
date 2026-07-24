using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Livet;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private void PlaylistTreeStorePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        lock (playlistHydrationNotificationDispatchLock)
        {
            PlaylistTreeStorePropertyChangedCore(sender, e);
        }
    }

    private void PlaylistTreeStorePropertyChangedCore(object sender, PropertyChangedEventArgs e)
    {
        BMSPlaylist sourceStore;
        DispatcherCollection<BMSTable> sourceTables;
        long generation;
        int hydrationVersion;
        PlaylistHydrationCompletionReceipt hydrationReceipt = null;
        PlaylistHydrationCompletionReceipt receiptToInvalidate = null;
        bool tablesChanged = false;
        bool hydrationRequested = false;
        bool hydrationCompleted = false;
        PlaylistEntriesHydrationVersionChangedEventArgs pendingCompletionToPublish = null;
        lock (playlistTreeStoreSyncRoot)
        {
            if (!ReferenceEquals(sender, playlistTreeStore))
            {
                return;
            }
            sourceStore = playlistTreeStore;
            if (string.Equals(e?.PropertyName, nameof(BMSPlaylist.BMSTables), StringComparison.Ordinal))
            {
                receiptToInvalidate = AttachObservedPlaylistTreeTables(playlistTreeStore?.BMSTables);
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
                receiptToInvalidate = DetachPlaylistHydrationCompletionReceiptUnsafe();
                hydrationReceipt = new PlaylistHydrationCompletionReceipt();
                playlistHydrationCompletionReceipt = hydrationReceipt;
                hydrationRequested = true;
            }
            else if (string.Equals(e?.PropertyName, nameof(BMSPlaylist.PlaylistEntriesHydrationCompletedVersion), StringComparison.Ordinal))
            {
                hydrationVersion = playlistTreeStore?.PlaylistEntriesHydrationCompletedVersion ?? 0;
                sourceTables = observedPlaylistTreeTables;
                generation = Volatile.Read(ref playlistTreeNotificationGeneration);
                if (playlistHydrationCompletionReceipt != null
                    && !playlistHydrationCompletionReceipt.IsRequestPublished)
                {
                    pendingPlaylistHydrationCompletion = new PlaylistEntriesHydrationVersionChangedEventArgs(
                        hydrationVersion,
                        sourceStore,
                        sourceTables,
                        generation,
                        playlistHydrationCompletionReceipt);
                }
                else
                {
                    receiptToInvalidate = DetachPlaylistHydrationCompletionReceiptUnsafe();
                    hydrationReceipt = new PlaylistHydrationCompletionReceipt(requestAlreadyPublished: true);
                    playlistHydrationCompletionReceipt = hydrationReceipt;
                    hydrationCompleted = true;
                }
            }
            else
            {
                return;
            }
        }
        receiptToInvalidate?.Invalidate();

        if (tablesChanged)
        {
            DispatchPlaylistStoreNotification(() =>
            {
                if (IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation))
                {
                    RequestPlaylistTreePresentationRefresh("playlist_tables_changed");
                }
            });
            return;
        }
        if (hydrationRequested)
        {
            if (!IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation))
            {
                return;
            }
            PlaylistEntriesHydrationRequested?.Invoke(
                this,
                new PlaylistEntriesHydrationVersionChangedEventArgs(
                    hydrationVersion,
                    sourceStore,
                    sourceTables,
                    generation,
                    hydrationReceipt));
            pendingCompletionToPublish = CompletePlaylistHydrationRequestPublication(
                sourceStore,
                sourceTables,
                generation,
                hydrationReceipt);
            if (pendingCompletionToPublish != null)
            {
                RequestPlaylistEntriesHydrationCompleted(
                    pendingCompletionToPublish.Version,
                    pendingCompletionToPublish.SourceStore,
                    pendingCompletionToPublish.SourceTables,
                    pendingCompletionToPublish.Generation,
                    pendingCompletionToPublish.CompletionReceipt);
            }
            return;
        }
        if (hydrationCompleted)
        {
            if (IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation))
            {
                RequestPlaylistEntriesHydrationCompleted(
                    hydrationVersion,
                    sourceStore,
                    sourceTables,
                    generation,
                    hydrationReceipt);
            }
        }
    }

    private PlaylistEntriesHydrationVersionChangedEventArgs CompletePlaylistHydrationRequestPublication(
        BMSPlaylist sourceStore,
        DispatcherCollection<BMSTable> sourceTables,
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

    internal bool IsCurrentPlaylistTreeNotificationSnapshot(
        BMSPlaylist sourceStore,
        DispatcherCollection<BMSTable> sourceTables,
        long generation)
    {
        return IsCurrentPlaylistTreeNotification(sourceStore, sourceTables, generation);
    }

    internal bool TryBeginPlaylistHydrationNotification(
        BMSPlaylist sourceStore,
        DispatcherCollection<BMSTable> sourceTables,
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
        DispatcherCollection<BMSTable> sourceTables,
        long generation,
        PlaylistHydrationCompletionReceipt receipt,
        Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        lock (playlistTreeStoreSyncRoot)
        {
            if (sourceStore != null
                && (!ReferenceEquals(playlistTreeStore, sourceStore)
                    || !ReferenceEquals(observedPlaylistTreeTables, sourceTables)
                    || Volatile.Read(ref playlistTreeNotificationGeneration) != generation))
            {
                return false;
            }

            if (receipt == null)
            {
                action();
                return true;
            }

            // Hold the store lease for the whole receipt-gated callback.  A replacement
            // may invalidate the receipt only after it commits its new source/generation,
            // so the callback cannot validate an old snapshot and then perform its side
            // effect against a newly attached store.
            return receipt.ExecuteIfCurrent(action);
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

    private void RequestPlaylistTreePresentationRefresh(string reason)
    {
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
        DispatcherCollection<BMSTable> sourceTables,
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
        DispatcherCollection<BMSTable> sourceTables = null,
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
        DispatcherCollection<BMSTable> sourceTables = null,
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
        DispatcherCollection<BMSTable> sourceTables = null,
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

    internal DispatcherCollection<BMSTable> SourceTables { get; }

    internal long Generation { get; }

    internal PlaylistHydrationCompletionReceipt CompletionReceipt { get; }
}
