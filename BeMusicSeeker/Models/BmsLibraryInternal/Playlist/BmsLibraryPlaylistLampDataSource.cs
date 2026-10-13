using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// BMSPlaylist と BMSLibrary の current owner state を immutable lamp input へ投影します。
/// </summary>
internal sealed class BmsLibraryPlaylistLampDataSource : IPlaylistLampViewerDataSource, IDisposable, IAsyncDisposable
{
    private readonly BMSPlaylist playlist;

    private readonly BMSLibrary library;

    private readonly Func<PlaylistLampHistoricalScoreSourceContext> historicalSourceContextFactory;

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

    private readonly object activityGate = new();

    private readonly HashSet<TaskCompletionSource> activities = [];

    private Task stopTask;

    /// <summary>
    /// production playlist lamp source を生成します。
    /// </summary>
    /// <param name="playlist">playlist owner。</param>
    /// <param name="library">catalog と score owner。</param>
    /// <param name="historicalSourceContextFactory">
    /// active provider の read-only history source context。null の場合は Latest のみを提供します。
    /// </param>
    public BmsLibraryPlaylistLampDataSource(
        BMSPlaylist playlist,
        BMSLibrary library,
        Func<PlaylistLampHistoricalScoreSourceContext> historicalSourceContextFactory = null)
    {
        this.playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.historicalSourceContextFactory = historicalSourceContextFactory;
        playlist.PlaylistTablesReplaced += PlaylistTablesReplaced;
        playlist.PlaylistEntriesHydrationCompleted += PlaylistEntriesHydrationCompleted;
        library.PropertyChanged += LibraryPropertyChanged;
        SynchronizeTableCollectionSubscription();
        SynchronizeTableSubscriptions();
    }

    /// <inheritdoc />
    public event EventHandler<PlaylistLampViewerSourceChangedEventArgs> Changed;

