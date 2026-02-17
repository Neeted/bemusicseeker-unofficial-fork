using System.IO;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

public enum ClearType
{
    NO_SONG = -2,
    INVALID = -1,
    NO_PLAY = 0,
    FAILED = 1,
    EASY = 2,
    CLEAR = 3,
    HARD = 4,
    FC = 5,
    PA = 21
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
                if (value == ClearType.PA)
                {
                    _clear = ClearType.FC;
                }
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
                if (!File.Exists(value) || !(Path.GetExtension(value).ToLower() == ".db"))
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
}
