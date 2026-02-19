using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

public sealed class LR2SongDBExtended : LR2SongDB
{
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

        public string compat_prefix { get; set; }

        public DateTime last_update { get; set; }

        public string org_name { get; set; }

        public string org_symbol { get; set; }

        public CustomFolderType ignore_folder_output { get; set; }

        public bool is_external_sync { get; set; }

        public string output_dir { get; protected set; }

        public bool is_root_folder { get; set; }
    }

    [Table("playlist_entry")]
    public class playlist_entry : SQLiteTable<playlist_entry>
    {
        protected int? _playlist_id;

        protected string _md5;

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

        public double? level { get; set; }

        public virtual string title
        {
            get
            {
                return title;
            }
            protected set
            {
                if (value == null)
                {
                    value = string.Empty;
                }
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
                if (value == null)
                {
                    value = string.Empty;
                }
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
                if (value == null)
                {
                    value = string.Empty;
                }
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
                if (value == ClearType.PA)
                {
                    _clear = ClearType.FC;
                }
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

        public ClearType clear { get; set; }

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
            List<string[]> list = new List<string[]>();
            IntPtr stmt = Prepare();
            int count = SQLite3.ColumnCount(stmt);
            while (SQLite3.Step(stmt) == SQLite3.Result.Row)
            {
                list.Add((from i in Enumerable.Range(0, count)
                          select SQLite3.ColumnString(stmt, i)).ToArray());
            }
            Finalize(stmt);
            return list;
        }
    }

    private static object lockObject = new object();

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

    public LR2SongDBExtended(string dbPath)
        : base(dbPath)
    {
        if (Monitor.IsEntered(lockObject))
        {
            doNotUnlock = true;
        }
        else
        {
            Monitor.Enter(lockObject);
        }
        base.BusyTimeout = new TimeSpan(0, 0, 60);
    }

    public void Uninstall()
    {
        DropTable<install>();
        DropTable<maintenance>();
        DropTable<playlist>();
        DropTable<playlist_entry>();
        DropTable<ir_score>();
        DropTable<ir_data>();
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
        List<string> colNames = (from r in ((SQLiteCommandExtended)sQLiteCommand).GetRawValuesAsString()
                                 select r[1]).ToList();
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
        Func<string, string> sqlQuote = (string str) => (!string.IsNullOrWhiteSpace(str)) ? ((!double.TryParse(str, out var _) || (str.Count() > 1 && str.StartsWith("0"))) ? ("'" + str.Replace("'", "''") + "'") : str) : "''";
        foreach (string[] item in rawValuesAsString)
        {
            yield return $"INSERT OR REPLACE INTO \"{GetMapping(typeof(T)).TableName}\" " + "(" + string.Join(", ", colNames.Select((string c) => $"\"{c}\"")) + ") VALUES (" + string.Join(", ", item.Select((string c) => (c != null) ? sqlQuote(c) : "NULL")) + ");";
        }
    }
}
