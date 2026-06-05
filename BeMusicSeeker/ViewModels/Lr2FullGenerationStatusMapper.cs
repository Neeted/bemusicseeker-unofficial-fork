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
            ProgressText = string.Empty,
            ProgressValue = 0.0,
            ProgressMaximum = 1.0,
            HasProgress = false,
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
            Detail = BuildDetail(snapshot, checkedAt),
            ProgressText = BuildProgressText(snapshot),
            ProgressValue = Math.Max(0.0, snapshot.StageProcessedCount.GetValueOrDefault()),
            ProgressMaximum = Math.Max(1.0, snapshot.StageTotalCount.GetValueOrDefault()),
            HasProgress = HasProgress(snapshot),
            HasWarningStatus = HasWarningStatus(snapshot.Status),
            CanRetry = CanRetry(snapshot),
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

    private static bool CanRetry(Lr2FullGenerationStatusSnapshot snapshot)
    {
        if (CanCleanupStartupScanBlockers(snapshot))
        {
            return false;
        }

        Lr2FullGenerationStatusKind kind = snapshot?.Status ?? Lr2FullGenerationStatusKind.NotNeeded;
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

    private static string BuildDetail(Lr2FullGenerationStatusSnapshot snapshot, DateTime checkedAt)
    {
        string timestampText = checkedAt == DateTime.MinValue ? string.Empty : checkedAt.ToString("yyyy/MM/dd HH:mm:ss");
        string stageText = string.IsNullOrWhiteSpace(snapshot.Stage) ? string.Empty : snapshot.Stage.Replace('_', ' ');
        string countText = snapshot.TotalCount.GetValueOrDefault() > 0
            ? "[" + snapshot.ProcessedCursor.GetValueOrDefault() + "/" + snapshot.TotalCount.GetValueOrDefault() + "]"
            : string.Empty;
        return JoinNonEmptyLines(timestampText, countText, stageText, snapshot.LastError);
    }

    private static string BuildProgressText(Lr2FullGenerationStatusSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return string.Empty;
        }

        string stageText = string.IsNullOrWhiteSpace(snapshot.Stage) ? string.Empty : snapshot.Stage.Replace('_', ' ');
        int stageTotalCount = snapshot.StageTotalCount.GetValueOrDefault();
        if (stageTotalCount > 0)
        {
            string countText = "[" + snapshot.StageProcessedCount.GetValueOrDefault() + "/" + stageTotalCount + "]";
            return string.IsNullOrWhiteSpace(stageText) ? countText : stageText + " " + countText;
        }
        if (snapshot.TotalCount.GetValueOrDefault() > 0)
        {
            string countText = "[" + snapshot.ProcessedCursor.GetValueOrDefault() + "/" + snapshot.TotalCount.GetValueOrDefault() + "]";
            return string.IsNullOrWhiteSpace(stageText) ? countText : stageText + " " + countText;
        }
        return stageText;
    }

    private static bool HasProgress(Lr2FullGenerationStatusSnapshot snapshot)
    {
        return snapshot?.Status == Lr2FullGenerationStatusKind.Running
            && snapshot.StageTotalCount.GetValueOrDefault() > 0;
    }

    private static string JoinNonEmptyLines(params string[] values)
    {
        return string.Join(Environment.NewLine, Array.FindAll(values ?? [], value => !string.IsNullOrWhiteSpace(value)));
    }
}
