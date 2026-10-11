using System;
using System.Globalization;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncStatusMapperTests
{
    /// <summary>
    /// 再同期が必要な状態は警告として表示し、保存位置・工程・前回エラーを詳細に残して状態名との混同や原因の欠落を検出します。
    /// </summary>
    [TestMethod]
    public void Create_NeededBuildsWarningStatus()
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Needed,
            StoredStatus = Lr2SongDbSyncStatusKind.Failed,
            Stage = "song_rows",
            LastError = "failed before",
            ProcessedCursor = 12,
            TotalCount = 100
        }, new DateTime(2026, 6, 5, 12, 0, 0));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, status.Kind);
        Assert.AreEqual(Resources.Lr2_song_db_sync_status_needed, status.StatusText);
        Assert.IsTrue(status.HasWarningStatus);
        StringAssert.Contains(status.Detail, "[12/100]");
        StringAssert.Contains(status.Detail, Resources.Lr2_song_db_sync_stage_song_rows);
        StringAssert.Contains(status.Detail, "failed before");
        Assert.IsFalse(status.Detail.Contains(Resources.Lr2_song_db_sync_status_needed));
    }

    /// <summary>
    /// 完了状態を完了文言で表示して警告を解除し、成功後に注意状態を残す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Create_CompletedClearsWarningStatus()
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Completed,
            Stage = "completed"
        }, DateTime.MinValue);

        Assert.AreEqual(Resources.Lr2_song_db_sync_status_completed, status.StatusText);
        Assert.IsFalse(status.HasWarningStatus);
    }

    /// <summary>
    /// 処理中は保存済み位置と現在工程の件数を分けて表示し、途中・最終件数で進捗値と最大値を取り違える誤りを検出します。
    /// </summary>
    [DataTestMethod]
    [DataRow(4)]
    [DataRow(50)]
    public void Create_RunningSeparatesSavedPositionFromStageProgress(int processedCount)
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Running,
            Stage = "song_rows",
            ProcessedCursor = 12,
            TotalCount = 100,
            StageProcessedCount = processedCount,
            StageTotalCount = 50
        }, new DateTime(2026, 6, 5, 12, 0, 0));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Running, status.Kind);
        Assert.AreEqual(Resources.Lr2_song_db_sync_status_running, status.StatusText);
        Assert.IsTrue(status.HasWarningStatus);
        StringAssert.Contains(status.Detail, "[12/100]");
        StringAssert.Contains(status.Detail, Resources.Lr2_song_db_sync_stage_song_rows);
        Assert.AreEqual(Resources.Lr2_song_db_sync_stage_song_rows + " [" + processedCount + "/50]", status.ProgressText);
        Assert.IsTrue(status.HasProgress);
        Assert.AreEqual((double)processedCount, status.ProgressValue);
        Assert.AreEqual(50.0, status.ProgressMaximum);
    }

    /// <summary>
    /// 失敗と未完了を各状態名で警告表示し、それぞれの原因を詳細から落とす誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Create_FailedAndIncompleteAreWarningStatuses()
    {
        Lr2SongDbSyncRuntimeStatus failed = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Failed,
            LastError = "boom"
        }, DateTime.MinValue);
        Lr2SongDbSyncRuntimeStatus incomplete = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Incomplete,
            LastError = "startup scan blockers"
        }, DateTime.MinValue);

        Assert.IsTrue(failed.HasWarningStatus);
        Assert.AreEqual(Resources.Lr2_song_db_sync_status_failed, failed.StatusText);
        StringAssert.Contains(failed.Detail, "boom");
        Assert.IsTrue(incomplete.HasWarningStatus);
        Assert.AreEqual(Resources.Lr2_song_db_sync_status_incomplete, incomplete.StatusText);
        StringAssert.Contains(incomplete.Detail, "startup scan blockers");
    }

    /// <summary>
    /// 旧取消状態を未完了の警告表示として読み替え、保存済みの旧状態を正常完了と扱う互換性退行を検出します。
    /// </summary>
    [TestMethod]
    public void Create_LegacyCancelledDisplaysIncompleteWarning()
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Cancelled,
            Stage = "cancelled",
            LastError = "legacy status"
        }, DateTime.MinValue);

        Assert.AreEqual(Resources.Lr2_song_db_sync_status_incomplete, status.StatusText);
        Assert.IsTrue(status.HasWarningStatus);
    }

    /// <summary>
    /// 既知の保存工程を役割ごとの表示文言へ変換し、保存位置と元の例外・パス・内部原因を保持して生の識別名の露出や診断切捨てを検出します。
    /// </summary>
    [DataTestMethod]
    [DataRow("queued", nameof(Resources.Lr2_song_db_sync_stage_queued))]
    [DataRow("preparing", nameof(Resources.Lr2_song_db_sync_stage_preparing))]
    [DataRow("input_preparation", nameof(Resources.Lr2_song_db_sync_stage_preparing))]
    [DataRow("playlist_preparation", nameof(Resources.Lr2_song_db_sync_stage_playlist_materialization))]
    [DataRow("playlist_materialization", nameof(Resources.Lr2_song_db_sync_stage_playlist_materialization))]
    [DataRow("playlist_projection", nameof(Resources.Lr2_song_db_sync_stage_playlist_projection))]
    [DataRow("playlist_output_discovery", nameof(Resources.Lr2_song_db_sync_stage_playlist_output_discovery))]
    [DataRow("playlist_files", nameof(Resources.Lr2_song_db_sync_stage_playlist_files))]
    [DataRow("playlist_output_cleanup", nameof(Resources.Lr2_song_db_sync_stage_playlist_output_cleanup))]
    [DataRow("playlist_directory_metadata", nameof(Resources.Lr2_song_db_sync_stage_playlist_directory_metadata))]
    [DataRow("playlist_state_saving", nameof(Resources.Lr2_song_db_sync_stage_playlist_state_saving))]
    [DataRow("playlist_result_preparation", nameof(Resources.Lr2_song_db_sync_stage_playlist_result_preparation))]
    [DataRow("builtin_folder_preparation", nameof(Resources.Lr2_song_db_sync_stage_builtin_folder_preparation))]
    [DataRow("chart_info_hydration", nameof(Resources.Lr2_song_db_sync_stage_chart_info_hydration))]
    [DataRow("input_surface", nameof(Resources.Lr2_song_db_sync_stage_input_surface))]
    [DataRow("compatibility_projection_index", nameof(Resources.Lr2_song_db_sync_stage_compatibility_projection_index))]
    [DataRow("chart_info_resolver_snapshot", nameof(Resources.Lr2_song_db_sync_stage_chart_info_resolver_snapshot))]
    [DataRow("folder_projection_preparation", nameof(Resources.Lr2_song_db_sync_stage_folder_projection_preparation))]
    [DataRow("normal_folder_discovery", nameof(Resources.Lr2_song_db_sync_stage_folder_projection_preparation))]
    [DataRow("directory_metadata", nameof(Resources.Lr2_song_db_sync_stage_directory_metadata))]
    [DataRow("normal_folder_roots", nameof(Resources.Lr2_song_db_sync_stage_normal_folder_roots))]
    [DataRow("normal_folders", nameof(Resources.Lr2_song_db_sync_stage_normal_folders))]
    [DataRow("normal_folders_completed", nameof(Resources.Lr2_song_db_sync_stage_normal_folders))]
    [DataRow("lr2folder_files", nameof(Resources.Lr2_song_db_sync_stage_lr2folder_files))]
    [DataRow("lr2folder_files_completed", nameof(Resources.Lr2_song_db_sync_stage_lr2folder_files))]
    [DataRow("folder_projection_candidates", nameof(Resources.Lr2_song_db_sync_stage_folder_projection_candidates))]
    [DataRow("custom_folder_rows", nameof(Resources.Lr2_song_db_sync_stage_custom_folder_rows))]
    [DataRow("custom_folder_parents", nameof(Resources.Lr2_song_db_sync_stage_custom_folder_parents))]
    [DataRow("folder_projection_validation", nameof(Resources.Lr2_song_db_sync_stage_folder_projection_validation))]
    [DataRow("folder_existing_rows", nameof(Resources.Lr2_song_db_sync_stage_folder_existing_rows))]
    [DataRow("folder_rows", nameof(Resources.Lr2_song_db_sync_stage_folder_reconciliation))]
    [DataRow("folder_reconciliation", nameof(Resources.Lr2_song_db_sync_stage_folder_reconciliation))]
    [DataRow("folder_reconciliation_completed", nameof(Resources.Lr2_song_db_sync_stage_folder_reconciliation))]
    [DataRow("folder_saving", nameof(Resources.Lr2_song_db_sync_stage_folder_saving))]
    [DataRow("song_rows_preparation", nameof(Resources.Lr2_song_db_sync_stage_song_rows_preparation))]
    [DataRow("song_rows", nameof(Resources.Lr2_song_db_sync_stage_song_rows))]
    [DataRow("song_rows_completed", nameof(Resources.Lr2_song_db_sync_stage_song_rows))]
    [DataRow("song_rows_saving", nameof(Resources.Lr2_song_db_sync_stage_song_rows_saving))]
    [DataRow("final_validation", nameof(Resources.Lr2_song_db_sync_stage_final_validation))]
    [DataRow("sync_state_saving", nameof(Resources.Lr2_song_db_sync_stage_sync_state_saving))]
    [DataRow("source_stale", nameof(Resources.Lr2_song_db_sync_stage_source_stale))]
    [DataRow("completed", nameof(Resources.Lr2_song_db_sync_status_completed))]
    [DataRow("preparation_failed", nameof(Resources.Lr2_song_db_sync_status_failed))]
    [DataRow("failed", nameof(Resources.Lr2_song_db_sync_status_failed))]
    [DataRow("cancelled", nameof(Resources.Lr2_song_db_sync_status_incomplete))]
    [DataRow("shutdown_skipped", nameof(Resources.Lr2_song_db_sync_status_incomplete))]
    [DataRow("lr2_playlist_lr2folder_sync_failed", nameof(Resources.Lr2_song_db_sync_stage_folder_saving))]
    [DataRow("lr2_normal_folder_file_diff_sync_failed", nameof(Resources.Lr2_song_db_sync_stage_folder_saving))]
    [DataRow("lr2_normal_folder_mutation_sync_failed", nameof(Resources.Lr2_song_db_sync_stage_folder_saving))]
    [DataRow("lr2folder_file_diff_sync_failed", nameof(Resources.Lr2_song_db_sync_stage_folder_saving))]
    [DataRow("lr2folder_settings_output_base_sync_failed", nameof(Resources.Lr2_song_db_sync_stage_folder_saving))]
    [DataRow("lr2_song_db_file_diff_write_failed", nameof(Resources.Lr2_song_db_sync_stage_song_rows_saving))]
    [DataRow("lr2_song_db_install_target_upsert_failed", nameof(Resources.Lr2_song_db_sync_stage_song_rows_saving))]
    [DataRow("lr2_song_db_maintenance_write_failed", nameof(Resources.Lr2_song_db_sync_stage_song_rows_saving))]
    [DataRow("lr2_song_db_write_failed", nameof(Resources.Lr2_song_db_sync_stage_song_rows_saving))]
    [DataRow("lr2_song_db_chart_info_inline_upsert_failed", nameof(Resources.Lr2_song_db_sync_stage_chart_info_saving))]
    [DataRow("lr2_song_db_mode_upsert_failed", nameof(Resources.Lr2_song_db_sync_stage_song_type_saving))]
    [DataRow("lr2_song_db_playlist_level_update_failed", nameof(Resources.Lr2_song_db_sync_stage_playlist_level_saving))]
    [DataRow("lr2_song_db_library_mutation_path_replace_failed", nameof(Resources.Lr2_song_db_sync_stage_song_location_update))]
    [DataRow("lr2_song_db_library_mutation_removal_failed", nameof(Resources.Lr2_song_db_sync_stage_song_removal))]
    [DataRow("lr2_song_db_encoding_upsert_failed", nameof(Resources.Lr2_song_db_sync_stage_song_encoding_saving))]
    public void Create_KnownStageUsesItsRoleResourceInProgressAndDetail(string stage, string resourceKey)
    {
        string suffix = "SQLiteException: C:\\Library\\chart.bms (Parameter 'path')" + Environment.NewLine
            + "InnerException: " + stage + ": original detail";
        var snapshot = new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Running,
            Stage = stage,
            ProcessedCursor = 12,
            TotalCount = 100,
            LastError = stage + ": " + suffix
        };
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(snapshot, DateTime.MinValue);

        string? expected = Resources.ResourceManager.GetString(resourceKey, Resources.Culture);
        Assert.IsNotNull(expected);
        Assert.AreEqual(expected, status.ProgressText);
        StringAssert.Contains(status.Detail, expected);
        StringAssert.Contains(status.Detail, expected + ": " + suffix);
        StringAssert.Contains(status.Detail, string.Format(Resources.Lr2_song_db_sync_saved_position, 12, 100));
        Assert.IsFalse(status.HasProgress);
    }

    /// <summary>
    /// 工程の総数が正の場合だけ進捗を確定値にし、未定・零を保存済み全体件数で埋めて誤った進捗を表示する処理を検出します。
    /// </summary>
    [DataTestMethod]
    [DataRow(null)]
    [DataRow(0)]
    [DataRow(20)]
    public void Create_UsesOnlyStageCountsForProgress(int? stageTotal)
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Running,
            Stage = "folder_reconciliation",
            StageProcessedCount = 8,
            StageTotalCount = stageTotal,
            ProcessedCursor = 12,
            TotalCount = 100
        }, DateTime.MinValue);

        Assert.AreEqual(Resources.Lr2_song_db_sync_stage_folder_reconciliation + (stageTotal > 0 ? " [8/20]" : ""), status.ProgressText);
        Assert.AreEqual(stageTotal > 0, status.HasProgress);
        Assert.AreEqual(8.0, status.ProgressValue);
        if (stageTotal > 0)
        {
            Assert.AreEqual(20.0, status.ProgressMaximum);
        }
        StringAssert.Contains(status.Detail, string.Format(Resources.Lr2_song_db_sync_saved_position, 12, 100));
    }

    /// <summary>
    /// 準備の一件目を工程進捗だけに表示し、未保存の件数を保存済み位置として見せる誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Create_PreparationStageCountsDoNotAppearAsSavedPosition()
    {
        var snapshot = new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Running,
            Stage = "playlist_files",
            StageProcessedCount = 1,
            StageTotalCount = 3,
            ProcessedCursor = null,
            TotalCount = null
        };
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(snapshot, DateTime.MinValue);

        Assert.AreEqual(Resources.Lr2_song_db_sync_stage_playlist_files, status.Detail);
        Assert.AreEqual(string.Format("{0} [{1}/{2}]", Resources.Lr2_song_db_sync_stage_playlist_files,
            snapshot.StageProcessedCount, snapshot.StageTotalCount), status.ProgressText);
        Assert.IsTrue(status.HasProgress);
        Assert.AreEqual((double)snapshot.StageProcessedCount.GetValueOrDefault(), status.ProgressValue);
        Assert.AreEqual((double)snapshot.StageTotalCount.GetValueOrDefault(), status.ProgressMaximum);
    }

    /// <summary>
    /// 空工程には表示を推測せず、未知工程と元診断を保持して不明な保存状態を別工程へ読み替える誤りを検出します。
    /// </summary>
    [DataTestMethod]
    [DataRow(null, "")]
    [DataRow(" ", "")]
    [DataRow("future_saved_stage", "future_saved_stage")]
    public void Create_EmptyStageDoesNotGuessAndUnknownStageKeepsDiagnosticInformation(string? stage, string expected)
    {
        string originalDiagnostic = (stage ?? string.Empty) + ": original diagnostic";
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Incomplete,
            Stage = stage,
            LastError = originalDiagnostic
        }, DateTime.MinValue);

        Assert.AreEqual(expected, status.ProgressText);
        StringAssert.Contains(status.Detail, originalDiagnostic);
        if (expected.Length > 0)
        {
            StringAssert.Contains(status.Detail, expected);
        }
    }

    /// <summary>
    /// 既知工程でも異なる原因接頭辞と既定外の区切りを診断から削らず、工程名変換による元エラーの改変を検出します。
    /// </summary>
    [DataTestMethod]
    [DataRow("future_error: original detail")]
    [DataRow("lr2_song_db_mode_upsert_failed:original detail")]
    public void Create_KnownStagePreservesNondefaultErrorPrefixes(string error)
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Incomplete,
            Stage = "lr2_song_db_mode_upsert_failed",
            LastError = error
        }, DateTime.MinValue);

        Assert.AreEqual(Resources.Lr2_song_db_sync_stage_song_type_saving, status.ProgressText);
        Assert.AreEqual(Resources.Lr2_song_db_sync_stage_song_type_saving + Environment.NewLine + error, status.Detail);
    }

    /// <summary>
    /// 日本語と英語の実辞書へ切替えると同じ工程と進捗が選択言語で表示され、固定言語の再利用や翻訳経路の未接続を検出します。
    /// </summary>
    /// <remarks>共有表示文化圏を変更して復元するため、このメソッドだけ並列実行を止めます。</remarks>
    [DoNotParallelize]
    [TestMethod]
    public void Create_LanguageSwitchUsesActualJapaneseAndEnglishStageResources()
    {
        CultureInfo previousCulture = Resources.Culture;
        try
        {
            string? japanese = null;
            foreach (string culture in new[] { "ja-JP", "en-US" })
            {
                Resources.Culture = CultureInfo.GetCultureInfo(culture);
                Assert.IsTrue(JsonLanguageCatalog.TryGetString(culture, nameof(Resources.Lr2_song_db_sync_stage_song_rows), out string expected));
                Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
                {
                    Status = Lr2SongDbSyncStatusKind.Running,
                    Stage = "song_rows",
                    StageProcessedCount = 2,
                    StageTotalCount = 5
                }, DateTime.MinValue);
                Assert.AreEqual(expected + " [2/5]", status.ProgressText);
                Assert.AreEqual(expected, status.Detail);
                if (japanese == null)
                {
                    japanese = expected;
                }
                else
                {
                    Assert.AreNotEqual(japanese, expected);
                }
            }
        }
        finally
        {
            Resources.Culture = previousCulture;
        }
    }
}
