using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>LR2の段階識別子を表示リソースへ変換し、保存位置と段階進捗を区別して表示します。</summary>
internal static class Lr2SongDbSyncStatusMapper
{
    /// <summary>表示対象がない状態を作ります。</summary>
    internal static Lr2SongDbSyncRuntimeStatus CreateNone()
    {
        return new Lr2SongDbSyncRuntimeStatus
        {
            Kind = Lr2SongDbSyncStatusKind.NotNeeded,
            CheckedAt = DateTime.MinValue
        };
    }

    /// <summary>実処理の状態の事実を保持します。文言は読取り時に現在の資源から作り、検査日時はGregorianの固定書式に揃え、既知の旧段階は翻訳し、未知の保存済み識別子は診断に残します。</summary>
    internal static Lr2SongDbSyncRuntimeStatus Create(Lr2SongDbSyncStatusSnapshot snapshot, DateTime checkedAt, OperationProgressRequest request = null)
    {
        if (snapshot == null)
        {
            return CreateNone();
        }

        return new Lr2SongDbSyncRuntimeStatus
        {
            Kind = snapshot.Status,
            Request = request,
            Stage = snapshot.Stage,
            StageProcessedCount = snapshot.StageProcessedCount,
            StageTotalCount = snapshot.StageTotalCount,
            SavedProcessedCount = snapshot.ProcessedCursor,
            SavedTotalCount = snapshot.TotalCount,
            LastError = snapshot.LastError,
            CheckedAt = checkedAt
        };
    }

    /// <summary>状態種別の現在語を返します。</summary>
    internal static string GetStatusText(Lr2SongDbSyncStatusKind kind)
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

    /// <summary>保持した事実から現在語の詳細文を作ります。</summary>
    internal static string BuildDetail(Lr2SongDbSyncRuntimeStatus status)
    {
        string timestampText = status.CheckedAt == DateTime.MinValue ? string.Empty : DateTimeDisplayFormatter.FormatTimestamp(status.CheckedAt);
        string stageText = GetStageText(status.Stage);
        string countText = status.SavedTotalCount.GetValueOrDefault() > 0
            ? string.Format(Resources.Lr2_song_db_sync_saved_position, status.SavedProcessedCount.GetValueOrDefault(), status.SavedTotalCount.GetValueOrDefault())
            : string.Empty;
        return JoinNonEmptyLines(timestampText, countText, stageText, GetErrorText(status.Stage, status.LastError));
    }

    /// <summary>保持した事実から現在語の段階進捗文を作ります。</summary>
    internal static string BuildProgressText(Lr2SongDbSyncRuntimeStatus status)
    {
        string stageText = GetStageText(status.Stage);
        int stageTotalCount = status.StageTotalCount.GetValueOrDefault();
        if (stageTotalCount > 0)
        {
            string countText = "[" + status.StageProcessedCount.GetValueOrDefault() + "/" + stageTotalCount + "]";
            return string.IsNullOrWhiteSpace(stageText) ? countText : stageText + " " + countText;
        }
        return stageText;
    }

    private static string GetStageText(string stage)
    {
        return GetKnownStageText(stage) ?? (string.IsNullOrWhiteSpace(stage) ? string.Empty : stage);
    }

    // 保存診断は変更せず、既知の段階と一致する既定の先頭ラベルだけを表示時に置き換える。
    private static string GetErrorText(string stage, string error)
    {
        string stageText = GetKnownStageText(stage);
        string prefix = stage + ": ";
        return stageText != null && !string.IsNullOrWhiteSpace(stage)
            && error?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? stageText + ": " + error[prefix.Length..]
            : error;
    }

