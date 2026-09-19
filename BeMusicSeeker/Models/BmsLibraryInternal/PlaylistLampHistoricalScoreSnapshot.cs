using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// provider-specific history row reduced to the fields required by the lamp rollback.
/// </summary>
internal sealed class PlaylistLampHistoricalScoreChange
{
    /// <summary>Creates one immutable score change.</summary>
    internal PlaylistLampHistoricalScoreChange(
        ActiveScoreSource source,
        string chartKey,
        long sourceId,
        long playedAtUnixSeconds,
        long? oldPlayCount,
        int? oldClear,
        int? oldOperationHistory,
        int? oldExScore,
        int? oldTotalNotes)
    {
        Source = source;
        ChartKey = chartKey?.Trim() ?? string.Empty;
        SourceId = sourceId;
        PlayedAtUnixSeconds = playedAtUnixSeconds;
        OldPlayCount = oldPlayCount;
        OldClear = oldClear;
        OldOperationHistory = oldOperationHistory;
        OldExScore = oldExScore;
        OldTotalNotes = oldTotalNotes;
    }

    /// <summary>provider that produced the row.</summary>
    internal ActiveScoreSource Source { get; }

    /// <summary>provider chart identity (MD5 or SHA256).</summary>
    internal string ChartKey { get; }

    /// <summary>provider source id used for same-timestamp ordering.</summary>
    internal long SourceId { get; }

    /// <summary>UTC Unix timestamp in seconds.</summary>
    internal long PlayedAtUnixSeconds { get; }

    /// <summary>
    /// LR2 score playcount immediately before this history row. A null value is the
    /// provider's explicit first-play/no-previous-score sentinel.
    /// </summary>
    internal long? OldPlayCount { get; }

    /// <summary>stored old clear value.</summary>
    internal int? OldClear { get; }

    /// <summary>stored LR2 option-history value.</summary>
    internal int? OldOperationHistory { get; }

    /// <summary>stored old EX score.</summary>
    internal int? OldExScore { get; }

    /// <summary>stored old total notes.</summary>
    internal int? OldTotalNotes { get; }
}

/// <summary>
/// Reads the active score provider's history and produces the immutable score snapshot used by
/// the playlist lamp aggregation. This owner never opens the inactive provider and never writes
/// or repairs a database.
/// </summary>
internal sealed class PlaylistLampHistoricalScoreSnapshotReader
{
    /// <summary>
    /// Reads the active provider history with no display-row limit and applies the requested date.
    /// </summary>
    /// <param name="sourceContext">Active provider and already-resolved source paths.</param>
    /// <param name="currentScoreSnapshot">Current immutable score snapshot.</param>
    /// <param name="selectedLocalDate">Selected local date, or null for Latest.</param>
    /// <param name="cancellationToken">Cancellation token for the read.</param>
    /// <returns>Immutable historical projection result.</returns>
    internal PlaylistLampHistoricalScoreSnapshotResult Read(
        PlaylistLampHistoricalScoreSourceContext sourceContext,
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        DateTime? selectedLocalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentScoreSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);

        if (sourceContext == null)
        {
            return CreateUnavailable(currentScoreSnapshot, selectedLocalDate, today, "Historical score source is not configured.");
        }
        if (sourceContext.ActiveScoreSource != currentScoreSnapshot.Source)
        {
            return CreateUnavailable(currentScoreSnapshot, selectedLocalDate, today, "Historical score source changed while reading.");
        }
        if (!currentScoreSnapshot.IsScoreDataAvailable)
        {
            return CreateUnavailable(currentScoreSnapshot, selectedLocalDate, today, currentScoreSnapshot.FailureMessage);
        }

        ReadChangesResult readResult = ReadChanges(sourceContext, currentScoreSnapshot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!readResult.IsAvailable)
        {
            return CreateUnavailable(
                currentScoreSnapshot,
                selectedLocalDate,
                today,
                readResult.FailureMessage);
        }

