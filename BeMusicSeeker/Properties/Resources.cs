using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models.Localization;

namespace BeMusicSeeker.Properties;

[GeneratedCode("System.Resources.Tools.StronglyTypedResourceBuilder", "15.0.0.0")]
[DebuggerNonUserCode]
[CompilerGenerated]
public class Resources
{
    private static ResourceManager resourceMan;

    private static CultureInfo resourceCulture;

    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static ResourceManager ResourceManager
    {
        get
        {
            if (resourceMan == null)
            {
                resourceMan = new JsonBackedResourceManager("BeMusicSeeker.Properties.Resources", typeof(Resources).Assembly);
            }
            return resourceMan;
        }
    }

    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static CultureInfo Culture
    {
        get
        {
            return resourceCulture;
        }
        set
        {
            resourceCulture = value;
        }
    }

    public static string Add => ResourceManager.GetString("Add", resourceCulture);

    public static string Add_ignore => ResourceManager.GetString("Add_ignore", resourceCulture);

    public static string Add_root_folder => ResourceManager.GetString("Add_root_folder", resourceCulture);

    public static string AddDate => ResourceManager.GetString("AddDate", resourceCulture);

    public static string Advanced_settings => ResourceManager.GetString("Advanced_settings", resourceCulture);

    public static string Advanced_features => ResourceManager.GetString("Advanced_features", resourceCulture);

    public static string Appearance => ResourceManager.GetString("Appearance", resourceCulture);

    public static string Appearance_theme => ResourceManager.GetString("Appearance_theme", resourceCulture);

    public static string Appearance_theme_dark => ResourceManager.GetString("Appearance_theme_dark", resourceCulture);

    public static string Appearance_theme_light => ResourceManager.GetString("Appearance_theme_light", resourceCulture);

    public static string Artist => ResourceManager.GetString("Artist", resourceCulture);

    public static string Auto => ResourceManager.GetString("Auto", resourceCulture);

    public static string Backup => ResourceManager.GetString("Backup", resourceCulture);

    public static string Backup_and_restore => ResourceManager.GetString("Backup_and_restore", resourceCulture);

    public static string Backup_desc => ResourceManager.GetString("Backup_desc", resourceCulture);

    public static string Backup_lr2backup_optimize => ResourceManager.GetString("Backup_lr2backup_optimize", resourceCulture);

    public static string Backup_lr2backup_saveto => ResourceManager.GetString("Backup_lr2backup_saveto", resourceCulture);

    public static string Backup_numgen => ResourceManager.GetString("Backup_numgen", resourceCulture);

    public static string Backup_playlist => ResourceManager.GetString("Backup_playlist", resourceCulture);

    public static string Backup_restore_playlist => ResourceManager.GetString("Backup_restore_playlist", resourceCulture);

    public static string Backup_unistall => ResourceManager.GetString("Backup_unistall", resourceCulture);

    public static string Backup_unistall_desc => ResourceManager.GetString("Backup_unistall_desc", resourceCulture);

    public static string Batch_rename_folders => ResourceManager.GetString("Batch_rename_folders", resourceCulture);

    public static string Browse => ResourceManager.GetString("Browse", resourceCulture);

    public static string Cancel => ResourceManager.GetString("Cancel", resourceCulture);

    public static string Close => ResourceManager.GetString("Close", resourceCulture);

    public static string Cancel_root_folder => ResourceManager.GetString("Cancel_root_folder", resourceCulture);

    public static string Change_stage_file => ResourceManager.GetString("Change_stage_file", resourceCulture);

    public static string Clear_all => ResourceManager.GetString("Clear_all", resourceCulture);

    public static string Clear_estimation => ResourceManager.GetString("Clear_estimation", resourceCulture);

    public static string Clear_reinstall_estimation => ResourceManager.GetString("Clear_reinstall_estimation", resourceCulture);

    public static string Confirm => ResourceManager.GetString("Confirm", resourceCulture);

    public static string Convert_to_audio_file => ResourceManager.GetString("Convert_to_audio_file", resourceCulture);

    public static string Converting => ResourceManager.GetString("Converting", resourceCulture);

    public static string Create_new => ResourceManager.GetString("Create_new", resourceCulture);

    public static string Create_new_folder => ResourceManager.GetString("Create_new_folder", resourceCulture);

    public static string Custom_folder => ResourceManager.GetString("Custom_folder", resourceCulture);

    public static string CustomFolder => ResourceManager.GetString("CustomFolder", resourceCulture);

    public static string Daily => ResourceManager.GetString("Daily", resourceCulture);

    public static string Delete_from_list => ResourceManager.GetString("Delete_from_list", resourceCulture);

    public static string Delete_pending_installed_only_packages_permanently => ResourceManager.GetString("Delete_pending_installed_only_packages_permanently", resourceCulture);

    public static string Overwrite_pending_installed_only_packages_resources => ResourceManager.GetString("Overwrite_pending_installed_only_packages_resources", resourceCulture);

    public static string Details => ResourceManager.GetString("Details", resourceCulture);

    public static string Details_message_diag => ResourceManager.GetString("Details_message_diag", resourceCulture);

    public static string Details_initialization_settings => ResourceManager.GetString("Details_initialization_settings", resourceCulture);

    public static string Details_lr2_integration_settings => ResourceManager.GetString("Details_lr2_integration_settings", resourceCulture);

    public static string Details_show_diag_chartview => ResourceManager.GetString("Details_show_diag_chartview", resourceCulture);

    public static string Details_show_diag_diff_install => ResourceManager.GetString("Details_show_diag_diff_install", resourceCulture);

    public static string Details_show_diag_recommend => ResourceManager.GetString("Details_show_diag_recommend", resourceCulture);

    public static string Details_test_download_and_install => ResourceManager.GetString("Details_test_download_and_install", resourceCulture);

    public static string Details_test_keep_installable_pending => ResourceManager.GetString("Details_test_keep_installable_pending", resourceCulture);

    public static string Details_use_everything_for_pending_package_source_scan => ResourceManager.GetString("Details_use_everything_for_pending_package_source_scan", resourceCulture);

    public static string Details_auto_apply_ambiguous_install_destination => ResourceManager.GetString("Details_auto_apply_ambiguous_install_destination", resourceCulture);

    public static string Details_estimate_offline_score_ranking => ResourceManager.GetString("Details_estimate_offline_score_ranking", resourceCulture);

    public static string Details_update_lr2ir_ranking_cache_on_startup => ResourceManager.GetString("Details_update_lr2ir_ranking_cache_on_startup", resourceCulture);

    public static string Details_download_lr2ir_score_and_detect_unsent => ResourceManager.GetString("Details_download_lr2ir_score_and_detect_unsent", resourceCulture);

    public static string Details_test_notcheck_playlists => ResourceManager.GetString("Details_test_notcheck_playlists", resourceCulture);

    public static string Details_test_db_read_optimized_pragmas => ResourceManager.GetString("Details_test_db_read_optimized_pragmas", resourceCulture);

    /// <summary>
    /// 通常インストール後に元パッケージを削除する設定項目の表示文言を取得します。
    /// </summary>
    public static string Details_test_delete_pending_source_after_install => ResourceManager.GetString("Details_test_delete_pending_source_after_install", resourceCulture);

    public static string Details_test_startup_select_install_pending => ResourceManager.GetString("Details_test_startup_select_install_pending", resourceCulture);

    public static string Details_test_smart_component_overwrite => ResourceManager.GetString("Details_test_smart_component_overwrite", resourceCulture);

    public static string Details_test_keep_smart_overwrite_protected_by_rename => ResourceManager.GetString("Details_test_keep_smart_overwrite_protected_by_rename", resourceCulture);

    public static string Details_test_notscan => ResourceManager.GetString("Details_test_notscan", resourceCulture);

    public static string Msg_confirm_skip_init_file_check => ResourceManager.GetString("Msg_confirm_skip_init_file_check", resourceCulture);

    public static string Msg_confirm_skip_init_playlist_load => ResourceManager.GetString("Msg_confirm_skip_init_playlist_load", resourceCulture);

    public static string Msg_confirm_enable_offline_score_ranking_estimation => ResourceManager.GetString("Msg_confirm_enable_offline_score_ranking_estimation", resourceCulture);

    public static string Msg_confirm_enable_lr2ir_ranking_cache_startup_update => ResourceManager.GetString("Msg_confirm_enable_lr2ir_ranking_cache_startup_update", resourceCulture);

    public static string Device => ResourceManager.GetString("Device", resourceCulture);

    public static string Device_setting => ResourceManager.GetString("Device_setting", resourceCulture);

    public static string Device_setting_buffersize => ResourceManager.GetString("Device_setting_buffersize", resourceCulture);

    public static string Device_setting_desc1 => ResourceManager.GetString("Device_setting_desc1", resourceCulture);

    public static string Device_setting_desc2 => ResourceManager.GetString("Device_setting_desc2", resourceCulture);

    public static string Device_setting_device => ResourceManager.GetString("Device_setting_device", resourceCulture);

    public static string Device_setting_driver => ResourceManager.GetString("Device_setting_driver", resourceCulture);

    public static string Device_setting_format => ResourceManager.GetString("Device_setting_format", resourceCulture);

    public static string Device_setting_latency => ResourceManager.GetString("Device_setting_latency", resourceCulture);

    public static string Device_setting_lowlatency => ResourceManager.GetString("Device_setting_lowlatency", resourceCulture);

    public static string Device_setting_samplerate => ResourceManager.GetString("Device_setting_samplerate", resourceCulture);

    public static string Device_setting_test => ResourceManager.GetString("Device_setting_test", resourceCulture);

    public static string Device_setting_volume => ResourceManager.GetString("Device_setting_volume", resourceCulture);

    public static string Diff_URL => ResourceManager.GetString("Diff_URL", resourceCulture);

    public static string Directory => ResourceManager.GetString("Directory", resourceCulture);

    public static string DirPath_BMS => ResourceManager.GetString("DirPath_BMS", resourceCulture);

    public static string Standalone_BMSDirectories => ResourceManager.GetString("Standalone_BMSDirectories", resourceCulture);

    public static string Add_BMSDirectory => ResourceManager.GetString("Add_BMSDirectory", resourceCulture);

    public static string Remove_BMSDirectory => ResourceManager.GetString("Remove_BMSDirectory", resourceCulture);

    public static string DirPath_LR2 => ResourceManager.GetString("DirPath_LR2", resourceCulture);

    public static string Download => ResourceManager.GetString("Download", resourceCulture);

    public static string Error => ResourceManager.GetString("Error", resourceCulture);

    public static string Error_InvalidStandaloneBmsRootPaths => ResourceManager.GetString("Error_InvalidStandaloneBmsRootPaths", resourceCulture);

    public static string Error_InvalidBmsInstallDir => ResourceManager.GetString("Error_InvalidBmsInstallDir", resourceCulture);

    public static string Confirm_RestartForOperationModeChange => ResourceManager.GetString("Confirm_RestartForOperationModeChange", resourceCulture);

    public static string Error_RestartApplicationFailed => ResourceManager.GetString("Error_RestartApplicationFailed", resourceCulture);

    public static string Estimate_install_loc => ResourceManager.GetString("Estimate_install_loc", resourceCulture);

    public static string Estimate_merge_loc => ResourceManager.GetString("Estimate_merge_loc", resourceCulture);

    public static string Estimate_reinstall_loc => ResourceManager.GetString("Estimate_reinstall_loc", resourceCulture);

    public static string Exclusive => ResourceManager.GetString("Exclusive", resourceCulture);

    public static string Export => ResourceManager.GetString("Export", resourceCulture);

    public static string Failure => ResourceManager.GetString("Failure", resourceCulture);

    public static string File => ResourceManager.GetString("File", resourceCulture);

    public static string File_scan => ResourceManager.GetString("File_scan", resourceCulture);

    public static string FilePath_configXml => ResourceManager.GetString("FilePath_configXml", resourceCulture);

