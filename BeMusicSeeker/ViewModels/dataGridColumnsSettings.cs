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

    private dataGridColumnlayouts _EntryLevel;

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

    private dataGridColumnlayouts _ChartDifficulty;

    private dataGridColumnlayouts _ChartMainBpm;

    private dataGridColumnlayouts _ChartMaxBpm;

    private dataGridColumnlayouts _ChartMinBpm;

    private dataGridColumnlayouts _ChartDuration;

    private dataGridColumnlayouts _ChartJudge;

    private dataGridColumnlayouts _ChartJudgePercent;

    private dataGridColumnlayouts _ChartFeature;

    private dataGridColumnlayouts _ChartLongNotes;

    private dataGridColumnlayouts _ChartScratchNotes;

    private dataGridColumnlayouts _ChartTotal;

    private dataGridColumnlayouts _ChartTotalPerNote;

    private dataGridColumnlayouts _ChartDensity;

    private dataGridColumnlayouts _ChartPeakDensity;

    private dataGridColumnlayouts _ChartEndDensity;

    private dataGridColumnlayouts _ChartSoflan;

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

    public dataGridColumnlayouts EntryLevel
    {
        get
        {
            return _EntryLevel ?? (_EntryLevel = CreateHiddenLayout(80));
        }
        set
        {
            if (_EntryLevel != value)
            {
                _EntryLevel = value;
                RaisePropertyChanged("EntryLevel");
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

    public dataGridColumnlayouts ChartDifficulty
    {
        get { return _ChartDifficulty ?? (_ChartDifficulty = CreateHiddenLayout(90)); }
        set { if (_ChartDifficulty != value) { _ChartDifficulty = value; RaisePropertyChanged("ChartDifficulty"); } }
    }

    public dataGridColumnlayouts ChartMainBpm
    {
        get { return _ChartMainBpm ?? (_ChartMainBpm = CreateHiddenLayout(70)); }
        set { if (_ChartMainBpm != value) { _ChartMainBpm = value; RaisePropertyChanged("ChartMainBpm"); } }
    }

    public dataGridColumnlayouts ChartMaxBpm
    {
        get { return _ChartMaxBpm ?? (_ChartMaxBpm = CreateHiddenLayout(70)); }
        set { if (_ChartMaxBpm != value) { _ChartMaxBpm = value; RaisePropertyChanged("ChartMaxBpm"); } }
    }

    public dataGridColumnlayouts ChartMinBpm
    {
        get { return _ChartMinBpm ?? (_ChartMinBpm = CreateHiddenLayout(70)); }
        set { if (_ChartMinBpm != value) { _ChartMinBpm = value; RaisePropertyChanged("ChartMinBpm"); } }
    }

    public dataGridColumnlayouts ChartDuration
    {
        get { return _ChartDuration ?? (_ChartDuration = CreateHiddenLayout(80)); }
        set { if (_ChartDuration != value) { _ChartDuration = value; RaisePropertyChanged("ChartDuration"); } }
    }

    public dataGridColumnlayouts ChartJudge
    {
        get { return _ChartJudge ?? (_ChartJudge = CreateHiddenLayout(80)); }
        set { if (_ChartJudge != value) { _ChartJudge = value; RaisePropertyChanged("ChartJudge"); } }
    }

    public dataGridColumnlayouts ChartJudgePercent
    {
        get { return _ChartJudgePercent ?? (_ChartJudgePercent = CreateHiddenLayout(70)); }
        set { if (_ChartJudgePercent != value) { _ChartJudgePercent = value; RaisePropertyChanged("ChartJudgePercent"); } }
    }

    public dataGridColumnlayouts ChartFeature
    {
        get { return _ChartFeature ?? (_ChartFeature = CreateHiddenLayout(160)); }
        set { if (_ChartFeature != value) { _ChartFeature = value; RaisePropertyChanged("ChartFeature"); } }
    }

    public dataGridColumnlayouts ChartLongNotes
    {
        get { return _ChartLongNotes ?? (_ChartLongNotes = CreateHiddenLayout(60)); }
        set { if (_ChartLongNotes != value) { _ChartLongNotes = value; RaisePropertyChanged("ChartLongNotes"); } }
    }

    public dataGridColumnlayouts ChartScratchNotes
    {
        get { return _ChartScratchNotes ?? (_ChartScratchNotes = CreateHiddenLayout(75)); }
        set { if (_ChartScratchNotes != value) { _ChartScratchNotes = value; RaisePropertyChanged("ChartScratchNotes"); } }
    }

    public dataGridColumnlayouts ChartTotal
    {
        get { return _ChartTotal ?? (_ChartTotal = CreateHiddenLayout(70)); }
        set { if (_ChartTotal != value) { _ChartTotal = value; RaisePropertyChanged("ChartTotal"); } }
    }

    public dataGridColumnlayouts ChartTotalPerNote
    {
        get { return _ChartTotalPerNote ?? (_ChartTotalPerNote = CreateHiddenLayout(60)); }
        set { if (_ChartTotalPerNote != value) { _ChartTotalPerNote = value; RaisePropertyChanged("ChartTotalPerNote"); } }
    }

    public dataGridColumnlayouts ChartDensity
    {
        get { return _ChartDensity ?? (_ChartDensity = CreateHiddenLayout(80)); }
        set { if (_ChartDensity != value) { _ChartDensity = value; RaisePropertyChanged("ChartDensity"); } }
    }

    public dataGridColumnlayouts ChartPeakDensity
    {
        get { return _ChartPeakDensity ?? (_ChartPeakDensity = CreateHiddenLayout(60)); }
        set { if (_ChartPeakDensity != value) { _ChartPeakDensity = value; RaisePropertyChanged("ChartPeakDensity"); } }
    }

    public dataGridColumnlayouts ChartEndDensity
    {
        get { return _ChartEndDensity ?? (_ChartEndDensity = CreateHiddenLayout(60)); }
        set { if (_ChartEndDensity != value) { _ChartEndDensity = value; RaisePropertyChanged("ChartEndDensity"); } }
    }

    public dataGridColumnlayouts ChartSoflan
    {
        get { return _ChartSoflan ?? (_ChartSoflan = CreateHiddenLayout(65)); }
        set { if (_ChartSoflan != value) { _ChartSoflan = value; RaisePropertyChanged("ChartSoflan"); } }
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
        EntryLevel = new dataGridColumnlayouts
        {
            Width = 80,
            Visibility = Visibility.Hidden
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
        ChartDifficulty = CreateHiddenLayout(90);
        ChartMainBpm = CreateHiddenLayout(70);
        ChartMaxBpm = CreateHiddenLayout(70);
        ChartMinBpm = CreateHiddenLayout(70);
        ChartDuration = CreateHiddenLayout(80);
        ChartJudge = CreateHiddenLayout(80);
        ChartJudgePercent = CreateHiddenLayout(70);
        ChartFeature = CreateHiddenLayout(160);
        ChartLongNotes = CreateHiddenLayout(60);
        ChartScratchNotes = CreateHiddenLayout(75);
        ChartTotal = CreateHiddenLayout(70);
        ChartTotalPerNote = CreateHiddenLayout(60);
        ChartDensity = CreateHiddenLayout(80);
        ChartPeakDensity = CreateHiddenLayout(60);
        ChartEndDensity = CreateHiddenLayout(60);
        ChartSoflan = CreateHiddenLayout(65);
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
        ApplyColumnOrder(GetAllColumnLayouts());
    }

    public dataGridColumnsSettings(viewType type)
        : this()
    {
        switch (type)
        {
            case viewType.STANDARD:
                ApplyVisibleColumnOrder(
                    Title,
                    Artist,
                    Genre,
                    Mode,
                    Folder,
                    Path,
                    Clear,
                    Rank,
                    Rate,
                    Bp,
                    Level,
                    ChartDifficulty,
                    ChartJudge,
                    ChartJudgePercent,
                    Notes,
                    ChartLongNotes,
                    ChartScratchNotes,
                    ChartMainBpm,
                    ChartMinBpm,
                    ChartMaxBpm,
                    ChartSoflan,
                    ChartTotal,
                    ChartTotalPerNote,
                    ChartDuration,
                    ChartFeature,
                    ChartDensity,
                    ChartPeakDensity,
                    ChartEndDensity,
                    PlaylistSymbols);
                break;
            case viewType.ZERO_NOTE:
                ApplyVisibleColumnOrder(
                    Title,
                    Artist,
                    Mode,
                    Warning,
                    Notes,
                    PlaylistSymbols,
                    Folder,
                    Path,
                    Hash);
                break;
            case viewType.PLAYLIST:
                {
                    ApplyVisibleColumnOrder(
                        Folder,
                        Title,
                        Artist,
                        Url1,
                        Url2,
                        Comment,
                        Clear,
                        Rank,
                        Rate,
                        Bp,
                        ChartJudge,
                        ChartJudgePercent,
                        Notes,
                        ChartLongNotes,
                        ChartScratchNotes,
                        ChartMainBpm,
                        ChartMinBpm,
                        ChartMaxBpm,
                        ChartSoflan,
                        ChartTotal,
                        ChartTotalPerNote,
                        ChartDuration,
                        ChartFeature,
                        ChartDensity,
                        ChartPeakDensity,
                        ChartEndDensity,
                        PlaylistSymbols);
                    Folder.Width = 80;
                    break;
                }
            case viewType.FULLSCAN:
                {
                    ApplyInstallAndFullScanDefaults();
                    break;
                }
            case viewType.DUPLICATE:
                ApplyVisibleColumnOrder(
                    PlaylistSymbols,
                    WavHealth,
                    BgaHealth,
                    MovieHealth,
                    Warning,
                    Hash,
                    Title,
                    Artist,
                    Mode,
                    Path,
                    Folder);
                break;
            case viewType.ENCODING:
                ApplyVisibleColumnOrder(
                    CharcterEncoding,
                    Title,
                    Artist,
                    Genre,
                    Mode,
                    Folder,
                    Path);
                break;
            case viewType.INSTALL:
                {
                    ApplyInstallAndFullScanDefaults();
                    break;
                }
        }
    }

    private dataGridColumnlayouts[] GetAllColumnLayouts()
    {
        return new[]
        {
            Status,
            EntryLevel,
            Title,
            Artist,
            Genre,
            Mode,
            Tag,
            Url1,
            Url2,
            Warning,
            Comment,
            Memo,
            Hash,
            Sha256,
            Folder,
            Path,
            InstallDst,
            InstallDstTitle,
            InstallDstArtist,
            WavHealth,
            BgaHealth,
            MovieHealth,
            PlaylistSymbols,
            Level,
            ChartDifficulty,
            ChartMainBpm,
            ChartMaxBpm,
            ChartMinBpm,
            ChartDuration,
            ChartJudge,
            ChartJudgePercent,
            ChartFeature,
            Notes,
            ChartLongNotes,
            ChartScratchNotes,
            ChartTotal,
            ChartTotalPerNote,
            ChartDensity,
            ChartPeakDensity,
            ChartEndDensity,
            ChartSoflan,
            Clear,
            Rank,
            Rate,
            Ranking,
            RankingLastupdate,
            Score,
            Combo,
            TScore,
            ScoreDifficulty,
            Bp,
            CharcterEncoding
        };
    }

    private void ApplyInstallAndFullScanDefaults()
    {
        ApplyVisibleColumnOrder(
            PlaylistSymbols,
            WavHealth,
            BgaHealth,
            MovieHealth,
            Warning,
            InstallDst,
            InstallDstTitle,
            Title,
            InstallDstArtist,
            Artist,
            Mode,
            Folder,
            Path,
            Hash);
    }

    private void ApplyVisibleColumnOrder(params dataGridColumnlayouts[] visibleLayouts)
    {
        foreach (dataGridColumnlayouts layout in GetAllColumnLayouts())
        {
            layout.Visibility = Visibility.Hidden;
        }
        foreach (dataGridColumnlayouts layout in visibleLayouts)
        {
            layout.Visibility = Visibility.Visible;
        }
        ApplyColumnOrder(visibleLayouts);
    }

    private void ApplyColumnOrder(params dataGridColumnlayouts[] firstLayouts)
    {
        int displayIndex = 0;
        foreach (dataGridColumnlayouts layout in firstLayouts)
        {
            layout.DisplayIndex = displayIndex++;
        }
        foreach (dataGridColumnlayouts layout in GetAllColumnLayouts())
        {
            if (Array.IndexOf(firstLayouts, layout) < 0)
            {
                layout.DisplayIndex = displayIndex++;
            }
        }
    }

    public void EnsureChartInfoColumnDefaults(viewType type)
    {
        _ = EntryLevel;
        _ = ChartDifficulty;
        _ = ChartMainBpm;
        _ = ChartMaxBpm;
        _ = ChartMinBpm;
        _ = ChartDuration;
        _ = ChartJudge;
        _ = ChartJudgePercent;
        _ = ChartFeature;
        _ = ChartLongNotes;
        _ = ChartScratchNotes;
        _ = ChartTotal;
        _ = ChartTotalPerNote;
        _ = ChartDensity;
        _ = ChartPeakDensity;
        _ = ChartEndDensity;
        _ = ChartSoflan;
    }

    private static dataGridColumnlayouts CreateHiddenLayout(int width)
    {
        return new dataGridColumnlayouts
        {
            Width = width,
            Visibility = Visibility.Hidden
        };
    }
}
