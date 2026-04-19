using System;
using System.Windows;
using Livet;

namespace BeMusicSeeker.ViewModels;

[Serializable]
public class dataGridColumnsSettings : NotificationObject
{
    [Serializable]
    public class dataGridColumnlayouts : NotificationObject
    {
        private int _Width = 50;

        private int _DisplayIndex = -1;

        private Visibility _Visibility;

        public int Width
        {
            get
            {
                return _Width;
            }
            set
            {
                _ = 0;
                if (_Width != value)
                {
                    _Width = value;
                    RaisePropertyChanged("Width");
                }
            }
        }

        public int DisplayIndex
        {
            get
            {
                return _DisplayIndex;
            }
            set
            {
                if (_DisplayIndex != value)
                {
                    _DisplayIndex = value;
                    RaisePropertyChanged("DisplayIndex");
                }
            }
        }

        public Visibility Visibility
        {
            get
            {
                return _Visibility;
            }
            set
            {
                if (_Visibility != value)
                {
                    _Visibility = value;
                    RaisePropertyChanged("Visibility");
                }
            }
        }
    }

    public enum viewType
    {
        STANDARD,
        PLAYLIST,
        FULLSCAN,
        DUPLICATE,
        ENCODING,
        INSTALL,
        ZERO_NOTE
    }

    private dataGridColumnlayouts _Status;

    private dataGridColumnlayouts _Level;

    private dataGridColumnlayouts _Title;

    private dataGridColumnlayouts _Artist;

    private dataGridColumnlayouts _Genre;

    private dataGridColumnlayouts _Mode;

    private dataGridColumnlayouts _Tag;

    private dataGridColumnlayouts _Url1;

    private dataGridColumnlayouts _Url2;

    private dataGridColumnlayouts _Clear;

    private dataGridColumnlayouts _Rank;

    private dataGridColumnlayouts _Ranking;

    private dataGridColumnlayouts _RankingLastupdate;

    private dataGridColumnlayouts _TScore;

    private dataGridColumnlayouts _ScoreDifficulty;

    private dataGridColumnlayouts _Warning;

    private dataGridColumnlayouts _Comment;

    private dataGridColumnlayouts _Memo;

    private dataGridColumnlayouts _Hash;

    private dataGridColumnlayouts _Sha256;

    private dataGridColumnlayouts _Folder;

    private dataGridColumnlayouts _Path;

    private dataGridColumnlayouts _InstallDst;

    private dataGridColumnlayouts _InstallDstTitle;

    private dataGridColumnlayouts _InstallDstArtist;

    private dataGridColumnlayouts _WavHealth;

    private dataGridColumnlayouts _BgaHealth;

    private dataGridColumnlayouts _MovieHealth;

    private dataGridColumnlayouts _PlaylistSymbols;

    private dataGridColumnlayouts _CharcterEncoding;

    private dataGridColumnlayouts _Rate;

    private dataGridColumnlayouts _Score;

    private dataGridColumnlayouts _Notes;

    private dataGridColumnlayouts _Combo;

    private dataGridColumnlayouts _Bp;

    public dataGridColumnlayouts Status
    {
        get
        {
            return _Status;
        }
        set
        {
            if (_Status != value)
            {
                _Status = value;
                RaisePropertyChanged("Status");
            }
        }
    }

    public dataGridColumnlayouts Level
    {
        get
        {
            return _Level;
        }
        set
        {
            if (_Level != value)
            {
                _Level = value;
                RaisePropertyChanged("Level");
            }
        }
    }

    public dataGridColumnlayouts Title
    {
        get
        {
            return _Title;
        }
        set
        {
            if (_Title != value)
            {
                _Title = value;
                RaisePropertyChanged("Title");
            }
        }
    }

    public dataGridColumnlayouts Artist
    {
        get
        {
            return _Artist;
        }
        set
        {
            if (_Artist != value)
            {
                _Artist = value;
                RaisePropertyChanged("Artist");
            }
        }
    }

    public dataGridColumnlayouts Genre
    {
        get
        {
            return _Genre;
        }
        set
        {
            if (_Genre != value)
            {
                _Genre = value;
                RaisePropertyChanged("Genre");
            }
        }
    }