    public static string FileDialogFilter_scoreDB => ResourceManager.GetString("FileDialogFilter_scoreDB", resourceCulture);

    public static string FilePath_scoreDB => ResourceManager.GetString("FilePath_scoreDB", resourceCulture);

    public static string FilePath_beatoraja_table => ResourceManager.GetString("FilePath_beatoraja_table", resourceCulture);

    public static string FilePath_beatoraja_root => ResourceManager.GetString("FilePath_beatoraja_root", resourceCulture);

    public static string Beatoraja_player => ResourceManager.GetString("Beatoraja_player", resourceCulture);

    public static string Register_beatoraja_bmt_urls => ResourceManager.GetString("Register_beatoraja_bmt_urls", resourceCulture);

    public static string Keep_beatoraja_bmt_files_when_output_disabled => ResourceManager.GetString("Keep_beatoraja_bmt_files_when_output_disabled", resourceCulture);

    public static string Beatoraja_bmt_hash_output_mode => ResourceManager.GetString("Beatoraja_bmt_hash_output_mode", resourceCulture);

    public static string Beatoraja_bmt_hash_output_original => ResourceManager.GetString("Beatoraja_bmt_hash_output_original", resourceCulture);

    public static string Beatoraja_bmt_hash_output_fill_missing => ResourceManager.GetString("Beatoraja_bmt_hash_output_fill_missing", resourceCulture);

    public static string Beatoraja_bmt_hash_output_prefer_sha256_only => ResourceManager.GetString("Beatoraja_bmt_hash_output_prefer_sha256_only", resourceCulture);

    public static string FilePath_songDB => ResourceManager.GetString("FilePath_songDB", resourceCulture);

    public static string FilePath_StageFile => ResourceManager.GetString("FilePath_StageFile", resourceCulture);

    public static string Fix_character_encoding => ResourceManager.GetString("Fix_character_encoding", resourceCulture);

    public static string Fix_installed_loc => ResourceManager.GetString("Fix_installed_loc", resourceCulture);

    public static string Fixed => ResourceManager.GetString("Fixed", resourceCulture);

    public static string Encoding_ChineseSimplified_GB2312 => ResourceManager.GetString("Encoding_ChineseSimplified_GB2312", resourceCulture);

    public static string Encoding_ChineseTraditional_Big5 => ResourceManager.GetString("Encoding_ChineseTraditional_Big5", resourceCulture);

    public static string Encoding_Japanese_ShiftJIS => ResourceManager.GetString("Encoding_Japanese_ShiftJIS", resourceCulture);

    public static string Encoding_Korean_KS_C_5601_1987 => ResourceManager.GetString("Encoding_Korean_KS_C_5601_1987", resourceCulture);

    public static string Encoding_UTF8 => ResourceManager.GetString("Encoding_UTF8", resourceCulture);

    public static string Folder => ResourceManager.GetString("Folder", resourceCulture);

    public static string Force_install => ResourceManager.GetString("Force_install", resourceCulture);

    public static string Full_scan_check => ResourceManager.GetString("Full_scan_check", resourceCulture);

    public static string Full_scan_all_charts => ResourceManager.GetString("Full_scan_all_charts", resourceCulture);

    public static string General => ResourceManager.GetString("General", resourceCulture);

    public static string Ignore_list => ResourceManager.GetString("Ignore_list", resourceCulture);

    public static string Import => ResourceManager.GetString("Import", resourceCulture);

    public static string Information => ResourceManager.GetString("Information", resourceCulture);

    public static string InitialSetupLanguageDialogTitle => ResourceManager.GetString("InitialSetupLanguageDialogTitle", resourceCulture);

    public static string InitialSetupLanguageDialogContinue => ResourceManager.GetString("InitialSetupLanguageDialogContinue", resourceCulture);

    public static string Init_column_setting => ResourceManager.GetString("Init_column_setting", resourceCulture);

    public static string Drop_install_queue_label_format => ResourceManager.GetString("Drop_install_queue_label_format", resourceCulture);

    public static string Pending_estimate_queue_label_format => ResourceManager.GetString("Pending_estimate_queue_label_format", resourceCulture);

    public static string Playlist_url_download_progress_label_format => ResourceManager.GetString("Playlist_url_download_progress_label_format", resourceCulture);

    public static string Pending_estimate_queue_startup_display_name => ResourceManager.GetString("Pending_estimate_queue_startup_display_name", resourceCulture);

    public static string Playlist_sync_progress_label_format => ResourceManager.GetString("Playlist_sync_progress_label_format", resourceCulture);

    public static string Playlist_sync_progress_single_label => ResourceManager.GetString("Playlist_sync_progress_single_label", resourceCulture);

    public static string Beatoraja_bmt_export_progress_label_format => ResourceManager.GetString("Beatoraja_bmt_export_progress_label_format", resourceCulture);

    public static string Beatoraja_bmt_export_progress_single_label => ResourceManager.GetString("Beatoraja_bmt_export_progress_single_label", resourceCulture);

    public static string Custom_folder_output_progress_label_format => ResourceManager.GetString("Custom_folder_output_progress_label_format", resourceCulture);

    public static string Custom_folder_output_progress_single_label => ResourceManager.GetString("Custom_folder_output_progress_single_label", resourceCulture);

    public static string Custom_folder_db_sync_progress_single_label => ResourceManager.GetString("Custom_folder_db_sync_progress_single_label", resourceCulture);

    public static string Playlist_import_progress_label_format => ResourceManager.GetString("Playlist_import_progress_label_format", resourceCulture);

    public static string Playlist_import_progress_single_label => ResourceManager.GetString("Playlist_import_progress_single_label", resourceCulture);

    public static string Statusbar_progress_startup => ResourceManager.GetString("Statusbar_progress_startup", resourceCulture);

    public static string Statusbar_progress_reload_files => ResourceManager.GetString("Statusbar_progress_reload_files", resourceCulture);

    public static string Statusbar_progress_reload_tables => ResourceManager.GetString("Statusbar_progress_reload_tables", resourceCulture);

    public static string Statusbar_progress_reload_scores => ResourceManager.GetString("Statusbar_progress_reload_scores", resourceCulture);

    public static string Statusbar_progress_full_reinitialize => ResourceManager.GetString("Statusbar_progress_full_reinitialize", resourceCulture);

    public static string Statusbar_progress_operable => ResourceManager.GetString("Statusbar_progress_operable", resourceCulture);

    public static string Statusbar_progress_operable_background => ResourceManager.GetString("Statusbar_progress_operable_background", resourceCulture);

    public static string Statusbar_progress_complete => ResourceManager.GetString("Statusbar_progress_complete", resourceCulture);

    public static string Statusbar_progress_complete_reload => ResourceManager.GetString("Statusbar_progress_complete_reload", resourceCulture);

    public static string Statusbar_progress_complete_scores => ResourceManager.GetString("Statusbar_progress_complete_scores", resourceCulture);

    public static string Statusbar_progress_complete_reinitialize => ResourceManager.GetString("Statusbar_progress_complete_reinitialize", resourceCulture);

    public static string Statusbar_progress_failed => ResourceManager.GetString("Statusbar_progress_failed", resourceCulture);

    public static string Statusbar_progress_failed_reload => ResourceManager.GetString("Statusbar_progress_failed_reload", resourceCulture);

    public static string Statusbar_progress_failed_scores => ResourceManager.GetString("Statusbar_progress_failed_scores", resourceCulture);

    public static string Statusbar_progress_failed_reinitialize => ResourceManager.GetString("Statusbar_progress_failed_reinitialize", resourceCulture);

    public static string Statusbar_progress_phase_library_load => ResourceManager.GetString("Statusbar_progress_phase_library_load", resourceCulture);

    public static string Statusbar_progress_phase_library_db_load => ResourceManager.GetString("Statusbar_progress_phase_library_db_load", resourceCulture);

    public static string Statusbar_progress_phase_file_enumeration => ResourceManager.GetString("Statusbar_progress_phase_file_enumeration", resourceCulture);

    public static string Statusbar_progress_phase_file_diff => ResourceManager.GetString("Statusbar_progress_phase_file_diff", resourceCulture);

    public static string Statusbar_progress_phase_ui_prepare => ResourceManager.GetString("Statusbar_progress_phase_ui_prepare", resourceCulture);

    public static string Statusbar_progress_phase_playlist_ref => ResourceManager.GetString("Statusbar_progress_phase_playlist_ref", resourceCulture);

    public static string Statusbar_progress_phase_maintenance => ResourceManager.GetString("Statusbar_progress_phase_maintenance", resourceCulture);

    public static string Statusbar_progress_phase_installable_maintenance => ResourceManager.GetString("Statusbar_progress_phase_installable_maintenance", resourceCulture);

    public static string Statusbar_progress_phase_score_hydration => ResourceManager.GetString("Statusbar_progress_phase_score_hydration", resourceCulture);

    public static string Statusbar_progress_phase_ranking_refresh => ResourceManager.GetString("Statusbar_progress_phase_ranking_refresh", resourceCulture);

    public static string Statusbar_progress_phase_chart_info => ResourceManager.GetString("Statusbar_progress_phase_chart_info", resourceCulture);

    public static string Statusbar_progress_phase_chart_info_load => ResourceManager.GetString("Statusbar_progress_phase_chart_info_load", resourceCulture);

    public static string Statusbar_progress_phase_background => ResourceManager.GetString("Statusbar_progress_phase_background", resourceCulture);

    public static string Statusbar_progress_phase_lr2_song_db_sync => ResourceManager.GetString("Statusbar_progress_phase_lr2_song_db_sync", resourceCulture);

    public static string Lr2_song_db_sync_status_needed => ResourceManager.GetString("Lr2_song_db_sync_status_needed", resourceCulture);

    public static string Lr2_song_db_sync_status_running => ResourceManager.GetString("Lr2_song_db_sync_status_running", resourceCulture);

    public static string Lr2_song_db_sync_status_completed => ResourceManager.GetString("Lr2_song_db_sync_status_completed", resourceCulture);

    public static string Lr2_song_db_sync_status_failed => ResourceManager.GetString("Lr2_song_db_sync_status_failed", resourceCulture);

    public static string Lr2_song_db_sync_status_incomplete => ResourceManager.GetString("Lr2_song_db_sync_status_incomplete", resourceCulture);

    public static string Lr2_song_db_sync_retry => ResourceManager.GetString("Lr2_song_db_sync_retry", resourceCulture);

    public static string Lr2_song_db_sync_data_resync => ResourceManager.GetString("Lr2_song_db_sync_data_resync", resourceCulture);

    public static string Lr2_song_db_sync_data_resync_tooltip => ResourceManager.GetString("Lr2_song_db_sync_data_resync_tooltip", resourceCulture);

    public static string Msg_confirm_lr2_song_db_sync_data_resync => ResourceManager.GetString("Msg_confirm_lr2_song_db_sync_data_resync", resourceCulture);

    public static string Lr2_play_history_schema_label => ResourceManager.GetString("Lr2_play_history_schema_label", resourceCulture);

    public static string Lr2_play_history_schema_refresh => ResourceManager.GetString("Lr2_play_history_schema_refresh", resourceCulture);

    public static string Lr2_play_history_schema_install => ResourceManager.GetString("Lr2_play_history_schema_install", resourceCulture);

    public static string Lr2_play_history_schema_repair => ResourceManager.GetString("Lr2_play_history_schema_repair", resourceCulture);

    public static string Lr2_play_history_schema_status_unknown => ResourceManager.GetString("Lr2_play_history_schema_status_unknown", resourceCulture);

    public static string Lr2_play_history_schema_status_installed => ResourceManager.GetString("Lr2_play_history_schema_status_installed", resourceCulture);

    public static string Lr2_play_history_schema_status_not_installed => ResourceManager.GetString("Lr2_play_history_schema_status_not_installed", resourceCulture);

    public static string Lr2_play_history_schema_status_repairable => ResourceManager.GetString("Lr2_play_history_schema_status_repairable", resourceCulture);

