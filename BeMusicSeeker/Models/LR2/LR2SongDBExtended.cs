using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

public sealed class LR2SongDBExtended : LR2SongDB
{
    internal static IReadOnlyList<string> BeMusicSeekerOwnedTableNames { get; } =
    [
        SQLiteTable<install>.GetTableName(),
        SQLiteTable<maintenance>.GetTableName(),
        SQLiteTable<playlist>.GetTableName(),
        SQLiteTable<playlist_course>.GetTableName(),
        SQLiteTable<playlist_entry>.GetTableName(),
        SQLiteTable<chart_digest_map>.GetTableName(),
        SQLiteTable<chart_info>.GetTableName(),
        SQLiteTable<chart_info_parse_failure>.GetTableName(),
        SQLiteTable<chart_info_import_history>.GetTableName(),
        SQLiteTable<bmson_song>.GetTableName(),
        SQLiteTable<app_schema_version>.GetTableName(),
        SQLiteTable<lr2_full_generation_status>.GetTableName(),
        SQLiteTable<ir_score>.GetTableName(),
        SQLiteTable<ir_score_refresh_metadata>.GetTableName(),
        SQLiteTable<ir_data>.GetTableName()
    ];

    internal static IReadOnlyList<string> BeMusicSeekerOwnedNativeIndexNames { get; } =
    [
        "song_idx_folder",
        "song_idx_path_nocase"
    ];

    [Table("install")]
    public class install : SQLiteTable<install>
    {
        [PrimaryKey]
        public virtual string path { get; set; }

        public bool delete_parent { get; set; }
    }

    [Table("maintenance")]
    public class maintenance : SQLiteTable<maintenance>
    {
        [Indexed(Name = "hashidx_mtn")]
        public string hash { get; set; }

        [PrimaryKey]
        public string path { get; set; }

        public virtual string encoding { get; set; }

        public bool is_encoding_fixed { get; set; }

        public virtual int? wav_files_existing { get; set; }

        public virtual int? wav_files_defined { get; set; }

        public virtual int? bga_files_existing { get; set; }

        public virtual int? bga_files_defined { get; set; }

        public virtual int? movie_files_existing { get; set; }

        public virtual int? movie_files_defined { get; set; }

        public virtual bool? is_stagefile_existing { get; set; }

        public virtual bool? is_stagefile_defined { get; set; }

        public virtual bool? is_banner_existing { get; set; }

        public virtual bool? is_banner_defined { get; set; }

        public virtual bool? is_backbmp_existing { get; set; }

        public virtual bool? is_backbmp_defined { get; set; }

        public bool is_files_warning_ignored { get; set; }

        public int? lr2_path_warning_flags { get; set; }

        public int? lr2_chart_path_cp932_bytes { get; set; }

        public int? lr2_folder_scan_cp932_bytes { get; set; }

        public int? lr2_resource_warning_flags { get; set; }

        public int? lr2_resource_max_raw_cp932_bytes { get; set; }

        public int? lr2_resource_max_resolved_cp932_bytes { get; set; }

        public int? lr2_resource_unsupported_count { get; set; }
    }

    [Table("playlist")]
    public class playlist : SQLiteTable<playlist>
    {
        [Flags]
        public enum CustomFolderType
        {
            None = 0,
            UserFolder = 1,
            LevelFolder = 2,
            AlphabetFolder = 4,
            ClearFolder = 8,
            DJLevelFolder = 0x10,
            CategoryAllFolder = 0x20,
            OtherFolder = 0x40,
            AllFolders = 0x7F
        }

        public enum CustomFolderSortType
        {
            NONE,
            LEVEL,
            TITLE,
            ARTIST,
            SCORE,
            MISS,
            PLAYCOUNT,
            ADDDATE
        }

        [Flags]
        public enum EntryUnitType
        {
            File = 0,
            Folder = 1
        }

        private string _name;

        private string _symbol;

        [PrimaryKey]
        [AutoIncrement]
        public int? playlist_id { get; set; }

        public string name
        {
            get
            {
                return _name;
            }
            set
            {
                if (!(_name == value))
                {
                    _name = value;
                    RaisePropertyChanged("name");
                }
            }
        }

