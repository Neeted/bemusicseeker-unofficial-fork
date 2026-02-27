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

    public static string Appearance => ResourceManager.GetString("Appearance", resourceCulture);

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

    public static string Details => ResourceManager.GetString("Details", resourceCulture);

    public static string Details_message_diag => ResourceManager.GetString("Details_message_diag", resourceCulture);

    public static string Details_show_diag_chartview => ResourceManager.GetString("Details_show_diag_chartview", resourceCulture);

    public static string Details_show_diag_diff_install => ResourceManager.GetString("Details_show_diag_diff_install", resourceCulture);

    public static string Details_show_diag_recommend => ResourceManager.GetString("Details_show_diag_recommend", resourceCulture);

    public static string Details_test_download_and_install => ResourceManager.GetString("Details_test_download_and_install", resourceCulture);

    public static string Details_test_fast_sort_list_view => ResourceManager.GetString("Details_test_fast_sort_list_view", resourceCulture);

    public static string Details_test_column_virtualization_list_view => ResourceManager.GetString("Details_test_column_virtualization_list_view", resourceCulture);

    public static string Details_test_keep_installable_pending => ResourceManager.GetString("Details_test_keep_installable_pending", resourceCulture);

    public static string Details_test_notcalc_offrank => ResourceManager.GetString("Details_test_notcalc_offrank", resourceCulture);

    public static string Details_test_notcheck_playlists => ResourceManager.GetString("Details_test_notcheck_playlists", resourceCulture);

    public static string Details_test_db_read_optimized_pragmas => ResourceManager.GetString("Details_test_db_read_optimized_pragmas", resourceCulture);

    public static string Details_test_startup_expand_playlist_tree => ResourceManager.GetString("Details_test_startup_expand_playlist_tree", resourceCulture);

    public static string Details_test_startup_select_install_pending => ResourceManager.GetString("Details_test_startup_select_install_pending", resourceCulture);

    public static string Details_test_smart_component_overwrite => ResourceManager.GetString("Details_test_smart_component_overwrite", resourceCulture);

    public static string Details_test_notscan => ResourceManager.GetString("Details_test_notscan", resourceCulture);

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

    public static string DirPath_LR2 => ResourceManager.GetString("DirPath_LR2", resourceCulture);

    public static string Download => ResourceManager.GetString("Download", resourceCulture);

    public static string Error => ResourceManager.GetString("Error", resourceCulture);

    public static string Estimate_install_loc => ResourceManager.GetString("Estimate_install_loc", resourceCulture);

    public static string Estimate_merge_loc => ResourceManager.GetString("Estimate_merge_loc", resourceCulture);

    public static string Estimate_reinstall_loc => ResourceManager.GetString("Estimate_reinstall_loc", resourceCulture);

    public static string Exclusive => ResourceManager.GetString("Exclusive", resourceCulture);

    public static string Export => ResourceManager.GetString("Export", resourceCulture);

    public static string Failure => ResourceManager.GetString("Failure", resourceCulture);

    public static string File => ResourceManager.GetString("File", resourceCulture);

    public static string File_scan => ResourceManager.GetString("File_scan", resourceCulture);

    public static string FilePath_configXml => ResourceManager.GetString("FilePath_configXml", resourceCulture);

    public static string FilePath_songDB => ResourceManager.GetString("FilePath_songDB", resourceCulture);

    public static string FilePath_StageFile => ResourceManager.GetString("FilePath_StageFile", resourceCulture);

    public static string Fix_character_encoding => ResourceManager.GetString("Fix_character_encoding", resourceCulture);

    public static string Fix_installed_loc => ResourceManager.GetString("Fix_installed_loc", resourceCulture);

    public static string Fixed => ResourceManager.GetString("Fixed", resourceCulture);

    public static string Folder => ResourceManager.GetString("Folder", resourceCulture);

    public static string Force_install => ResourceManager.GetString("Force_install", resourceCulture);

    public static string Full_scan_check => ResourceManager.GetString("Full_scan_check", resourceCulture);

    public static string General => ResourceManager.GetString("General", resourceCulture);

    public static string Ignore_list => ResourceManager.GetString("Ignore_list", resourceCulture);

    public static string Import => ResourceManager.GetString("Import", resourceCulture);

    public static string Information => ResourceManager.GetString("Information", resourceCulture);

    public static string Init_column_setting => ResourceManager.GetString("Init_column_setting", resourceCulture);

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

    public static string Msg_load_recommended_tables_error => ResourceManager.GetString("Msg_load_recommended_tables_error", resourceCulture);

    public static string Msg_load_recommended_tables_readonly_mode => ResourceManager.GetString("Msg_load_recommended_tables_readonly_mode", resourceCulture);

    public static string Msg_load_recommended_tables_update_mode => ResourceManager.GetString("Msg_load_recommended_tables_update_mode", resourceCulture);

    public static string Msg_manual_installation => ResourceManager.GetString("Msg_manual_installation", resourceCulture);

    public static string Msg_estimate_merge_confirm => ResourceManager.GetString("Msg_estimate_merge_confirm", resourceCulture);

    public static string Msg_merge_bms_destination => ResourceManager.GetString("Msg_merge_bms_destination", resourceCulture);

    public static string Msg_open_install_destination_missing => ResourceManager.GetString("Msg_open_install_destination_missing", resourceCulture);

    public static string Msg_open_install_destination_multiple_selected => ResourceManager.GetString("Msg_open_install_destination_multiple_selected", resourceCulture);

    public static string Msg_open_install_destination_not_found => ResourceManager.GetString("Msg_open_install_destination_not_found", resourceCulture);

    public static string Msg_merge_bms_folder => ResourceManager.GetString("Msg_merge_bms_folder", resourceCulture);

    public static string Msg_merge_bms_target => ResourceManager.GetString("Msg_merge_bms_target", resourceCulture);

    public static string Msg_move_to_other_root => ResourceManager.GetString("Msg_move_to_other_root", resourceCulture);

    public static string Msg_move_to_recycle => ResourceManager.GetString("Msg_move_to_recycle", resourceCulture);

    public static string Msg_override_level_error_recommended => ResourceManager.GetString("Msg_override_level_error_recommended", resourceCulture);

    public static string Msg_override_level_warning => ResourceManager.GetString("Msg_override_level_warning", resourceCulture);

    public static string Msg_ranking_cache_notfound => ResourceManager.GetString("Msg_ranking_cache_notfound", resourceCulture);

    public static string Msg_register_chart => ResourceManager.GetString("Msg_register_chart", resourceCulture);

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

    public static string Open_install_destination => ResourceManager.GetString("Open_install_destination", resourceCulture);

    public static string Open_lr2ir => ResourceManager.GetString("Open_lr2ir", resourceCulture);

    public static string Open_page => ResourceManager.GetString("Open_page", resourceCulture);

    public static string Open_Url => ResourceManager.GetString("Open_Url", resourceCulture);

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

    public static string Playlist_output_note => ResourceManager.GetString("Playlist_output_note", resourceCulture);

    public static string Playlist_output_root => ResourceManager.GetString("Playlist_output_root", resourceCulture);

    public static string Playlist_table_uri => ResourceManager.GetString("Playlist_table_uri", resourceCulture);

    public static string PlaylistProp_asc => ResourceManager.GetString("PlaylistProp_asc", resourceCulture);

    public static string PlaylistProp_datauri => ResourceManager.GetString("PlaylistProp_datauri", resourceCulture);

    public static string PlaylistProp_desc => ResourceManager.GetString("PlaylistProp_desc", resourceCulture);

    public static string PlaylistProp_entrytype => ResourceManager.GetString("PlaylistProp_entrytype", resourceCulture);

    public static string PlaylistProp_folder_order => ResourceManager.GetString("PlaylistProp_folder_order", resourceCulture);

    public static string PlaylistProp_folder_prefix => ResourceManager.GetString("PlaylistProp_folder_prefix", resourceCulture);

    public static string PlaylistProp_folder_type => ResourceManager.GetString("PlaylistProp_folder_type", resourceCulture);

    public static string PlaylistProp_ftype_all => ResourceManager.GetString("PlaylistProp_ftype_all", resourceCulture);

    public static string PlaylistProp_ftype_alphabet => ResourceManager.GetString("PlaylistProp_ftype_alphabet", resourceCulture);

    public static string PlaylistProp_ftype_clear => ResourceManager.GetString("PlaylistProp_ftype_clear", resourceCulture);

    public static string PlaylistProp_ftype_desc => ResourceManager.GetString("PlaylistProp_ftype_desc", resourceCulture);

    public static string PlaylistProp_ftype_djlevel => ResourceManager.GetString("PlaylistProp_ftype_djlevel", resourceCulture);

    public static string PlaylistProp_ftype_etc => ResourceManager.GetString("PlaylistProp_ftype_etc", resourceCulture);

    public static string PlaylistProp_ftype_level => ResourceManager.GetString("PlaylistProp_ftype_level", resourceCulture);

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

    public static string Reload => ResourceManager.GetString("Reload", resourceCulture);

    public static string Remarks_URL => ResourceManager.GetString("Remarks_URL", resourceCulture);

    public static string Remove => ResourceManager.GetString("Remove", resourceCulture);

    public static string Remove_folder => ResourceManager.GetString("Remove_folder", resourceCulture);

    public static string Remove_ignore => ResourceManager.GetString("Remove_ignore", resourceCulture);

    public static string Remove_playlist => ResourceManager.GetString("Remove_playlist", resourceCulture);

    public static string Rename_folder => ResourceManager.GetString("Rename_folder", resourceCulture);

    public static string Rename_folder_auto => ResourceManager.GetString("Rename_folder_auto", resourceCulture);

    public static string Rename_invalid_ext => ResourceManager.GetString("Rename_invalid_ext", resourceCulture);

    public static string Rescan => ResourceManager.GetString("Rescan", resourceCulture);

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

    public static string Version_info => ResourceManager.GetString("Version_info", resourceCulture);

    public static string Warning => ResourceManager.GetString("Warning", resourceCulture);

    public static string Weekly => ResourceManager.GetString("Weekly", resourceCulture);

    public static string YouTube => ResourceManager.GetString("YouTube", resourceCulture);

    public static string Playlist_summary_header => ResourceManager.GetString("Playlist_summary_header", resourceCulture);

    public static string Playlist_summary_format => ResourceManager.GetString("Playlist_summary_format", resourceCulture);

    public static string MessageBoxTitle_Error => ResourceManager.GetString("MessageBoxTitle_Error", resourceCulture);


    public static string MessageBoxTitle_Warning => ResourceManager.GetString("MessageBoxTitle_Warning", resourceCulture);


    public static string MessageBoxTitle_Confirm => ResourceManager.GetString("MessageBoxTitle_Confirm", resourceCulture);


    public static string Error_NotMd5Hash => ResourceManager.GetString("Error_NotMd5Hash", resourceCulture);


    public static string Error_InvalidJsonObject => ResourceManager.GetString("Error_InvalidJsonObject", resourceCulture);


    public static string Error_LR2ScoreDBNotConnected => ResourceManager.GetString("Error_LR2ScoreDBNotConnected", resourceCulture);


    public static string Warning_AlreadyInstalled => ResourceManager.GetString("Warning_AlreadyInstalled", resourceCulture);


    public static string Warning_SingleBmsFile => ResourceManager.GetString("Warning_SingleBmsFile", resourceCulture);


    public static string Warning_DuplicateBmsFile => ResourceManager.GetString("Warning_DuplicateBmsFile", resourceCulture);


    public static string Error_FileNotFound => ResourceManager.GetString("Error_FileNotFound", resourceCulture);


    public static string Error_DirectoryNotFound => ResourceManager.GetString("Error_DirectoryNotFound", resourceCulture);


    public static string NewFolderName => ResourceManager.GetString("NewFolderName", resourceCulture);


    public static string Error_LR2SongDBNotFound => ResourceManager.GetString("Error_LR2SongDBNotFound", resourceCulture);


    public static string Error_LR2ScoreDBNotFound => ResourceManager.GetString("Error_LR2ScoreDBNotFound", resourceCulture);


    public static string Error_IRCacheDirNotFound => ResourceManager.GetString("Error_IRCacheDirNotFound", resourceCulture);


    public static string Error_PathTooLong => ResourceManager.GetString("Error_PathTooLong", resourceCulture);


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


    public static string Error_BmsFileMoveFailed => ResourceManager.GetString("Error_BmsFileMoveFailed", resourceCulture);


    public static string Confirm_DeleteFolderWithNoBms => ResourceManager.GetString("Confirm_DeleteFolderWithNoBms", resourceCulture);


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


    public static string Error_OutputDirNameEmpty => ResourceManager.GetString("Error_OutputDirNameEmpty", resourceCulture);


    public static string Error_URIMustBeAbsolute => ResourceManager.GetString("Error_URIMustBeAbsolute", resourceCulture);


    public static string Error_InvalidBackupData => ResourceManager.GetString("Error_InvalidBackupData", resourceCulture);


    public static string Error_ParseFailed => ResourceManager.GetString("Error_ParseFailed", resourceCulture);


    public static string Warn_CustomFolderOutputDirInvalid => ResourceManager.GetString("Warn_CustomFolderOutputDirInvalid", resourceCulture);


    public static string Error_InstallComponentFileNotFound => ResourceManager.GetString("Error_InstallComponentFileNotFound", resourceCulture);


    public static string Msg_merge_bms_completed => ResourceManager.GetString("Msg_merge_bms_completed", resourceCulture);


    public static string Msg_cleanup_duplicate_hash => ResourceManager.GetString("Msg_cleanup_duplicate_hash", resourceCulture);


    internal Resources()
    {
    }
}