    public static string Lr2_play_history_schema_status_manual_repair_required => ResourceManager.GetString("Lr2_play_history_schema_status_manual_repair_required", resourceCulture);

    public static string Lr2_play_history_schema_status_unreadable => ResourceManager.GetString("Lr2_play_history_schema_status_unreadable", resourceCulture);

    public static string Lr2_play_history_schema_status_skipped_profile => ResourceManager.GetString("Lr2_play_history_schema_status_skipped_profile", resourceCulture);

    public static string Play_history_tree_root => ResourceManager.GetString("Play_history_tree_root", resourceCulture);

    public static string Play_history_period_all => ResourceManager.GetString("Play_history_period_all", resourceCulture);

    public static string Play_history_period_today => ResourceManager.GetString("Play_history_period_today", resourceCulture);

    public static string Play_history_period_yesterday => ResourceManager.GetString("Play_history_period_yesterday", resourceCulture);

    public static string Play_history_period_recent_7_days => ResourceManager.GetString("Play_history_period_recent_7_days", resourceCulture);

    public static string Play_history_period_recent_30_days => ResourceManager.GetString("Play_history_period_recent_30_days", resourceCulture);

    public static string Play_history_period_archive => ResourceManager.GetString("Play_history_period_archive", resourceCulture);

    public static string Play_history_period_diagnostics => ResourceManager.GetString("Play_history_period_diagnostics", resourceCulture);

    public static string Play_history_summary_format => ResourceManager.GetString("Play_history_summary_format", resourceCulture);

    public static string Play_history_copy_md5 => ResourceManager.GetString("Play_history_copy_md5", resourceCulture);

    public static string Play_history_copy_repository_sha256 => ResourceManager.GetString("Play_history_copy_repository_sha256", resourceCulture);

    public static string Play_history_copy_raw_hash => ResourceManager.GetString("Play_history_copy_raw_hash", resourceCulture);

    public static string Msg_confirm_lr2_play_history_schema_install_or_repair => ResourceManager.GetString("Msg_confirm_lr2_play_history_schema_install_or_repair", resourceCulture);

    public static string Msg_success_lr2_play_history_schema_install_or_repair => ResourceManager.GetString("Msg_success_lr2_play_history_schema_install_or_repair", resourceCulture);

    public static string Lr2_song_db_sync_cleanup_startup_scan_blockers => ResourceManager.GetString("Lr2_song_db_sync_cleanup_startup_scan_blockers", resourceCulture);

    public static string Msg_confirm_lr2_song_db_sync_startup_scan_blocker_cleanup => ResourceManager.GetString("Msg_confirm_lr2_song_db_sync_startup_scan_blocker_cleanup", resourceCulture);

    public static string Install => ResourceManager.GetString("Install", resourceCulture);

    public static string Install_desc => ResourceManager.GetString("Install_desc", resourceCulture);

    public static string Install_Dst => ResourceManager.GetString("Install_Dst", resourceCulture);

    public static string Install_foldername => ResourceManager.GetString("Install_foldername", resourceCulture);

    public static string Install_new_folder => ResourceManager.GetString("Install_new_folder", resourceCulture);

    public static string Install_new_folder_desc => ResourceManager.GetString("Install_new_folder_desc", resourceCulture);

    public static string Install_to_estimation => ResourceManager.GetString("Install_to_estimation", resourceCulture);

    public static string Json_file_exts => ResourceManager.GetString("Json_file_exts", resourceCulture);

    public static string Korean => ResourceManager.GetString("Korean", resourceCulture);

    public static string Language => ResourceManager.GetString("Language", resourceCulture);

    public static string Level => ResourceManager.GetString("Level", resourceCulture);

    public static string Library => ResourceManager.GetString("Library", resourceCulture);

    public static string Load_from_insane_estimation_table => ResourceManager.GetString("Load_from_insane_estimation_table", resourceCulture);

    public static string Load_from_table_list => ResourceManager.GetString("Load_from_table_list", resourceCulture);

    public static string Load_PlaylistURI => ResourceManager.GetString("Load_PlaylistURI", resourceCulture);

    public static string Load_Url => ResourceManager.GetString("Load_Url", resourceCulture);

    public static string Maintenance => ResourceManager.GetString("Maintenance", resourceCulture);

    public static string Mark_fixed => ResourceManager.GetString("Mark_fixed", resourceCulture);

    public static string Merge_to => ResourceManager.GetString("Merge_to", resourceCulture);

    public static string MissCount => ResourceManager.GetString("MissCount", resourceCulture);

    public static string Monthly => ResourceManager.GetString("Monthly", resourceCulture);

    public static string Move_to => ResourceManager.GetString("Move_to", resourceCulture);

    public static string Move_to_recycle => ResourceManager.GetString("Move_to_recycle", resourceCulture);

    public static string Movie_playback => ResourceManager.GetString("Movie_playback", resourceCulture);

    public static string Movie_playback_exbrowser => ResourceManager.GetString("Movie_playback_exbrowser", resourceCulture);

    public static string Msg_clear_all_installed => ResourceManager.GetString("Msg_clear_all_installed", resourceCulture);

    public static string Msg_clear_all_pendings => ResourceManager.GetString("Msg_clear_all_pendings", resourceCulture);

    public static string Msg_clear_pendings => ResourceManager.GetString("Msg_clear_pendings", resourceCulture);

    public static string Msg_clear_selected_installed => ResourceManager.GetString("Msg_clear_selected_installed", resourceCulture);

    public static string Msg_clear_selected_pendings => ResourceManager.GetString("Msg_clear_selected_pendings", resourceCulture);

    public static string Msg_conversion_completed => ResourceManager.GetString("Msg_conversion_completed", resourceCulture);

    public static string Msg_conversion_stopped => ResourceManager.GetString("Msg_conversion_stopped", resourceCulture);

    public static string Msg_download_completed => ResourceManager.GetString("Msg_download_completed", resourceCulture);

    public static string Msg_download_ranking_cache => ResourceManager.GetString("Msg_download_ranking_cache", resourceCulture);

    public static string Msg_error_cache_download => ResourceManager.GetString("Msg_error_cache_download", resourceCulture);

    public static string Msg_error_close_timeout => ResourceManager.GetString("Msg_error_close_timeout", resourceCulture);

    public static string Msg_error_preview => ResourceManager.GetString("Msg_error_preview", resourceCulture);

    public static string Msg_error_timeout_dblock_restore => ResourceManager.GetString("Msg_error_timeout_dblock_restore", resourceCulture);

    public static string Msg_error_timeout_dblock_uninstall => ResourceManager.GetString("Msg_error_timeout_dblock_uninstall", resourceCulture);

    public static string Msg_error_unexpected => ResourceManager.GetString("Msg_error_unexpected", resourceCulture);

    public static string Msg_failed_add_playlist_entry => ResourceManager.GetString("Msg_failed_add_playlist_entry", resourceCulture);

    public static string Msg_failed_backups => ResourceManager.GetString("Msg_failed_backups", resourceCulture);

    public static string Msg_failed_create_playlist_folder => ResourceManager.GetString("Msg_failed_create_playlist_folder", resourceCulture);

    public static string Msg_failed_installation => ResourceManager.GetString("Msg_failed_installation", resourceCulture);

    public static string Msg_failed_load_playlist => ResourceManager.GetString("Msg_failed_load_playlist", resourceCulture);

    public static string Msg_failed_play => ResourceManager.GetString("Msg_failed_play", resourceCulture);

    public static string Msg_failed_playlist_backup => ResourceManager.GetString("Msg_failed_playlist_backup", resourceCulture);

    public static string Msg_failed_playlist_restore => ResourceManager.GetString("Msg_failed_playlist_restore", resourceCulture);

    public static string Msg_failed_remove_playlist_entry => ResourceManager.GetString("Msg_failed_remove_playlist_entry", resourceCulture);

    public static string Msg_failed_remove_playlist_folder => ResourceManager.GetString("Msg_failed_remove_playlist_folder", resourceCulture);

    public static string Msg_failed_save_playlist => ResourceManager.GetString("Msg_failed_save_playlist", resourceCulture);

    public static string Msg_failed_uninstall => ResourceManager.GetString("Msg_failed_uninstall", resourceCulture);

    public static string Msg_fix_installation => ResourceManager.GetString("Msg_fix_installation", resourceCulture);

    public static string Msg_fix_installation_warning => ResourceManager.GetString("Msg_fix_installation_warning", resourceCulture);

    public static string Msg_hide_message => ResourceManager.GetString("Msg_hide_message", resourceCulture);

    public static string Msg_init_column_settings => ResourceManager.GetString("Msg_init_column_settings", resourceCulture);

    public static string Msg_init_completed => ResourceManager.GetString("Msg_init_completed", resourceCulture);

    public static string Msg_init_settings => ResourceManager.GetString("Msg_init_settings", resourceCulture);

    public static string Msg_init_settings_check => ResourceManager.GetString("Msg_init_settings_check", resourceCulture);

    public static string Msg_initsetting_completed => ResourceManager.GetString("Msg_initsetting_completed", resourceCulture);

    public static string Msg_invalid_setting => ResourceManager.GetString("Msg_invalid_setting", resourceCulture);

    public static string Msg_settings_apply_blocked_during_initialization => ResourceManager.GetString("Msg_settings_apply_blocked_during_initialization", resourceCulture);

    public static string Msg_load_recommended_tables_error => ResourceManager.GetString("Msg_load_recommended_tables_error", resourceCulture);

    public static string Msg_load_recommended_tables_readonly_mode => ResourceManager.GetString("Msg_load_recommended_tables_readonly_mode", resourceCulture);

    public static string Msg_load_recommended_tables_update_mode => ResourceManager.GetString("Msg_load_recommended_tables_update_mode", resourceCulture);

    public static string Msg_manual_installation => ResourceManager.GetString("Msg_manual_installation", resourceCulture);

    /// <summary>
    /// 通常インストール時に元パッケージも削除する場合の確認メッセージを取得します。
    /// </summary>
    public static string Msg_manual_installation_delete_source => ResourceManager.GetString("Msg_manual_installation_delete_source", resourceCulture);

    public static string Msg_estimate_merge_confirm => ResourceManager.GetString("Msg_estimate_merge_confirm", resourceCulture);

    public static string Msg_merge_bms_destination => ResourceManager.GetString("Msg_merge_bms_destination", resourceCulture);

    public static string Msg_open_install_destination_missing => ResourceManager.GetString("Msg_open_install_destination_missing", resourceCulture);

    public static string Msg_open_install_destination_multiple_selected => ResourceManager.GetString("Msg_open_install_destination_multiple_selected", resourceCulture);

    public static string Msg_open_install_destination_not_found => ResourceManager.GetString("Msg_open_install_destination_not_found", resourceCulture);

    public static string Msg_merge_bms_folder => ResourceManager.GetString("Msg_merge_bms_folder", resourceCulture);

    public static string Msg_merge_bms_target => ResourceManager.GetString("Msg_merge_bms_target", resourceCulture);

    public static string Msg_move_to_other_root => ResourceManager.GetString("Msg_move_to_other_root", resourceCulture);

    public static string Msg_move_to_recycle => ResourceManager.GetString("Msg_move_to_recycle", resourceCulture);

    public static string Msg_delete_pending_installed_only_packages_permanently => ResourceManager.GetString("Msg_delete_pending_installed_only_packages_permanently", resourceCulture);

    public static string Msg_rename_pending_zero_note_to_invalid_ext => ResourceManager.GetString("Msg_rename_pending_zero_note_to_invalid_ext", resourceCulture);

    public static string Msg_overwrite_pending_installed_only_packages_resources => ResourceManager.GetString("Msg_overwrite_pending_installed_only_packages_resources", resourceCulture);

    public static string Msg_override_level_error_recommended => ResourceManager.GetString("Msg_override_level_error_recommended", resourceCulture);

