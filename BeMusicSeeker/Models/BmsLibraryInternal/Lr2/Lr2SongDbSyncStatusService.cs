using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum Lr2SongDbSyncStatusKind
{
    NotNeeded,
    Needed,
    Running,
    Completed,
    Failed,
    Cancelled,
    Incomplete
}

internal sealed class Lr2SongDbSyncStatusSnapshot
{
    public Lr2SongDbSyncStatusKind Status { get; set; }

    public Lr2SongDbSyncStatusKind? StoredStatus { get; set; }

    public string Signature { get; set; }

    public string RunId { get; set; }

    public int? ProcessedCursor { get; set; }

    public int? TotalCount { get; set; }

    public string Stage { get; set; }

    public int? StageProcessedCount { get; set; }

    public int? StageTotalCount { get; set; }

    public string LastError { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public bool IsNeeded => Status == Lr2SongDbSyncStatusKind.Needed;

    internal Lr2SongDbSyncStatusSnapshot Clone()
    {
        return new Lr2SongDbSyncStatusSnapshot
        {
            Status = Status,
            StoredStatus = StoredStatus,
            Signature = Signature,
            RunId = RunId,
            ProcessedCursor = ProcessedCursor,
            TotalCount = TotalCount,
            Stage = Stage,
            StageProcessedCount = StageProcessedCount,
            StageTotalCount = StageTotalCount,
            LastError = LastError,
            UpdatedAt = UpdatedAt,
            CompletedAt = CompletedAt
        };
    }
}

internal static class Lr2SongDbSyncStatusService
{
    internal const string DefaultStatusName = "default";

    internal static Lr2SongDbSyncStatusSnapshot Evaluate(
        LR2SongDBExtended songDb,
        bool enabled,
        string signature,
        DateTime nowUtc)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        BmsLibraryDbGateway.EnsureLr2SongDbSyncStatusSchema(songDb);
        if (!enabled)
        {
            return CreateSnapshot(
                Lr2SongDbSyncStatusKind.NotNeeded,
                storedStatus: null,
                row: LoadRow(songDb),
                signature,
                nowUtc);
        }

        LR2SongDBExtended.lr2_song_db_sync_status row = LoadRow(songDb);
        if (row == null)
        {
            return CreateSnapshot(
                Lr2SongDbSyncStatusKind.Needed,
                storedStatus: null,
                row: null,
                signature,
                nowUtc);
        }

        Lr2SongDbSyncStatusKind storedStatus = ParseStatus(row.status);
        if (!string.Equals(row.signature ?? string.Empty, signature ?? string.Empty, StringComparison.Ordinal))
        {
            return CreateSnapshot(
                Lr2SongDbSyncStatusKind.Needed,
                storedStatus,
                row,
                signature,
                nowUtc);
        }

        if (storedStatus == Lr2SongDbSyncStatusKind.Completed)
        {
            return CreateSnapshot(
                Lr2SongDbSyncStatusKind.Completed,
                storedStatus,
                row,
                signature,
                nowUtc);
        }

        // A durable cursor is retained for progress reporting and backwards
        // compatibility only.  It is never an input to the next run.
        return CreateSnapshot(
            Lr2SongDbSyncStatusKind.Needed,
            storedStatus,
            row,
            signature,
            nowUtc);
    }

    internal static Lr2SongDbSyncStatusSnapshot MarkRunning(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? totalCount,
        string stage,
        DateTime nowUtc,
        int? processedCursor = null)
    {
        return Upsert(
            songDb,
            Lr2SongDbSyncStatusKind.Running,
            signature,
            runId,
            // The optional argument remains source-compatible with older
            // callers, but a new run always starts at the first input item.
            processedCursor: 0,
            totalCount,
            stage,
            lastError: null,
            completedAt: null,
            nowUtc);
    }

    internal static Lr2SongDbSyncStatusSnapshot UpdateCursor(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int processedCursor,
        int? totalCount,
        string stage,
        DateTime nowUtc)
    {
        return Upsert(
            songDb,
            Lr2SongDbSyncStatusKind.Running,
            signature,
            runId,
            Math.Max(0, processedCursor),
            totalCount,
            stage,
            lastError: null,
            completedAt: null,
            nowUtc);
    }