    /// <summary>
    /// immutable入力を捕捉します。開始済みcaptureの実終端は呼出元sessionが所有し、
    /// 全captureを待ってからこのsourceを破棄する必要があります。
    /// </summary>
    /// <param name="query">捕捉するplaylistと基準日。</param>
    /// <param name="cancellationToken">捕捉の取消token。</param>
    /// <returns>captureの実終端までを表す要求Task。</returns>
    public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
        PlaylistLampViewerQuery query,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BmsLibraryPlaylistLampDataSource));
        }
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<PlaylistLampAggregationRequest>(
            Task.Run(() => CaptureCore(query, cancellationToken), cancellationToken));
    }

    private PlaylistLampAggregationRequest CaptureCore(
        PlaylistLampViewerQuery query,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(BmsLibraryPlaylistLampDataSource));
        }
        cancellationToken.ThrowIfCancellationRequested();
        BMSTable table = FindTable(query.PlaylistId);
        if (table == null)
        {
            return PlaylistLampAggregationRequest.Deleted(query.PlaylistId, query: query);
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
                query.PlaylistId,
                [],
                [],
                null,
                inputState: PlaylistLampInputState.Loading,
                dependencyStamp: CreateUnavailableDependencyStamp(entriesRevision),
                query: query);
        }
        if (entriesLoadState == PlaylistEntriesLoadState.Failed)
        {
            return new PlaylistLampAggregationRequest(
                query.PlaylistId,
                [],
                [],
                null,
                inputState: PlaylistLampInputState.Failed,
                failureMessage: failureMessage,
                dependencyStamp: CreateUnavailableDependencyStamp(entriesRevision),
                query: query);
        }

        PlaylistLibraryResolveIndexSnapshot resolveIndex = library.GetPlaylistLibraryResolveIndexSnapshot(
            cancellationToken,
            out _,
            out _);
        cancellationToken.ThrowIfCancellationRequested();
        BMSLibrary.ScoreSnapshot scoreSnapshot = library.GetScoreSnapshotForDiagnostics();
        var currentLampScoreSnapshot = PlaylistLampScoreSnapshot.FromBmsLibrarySnapshot(scoreSnapshot);
        PlaylistLampHistoricalScoreSnapshotResult historicalResult = ResolveHistoricalScoreSnapshot(
            query,
            currentLampScoreSnapshot,
            cancellationToken);
        PlaylistLampScoreSnapshot lampScoreSnapshot = historicalResult.ScoreSnapshot;
        var lampEntries = new List<PlaylistLampEntrySnapshot>(entries.Length);
        foreach (RawEntrySnapshot entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraryChartRef resolved = resolveIndex.ResolveChartForPlaylistHash(entry.Md5, entry.Sha256);
            string resolvedMd5 = resolved?.Md5;
            string resolvedSha256 = resolved?.Sha256;
            BeMusicSeeker.Models.ChartDetails chartInfo = library.ResolveChartInfo(entry.Sha256, entry.Md5);
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

        OwnedChartCollectionVersionSnapshot storageRowsVersion = library.CatalogStorageRowsVersion;
        int catalogVersion = HashCode.Combine(storageRowsVersion.OwnedCollectionVersion);
        PlaylistLampDependencyStamp dependencyStamp = new(
            entriesRevision,
            catalogVersion,
            library.OwnedCollectionVersion,
            lampScoreSnapshot.Version,
            lampScoreSnapshot.SourceGeneration,
            lampScoreSnapshot.Source,
            lampScoreSnapshot.LoadStatus,
            library.ChartInfoIndexVersion);
        DateTime? playlistLastUpdatedValue = playlistLastUpdated == default ? null : playlistLastUpdated;
        return new PlaylistLampAggregationRequest(
            query.PlaylistId,
            folderOrder,
            lampEntries,
            lampScoreSnapshot,
            playlistLastUpdatedValue,
            inputState: PlaylistLampInputState.Loaded,
            dependencyStamp: dependencyStamp,
            query: query,
            historicalDateRange: historicalResult.DateRange,
            historicalStatus: historicalResult.Status,
            historicalFailureMessage: historicalResult.FailureMessage);
    }

    private PlaylistLampHistoricalScoreSnapshotResult ResolveHistoricalScoreSnapshot(
        PlaylistLampViewerQuery query,
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        CancellationToken cancellationToken)
    {
        if (historicalSourceContextFactory == null)
        {
            if (!query.SelectedLocalDate.HasValue)
            {
                return new PlaylistLampHistoricalScoreSnapshotResult(
                    currentScoreSnapshot,
                    new PlaylistLampHistoricalDateRange(null, DateTime.Today),
                    null,
                    PlaylistLampHistoricalSnapshotStatus.Latest);
            }
            return new PlaylistLampHistoricalScoreSnapshotResult(
                currentScoreSnapshot.WithScores(
                    new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
                    "Historical score source is not configured.",
                    ScoreTableLoadStatus.Failed),
                new PlaylistLampHistoricalDateRange(null, DateTime.Today),
                query.SelectedLocalDate,
                PlaylistLampHistoricalSnapshotStatus.Unavailable,
                "Historical score source is not configured.");
        }

        PlaylistLampHistoricalScoreSourceContext sourceContext;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceContext = historicalSourceContextFactory();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateHistoricalSourceUnavailable(query, currentScoreSnapshot, exception);
        }
        return new PlaylistLampHistoricalScoreSnapshotReader().Read(
            sourceContext,
            currentScoreSnapshot,
            query.SelectedLocalDate,
            cancellationToken);
    }

    private static PlaylistLampHistoricalScoreSnapshotResult CreateHistoricalSourceUnavailable(
        PlaylistLampViewerQuery query,
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        Exception exception)
    {
        string message = string.IsNullOrWhiteSpace(exception?.Message)
            ? "Historical score source could not be resolved."
            : exception.Message;
        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
        PlaylistLampHistoricalDateRange range = new(null, today);
        if (!query.SelectedLocalDate.HasValue)
        {
            return new PlaylistLampHistoricalScoreSnapshotResult(
                currentScoreSnapshot,
                range,
                null,
                PlaylistLampHistoricalSnapshotStatus.Latest,
                message);
        }
        return new PlaylistLampHistoricalScoreSnapshotResult(
            currentScoreSnapshot.WithScores(
                new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
                message,
                ScoreTableLoadStatus.Failed),
            range,
            query.SelectedLocalDate,
            PlaylistLampHistoricalSnapshotStatus.Unavailable,
            message);
    }

    private PlaylistLampDependencyStamp CreateUnavailableDependencyStamp(int entriesRevision)
    {
        return new PlaylistLampDependencyStamp(
            entriesRevision,
            0,
            library.OwnedCollectionVersion,
            0,
            0L,
            ActiveScoreSource.None,
            ScoreTableLoadStatus.NotConfigured,
            library.ChartInfoIndexVersion);
    }

    /// <summary>
    /// 新しいsource通知活動を拒否し、購読解除と受理済み活動の回収を一度要求します。
    /// model lock待ちや既取得Changed delegateを同期的に待ちません。
    /// </summary>
    public void Dispose()
    {
        TaskCompletionSource completion;
        Task[] accepted;
        lock (activityGate)
        {
            if (stopTask != null) { return; }
            disposed = 1;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
            accepted = activities.Select(activity => activity.Task).ToArray();
        }
        _ = Task.Run(() => StopCoreAsync(accepted, completion));
    }

    /// <summary>
    /// 購読解除自身、キュー前score reader、queued playlist readerと既取得通知の実終端を待ちます。
    /// captureは呼出元が先に待ちます。購読解除の故障は後続呼出しにも保持し、再試行しません。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (activityGate) { return new ValueTask(stopTask); }
    }

    private async Task StopCoreAsync(Task[] accepted, TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        void unsubscribe(Action action)
        {
            try { action(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        unsubscribe(() => playlist.PlaylistTablesReplaced -= PlaylistTablesReplaced);
        unsubscribe(() => playlist.PlaylistEntriesHydrationCompleted -= PlaylistEntriesHydrationCompleted);
        unsubscribe(() => library.PropertyChanged -= LibraryPropertyChanged);
        BMSTable[] tables;
        INotifyCollectionChanged collection;
        lock (subscriptionGate)
        {
            collection = subscribedTablesCollection;
            subscribedTablesCollection = null;
            tables = subscribedTables.ToArray();
            subscribedTables.Clear();
        }
        if (collection != null) { unsubscribe(() => collection.CollectionChanged -= PlaylistTablesCollectionChanged); }
        foreach (BMSTable table in tables) { unsubscribe(() => table.PropertyChanged -= TablePropertyChanged); }
        Changed = null;
        await Task.WhenAll(accepted).ConfigureAwait(false);
        if (failures.Count == 0) { completion.TrySetResult(); }
        else { completion.TrySetException(new AggregateException(failures)); }
    }

    private TaskCompletionSource ReserveActivity()
    {
        lock (activityGate)
        {
            if (disposed != 0) { return null; }
            var activity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            activities.Add(activity);
            return activity;
        }
    }

    private void CompleteActivity(TaskCompletionSource activity)
    {
        lock (activityGate)
        {
            activity.TrySetResult();
            activities.Remove(activity);
        }
    }

    private void RunSourceActivity(Action action)
    {
        TaskCompletionSource activity = ReserveActivity();
        if (activity == null) { return; }
        try { action(); }
        finally { CompleteActivity(activity); }
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
            if (Volatile.Read(ref disposed) != 0) { return; }
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
        RunSourceActivity(() =>
        {
            SynchronizeTableCollectionSubscription();
            SynchronizeTableSubscriptions();
            NotifyChanged(string.Empty);
        });
    }

    private void PlaylistTablesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        => RunSourceActivity(() => NotifyChanged(string.Empty));

    private void PlaylistEntriesHydrationCompleted(object sender, PlaylistHydrationVersionEventArgs e)
        => RunSourceActivity(() => NotifyChanged(string.Empty));

    private void TablePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        RunSourceActivity(() =>
        {
            if (e == null || string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName is nameof(BMSTable.PlaylistEntriesRevision)
                    or nameof(BMSTable.PlaylistEntriesLoadState)
                    or nameof(BMSTable.EntriesLoadErrorMessage)
                    or nameof(BMSTable.Folder_order)
                    or nameof(BMSTable.folder_list)
                    or nameof(BMSTable.last_update))
            {
                NotifyChanged((sender as BMSTable)?.playlist_id?.ToString(CultureInfo.InvariantCulture));
            }
        });
    }

    private void LibraryPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        RunSourceActivity(() =>
        {
            if (e == null || string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName is nameof(BMSLibrary.ScoreSnapshotVersion)
                    or nameof(BMSLibrary.ScoreSnapshotReady)
                    or nameof(BMSLibrary.BmsCharts)
                    or nameof(BMSLibrary.BmsonCharts)
                    or nameof(BMSLibrary.BMSParentFolderListCacheVersion)
                    or nameof(BMSLibrary.OwnedCollectionVersion)
                    or nameof(BMSLibrary.ChartInfoIndexVersion))
            {
                NotifyChanged(string.Empty);
            }
        });
    }

    private void NotifyChanged(string playlistId)
    {
        if (Volatile.Read(ref disposed) != 0) { return; }
        long sequence = Interlocked.Increment(ref changeSequence);
        PlaylistLampViewerSourceChangedEventArgs args = new(
            playlistId,
            sequence,
            sourceIdentity: library.GetActiveScoreSourceForDiagnostics().ToString());
        // 同期callbackがscore readerを使う期間から、worker予約までに所有の切れ目を作りません。
        TaskCompletionSource activity = ReserveActivity();
        if (activity == null) { return; }
        _ = Task.Run(() =>
        {
            try
            {
                if (Volatile.Read(ref disposed) != 0) { return; }
                SynchronizeTableCollectionSubscription();
                SynchronizeTableSubscriptions();
                if (Volatile.Read(ref disposed) == 0) { Changed?.Invoke(this, args); }
            }
            catch (Exception exception)
            {
                // 任意通知の診断も活動終端より前に試み、終了後の別ログ継続を作りません。
                try { NLogWrapper.FileLogger?.Warn(exception, "BmsLibraryPlaylistLampDataSource.NotifyChanged"); }
                catch { /* 任意診断の故障を未確認解放へ混ぜません。 */ }
            }
            finally { CompleteActivity(activity); }
        });
    }

    private void SynchronizeTableCollectionSubscription()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        var currentCollection = playlist.BMSTables as INotifyCollectionChanged;
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