    public static string Msg_override_level_warning => ResourceManager.GetString("Msg_override_level_warning", resourceCulture);

    public static string Msg_ranking_cache_notfound => ResourceManager.GetString("Msg_ranking_cache_notfound", resourceCulture);

    public static string Msg_register_chart => ResourceManager.GetString("Msg_register_chart", resourceCulture);

    public static string Msg_remove_chart_info_parse_failure_record => ResourceManager.GetString("Msg_remove_chart_info_parse_failure_record", resourceCulture);

    public static string Msg_remove_folder => ResourceManager.GetString("Msg_remove_folder", resourceCulture);

    public static string Msg_remove_playlist => ResourceManager.GetString("Msg_remove_playlist", resourceCulture);

    public static string Msg_rename_folders => ResourceManager.GetString("Msg_rename_folders", resourceCulture);

    public static string Msg_rename_to_invalid => ResourceManager.GetString("Msg_rename_to_invalid", resourceCulture);

    public static string Msg_show_chart => ResourceManager.GetString("Msg_show_chart", resourceCulture);

    public static string Msg_success_playlist_backup => ResourceManager.GetString("Msg_success_playlist_backup", resourceCulture);

    public static string Msg_success_playlist_restore => ResourceManager.GetString("Msg_success_playlist_restore", resourceCulture);

    public static string Msg_success_register_chart => ResourceManager.GetString("Msg_success_register_chart", resourceCulture);

    public static string Msg_success_uninstall => ResourceManager.GetString("Msg_success_uninstall", resourceCulture);

    public static string Msg_unregister_root_folder => ResourceManager.GetString("Msg_unregister_root_folder", resourceCulture);

    public static string Msg_warn_cache_download => ResourceManager.GetString("Msg_warn_cache_download", resourceCulture);

    public static string Msg_warn_play_temp_install => ResourceManager.GetString("Msg_warn_play_temp_install", resourceCulture);

    public static string Msg_warn_playlist_backup => ResourceManager.GetString("Msg_warn_playlist_backup", resourceCulture);

    public static string Msg_warn_preview => ResourceManager.GetString("Msg_warn_preview", resourceCulture);

    public static string New => ResourceManager.GetString("New", resourceCulture);

    public static string NicoNico => ResourceManager.GetString("NicoNico", resourceCulture);

    public static string No_folder_name => ResourceManager.GetString("No_folder_name", resourceCulture);

    public static string None => ResourceManager.GetString("None", resourceCulture);

    public static string Not_found => ResourceManager.GetString("Not_found", resourceCulture);

    public static string Not_use_LR2DB => ResourceManager.GetString("Not_use_LR2DB", resourceCulture);

    public static string Note => ResourceManager.GetString("Note", resourceCulture);

    public static string Now_searching => ResourceManager.GetString("Now_searching", resourceCulture);

    public static string Num_chart => ResourceManager.GetString("Num_chart", resourceCulture);

    public static string Num_folders => ResourceManager.GetString("Num_folders", resourceCulture);

    public static string Num_songs => ResourceManager.GetString("Num_songs", resourceCulture);

    public static string Open_association => ResourceManager.GetString("Open_association", resourceCulture);

    public static string Open_chart_viewer => ResourceManager.GetString("Open_chart_viewer", resourceCulture);

    public static string Open_clear_lamp => ResourceManager.GetString("Open_clear_lamp", resourceCulture);

    public static string Open_document => ResourceManager.GetString("Open_document", resourceCulture);

    public static string Open_file_explorer => ResourceManager.GetString("Open_file_explorer", resourceCulture);

    public static string Open_folder_explorer => ResourceManager.GetString("Open_folder_explorer", resourceCulture);

    public static string Open_image => ResourceManager.GetString("Open_image", resourceCulture);

    public static string Open_scoreDB => ResourceManager.GetString("Open_scoreDB", resourceCulture);

    public static string Open_install_destination => ResourceManager.GetString("Open_install_destination", resourceCulture);

    public static string Open_lr2ir => ResourceManager.GetString("Open_lr2ir", resourceCulture);

    public static string Open_minir => ResourceManager.GetString("Open_minir", resourceCulture);

    public static string Open_mocha => ResourceManager.GetString("Open_mocha", resourceCulture);

    public static string Open_page => ResourceManager.GetString("Open_page", resourceCulture);

    public static string Open_Url => ResourceManager.GetString("Open_Url", resourceCulture);

    public static string Import_Selected_Url => ResourceManager.GetString("Import_Selected_Url", resourceCulture);

    public static string Import_Selected_Url_diff => ResourceManager.GetString("Import_Selected_Url_diff", resourceCulture);

    public static string Open_Url_diff => ResourceManager.GetString("Open_Url_diff", resourceCulture);

    public static string Open_video => ResourceManager.GetString("Open_video", resourceCulture);

    public static string Operation_Mode => ResourceManager.GetString("Operation_Mode", resourceCulture);

    public static string Original_URL => ResourceManager.GetString("Original_URL", resourceCulture);

    public static string Override_level => ResourceManager.GetString("Override_level", resourceCulture);

    public static string Pending => ResourceManager.GetString("Pending", resourceCulture);

    public static string Playback => ResourceManager.GetString("Playback", resourceCulture);

    public static string PlayCount => ResourceManager.GetString("PlayCount", resourceCulture);

    public static string Player => ResourceManager.GetString("Player", resourceCulture);

    public static string Player_BMIIDXView_desc => ResourceManager.GetString("Player_BMIIDXView_desc", resourceCulture);

    public static string Player_Internal_desc => ResourceManager.GetString("Player_Internal_desc", resourceCulture);

    public static string Player_LR2_savewpos => ResourceManager.GetString("Player_LR2_savewpos", resourceCulture);

    public static string Player_LR2_wsize => ResourceManager.GetString("Player_LR2_wsize", resourceCulture);

    public static string Player_Name_Internal => ResourceManager.GetString("Player_Name_Internal", resourceCulture);

    public static string Player_uBMplay_desc => ResourceManager.GetString("Player_uBMplay_desc", resourceCulture);

    public static string Player_Use => ResourceManager.GetString("Player_Use", resourceCulture);

    public static string Playlist => ResourceManager.GetString("Playlist", resourceCulture);

    public static string Playlist_output => ResourceManager.GetString("Playlist_output", resourceCulture);

    public static string Playlist_output_desc => ResourceManager.GetString("Playlist_output_desc", resourceCulture);

    public static string Playlist_output_root => ResourceManager.GetString("Playlist_output_root", resourceCulture);

    public static string Playlist_output_additional => ResourceManager.GetString("Playlist_output_additional", resourceCulture);

    public static string Playlist_output_additional_name => ResourceManager.GetString("Playlist_output_additional_name", resourceCulture);

    public static string Playlist_output_base => ResourceManager.GetString("Playlist_output_base", resourceCulture);

    public static string Playlist_table_uri => ResourceManager.GetString("Playlist_table_uri", resourceCulture);

    public static string Playlist_md5_url_mapping_tsv_uri => ResourceManager.GetString("Playlist_md5_url_mapping_tsv_uri", resourceCulture);

    public static string Playlist_url_completion_enable => ResourceManager.GetString("Playlist_url_completion_enable", resourceCulture);

    public static string Playlist_url_completion_overwrite => ResourceManager.GetString("Playlist_url_completion_overwrite", resourceCulture);

    public static string Playlist_url_completion_stella_full_enable => ResourceManager.GetString("Playlist_url_completion_stella_full_enable", resourceCulture);

    public static string Error_InvalidPlaylistMd5UrlMappingTsvUri => ResourceManager.GetString("Error_InvalidPlaylistMd5UrlMappingTsvUri", resourceCulture);

    public static string PlaylistProp_asc => ResourceManager.GetString("PlaylistProp_asc", resourceCulture);

    public static string PlaylistProp_datauri => ResourceManager.GetString("PlaylistProp_datauri", resourceCulture);

    public static string PlaylistProp_desc => ResourceManager.GetString("PlaylistProp_desc", resourceCulture);

    public static string PlaylistProp_entrytype => ResourceManager.GetString("PlaylistProp_entrytype", resourceCulture);

    public static string PlaylistProp_folder_order => ResourceManager.GetString("PlaylistProp_folder_order", resourceCulture);

    public static string PlaylistProp_folder_prefix => ResourceManager.GetString("PlaylistProp_folder_prefix", resourceCulture);

    public static string PlaylistProp_folder_type => ResourceManager.GetString("PlaylistProp_folder_type", resourceCulture);

    public static string PlaylistProp_ftype_all => ResourceManager.GetString("PlaylistProp_ftype_all", resourceCulture);

    public static string PlaylistProp_ftype_alphabet => ResourceManager.GetString("PlaylistProp_ftype_alphabet", resourceCulture);

    public static string PlaylistProp_ftype_bp_sort => ResourceManager.GetString("PlaylistProp_ftype_bp_sort", resourceCulture);

    public static string PlaylistProp_ftype_bpm_sort => ResourceManager.GetString("PlaylistProp_ftype_bpm_sort", resourceCulture);

    public static string PlaylistProp_ftype_clear => ResourceManager.GetString("PlaylistProp_ftype_clear", resourceCulture);

    public static string PlaylistProp_ftype_desc => ResourceManager.GetString("PlaylistProp_ftype_desc", resourceCulture);

    public static string PlaylistProp_ftype_djlevel => ResourceManager.GetString("PlaylistProp_ftype_djlevel", resourceCulture);

    public static string PlaylistProp_ftype_etc => ResourceManager.GetString("PlaylistProp_ftype_etc", resourceCulture);

    public static string PlaylistProp_ftype_level => ResourceManager.GetString("PlaylistProp_ftype_level", resourceCulture);

    public static string PlaylistProp_ftype_play_count_sort => ResourceManager.GetString("PlaylistProp_ftype_play_count_sort", resourceCulture);

    public static string PlaylistProp_ftype_last_play_sort => ResourceManager.GetString("PlaylistProp_ftype_last_play_sort", resourceCulture);

    public static string PlaylistProp_ftype_random => ResourceManager.GetString("PlaylistProp_ftype_random", resourceCulture);

    public static string PlaylistProp_ftype_user => ResourceManager.GetString("PlaylistProp_ftype_user", resourceCulture);

    public static string PlaylistProp_headeruri => ResourceManager.GetString("PlaylistProp_headeruri", resourceCulture);

    public static string PlaylistProp_make_root => ResourceManager.GetString("PlaylistProp_make_root", resourceCulture);

    public static string PlaylistProp_output_dst => ResourceManager.GetString("PlaylistProp_output_dst", resourceCulture);

    public static string PlaylistProp_pageuri => ResourceManager.GetString("PlaylistProp_pageuri", resourceCulture);

    public static string PlaylistProp_playlist_name => ResourceManager.GetString("PlaylistProp_playlist_name", resourceCulture);

    public static string PlaylistProp_save_name => ResourceManager.GetString("PlaylistProp_save_name", resourceCulture);

    public static string PlaylistProp_sortkey => ResourceManager.GetString("PlaylistProp_sortkey", resourceCulture);

    public static string PlaylistProp_sortorder_song => ResourceManager.GetString("PlaylistProp_sortorder_song", resourceCulture);

    public static string PlaylistProp_symbol => ResourceManager.GetString("PlaylistProp_symbol", resourceCulture);

    public static string PlaylistProp_syncmode => ResourceManager.GetString("PlaylistProp_syncmode", resourceCulture);

    public static string Property => ResourceManager.GetString("Property", resourceCulture);

    public static string Recommended_automatic_update => ResourceManager.GetString("Recommended_automatic_update", resourceCulture);

    public static string Recommended_read_only => ResourceManager.GetString("Recommended_read_only", resourceCulture);

    public static string Record => ResourceManager.GetString("Record", resourceCulture);

    public static string Record_setting => ResourceManager.GetString("Record_setting", resourceCulture);

    public static string Record_setting_encoder => ResourceManager.GetString("Record_setting_encoder", resourceCulture);