    public dataGridColumnlayouts Mode
    {
        get
        {
            return _Mode;
        }
        set
        {
            if (_Mode != value)
            {
                _Mode = value;
                RaisePropertyChanged("Mode");
            }
        }
    }

    public dataGridColumnlayouts Tag
    {
        get
        {
            return _Tag;
        }
        set
        {
            if (_Tag != value)
            {
                _Tag = value;
                RaisePropertyChanged("Tag");
            }
        }
    }

    public dataGridColumnlayouts Url1
    {
        get
        {
            return _Url1;
        }
        set
        {
            if (_Url1 != value)
            {
                _Url1 = value;
                RaisePropertyChanged("Url1");
            }
        }
    }

    public dataGridColumnlayouts Url2
    {
        get
        {
            return _Url2;
        }
        set
        {
            if (_Url2 != value)
            {
                _Url2 = value;
                RaisePropertyChanged("Url2");
            }
        }
    }

    public dataGridColumnlayouts Clear
    {
        get
        {
            return _Clear;
        }
        set
        {
            if (_Clear != value)
            {
                _Clear = value;
                RaisePropertyChanged("Clear");
            }
        }
    }

    public dataGridColumnlayouts Rank
    {
        get
        {
            return _Rank;
        }
        set
        {
            if (_Rank != value)
            {
                _Rank = value;
                RaisePropertyChanged("Rank");
            }
        }
    }

    public dataGridColumnlayouts Ranking
    {
        get
        {
            return _Ranking;
        }
        set
        {
            if (_Ranking != value)
            {
                _Ranking = value;
                RaisePropertyChanged("Ranking");
            }
        }
    }

    public dataGridColumnlayouts RankingLastupdate
    {
        get
        {
            return _RankingLastupdate;
        }
        set
        {
            if (_RankingLastupdate != value)
            {
                _RankingLastupdate = value;
                RaisePropertyChanged("RankingLastupdate");
            }
        }
    }

    public dataGridColumnlayouts TScore
    {
        get
        {
            return _TScore;
        }
        set
        {
            if (_TScore != value)
            {
                _TScore = value;
                RaisePropertyChanged("TScore");
            }
        }
    }

    public dataGridColumnlayouts ScoreDifficulty
    {
        get
        {
            return _ScoreDifficulty;
        }
        set
        {
            if (_ScoreDifficulty != value)
            {
                _ScoreDifficulty = value;
                RaisePropertyChanged("ScoreDifficulty");
            }
        }
    }

    public dataGridColumnlayouts Warning
    {
        get
        {
            return _Warning;
        }
        set
        {
            if (_Warning != value)
            {
                _Warning = value;
                RaisePropertyChanged("Warning");
            }
        }
    }

    public dataGridColumnlayouts Comment
    {
        get
        {
            return _Comment;
        }
        set
        {
            if (_Comment != value)
            {
                _Comment = value;
                RaisePropertyChanged("Comment");
            }
        }
    }

    public dataGridColumnlayouts Memo
    {
        get
        {
            return _Memo;
        }
        set
        {
            if (_Memo != value)
            {
                _Memo = value;
                RaisePropertyChanged("Memo");
            }
        }
    }

    public dataGridColumnlayouts Hash
    {
        get
        {
            return _Hash;
        }
        set
        {
            if (_Hash != value)
            {
                _Hash = value;
                RaisePropertyChanged("Hash");
            }
        }
    }

    public dataGridColumnlayouts Sha256
    {
        get
        {
            if (_Sha256 == null)
            {
                _Sha256 = new dataGridColumnlayouts
                {
                    Width = 420,
                    Visibility = Visibility.Hidden
                };
            }
            return _Sha256;
        }
        set
        {
            if (_Sha256 != value)
            {
                _Sha256 = value;
                RaisePropertyChanged("Sha256");
            }
        }
    }

    public dataGridColumnlayouts Folder
    {
        get
        {
            return _Folder;
        }
        set
        {
            if (_Folder != value)
            {
                _Folder = value;
                RaisePropertyChanged("Folder");
            }
        }
    }

    public dataGridColumnlayouts Path
    {
        get
        {
            return _Path;
        }
        set
        {
            if (_Path != value)
            {
                _Path = value;
                RaisePropertyChanged("Path");
            }
        }
    }

