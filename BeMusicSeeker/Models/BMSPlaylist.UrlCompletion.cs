using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Properties;
using Ribbit.Logging;
using Ribbit.Net;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public partial class BMSPlaylist
{
    private const int PlaylistUrlCompletionTimeoutMs = 60000;

    private static readonly AppHttpClient playlistUrlCompletionTsvHttpClient = AppHttpClient.Create(PlaylistUrlCompletionTimeoutMs);

    private static readonly AppHttpClient playlistUrlCompletionStellaHttpClient = AppHttpClient.Create(PlaylistUrlCompletionTimeoutMs);

    private static readonly object playlistUrlCompletionSnapshotLock = new();

    private readonly SemaphoreSlim playlistUrlCompletionRefreshSemaphore = new(1, 1);

    private static IReadOnlyDictionary<string, PlaylistUrlCompletionCandidate> playlistUrlCompletionTsvSnapshot = new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, PlaylistUrlCompletionCandidate> playlistUrlCompletionStellaSnapshot = new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase);

    private static Uri playlistUrlCompletionTsvSnapshotSourceUri;

    private static bool playlistUrlCompletionTsvSnapshotFetched;

    private static bool playlistUrlCompletionStellaSnapshotFetched;

    private long playlistUrlCompletionRequestedVersion;

    private long playlistUrlCompletionCompletedVersion;

    private string playlistUrlCompletionLatestReason = "initial";

    internal static Func<Uri, CancellationToken, Task<string>> PlaylistUrlCompletionTsvContentFetcherForTests { get; set; }

    internal static Func<Uri, CancellationToken, Task<string>> PlaylistUrlCompletionStellaContentFetcherForTests { get; set; }

    internal static void ResetPlaylistUrlCompletionSourceCacheForTests()
    {
        lock (playlistUrlCompletionSnapshotLock)
        {
            playlistUrlCompletionTsvSnapshot = new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase);
            playlistUrlCompletionStellaSnapshot = new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase);
            playlistUrlCompletionTsvSnapshotSourceUri = null;
            playlistUrlCompletionTsvSnapshotFetched = false;
            playlistUrlCompletionStellaSnapshotFetched = false;
        }
    }

    /// <summary>
    /// プレイリスト URL 補完の再取得と再適用をバックグラウンドで予約します。
    /// 初期化や外部同期と競合しても最新要求だけへ収束するよう、versioning と単一実行セマフォで直列化します。
    /// </summary>
    /// <param name="reason">ログに残す呼び出し理由。</param>
    internal void SchedulePlaylistUrlCompletionRefresh(string reason)
    {
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        playlistUrlCompletionLatestReason = normalizedReason;
        long requestedVersion = Interlocked.Increment(ref playlistUrlCompletionRequestedVersion);
        NLogWrapper.FileLogger?.Info("playlist_url_completion schedule version=" + requestedVersion + " reason=" + normalizedReason);
        async Task work()
        {
            try
            {
                if (IsShutdownRequested)
                {
                    NLogWrapper.FileLogger?.Info("playlist_url_completion skipped reason=shutdown_requested requestReason=" + normalizedReason);
                    return;
                }
                await RefreshPlaylistUrlCompletionLoopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NLogWrapper.FileLogger?.Warn(ex, "playlist_url_completion_refresh_failed reason=" + normalizedReason);
            }
        }
        if (StartupBackgroundTaskScheduler != null)
        {
            if (StartupBackgroundTaskScheduler("playlist_url_completion", normalizedReason, "playlist_entries_hydration", work))
            {
                return;
            }
            NLogWrapper.FileLogger?.Info("playlist_url_completion skipped reason=startup_scheduler_rejected requestReason=" + normalizedReason);
            return;
        }
        if (IsShutdownRequested)
        {
            NLogWrapper.FileLogger?.Info("playlist_url_completion skipped reason=shutdown_requested requestReason=" + normalizedReason);
            return;
        }
        _ = Task.Run(work);
    }

    /// <summary>
    /// 既に保持している補完スナップショットだけを使って、単一プレイリストへランタイム補完を適用します。
    /// 外部同期直後の新テーブルへ即時反映したいときに使い、補完のために DB 保存は行いません。
    /// </summary>
    /// <param name="table">適用対象のプレイリスト。</param>
    /// <param name="reason">ログに残す呼び出し理由。</param>
    internal void ApplyCachedPlaylistUrlCompletionToTable(BMSTable table, string reason)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        PlaylistUrlCompletionApplyStats applyStats = ApplyPlaylistUrlCompletionToTableCore(table);
        NLogWrapper.FileLogger?.Info("playlist_url_completion apply_cached_table reason=" + (reason ?? string.Empty) + " table=" + FormatTextForLog(table.name) + " entries=" + applyStats.EntryCount + " changed=" + applyStats.ChangedEntryCount + " runtimeUrl=" + applyStats.RuntimeUrlCount + " runtimeUrlDiff=" + applyStats.RuntimeUrlDiffCount);
    }

    private async Task RefreshPlaylistUrlCompletionLoopAsync()
    {
        await playlistUrlCompletionRefreshSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                long targetVersion = Interlocked.Read(ref playlistUrlCompletionRequestedVersion);
                if (Interlocked.Read(ref playlistUrlCompletionCompletedVersion) >= targetVersion)
                {
                    return;
                }
                string reason = playlistUrlCompletionLatestReason;
                await RefreshPlaylistUrlCompletionCoreAsync(targetVersion, reason).ConfigureAwait(false);
                Interlocked.Exchange(ref playlistUrlCompletionCompletedVersion, targetVersion);
                if (Interlocked.Read(ref playlistUrlCompletionRequestedVersion) == targetVersion)
                {
                    return;
                }
                // NOTE:
                // 起動直後や連続リロードでは複数トリガーが短時間に重なります。
                // ここで最新 version を取り直すことで、中間要求を捨てつつ最後の 1 回だけを確実に反映します。
            }
        }
        finally
        {
            playlistUrlCompletionRefreshSemaphore.Release();
        }
    }

    private async Task RefreshPlaylistUrlCompletionCoreAsync(long version, string reason)
    {
        NLogWrapper.FileLogger?.Info("playlist_url_completion start version=" + version + " reason=" + reason);
        await EnsureAllPlaylistEntriesLoadedAsync("playlist_url_completion").ConfigureAwait(false);
        if (!Settings.Default.EnablePlaylistUrlCompletion)
        {
            PlaylistUrlCompletionApplyStats disabledApplyStats = ClearPlaylistUrlCompletionFromLoadedTables();
            NLogWrapper.FileLogger?.Info("playlist_url_completion finish version=" + version + " reason=" + reason + " disabled=true tables=" + disabledApplyStats.TableCount + " entries=" + disabledApplyStats.EntryCount + " changed=" + disabledApplyStats.ChangedEntryCount);
            return;
        }
        string configuredTsvSource = Settings.Default.PlaylistMd5UrlMappingTsvUri;
        Uri tsvSourceUri = null;
        bool hasConfiguredTsvSource = !string.IsNullOrWhiteSpace(configuredTsvSource);
        bool hasValidConfiguredTsvSource = !hasConfiguredTsvSource || PlaylistUrlCompletionSupport.TryResolveSourceUri(configuredTsvSource, out tsvSourceUri);
        if (hasConfiguredTsvSource && !hasValidConfiguredTsvSource)
        {
            NLogWrapper.FileLogger?.Warn("playlist_url_completion_tsv_invalid_source raw=" + configuredTsvSource);
        }
        Task<PlaylistUrlCompletionRefreshSourceResult> tsvTask = FetchPlaylistUrlCompletionTsvSnapshotAsync(tsvSourceUri, hasConfiguredTsvSource, hasValidConfiguredTsvSource);
        Task<PlaylistUrlCompletionRefreshSourceResult> stellaTask = FetchPlaylistUrlCompletionStellaSnapshotAsync();
        PlaylistUrlCompletionRefreshSourceResult[] refreshResults = await Task.WhenAll(tsvTask, stellaTask).ConfigureAwait(false);
        PlaylistUrlCompletionRefreshSourceResult tsvResult = refreshResults[0];
        PlaylistUrlCompletionRefreshSourceResult stellaResult = refreshResults[1];
        PlaylistUrlCompletionApplyStats applyStats = ApplyPlaylistUrlCompletionToLoadedTablesCore();
        NLogWrapper.FileLogger?.Info("playlist_url_completion finish version=" + version + " reason=" + reason + " tsvCandidates=" + tsvResult.Snapshot.CandidateCount + " tsvDuplicateSkipped=" + tsvResult.Snapshot.DuplicateCount + " tsvIgnored=" + tsvResult.Snapshot.IgnoredRowCount + " tsvSnapshotReplaced=" + tsvResult.ShouldReplaceSnapshot.ToString().ToLowerInvariant() + " tsvSnapshotCached=" + tsvResult.UsedCachedSnapshot.ToString().ToLowerInvariant() + " stellaCandidates=" + stellaResult.Snapshot.CandidateCount + " stellaDuplicateSkipped=" + stellaResult.Snapshot.DuplicateCount + " stellaIgnored=" + stellaResult.Snapshot.IgnoredRowCount + " stellaSnapshotReplaced=" + stellaResult.ShouldReplaceSnapshot.ToString().ToLowerInvariant() + " stellaSnapshotCached=" + stellaResult.UsedCachedSnapshot.ToString().ToLowerInvariant() + " tables=" + applyStats.TableCount + " entries=" + applyStats.EntryCount + " changed=" + applyStats.ChangedEntryCount + " runtimeUrl=" + applyStats.RuntimeUrlCount + " runtimeUrlDiff=" + applyStats.RuntimeUrlDiffCount);
    }

    private async Task<PlaylistUrlCompletionRefreshSourceResult> FetchPlaylistUrlCompletionTsvSnapshotAsync(Uri sourceUri, bool hasConfiguredSource, bool hasValidConfiguredSource)
    {
        if (!hasConfiguredSource)
        {
            lock (playlistUrlCompletionSnapshotLock)
            {
                playlistUrlCompletionTsvSnapshot = PlaylistUrlCompletionSourceSnapshot.Empty.Candidates;
                playlistUrlCompletionTsvSnapshotFetched = true;
                playlistUrlCompletionTsvSnapshotSourceUri = null;
            }
            return PlaylistUrlCompletionRefreshSourceResult.ReplaceWith(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
        if (!hasValidConfiguredSource || sourceUri == null)
        {
            return PlaylistUrlCompletionRefreshSourceResult.KeepCurrent(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
        lock (playlistUrlCompletionSnapshotLock)
        {
            if (playlistUrlCompletionTsvSnapshotFetched && UriEquals(playlistUrlCompletionTsvSnapshotSourceUri, sourceUri))
            {
                return PlaylistUrlCompletionRefreshSourceResult.FromCache(new PlaylistUrlCompletionSourceSnapshot(playlistUrlCompletionTsvSnapshot, 0, 0));
            }
        }
        try
        {
            string content = await FetchPlaylistUrlCompletionTsvContentAsync(sourceUri, CancellationToken.None).ConfigureAwait(false);
            PlaylistUrlCompletionSourceSnapshot snapshot = PlaylistUrlCompletionSupport.ParseMd5UrlMappingTsv(content);
            lock (playlistUrlCompletionSnapshotLock)
            {
                playlistUrlCompletionTsvSnapshot = snapshot.Candidates;
                playlistUrlCompletionTsvSnapshotFetched = true;
                playlistUrlCompletionTsvSnapshotSourceUri = sourceUri;
            }
            return PlaylistUrlCompletionRefreshSourceResult.ReplaceWith(snapshot);
        }
        catch (TaskCanceledException ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "playlist_url_completion_tsv_timeout source=" + sourceUri + " timeoutMs=" + PlaylistUrlCompletionTimeoutMs);
            return PlaylistUrlCompletionRefreshSourceResult.KeepCurrent(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
        catch (Exception ex2)
        {
            NLogWrapper.FileLogger?.Warn(ex2, "playlist_url_completion_tsv_failed source=" + sourceUri);
            return PlaylistUrlCompletionRefreshSourceResult.KeepCurrent(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
    }

    private async Task<PlaylistUrlCompletionRefreshSourceResult> FetchPlaylistUrlCompletionStellaSnapshotAsync()
    {
        if (!Settings.Default.EnableStellaFullPlaylistUrlCompletion)
        {
            return PlaylistUrlCompletionRefreshSourceResult.KeepCurrent(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
        var stellaUri = new Uri(PlaylistUrlCompletionSupport.StellaScoreUploadFullJsonUri, UriKind.Absolute);
        lock (playlistUrlCompletionSnapshotLock)
        {
            if (playlistUrlCompletionStellaSnapshotFetched)
            {
                return PlaylistUrlCompletionRefreshSourceResult.FromCache(new PlaylistUrlCompletionSourceSnapshot(playlistUrlCompletionStellaSnapshot, 0, 0));
            }
        }
        try
        {
            string content = await FetchPlaylistUrlCompletionStellaContentAsync(stellaUri, CancellationToken.None).ConfigureAwait(false);
            PlaylistUrlCompletionSourceSnapshot snapshot = PlaylistUrlCompletionSupport.ParseStellaUploadFullJson(content);
            lock (playlistUrlCompletionSnapshotLock)
            {
                playlistUrlCompletionStellaSnapshot = snapshot.Candidates;
                playlistUrlCompletionStellaSnapshotFetched = true;
            }
            return PlaylistUrlCompletionRefreshSourceResult.ReplaceWith(snapshot);
        }
        catch (TaskCanceledException ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "playlist_url_completion_stella_timeout source=" + stellaUri + " timeoutMs=" + PlaylistUrlCompletionTimeoutMs);
            return PlaylistUrlCompletionRefreshSourceResult.KeepCurrent(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
        catch (Exception ex2)
        {
            NLogWrapper.FileLogger?.Warn(ex2, "playlist_url_completion_stella_failed source=" + stellaUri);
            return PlaylistUrlCompletionRefreshSourceResult.KeepCurrent(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
    }

    private static Task<string> FetchPlaylistUrlCompletionTsvContentAsync(Uri sourceUri, CancellationToken cancellationToken)
    {
        Func<Uri, CancellationToken, Task<string>> testFetcher = PlaylistUrlCompletionTsvContentFetcherForTests;
        if (testFetcher != null)
        {
            return testFetcher(sourceUri, cancellationToken);
        }
        return playlistUrlCompletionTsvHttpClient.GetStringAsync(sourceUri, null, cancellationToken);
    }

    private static Task<string> FetchPlaylistUrlCompletionStellaContentAsync(Uri sourceUri, CancellationToken cancellationToken)
    {
        Func<Uri, CancellationToken, Task<string>> testFetcher = PlaylistUrlCompletionStellaContentFetcherForTests;
        if (testFetcher != null)
        {
            return testFetcher(sourceUri, cancellationToken);
        }
        return playlistUrlCompletionStellaHttpClient.GetStringAsync(sourceUri, null, cancellationToken);
    }

    internal async Task RefreshPlaylistUrlCompletionForTestsAsync(string reason)
    {
        await playlistUrlCompletionRefreshSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            long targetVersion = Interlocked.Increment(ref playlistUrlCompletionRequestedVersion);
            await RefreshPlaylistUrlCompletionCoreAsync(targetVersion, reason ?? "test").ConfigureAwait(false);
            Interlocked.Exchange(ref playlistUrlCompletionCompletedVersion, targetVersion);
        }
        finally
        {
            playlistUrlCompletionRefreshSemaphore.Release();
        }
    }

    private PlaylistUrlCompletionApplyStats ApplyPlaylistUrlCompletionToLoadedTablesCore()
    {
        List<BMSTable> tableSnapshot;
        using (rwlockBMSTables.GetReaderGuard())
        {
            tableSnapshot = [.. BMSTables];
        }
        PlaylistUrlCompletionApplyStats aggregateStats = default;
        foreach (BMSTable table in tableSnapshot)
        {
            aggregateStats.Add(ApplyPlaylistUrlCompletionToTableCore(table));
        }
        return aggregateStats;
    }

    private PlaylistUrlCompletionApplyStats ClearPlaylistUrlCompletionFromLoadedTables()
    {
        List<BMSTable> tableSnapshot;
        using (rwlockBMSTables.GetReaderGuard())
        {
            tableSnapshot = [.. BMSTables];
        }
        PlaylistUrlCompletionApplyStats aggregateStats = default;
        foreach (BMSTable table in tableSnapshot)
        {
            aggregateStats.Add(ClearPlaylistUrlCompletionFromTable(table));
        }
        return aggregateStats;
    }

    private PlaylistUrlCompletionApplyStats ApplyPlaylistUrlCompletionToTableCore(BMSTable table)
    {
        if (table == null)
        {
            return default;
        }
        if (!table.ArePlaylistEntriesLoaded)
        {
            NLogWrapper.FileLogger?.Info("playlist_url_completion skip_unloaded_table table=\"" + (table.name ?? string.Empty).Replace("\"", "\"\"") + "\"");
            return new PlaylistUrlCompletionApplyStats
            {
                TableCount = 1
            };
        }
        if (!Settings.Default.EnablePlaylistUrlCompletion)
        {
            return ClearPlaylistUrlCompletionFromTable(table);
        }
        IReadOnlyDictionary<string, PlaylistUrlCompletionCandidate> tsvSnapshot;
        IReadOnlyDictionary<string, PlaylistUrlCompletionCandidate> stellaSnapshot;
        lock (playlistUrlCompletionSnapshotLock)
        {
            tsvSnapshot = playlistUrlCompletionTsvSnapshot;
            stellaSnapshot = Settings.Default.EnableStellaFullPlaylistUrlCompletion ? playlistUrlCompletionStellaSnapshot : PlaylistUrlCompletionSourceSnapshot.Empty.Candidates;
        }
        var tableStats = new PlaylistUrlCompletionApplyStats
        {
            TableCount = 1
        };
        bool overwriteExisting = Settings.Default.OverwritePlaylistUrlsWithCompletion;
        using (table.ReaderWriterLock.GetWriterGuard())
        {
            foreach (BMSTableEntry entry in table.entries ?? Enumerable.Empty<BMSTableEntry>())
            {
                if (entry == null || entry.is_removed || string.IsNullOrWhiteSpace(entry.md5) || string.Equals(entry.md5, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                tableStats.EntryCount++;
                if (!tsvSnapshot.TryGetValue(entry.md5, out PlaylistUrlCompletionCandidate candidate))
                {
                    stellaSnapshot.TryGetValue(entry.md5, out candidate);
                }
                // NOTE:
                // Stella 補完は TSV に見つからなかった MD5 だけに限定する要件です。
                // そのため URL2 だけ空でも、TSV 側に MD5 がある限り Stella で補うことはしません。
                Uri completedUrl = candidate?.Url;
                Uri completedUrlDiff = candidate?.UrlDiff;
                if (entry.ApplyRuntimeUrlCompletion(completedUrl, completedUrlDiff, overwriteExisting))
                {
                    tableStats.ChangedEntryCount++;
                }
                if (entry.RuntimeUrlCompletion != null)
                {
                    tableStats.RuntimeUrlCount++;
                }
                if (entry.RuntimeUrlDiffCompletion != null)
                {
                    tableStats.RuntimeUrlDiffCount++;
                }
            }
        }
        return tableStats;
    }

    private static PlaylistUrlCompletionApplyStats ClearPlaylistUrlCompletionFromTable(BMSTable table)
    {
        if (table == null)
        {
            return default;
        }
        var tableStats = new PlaylistUrlCompletionApplyStats
        {
            TableCount = 1
        };
        if (!table.ArePlaylistEntriesLoaded)
        {
            NLogWrapper.FileLogger?.Info("playlist_url_completion clear_skip_unloaded_table table=\"" + (table.name ?? string.Empty).Replace("\"", "\"\"") + "\"");
            return tableStats;
        }
        using (table.ReaderWriterLock.GetWriterGuard())
        {
            foreach (BMSTableEntry entry in table.entries ?? Enumerable.Empty<BMSTableEntry>())
            {
                if (entry == null || entry.is_removed || string.IsNullOrWhiteSpace(entry.md5) || string.Equals(entry.md5, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                tableStats.EntryCount++;
                if (entry.ClearRuntimeUrlCompletions())
                {
                    tableStats.ChangedEntryCount++;
                }
            }
        }
        return tableStats;
    }

    private BMSTable ResolveOwningTableForEntry(BMSTableEntry entry)
    {
        if (entry?.parent != null)
        {
            return entry.parent;
        }
        if (entry?.playlist_id == null)
        {
            return null;
        }
        using (rwlockBMSTables.GetReaderGuard())
        {
            return BMSTables.FirstOrDefault(table => table != null && table.playlist_id == entry.playlist_id);
        }
    }

    private sealed class PlaylistUrlCompletionRefreshSourceResult
    {
        public bool ShouldReplaceSnapshot { get; private set; }

        public bool UsedCachedSnapshot { get; private set; }

        public PlaylistUrlCompletionSourceSnapshot Snapshot { get; private set; }

        public static PlaylistUrlCompletionRefreshSourceResult ReplaceWith(PlaylistUrlCompletionSourceSnapshot snapshot)
        {
            return new PlaylistUrlCompletionRefreshSourceResult
            {
                ShouldReplaceSnapshot = true,
                UsedCachedSnapshot = false,
                Snapshot = snapshot ?? PlaylistUrlCompletionSourceSnapshot.Empty
            };
        }

        public static PlaylistUrlCompletionRefreshSourceResult KeepCurrent(PlaylistUrlCompletionSourceSnapshot snapshot)
        {
            return new PlaylistUrlCompletionRefreshSourceResult
            {
                ShouldReplaceSnapshot = false,
                UsedCachedSnapshot = false,
                Snapshot = snapshot ?? PlaylistUrlCompletionSourceSnapshot.Empty
            };
        }

        public static PlaylistUrlCompletionRefreshSourceResult FromCache(PlaylistUrlCompletionSourceSnapshot snapshot)
        {
            return new PlaylistUrlCompletionRefreshSourceResult
            {
                ShouldReplaceSnapshot = false,
                UsedCachedSnapshot = true,
                Snapshot = snapshot ?? PlaylistUrlCompletionSourceSnapshot.Empty
            };
        }
    }

    private static bool UriEquals(Uri left, Uri right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null)
        {
            return false;
        }
        return Uri.Compare(left, right, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private struct PlaylistUrlCompletionApplyStats
    {
        public int TableCount;

        public int EntryCount;

        public int ChangedEntryCount;

        public int RuntimeUrlCount;

        public int RuntimeUrlDiffCount;

        public void Add(PlaylistUrlCompletionApplyStats other)
        {
            TableCount += other.TableCount;
            EntryCount += other.EntryCount;
            ChangedEntryCount += other.ChangedEntryCount;
            RuntimeUrlCount += other.RuntimeUrlCount;
            RuntimeUrlDiffCount += other.RuntimeUrlDiffCount;
        }
    }
}