        public string symbol
        {
            get
            {
                return _symbol;
            }
            set
            {
                if (!(_symbol == value))
                {
                    _symbol = value;
                    RaisePropertyChanged("symbol");
                }
            }
        }

        public virtual string folder_order { get; protected set; }

        public CustomFolderSortType folder_sort_key { get; set; }

        public bool folder_sort_ascending { get; set; }

        public EntryUnitType entry_type { get; set; }

        public virtual string page_url { get; protected set; }

        public virtual string header_url { get; protected set; }

        public virtual string data_url { get; protected set; }

        public string tag { get; set; }

        public string header_sha256 { get; set; }

        public string data_sha256 { get; set; }

        public string compat_prefix { get; set; }

        public DateTime last_update { get; set; }

        public string org_name { get; set; }

        public string org_symbol { get; set; }

        public CustomFolderType ignore_folder_output { get; set; }

        public bool is_external_sync { get; set; }

        public string output_dir { get; protected set; }

        public bool is_root_folder { get; set; }
    }

    [Table("playlist_course")]
    public class playlist_course : SQLiteTable<playlist_course>
    {
        [PrimaryKey]
        [AutoIncrement]
        public int? course_id { get; set; }

        [NotNull]
        public int? playlist_id { get; set; }

        public int course_order { get; set; }

        public string course_json { get; set; }
    }

    [Table("playlist_entry")]
    public class playlist_entry : SQLiteTable<playlist_entry>
    {
        protected int? _playlist_id;

        protected string _md5;

        protected string _sha256;

        protected string _title = string.Empty;

        protected string _artist = string.Empty;

        private string _folder = string.Empty;

        [NotNull]
        public virtual int? playlist_id
        {
            get
            {
                return _playlist_id;
            }
            set
            {
                if (_playlist_id != value)
                {
                    _playlist_id = value;
                }
            }
        }

        public virtual string md5
        {
            get
            {
                return _md5;
            }
            protected set
            {
                if (!(_md5 == value))
                {
                    _md5 = value;
                }
            }
        }

        public virtual string sha256
        {
            get
            {
                return _sha256;
            }
            protected set
            {
                if (!(_sha256 == value))
                {
                    _sha256 = value;
                }
            }
        }

        public double? level { get; set; }

        public virtual string title
        {
            get
            {
                return title;
            }
            protected set
            {
                value ??= string.Empty;
                if (!(title == value))
                {
                    title = value;
                }
            }
        }

        public virtual string artist
        {
            get
            {
                return _artist;
            }
            protected set
            {
                value ??= string.Empty;
                if (!(_artist == value))
                {
                    _artist = value;
                }
            }
        }

        public string folder
        {
            get
            {
                return _folder;
            }
            set
            {
                value ??= string.Empty;
                if (!(_folder == value))
                {
                    _folder = value;
                }
            }
        }

        public string lr2_bmsid { get; set; }

        public virtual string url { get; protected set; }

        public virtual string url_diff { get; protected set; }

        public string name_diff { get; set; }

        public virtual string org_md5 { get; protected set; }

        public DateTime adddate { get; set; }

        public string comment { get; set; }

        public string memo { get; set; }

        public bool is_removed { get; set; }
    }

    [Table("chart_digest_map")]
    public class chart_digest_map : SQLiteTable<chart_digest_map>
    {
        private string _md5;

        private string _sha256;

        [PrimaryKey]
        public virtual string md5
        {
            get
            {
                return _md5;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _md5 = null;
                    return;
                }
                if (!LR2SongDB.md5HashRegex.IsMatch(value))
                {
                    throw new FormatException("MD5 HASH ではありません");
                }
                string normalized = value.ToLowerInvariant();
                if (_md5 != normalized)
                {
                    _md5 = normalized;
                }
            }
        }