    public dataGridColumnlayouts InstallDst
    {
        get
        {
            return _InstallDst;
        }
        set
        {
            if (_InstallDst != value)
            {
                _InstallDst = value;
                RaisePropertyChanged("InstallDst");
            }
        }
    }

    public dataGridColumnlayouts InstallDstTitle
    {
        get
        {
            return _InstallDstTitle;
        }
        set
        {
            if (_InstallDstTitle != value)
            {
                _InstallDstTitle = value;
                RaisePropertyChanged("InstallDstTitle");
            }
        }
    }

    public dataGridColumnlayouts InstallDstArtist
    {
        get
        {
            return _InstallDstArtist;
        }
        set
        {
            if (_InstallDstArtist != value)
            {
                _InstallDstArtist = value;
                RaisePropertyChanged("InstallDstArtist");
            }
        }
    }

    public dataGridColumnlayouts WavHealth
    {
        get
        {
            return _WavHealth;
        }
        set
        {
            if (_WavHealth != value)
            {
                _WavHealth = value;
                RaisePropertyChanged("WavHealth");
            }
        }
    }

    public dataGridColumnlayouts BgaHealth
    {
        get
        {
            return _BgaHealth;
        }
        set
        {
            if (_BgaHealth != value)
            {
                _BgaHealth = value;
                RaisePropertyChanged("BgaHealth");
            }
        }
    }

    public dataGridColumnlayouts MovieHealth
    {
        get
        {
            return _MovieHealth;
        }
        set
        {
            if (_MovieHealth != value)
            {
                _MovieHealth = value;
                RaisePropertyChanged("MovieHealth");
            }
        }
    }

    public dataGridColumnlayouts PlaylistSymbols
    {
        get
        {
            return _PlaylistSymbols;
        }
        set
        {
            if (_PlaylistSymbols != value)
            {
                _PlaylistSymbols = value;
                RaisePropertyChanged("PlaylistSymbols");
            }
        }
    }

    public dataGridColumnlayouts CharcterEncoding
    {
        get
        {
            return _CharcterEncoding;
        }
        set
        {
            if (_CharcterEncoding != value)
            {
                _CharcterEncoding = value;
                RaisePropertyChanged("CharcterEncoding");
            }
        }
    }

    public dataGridColumnlayouts Rate
    {
        get
        {
            return _Rate;
        }
        set
        {
            if (_Rate != value)
            {
                _Rate = value;
                RaisePropertyChanged("Rate");
            }
        }
    }

    public dataGridColumnlayouts Score
    {
        get
        {
            return _Score;
        }
        set
        {
            if (_Score != value)
            {
                _Score = value;
                RaisePropertyChanged("Score");
            }
        }
    }

    public dataGridColumnlayouts Notes
    {
        get
        {
            return _Notes;
        }
        set
        {
            if (_Notes != value)
            {
                _Notes = value;
                RaisePropertyChanged("Notes");
            }
        }
    }

    public dataGridColumnlayouts Combo
    {
        get
        {
            return _Combo;
        }
        set
        {
            if (_Combo != value)
            {
                _Combo = value;
                RaisePropertyChanged("Combo");
            }
        }
    }

    public dataGridColumnlayouts Bp
    {
        get
        {
            return _Bp;
        }
        set
        {
            if (_Bp != value)
            {
                _Bp = value;
                RaisePropertyChanged("Bp");
            }
        }
    }