    internal static Lr2SongDbSyncStatusSnapshot MarkCompleted(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? totalCount,
        DateTime nowUtc)
    {
        return Upsert(
            songDb,
            Lr2SongDbSyncStatusKind.Completed,
            signature,
            runId,
            totalCount,
            totalCount,
            stage: "completed",
            lastError: null,
            completedAt: nowUtc,
            nowUtc);
    }

    internal static Lr2SongDbSyncStatusSnapshot MarkFailed(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? processedCursor,
        int? totalCount,
        string stage,
        string error,
        DateTime nowUtc)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        BmsLibraryDbGateway.EnsureLr2SongDbSyncStatusSchema(songDb);
        LR2SongDBExtended.lr2_song_db_sync_status existing = LoadRow(songDb);
        if (existing != null
            && string.Equals(existing.signature ?? string.Empty, signature ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(existing.run_id ?? string.Empty, runId ?? string.Empty, StringComparison.Ordinal))
        {
            processedCursor ??= existing.processed_cursor;
            totalCount ??= existing.total_count;
        }
        return Upsert(songDb, Lr2SongDbSyncStatusKind.Failed, signature, runId, processedCursor, totalCount, stage, error, completedAt: null, nowUtc);
    }

    internal static Lr2SongDbSyncStatusSnapshot MarkIncomplete(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? processedCursor,
        int? totalCount,
        string stage,
        string detail,
        DateTime nowUtc)
    {
        return Upsert(songDb, Lr2SongDbSyncStatusKind.Incomplete, signature, runId, processedCursor, totalCount, stage, detail, completedAt: null, nowUtc);
    }

    private static Lr2SongDbSyncStatusSnapshot Upsert(
        LR2SongDBExtended songDb,
        Lr2SongDbSyncStatusKind status,
        string signature,
        string runId,
        int? processedCursor,
        int? totalCount,
        string stage,
        string lastError,
        DateTime? completedAt,
        DateTime nowUtc)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        BmsLibraryDbGateway.EnsureLr2SongDbSyncStatusSchema(songDb);
        var row = new LR2SongDBExtended.lr2_song_db_sync_status
        {
            name = DefaultStatusName,
            status = FormatStatus(status),
            signature = signature ?? string.Empty,
            run_id = runId ?? string.Empty,
            processed_cursor = processedCursor,
            total_count = totalCount,
            stage = stage ?? string.Empty,
            last_error = lastError ?? string.Empty,
            updated_at = NormalizeUtc(nowUtc),
            completed_at = completedAt.HasValue ? NormalizeUtc(completedAt.Value) : null
        };
        songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.lr2_song_db_sync_status));
        return CreateSnapshot(status, status, row, signature, nowUtc);
    }

    private static LR2SongDBExtended.lr2_song_db_sync_status LoadRow(LR2SongDBExtended songDb)
    {
        return songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(DefaultStatusName);
    }

    private static Lr2SongDbSyncStatusSnapshot CreateSnapshot(
        Lr2SongDbSyncStatusKind status,
        Lr2SongDbSyncStatusKind? storedStatus,
        LR2SongDBExtended.lr2_song_db_sync_status row,
        string signature,
        DateTime nowUtc)
    {
        return new Lr2SongDbSyncStatusSnapshot
        {
            Status = status,
            StoredStatus = storedStatus,
            Signature = signature ?? row?.signature ?? string.Empty,
            RunId = row?.run_id ?? string.Empty,
            ProcessedCursor = row?.processed_cursor,
            TotalCount = row?.total_count,
            Stage = row?.stage ?? string.Empty,
            LastError = row?.last_error ?? string.Empty,
            UpdatedAt = row?.updated_at == default ? NormalizeUtc(nowUtc) : row.updated_at,
            CompletedAt = row?.completed_at
        };
    }

    private static Lr2SongDbSyncStatusKind ParseStatus(string status)
    {
        return Enum.TryParse(status ?? string.Empty, ignoreCase: true, out Lr2SongDbSyncStatusKind parsed)
            ? parsed
            : Lr2SongDbSyncStatusKind.Needed;
    }

    private static string FormatStatus(Lr2SongDbSyncStatusKind status)
    {
        return status.ToString();
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
    }
}
