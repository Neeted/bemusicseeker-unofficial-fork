using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// BMSPlaylist と BMSLibrary の current owner state を immutable lamp input へ投影します。
/// </summary>
internal sealed class BmsLibraryPlaylistLampDataSource : IPlaylistLampViewerDataSource, IDisposable
{
    private readonly BMSPlaylist playlist;

    private readonly BMSLibrary library;

    private readonly object subscriptionGate = new();

    private readonly HashSet<BMSTable> subscribedTables = [];

    private INotifyCollectionChanged subscribedTablesCollection;

    private readonly record struct RawEntrySnapshot(
        string Folder,
        string Md5,
        string Sha256,
        bool IsRemoved);

    private long changeSequence;

    private int disposed;

    /// <summary>
    /// production playlist lamp source を生成します。
    /// </summary>
    /// <param name="playlist">playlist owner。</param>
    /// <param name="library">catalog と score owner。</param>
    public BmsLibraryPlaylistLampDataSource(BMSPlaylist playlist, BMSLibrary library)
    {
        this.playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        playlist.PlaylistTablesReplaced += PlaylistTablesReplaced;
        playlist.PlaylistEntriesHydrationCompleted += PlaylistEntriesHydrationCompleted;
        library.PropertyChanged += LibraryPropertyChanged;
        SynchronizeTableCollectionSubscription();
        SynchronizeTableSubscriptions();
    }

    /// <inheritdoc />
    public event EventHandler<PlaylistLampViewerSourceChangedEventArgs> Changed;