    public dataGridColumnsSettings()
    {
        Status = new dataGridColumnlayouts
        {
            Width = 18
        };
        Level = new dataGridColumnlayouts
        {
            Width = 50
        };
        Title = new dataGridColumnlayouts
        {
            Width = 200
        };
        Artist = new dataGridColumnlayouts
        {
            Width = 150
        };
        Genre = new dataGridColumnlayouts
        {
            Width = 100
        };
        Mode = new dataGridColumnlayouts
        {
            Width = 50
        };
        Tag = new dataGridColumnlayouts
        {
            Width = 50
        };
        Url1 = new dataGridColumnlayouts
        {
            Width = 40
        };
        Url2 = new dataGridColumnlayouts
        {
            Width = 40
        };
        Clear = new dataGridColumnlayouts
        {
            Width = 120
        };
        Rank = new dataGridColumnlayouts
        {
            Width = 60
        };
        Ranking = new dataGridColumnlayouts
        {
            Width = 95
        };
        RankingLastupdate = new dataGridColumnlayouts
        {
            Width = 95
        };
        Rate = new dataGridColumnlayouts
        {
            Width = 40
        };
        Score = new dataGridColumnlayouts
        {
            Width = 55
        };
        Notes = new dataGridColumnlayouts
        {
            Width = 55
        };
        Combo = new dataGridColumnlayouts
        {
            Width = 55
        };
        Bp = new dataGridColumnlayouts
        {
            Width = 55
        };
        TScore = new dataGridColumnlayouts
        {
            Width = 65
        };
        ScoreDifficulty = new dataGridColumnlayouts
        {
            Width = 50
        };
        Warning = new dataGridColumnlayouts
        {
            Width = 200
        };
        Comment = new dataGridColumnlayouts
        {
            Width = 300
        };
        Memo = new dataGridColumnlayouts
        {
            Width = 300
        };
        Hash = new dataGridColumnlayouts
        {
            Width = 240
        };
        Sha256 = new dataGridColumnlayouts
        {
            Width = 420,
            Visibility = Visibility.Hidden
        };
        Folder = new dataGridColumnlayouts
        {
            Width = 200
        };
        Path = new dataGridColumnlayouts
        {
            Width = 300
        };
        InstallDst = new dataGridColumnlayouts
        {
            Width = 300
        };
        InstallDstTitle = new dataGridColumnlayouts
        {
            Width = 220
        };
        InstallDstArtist = new dataGridColumnlayouts
        {
            Width = 180
        };
        WavHealth = new dataGridColumnlayouts
        {
            Width = 50
        };
        BgaHealth = new dataGridColumnlayouts
        {
            Width = 50
        };
        MovieHealth = new dataGridColumnlayouts
        {
            Width = 50
        };
        CharcterEncoding = new dataGridColumnlayouts
        {
            Width = 130
        };
        PlaylistSymbols = new dataGridColumnlayouts
        {
            Width = 70
        };
        int num = 0;
        Status.DisplayIndex = num++;
        Level.DisplayIndex = num++;
        Title.DisplayIndex = num++;
        Artist.DisplayIndex = num++;
        Genre.DisplayIndex = num++;
        Mode.DisplayIndex = num++;
        Tag.DisplayIndex = num++;
        Url1.DisplayIndex = num++;
        Url2.DisplayIndex = num++;
        Clear.DisplayIndex = num++;
        Rank.DisplayIndex = num++;
        Rate.DisplayIndex = num++;
        Ranking.DisplayIndex = num++;
        RankingLastupdate.DisplayIndex = num++;
        Score.DisplayIndex = num++;
        Notes.DisplayIndex = num++;
        Combo.DisplayIndex = num++;
        TScore.DisplayIndex = num++;
        ScoreDifficulty.DisplayIndex = num++;
        Bp.DisplayIndex = num++;
        Warning.DisplayIndex = num++;
        Comment.DisplayIndex = num++;
        Memo.DisplayIndex = num++;
        Hash.DisplayIndex = num++;
        Sha256.DisplayIndex = num++;
        Folder.DisplayIndex = num++;
        Path.DisplayIndex = num++;
        InstallDst.DisplayIndex = num++;
        InstallDstTitle.DisplayIndex = num++;
        InstallDstArtist.DisplayIndex = num++;
        WavHealth.DisplayIndex = num++;
        BgaHealth.DisplayIndex = num++;
        MovieHealth.DisplayIndex = num++;
        PlaylistSymbols.DisplayIndex = num++;
        CharcterEncoding.DisplayIndex = num++;
    }

