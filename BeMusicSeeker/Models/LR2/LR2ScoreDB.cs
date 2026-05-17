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
    internal static ClearType FromLr2Value(int value)
    {
        switch (value)
        {
            case 0:
                return ClearType.NO_PLAY;
            case 1:
                return ClearType.FAILED;
            case 2:
                return ClearType.EASY;
            case 3:
                return ClearType.CLEAR;
            case 4:
                return ClearType.HARD;
            case 5:
                return ClearType.FC;
            case 21:
                return ClearType.PA;
            default:
                return (ClearType)value;
        }
    }

    internal static int ToLr2Value(ClearType clear)
    {
        switch (clear)
        {
            case ClearType.NO_SONG:
            case ClearType.NO_PLAY:
                return 0;
            case ClearType.FAILED:
            case ClearType.INVALID:
            case ClearType.L_ASSIST:
                return 1;
            case ClearType.EASY:
                return 2;
            case ClearType.CLEAR:
                return 3;
            case ClearType.HARD:
            case ClearType.EX_HARD:
                return 4;
            case ClearType.FC:
            case ClearType.PA:
            case ClearType.MAX:
                return 5;
            default:
                return (int)clear;
        }
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

        protected RankType _rank;

        [PrimaryKey]
        public string hash { get; set; }

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
                if ((op_history & 0x10) != 0 && _clear == ClearType.FC)
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
                    throw new FileNotFoundException("ファイルが見つからないか、db ファイルではありません。", value);
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
