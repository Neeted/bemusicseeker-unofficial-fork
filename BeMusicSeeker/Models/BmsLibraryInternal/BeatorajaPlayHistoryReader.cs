using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BeatorajaPlayHistoryReader
{
    internal const int DefaultReadLimit = 5000;

    private const int NormalScoreMode = 0;

    internal BeatorajaPlayHistoryReadResult Read(BeatorajaPlayHistoryReadRequest request)
    {
        return Read(request, CancellationToken.None);
    }

    internal BeatorajaPlayHistoryReadResult Read(BeatorajaPlayHistoryReadRequest request, CancellationToken cancellationToken)
    {
        request = ResolveRequestPaths(request);
        cancellationToken.ThrowIfCancellationRequested();
        PlayHistorySourceProfile sourceProfile = PlayHistorySourceProfile.Beatoraja(request.ScoreLogDbPath);
        var diagnostics = new List<PlayHistoryDiagnostic>();
        IReadOnlyList<BeatorajaPlayerAggregateSnapshot> playerSnapshots = LoadPlayerAggregateSnapshots(request, diagnostics, cancellationToken, out bool playerSnapshotsAvailable);
        try
        {
            if (request.FinalizationFilter == Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly)
            {
                return new BeatorajaPlayHistoryReadResult(
                    sourceProfile,
                    [],
                    diagnostics,
                    Lr2PlayHistorySchemaStatus.Installed,
                    playerSnapshots,
                    playerSnapshotsAvailable);
            }

            if (!EnsureScoreLogExists(request.ScoreLogDbPath, diagnostics))
            {
                return new BeatorajaPlayHistoryReadResult(
                    sourceProfile,
                    [],
                    diagnostics,
                    Lr2PlayHistorySchemaStatus.Installed,
                    playerSnapshots,
                    playerSnapshotsAvailable);
            }

            using var connection = new SQLiteConnection(request.ScoreLogDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            List<object> args = [];
            string sql = BuildScoreLogReadSql(request, args);
            cancellationToken.ThrowIfCancellationRequested();
            List<ScoreLogRow> logRows = connection.Query<ScoreLogRow>(sql, [.. args]);
            var rows = new List<BeatorajaPlayHistoryRecord>(logRows.Count);
            foreach (ScoreLogRow row in logRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(Convert(row, request.ScoresBySha256));
            }
            logRows.Clear();

            return new BeatorajaPlayHistoryReadResult(
                sourceProfile,
                rows,
                diagnostics,
                Lr2PlayHistorySchemaStatus.Installed,
                playerSnapshots,
                playerSnapshotsAvailable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Error, "play_history_beatoraja_read_failed", ex.Message, request.ScoreLogDbPath));
            return new BeatorajaPlayHistoryReadResult(sourceProfile, [], diagnostics, Lr2PlayHistorySchemaStatus.Unreadable, playerSnapshots, playerSnapshotsAvailable);
        }
    }

    internal BeatorajaPlayHistoryPeriodIndexResult ReadPeriodIndex(BeatorajaPlayHistoryPeriodIndexRequest request, CancellationToken cancellationToken)
    {
        BeatorajaPlayHistoryReadRequest resolved = ResolveRequestPaths(new BeatorajaPlayHistoryReadRequest
        {
            ScoreDbPath = request?.ScoreDbPath,
            ScoreLogDbPath = request?.ScoreLogDbPath
        });
        cancellationToken.ThrowIfCancellationRequested();
        PlayHistorySourceProfile sourceProfile = PlayHistorySourceProfile.Beatoraja(resolved.ScoreLogDbPath);
        var diagnostics = new List<PlayHistoryDiagnostic>();
        if (!EnsureScoreLogExists(resolved.ScoreLogDbPath, diagnostics))
        {
            return new BeatorajaPlayHistoryPeriodIndexResult(sourceProfile, [], diagnostics, Lr2PlayHistorySchemaStatus.Installed);
        }

        try
        {
            using var connection = new SQLiteConnection(resolved.ScoreLogDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            cancellationToken.ThrowIfCancellationRequested();
            List<PeriodIndexRow> rows = connection.Query<PeriodIndexRow>(
                "SELECT MAX(date) AS played_at FROM scorelog WHERE mode = ? GROUP BY strftime('%Y-%m-%d', date, 'unixepoch', 'localtime') ORDER BY played_at DESC;",
                NormalScoreMode);
            cancellationToken.ThrowIfCancellationRequested();
            return new BeatorajaPlayHistoryPeriodIndexResult(sourceProfile, [.. rows.Select(row => row.played_at)], diagnostics, Lr2PlayHistorySchemaStatus.Installed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Error, "play_history_beatoraja_period_index_failed", ex.Message, resolved.ScoreLogDbPath));
            return new BeatorajaPlayHistoryPeriodIndexResult(sourceProfile, [], diagnostics, Lr2PlayHistorySchemaStatus.Unreadable);
        }
    }

    internal static BeatorajaPlayHistoryReadRequest ResolveRequestPaths(BeatorajaPlayHistoryReadRequest request)
    {
        request ??= new BeatorajaPlayHistoryReadRequest();
        if (string.IsNullOrWhiteSpace(request.ScoreDbPath))
        {
            return request;
        }

        string playerDirectory = Path.GetDirectoryName(request.ScoreDbPath);
        if (string.IsNullOrWhiteSpace(playerDirectory))
        {
            return request;
        }
        request.ScoreLogDbPath = string.IsNullOrWhiteSpace(request.ScoreLogDbPath)
            ? Path.Combine(playerDirectory, "scorelog.db")
            : request.ScoreLogDbPath;
        return request;
    }

    private static bool EnsureScoreLogExists(string scoreLogDbPath, List<PlayHistoryDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(scoreLogDbPath) && File.Exists(scoreLogDbPath))
        {
            return true;
        }

        diagnostics.Add(CreateDiagnostic(
            PlayHistoryDiagnosticSeverity.Warning,
            "play_history_beatoraja_scorelog_missing",
            "scorelog.db does not exist; beatoraja update history is unavailable.",
            scoreLogDbPath));
        return false;
    }

    private static string BuildScoreLogReadSql(BeatorajaPlayHistoryReadRequest request, List<object> args)
    {
        var where = new List<string> { "mode = ?" };
        args.Add(NormalScoreMode);
        if (request.PlayedAtFromInclusive.HasValue)
        {
            where.Add("date >= ?");
            args.Add(request.PlayedAtFromInclusive.Value);
        }
        if (request.PlayedAtToExclusive.HasValue)
        {
            where.Add("date < ?");
            args.Add(request.PlayedAtToExclusive.Value);
        }

        string sql = "SELECT rowid AS history_id, sha256, mode, clear, oldclear, score, oldscore, combo, oldcombo, minbp, oldminbp, date FROM scorelog WHERE "
            + string.Join(" AND ", where)
            + " ORDER BY date DESC, rowid DESC";
        int limit = request.Limit.HasValue && request.Limit.Value > 0
            ? request.Limit.Value
            : DefaultReadLimit;
        if (!request.DisableLimit && limit > 0)
        {
            sql += " LIMIT ?";
            args.Add(limit);
        }
        return sql + ";";
    }

    private static IReadOnlyList<BeatorajaPlayerAggregateSnapshot> LoadPlayerAggregateSnapshots(
        BeatorajaPlayHistoryReadRequest request,
        List<PlayHistoryDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        out bool available)
    {
        available = false;
        if (string.IsNullOrWhiteSpace(request.ScoreDbPath) || !File.Exists(request.ScoreDbPath))
        {
            diagnostics?.Add(CreateDiagnostic(
                PlayHistoryDiagnosticSeverity.Warning,
                "play_history_beatoraja_player_aggregate_unavailable",
                "score.db does not exist; beatoraja period summary is unavailable.",
                request.ScoreDbPath));
            return [];
        }

        try
        {
            using var connection = new SQLiteConnection(request.ScoreDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            cancellationToken.ThrowIfCancellationRequested();
            List<PlayerAggregateRow> rows = connection.Query<PlayerAggregateRow>(
                "SELECT date, playcount, epg, lpg, egr, lgr, egd, lgd, ebd, lbd, epr, lpr, ems, lms, playtime FROM player WHERE date > 0 ORDER BY date ASC;");
            cancellationToken.ThrowIfCancellationRequested();
            available = true;
            var snapshots = new List<BeatorajaPlayerAggregateSnapshot>(rows.Count);
            long currentDate = 0;
            for (int index = 0; index < rows.Count; index++)
            {
                PlayerAggregateRow row = rows[index];
                if (row.date <= 0)
                {
                    continue;
                }
                var snapshot = new BeatorajaPlayerAggregateSnapshot
                {
                    DateUnixSeconds = row.date,
                    PlayCount = row.playcount,
                    JudgeCount =
                        row.epg + row.lpg
                        + row.egr + row.lgr
                        + row.egd + row.lgd
                        + row.ebd + row.lbd
                        + row.epr + row.lpr
                        + row.ems + row.lms,
                    PlaytimeSeconds = row.playtime,
                    HasInvalidRawValue = HasNegativeAggregateValue(row)
                };
                if (snapshots.Count > 0 && row.date == currentDate)
                {
                    snapshots[snapshots.Count - 1] = snapshot;
                    continue;
                }
                snapshots.Add(snapshot);
                currentDate = row.date;
            }
            return snapshots;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics?.Add(CreateDiagnostic(
                PlayHistoryDiagnosticSeverity.Warning,
                "play_history_beatoraja_player_aggregate_unreadable",
                "score.db player aggregate could not be read; beatoraja period summary is unavailable. " + ex.Message,
                request.ScoreDbPath));
            return [];
        }
    }

    private static bool HasNegativeAggregateValue(PlayerAggregateRow row)
    {
        return row.playcount < 0
            || row.epg < 0
            || row.lpg < 0
            || row.egr < 0
            || row.lgr < 0
            || row.egd < 0
            || row.lgd < 0
            || row.ebd < 0
            || row.lbd < 0
            || row.epr < 0
            || row.lpr < 0
            || row.ems < 0
            || row.lms < 0
            || row.playtime < 0;
    }

    private static BeatorajaPlayHistoryRecord Convert(ScoreLogRow row, IReadOnlyDictionary<string, BMSScore> scoresBySha256)
    {
        string sha256 = NormalizeSha256(row.sha256);
        int notes = 0;
        if (row.mode == NormalScoreMode && !string.IsNullOrWhiteSpace(sha256))
        {
            notes = scoresBySha256 != null
                && scoresBySha256.TryGetValue(sha256, out BMSScore score)
                && score?.totalnotes > 0
                    ? score.totalnotes
                    : 0;
        }
        var record = new BeatorajaPlayHistoryRecord
        {
            history_id = row.history_id,
            sha256 = sha256,
            mode = row.mode,
            played_at = row.date,
            old_clear = row.oldclear,
            new_clear = row.clear,
            old_exscore = row.oldscore,
            new_exscore = row.score,
            notes = notes,
            old_maxcombo = row.oldcombo,
            new_maxcombo = row.combo,
            old_minbp = NormalizeBeatorajaMinBp(row.oldminbp),
            new_minbp = NormalizeBeatorajaMinBp(row.minbp)
        };
        return record;
    }

    private static int? NormalizeBeatorajaMinBp(int? value)
    {
        return !value.HasValue || value.Value == int.MaxValue ? null : value;
    }

    private static PlayHistoryDiagnostic CreateDiagnostic(PlayHistoryDiagnosticSeverity severity, string code, string message, string sourcePath)
    {
        return new PlayHistoryDiagnostic
        {
            Provider = PlayHistoryProvider.Beatoraja,
            Stage = "read",
            Severity = severity,
            Code = code ?? string.Empty,
            Message = message ?? string.Empty,
            SourcePath = sourcePath ?? string.Empty
        };
    }

    private static string NormalizeSha256(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
    }

    private sealed class ScoreLogRow
    {
        public long history_id { get; set; }

        public string sha256 { get; set; }

        public int mode { get; set; }

        public int clear { get; set; }

        public int oldclear { get; set; }

        public int score { get; set; }

        public int oldscore { get; set; }

        public int combo { get; set; }

        public int oldcombo { get; set; }

        public int minbp { get; set; }

        public int oldminbp { get; set; }

        public long date { get; set; }
    }

    private sealed class PlayerAggregateRow
    {
        public long date { get; set; }

        public long playcount { get; set; }

        public long epg { get; set; }

        public long lpg { get; set; }

        public long egr { get; set; }

        public long lgr { get; set; }

        public long egd { get; set; }

        public long lgd { get; set; }

        public long ebd { get; set; }

        public long lbd { get; set; }

        public long epr { get; set; }

        public long lpr { get; set; }

        public long ems { get; set; }

        public long lms { get; set; }

        public long playtime { get; set; }
    }

    private sealed class PeriodIndexRow
    {
        public long played_at { get; set; }
    }
}