    public dataGridColumnsSettings(viewType type)
        : this()
    {
        switch (type)
        {
            case viewType.STANDARD:
                ApplyStandardViewDefaults(showWarningColumn: false);
                break;
            case viewType.ZERO_NOTE:
                ApplyStandardViewDefaults(showWarningColumn: true);
                break;
            case viewType.PLAYLIST:
                {
                    Genre.Visibility = Visibility.Hidden;
                    Mode.Visibility = Visibility.Hidden;
                    Tag.Visibility = Visibility.Hidden;
                    Rate.Visibility = Visibility.Hidden;
                    RankingLastupdate.Visibility = Visibility.Hidden;
                    Score.Visibility = Visibility.Hidden;
                    Notes.Visibility = Visibility.Hidden;
                    Combo.Visibility = Visibility.Hidden;
                    Bp.Visibility = Visibility.Hidden;
                    Warning.Visibility = Visibility.Hidden;
                    Hash.Visibility = Visibility.Hidden;
                    InstallDst.Visibility = Visibility.Hidden;
                    InstallDstTitle.Visibility = Visibility.Hidden;
                    InstallDstArtist.Visibility = Visibility.Hidden;
                    PlaylistSymbols.Visibility = Visibility.Hidden;
                    CharcterEncoding.Visibility = Visibility.Hidden;
                    int num3 = 0;
                    Status.DisplayIndex = num3++;
                    Folder.DisplayIndex = num3++;
                    Level.DisplayIndex = num3++;
                    Title.DisplayIndex = num3++;
                    Artist.DisplayIndex = num3++;
                    Genre.DisplayIndex = num3++;
                    Mode.DisplayIndex = num3++;
                    Tag.DisplayIndex = num3++;
                    Url1.DisplayIndex = num3++;
                    Url2.DisplayIndex = num3++;
                    Clear.DisplayIndex = num3++;
                    Rank.DisplayIndex = num3++;
                    Rate.DisplayIndex = num3++;
                    Ranking.DisplayIndex = num3++;
                    RankingLastupdate.DisplayIndex = num3++;
                    Score.DisplayIndex = num3++;
                    Notes.DisplayIndex = num3++;
                    Combo.DisplayIndex = num3++;
                    Bp.DisplayIndex = num3++;
                    TScore.DisplayIndex = num3++;
                    ScoreDifficulty.DisplayIndex = num3++;
                    Warning.DisplayIndex = num3++;
                    Comment.DisplayIndex = num3++;
                    Memo.DisplayIndex = num3++;
                    Hash.DisplayIndex = num3++;
                    Sha256.DisplayIndex = num3++;
                    Path.DisplayIndex = num3++;
                    InstallDst.DisplayIndex = num3++;
                    InstallDstTitle.DisplayIndex = num3++;
                    InstallDstArtist.DisplayIndex = num3++;
                    WavHealth.DisplayIndex = num3++;
                    BgaHealth.DisplayIndex = num3++;
                    MovieHealth.DisplayIndex = num3++;
                    PlaylistSymbols.DisplayIndex = num3++;
                    CharcterEncoding.DisplayIndex = num3++;
                    Folder.Width = 80;
                    break;
                }
            case viewType.FULLSCAN:
                {
                    Genre.Visibility = Visibility.Hidden;
                    Tag.Visibility = Visibility.Hidden;
                    Url1.Visibility = Visibility.Hidden;
                    Url2.Visibility = Visibility.Hidden;
                    Clear.Visibility = Visibility.Hidden;
                    Rank.Visibility = Visibility.Hidden;
                    Rate.Visibility = Visibility.Hidden;
                    Ranking.Visibility = Visibility.Hidden;
                    RankingLastupdate.Visibility = Visibility.Hidden;
                    Score.Visibility = Visibility.Hidden;
                    Notes.Visibility = Visibility.Hidden;
                    Combo.Visibility = Visibility.Hidden;
                    Bp.Visibility = Visibility.Hidden;
                    TScore.Visibility = Visibility.Hidden;
                    ScoreDifficulty.Visibility = Visibility.Hidden;
                    Comment.Visibility = Visibility.Hidden;
                    Memo.Visibility = Visibility.Hidden;
                    Hash.Visibility = Visibility.Hidden;
                    InstallDstTitle.Visibility = Visibility.Hidden;
                    InstallDstArtist.Visibility = Visibility.Hidden;
                    Folder.Visibility = Visibility.Hidden;
                    CharcterEncoding.Visibility = Visibility.Hidden;
                    int num2 = 0;
                    Status.DisplayIndex = num2++;
                    Level.DisplayIndex = num2++;
                    Title.DisplayIndex = num2++;
                    Artist.DisplayIndex = num2++;
                    Genre.DisplayIndex = num2++;
                    Mode.DisplayIndex = num2++;
                    Tag.DisplayIndex = num2++;
                    Url1.DisplayIndex = num2++;
                    Url2.DisplayIndex = num2++;
                    Clear.DisplayIndex = num2++;
                    Rank.DisplayIndex = num2++;
                    Rate.DisplayIndex = num2++;
                    Ranking.DisplayIndex = num2++;
                    RankingLastupdate.DisplayIndex = num2++;
                    Score.DisplayIndex = num2++;
                    Notes.DisplayIndex = num2++;
                    Combo.DisplayIndex = num2++;
                    Bp.DisplayIndex = num2++;
                    TScore.DisplayIndex = num2++;
                    ScoreDifficulty.DisplayIndex = num2++;
                    Warning.DisplayIndex = num2++;
                    Comment.DisplayIndex = num2++;
                    Memo.DisplayIndex = num2++;
                    Hash.DisplayIndex = num2++;
                    Sha256.DisplayIndex = num2++;
                    Folder.DisplayIndex = num2++;
                    WavHealth.DisplayIndex = num2++;
                    BgaHealth.DisplayIndex = num2++;
                    MovieHealth.DisplayIndex = num2++;
                    PlaylistSymbols.DisplayIndex = num2++;
                    CharcterEncoding.DisplayIndex = num2++;
                    Path.DisplayIndex = num2++;
                    InstallDst.DisplayIndex = num2++;
                    InstallDstTitle.DisplayIndex = num2++;
                    InstallDstArtist.DisplayIndex = num2++;
                    break;
                }
            case viewType.DUPLICATE:
                Genre.Visibility = Visibility.Hidden;
                Tag.Visibility = Visibility.Hidden;
                Url1.Visibility = Visibility.Hidden;
                Url2.Visibility = Visibility.Hidden;
                Clear.Visibility = Visibility.Hidden;
                Rank.Visibility = Visibility.Hidden;
                Rate.Visibility = Visibility.Hidden;
                Ranking.Visibility = Visibility.Hidden;
                RankingLastupdate.Visibility = Visibility.Hidden;
                Score.Visibility = Visibility.Hidden;
                Notes.Visibility = Visibility.Hidden;
                Combo.Visibility = Visibility.Hidden;
                Bp.Visibility = Visibility.Hidden;
                TScore.Visibility = Visibility.Hidden;
                ScoreDifficulty.Visibility = Visibility.Hidden;
                Warning.Visibility = Visibility.Hidden;
                Comment.Visibility = Visibility.Hidden;
                Memo.Visibility = Visibility.Hidden;
                Folder.Visibility = Visibility.Hidden;
                InstallDst.Visibility = Visibility.Hidden;
                InstallDstTitle.Visibility = Visibility.Hidden;
                InstallDstArtist.Visibility = Visibility.Hidden;
                CharcterEncoding.Visibility = Visibility.Hidden;
                break;
            case viewType.ENCODING:
                Tag.Visibility = Visibility.Hidden;
                Url1.Visibility = Visibility.Hidden;
                Url2.Visibility = Visibility.Hidden;
                Clear.Visibility = Visibility.Hidden;
                Rank.Visibility = Visibility.Hidden;
                Rate.Visibility = Visibility.Hidden;
                Ranking.Visibility = Visibility.Hidden;
                RankingLastupdate.Visibility = Visibility.Hidden;
                Score.Visibility = Visibility.Hidden;
                Notes.Visibility = Visibility.Hidden;
                Combo.Visibility = Visibility.Hidden;
                Bp.Visibility = Visibility.Hidden;
                TScore.Visibility = Visibility.Hidden;
                ScoreDifficulty.Visibility = Visibility.Hidden;
                Warning.Visibility = Visibility.Hidden;
                Comment.Visibility = Visibility.Hidden;
                Memo.Visibility = Visibility.Hidden;
                Hash.Visibility = Visibility.Hidden;
                Path.Visibility = Visibility.Hidden;
                InstallDst.Visibility = Visibility.Hidden;
                InstallDstTitle.Visibility = Visibility.Hidden;
                InstallDstArtist.Visibility = Visibility.Hidden;
                WavHealth.Visibility = Visibility.Hidden;
                BgaHealth.Visibility = Visibility.Hidden;
                MovieHealth.Visibility = Visibility.Hidden;
                PlaylistSymbols.Visibility = Visibility.Hidden;
                break;
            case viewType.INSTALL:
                {
                    Genre.Visibility = Visibility.Hidden;
                    Tag.Visibility = Visibility.Hidden;
                    Url1.Visibility = Visibility.Hidden;
                    Url2.Visibility = Visibility.Hidden;
                    Clear.Visibility = Visibility.Hidden;
                    Rank.Visibility = Visibility.Hidden;
                    Rate.Visibility = Visibility.Hidden;
                    Ranking.Visibility = Visibility.Hidden;
                    RankingLastupdate.Visibility = Visibility.Hidden;
                    Score.Visibility = Visibility.Hidden;
                    Notes.Visibility = Visibility.Hidden;
                    Combo.Visibility = Visibility.Hidden;
                    Bp.Visibility = Visibility.Hidden;
                    TScore.Visibility = Visibility.Hidden;
                    ScoreDifficulty.Visibility = Visibility.Hidden;
                    Comment.Visibility = Visibility.Hidden;
                    Memo.Visibility = Visibility.Hidden;
                    Hash.Visibility = Visibility.Hidden;
                    Folder.Visibility = Visibility.Hidden;
                    CharcterEncoding.Visibility = Visibility.Hidden;
                    int num = 0;
                    Status.DisplayIndex = num++;
                    Folder.DisplayIndex = num++;
                    Level.DisplayIndex = num++;
                    Title.DisplayIndex = num++;
                    Artist.DisplayIndex = num++;
                    Genre.DisplayIndex = num++;
                    Mode.DisplayIndex = num++;
                    Tag.DisplayIndex = num++;
                    Url1.DisplayIndex = num++;
                    Url2.DisplayIndex = num++;
                    Clear.DisplayIndex = num++;
                    Rank.DisplayIndex = num++;
                    Rate.DisplayIndex = num++;
                    Ranking.DisplayIndex = num++;
                    RankingLastupdate.DisplayIndex = num++;
                    Score.DisplayIndex = num++;
                    Notes.DisplayIndex = num++;
                    Combo.DisplayIndex = num++;
                    Bp.DisplayIndex = num++;
                    TScore.DisplayIndex = num++;
                    ScoreDifficulty.DisplayIndex = num++;
                    InstallDst.DisplayIndex = num++;
                    InstallDstTitle.DisplayIndex = num++;
                    InstallDstArtist.DisplayIndex = num++;
                    Warning.DisplayIndex = num++;
                    Comment.DisplayIndex = num++;
                    Memo.DisplayIndex = num++;
                    Hash.DisplayIndex = num++;
                    Sha256.DisplayIndex = num++;
                    Path.DisplayIndex = num++;
                    WavHealth.DisplayIndex = num++;
                    BgaHealth.DisplayIndex = num++;
                    MovieHealth.DisplayIndex = num++;
                    PlaylistSymbols.DisplayIndex = num++;
                    CharcterEncoding.DisplayIndex = num++;
                    break;
                }
        }
    }