    private static string GetKnownStageText(string stage)
    {
        return stage switch
        {
            null => string.Empty,
            "queued" => Resources.Lr2_song_db_sync_stage_queued,
            "preparing" or "input_preparation" => Resources.Lr2_song_db_sync_stage_preparing,
            "playlist_preparation" or "playlist_materialization" => Resources.Lr2_song_db_sync_stage_playlist_materialization,
            "playlist_projection" => Resources.Lr2_song_db_sync_stage_playlist_projection,
            "playlist_output_discovery" => Resources.Lr2_song_db_sync_stage_playlist_output_discovery,
            "playlist_files" => Resources.Lr2_song_db_sync_stage_playlist_files,
            "playlist_output_cleanup" => Resources.Lr2_song_db_sync_stage_playlist_output_cleanup,
            "playlist_directory_metadata" => Resources.Lr2_song_db_sync_stage_playlist_directory_metadata,
            "playlist_state_saving" => Resources.Lr2_song_db_sync_stage_playlist_state_saving,
            "playlist_result_preparation" => Resources.Lr2_song_db_sync_stage_playlist_result_preparation,
            "builtin_folder_preparation" => Resources.Lr2_song_db_sync_stage_builtin_folder_preparation,
            "chart_info_hydration" => Resources.Lr2_song_db_sync_stage_chart_info_hydration,
            "input_surface" => Resources.Lr2_song_db_sync_stage_input_surface,
            "compatibility_projection_index" => Resources.Lr2_song_db_sync_stage_compatibility_projection_index,
            "chart_info_resolver_snapshot" => Resources.Lr2_song_db_sync_stage_chart_info_resolver_snapshot,
            "folder_projection_preparation" or "normal_folder_discovery" => Resources.Lr2_song_db_sync_stage_folder_projection_preparation,
            "directory_metadata" => Resources.Lr2_song_db_sync_stage_directory_metadata,
            "normal_folder_roots" => Resources.Lr2_song_db_sync_stage_normal_folder_roots,
            "normal_folders" or "normal_folders_completed" => Resources.Lr2_song_db_sync_stage_normal_folders,
            "lr2folder_files" or "lr2folder_files_completed" => Resources.Lr2_song_db_sync_stage_lr2folder_files,
            "folder_projection_candidates" => Resources.Lr2_song_db_sync_stage_folder_projection_candidates,
            "custom_folder_rows" => Resources.Lr2_song_db_sync_stage_custom_folder_rows,
            "custom_folder_parents" => Resources.Lr2_song_db_sync_stage_custom_folder_parents,
            "folder_projection_validation" => Resources.Lr2_song_db_sync_stage_folder_projection_validation,
            "folder_existing_rows" => Resources.Lr2_song_db_sync_stage_folder_existing_rows,
            "folder_rows" or "folder_reconciliation" or "folder_reconciliation_completed" => Resources.Lr2_song_db_sync_stage_folder_reconciliation,
            "folder_saving" => Resources.Lr2_song_db_sync_stage_folder_saving,
            "song_rows_preparation" => Resources.Lr2_song_db_sync_stage_song_rows_preparation,
            "song_rows" or "song_rows_completed" => Resources.Lr2_song_db_sync_stage_song_rows,
            "song_rows_saving" => Resources.Lr2_song_db_sync_stage_song_rows_saving,
            "final_validation" => Resources.Lr2_song_db_sync_stage_final_validation,
            "sync_state_saving" => Resources.Lr2_song_db_sync_stage_sync_state_saving,
            "source_stale" => Resources.Lr2_song_db_sync_stage_source_stale,
            "lr2_playlist_lr2folder_sync_failed" or "lr2_normal_folder_file_diff_sync_failed"
                or "lr2_normal_folder_mutation_sync_failed" or "lr2folder_file_diff_sync_failed"
                or "lr2folder_settings_output_base_sync_failed" => Resources.Lr2_song_db_sync_stage_folder_saving,
            "lr2_song_db_file_diff_write_failed" or "lr2_song_db_install_target_upsert_failed"
                or "lr2_song_db_maintenance_write_failed" or "lr2_song_db_write_failed" => Resources.Lr2_song_db_sync_stage_song_rows_saving,
            "lr2_song_db_chart_info_inline_upsert_failed" => Resources.Lr2_song_db_sync_stage_chart_info_saving,
            "lr2_song_db_mode_upsert_failed" => Resources.Lr2_song_db_sync_stage_song_type_saving,
            "lr2_song_db_playlist_level_update_failed" => Resources.Lr2_song_db_sync_stage_playlist_level_saving,
            "lr2_song_db_library_mutation_path_replace_failed" => Resources.Lr2_song_db_sync_stage_song_location_update,
            "lr2_song_db_library_mutation_removal_failed" => Resources.Lr2_song_db_sync_stage_song_removal,
            "lr2_song_db_encoding_upsert_failed" => Resources.Lr2_song_db_sync_stage_song_encoding_saving,
            "completed" => Resources.Lr2_song_db_sync_status_completed,
            "preparation_failed" or "failed" => Resources.Lr2_song_db_sync_status_failed,
            "cancelled" or "shutdown_skipped" => Resources.Lr2_song_db_sync_status_incomplete,
            _ => null,
        };
    }

    private static string JoinNonEmptyLines(params string[] values)
    {
        return string.Join(Environment.NewLine, Array.FindAll(values ?? [], value => !string.IsNullOrWhiteSpace(value)));
    }
}