    public static string Record_setting_encoder_desc => ResourceManager.GetString("Record_setting_encoder_desc", resourceCulture);

    public static string Record_setting_encoder_dir => ResourceManager.GetString("Record_setting_encoder_dir", resourceCulture);

    public static string Record_setting_encoder_dir_dialog => ResourceManager.GetString("Record_setting_encoder_dir_dialog", resourceCulture);

    public static string Record_setting_encoder_tooltip => ResourceManager.GetString("Record_setting_encoder_tooltip", resourceCulture);

    public static string Record_setting_filename => ResourceManager.GetString("Record_setting_filename", resourceCulture);

    public static string Record_setting_filetype => ResourceManager.GetString("Record_setting_filetype", resourceCulture);

    public static string Record_setting_format => ResourceManager.GetString("Record_setting_format", resourceCulture);

    public static string Record_setting_gain => ResourceManager.GetString("Record_setting_gain", resourceCulture);

    public static string Record_setting_normalize => ResourceManager.GetString("Record_setting_normalize", resourceCulture);

    public static string Record_setting_normalize_average => ResourceManager.GetString("Record_setting_normalize_average", resourceCulture);

    public static string Record_setting_normalize_none => ResourceManager.GetString("Record_setting_normalize_none", resourceCulture);

    public static string Record_setting_normalize_peak => ResourceManager.GetString("Record_setting_normalize_peak", resourceCulture);

    public static string Record_setting_quality => ResourceManager.GetString("Record_setting_quality", resourceCulture);

    public static string Record_setting_replacepattern => ResourceManager.GetString("Record_setting_replacepattern", resourceCulture);

    public static string Record_setting_samplerate => ResourceManager.GetString("Record_setting_samplerate", resourceCulture);

    public static string Register_chart_with_viewer => ResourceManager.GetString("Register_chart_with_viewer", resourceCulture);

    public static string Reinstall_to_estimation => ResourceManager.GetString("Reinstall_to_estimation", resourceCulture);

    public static string Recheck_zero_note => ResourceManager.GetString("Recheck_zero_note", resourceCulture);

    public static string Reload => ResourceManager.GetString("Reload", resourceCulture);

    public static string Reinitialize_library => ResourceManager.GetString("Reinitialize_library", resourceCulture);

    public static string Remarks_URL => ResourceManager.GetString("Remarks_URL", resourceCulture);

    public static string Remove => ResourceManager.GetString("Remove", resourceCulture);

    public static string Remove_playlist_entry => ResourceManager.GetString("Remove_playlist_entry", resourceCulture);

    public static string Remove_chart_file => ResourceManager.GetString("Remove_chart_file", resourceCulture);

    public static string Remove_chart_info_parse_failure_record => ResourceManager.GetString("Remove_chart_info_parse_failure_record", resourceCulture);

    public static string Remove_folder => ResourceManager.GetString("Remove_folder", resourceCulture);

    public static string Remove_ignore => ResourceManager.GetString("Remove_ignore", resourceCulture);

    public static string Remove_playlist => ResourceManager.GetString("Remove_playlist", resourceCulture);

    public static string Rename_folder => ResourceManager.GetString("Rename_folder", resourceCulture);

    public static string Rename_folder_auto => ResourceManager.GetString("Rename_folder_auto", resourceCulture);

    public static string Rename_invalid_ext => ResourceManager.GetString("Rename_invalid_ext", resourceCulture);

    public static string Rename_pending_zero_note_to_invalid_ext => ResourceManager.GetString("Rename_pending_zero_note_to_invalid_ext", resourceCulture);

    public static string Rescan => ResourceManager.GetString("Rescan", resourceCulture);

    public static string Rescan_all_charts => ResourceManager.GetString("Rescan_all_charts", resourceCulture);

    public static string Msg_rescan_all_charts_confirm => ResourceManager.GetString("Msg_rescan_all_charts_confirm", resourceCulture);

    public static string Maintenance_rescan_progress_label_format => ResourceManager.GetString("Maintenance_rescan_progress_label_format", resourceCulture);

    public static string Maintenance_rescan_complete => ResourceManager.GetString("Maintenance_rescan_complete", resourceCulture);

    public static string Maintenance_rescan_canceled => ResourceManager.GetString("Maintenance_rescan_canceled", resourceCulture);

    public static string Sampling_format => ResourceManager.GetString("Sampling_format", resourceCulture);

    public static string Sampling_rate => ResourceManager.GetString("Sampling_rate", resourceCulture);

    public static string Save_data_file => ResourceManager.GetString("Save_data_file", resourceCulture);

    public static string Save_header_file => ResourceManager.GetString("Save_header_file", resourceCulture);

    public static string Save_to => ResourceManager.GetString("Save_to", resourceCulture);

    public static string Schedule => ResourceManager.GetString("Schedule", resourceCulture);

    public static string Score => ResourceManager.GetString("Score", resourceCulture);

    public static string ScoreDB => ResourceManager.GetString("ScoreDB", resourceCulture);

    public static string Search_downalods => ResourceManager.GetString("Search_downalods", resourceCulture);

    public static string Search_duplicates => ResourceManager.GetString("Search_duplicates", resourceCulture);

    public static string Search_garbled => ResourceManager.GetString("Search_garbled", resourceCulture);

    public static string Keyword_search_help_button_tooltip => ResourceManager.GetString("Keyword_search_help_button_tooltip", resourceCulture);

    public static string Keyword_search_help_template => ResourceManager.GetString("Keyword_search_help_template", resourceCulture);

    public static string Keyword_search_help_fields_bmsfile => ResourceManager.GetString("Keyword_search_help_fields_bmsfile", resourceCulture);

    public static string Keyword_search_help_fields_playlist_detail => ResourceManager.GetString("Keyword_search_help_fields_playlist_detail", resourceCulture);

    public static string Keyword_search_help_fields_playlist_summary => ResourceManager.GetString("Keyword_search_help_fields_playlist_summary", resourceCulture);

    public static string Keyword_search_help_fields_play_history => ResourceManager.GetString("Keyword_search_help_fields_play_history", resourceCulture);

    public static string Keyword_search_completion_fields_header => ResourceManager.GetString("Keyword_search_completion_fields_header", resourceCulture);

    public static string Keyword_search_completion_history_header => ResourceManager.GetString("Keyword_search_completion_history_header", resourceCulture);

    public static string Keyword_search_completion_playlist_names_header => ResourceManager.GetString("Keyword_search_completion_playlist_names_header", resourceCulture);

    public static string Keyword_search_warning_unknown_field => ResourceManager.GetString("Keyword_search_warning_unknown_field", resourceCulture);

    public static string Keyword_search_warning_empty_field_term => ResourceManager.GetString("Keyword_search_warning_empty_field_term", resourceCulture);

    public static string Keyword_search_warning_empty_negation => ResourceManager.GetString("Keyword_search_warning_empty_negation", resourceCulture);

    public static string Keyword_search_warning_empty_or => ResourceManager.GetString("Keyword_search_warning_empty_or", resourceCulture);

    public static string Keyword_search_warning_invalid_regex => ResourceManager.GetString("Keyword_search_warning_invalid_regex", resourceCulture);

    public static string Search_zero_note => ResourceManager.GetString("Search_zero_note", resourceCulture);

    public static string Searching => ResourceManager.GetString("Searching", resourceCulture);

    public static string Shared => ResourceManager.GetString("Shared", resourceCulture);

    public static string Size => ResourceManager.GetString("Size", resourceCulture);

    public static string Skip => ResourceManager.GetString("Skip", resourceCulture);

    public static string Success => ResourceManager.GetString("Success", resourceCulture);

    public static string Target => ResourceManager.GetString("Target", resourceCulture);

    public static string Title => ResourceManager.GetString("Title", resourceCulture);

    public static string Tooltip_bms_player => ResourceManager.GetString("Tooltip_bms_player", resourceCulture);

    public static string Tooltip_fast_forward => ResourceManager.GetString("Tooltip_fast_forward", resourceCulture);

    public static string Tooltip_image_file => ResourceManager.GetString("Tooltip_image_file", resourceCulture);

    public static string Tooltip_loading => ResourceManager.GetString("Tooltip_loading", resourceCulture);

    public static string Tooltip_movie_preview => ResourceManager.GetString("Tooltip_movie_preview", resourceCulture);

    public static string Tooltip_pause => ResourceManager.GetString("Tooltip_pause", resourceCulture);

    public static string Tooltip_play => ResourceManager.GetString("Tooltip_play", resourceCulture);

    public static string Tooltip_rewind => ResourceManager.GetString("Tooltip_rewind", resourceCulture);

    public static string Tooltip_score_unsent => ResourceManager.GetString("Tooltip_score_unsent", resourceCulture);

    public static string Tooltip_searching => ResourceManager.GetString("Tooltip_searching", resourceCulture);

    public static string Tooltip_view_mode => ResourceManager.GetString("Tooltip_view_mode", resourceCulture);

    public static string Unregistered => ResourceManager.GetString("Unregistered", resourceCulture);

    public static string Unregistered_in_lr2_db => ResourceManager.GetString("Unregistered_in_lr2_db", resourceCulture);

    public static string Update_history => ResourceManager.GetString("Update_history", resourceCulture);

    public static string Update_history_v0340_1 => ResourceManager.GetString("Update_history_v0340_1", resourceCulture);

    public static string Update_history_v0340_2 => ResourceManager.GetString("Update_history_v0340_2", resourceCulture);

    public static string Update_history_v0350_1 => ResourceManager.GetString("Update_history_v0350_1", resourceCulture);

    public static string Update_history_v0350_2 => ResourceManager.GetString("Update_history_v0350_2", resourceCulture);

    public static string Update_history_v0350_3 => ResourceManager.GetString("Update_history_v0350_3", resourceCulture);

    public static string Update_history_v0353_1 => ResourceManager.GetString("Update_history_v0353_1", resourceCulture);
    public static string Update_history_v1000_1 => ResourceManager.GetString("Update_history_v1000_1", resourceCulture);

    public static string Update_ranking_data => ResourceManager.GetString("Update_ranking_data", resourceCulture);

    public static string Use_LR2_DB => ResourceManager.GetString("Use_LR2_DB", resourceCulture);

    public static string Beatoraja_integration => ResourceManager.GetString("Beatoraja_integration", resourceCulture);

    public static string Use_beatoraja_scoreDB => ResourceManager.GetString("Use_beatoraja_scoreDB", resourceCulture);

    public static string Use_beatoraja_bmt_output => ResourceManager.GetString("Use_beatoraja_bmt_output", resourceCulture);

    public static string Version_info => ResourceManager.GetString("Version_info", resourceCulture);

    public static string Warning => ResourceManager.GetString("Warning", resourceCulture);

    public static string Weekly => ResourceManager.GetString("Weekly", resourceCulture);

    public static string YouTube => ResourceManager.GetString("YouTube", resourceCulture);

    public static string Playlist_summary_header => ResourceManager.GetString("Playlist_summary_header", resourceCulture);

    public static string Playlist_summary_format => ResourceManager.GetString("Playlist_summary_format", resourceCulture);

    public static string Playlist_summary_status_header => ResourceManager.GetString("Playlist_summary_status_header", resourceCulture);

    public static string Playlist_summary_apply_current_order_to_bmt_sort => ResourceManager.GetString("Playlist_summary_apply_current_order_to_bmt_sort", resourceCulture);

    public static string Playlist_summary_move_to_bmt_sort_top => ResourceManager.GetString("Playlist_summary_move_to_bmt_sort_top", resourceCulture);

    public static string Playlist_summary_move_to_bmt_sort_bottom => ResourceManager.GetString("Playlist_summary_move_to_bmt_sort_bottom", resourceCulture);

    public static string Playlist_summary_bulk_edit => ResourceManager.GetString("Playlist_summary_bulk_edit", resourceCulture);

