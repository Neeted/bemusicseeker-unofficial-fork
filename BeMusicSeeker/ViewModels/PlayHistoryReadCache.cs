using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryReadCache
{
    private readonly object syncRoot = new();

    private long generation;

    private Lr2CacheEntry lr2Entry;

    private Lr2CacheKey lr2LoadingKey;

    private long lr2LoadingGeneration;

    private BeatorajaCacheEntry beatorajaEntry;

    private BeatorajaCacheKey beatorajaLoadingKey;

    private long beatorajaLoadingGeneration;

    internal void Invalidate()
    {
        lock (syncRoot)
        {
            generation++;
            lr2Entry = null;
            lr2LoadingKey = null;
            lr2LoadingGeneration = 0L;
            beatorajaEntry = null;
            beatorajaLoadingKey = null;
            beatorajaLoadingGeneration = 0L;
            Monitor.PulseAll(syncRoot);
        }
    }

    internal Lr2PlayHistoryReadResult ReadLr2(
        Lr2PlayHistoryReadRequest request,
        CancellationToken cancellationToken,
        out bool cacheHit)
    {
        request ??= new Lr2PlayHistoryReadRequest();
        var key = new Lr2CacheKey(request.ScoreDbPath, request.IsLr2LinkedProfile);
        long loadGeneration;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (syncRoot)
            {
                if (lr2Entry != null && lr2Entry.Key.IsSameSource(key))
                {
                    cacheHit = true;
                    return FilterLr2(lr2Entry.Result, request);
                }
                if (lr2LoadingKey == null)
                {
                    lr2LoadingKey = key;
                    loadGeneration = generation;
                    lr2LoadingGeneration = loadGeneration;
                    break;
                }
                Monitor.Wait(syncRoot, TimeSpan.FromMilliseconds(100));
            }
        }

        cacheHit = false;
        var cacheRequest = new Lr2PlayHistoryReadRequest
        {
            ScoreDbPath = key.ScoreDbPath,
            IsLr2LinkedProfile = key.IsLr2LinkedProfile,
            FinalizationFilter = Lr2PlayHistoryFinalizationFilter.All,
            DisableLimit = true
        };
        try
        {
            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(cacheRequest, CancellationToken.None);
            if (ShouldCache(result.SchemaStatus))
            {
                lock (syncRoot)
                {
                    if (generation == loadGeneration)
                    {
                        lr2Entry = new Lr2CacheEntry(key, result);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return FilterLr2(result, request);
        }
        finally
        {
            lock (syncRoot)
            {
                if (lr2LoadingGeneration == loadGeneration
                    && lr2LoadingKey != null
                    && lr2LoadingKey.IsSameSource(key))
                {
                    lr2LoadingKey = null;
                    lr2LoadingGeneration = 0L;
                }
                Monitor.PulseAll(syncRoot);
            }
        }
    }

    internal BeatorajaPlayHistoryReadResult ReadBeatoraja(
        BeatorajaPlayHistoryReadRequest request,
        CancellationToken cancellationToken,
        out bool cacheHit)
    {
        BeatorajaPlayHistoryReadRequest resolved = BeatorajaPlayHistoryReader.ResolveRequestPaths(CopyBeatorajaRequest(request));
        var key = new BeatorajaCacheKey(resolved.ScoreDbPath, resolved.ScoreDataLogDbPath, resolved.ScoreLogDbPath);
        long loadGeneration;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (syncRoot)
            {
                if (beatorajaEntry != null && beatorajaEntry.Key.IsSameSource(key))
                {
                    cacheHit = true;
                    return FilterBeatoraja(beatorajaEntry.Result, resolved);
                }
                if (beatorajaLoadingKey == null)
                {
                    beatorajaLoadingKey = key;
                    loadGeneration = generation;
                    beatorajaLoadingGeneration = loadGeneration;
                    break;
                }
                Monitor.Wait(syncRoot, TimeSpan.FromMilliseconds(100));
            }
        }

        cacheHit = false;
        var cacheRequest = new BeatorajaPlayHistoryReadRequest
        {
            ScoreDbPath = key.ScoreDbPath,
            ScoreDataLogDbPath = key.ScoreDataLogDbPath,
            ScoreLogDbPath = key.ScoreLogDbPath,
            FinalizationFilter = Lr2PlayHistoryFinalizationFilter.All,
            DisableLimit = true
        };
        try
        {
            BeatorajaPlayHistoryReadResult result = new BeatorajaPlayHistoryReader().Read(cacheRequest, CancellationToken.None);
            if (ShouldCache(result.SchemaStatus))
            {
                lock (syncRoot)
                {
                    if (generation == loadGeneration)
                    {
                        beatorajaEntry = new BeatorajaCacheEntry(key, result);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return FilterBeatoraja(result, resolved);
        }
        finally
        {
            lock (syncRoot)
            {
                if (beatorajaLoadingGeneration == loadGeneration
                    && beatorajaLoadingKey != null
                    && beatorajaLoadingKey.IsSameSource(key))
                {
                    beatorajaLoadingKey = null;
                    beatorajaLoadingGeneration = 0L;
                }
                Monitor.PulseAll(syncRoot);
            }
        }
    }

    internal Lr2PlayHistoryPeriodIndexResult ReadLr2PeriodIndex(
        Lr2PlayHistoryPeriodIndexRequest request,
        CancellationToken cancellationToken,
        out bool cacheHit)
    {
        var read = ReadLr2(
            new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = request?.ScoreDbPath,
                IsLr2LinkedProfile = request?.IsLr2LinkedProfile ?? true,
                FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly,
                DisableLimit = true
            },
            cancellationToken,
            out cacheHit);
        return new Lr2PlayHistoryPeriodIndexResult(
            read.SourceProfile,
            BuildPeriodIndex(read.Rows.Select(row => row.played_at)),
            read.Diagnostics,
            read.SchemaStatus);
    }

    internal BeatorajaPlayHistoryPeriodIndexResult ReadBeatorajaPeriodIndex(
        BeatorajaPlayHistoryPeriodIndexRequest request,
        CancellationToken cancellationToken,
        out bool cacheHit)
    {
        var read = ReadBeatoraja(
            new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = request?.ScoreDbPath,
                ScoreDataLogDbPath = request?.ScoreDataLogDbPath,
                FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly,
                DisableLimit = true
            },
            cancellationToken,
            out cacheHit);
        return new BeatorajaPlayHistoryPeriodIndexResult(
            read.SourceProfile,
            BuildPeriodIndex(read.Rows.Select(row => row.played_at)),
            read.Diagnostics,
            read.SchemaStatus);
    }

    private static Lr2PlayHistoryReadResult FilterLr2(Lr2PlayHistoryReadResult source, Lr2PlayHistoryReadRequest request)
    {
        IEnumerable<Lr2PlayHistoryRecord> rows = source?.Rows ?? [];
        rows = rows.Where(row => MatchesFinalization(row.finalized != 0, request.FinalizationFilter)
            && MatchesPeriod(row.played_at, request.PlayedAtFromInclusive, request.PlayedAtToExclusive));
        int? limit = ResolveLimit(request.Limit, request.DisableLimit, Lr2PlayHistoryReader.DefaultReadLimit);
        if (limit.HasValue)
        {
            rows = rows.Take(limit.Value);
        }
        return new Lr2PlayHistoryReadResult(
            source?.SourceProfile ?? PlayHistorySourceProfile.Lr2(request.ScoreDbPath),
            [.. rows],
            source?.Diagnostics ?? [],
            source?.SchemaStatus ?? Lr2PlayHistorySchemaStatus.Unreadable,
            source?.SchemaCheckResult);
    }

    private static BeatorajaPlayHistoryReadResult FilterBeatoraja(BeatorajaPlayHistoryReadResult source, BeatorajaPlayHistoryReadRequest request)
    {
        IEnumerable<BeatorajaPlayHistoryRecord> rows = source?.Rows ?? [];
        if (request.FinalizationFilter == Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly)
        {
            rows = [];
        }
        rows = rows.Where(row => MatchesPeriod(row.played_at, request.PlayedAtFromInclusive, request.PlayedAtToExclusive));
        int? limit = ResolveLimit(request.Limit, request.DisableLimit, BeatorajaPlayHistoryReader.DefaultReadLimit);
        if (limit.HasValue)
        {
            rows = rows.Take(limit.Value);
        }
        return new BeatorajaPlayHistoryReadResult(
            source?.SourceProfile ?? PlayHistorySourceProfile.Beatoraja(request?.ScoreDataLogDbPath),
            [.. rows],
            source?.Diagnostics ?? [],
            source?.SchemaStatus ?? Lr2PlayHistorySchemaStatus.Unreadable);
    }

    private static bool MatchesFinalization(bool finalized, Lr2PlayHistoryFinalizationFilter filter)
    {
        return filter switch
        {
            Lr2PlayHistoryFinalizationFilter.FinalizedOnly => finalized,
            Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly => !finalized,
            _ => true
        };
    }

    private static bool MatchesPeriod(long playedAt, long? fromInclusive, long? toExclusive)
    {
        return (!fromInclusive.HasValue || playedAt >= fromInclusive.Value)
            && (!toExclusive.HasValue || playedAt < toExclusive.Value);
    }

    private static int? ResolveLimit(int? requestedLimit, bool disableLimit, int defaultLimit)
    {
        if (disableLimit)
        {
            return null;
        }
        return requestedLimit.HasValue && requestedLimit.Value > 0
            ? requestedLimit.Value
            : defaultLimit;
    }

    private static IReadOnlyList<long> BuildPeriodIndex(IEnumerable<long> playedAtUnixSeconds)
    {
        var latestByLocalDate = new Dictionary<DateTime, long>();
        foreach (long playedAt in playedAtUnixSeconds ?? [])
        {
            DateTime localDate = DateTimeOffset.FromUnixTimeSeconds(playedAt).LocalDateTime.Date;
            if (!latestByLocalDate.TryGetValue(localDate, out long current) || playedAt > current)
            {
                latestByLocalDate[localDate] = playedAt;
            }
        }
        return [.. latestByLocalDate.Values.OrderByDescending(playedAt => playedAt)];
    }

    private static bool ShouldCache(Lr2PlayHistorySchemaStatus status)
    {
        return status != Lr2PlayHistorySchemaStatus.Unreadable;
    }

    private static BeatorajaPlayHistoryReadRequest CopyBeatorajaRequest(BeatorajaPlayHistoryReadRequest request)
    {
        request ??= new BeatorajaPlayHistoryReadRequest();
        return new BeatorajaPlayHistoryReadRequest
        {
            ScoreDbPath = request.ScoreDbPath,
            ScoreDataLogDbPath = request.ScoreDataLogDbPath,
            ScoreLogDbPath = request.ScoreLogDbPath,
            PlayedAtFromInclusive = request.PlayedAtFromInclusive,
            PlayedAtToExclusive = request.PlayedAtToExclusive,
            Limit = request.Limit,
            DisableLimit = request.DisableLimit,
            FinalizationFilter = request.FinalizationFilter
        };
    }

    private sealed class Lr2CacheKey
    {
        internal Lr2CacheKey(string scoreDbPath, bool isLr2LinkedProfile)
        {
            ScoreDbPath = scoreDbPath ?? string.Empty;
            IsLr2LinkedProfile = isLr2LinkedProfile;
        }

        internal string ScoreDbPath { get; }

        internal bool IsLr2LinkedProfile { get; }

        internal bool IsSameSource(Lr2CacheKey other)
        {
            return other != null
                && IsLr2LinkedProfile == other.IsLr2LinkedProfile
                && string.Equals(ScoreDbPath, other.ScoreDbPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Lr2CacheEntry
    {
        internal Lr2CacheEntry(Lr2CacheKey key, Lr2PlayHistoryReadResult result)
        {
            Key = key;
            Result = result;
        }

        internal Lr2CacheKey Key { get; }

        internal Lr2PlayHistoryReadResult Result { get; }
    }

    private sealed class BeatorajaCacheKey
    {
        internal BeatorajaCacheKey(
            string scoreDbPath,
            string scoreDataLogDbPath,
            string scoreLogDbPath)
        {
            ScoreDbPath = scoreDbPath ?? string.Empty;
            ScoreDataLogDbPath = scoreDataLogDbPath ?? string.Empty;
            ScoreLogDbPath = scoreLogDbPath ?? string.Empty;
        }

        internal string ScoreDbPath { get; }

        internal string ScoreDataLogDbPath { get; }

        internal string ScoreLogDbPath { get; }

        internal bool IsSameSource(BeatorajaCacheKey other)
        {
            return other != null
                && string.Equals(ScoreDbPath, other.ScoreDbPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ScoreDataLogDbPath, other.ScoreDataLogDbPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ScoreLogDbPath, other.ScoreLogDbPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class BeatorajaCacheEntry
    {
        internal BeatorajaCacheEntry(BeatorajaCacheKey key, BeatorajaPlayHistoryReadResult result)
        {
            Key = key;
            Result = result;
        }

        internal BeatorajaCacheKey Key { get; }

        internal BeatorajaPlayHistoryReadResult Result { get; }
    }
}
