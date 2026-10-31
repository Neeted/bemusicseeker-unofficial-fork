using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>取得済み全履歴の期間・確定状態・日索引を純投影します。raw配列やDB接続、取得元選択を所有せず、最新取得組の日索引だけを再利用します。</summary>
internal sealed class PlayHistoryReadCache
{
    private readonly object periodIndexGate = new();
    private PeriodIndexProjection periodIndex;

    /// <summary>新取得組の公開・画面退役時に、旧取得組から派生した日索引を解放します。</summary>
    internal void ClearPeriodIndex()
    {
        lock (periodIndexGate) { periodIndex = null; }
    }

    /// <summary>最新一組の純派生索引です。rawの複製や取得世代の台帳は持ちません。</summary>
    private sealed record PeriodIndexProjection(WeakReference<ScorePlayHistorySnapshot> Source, Lr2PlayHistoryPeriodIndexResult Lr2 = null, BeatorajaPlayHistoryPeriodIndexResult Beatoraja = null)
    {
        /// <summary>キーのために旧rawを生存させず、現在の取得組とだけ参照照合します。</summary>
        internal bool IsSameSource(ScorePlayHistorySnapshot snapshot)
            => snapshot != null && Source.TryGetTarget(out ScorePlayHistorySnapshot captured) && ReferenceEquals(captured, snapshot);
    }

    /// <summary>同じ取得組のLR2全行に通常一覧のスキーマ条件と期間条件を適用します。</summary>
    internal Lr2PlayHistoryReadResult ReadLr2(ScorePlayHistorySnapshot snapshot, Lr2PlayHistoryReadRequest request, CancellationToken cancellationToken, out bool cacheHit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cacheHit = true;
        Lr2PlayHistoryReadResult raw = snapshot?.Lr2;
        return FilterLr2(raw == null ? null : Lr2PlayHistoryReader.ForPurpose(raw, false, false), request);
    }

    /// <summary>取得時点のノーツ・player時系列を保持したbeatoraja全行を期間投影します。</summary>
    internal BeatorajaPlayHistoryReadResult ReadBeatoraja(ScorePlayHistorySnapshot snapshot, BeatorajaPlayHistoryReadRequest request, CancellationToken cancellationToken, out bool cacheHit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        cacheHit = true;
        return FilterBeatoraja(snapshot?.Beatoraja, request);
    }

    /// <summary>取得済みの単曲確定行から日索引を作ります。</summary>
    internal Lr2PlayHistoryPeriodIndexResult ReadLr2PeriodIndex(ScorePlayHistorySnapshot snapshot, Lr2PlayHistoryPeriodIndexRequest request, CancellationToken cancellationToken, out bool cacheHit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (periodIndexGate)
        {
            cacheHit = true;
            if (periodIndex?.IsSameSource(snapshot) == true && periodIndex.Lr2 != null)
            {
                return periodIndex.Lr2;
            }
            Lr2PlayHistoryReadResult read = ReadLr2(snapshot, new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = request?.ScoreDbPath,
                IsLr2LinkedProfile = request?.IsLr2LinkedProfile ?? true
            }, cancellationToken, out _);
            Lr2PlayHistoryPeriodIndexResult result;
            try
            {
                result = new(read.SourceProfile, BuildPeriodIndex(read.Rows.Select(row => row.played_at)), read.Diagnostics, read.SchemaStatus);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                result = new(read.SourceProfile, [], [CreatePeriodIndexFailure(read.SourceProfile, failure)], Lr2PlayHistorySchemaStatus.Unreadable);
            }
            periodIndex = new(new(snapshot), Lr2: result);
            return result;
        }
    }

    /// <summary>同じ取得組のbeatoraja単曲行から日索引を作ります。</summary>
    internal BeatorajaPlayHistoryPeriodIndexResult ReadBeatorajaPeriodIndex(ScorePlayHistorySnapshot snapshot, BeatorajaPlayHistoryPeriodIndexRequest request, CancellationToken cancellationToken, out bool cacheHit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (periodIndexGate)
        {
            cacheHit = true;
            if (periodIndex?.IsSameSource(snapshot) == true && periodIndex.Beatoraja != null)
            {
                return periodIndex.Beatoraja;
            }
            BeatorajaPlayHistoryReadResult read = ReadBeatoraja(snapshot, new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = request?.ScoreDbPath,
                ScoreLogDbPath = request?.ScoreLogDbPath
            }, cancellationToken, out _);
            BeatorajaPlayHistoryPeriodIndexResult result = BuildBeatorajaPeriodIndex(read);
            periodIndex = new(new(snapshot), Beatoraja: result);
            return result;
        }
    }

    private static Lr2PlayHistoryReadResult FilterLr2(Lr2PlayHistoryReadResult source, Lr2PlayHistoryReadRequest request)
    {
        IEnumerable<Lr2PlayHistoryRecord> rows = source?.Rows ?? [];
        rows = rows.Where(row => MatchesFinalization(row.finalized != 0, request.FinalizationFilter)
            && MatchesPeriod(row.played_at, request.PlayedAtFromInclusive, request.PlayedAtToExclusive));
        return new Lr2PlayHistoryReadResult(
            source?.SourceProfile ?? PlayHistorySourceProfile.Lr2(request.ScoreDbPath),
            IsUnfiltered(request.FinalizationFilter, request.PlayedAtFromInclusive, request.PlayedAtToExclusive) ? source?.Rows ?? [] : [.. rows],
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
        return new BeatorajaPlayHistoryReadResult(
            source?.SourceProfile ?? PlayHistorySourceProfile.Beatoraja(request?.ScoreLogDbPath),
            IsUnfiltered(request.FinalizationFilter, request.PlayedAtFromInclusive, request.PlayedAtToExclusive) ? source?.Rows ?? [] : [.. rows],
            source?.Diagnostics ?? [],
            source?.SchemaStatus ?? Lr2PlayHistorySchemaStatus.Unreadable,
            source?.PlayerSnapshots ?? [],
            source?.PlayerSnapshotsAvailable ?? false);
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

    private static BeatorajaPlayHistoryPeriodIndexResult BuildBeatorajaPeriodIndex(BeatorajaPlayHistoryReadResult read)
    {
        try
        {
            return new BeatorajaPlayHistoryPeriodIndexResult(read.SourceProfile,
                BuildPeriodIndex(read.Rows.Select(row => row.played_at)), read.Diagnostics, read.SchemaStatus);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return new BeatorajaPlayHistoryPeriodIndexResult(read.SourceProfile, [],
                [CreatePeriodIndexFailure(read.SourceProfile, failure)], Lr2PlayHistorySchemaStatus.Unreadable);
        }
    }

    private static PlayHistoryDiagnostic CreatePeriodIndexFailure(PlayHistorySourceProfile source, Exception failure)
    {
        PlayHistoryReadDiagnostics.Report(failure, source.Provider.ToString(), "period_index", source.SourcePath);
        return new PlayHistoryDiagnostic
        {
            Provider = source.Provider,
            Stage = "period_index",
            Severity = PlayHistoryDiagnosticSeverity.Error,
            Code = "play_history_period_index_failed",
            Message = failure.Message,
            SourcePath = source.SourcePath
        };
    }

    /// <summary>期間・確定状態の絞込みがない要求はrawリストを共有します。</summary>
    private static bool IsUnfiltered(Lr2PlayHistoryFinalizationFilter finalization, long? from, long? to)
        => finalization == Lr2PlayHistoryFinalizationFilter.All && !from.HasValue && !to.HasValue;
}