    public static string Playlist_summary_bulk_target_count => ResourceManager.GetString("Playlist_summary_bulk_target_count", resourceCulture);

    public static string Playlist_summary_bulk_custom_folder_output => ResourceManager.GetString("Playlist_summary_bulk_custom_folder_output", resourceCulture);

    public static string Playlist_summary_bulk_root_folder => ResourceManager.GetString("Playlist_summary_bulk_root_folder", resourceCulture);

    public static string Playlist_summary_bulk_external_sync => ResourceManager.GetString("Playlist_summary_bulk_external_sync", resourceCulture);

    public static string Playlist_summary_bulk_bmt_output => ResourceManager.GetString("Playlist_summary_bulk_bmt_output", resourceCulture);

    public static string Playlist_summary_bulk_output_base => ResourceManager.GetString("Playlist_summary_bulk_output_base", resourceCulture);

    public static string Playlist_summary_bulk_external_property_initialization => ResourceManager.GetString("Playlist_summary_bulk_external_property_initialization", resourceCulture);

    public static string Playlist_summary_bulk_apply_custom_folder_output => ResourceManager.GetString("Playlist_summary_bulk_apply_custom_folder_output", resourceCulture);

    public static string Playlist_summary_bulk_apply_root_folder => ResourceManager.GetString("Playlist_summary_bulk_apply_root_folder", resourceCulture);

    public static string Playlist_summary_bulk_apply_external_sync => ResourceManager.GetString("Playlist_summary_bulk_apply_external_sync", resourceCulture);

    public static string Playlist_summary_bulk_apply_bmt_output => ResourceManager.GetString("Playlist_summary_bulk_apply_bmt_output", resourceCulture);

    public static string Playlist_summary_bulk_apply_output_base => ResourceManager.GetString("Playlist_summary_bulk_apply_output_base", resourceCulture);

    public static string Playlist_summary_bulk_apply_external_property_initialization => ResourceManager.GetString("Playlist_summary_bulk_apply_external_property_initialization", resourceCulture);

    public static string Playlist_summary_bulk_initialize_playlist_name => ResourceManager.GetString("Playlist_summary_bulk_initialize_playlist_name", resourceCulture);

    public static string Playlist_summary_bulk_initialize_symbol => ResourceManager.GetString("Playlist_summary_bulk_initialize_symbol", resourceCulture);

    public static string Playlist_summary_bulk_initialize_compat_prefix => ResourceManager.GetString("Playlist_summary_bulk_initialize_compat_prefix", resourceCulture);

    public static string Playlist_summary_bulk_initialize_output_dir => ResourceManager.GetString("Playlist_summary_bulk_initialize_output_dir", resourceCulture);

    public static string Playlist_summary_bulk_no_change => ResourceManager.GetString("Playlist_summary_bulk_no_change", resourceCulture);

    public static string Playlist_summary_bulk_on => ResourceManager.GetString("Playlist_summary_bulk_on", resourceCulture);

    public static string Playlist_summary_bulk_off => ResourceManager.GetString("Playlist_summary_bulk_off", resourceCulture);

    public static string Playlist_sync_status_none => ResourceManager.GetString("Playlist_sync_status_none", resourceCulture);

    public static string Playlist_sync_status_ok => ResourceManager.GetString("Playlist_sync_status_ok", resourceCulture);

    public static string Playlist_sync_status_updated => ResourceManager.GetString("Playlist_sync_status_updated", resourceCulture);

    public static string Playlist_sync_status_header => ResourceManager.GetString("Playlist_sync_status_header", resourceCulture);

    public static string Playlist_sync_status_header_url => ResourceManager.GetString("Playlist_sync_status_header_url", resourceCulture);

    public static string Playlist_sync_status_data => ResourceManager.GetString("Playlist_sync_status_data", resourceCulture);

    public static string Playlist_sync_status_invalid_url => ResourceManager.GetString("Playlist_sync_status_invalid_url", resourceCulture);

    public static string Playlist_sync_status_404 => ResourceManager.GetString("Playlist_sync_status_404", resourceCulture);

    public static string Playlist_sync_status_403 => ResourceManager.GetString("Playlist_sync_status_403", resourceCulture);

    public static string Playlist_sync_status_http => ResourceManager.GetString("Playlist_sync_status_http", resourceCulture);

    public static string Playlist_sync_status_network => ResourceManager.GetString("Playlist_sync_status_network", resourceCulture);

    public static string Playlist_sync_status_unknown => ResourceManager.GetString("Playlist_sync_status_unknown", resourceCulture);

    public static string MessageBoxTitle_Error => ResourceManager.GetString("MessageBoxTitle_Error", resourceCulture);


    public static string MessageBoxTitle_Warning => ResourceManager.GetString("MessageBoxTitle_Warning", resourceCulture);


    public static string MessageBoxTitle_Confirm => ResourceManager.GetString("MessageBoxTitle_Confirm", resourceCulture);


    public static string Error_NotMd5Hash => ResourceManager.GetString("Error_NotMd5Hash", resourceCulture);


    public static string Error_InvalidJsonObject => ResourceManager.GetString("Error_InvalidJsonObject", resourceCulture);


    public static string Error_LR2ScoreDBNotConnected => ResourceManager.GetString("Error_LR2ScoreDBNotConnected", resourceCulture);


    public static string Warning_AlreadyInstalled => ResourceManager.GetString("Warning_AlreadyInstalled", resourceCulture);


    public static string Warning_SingleBmsFile => ResourceManager.GetString("Warning_SingleBmsFile", resourceCulture);


    public static string Warning_SingleBmsonFile => ResourceManager.GetString("Warning_SingleBmsonFile", resourceCulture);


    public static string Warning_NestedChartFileInPackage => ResourceManager.GetString("Warning_NestedChartFileInPackage", resourceCulture);


    public static string WarningDigest_NestedChart => ResourceManager.GetString("WarningDigest_NestedChart", resourceCulture);


    public static string WarningDigest_ZeroNoteMismatch => ResourceManager.GetString("WarningDigest_ZeroNoteMismatch", resourceCulture);


    public static string WarningDigest_ChartInfoParseFailure => ResourceManager.GetString("WarningDigest_ChartInfoParseFailure", resourceCulture);


    public static string WarningDigest_Lr2PathEncodingUnsupported => ResourceManager.GetString("WarningDigest_Lr2PathEncodingUnsupported", resourceCulture);


    public static string WarningDigest_Lr2PathTooLong => ResourceManager.GetString("WarningDigest_Lr2PathTooLong", resourceCulture);


    public static string WarningDigest_Lr2ResourcePathUnsupported => ResourceManager.GetString("WarningDigest_Lr2ResourcePathUnsupported", resourceCulture);


    public static string WarningDigest_Lr2ResourcePathTooLong => ResourceManager.GetString("WarningDigest_Lr2ResourcePathTooLong", resourceCulture);


    public static string WarningDigest_DuplicateChart => ResourceManager.GetString("WarningDigest_DuplicateChart", resourceCulture);


    public static string WarningDigest_InstallEstimationAmbiguous => ResourceManager.GetString("WarningDigest_InstallEstimationAmbiguous", resourceCulture);


    public static string WarningDigest_InstallEstimationMetadataMismatch => ResourceManager.GetString("WarningDigest_InstallEstimationMetadataMismatch", resourceCulture);


    public static string WarningDigest_InstallEstimationReinstallNotImproved => ResourceManager.GetString("WarningDigest_InstallEstimationReinstallNotImproved", resourceCulture);


    public static string WarningDigest_InstalledDestinationAmbiguous => ResourceManager.GetString("WarningDigest_InstalledDestinationAmbiguous", resourceCulture);


    public static string WarningDigest_InstalledDestinationAutoAppliedAmbiguous => ResourceManager.GetString("WarningDigest_InstalledDestinationAutoAppliedAmbiguous", resourceCulture);


    public static string WarningDigest_UnsupportedResourcePath => ResourceManager.GetString("WarningDigest_UnsupportedResourcePath", resourceCulture);


    public static string WarningDigest_InstalledDestinationResolveFailed => ResourceManager.GetString("WarningDigest_InstalledDestinationResolveFailed", resourceCulture);


    public static string WarningDigest_InstallEstimationLowConfidence => ResourceManager.GetString("WarningDigest_InstallEstimationLowConfidence", resourceCulture);


    public static string WarningDigest_AlreadyInstalled => ResourceManager.GetString("WarningDigest_AlreadyInstalled", resourceCulture);


    public static string WarningDigest_SingleBmsFile => ResourceManager.GetString("WarningDigest_SingleBmsFile", resourceCulture);


    public static string WarningDigest_SingleBmsonFile => ResourceManager.GetString("WarningDigest_SingleBmsonFile", resourceCulture);


    public static string WarningDigest_ResourceMissing => ResourceManager.GetString("WarningDigest_ResourceMissing", resourceCulture);


    public static string WarningDigest_ImageMissing => ResourceManager.GetString("WarningDigest_ImageMissing", resourceCulture);


    public static string WarningDigest_Other => ResourceManager.GetString("WarningDigest_Other", resourceCulture);


    public static string Warning_InstallEstimationAmbiguousPrefix => ResourceManager.GetString("Warning_InstallEstimationAmbiguousPrefix", resourceCulture);


    public static string Warning_InstallEstimationAmbiguous => ResourceManager.GetString("Warning_InstallEstimationAmbiguous", resourceCulture);


    public static string Warning_InstallEstimationMetadataMismatchPrefix => ResourceManager.GetString("Warning_InstallEstimationMetadataMismatchPrefix", resourceCulture);


    public static string Warning_InstallEstimationMetadataMismatch => ResourceManager.GetString("Warning_InstallEstimationMetadataMismatch", resourceCulture);


    public static string Warning_InstallEstimationReinstallNotImprovedPrefix => ResourceManager.GetString("Warning_InstallEstimationReinstallNotImprovedPrefix", resourceCulture);


    public static string Warning_InstallEstimationReinstallNotImproved => ResourceManager.GetString("Warning_InstallEstimationReinstallNotImproved", resourceCulture);


    public static string Warning_InstalledDestinationAmbiguous => ResourceManager.GetString("Warning_InstalledDestinationAmbiguous", resourceCulture);


    public static string Warning_InstalledDestinationAutoAppliedAmbiguous => ResourceManager.GetString("Warning_InstalledDestinationAutoAppliedAmbiguous", resourceCulture);


    public static string Warning_InstalledDestinationResolveFailed => ResourceManager.GetString("Warning_InstalledDestinationResolveFailed", resourceCulture);


    public static string Warning_UnsupportedResourcePath => ResourceManager.GetString("Warning_UnsupportedResourcePath", resourceCulture);


    public static string AppSchemaRepairWarningMessage => ResourceManager.GetString("AppSchemaRepairWarningMessage", resourceCulture);


    public static string AppSchemaRepairWarningTitle => ResourceManager.GetString("AppSchemaRepairWarningTitle", resourceCulture);


    public static string Warning_DuplicateBmsFile => ResourceManager.GetString("Warning_DuplicateBmsFile", resourceCulture);


    public static string Warning_ZeroNoteMismatch => ResourceManager.GetString("Warning_ZeroNoteMismatch", resourceCulture);


    public static string Warning_ChartInfoParseFailure => ResourceManager.GetString("Warning_ChartInfoParseFailure", resourceCulture);


    public static string Warning_Lr2PathEncodingUnsupported => ResourceManager.GetString("Warning_Lr2PathEncodingUnsupported", resourceCulture);


    public static string Warning_Lr2PathTooLong => ResourceManager.GetString("Warning_Lr2PathTooLong", resourceCulture);


    public static string Warning_Lr2ResourcePathUnsupported => ResourceManager.GetString("Warning_Lr2ResourcePathUnsupported", resourceCulture);


    public static string Warning_Lr2ResourcePathTooLong => ResourceManager.GetString("Warning_Lr2ResourcePathTooLong", resourceCulture);


