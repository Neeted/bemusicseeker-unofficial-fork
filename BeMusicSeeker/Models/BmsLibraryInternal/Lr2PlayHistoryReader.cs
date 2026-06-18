using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2PlayHistoryReader
{
    internal const int DefaultReadLimit = 5000;

    internal Lr2PlayHistoryReadResult Read(Lr2PlayHistoryReadRequest request)
    {
        request ??= new Lr2PlayHistoryReadRequest();
        PlayHistorySourceProfile sourceProfile = PlayHistorySourceProfile.Lr2(request.ScoreDbPath);
        var diagnostics = new List<PlayHistoryDiagnostic>();

        Lr2PlayHistorySchemaCheckResult schema = new Lr2PlayHistorySchemaService().Check(request.ScoreDbPath, request.IsLr2LinkedProfile);
        if (schema.Status == Lr2PlayHistorySchemaStatus.SkippedProfile)
        {
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Info, "play_history_lr2_skipped_profile", schema.Message, request.ScoreDbPath));
            return new Lr2PlayHistoryReadResult(sourceProfile, [], diagnostics, schema.Status);
        }
        if (schema.Status == Lr2PlayHistorySchemaStatus.Unreadable
            || schema.Status == Lr2PlayHistorySchemaStatus.NotInstalled
            || schema.Status == Lr2PlayHistorySchemaStatus.ManualRepairRequired)
        {
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Error, GetSchemaDiagnosticCode(schema.Status), schema.Message, request.ScoreDbPath));
            return new Lr2PlayHistoryReadResult(sourceProfile, [], diagnostics, schema.Status);
        }
        if (schema.Status == Lr2PlayHistorySchemaStatus.Repairable)
        {
            if (schema.MissingIndexes.Count > 0 || schema.MismatchedIndexes.Count > 0)
            {
                diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_index_repair_required", schema.Message, request.ScoreDbPath));
                return new Lr2PlayHistoryReadResult(sourceProfile, [], diagnostics, schema.Status);
            }
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Warning, "play_history_lr2_schema_repairable", schema.Message, request.ScoreDbPath));
        }

        if (string.IsNullOrWhiteSpace(request.ScoreDbPath) || !File.Exists(request.ScoreDbPath))
        {
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_score_db_missing", "Score DB file does not exist.", request.ScoreDbPath));
            return new Lr2PlayHistoryReadResult(sourceProfile, [], diagnostics, schema.Status);
        }

        try
        {
            using LR2ScoreDBExtended db = new(request.ScoreDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, acquireProcessLock: false);
            List<object> args = [];
            string sql = BuildReadSql(request, args);
            List<Lr2PlayHistoryRecord> rows = db.Query<Lr2PlayHistoryRecord>(sql, [.. args]);
            return new Lr2PlayHistoryReadResult(sourceProfile, rows, diagnostics, schema.Status);
        }
        catch (Exception ex)
        {
            diagnostics.Add(CreateDiagnostic(PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_read_failed", ex.Message, request.ScoreDbPath));
            return new Lr2PlayHistoryReadResult(sourceProfile, [], diagnostics, schema.Status);
        }
    }

    private static string BuildReadSql(Lr2PlayHistoryReadRequest request, List<object> args)
    {
        var where = new List<string>();
        if (!request.IncludeUnfinalized)
        {
            where.Add("finalized = 1");
        }
        if (request.PlayedAtFromInclusive.HasValue)
        {
            where.Add("played_at >= ?");
            args.Add(request.PlayedAtFromInclusive.Value);
        }
        if (request.PlayedAtToExclusive.HasValue)
        {
            where.Add("played_at < ?");
            args.Add(request.PlayedAtToExclusive.Value);
        }

        string sql = "SELECT * FROM " + Lr2PlayHistorySchemaService.PlayHistoryTableName;
        if (where.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", where);
        }
        sql += " ORDER BY played_at DESC, history_id DESC";
        int limit = request.Limit.HasValue && request.Limit.Value > 0
            ? request.Limit.Value
            : DefaultReadLimit;
        if (limit > 0)
        {
            sql += " LIMIT ?";
            args.Add(limit);
        }
        sql += ";";
        return sql;
    }

    private static PlayHistoryDiagnostic CreateDiagnostic(PlayHistoryDiagnosticSeverity severity, string code, string message, string sourcePath)
    {
        return new PlayHistoryDiagnostic
        {
            Provider = PlayHistoryProvider.Lr2,
            Stage = "read",
            Severity = severity,
            Code = code ?? string.Empty,
            Message = message ?? string.Empty,
            SourcePath = sourcePath ?? string.Empty
        };
    }

    private static string GetSchemaDiagnosticCode(Lr2PlayHistorySchemaStatus status)
    {
        return status switch
        {
            Lr2PlayHistorySchemaStatus.Unreadable => "play_history_lr2_schema_unreadable",
            Lr2PlayHistorySchemaStatus.NotInstalled => "play_history_lr2_schema_not_installed",
            Lr2PlayHistorySchemaStatus.ManualRepairRequired => "play_history_lr2_schema_manual_repair_required",
            _ => "play_history_lr2_schema_" + status.ToString().ToLowerInvariant(),
        };
    }
}