    private void ApplyStandardViewDefaults(bool showWarningColumn)
    {
        Genre.Visibility = Visibility.Hidden;
        Tag.Visibility = Visibility.Hidden;
        Url1.Visibility = Visibility.Hidden;
        Url2.Visibility = Visibility.Hidden;
        Rate.Visibility = Visibility.Hidden;
        RankingLastupdate.Visibility = Visibility.Hidden;
        Score.Visibility = Visibility.Hidden;
        Notes.Visibility = Visibility.Hidden;
        Combo.Visibility = Visibility.Hidden;
        Bp.Visibility = Visibility.Hidden;
        TScore.Visibility = Visibility.Hidden;
        ScoreDifficulty.Visibility = Visibility.Hidden;
        Warning.Visibility = showWarningColumn ? Visibility.Visible : Visibility.Hidden;
        Comment.Visibility = Visibility.Hidden;
        Memo.Visibility = Visibility.Hidden;
        Hash.Visibility = Visibility.Hidden;
        Path.Visibility = Visibility.Hidden;
        InstallDst.Visibility = Visibility.Hidden;
        InstallDstTitle.Visibility = Visibility.Hidden;
        InstallDstArtist.Visibility = Visibility.Hidden;
        WavHealth.Visibility = Visibility.Hidden;
        BgaHealth.Visibility = Visibility.Hidden;
        MovieHealth.Visibility = Visibility.Hidden;
        CharcterEncoding.Visibility = Visibility.Hidden;
    }
}
