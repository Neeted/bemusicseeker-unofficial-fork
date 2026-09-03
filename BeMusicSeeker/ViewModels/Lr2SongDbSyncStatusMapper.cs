using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal static class Lr2SongDbSyncStatusMapper
{
    internal static Lr2SongDbSyncRuntimeStatus CreateNone()
    {
        return new Lr2SongDbSyncRuntimeStatus
        {
            Kind = Lr2SongDbSyncStatusKind.NotNeeded,
            StatusText = string.Empty,
            Detail = string.Empty,
            ProgressText = string.Empty,
            ProgressValue = 0.0,
            ProgressMaximum = 1.0,
            HasProgress = false,
            HasWarningStatus = false,
            CanRetry = false,
            CanCleanupStartupScanBlockers = false,
            CheckedAt = DateTime.MinValue
        };
    }

    internal static Lr2SongDbSyncRuntimeStatus Create(Lr2SongDbSyncStatusSnapshot snapshot, DateTime checkedAt)
    {
        if (snapshot == null)
        {
            return CreateNone();
        }

        string statusText = GetStatusText(snapshot.Status);
        return new Lr2SongDbSyncRuntimeStatus
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
            CanCleanupStartupScanBlockers = CanCleanupStartupScanBlockers(snapshot),
            CheckedAt = checkedAt
        };
    }

    private static bool HasWarningStatus(Lr2SongDbSyncStatusKind kind)
    {
        return kind == Lr2SongDbSyncStatusKind.Needed
            || kind == Lr2SongDbSyncStatusKind.Running
            || kind == Lr2SongDbSyncStatusKind.Failed
            || kind == Lr2SongDbSyncStatusKind.Incomplete
            || kind == Lr2SongDbSyncStatusKind.Cancelled;
    }

    private static bool CanRetry(Lr2SongDbSyncStatusSnapshot snapshot)
    {
        if (CanCleanupStartupScanBlockers(snapshot))
        {
            return false;
        }

        Lr2SongDbSyncStatusKind kind = snapshot?.Status ?? Lr2SongDbSyncStatusKind.NotNeeded;
        return kind == Lr2SongDbSyncStatusKind.Needed
            || kind == Lr2SongDbSyncStatusKind.Failed
            || kind == Lr2SongDbSyncStatusKind.Incomplete
            || kind == Lr2SongDbSyncStatusKind.Cancelled;
    }

    private static bool CanCleanupStartupScanBlockers(Lr2SongDbSyncStatusSnapshot snapshot)
    {
        return snapshot?.Status == Lr2SongDbSyncStatusKind.Incomplete
            && string.Equals(snapshot.Stage, Lr2SongDbSyncService.StartupScanBlockersStage, StringComparison.Ordinal);
    }

    private static string GetStatusText(Lr2SongDbSyncStatusKind kind)
    {
        return kind switch
        {
            Lr2SongDbSyncStatusKind.Needed => Resources.Lr2_song_db_sync_status_needed,
            Lr2SongDbSyncStatusKind.Running => Resources.Lr2_song_db_sync_status_running,
            Lr2SongDbSyncStatusKind.Completed => Resources.Lr2_song_db_sync_status_completed,
            Lr2SongDbSyncStatusKind.Failed => Resources.Lr2_song_db_sync_status_failed,
            Lr2SongDbSyncStatusKind.Cancelled => Resources.Lr2_song_db_sync_status_incomplete,
            Lr2SongDbSyncStatusKind.Incomplete => Resources.Lr2_song_db_sync_status_incomplete,
            _ => string.Empty,
        };
    }

    private static string BuildDetail(Lr2SongDbSyncStatusSnapshot snapshot, DateTime checkedAt)
    {
        string timestampText = checkedAt == DateTime.MinValue ? string.Empty : checkedAt.ToString("yyyy/MM/dd HH:mm:ss");
        string stageText = string.IsNullOrWhiteSpace(snapshot.Stage) ? string.Empty : snapshot.Stage.Replace('_', ' ');
        string countText = snapshot.TotalCount.GetValueOrDefault() > 0
            ? "[" + snapshot.ProcessedCursor.GetValueOrDefault() + "/" + snapshot.TotalCount.GetValueOrDefault() + "]"
            : string.Empty;
        return JoinNonEmptyLines(timestampText, countText, stageText, snapshot.LastError);
    }

    private static string BuildProgressText(Lr2SongDbSyncStatusSnapshot snapshot)
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

    private static bool HasProgress(Lr2SongDbSyncStatusSnapshot snapshot)
    {
        return snapshot?.Status == Lr2SongDbSyncStatusKind.Running
            && snapshot.StageTotalCount.GetValueOrDefault() > 0;
    }

    private static string JoinNonEmptyLines(params string[] values)
    {
        return string.Join(Environment.NewLine, Array.FindAll(values ?? [], value => !string.IsNullOrWhiteSpace(value)));
    }
}
