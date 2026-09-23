#pragma warning disable CS8981 // LR2 SQLiteモデルの型名はテーブル名と意図的に一致させている。

using System.IO;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

public enum ClearType
{
    NO_SONG = -1,
    NO_PLAY = 0,
    FAILED = 1,
    INVALID = 2,
    L_ASSIST = 3,
    EASY = 4,
    CLEAR = 5,
    HARD = 6,
    EX_HARD = 7,
    FC = 8,
    PA = 9,
    MAX = 10
}

internal static class ClearTypeStorageConverter
{
    internal const int OptionHistoryEasy = 0x00000008;

    internal const int OptionHistoryPerfect = 0x00000010;

    internal const int OptionHistoryAssist = 0x01000000;

    internal static ClearType FromLr2Value(int value)
    {
        return value switch
        {
            0 => ClearType.NO_PLAY,
            1 => ClearType.FAILED,
            2 => ClearType.EASY,
            3 => ClearType.CLEAR,
            4 => ClearType.HARD,
            5 => ClearType.FC,
            21 => ClearType.PA,
            _ => (ClearType)value,
        };
    }

    internal static ClearType FromLr2ScoreValue(int value, int opHistory)
    {
        ClearType clear = FromLr2Value(value);
        if (value == 2 && (opHistory & OptionHistoryEasy) == 0)
        {
            return ClearType.INVALID;
        }
        return ResolveInternalClear(clear, opHistory);
    }

    internal static ClearType ResolveInternalClear(ClearType clear, int opHistory)
    {
        if (clear == ClearType.FC && (opHistory & OptionHistoryPerfect) != 0)
        {
            return ClearType.PA;
        }
        return clear;
    }

    internal static int GetLr2IrDataOptionHistory(ClearType clear)
    {
        return clear == ClearType.EASY ? OptionHistoryEasy : 0;
    }

    internal static int ToLr2Value(ClearType clear)
    {
        return clear switch
        {
            ClearType.NO_SONG or ClearType.NO_PLAY => 0,
            ClearType.FAILED or ClearType.INVALID or ClearType.L_ASSIST => 1,
            ClearType.EASY => 2,
            ClearType.CLEAR => 3,
            ClearType.HARD or ClearType.EX_HARD => 4,
            ClearType.FC or ClearType.PA or ClearType.MAX => 5,
            _ => (int)clear,
        };
    }
}

public enum RankType
{
    INVALID,
    F,
    E,
    D,
    C,
    B,
    A,
    AA,
    AAA,
    MAX
}

public class LR2ScoreDB : SQLiteConnectionEx
{
    [Table("player")]
    public class player : SQLiteTable<player>
    {
        [PrimaryKey]
        public string id { get; set; }

        public string hash { get; set; }

        public string name { get; set; }

        public int? irid { get; set; }

        public string irname { get; set; }

        public int? playcount { get; set; }

        public int? clear { get; set; }

        public int? fail { get; set; }

        public int? perfect { get; set; }

        public int? great { get; set; }

        public int? good { get; set; }

        public int? bad { get; set; }

        public int? poor { get; set; }

        public int? playtime { get; set; }

        public int? combo { get; set; }

        public int? maxcombo { get; set; }

        public int? grade_7 { get; set; }

        public int? grade_5 { get; set; }

        public int? grade_14 { get; set; }

        public int? grade_10 { get; set; }

        public int? grade_9 { get; set; }

        public int? trial { get; set; }

        public int? option { get; set; }

        public int? systemversion { get; set; }

        public int? gradeversion { get; set; }

        public int? trialversion { get; set; }

        public string scorehash { get; set; }
    }



    [Table("score")]
    public class score : SQLiteTable<score>
    {

        protected ClearType _clear;

        private int? lr2ClearValue;

        protected RankType _rank;

        [PrimaryKey]
        public string hash { get; set; }

        [Column("clear")]
        public int clearValue
        {
            get
            {
                return lr2ClearValue ?? ClearTypeStorageConverter.ToLr2Value(_clear);
            }
            set
            {
                lr2ClearValue = value;
                _clear = ClearTypeStorageConverter.FromLr2Value(value);
            }
        }

        [Ignore]
        public ClearType clear
        {
            get
            {
                return lr2ClearValue.HasValue
                    ? ClearTypeStorageConverter.FromLr2ScoreValue(lr2ClearValue.Value, op_history)
                    : ClearTypeStorageConverter.ResolveInternalClear(_clear, op_history);
            }
            set
            {
                lr2ClearValue = null;
                _clear = value;
            }
        }

        public int perfect { get; set; }

        public int great { get; set; }

        public int good { get; set; }

        public int bad { get; set; }

        public int poor { get; set; }

        public int totalnotes { get; set; }

        public int maxcombo { get; set; }

        public int minbp { get; set; }

        public int playcount { get; set; }

        public int clearcount { get; set; }

        public int failcount { get; set; }

        public RankType rank
        {
            get
            {
                if (rate == 100 && _rank == RankType.AAA)
                {
                    return RankType.MAX;
                }
                return _rank;
            }
            set
            {
                if (value == RankType.MAX)
                {
                    _rank = RankType.AAA;
                }
                _rank = value;
            }
        }

        public int rate { get; set; }

        public int clear_db { get; set; }

        public int op_history { get; set; }

        public string scorehash { get; set; }

        public string ghost { get; set; }

        public int clear_sd { get; set; }

        public int clear_ex { get; set; }

        public int op_best { get; set; }

        public int rseed { get; set; }

        public bool complete { get; set; }
    }

    private string _dbPath;

    private string DBPath
    {
        get
        {
            return _dbPath;
        }
        set
        {
            if (!(_dbPath == value))
            {
                if (!File.Exists(value) || !(string.Equals(Path.GetExtension(value), ".db", System.StringComparison.OrdinalIgnoreCase)))
                {
                    Close();
                    throw new FileNotFoundException(BeMusicSeeker.Properties.Resources.Error_InvalidLR2ScoreDbFile, value);
                }
                _dbPath = value;
            }
        }
    }

    public LR2ScoreDB(string dbPath)
        : base(dbPath)
    {
        DBPath = dbPath;
    }

    internal LR2ScoreDB(string dbPath, SQLiteOpenFlags openFlags)
        : base(dbPath, openFlags)
    {
        DBPath = dbPath;
    }
}
