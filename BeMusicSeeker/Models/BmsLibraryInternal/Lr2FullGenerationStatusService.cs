using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum Lr2FullGenerationStatusKind
{
    NotNeeded,
    Needed,
    Running,
    Completed,
    Failed,
    Cancelled,
    Incomplete
}

internal sealed class Lr2FullGenerationStatusSnapshot
{
    public Lr2FullGenerationStatusKind Status { get; set; }

    public Lr2FullGenerationStatusKind? StoredStatus { get; set; }

    public string Signature { get; set; }

    public string RunId { get; set; }

    public int? ProcessedCursor { get; set; }

    public int? TotalCount { get; set; }

    public string Stage { get; set; }

    public string LastError { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public bool IsNeeded => Status == Lr2FullGenerationStatusKind.Needed;

    internal Lr2FullGenerationStatusSnapshot Clone()
    {
        return new Lr2FullGenerationStatusSnapshot
        {
            Status = Status,
            StoredStatus = StoredStatus,
            Signature = Signature,
            RunId = RunId,
            ProcessedCursor = ProcessedCursor,
            TotalCount = TotalCount,
            Stage = Stage,
            LastError = LastError,
            UpdatedAt = UpdatedAt,
            CompletedAt = CompletedAt
        };
    }
}

internal static class Lr2FullGenerationStatusService
{
    internal const string DefaultStatusName = "default";

    internal static Lr2FullGenerationStatusSnapshot Evaluate(
        LR2SongDBExtended songDb,
        bool enabled,
        string signature,
        DateTime nowUtc)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        BmsLibraryDbGateway.EnsureLr2FullGenerationStatusSchema(songDb);
        if (!enabled)
        {
            return CreateSnapshot(
                Lr2FullGenerationStatusKind.NotNeeded,
                storedStatus: null,
                row: LoadRow(songDb),
                signature,
                nowUtc);
        }

        LR2SongDBExtended.lr2_full_generation_status row = LoadRow(songDb);
        if (row == null)
        {
            return CreateSnapshot(
                Lr2FullGenerationStatusKind.Needed,
                storedStatus: null,
                row: null,
                signature,
                nowUtc);
        }

        Lr2FullGenerationStatusKind storedStatus = ParseStatus(row.status);
        if (!string.Equals(row.signature ?? string.Empty, signature ?? string.Empty, StringComparison.Ordinal))
        {
            return CreateSnapshot(
                Lr2FullGenerationStatusKind.Needed,
                storedStatus,
                row,
                signature,
                nowUtc);
        }

        if (storedStatus == Lr2FullGenerationStatusKind.Completed)
        {
            return CreateSnapshot(
                Lr2FullGenerationStatusKind.Completed,
                storedStatus,
                row,
                signature,
                nowUtc);
        }

        return CreateSnapshot(
            Lr2FullGenerationStatusKind.Needed,
            storedStatus,
            row,
            signature,
            nowUtc);
    }

    internal static Lr2FullGenerationStatusSnapshot MarkRunning(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? totalCount,
        string stage,
        DateTime nowUtc)
    {
        return Upsert(
            songDb,
            Lr2FullGenerationStatusKind.Running,
            signature,
            runId,
            processedCursor: 0,
            totalCount,
            stage,
            lastError: null,
            completedAt: null,
            nowUtc);
    }

    internal static Lr2FullGenerationStatusSnapshot UpdateCursor(
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
            Lr2FullGenerationStatusKind.Running,
            signature,
            runId,
            Math.Max(0, processedCursor),
            totalCount,
            stage,
            lastError: null,
            completedAt: null,
            nowUtc);
    }

    internal static Lr2FullGenerationStatusSnapshot MarkCompleted(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? totalCount,
        DateTime nowUtc)
    {
        return Upsert(
            songDb,
            Lr2FullGenerationStatusKind.Completed,
            signature,
            runId,
            totalCount,
            totalCount,
            stage: "completed",
            lastError: null,
            completedAt: nowUtc,
            nowUtc);
    }

    internal static Lr2FullGenerationStatusSnapshot MarkFailed(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? processedCursor,
        int? totalCount,
        string stage,
        string error,
        DateTime nowUtc)
    {
        return Upsert(songDb, Lr2FullGenerationStatusKind.Failed, signature, runId, processedCursor, totalCount, stage, error, completedAt: null, nowUtc);
    }

    internal static Lr2FullGenerationStatusSnapshot MarkCancelled(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? processedCursor,
        int? totalCount,
        string stage,
        DateTime nowUtc)
    {
        return Upsert(songDb, Lr2FullGenerationStatusKind.Cancelled, signature, runId, processedCursor, totalCount, stage, lastError: null, completedAt: null, nowUtc);
    }

    internal static Lr2FullGenerationStatusSnapshot MarkIncomplete(
        LR2SongDBExtended songDb,
        string signature,
        string runId,
        int? processedCursor,
        int? totalCount,
        string stage,
        string detail,
        DateTime nowUtc)
    {
        return Upsert(songDb, Lr2FullGenerationStatusKind.Incomplete, signature, runId, processedCursor, totalCount, stage, detail, completedAt: null, nowUtc);
    }

    private static Lr2FullGenerationStatusSnapshot Upsert(
        LR2SongDBExtended songDb,
        Lr2FullGenerationStatusKind status,
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

        BmsLibraryDbGateway.EnsureLr2FullGenerationStatusSchema(songDb);
        var row = new LR2SongDBExtended.lr2_full_generation_status
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
        songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.lr2_full_generation_status));
        return CreateSnapshot(status, status, row, signature, nowUtc);
    }

    private static LR2SongDBExtended.lr2_full_generation_status LoadRow(LR2SongDBExtended songDb)
    {
        return songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(DefaultStatusName);
    }

    private static Lr2FullGenerationStatusSnapshot CreateSnapshot(
        Lr2FullGenerationStatusKind status,
        Lr2FullGenerationStatusKind? storedStatus,
        LR2SongDBExtended.lr2_full_generation_status row,
        string signature,
        DateTime nowUtc)
    {
        return new Lr2FullGenerationStatusSnapshot
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

    private static Lr2FullGenerationStatusKind ParseStatus(string status)
    {
        return Enum.TryParse(status ?? string.Empty, ignoreCase: true, out Lr2FullGenerationStatusKind parsed)
            ? parsed
            : Lr2FullGenerationStatusKind.Needed;
    }

    private static string FormatStatus(Lr2FullGenerationStatusKind status)
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