        return PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            currentScoreSnapshot,
            sourceContext.ActiveScoreSource,
            selectedLocalDate,
            readResult.Changes,
            today);
    }

    private static ReadChangesResult ReadChanges(
        PlaylistLampHistoricalScoreSourceContext sourceContext,
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceContext.ScoreDbPath)
            || !File.Exists(sourceContext.ScoreDbPath))
        {
            return ReadChangesResult.Unavailable("Historical score database does not exist.");
        }

        try
        {
            if (sourceContext.ActiveScoreSource == ActiveScoreSource.Lr2)
            {
                Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(
                    new Lr2PlayHistoryReadRequest
                    {
                        ScoreDbPath = sourceContext.ScoreDbPath,
                        IsLr2LinkedProfile = sourceContext.IsLr2LinkedProfile,
                        FinalizationFilter = Lr2PlayHistoryFinalizationFilter.All,
                        DisableLimit = true,
                        AllowRepairableIndexRead = true,
                        RequireCompleteHistoryTriggers = true
                    },
                    cancellationToken);
                if (result.HasErrors)
                {
                    return ReadChangesResult.Unavailable(DescribeDiagnostics(result.Diagnostics));
                }
                var changes = new List<PlaylistLampHistoricalScoreChange>(result.Rows.Count);
                foreach (Lr2PlayHistoryRecord row in result.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    changes.Add(new PlaylistLampHistoricalScoreChange(
                        ActiveScoreSource.Lr2,
                        row?.hash,
                        row?.history_id ?? 0L,
                        row?.played_at ?? 0L,
                        row?.old_playcount,
                        row?.old_clear,
                        row?.old_op_history,
                        row?.old_exscore,
                        row?.old_totalnotes));
                }
                return ReadChangesResult.Available(changes);
            }

            if (sourceContext.ActiveScoreSource == ActiveScoreSource.Beatoraja)
            {
                string scoreLogPath = Path.Combine(
                    Path.GetDirectoryName(sourceContext.ScoreDbPath) ?? string.Empty,
                    "scorelog.db");
                if (!File.Exists(scoreLogPath))
                {
                    return ReadChangesResult.Unavailable("beatoraja scorelog.db does not exist.");
                }
                BeatorajaPlayHistoryReadResult result = new BeatorajaPlayHistoryReader().Read(
                    new BeatorajaPlayHistoryReadRequest
                    {
                        ScoreDbPath = sourceContext.ScoreDbPath,
                        ScoreLogDbPath = scoreLogPath,
                        ScoresBySha256 = BuildBmsScoreMap(currentScoreSnapshot),
                        FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly,
                        DisableLimit = true
                    },
                    cancellationToken);
                if (result.HasErrors)
                {
                    return ReadChangesResult.Unavailable(DescribeDiagnostics(result.Diagnostics));
                }
                var changes = new List<PlaylistLampHistoricalScoreChange>(result.Rows.Count);
                foreach (BeatorajaPlayHistoryRecord row in result.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int? totalNotes = ResolveNotes(sourceContext, row?.sha256, currentScoreSnapshot);
                    changes.Add(new PlaylistLampHistoricalScoreChange(
                        ActiveScoreSource.Beatoraja,
                        row?.sha256,
                        row?.history_id ?? 0L,
                        row?.played_at ?? 0L,
                        null,
                        row?.old_clear,
                        null,
                        row?.old_exscore,
                        totalNotes));
                }
                return ReadChangesResult.Available(changes);
            }

            return ReadChangesResult.Unavailable("No active score provider is configured.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ReadChangesResult.Unavailable(exception.Message);
        }
    }

    private static IReadOnlyDictionary<string, BMSScore> BuildBmsScoreMap(PlaylistLampScoreSnapshot snapshot)
    {
        var result = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, PlaylistLampScore> pair in snapshot.ScoresBySha256)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
            {
                continue;
            }
            result[pair.Key] = new BMSScore
            {
                hash = pair.Key,
                totalnotes = pair.Value.TotalNotes
            };
        }
        return result;
    }

    private static int? ResolveNotes(
        PlaylistLampHistoricalScoreSourceContext sourceContext,
        string sha256,
        PlaylistLampScoreSnapshot currentScoreSnapshot)
    {
        if (!string.IsNullOrWhiteSpace(sha256)
            && sourceContext.BeatorajaNotesBySha256.TryGetValue(sha256, out int notes)
            && notes > 0)
        {
            return notes;
        }
        return !string.IsNullOrWhiteSpace(sha256)
            && currentScoreSnapshot.ScoresBySha256.TryGetValue(sha256, out PlaylistLampScore score)
            && score?.TotalNotes > 0
                ? score.TotalNotes
                : null;
    }

    private static PlaylistLampHistoricalScoreSnapshotResult CreateUnavailable(
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        DateTime? selectedLocalDate,
        DateTime today,
        string message)
    {
        PlaylistLampHistoricalDateRange range = new(null, today);
        if (!selectedLocalDate.HasValue)
        {
            return new PlaylistLampHistoricalScoreSnapshotResult(
                currentScoreSnapshot,
                range,
                null,
                PlaylistLampHistoricalSnapshotStatus.Latest,
                message);
        }
        return new PlaylistLampHistoricalScoreSnapshotResult(
            CreateFailedScoreSnapshot(currentScoreSnapshot, message),
            range,
            selectedLocalDate,
            PlaylistLampHistoricalSnapshotStatus.Unavailable,
            message);
    }

    private static PlaylistLampScoreSnapshot CreateFailedScoreSnapshot(
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        string message)
    {
        return currentScoreSnapshot.WithScores(
            new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
            message,
            ScoreTableLoadStatus.Failed);
    }

    private static string DescribeDiagnostics(IReadOnlyList<PlayHistoryDiagnostic> diagnostics)
    {
        PlayHistoryDiagnostic diagnostic = diagnostics?.FirstOrDefault(item => item?.Severity == PlayHistoryDiagnosticSeverity.Error)
            ?? diagnostics?.FirstOrDefault();
        return string.IsNullOrWhiteSpace(diagnostic?.Message)
            ? "Historical score history could not be read."
            : diagnostic.Message;
    }

    private sealed class ReadChangesResult
    {
        private ReadChangesResult(bool isAvailable, IReadOnlyList<PlaylistLampHistoricalScoreChange> changes, string failureMessage)
        {
            IsAvailable = isAvailable;
            Changes = changes ?? [];
            FailureMessage = failureMessage ?? string.Empty;
        }

        internal bool IsAvailable { get; }

        internal IReadOnlyList<PlaylistLampHistoricalScoreChange> Changes { get; }

        internal string FailureMessage { get; }

        internal static ReadChangesResult Available(IReadOnlyList<PlaylistLampHistoricalScoreChange> changes)
            => new(true, changes, string.Empty);

        internal static ReadChangesResult Unavailable(string message)
            => new(false, [], message);
    }
}