        public virtual string sha256
        {
            get
            {
                return _sha256;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _sha256 = null;
                    return;
                }
                if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
                {
                    throw new FormatException("SHA256 HASH ではありません");
                }
                string normalized = value.ToLowerInvariant();
                if (_sha256 != normalized)
                {
                    _sha256 = normalized;
                }
            }
        }
    }

    /// <summary>
    /// アプリ独自の譜面解析メタデータです。
    /// LR2 が管理する song テーブルを変更せず、譜面内容の SHA-256 に紐づく情報を保持します。
    /// </summary>
    [Table("chart_info")]
    public class chart_info : SQLiteTable<chart_info>
    {
        private string _sha256;

        private string _md5;

        private string _charthash;

        /// <summary>
        /// 譜面ファイル内容の SHA-256 です。
        /// </summary>
        [PrimaryKey]
        public virtual string sha256
        {
            get
            {
                return _sha256;
            }
            set
            {
                _sha256 = NormalizeSha256(value);
            }
        }

        /// <summary>
        /// LR2 song.hash と照合するための MD5 です。
        /// </summary>
        public virtual string md5
        {
            get
            {
                return _md5;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _md5 = null;
                    return;
                }
                if (!LR2SongDB.md5HashRegex.IsMatch(value))
                {
                    throw new FormatException("MD5 HASH ではありません");
                }
                _md5 = value.ToLowerInvariant();
            }
        }

        /// <summary>
        /// 譜面配置内容から算出した beatoraja 互換の chart hash です。
        /// </summary>
        public virtual string charthash
        {
            get
            {
                return _charthash;
            }
            set
            {
                _charthash = NormalizeSha256(value);
            }
        }

        /// <summary>
        /// 表記レベルです。
        /// </summary>
        public int? level { get; set; }

        /// <summary>
        /// 譜面難易度種別です。
        /// </summary>
        public int? difficulty { get; set; }

        /// <summary>
        /// 譜面難易度種別が譜面内で明示されていたかどうかです。
        /// </summary>
        public bool difficulty_defined { get; set; }

        /// <summary>
        /// ノーツ数が最も多い BPM です。
        /// </summary>
        public double? mainbpm { get; set; }

        /// <summary>
        /// 最大 BPM です。
        /// </summary>
        public double? maxbpm { get; set; }

        /// <summary>
        /// 最小 BPM です。
        /// </summary>
        public double? minbpm { get; set; }

        /// <summary>
        /// 譜面終端までの長さをミリ秒で保持します。
        /// </summary>
        public int? length { get; set; }

        /// <summary>
        /// 鍵盤数に相当するモード値です。
        /// </summary>
        public int? mode { get; set; }

        /// <summary>
        /// beatoraja 互換の判定幅倍率値です。
        /// </summary>
        public int? judge { get; set; }

        /// <summary>
        /// LR2 song.bga へ反映する BGA 使用有無です。
        /// </summary>
        public int? bga { get; set; }

        /// <summary>
        /// LR2 song.exlevel へ反映する #EXLEVEL の raw 値です。
        /// </summary>
        public int? exlevel { get; set; }

        /// <summary>
        /// 譜面特徴を表す bit flag です。
        /// </summary>
        public int feature { get; set; }

        /// <summary>
        /// 総ノーツ数です。
        /// </summary>
        public int notes { get; set; }

        /// <summary>
        /// 通常鍵盤ノーツ数です。
        /// </summary>
        public int n { get; set; }

        /// <summary>
        /// ロング鍵盤ノーツ数です。
        /// </summary>
        public int ln { get; set; }

        /// <summary>
        /// 通常スクラッチノーツ数です。
        /// </summary>
        public int s { get; set; }

        /// <summary>
        /// ロングスクラッチノーツ数です。
        /// </summary>
        public int ls { get; set; }

        /// <summary>
        /// 表示・ソートに使う TOTAL 有効値です。
        /// </summary>
        public double? total { get; set; }

        /// <summary>
        /// TOTAL が譜面内で明示されていたかどうかです。
        /// </summary>
        public bool total_defined { get; set; }

        /// <summary>
        /// 平均密度です。
        /// </summary>
        public double? density { get; set; }

        /// <summary>
        /// 最大密度です。
        /// </summary>
        public double? peakdensity { get; set; }

        /// <summary>
        /// 終盤最大密度です。
        /// </summary>
        public double? enddensity { get; set; }

        /// <summary>
        /// beatoraja songinfo 互換のノーツ分布文字列です。
        /// </summary>
        public string distribution { get; set; }

        /// <summary>
        /// beatoraja songinfo 互換の変速列です。
        /// </summary>
        public string speedchange { get; set; }

        /// <summary>
        /// 一覧表示向けに保持する変速回数です。
        /// </summary>
        public int speedchange_count { get; set; }

        /// <summary>
        /// beatoraja songinfo 互換のレーン別ノーツ数です。
        /// </summary>
        public string lanenotes { get; set; }

        /// <summary>
        /// この行を生成した解析器のバージョンです。
        /// </summary>
        public int parser_version { get; set; }

        /// <summary>
        /// この行を最後に更新した UTC 時刻です。
        /// </summary>
        public DateTime updated_at { get; set; }

        internal static string NormalizeSha256ForInternalUse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            {
                throw new FormatException("SHA256 HASH ではありません");
            }
            return value.ToLowerInvariant();
        }

        private static string NormalizeSha256(string value)
        {
            return NormalizeSha256ForInternalUse(value);
        }
    }

    /// <summary>
    /// chart_info 解析に失敗した譜面を記録するアプリ独自テーブルです。
    /// 成功済みメタデータと混同しないよう、chart_info とは別テーブルで保持します。
    /// </summary>
    [Table("chart_info_parse_failure")]
    public class chart_info_parse_failure : SQLiteTable<chart_info_parse_failure>
    {
        private string _md5;

        private string _sha256;

        /// <summary>
        /// LR2 song.hash と対応する MD5 です。
        /// path が変わっても同一譜面内容なら再解析を避けるため主キーにします。
        /// </summary>
        [PrimaryKey]
        public virtual string md5
        {
            get
            {
                return _md5;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _md5 = null;
                    return;
                }
                if (!LR2SongDB.md5HashRegex.IsMatch(value))
                {
                    throw new FormatException("MD5 HASH ではありません");
                }
                _md5 = value.ToLowerInvariant();
            }
        }

        /// <summary>
        /// 譜面ファイル内容の SHA-256 です。
        /// </summary>
        public virtual string sha256
        {
            get
            {
                return _sha256;
            }
            set
            {
                _sha256 = chart_info.NormalizeSha256ForInternalUse(value);
            }
        }

        /// <summary>
        /// 最後に解析失敗した path です。
        /// </summary>
        public string path { get; set; }

        /// <summary>
        /// この失敗記録を生成した解析器のバージョンです。
        /// </summary>
        public int parser_version { get; set; }

        /// <summary>
        /// 失敗種別です。parse_failed または timeout を保存します。
        /// </summary>
        public string failure_kind { get; set; }

        /// <summary>
        /// 例外型名です。
        /// </summary>
        public string exception_type { get; set; }

        /// <summary>
        /// 表示用に短縮・正規化した例外メッセージです。
        /// </summary>
        public string message { get; set; }

        /// <summary>
        /// timeout 失敗時に使用した timeout ミリ秒です。
        /// </summary>
        public int? parse_timeout_ms { get; set; }

        /// <summary>
        /// この行を最後に更新した UTC 時刻です。
        /// </summary>
        public DateTime updated_at { get; set; }
    }

    [Table("app_schema_version")]
    public class app_schema_version : SQLiteTable<app_schema_version>
    {
        [PrimaryKey]
        public virtual string name { get; set; }

        public virtual int version { get; set; }
    }

    [Table("lr2_full_generation_status")]
    public class lr2_full_generation_status : SQLiteTable<lr2_full_generation_status>
    {
        [PrimaryKey]
        public virtual string name { get; set; }

        public virtual string status { get; set; }

        public virtual string signature { get; set; }

        public virtual string run_id { get; set; }

        public virtual int? processed_cursor { get; set; }

        public virtual int? total_count { get; set; }

        public virtual string stage { get; set; }

        public virtual string last_error { get; set; }

        public virtual DateTime updated_at { get; set; }

        public virtual DateTime? completed_at { get; set; }
    }

    [Table("ir_score_refresh_metadata")]
    public class ir_score_refresh_metadata : SQLiteTable<ir_score_refresh_metadata>
    {
        [PrimaryKey]
        public virtual int lr2id { get; set; }

        public virtual string score_digest_sha256 { get; set; }

        public virtual DateTime updated_at { get; set; }
    }

    /// <summary>
    /// 同梱 chart_info metadata bundle の import 済み履歴です。
    /// 同じ bundle を起動のたびに再 import しないために保持します。
    /// </summary>
    [Table("chart_info_import_history")]
    public class chart_info_import_history : SQLiteTable<chart_info_import_history>
    {
        /// <summary>
        /// bundle SHA-256 と parser version から作る import identity です。
        /// </summary>
        [PrimaryKey]
        public virtual string import_key { get; set; }

        public virtual string bundle_id { get; set; }

        public virtual string bundle_sha256 { get; set; }

        public int parser_version { get; set; }

        public int chart_info_imported_count { get; set; }

        public int chart_digest_imported_count { get; set; }

        public int failure_cleared_count { get; set; }

        public DateTime imported_at { get; set; }
    }

    [Table("bmson_song")]
    public class bmson_song : SQLiteTable<bmson_song>
    {
        private string _path;

        private string _folder;

        private string _title;

        private string _subtitle;

        private string _artist;

        private string _genre;

        private string _mode_hint;

        private string _md5;

        private string _sha256;

        private string _banner;

        private string _backbmp;

        private string _stagefile;

        private string _preview_music;

        [PrimaryKey]
        public virtual string path
        {
            get
            {
                return _path;
            }
            set
            {
                _path = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        public virtual string folder
        {
            get
            {
                return _folder;
            }
            set
            {
                _folder = value ?? string.Empty;
            }
        }

        public virtual string title
        {
            get
            {
                return _title;
            }
            set
            {
                _title = value ?? string.Empty;
            }
        }

        public virtual string subtitle
        {
            get
            {
                return _subtitle;
            }
            set
            {
                _subtitle = value ?? string.Empty;
            }
        }

        public virtual string artist
        {
            get
            {
                return _artist;
            }
            set
            {
                _artist = value ?? string.Empty;
            }
        }

        public virtual string genre
        {
            get
            {
                return _genre;
            }
            set
            {
                _genre = value ?? string.Empty;
            }
        }

        public double? level { get; set; }

        public virtual string mode_hint
        {
            get
            {
                return _mode_hint;
            }
            set
            {
                _mode_hint = value ?? string.Empty;
            }
        }

        public virtual string md5
        {
            get
            {
                return _md5;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _md5 = null;
                    return;
                }
                if (!LR2SongDB.md5HashRegex.IsMatch(value))
                {
                    throw new FormatException("MD5 HASH ではありません");
                }
                _md5 = value.ToLowerInvariant();
            }
        }

        public virtual string sha256
        {
            get
            {
                return _sha256;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _sha256 = null;
                    return;
                }
                if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
                {
                    throw new FormatException("SHA256 HASH ではありません");
                }
                _sha256 = value.ToLowerInvariant();
            }
        }

        public virtual string banner
        {
            get
            {
                return _banner;
            }
            set
            {
                _banner = value ?? string.Empty;
            }
        }

        public virtual string backbmp
        {
            get
            {
                return _backbmp;
            }
            set
            {
                _backbmp = value ?? string.Empty;
            }
        }

        public virtual string stagefile
        {
            get
            {
                return _stagefile;
            }
            set
            {
                _stagefile = value ?? string.Empty;
            }
        }

        public virtual string preview_music
        {
            get
            {
                return _preview_music;
            }
            set
            {
                _preview_music = value ?? string.Empty;
            }
        }

        public DateTime updated_at { get; set; }

        [Ignore]
        public List<string> wav_files { get; set; } = [];

        [Ignore]
        public List<string> bga_files { get; set; } = [];

        [Ignore]
        internal List<BeMusicSeeker.Models.BmsLibraryInternal.UnsupportedChartResourceReference> UnsupportedResourceReferences { get; set; } = [];

        /// <summary>
        /// この実行中に parser から resource reference を構築済みかどうか。
        /// DB へ保存せず、file diff 直後の maintenance で再パースを避けるためだけに使います。
        /// </summary>
        [Ignore]
        public bool HasFreshResourceReferences { get; set; }

        /// <summary>
        /// maintenance table から読み込んだ bmson 用の構成ファイル検査結果です。
        /// bmson_song table には保存せず、既存 maintenance table の row を参照します。
        /// </summary>
        [Ignore]
        public BeMusicSeeker.Models.BMSFileMaintenanceInfo MaintenanceInfo { get; set; }
    }

    [Table("ir_score")]
    public class ir_score : SQLiteTable<ir_score>
    {
        private string _hash;

        protected ClearType _clear;

        [PrimaryKey]
        [Indexed(Name = "hashidx_ir_score")]
        public virtual string hash
        {
            get
            {
                return _hash;
            }
            protected set
            {
                if (LR2SongDB.md5HashRegex.IsMatch(value))
                {
                    if (!(_hash == value))
                    {
                        _hash = value.ToLowerInvariant();
                    }
                    return;
                }
                throw new FormatException("MD5 HASH ではありません");
            }
        }

        [Column("clear")]
        public int clearValue
        {
            get
            {
                return ClearTypeStorageConverter.ToLr2Value(_clear);
            }
            set
            {
                _clear = ClearTypeStorageConverter.FromLr2Value(value);
            }
        }

        [Ignore]
        public ClearType clear
        {
            get
            {
                if ((option & 0x10) != 0 && _clear == ClearType.FC)
                {
                    return ClearType.PA;
                }
                return _clear;
            }
            set
            {
                _clear = value;
            }
        }

        public int notes { get; set; }

        public int combo { get; set; }

        public int pg { get; set; }

        public int gr { get; set; }

        public int gd { get; set; }

        public int bd { get; set; }

        public int pr { get; set; }

        public int minbp { get; set; }

        public int option { get; set; }

        public int lastupdate { get; set; }
    }

    [Table("ir_data")]
    public class ir_data : SQLiteTable<ir_data>
    {
        private static string _prevHash;

        private string _hash;

        private ClearType _clear;

        public string hash
        {
            get
            {
                return _hash;
            }
            protected set
            {
                value = value.ToLowerInvariant();
                if (_prevHash == value || LR2SongDB.md5HashRegex.IsMatch(value))
                {
                    if (!(_hash == value))
                    {
                        _hash = value.ToLowerInvariant();
                        _prevHash = _hash;
                    }
                    return;
                }
                throw new FormatException("MD5 HASH ではありません");
            }
        }

        public int rank { get; set; }

        public int players_num { get; set; }

        public int lr2id { get; set; }

        [Column("clear")]
        public int clearValue
        {
            get
            {
                return ClearTypeStorageConverter.ToLr2Value(_clear);
            }
            set
            {
                _clear = ClearTypeStorageConverter.FromLr2Value(value);
            }
        }

        [Ignore]
        public ClearType clear
        {
            get
            {
                return _clear;
            }
            set
            {
                _clear = value;
            }
        }

        public int notes { get; set; }

        public int combo { get; set; }

        public int pg { get; set; }

        public int gr { get; set; }

        public int minbp { get; set; }

        public double average { get; set; }

        public double sigma { get; set; }

        public DateTime lastupdate { get; set; }

        public DateTime? lastcacheupdate { get; set; }
    }

    public class SQLiteCommandExtended : SQLiteCommand
    {
        internal SQLiteCommandExtended(SQLiteConnection conn)
            : base(conn)
        {
        }

        public List<string[]> GetRawValuesAsString()
        {
            List<string[]> list = [];
            IntPtr stmt = Prepare();
            int count = SQLite3.ColumnCount(stmt);
            while (SQLite3.Step(stmt) == SQLite3.Result.Row)
            {
                list.Add([.. (from i in Enumerable.Range(0, count)
                          select SQLite3.ColumnString(stmt, i))]);
            }
            Finalize(stmt);
            return list;
        }

        public int ForEachRawValueAsString(Action<string[]> rowAction)
        {
            if (rowAction == null)
            {
                throw new ArgumentNullException(nameof(rowAction));
            }
            IntPtr stmt = Prepare();
            int count = SQLite3.ColumnCount(stmt);
            int rowCount = 0;
            try
            {
                while (SQLite3.Step(stmt) == SQLite3.Result.Row)
                {
                    string[] values = new string[count];
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = SQLite3.ColumnString(stmt, i);
                    }
                    rowAction(values);
                    rowCount++;
                }
            }
            finally
            {
                Finalize(stmt);
            }
            return rowCount;
        }
    }

    private static readonly object lockObject = new();

    private bool doNotUnlock;

    public static bool Lock(TimeSpan timespan)
    {
        if (Monitor.IsEntered(lockObject))
        {
            return true;
        }
        bool lockTaken = false;
        Monitor.TryEnter(lockObject, timespan, ref lockTaken);
        return lockTaken;
    }

    public static void Unlock()
    {
        if (Monitor.IsEntered(lockObject))
        {
            Monitor.Exit(lockObject);
        }
    }

    internal static bool IsProcessLockEnteredByCurrentThread()
    {
        return Monitor.IsEntered(lockObject);
    }

    public LR2SongDBExtended(string dbPath)
        : base(dbPath)
    {
        AcquireProcessLock();
        base.BusyTimeout = new TimeSpan(0, 0, 60);
    }

    internal LR2SongDBExtended(string dbPath, SQLiteOpenFlags openFlags, bool acquireProcessLock)
        : base(dbPath, openFlags)
    {
        IsReadOnlyConnection = (openFlags & SQLiteOpenFlags.ReadOnly) == SQLiteOpenFlags.ReadOnly;
        if (acquireProcessLock)
        {
            AcquireProcessLock();
        }
        else
        {
            doNotUnlock = true;
        }
        base.BusyTimeout = new TimeSpan(0, 0, 60);
    }

    public bool IsReadOnlyConnection { get; }

    public long ProcessLockWaitMs { get; private set; }

    private void AcquireProcessLock()
    {
        if (Monitor.IsEntered(lockObject))
        {
            doNotUnlock = true;
            return;
        }
        var stopwatch = Stopwatch.StartNew();
        Monitor.Enter(lockObject);
        stopwatch.Stop();
        ProcessLockWaitMs = stopwatch.ElapsedMilliseconds;
    }

    public void Uninstall()
    {
        foreach (string tableName in BeMusicSeekerOwnedTableNames)
        {
            Execute("DROP TABLE IF EXISTS " + QuoteIdentifier(tableName) + ";");
        }
        foreach (string indexName in BeMusicSeekerOwnedNativeIndexNames)
        {
            Execute("DROP INDEX IF EXISTS " + QuoteIdentifier(indexName) + ";");
        }
        DeleteOwnedSqliteSequenceRows();
    }

    private static string QuoteIdentifier(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    }

    private void DeleteOwnedSqliteSequenceRows()
    {
        if (ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence';") <= 0)
        {
            return;
        }
        foreach (string tableName in BeMusicSeekerOwnedTableNames)
        {
            Execute("DELETE FROM sqlite_sequence WHERE name = ?;", tableName);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (Monitor.IsEntered(lockObject) && !doNotUnlock)
        {
            Monitor.Exit(lockObject);
        }
        base.Dispose(disposing);
    }

    protected override SQLiteCommand NewCommand()
    {
        return new SQLiteCommandExtended(this);
    }

    public IEnumerable<string> Dump<T>()
    {
        SQLiteCommand sQLiteCommand = CreateCommand($"PRAGMA TABLE_INFO ('{GetMapping(typeof(T)).TableName}');");
        List<string> colNames = [.. (from r in ((SQLiteCommandExtended)sQLiteCommand).GetRawValuesAsString()
                                 select r[1])];
        sQLiteCommand = CreateCommand($"SELECT * FROM \"{GetMapping(typeof(T)).TableName}\";");
        List<string[]> rawValuesAsString = ((SQLiteCommandExtended)sQLiteCommand).GetRawValuesAsString();
        if (rawValuesAsString.Count == 0)
        {
            yield break;
        }
        if (colNames.Count != rawValuesAsString.First().Count())
        {
            throw SQLiteException.New(SQLite3.Result.Abort, "Dump failed");
        }
        static string sqlQuote(string str) => (!string.IsNullOrWhiteSpace(str)) ? ((!double.TryParse(str, out double _) || (str.Count() > 1 && str.StartsWith("0"))) ? ("'" + str.Replace("'", "''") + "'") : str) : "''";
        foreach (string[] item in rawValuesAsString)
        {
            yield return $"INSERT OR REPLACE INTO \"{GetMapping(typeof(T)).TableName}\" " + "(" + string.Join(", ", colNames.Select(c => $"\"{c}\"")) + ") VALUES (" + string.Join(", ", item.Select(c => (c != null) ? sqlQuote(c) : "NULL")) + ");";
        }
    }
}
