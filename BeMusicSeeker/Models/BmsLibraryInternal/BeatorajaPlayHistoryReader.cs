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
        PlayHistorySourceProfile sourceProfile = PlayHistorySourceProfile.Beatoraja(request.ScoreDataLogDbPath);
        var diagnostics = new List<PlayHistoryDiagnostic>();
        if (!EnsureScoreDataLogExists(request.ScoreDataLogDbPath, diagnostics))
        {
            return new BeatorajaPlayHistoryReadResult(sourceProfile, [], diagnostics, Lr2PlayHistorySchemaStatus.NotInstalled);
        }

        try
        {
            using var connection = new SQLiteConnection(request.ScoreDataLogDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            List<object> args = [];
            string sql = BuildScoreDataLogReadSql(request, args);
            cancellationToken.ThrowIfCancellationRequested();
            List<ScoreDataLogRow> dataRows = connection.Query<ScoreDataLogRow>(sql, [.. args]);
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<ScoreLogKey, ScoreLogRow> scoreLogs = LoadScoreLogs(request, dataRows, diagnostics, cancellationToken);
            var rows = new List<BeatorajaPlayHistoryRecord>(dataRows.Count);
            foreach (ScoreDataLogRow row in dataRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BeatorajaPlayHistoryRecord record = Convert(row);
                if (scoreLogs.TryGetValue(new ScoreLogKey(record.sha256, record.mode, record.played_at), out ScoreLogRow log))
                {
                    ApplyScoreLog(record, log);
                }
                rows.Add(record);
            }

            return new BeatorajaPlayHistoryReadResult(sourceProfile, rows, diagnostics, Lr2PlayHistorySchemaStatus.Installed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Error, "play_history_beatoraja_read_failed", ex.Message, request.ScoreDataLogDbPath));
            return new BeatorajaPlayHistoryReadResult(sourceProfile, [], diagnostics, Lr2PlayHistorySchemaStatus.Unreadable);
        }
    }

    internal BeatorajaPlayHistoryPeriodIndexResult ReadPeriodIndex(BeatorajaPlayHistoryPeriodIndexRequest request, CancellationToken cancellationToken)
    {
        BeatorajaPlayHistoryReadRequest resolved = ResolveRequestPaths(new BeatorajaPlayHistoryReadRequest
        {
            ScoreDbPath = request?.ScoreDbPath,
            ScoreDataLogDbPath = request?.ScoreDataLogDbPath
        });
        cancellationToken.ThrowIfCancellationRequested();
        PlayHistorySourceProfile sourceProfile = PlayHistorySourceProfile.Beatoraja(resolved.ScoreDataLogDbPath);
        var diagnostics = new List<PlayHistoryDiagnostic>();
        if (!EnsureScoreDataLogExists(resolved.ScoreDataLogDbPath, diagnostics))
        {
            return new BeatorajaPlayHistoryPeriodIndexResult(sourceProfile, [], diagnostics, Lr2PlayHistorySchemaStatus.NotInstalled);
        }

        try
        {
            using var connection = new SQLiteConnection(resolved.ScoreDataLogDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            cancellationToken.ThrowIfCancellationRequested();
            List<PeriodIndexRow> rows = connection.Query<PeriodIndexRow>(
                "SELECT MAX(date) AS played_at FROM scoredatalog WHERE mode = ? GROUP BY strftime('%Y-%m-%d', date, 'unixepoch', 'localtime') ORDER BY played_at DESC;",
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
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Error, "play_history_beatoraja_period_index_failed", ex.Message, resolved.ScoreDataLogDbPath));
            return new BeatorajaPlayHistoryPeriodIndexResult(sourceProfile, [], diagnostics, Lr2PlayHistorySchemaStatus.Unreadable);
        }
    }

    private static BeatorajaPlayHistoryReadRequest ResolveRequestPaths(BeatorajaPlayHistoryReadRequest request)
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
        request.ScoreDataLogDbPath = string.IsNullOrWhiteSpace(request.ScoreDataLogDbPath)
            ? Path.Combine(playerDirectory, "scoredatalog.db")
            : request.ScoreDataLogDbPath;
        request.ScoreLogDbPath = string.IsNullOrWhiteSpace(request.ScoreLogDbPath)
            ? Path.Combine(playerDirectory, "scorelog.db")
            : request.ScoreLogDbPath;
        return request;
    }

    private static bool EnsureScoreDataLogExists(string scoreDataLogDbPath, List<PlayHistoryDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(scoreDataLogDbPath) && File.Exists(scoreDataLogDbPath))
        {
            return true;
        }

        diagnostics.Add(CreateDiagnostic(
            PlayHistoryDiagnosticSeverity.Error,
            "play_history_beatoraja_scoredatalog_missing",
            "scoredatalog.db does not exist.",
            scoreDataLogDbPath));
        return false;
    }

    private static string BuildScoreDataLogReadSql(BeatorajaPlayHistoryReadRequest request, List<object> args)
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

        string sql = "SELECT rowid AS history_id, sha256, mode, clear, epg, lpg, egr, lgr, egd, lgd, ebd, lbd, epr, lpr, ems, lms, notes, combo, minbp, playcount, clearcount, option, seed, random, date AS played_at, state, scorehash FROM scoredatalog WHERE "
            + string.Join(" AND ", where)
            + " ORDER BY date DESC, rowid DESC";
        int limit = request.Limit.HasValue && request.Limit.Value > 0
            ? request.Limit.Value
            : DefaultReadLimit;
        if (limit > 0)
        {
            sql += " LIMIT ?";
            args.Add(limit);
        }
        return sql + ";";
    }

    private static string BuildScoreLogReadSql(BeatorajaPlayHistoryReadRequest request, IReadOnlyList<ScoreDataLogRow> dataRows, List<object> args)
    {
        var where = new List<string> { "mode = ?" };
        args.Add(NormalScoreMode);
        long? fromInclusive = request.PlayedAtFromInclusive;
        long? toExclusive = request.PlayedAtToExclusive;
        if (dataRows != null && dataRows.Count > 0)
        {
            long minDate = dataRows.Min(row => row.played_at);
            long maxDate = dataRows.Max(row => row.played_at);
            fromInclusive = !fromInclusive.HasValue || fromInclusive.Value < minDate ? minDate : fromInclusive;
            toExclusive = !toExclusive.HasValue || toExclusive.Value > maxDate + 1 ? maxDate + 1 : toExclusive;
        }
        if (fromInclusive.HasValue)
        {
            where.Add("date >= ?");
            args.Add(fromInclusive.Value);
        }
        if (toExclusive.HasValue)
        {
            where.Add("date < ?");
            args.Add(toExclusive.Value);
        }
        return "SELECT sha256, mode, clear, oldclear, score, oldscore, combo, oldcombo, minbp, oldminbp, date FROM scorelog WHERE "
            + string.Join(" AND ", where)
            + ";";
    }

    private static Dictionary<ScoreLogKey, ScoreLogRow> LoadScoreLogs(
        BeatorajaPlayHistoryReadRequest request,
        IReadOnlyList<ScoreDataLogRow> dataRows,
        List<PlayHistoryDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var logs = new Dictionary<ScoreLogKey, ScoreLogRow>();
        if (dataRows == null || dataRows.Count == 0 || string.IsNullOrWhiteSpace(request.ScoreLogDbPath) || !File.Exists(request.ScoreLogDbPath))
        {
            return logs;
        }

        try
        {
            using var connection = new SQLiteConnection(request.ScoreLogDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            List<object> args = [];
            string sql = BuildScoreLogReadSql(request, dataRows, args);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ScoreLogRow row in connection.Query<ScoreLogRow>(sql, [.. args]))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = new ScoreLogKey(row.sha256, row.mode, row.date);
                if (!logs.ContainsKey(key))
                {
                    logs.Add(key, row);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            diagnostics?.Add(CreateDiagnostic(
                PlayHistoryDiagnosticSeverity.Warning,
                "play_history_beatoraja_scorelog_unreadable",
                "scorelog.db could not be read; best deltas are unavailable.",
                request.ScoreLogDbPath));
            return new Dictionary<ScoreLogKey, ScoreLogRow>();
        }
        return logs;
    }

    private static BeatorajaPlayHistoryRecord Convert(ScoreDataLogRow row)
    {
        return new BeatorajaPlayHistoryRecord
        {
            history_id = row.history_id,
            sha256 = NormalizeSha256(row.sha256),
            mode = row.mode,
            played_at = row.played_at,
            clear = row.clear,
            epg = Math.Max(0, row.epg),
            lpg = Math.Max(0, row.lpg),
            egr = Math.Max(0, row.egr),
            lgr = Math.Max(0, row.lgr),
            egd = Math.Max(0, row.egd),
            lgd = Math.Max(0, row.lgd),
            ebd = Math.Max(0, row.ebd),
            lbd = Math.Max(0, row.lbd),
            epr = Math.Max(0, row.epr),
            lpr = Math.Max(0, row.lpr),
            ems = Math.Max(0, row.ems),
            lms = Math.Max(0, row.lms),
            notes = Math.Max(0, row.notes),
            combo = Math.Max(0, row.combo),
            minbp = row.minbp,
            playcount = Math.Max(0, row.playcount),
            clearcount = Math.Max(0, row.clearcount),
            option = row.option,
            seed = row.seed,
            random = row.random,
            state = row.state,
            scorehash = row.scorehash ?? string.Empty
        };
    }

    private static void ApplyScoreLog(BeatorajaPlayHistoryRecord record, ScoreLogRow log)
    {
        record.old_clear = log.oldclear;
        record.new_clear = log.clear;
        record.old_exscore = log.oldscore;
        record.new_exscore = log.score;
        record.old_maxcombo = log.oldcombo;
        record.new_maxcombo = log.combo;
        record.old_minbp = log.oldminbp;
        record.new_minbp = log.minbp;
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

    private readonly struct ScoreLogKey : IEquatable<ScoreLogKey>
    {
        private readonly string sha256;

        private readonly int mode;

        private readonly long date;

        internal ScoreLogKey(string sha256, int mode, long date)
        {
            this.sha256 = NormalizeSha256(sha256);
            this.mode = mode;
            this.date = date;
        }

        public bool Equals(ScoreLogKey other)
        {
            return mode == other.mode
                && date == other.date
                && string.Equals(sha256, other.sha256, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return obj is ScoreLogKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + StringComparer.OrdinalIgnoreCase.GetHashCode(sha256 ?? string.Empty);
                hash = (hash * 31) + mode;
                hash = (hash * 31) + date.GetHashCode();
                return hash;
            }
        }
    }

    private sealed class ScoreDataLogRow
    {
        public long history_id { get; set; }

        public string sha256 { get; set; }

        public int mode { get; set; }

        public int clear { get; set; }

        public int epg { get; set; }

        public int lpg { get; set; }

        public int egr { get; set; }

        public int lgr { get; set; }

        public int egd { get; set; }

        public int lgd { get; set; }

        public int ebd { get; set; }

        public int lbd { get; set; }

        public int epr { get; set; }

        public int lpr { get; set; }

        public int ems { get; set; }

        public int lms { get; set; }

        public int notes { get; set; }

        public int combo { get; set; }

        public int minbp { get; set; }

        public int playcount { get; set; }

        public int clearcount { get; set; }

        public int option { get; set; }

        public long seed { get; set; }

        public int random { get; set; }

        public long played_at { get; set; }

        public int state { get; set; }

        public string scorehash { get; set; }
    }

    private sealed class ScoreLogRow
    {
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

    private sealed class PeriodIndexRow
    {
        public long played_at { get; set; }
    }
}