/// <summary>
/// Applies provider-neutral local-midnight rollback semantics to immutable score maps.
/// </summary>
internal static class PlaylistLampHistoricalScoreSnapshotBuilder
{
    /// <summary>
    /// Builds Latest or a historical score projection from provider history changes.
    /// </summary>
    /// <param name="currentScoreSnapshot">Current immutable score snapshot.</param>
    /// <param name="activeSource">The exact active provider.</param>
    /// <param name="selectedLocalDate">Selected local date, or null for Latest.</param>
    /// <param name="changes">Provider history rows in any order.</param>
    /// <param name="todayLocalDate">Testable local today override.</param>
    /// <returns>Immutable historical result.</returns>
    internal static PlaylistLampHistoricalScoreSnapshotResult Build(
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        ActiveScoreSource activeSource,
        DateTime? selectedLocalDate,
        IEnumerable<PlaylistLampHistoricalScoreChange> changes,
        DateTime? todayLocalDate = null)
    {
        ArgumentNullException.ThrowIfNull(currentScoreSnapshot);
        var today = DateTime.SpecifyKind((todayLocalDate ?? DateTime.Today).Date, DateTimeKind.Unspecified);
        var rows = (changes ?? [])
            .Where(row => row != null && row.Source == activeSource)
            .ToList();
        DateTime? earliest = null;
        foreach (PlaylistLampHistoricalScoreChange row in rows)
        {
            if (!TryToLocalDate(row.PlayedAtUnixSeconds, out DateTime localDate))
            {
                return CreateUnavailable(
                    currentScoreSnapshot,
                    selectedLocalDate,
                    today,
                    "Historical score row has an invalid Unix timestamp.");
            }
            if (localDate > today)
            {
                continue;
            }
            earliest = !earliest.HasValue || localDate < earliest.Value ? localDate : earliest;
        }
        PlaylistLampHistoricalDateRange range = new(earliest, today);

        if (!selectedLocalDate.HasValue)
        {
            return new PlaylistLampHistoricalScoreSnapshotResult(
                currentScoreSnapshot,
                range,
                null,
                PlaylistLampHistoricalSnapshotStatus.Latest);
        }

        var selected = DateTime.SpecifyKind(selectedLocalDate.Value.Date, DateTimeKind.Unspecified);
        if (!range.Contains(selected))
        {
            return new PlaylistLampHistoricalScoreSnapshotResult(
                CreateFailedScoreSnapshot(currentScoreSnapshot, "Selected historical date is outside the available range."),
                range,
                selected,
                PlaylistLampHistoricalSnapshotStatus.Unavailable,
                "Selected historical date is outside the available range.");
        }

        long cutoffUnixSeconds = ToUnixSecondsAtLocalMidnight(selected.AddDays(1));
        var futureRows = rows
            .Where(row => row.PlayedAtUnixSeconds >= cutoffUnixSeconds)
            .OrderByDescending(row => row.PlayedAtUnixSeconds)
            .ThenByDescending(row => row.SourceId)
            .ToList();
        var byHash = new Dictionary<string, PlaylistLampScore>(currentScoreSnapshot.ScoresByHash, StringComparer.OrdinalIgnoreCase);
        var bySha256 = new Dictionary<string, PlaylistLampScore>(currentScoreSnapshot.ScoresBySha256, StringComparer.OrdinalIgnoreCase);
        foreach (PlaylistLampHistoricalScoreChange row in futureRows)
        {
            if (string.IsNullOrWhiteSpace(row.ChartKey))
            {
                return new PlaylistLampHistoricalScoreSnapshotResult(
                    CreateFailedScoreSnapshot(currentScoreSnapshot, "Historical score row has no chart identity."),
                    range,
                    selected,
                    PlaylistLampHistoricalSnapshotStatus.Unavailable,
                    "Historical score row has no chart identity.");
            }
            if (!TryBuildOldScore(row, out PlaylistLampScore oldScore, out bool explicitNoScore, out string failureMessage))
            {
                return new PlaylistLampHistoricalScoreSnapshotResult(
                    CreateFailedScoreSnapshot(currentScoreSnapshot, failureMessage),
                    range,
                    selected,
                    PlaylistLampHistoricalSnapshotStatus.Unavailable,
                    failureMessage);
            }
            if (row.Source == ActiveScoreSource.Lr2)
            {
                if (explicitNoScore)
                {
                    byHash.Remove(row.ChartKey);
                }
                else
                {
                    byHash[row.ChartKey] = oldScore;
                }
            }
            else
            {
                if (explicitNoScore)
                {
                    bySha256.Remove(row.ChartKey);
                }
                else
                {
                    bySha256[row.ChartKey] = oldScore;
                }
            }
        }

        PlaylistLampScoreSnapshot snapshot = currentScoreSnapshot.WithScores(byHash, bySha256);
        return new PlaylistLampHistoricalScoreSnapshotResult(
            snapshot,
            range,
            selected,
            PlaylistLampHistoricalSnapshotStatus.Available);
    }

