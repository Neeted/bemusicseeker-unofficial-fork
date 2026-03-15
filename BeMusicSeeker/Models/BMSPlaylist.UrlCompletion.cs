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

    private readonly object playlistUrlCompletionSnapshotLock = new object();

    private readonly SemaphoreSlim playlistUrlCompletionRefreshSemaphore = new SemaphoreSlim(1, 1);

    private IReadOnlyDictionary<string, PlaylistUrlCompletionCandidate> playlistUrlCompletionTsvSnapshot = new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, PlaylistUrlCompletionCandidate> playlistUrlCompletionStellaSnapshot = new Dictionary<string, PlaylistUrlCompletionCandidate>(StringComparer.OrdinalIgnoreCase);

    private long playlistUrlCompletionRequestedVersion;

    private long playlistUrlCompletionCompletedVersion;

    private string playlistUrlCompletionLatestReason = "initial";

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
        _ = Task.Run(async delegate
        {
            try
            {
                await RefreshPlaylistUrlCompletionLoopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                NLogWrapper.FileLogger?.Warn(ex, "playlist_url_completion_refresh_failed reason=" + normalizedReason);
            }
        });
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
        lock (playlistUrlCompletionSnapshotLock)
        {
            if (tsvResult.ShouldReplaceSnapshot)
            {
                playlistUrlCompletionTsvSnapshot = tsvResult.Snapshot.Candidates;
            }
            if (stellaResult.ShouldReplaceSnapshot)
            {
                playlistUrlCompletionStellaSnapshot = stellaResult.Snapshot.Candidates;
            }
        }
        PlaylistUrlCompletionApplyStats applyStats = ApplyPlaylistUrlCompletionToLoadedTablesCore();
        NLogWrapper.FileLogger?.Info("playlist_url_completion finish version=" + version + " reason=" + reason + " tsvCandidates=" + tsvResult.Snapshot.CandidateCount + " tsvDuplicateSkipped=" + tsvResult.Snapshot.DuplicateCount + " tsvIgnored=" + tsvResult.Snapshot.IgnoredRowCount + " tsvSnapshotReplaced=" + tsvResult.ShouldReplaceSnapshot.ToString().ToLowerInvariant() + " stellaCandidates=" + stellaResult.Snapshot.CandidateCount + " stellaDuplicateSkipped=" + stellaResult.Snapshot.DuplicateCount + " stellaIgnored=" + stellaResult.Snapshot.IgnoredRowCount + " stellaSnapshotReplaced=" + stellaResult.ShouldReplaceSnapshot.ToString().ToLowerInvariant() + " tables=" + applyStats.TableCount + " entries=" + applyStats.EntryCount + " changed=" + applyStats.ChangedEntryCount + " runtimeUrl=" + applyStats.RuntimeUrlCount + " runtimeUrlDiff=" + applyStats.RuntimeUrlDiffCount);
    }

    private async Task<PlaylistUrlCompletionRefreshSourceResult> FetchPlaylistUrlCompletionTsvSnapshotAsync(Uri sourceUri, bool hasConfiguredSource, bool hasValidConfiguredSource)
    {
        if (!hasConfiguredSource)
        {
            return PlaylistUrlCompletionRefreshSourceResult.ReplaceWith(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
        if (!hasValidConfiguredSource || sourceUri == null)
        {
            return PlaylistUrlCompletionRefreshSourceResult.KeepCurrent(PlaylistUrlCompletionSourceSnapshot.Empty);
        }
        try
        {
            string content = await playlistUrlCompletionTsvHttpClient.GetStringAsync(sourceUri, null, CancellationToken.None).ConfigureAwait(false);
            return PlaylistUrlCompletionRefreshSourceResult.ReplaceWith(PlaylistUrlCompletionSupport.ParseMd5UrlMappingTsv(content));
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
        Uri stellaUri = new Uri(PlaylistUrlCompletionSupport.StellaScoreUploadJsonUri, UriKind.Absolute);
        try
        {
            string content = await playlistUrlCompletionStellaHttpClient.GetStringAsync(stellaUri, null, CancellationToken.None).ConfigureAwait(false);
            return PlaylistUrlCompletionRefreshSourceResult.ReplaceWith(PlaylistUrlCompletionSupport.ParseStellaUploadJson(content));
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

    private PlaylistUrlCompletionApplyStats ApplyPlaylistUrlCompletionToLoadedTablesCore()
    {
        List<BMSTable> tableSnapshot;
        using (rwlockBMSTables.GetReaderGuard())
        {
            tableSnapshot = BMSTables.ToList();
        }
        PlaylistUrlCompletionApplyStats aggregateStats = default(PlaylistUrlCompletionApplyStats);
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
            tableSnapshot = BMSTables.ToList();
        }
        PlaylistUrlCompletionApplyStats aggregateStats = default(PlaylistUrlCompletionApplyStats);
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
            return default(PlaylistUrlCompletionApplyStats);
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
            stellaSnapshot = playlistUrlCompletionStellaSnapshot;
        }
        PlaylistUrlCompletionApplyStats tableStats = new PlaylistUrlCompletionApplyStats
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
                PlaylistUrlCompletionCandidate candidate = null;
                if (!tsvSnapshot.TryGetValue(entry.md5, out candidate))
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
            return default(PlaylistUrlCompletionApplyStats);
        }
        PlaylistUrlCompletionApplyStats tableStats = new PlaylistUrlCompletionApplyStats
        {
            TableCount = 1
        };
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
            return BMSTables.FirstOrDefault((BMSTable table) => table != null && table.playlist_id == entry.playlist_id);
        }
    }

    private sealed class PlaylistUrlCompletionRefreshSourceResult
    {
        public bool ShouldReplaceSnapshot { get; private set; }

        public PlaylistUrlCompletionSourceSnapshot Snapshot { get; private set; }

        public static PlaylistUrlCompletionRefreshSourceResult ReplaceWith(PlaylistUrlCompletionSourceSnapshot snapshot)
        {
            return new PlaylistUrlCompletionRefreshSourceResult
            {
                ShouldReplaceSnapshot = true,
                Snapshot = snapshot ?? PlaylistUrlCompletionSourceSnapshot.Empty
            };
        }

        public static PlaylistUrlCompletionRefreshSourceResult KeepCurrent(PlaylistUrlCompletionSourceSnapshot snapshot)
        {
            return new PlaylistUrlCompletionRefreshSourceResult
            {
                ShouldReplaceSnapshot = false,
                Snapshot = snapshot ?? PlaylistUrlCompletionSourceSnapshot.Empty
            };
        }
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