    public static string Chart_info_parse_errors => ResourceManager.GetString("Chart_info_parse_errors", resourceCulture);


    public static string Header_InstallDstTitle => ResourceManager.GetString("Header_InstallDstTitle", resourceCulture);


    public static string Header_InstallDstArtist => ResourceManager.GetString("Header_InstallDstArtist", resourceCulture);


    public static string Error_FileNotFound => ResourceManager.GetString("Error_FileNotFound", resourceCulture);


    public static string Error_DirectoryNotFound => ResourceManager.GetString("Error_DirectoryNotFound", resourceCulture);


    public static string NewFolderName => ResourceManager.GetString("NewFolderName", resourceCulture);


    public static string Error_LR2SongDBNotFound => ResourceManager.GetString("Error_LR2SongDBNotFound", resourceCulture);


    public static string Error_LR2ScoreDBNotFound => ResourceManager.GetString("Error_LR2ScoreDBNotFound", resourceCulture);


    public static string Error_IRCacheDirNotFound => ResourceManager.GetString("Error_IRCacheDirNotFound", resourceCulture);


    public static string Error_PathTooLong => ResourceManager.GetString("Error_PathTooLong", resourceCulture);

    public static string Error_InvalidBeatorajaScoreDbPath => ResourceManager.GetString("Error_InvalidBeatorajaScoreDbPath", resourceCulture);

    public static string Error_InvalidBeatorajaBmtTablePath => ResourceManager.GetString("Error_InvalidBeatorajaBmtTablePath", resourceCulture);

    public static string Error_InvalidBeatorajaRootPath => ResourceManager.GetString("Error_InvalidBeatorajaRootPath", resourceCulture);


    public static string Warn_LR2LeapYearFolderDetected => ResourceManager.GetString("Warn_LR2LeapYearFolderDetected", resourceCulture);


    public static string Error_FailedToChangeDate => ResourceManager.GetString("Error_FailedToChangeDate", resourceCulture);


    public static string Warn_LR2LeapYearBugDetected => ResourceManager.GetString("Warn_LR2LeapYearBugDetected", resourceCulture);


    public static string Error_InitializationFailed => ResourceManager.GetString("Error_InitializationFailed", resourceCulture);


    public static string Error_BmsLoadFailedSkip => ResourceManager.GetString("Error_BmsLoadFailedSkip", resourceCulture);


    public static string Warning_WavFilesNotFound => ResourceManager.GetString("Warning_WavFilesNotFound", resourceCulture);


    public static string Warning_BgaFilesNotFound => ResourceManager.GetString("Warning_BgaFilesNotFound", resourceCulture);


    public static string Warning_MovieFilesNotFound => ResourceManager.GetString("Warning_MovieFilesNotFound", resourceCulture);


    public static string Warning_StagefileNotFound => ResourceManager.GetString("Warning_StagefileNotFound", resourceCulture);


    public static string Warning_BackbmpNotFound => ResourceManager.GetString("Warning_BackbmpNotFound", resourceCulture);


    public static string Warning_BannerNotFound => ResourceManager.GetString("Warning_BannerNotFound", resourceCulture);


    public static string Warn_InstallAbortedFilesNotFound => ResourceManager.GetString("Warn_InstallAbortedFilesNotFound", resourceCulture);


    public static string Error_InstallFailed => ResourceManager.GetString("Error_InstallFailed", resourceCulture);


    public static string Error_FolderDeleteFailed => ResourceManager.GetString("Error_FolderDeleteFailed", resourceCulture);


    public static string Confirm_NormalInstallOverride => ResourceManager.GetString("Confirm_NormalInstallOverride", resourceCulture);


    public static string Confirm_NormalInstallTitle => ResourceManager.GetString("Confirm_NormalInstallTitle", resourceCulture);


    public static string Confirm_SelectedPlaylistUrlDownload => ResourceManager.GetString("Confirm_SelectedPlaylistUrlDownload", resourceCulture);


    public static string Msg_SelectedPlaylistUrlDownloadResult => ResourceManager.GetString("Msg_SelectedPlaylistUrlDownloadResult", resourceCulture);


    public static string Warn_SelectedPlaylistUrlDownloadNoTargets => ResourceManager.GetString("Warn_SelectedPlaylistUrlDownloadNoTargets", resourceCulture);


    public static string Warn_SelectedPlaylistUrlDownloadBlockedByInstallQueue => ResourceManager.GetString("Warn_SelectedPlaylistUrlDownloadBlockedByInstallQueue", resourceCulture);


    public static string Warn_DropInstallBlockedByPlaylistUrlDownload => ResourceManager.GetString("Warn_DropInstallBlockedByPlaylistUrlDownload", resourceCulture);


    public static string Warn_PendingPackageNotFound => ResourceManager.GetString("Warn_PendingPackageNotFound", resourceCulture);


    public static string Warn_InvalidInstallPath => ResourceManager.GetString("Warn_InvalidInstallPath", resourceCulture);


    public static string Warn_InstallDirNotFound => ResourceManager.GetString("Warn_InstallDirNotFound", resourceCulture);


    public static string Warn_InstallDirMustContainBms => ResourceManager.GetString("Warn_InstallDirMustContainBms", resourceCulture);


    public static string Error_BmsFolderMergeFailed => ResourceManager.GetString("Error_BmsFolderMergeFailed", resourceCulture);


    public static string Confirm_DuplicateReinstallSkipped => ResourceManager.GetString("Confirm_DuplicateReinstallSkipped", resourceCulture);


    public static string Warn_DriveRootBmsSkipped => ResourceManager.GetString("Warn_DriveRootBmsSkipped", resourceCulture);


    public static string Error_RenameFailed => ResourceManager.GetString("Error_RenameFailed", resourceCulture);


    public static string Warn_CannotRenameRootFolder => ResourceManager.GetString("Warn_CannotRenameRootFolder", resourceCulture);


    public static string Warn_RenameFolderNotExists => ResourceManager.GetString("Warn_RenameFolderNotExists", resourceCulture);


    public static string Error_MoveDestRootNotFound => ResourceManager.GetString("Error_MoveDestRootNotFound", resourceCulture);


    public static string Warn_DriveRootCannotChangeRoot => ResourceManager.GetString("Warn_DriveRootCannotChangeRoot", resourceCulture);


    public static string Warn_MoveDestAlreadyExists => ResourceManager.GetString("Warn_MoveDestAlreadyExists", resourceCulture);


    public static string Error_FolderMoveFailed => ResourceManager.GetString("Error_FolderMoveFailed", resourceCulture);


    public static string Warn_RenameDestAlreadyExists => ResourceManager.GetString("Warn_RenameDestAlreadyExists", resourceCulture);

    public static string Warn_Lr2SongDbSyncRunning => ResourceManager.GetString("Warn_Lr2SongDbSyncRunning", resourceCulture);

    public static string Warn_EverythingFallbackScanUsed => ResourceManager.GetString("Warn_EverythingFallbackScanUsed", resourceCulture);

    public static string Warn_no_pending_installed_only_packages => ResourceManager.GetString("Warn_no_pending_installed_only_packages", resourceCulture);

    public static string Warn_no_pending_charts => ResourceManager.GetString("Warn_no_pending_charts", resourceCulture);

    public static string Warn_estimated_install_cleanup_only_completed => ResourceManager.GetString("Warn_estimated_install_cleanup_only_completed", resourceCulture);

    public static string Warn_overwrite_pending_installed_only_packages_summary => ResourceManager.GetString("Warn_overwrite_pending_installed_only_packages_summary", resourceCulture);


    public static string Error_BmsFileMoveFailed => ResourceManager.GetString("Error_BmsFileMoveFailed", resourceCulture);


    public static string Confirm_DeleteFolderWithNoBms => ResourceManager.GetString("Confirm_DeleteFolderWithNoBms", resourceCulture);


    /// <summary>
    /// 保留削除時に、BMSが空になるフォルダをまとめて削除するかどうかの確認文言を取得します。
    /// </summary>
    public static string Confirm_DeletePendingFolderWhenNoBms => ResourceManager.GetString("Confirm_DeletePendingFolderWhenNoBms", resourceCulture);


    public static string Error_FolderOrTrashDeleteFailed => ResourceManager.GetString("Error_FolderOrTrashDeleteFailed", resourceCulture);


    public static string Error_BmsFileDeleteFailed => ResourceManager.GetString("Error_BmsFileDeleteFailed", resourceCulture);


    public static string Error_RenameDestFileNotFound => ResourceManager.GetString("Error_RenameDestFileNotFound", resourceCulture);


    public static string Error_OldPathMismatch => ResourceManager.GetString("Error_OldPathMismatch", resourceCulture);


    public static string Error_RenameDestDirNotFound => ResourceManager.GetString("Error_RenameDestDirNotFound", resourceCulture);


    public static string Error_SchemeMustBeBemusic => ResourceManager.GetString("Error_SchemeMustBeBemusic", resourceCulture);


    public static string Error_UnsupportedURI => ResourceManager.GetString("Error_UnsupportedURI", resourceCulture);


    public static string Error_UnsupportedType => ResourceManager.GetString("Error_UnsupportedType", resourceCulture);


    public static string InsaneBMSDiffTable_Easy => ResourceManager.GetString("InsaneBMSDiffTable_Easy", resourceCulture);


    public static string InsaneBMSDiffTable_Normal => ResourceManager.GetString("InsaneBMSDiffTable_Normal", resourceCulture);


    public static string InsaneBMSDiffTable_Hard => ResourceManager.GetString("InsaneBMSDiffTable_Hard", resourceCulture);


    public static string InsaneBMSDiffTable_FC => ResourceManager.GetString("InsaneBMSDiffTable_FC", resourceCulture);


    public static string Error_ScoreDBConnectionFailed => ResourceManager.GetString("Error_ScoreDBConnectionFailed", resourceCulture);


    public static string Error_LR2IDOrScoreDBFailed => ResourceManager.GetString("Error_LR2IDOrScoreDBFailed", resourceCulture);


    public static string Warn_RecommendUpdateFailed => ResourceManager.GetString("Warn_RecommendUpdateFailed", resourceCulture);


    public static string Warn_RecommendFetchFailed => ResourceManager.GetString("Warn_RecommendFetchFailed", resourceCulture);


    public static string Error_RecommendFetchFailed => ResourceManager.GetString("Error_RecommendFetchFailed", resourceCulture);


    public static string RecommendFormat => ResourceManager.GetString("RecommendFormat", resourceCulture);


    public static string Recommend_SkillUpdatedMessage => ResourceManager.GetString("Recommend_SkillUpdatedMessage", resourceCulture);


    public static string Recommend_SkillUpdatedTitle => ResourceManager.GetString("Recommend_SkillUpdatedTitle", resourceCulture);


    public static string Error_LocalScoreDataNotFetched => ResourceManager.GetString("Error_LocalScoreDataNotFetched", resourceCulture);


    public static string Warn_CustomFolderOutputFailed => ResourceManager.GetString("Warn_CustomFolderOutputFailed", resourceCulture);


    public static string Warn_FileOrDirDeleteFailed => ResourceManager.GetString("Warn_FileOrDirDeleteFailed", resourceCulture);


    public static string Error_PlaylistAlreadyExists => ResourceManager.GetString("Error_PlaylistAlreadyExists", resourceCulture);


    public static string Playlist_import_result_title => ResourceManager.GetString("Playlist_import_result_title", resourceCulture);


    public static string Playlist_import_result_summary_format => ResourceManager.GetString("Playlist_import_result_summary_format", resourceCulture);


    public static string Playlist_import_result_skipped_header => ResourceManager.GetString("Playlist_import_result_skipped_header", resourceCulture);


    public static string Playlist_import_result_failed_header => ResourceManager.GetString("Playlist_import_result_failed_header", resourceCulture);

    public static string Playlist_uri_input_no_valid_uri => ResourceManager.GetString("Playlist_uri_input_no_valid_uri", resourceCulture);