    private static bool TryBuildOldScore(
        PlaylistLampHistoricalScoreChange row,
        out PlaylistLampScore score,
        out bool explicitNoScore,
        out string failureMessage)
    {
        score = null;
        explicitNoScore = false;
        failureMessage = string.Empty;
        if (row.Source == ActiveScoreSource.Lr2 && !row.OldPlayCount.HasValue)
        {
            explicitNoScore = true;
            return true;
        }
        if (row.Source == ActiveScoreSource.Lr2 && row.OldPlayCount is < 0)
        {
            failureMessage = "Historical LR2 score row has a negative old playcount.";
            return false;
        }

        bool noOldScore = !row.OldClear.HasValue
            && !row.OldOperationHistory.HasValue
            && !row.OldExScore.HasValue
            && !row.OldTotalNotes.HasValue;
        if (noOldScore)
        {
            if (row.Source == ActiveScoreSource.Lr2)
            {
                failureMessage = "Historical LR2 score row has incomplete old score values.";
                return false;
            }
            explicitNoScore = true;
            return true;
        }

        if (row.OldClear is not { } oldClear
            || row.OldExScore is not { } oldExScore
            || row.OldTotalNotes is not { } oldTotalNotes)
        {
            failureMessage = "Historical score row has incomplete old score values.";
            return false;
        }
        if (row.Source == ActiveScoreSource.Lr2 && row.OldOperationHistory is not { } oldOperationHistory)
        {
            failureMessage = "Historical LR2 score row has no old option history.";
            return false;
        }
        if (oldExScore < 0
            || oldTotalNotes <= 0
            || (long)oldExScore > 2L * oldTotalNotes
            || !IsValidClearValue(row.Source, oldClear))
        {
            failureMessage = "Historical score row has malformed old score values.";
            return false;
        }

        ClearType clear = row.Source == ActiveScoreSource.Lr2
            ? ClearTypeStorageConverter.FromLr2ScoreValue(oldClear, row.OldOperationHistory!.Value)
            : (ClearType)oldClear;
        score = PlaylistLampScore.FromExScore(
            row.Source == ActiveScoreSource.Lr2 ? row.ChartKey : null,
            row.Source == ActiveScoreSource.Beatoraja ? row.ChartKey : null,
            clear,
            oldExScore,
            oldTotalNotes);
        return true;
    }

