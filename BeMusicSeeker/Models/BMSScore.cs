using System;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models;

public class BMSScore : LR2ScoreDB.score
{
    private int _ranking;

    private int _rankingNum;

    private DateTime? _rankingLastupdate;

    private double? _stddevVal;

    private double? _scoreDifficulty;

    private bool isLr2IrScoreUnsent;

    public int score => base.perfect * 2 + base.great;

    public int ranking
    {
        get
        {
            return _ranking;
        }
        set
        {
            if (_ranking != value)
            {
                _ranking = value;
                RaisePropertyChanged("ranking");
            }
        }
    }

    public int rankingNum
    {
        get
        {
            return _rankingNum;
        }
        set
        {
            if (_rankingNum != value)
            {
                _rankingNum = value;
                RaisePropertyChanged("rankingNum");
            }
        }
    }

    public DateTime? rankingLastupdate
    {
        get
        {
            return _rankingLastupdate;
        }
        set
        {
            if (!(_rankingLastupdate == value))
            {
                _rankingLastupdate = value;
                RaisePropertyChanged("rankingLastupdate");
            }
        }
    }

    public double? stddevVal
    {
        get
        {
            return _stddevVal;
        }
        set
        {
            if (_stddevVal != value)
            {
                _stddevVal = value;
                RaisePropertyChanged("stddevVal");
            }
        }
    }

    public double? scoreDifficulty
    {
        get
        {
            return _scoreDifficulty;
        }
        set
        {
            if (_scoreDifficulty != value)
            {
                _scoreDifficulty = value;
                RaisePropertyChanged("scoreDifficulty");
            }
        }
    }

    [Ignore]
    public bool IsLr2IrScoreUnsent
    {
        get
        {
            return isLr2IrScoreUnsent;
        }
        set
        {
            if (isLr2IrScoreUnsent != value)
            {
                isLr2IrScoreUnsent = value;
                RaisePropertyChanged("IsLr2IrScoreUnsent");
            }
        }
    }

    public BMSScore()
    {
    }

    public BMSScore(LR2IRScore _lr2irScore)
    {
        Overwrite(_lr2irScore);
    }

    public BMSScore(LR2IRData _lr2irData)
    {
        Overwrite(_lr2irData);
    }

    public void SetStdDevVal(double average, double sigma)
    {
        if (score == 0)
        {
            stddevVal = null;
        }
        else
        {
            stddevVal = 10.0 * ((double)score - average) / sigma + 50.0;
        }
    }

    public void SetScoreDiffic(double average, double sigma)
    {
        if (average == 0.0 && sigma == 0.0)
        {
            scoreDifficulty = null;
        }
        else
        {
            scoreDifficulty = 100.0 * (1.0 - (average + sigma) / (double)(2 * base.totalnotes));
        }
    }

    public void Overwrite(LR2IRScore _lr2irScore)
    {
        base.hash = _lr2irScore.hash;
        base.clear = _lr2irScore.clear;
        base.totalnotes = _lr2irScore.notes;
        base.maxcombo = _lr2irScore.combo;
        base.perfect = _lr2irScore.pg;
        base.great = _lr2irScore.gr;
        base.good = _lr2irScore.gd;
        base.bad = _lr2irScore.bd;
        base.poor = _lr2irScore.pr;
        base.minbp = _lr2irScore.minbp;
        base.op_history = _lr2irScore.option;
    }

    public void Overwrite(LR2IRData _lr2irData)
    {
        base.hash = _lr2irData.hash;
        base.clear = _lr2irData.clear;
        base.totalnotes = _lr2irData.notes;
        base.maxcombo = _lr2irData.combo;
        base.perfect = _lr2irData.pg;
        base.great = _lr2irData.gr;
        base.minbp = _lr2irData.minbp;
        ranking = _lr2irData.rank;
        rankingNum = _lr2irData.players_num;
        rankingLastupdate = _lr2irData.lastupdate;
        SetStdDevVal(_lr2irData.average, _lr2irData.sigma);
        SetScoreDiffic(_lr2irData.average, _lr2irData.sigma);
    }
}