    /// <inheritdoc />
    public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BmsLibraryPlaylistLampDataSource));
        }
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("playlistId is required.", nameof(playlistId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<PlaylistLampAggregationRequest>(
            Task.Run(() => CaptureCore(playlistId, cancellationToken), cancellationToken));
    }

    private PlaylistLampAggregationRequest CaptureCore(
        string playlistId,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BmsLibraryPlaylistLampDataSource));
        }
        cancellationToken.ThrowIfCancellationRequested();
        BMSTable table = FindTable(playlistId);
        if (table == null)
        {
            return PlaylistLampAggregationRequest.Deleted(playlistId);
        }

        PlaylistEntriesLoadState entriesLoadState;
        int entriesRevision;
        string failureMessage;
        string[] folderOrder;
        RawEntrySnapshot[] entries;
        DateTime playlistLastUpdated;
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entriesLoadState = table.PlaylistEntriesLoadState;
            entriesRevision = table.PlaylistEntriesRevision;
            failureMessage = table.EntriesLoadErrorMessage;
            if (entriesLoadState == PlaylistEntriesLoadState.Loading
                || entriesLoadState == PlaylistEntriesLoadState.NotLoaded
                || entriesLoadState == PlaylistEntriesLoadState.Failed)
            {
                folderOrder = [];
                entries = [];
                playlistLastUpdated = default;
            }
            else
            {
                folderOrder = (table.folder_list ?? []).ToArray();
                entries = (table.entries ?? [])
                    .Where(entry => entry != null)
                    .Select(entry => new RawEntrySnapshot(
                        entry.folder,
                        entry.md5,
                        entry.sha256,
                        entry.is_removed))
                    .ToArray();
                playlistLastUpdated = table.last_update;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (entriesLoadState == PlaylistEntriesLoadState.Loading
            || entriesLoadState == PlaylistEntriesLoadState.NotLoaded)
        {
            return new PlaylistLampAggregationRequest(
                playlistId,
                [],
                [],
                null,
                inputState: PlaylistLampInputState.Loading,
                dependencyStamp: CreateUnavailableDependencyStamp(entriesRevision));
        }
        if (entriesLoadState == PlaylistEntriesLoadState.Failed)
        {
            return new PlaylistLampAggregationRequest(
                playlistId,
                [],
                [],
                null,
                inputState: PlaylistLampInputState.Failed,
                failureMessage: failureMessage,
                dependencyStamp: CreateUnavailableDependencyStamp(entriesRevision));
        }

        PlaylistLibraryResolveIndexSnapshot resolveIndex = library.GetPlaylistLibraryResolveIndexSnapshot(
            cancellationToken,
            out _,
            out _);
        cancellationToken.ThrowIfCancellationRequested();
        BMSLibrary.ScoreSnapshot scoreSnapshot = library.GetScoreSnapshotForDiagnostics();
        PlaylistLampScoreSnapshot lampScoreSnapshot = PlaylistLampScoreSnapshot.FromBmsLibrarySnapshot(scoreSnapshot);
        var lampEntries = new List<PlaylistLampEntrySnapshot>(entries.Length);
        foreach (RawEntrySnapshot entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraryChartRef resolved = resolveIndex.ResolveChartForPlaylistHash(entry.Md5, entry.Sha256);
            string resolvedMd5 = resolved?.Md5;
            string resolvedSha256 = resolved?.Sha256;
            LR2SongDBExtended.chart_info chartInfo = library.ResolveChartInfo(entry.Sha256, entry.Md5);
            string chartInfoSha256 = chartInfo?.sha256;
            string identityKey = !string.IsNullOrWhiteSpace(resolvedMd5)
                ? "md5:" + resolvedMd5
                : !string.IsNullOrWhiteSpace(entry.Md5)
                    ? "md5:" + entry.Md5
                    : !string.IsNullOrWhiteSpace(resolvedSha256)
                        ? "sha256:" + resolvedSha256
                        : !string.IsNullOrWhiteSpace(entry.Sha256)
                            ? "sha256:" + entry.Sha256
                            : !string.IsNullOrWhiteSpace(chartInfoSha256)
                                ? "chart-info-sha256:" + chartInfoSha256
                                : resolved?.Path;
            lampEntries.Add(new PlaylistLampEntrySnapshot(
                entry.Folder,
                identityKey,
                resolved != null && !string.IsNullOrWhiteSpace(resolved.Path),
                entry.Md5,
                entry.Sha256,
                resolved?.Path,
                resolvedMd5,
                resolvedSha256,
                entry.IsRemoved,
                string.Equals(entry.Md5, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER, StringComparison.OrdinalIgnoreCase),
                chartInfoSha256));
        }

        StorageRowsVersionSnapshot storageRowsVersion = library.CatalogStorageRowsVersion;
        int catalogVersion = HashCode.Combine(storageRowsVersion.BmsRowsVersion, storageRowsVersion.BmsonRowsVersion);
        PlaylistLampDependencyStamp dependencyStamp = new(
            entriesRevision,
            catalogVersion,
            library.OwnedChartCollectionVersion,
            lampScoreSnapshot.Version,
            lampScoreSnapshot.SourceGeneration,
            lampScoreSnapshot.Source,
            lampScoreSnapshot.LoadStatus,
            library.ChartInfoIndexVersion);
        DateTime? lastUpdatedUtc = playlistLastUpdated == default ? null : playlistLastUpdated;
        return new PlaylistLampAggregationRequest(
            playlistId,
            folderOrder,
            lampEntries,
            lampScoreSnapshot,
            lastUpdatedUtc,
            inputState: PlaylistLampInputState.Loaded,
            dependencyStamp: dependencyStamp);
    }

    private PlaylistLampDependencyStamp CreateUnavailableDependencyStamp(int entriesRevision)
    {
        return new PlaylistLampDependencyStamp(
            entriesRevision,
            0,
            library.OwnedChartCollectionVersion,
            0,
            0L,
            ActiveScoreSource.None,
            ScoreTableLoadStatus.NotConfigured,
            library.ChartInfoIndexVersion);
    }

    /// <summary>
    /// source の event subscription を一度だけ解除します。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        playlist.PlaylistTablesReplaced -= PlaylistTablesReplaced;
        playlist.PlaylistEntriesHydrationCompleted -= PlaylistEntriesHydrationCompleted;
        library.PropertyChanged -= LibraryPropertyChanged;
        lock (subscriptionGate)
        {
            if (subscribedTablesCollection != null)
            {
                subscribedTablesCollection.CollectionChanged -= PlaylistTablesCollectionChanged;
                subscribedTablesCollection = null;
            }
            foreach (BMSTable table in subscribedTables)
            {
                table.PropertyChanged -= TablePropertyChanged;
            }
            subscribedTables.Clear();
        }
        Changed = null;
    }

    private BMSTable FindTable(string playlistId)
    {
        if (!int.TryParse(playlistId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericId))
        {
            return null;
        }
        playlist.AcquireReaderLockBMSTables();
        try
        {
            return (playlist.BMSTables ?? [])
                .FirstOrDefault(table => table?.playlist_id == numericId);
        }
        finally
        {
            playlist.FreeReaderLockBMSTables();
        }
    }

    private void SynchronizeTableSubscriptions()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        List<BMSTable> currentTables;
        playlist.AcquireReaderLockBMSTables();
        try
        {
            currentTables = [.. (playlist.BMSTables ?? []).Where(table => table != null)];
        }
        finally
        {
            playlist.FreeReaderLockBMSTables();
        }
        lock (subscriptionGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }
            foreach (BMSTable table in subscribedTables.Where(table => !currentTables.Contains(table)).ToArray())
            {
                table.PropertyChanged -= TablePropertyChanged;
                subscribedTables.Remove(table);
            }
            foreach (BMSTable table in currentTables.Where(table => !subscribedTables.Contains(table)))
            {
                table.PropertyChanged += TablePropertyChanged;
                subscribedTables.Add(table);
            }
        }
    }

    private void PlaylistTablesReplaced(object sender, EventArgs e)
    {
        SynchronizeTableCollectionSubscription();
        SynchronizeTableSubscriptions();
        NotifyChanged(string.Empty);
    }

    private void PlaylistTablesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        // CollectionChanged can be raised while BMSPlaylist owns its writer lock.  The
        // actual snapshot and table-subscription reconciliation are deferred by NotifyChanged.
        NotifyChanged(string.Empty);
    }

    private void PlaylistEntriesHydrationCompleted(object sender, PlaylistHydrationVersionEventArgs e)
    {
        NotifyChanged(string.Empty);
    }

    private void TablePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e == null
            || string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(BMSTable.PlaylistEntriesRevision)
                or nameof(BMSTable.PlaylistEntriesLoadState)
                or nameof(BMSTable.EntriesLoadErrorMessage)
                or nameof(BMSTable.Folder_order)
                or nameof(BMSTable.folder_list)
                or nameof(BMSTable.last_update))
        {
            NotifyChanged((sender as BMSTable)?.playlist_id?.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void LibraryPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e == null
            || string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName is nameof(BMSLibrary.ScoreSnapshotVersion)
                or nameof(BMSLibrary.ScoreSnapshotReady)
                or nameof(BMSLibrary.BMSFiles)
                or nameof(BMSLibrary.BmsonSongs)
                or nameof(BMSLibrary.BMSParentFolderListCacheVersion)
                or nameof(BMSLibrary.OwnedChartCollectionVersion)
                or nameof(BMSLibrary.ChartInfoIndexVersion))
        {
            NotifyChanged(string.Empty);
        }
    }

    private void NotifyChanged(string playlistId)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        long sequence = Interlocked.Increment(ref changeSequence);
        PlaylistLampViewerSourceChangedEventArgs args = new(playlistId, sequence);
        _ = Task.Run(() =>
        {
            // Table/library notifications may arrive while their owner holds a write
            // lock.  Keep every external subscriber off that publication stack.
            if (Volatile.Read(ref disposed) == 0)
            {
                SynchronizeTableCollectionSubscription();
                SynchronizeTableSubscriptions();
                Changed?.Invoke(this, args);
            }
        });
    }

    private void SynchronizeTableCollectionSubscription()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        INotifyCollectionChanged currentCollection = playlist.BMSTables as INotifyCollectionChanged;
        lock (subscriptionGate)
        {
            if (Volatile.Read(ref disposed) != 0
                || ReferenceEquals(currentCollection, subscribedTablesCollection))
            {
                return;
            }
            if (subscribedTablesCollection != null)
            {
                subscribedTablesCollection.CollectionChanged -= PlaylistTablesCollectionChanged;
            }
            subscribedTablesCollection = currentCollection;
            if (subscribedTablesCollection != null)
            {
                subscribedTablesCollection.CollectionChanged += PlaylistTablesCollectionChanged;
            }
        }
    }
}