    private static bool IsValidClearValue(ActiveScoreSource source, int value)
    {
        if (source == ActiveScoreSource.Beatoraja)
        {
            return value >= (int)ClearType.NO_PLAY && value <= (int)ClearType.MAX;
        }
        return value is 0 or 1 or 2 or 3 or 4 or 5 or 21;
    }

    private static bool TryToLocalDate(long unixSeconds, out DateTime localDate)
    {
        try
        {
            localDate = DateTime.SpecifyKind(
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().Date,
                DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            localDate = default;
            return false;
        }
    }

    private static long ToUnixSecondsAtLocalMidnight(DateTime localDate)
    {
        var unspecified = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZoneInfo.Local);
        return new DateTimeOffset(utc).ToUnixTimeSeconds();
    }

    private static PlaylistLampScoreSnapshot CreateFailedScoreSnapshot(
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        string message)
    {
        return currentScoreSnapshot.WithScores(
            new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
            message,
            ScoreTableLoadStatus.Failed);
    }

    private static PlaylistLampHistoricalScoreSnapshotResult CreateUnavailable(
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        DateTime? selectedLocalDate,
        DateTime today,
        string message)
    {
        PlaylistLampHistoricalDateRange range = new(null, today);
        if (!selectedLocalDate.HasValue)
        {
            return new PlaylistLampHistoricalScoreSnapshotResult(
                currentScoreSnapshot,
                range,
                null,
                PlaylistLampHistoricalSnapshotStatus.Latest,
                message);
        }
        return new PlaylistLampHistoricalScoreSnapshotResult(
            CreateFailedScoreSnapshot(currentScoreSnapshot, message),
            range,
            selectedLocalDate,
            PlaylistLampHistoricalSnapshotStatus.Unavailable,
            message);
    }
}
