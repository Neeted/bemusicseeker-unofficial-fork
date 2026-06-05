using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal static class Lr2FullGenerationStatusMapper
{
    internal static Lr2FullGenerationRuntimeStatus CreateNone()
    {
        return new Lr2FullGenerationRuntimeStatus
        {
            Kind = Lr2FullGenerationStatusKind.NotNeeded,
            StatusText = string.Empty,
            Detail = string.Empty,
            HasWarningStatus = false,
            CanRetry = false,
            CanCancel = false,
            CanCleanupStartupScanBlockers = false,
            CheckedAt = DateTime.MinValue
        };
    }

    internal static Lr2FullGenerationRuntimeStatus Create(Lr2FullGenerationStatusSnapshot snapshot, DateTime checkedAt)
    {
        if (snapshot == null)
        {
            return CreateNone();
        }

        string statusText = GetStatusText(snapshot.Status);
        return new Lr2FullGenerationRuntimeStatus
        {
            Kind = snapshot.Status,
            StatusText = statusText,
            Detail = BuildDetail(snapshot, statusText, checkedAt),
            HasWarningStatus = HasWarningStatus(snapshot.Status),
            CanRetry = CanRetry(snapshot.Status),
            CanCancel = CanCancel(snapshot.Status),
            CanCleanupStartupScanBlockers = CanCleanupStartupScanBlockers(snapshot),
            CheckedAt = checkedAt
        };
    }

    private static bool HasWarningStatus(Lr2FullGenerationStatusKind kind)
    {
        return kind == Lr2FullGenerationStatusKind.Needed
            || kind == Lr2FullGenerationStatusKind.Running
            || kind == Lr2FullGenerationStatusKind.Failed
            || kind == Lr2FullGenerationStatusKind.Incomplete
            || kind == Lr2FullGenerationStatusKind.Cancelled;
    }

    private static bool CanRetry(Lr2FullGenerationStatusKind kind)
    {
        return kind == Lr2FullGenerationStatusKind.Needed
            || kind == Lr2FullGenerationStatusKind.Failed
            || kind == Lr2FullGenerationStatusKind.Incomplete
            || kind == Lr2FullGenerationStatusKind.Cancelled;
    }

    private static bool CanCancel(Lr2FullGenerationStatusKind kind)
    {
        return kind == Lr2FullGenerationStatusKind.Running;
    }

    private static bool CanCleanupStartupScanBlockers(Lr2FullGenerationStatusSnapshot snapshot)
    {
        return snapshot?.Status == Lr2FullGenerationStatusKind.Incomplete
            && string.Equals(snapshot.Stage, Lr2FullGenerationBackfillService.StartupScanBlockersStage, StringComparison.Ordinal);
    }

    private static string GetStatusText(Lr2FullGenerationStatusKind kind)
    {
        return kind switch
        {
            Lr2FullGenerationStatusKind.Needed => Resources.Lr2_full_generation_status_needed,
            Lr2FullGenerationStatusKind.Running => Resources.Lr2_full_generation_status_running,
            Lr2FullGenerationStatusKind.Completed => Resources.Lr2_full_generation_status_completed,
            Lr2FullGenerationStatusKind.Failed => Resources.Lr2_full_generation_status_failed,
            Lr2FullGenerationStatusKind.Cancelled => Resources.Lr2_full_generation_status_incomplete,
            Lr2FullGenerationStatusKind.Incomplete => Resources.Lr2_full_generation_status_incomplete,
            _ => string.Empty,
        };
    }

    private static string BuildDetail(Lr2FullGenerationStatusSnapshot snapshot, string statusText, DateTime checkedAt)
    {
        string timestampText = checkedAt == DateTime.MinValue ? string.Empty : checkedAt.ToString("yyyy/MM/dd HH:mm:ss");
        string stageText = string.IsNullOrWhiteSpace(snapshot.Stage) ? string.Empty : snapshot.Stage.Replace('_', ' ');
        string countText = snapshot.TotalCount.GetValueOrDefault() > 0
            ? "[" + snapshot.ProcessedCursor.GetValueOrDefault() + "/" + snapshot.TotalCount.GetValueOrDefault() + "]"
            : string.Empty;
        return JoinNonEmptyLines(statusText, timestampText, countText, stageText, snapshot.LastError);
    }

    private static string JoinNonEmptyLines(params string[] values)
    {
        return string.Join(Environment.NewLine, Array.FindAll(values ?? [], value => !string.IsNullOrWhiteSpace(value)));
    }
}