    public static string Playlist_uri_input_invalid_lines_format => ResourceManager.GetString("Playlist_uri_input_invalid_lines_format", resourceCulture);


    public static string Error_OutputDirNameEmpty => ResourceManager.GetString("Error_OutputDirNameEmpty", resourceCulture);


    public static string Error_URIMustBeAbsolute => ResourceManager.GetString("Error_URIMustBeAbsolute", resourceCulture);


    public static string Error_InvalidBackupData => ResourceManager.GetString("Error_InvalidBackupData", resourceCulture);


    public static string Error_ParseFailed => ResourceManager.GetString("Error_ParseFailed", resourceCulture);


    public static string Warn_CustomFolderOutputDirInvalid => ResourceManager.GetString("Warn_CustomFolderOutputDirInvalid", resourceCulture);


    public static string Error_InstallComponentFileNotFound => ResourceManager.GetString("Error_InstallComponentFileNotFound", resourceCulture);


    public static string Msg_merge_bms_completed => ResourceManager.GetString("Msg_merge_bms_completed", resourceCulture);


    public static string Msg_cleanup_duplicate_hash => ResourceManager.GetString("Msg_cleanup_duplicate_hash", resourceCulture);


    public static string Warn_ArchiveExtractFailed => ResourceManager.GetString("Warn_ArchiveExtractFailed", resourceCulture);


    public static string Warn_ArchiveTimestampRestoreFailed => ResourceManager.GetString("Warn_ArchiveTimestampRestoreFailed", resourceCulture);


    public static string Warn_ArchiveLastWriteTimeMissingDetail => ResourceManager.GetString("Warn_ArchiveLastWriteTimeMissingDetail", resourceCulture);


    public static string Warn_ArchiveBundledSevenZipMissingDetail => ResourceManager.GetString("Warn_ArchiveBundledSevenZipMissingDetail", resourceCulture);
    public static string SettingValidation_SectionMessageFormat => ResourceManager.GetString("SettingValidation_SectionMessageFormat", resourceCulture);

    public static string Error_InvalidTableListUri => ResourceManager.GetString("Error_InvalidTableListUri", resourceCulture);

    public static string Error_InvalidFolderNameFormat => ResourceManager.GetString("Error_InvalidFolderNameFormat", resourceCulture);

    public static string Warn_DisableShiftJisFolderNames => ResourceManager.GetString("Warn_DisableShiftJisFolderNames", resourceCulture);

    public static string Warn_EncoderExecutableNotFoundFormat => ResourceManager.GetString("Warn_EncoderExecutableNotFoundFormat", resourceCulture);

    public static string Error_LR2UnicodePathUnsupported => ResourceManager.GetString("Error_LR2UnicodePathUnsupported", resourceCulture);

    public static string Confirm_RemoveAdditionalOutputBaseReferencedFormat => ResourceManager.GetString("Confirm_RemoveAdditionalOutputBaseReferencedFormat", resourceCulture);

    public static string Error_CustomFolderOutputBaseNameEmpty => ResourceManager.GetString("Error_CustomFolderOutputBaseNameEmpty", resourceCulture);

    public static string Error_AdditionalOutputBaseNameMatchesNormalOutput => ResourceManager.GetString("Error_AdditionalOutputBaseNameMatchesNormalOutput", resourceCulture);

    public static string Error_AdditionalOutputBaseNameDuplicate => ResourceManager.GetString("Error_AdditionalOutputBaseNameDuplicate", resourceCulture);

    public static string Validation_AdditionalOutputBaseNameEmpty => ResourceManager.GetString("Validation_AdditionalOutputBaseNameEmpty", resourceCulture);

    public static string Validation_OutputBaseNameDuplicateFormat => ResourceManager.GetString("Validation_OutputBaseNameDuplicateFormat", resourceCulture);

    public static string Validation_AdditionalOutputBasesNested => ResourceManager.GetString("Validation_AdditionalOutputBasesNested", resourceCulture);

    public static string Label_NormalOutputBaseFolder => ResourceManager.GetString("Label_NormalOutputBaseFolder", resourceCulture);

    public static string Label_AdditionalOutputBaseFolder => ResourceManager.GetString("Label_AdditionalOutputBaseFolder", resourceCulture);

    public static string Error_SjisDirectoryPathNotSelectedFormat => ResourceManager.GetString("Error_SjisDirectoryPathNotSelectedFormat", resourceCulture);

    public static string Error_SjisDirectoryPathContainsUnsupportedCharsFormat => ResourceManager.GetString("Error_SjisDirectoryPathContainsUnsupportedCharsFormat", resourceCulture);

    public static string Validation_NormalOutputBaseNameEmpty => ResourceManager.GetString("Validation_NormalOutputBaseNameEmpty", resourceCulture);

    public static string Validation_NormalAndAdditionalOutputBasesNested => ResourceManager.GetString("Validation_NormalAndAdditionalOutputBasesNested", resourceCulture);

    public static string Label_NormalOutputBase => ResourceManager.GetString("Label_NormalOutputBase", resourceCulture);

    public static string Label_PreviousNormalOutputBase => ResourceManager.GetString("Label_PreviousNormalOutputBase", resourceCulture);

    public static string Label_PreviousAdditionalOutputBase => ResourceManager.GetString("Label_PreviousAdditionalOutputBase", resourceCulture);

    public static string Validation_NormalAndRootOutputBasesNested => ResourceManager.GetString("Validation_NormalAndRootOutputBasesNested", resourceCulture);

    public static string Validation_PreviousManagedRootNestedFormat => ResourceManager.GetString("Validation_PreviousManagedRootNestedFormat", resourceCulture);

    public static string Label_RootOutputBaseFolder => ResourceManager.GetString("Label_RootOutputBaseFolder", resourceCulture);

    public static string Validation_RootOutputBaseNameEmpty => ResourceManager.GetString("Validation_RootOutputBaseNameEmpty", resourceCulture);

    public static string Validation_RootAndNormalOutputBasesNested => ResourceManager.GetString("Validation_RootAndNormalOutputBasesNested", resourceCulture);

    public static string Validation_RootAndAdditionalOutputBasesNested => ResourceManager.GetString("Validation_RootAndAdditionalOutputBasesNested", resourceCulture);

    public static string Label_RootOutputBase => ResourceManager.GetString("Label_RootOutputBase", resourceCulture);

    public static string Label_PreviousRootOutputBase => ResourceManager.GetString("Label_PreviousRootOutputBase", resourceCulture);

    public static string Validation_OutputBaseSameAsBmsRootFormat => ResourceManager.GetString("Validation_OutputBaseSameAsBmsRootFormat", resourceCulture);

    public static string Validation_OutputBaseNestedWithBmsRootFormat => ResourceManager.GetString("Validation_OutputBaseNestedWithBmsRootFormat", resourceCulture);

    public static string Error_AdditionalOutputBaseNestedWithPreviousAdditional => ResourceManager.GetString("Error_AdditionalOutputBaseNestedWithPreviousAdditional", resourceCulture);

    public static string Error_AdditionalOutputBaseSameAsBmsRoot => ResourceManager.GetString("Error_AdditionalOutputBaseSameAsBmsRoot", resourceCulture);

    public static string Error_AdditionalOutputBaseNestedWithBmsRoot => ResourceManager.GetString("Error_AdditionalOutputBaseNestedWithBmsRoot", resourceCulture);

    public static string Error_AdditionalOutputBaseNestedWithOutputBaseFormat => ResourceManager.GetString("Error_AdditionalOutputBaseNestedWithOutputBaseFormat", resourceCulture);

    public static string Error_ManagedCustomFolderOutputCannotBeAddedAsBmsRoot => ResourceManager.GetString("Error_ManagedCustomFolderOutputCannotBeAddedAsBmsRoot", resourceCulture);

    public static string Error_CannotRemoveBmsInstallDir => ResourceManager.GetString("Error_CannotRemoveBmsInstallDir", resourceCulture);

    public static string Error_CannotRemoveCustomFolderOutputDir => ResourceManager.GetString("Error_CannotRemoveCustomFolderOutputDir", resourceCulture);

    public static string Error_CannotRemoveAdditionalOutputBaseDir => ResourceManager.GetString("Error_CannotRemoveAdditionalOutputBaseDir", resourceCulture);

    public static string Error_CannotRemoveRootCustomFolderOutputDir => ResourceManager.GetString("Error_CannotRemoveRootCustomFolderOutputDir", resourceCulture);

    public static string Msg_LR2ConfigBackupEnabledNextStartup => ResourceManager.GetString("Msg_LR2ConfigBackupEnabledNextStartup", resourceCulture);

    public static string Error_InvalidLR2SongDbOrConfigPath => ResourceManager.GetString("Error_InvalidLR2SongDbOrConfigPath", resourceCulture);

    public static string Error_InvalidStagefilePath => ResourceManager.GetString("Error_InvalidStagefilePath", resourceCulture);

    public static string Error_InvalidUBMPlayExecutablePath => ResourceManager.GetString("Error_InvalidUBMPlayExecutablePath", resourceCulture);

    public static string Error_InvalidLR2RootPath => ResourceManager.GetString("Error_InvalidLR2RootPath", resourceCulture);

    public static string Error_LR2ExecutableNotFoundFormat => ResourceManager.GetString("Error_LR2ExecutableNotFoundFormat", resourceCulture);

    public static string Error_InvalidLR2WindowSize => ResourceManager.GetString("Error_InvalidLR2WindowSize", resourceCulture);

    public static string Error_InvalidBMIIDXViewPath => ResourceManager.GetString("Error_InvalidBMIIDXViewPath", resourceCulture);

    public static string Error_CustomFolderOutputPathNotSet => ResourceManager.GetString("Error_CustomFolderOutputPathNotSet", resourceCulture);

    public static string Error_CustomFolderRootOutputPathNotSet => ResourceManager.GetString("Error_CustomFolderRootOutputPathNotSet", resourceCulture);

    public static string Error_TableListUrlNotSet => ResourceManager.GetString("Error_TableListUrlNotSet", resourceCulture);

    public static string Error_LR2BackupPathNotSet => ResourceManager.GetString("Error_LR2BackupPathNotSet", resourceCulture);

    public static string Error_OutputFolderNameEmptyOrDuplicateChangePlaylist => ResourceManager.GetString("Error_OutputFolderNameEmptyOrDuplicateChangePlaylist", resourceCulture);

    public static string Error_InvalidPageUriAbsoluteRequired => ResourceManager.GetString("Error_InvalidPageUriAbsoluteRequired", resourceCulture);

    public static string Error_InvalidHeaderUri => ResourceManager.GetString("Error_InvalidHeaderUri", resourceCulture);

    public static string Error_InvalidDataUri => ResourceManager.GetString("Error_InvalidDataUri", resourceCulture);

    public static string Error_InvalidPageOrHeaderUri => ResourceManager.GetString("Error_InvalidPageOrHeaderUri", resourceCulture);

    public static string Confirm_EnablePlaylistSyncModeLoseLocalChanges => ResourceManager.GetString("Confirm_EnablePlaylistSyncModeLoseLocalChanges", resourceCulture);

    public static string Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied => ResourceManager.GetString("Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied", resourceCulture);

    public static string Error_OutputFolderNameEmptyOrDuplicateCheckInput => ResourceManager.GetString("Error_OutputFolderNameEmptyOrDuplicateCheckInput", resourceCulture);

    public static string Statusbar_progress_phase_playlist_load => ResourceManager.GetString("Statusbar_progress_phase_playlist_load", resourceCulture);

    public static string Statusbar_progress_phase_playlist_loading => ResourceManager.GetString("Statusbar_progress_phase_playlist_loading", resourceCulture);

    public static string Statusbar_progress_detail_separator_format => ResourceManager.GetString("Statusbar_progress_detail_separator_format", resourceCulture);

    public static string Msg_failed_rename_playlist_folder => ResourceManager.GetString("Msg_failed_rename_playlist_folder", resourceCulture);

    internal Resources()
    {
    }
}
