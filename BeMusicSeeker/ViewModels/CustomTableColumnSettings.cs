using System;
using System.Windows;
using Livet;

namespace BeMusicSeeker.ViewModels;

[Serializable]
public class CustomTableColumnSettings : NotificationObject
{
    [Serializable]
    public class ColumnLayout : NotificationObject, ICustomTableColumnLayout
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

    public enum ViewKind
    {
        STANDARD,
        PLAYLIST,
        FULLSCAN,
        DUPLICATE,
        ENCODING,
        INSTALL,
        ZERO_NOTE,
        CHART_INFO_PARSE_ERROR,
        UNREGISTERED
    }

    private ColumnLayout _Status;

    private ColumnLayout _Level;

    private ColumnLayout _EntryLevel;

    private ColumnLayout _Title;

    private ColumnLayout _Artist;

    private ColumnLayout _Genre;

    private ColumnLayout _Mode;

    private ColumnLayout _Tag;

    private ColumnLayout _Url1;

    private ColumnLayout _Url2;

    private ColumnLayout _Clear;

    private ColumnLayout _Rank;

    private ColumnLayout _Ranking;

    private ColumnLayout _RankingLastupdate;

    private ColumnLayout _TScore;

    private ColumnLayout _ScoreDifficulty;

    private ColumnLayout _Warning;

    private ColumnLayout _Comment;

    private ColumnLayout _Memo;

    private ColumnLayout _Hash;

    private ColumnLayout _Sha256;

    private ColumnLayout _Folder;

    private ColumnLayout _Path;

    private ColumnLayout _InstallDst;

    private ColumnLayout _InstallDstTitle;

    private ColumnLayout _InstallDstArtist;

    private ColumnLayout _WavHealth;

    private ColumnLayout _BgaHealth;

    private ColumnLayout _MovieHealth;

    private ColumnLayout _PlaylistSymbols;

    private ColumnLayout _CharcterEncoding;

    private ColumnLayout _Rate;

    private ColumnLayout _Score;

    private ColumnLayout _Notes;

    private ColumnLayout _Combo;

    private ColumnLayout _Bp;

    private ColumnLayout _ChartDifficulty;

    private ColumnLayout _ChartMainBpm;

    private ColumnLayout _ChartMaxBpm;

    private ColumnLayout _ChartMinBpm;

    private ColumnLayout _ChartDuration;

    private ColumnLayout _ChartJudge;

    private ColumnLayout _ChartJudgePercent;

    private ColumnLayout _ChartFeature;

    private ColumnLayout _ChartLongNotes;

    private ColumnLayout _ChartScratchNotes;

    private ColumnLayout _ChartTotal;

    private ColumnLayout _ChartTotalPerNote;

    private ColumnLayout _ChartDensity;

    private ColumnLayout _ChartPeakDensity;

    private ColumnLayout _ChartEndDensity;

    private ColumnLayout _ChartSoflan;

    public ColumnLayout Status
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

    public ColumnLayout Level
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

    public ColumnLayout EntryLevel
    {
        get
        {
            return _EntryLevel ??= CreateHiddenLayout(80);
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

    public ColumnLayout Title
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

    public ColumnLayout Artist
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

    public ColumnLayout Genre
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

    public ColumnLayout Mode
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

    public ColumnLayout Tag
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

    public ColumnLayout Url1
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

    public ColumnLayout Url2
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

    public ColumnLayout Clear
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

    public ColumnLayout Rank
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

    public ColumnLayout Ranking
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

    public ColumnLayout RankingLastupdate
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

    public ColumnLayout TScore
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

    public ColumnLayout ScoreDifficulty
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

    public ColumnLayout Warning
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

    public ColumnLayout Comment
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

    public ColumnLayout Memo
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

    public ColumnLayout Hash
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

    public ColumnLayout Sha256
    {
        get
        {
            _Sha256 ??= new ColumnLayout
                {
                    Width = 480,
                    Visibility = Visibility.Hidden
                };
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

    public ColumnLayout Folder
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

    public ColumnLayout Path
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

    public ColumnLayout InstallDst
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

    public ColumnLayout InstallDstTitle
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

    public ColumnLayout InstallDstArtist
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

    public ColumnLayout WavHealth
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

    public ColumnLayout BgaHealth
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

    public ColumnLayout MovieHealth
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

    public ColumnLayout PlaylistSymbols
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

    public ColumnLayout CharcterEncoding
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

    public ColumnLayout Rate
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

    public ColumnLayout Score
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

    public ColumnLayout Notes
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

    public ColumnLayout Combo
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

    public ColumnLayout Bp
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

    public ColumnLayout ChartDifficulty
    {
        get { return _ChartDifficulty ??= CreateHiddenLayout(80); }
        set { if (_ChartDifficulty != value) { _ChartDifficulty = value; RaisePropertyChanged("ChartDifficulty"); } }
    }

    public ColumnLayout ChartMainBpm
    {
        get { return _ChartMainBpm ??= CreateHiddenLayout(40); }
        set { if (_ChartMainBpm != value) { _ChartMainBpm = value; RaisePropertyChanged("ChartMainBpm"); } }
    }

    public ColumnLayout ChartMaxBpm
    {
        get { return _ChartMaxBpm ??= CreateHiddenLayout(40); }
        set { if (_ChartMaxBpm != value) { _ChartMaxBpm = value; RaisePropertyChanged("ChartMaxBpm"); } }
    }

    public ColumnLayout ChartMinBpm
    {
        get { return _ChartMinBpm ??= CreateHiddenLayout(40); }
        set { if (_ChartMinBpm != value) { _ChartMinBpm = value; RaisePropertyChanged("ChartMinBpm"); } }
    }

    public ColumnLayout ChartDuration
    {
        get { return _ChartDuration ??= CreateHiddenLayout(50); }
        set { if (_ChartDuration != value) { _ChartDuration = value; RaisePropertyChanged("ChartDuration"); } }
    }

    public ColumnLayout ChartJudge
    {
        get { return _ChartJudge ??= CreateHiddenLayout(70); }
        set { if (_ChartJudge != value) { _ChartJudge = value; RaisePropertyChanged("ChartJudge"); } }
    }

    public ColumnLayout ChartJudgePercent
    {
        get { return _ChartJudgePercent ??= CreateHiddenLayout(60); }
        set { if (_ChartJudgePercent != value) { _ChartJudgePercent = value; RaisePropertyChanged("ChartJudgePercent"); } }
    }

    public ColumnLayout ChartFeature
    {
        get { return _ChartFeature ??= CreateHiddenLayout(60); }
        set { if (_ChartFeature != value) { _ChartFeature = value; RaisePropertyChanged("ChartFeature"); } }
    }

    public ColumnLayout ChartLongNotes
    {
        get { return _ChartLongNotes ??= CreateHiddenLayout(40); }
        set { if (_ChartLongNotes != value) { _ChartLongNotes = value; RaisePropertyChanged("ChartLongNotes"); } }
    }

    public ColumnLayout ChartScratchNotes
    {
        get { return _ChartScratchNotes ??= CreateHiddenLayout(40); }
        set { if (_ChartScratchNotes != value) { _ChartScratchNotes = value; RaisePropertyChanged("ChartScratchNotes"); } }
    }

    public ColumnLayout ChartTotal
    {
        get { return _ChartTotal ??= CreateHiddenLayout(40); }
        set { if (_ChartTotal != value) { _ChartTotal = value; RaisePropertyChanged("ChartTotal"); } }
    }

    public ColumnLayout ChartTotalPerNote
    {
        get { return _ChartTotalPerNote ??= CreateHiddenLayout(40); }
        set { if (_ChartTotalPerNote != value) { _ChartTotalPerNote = value; RaisePropertyChanged("ChartTotalPerNote"); } }
    }

    public ColumnLayout ChartDensity
    {
        get { return _ChartDensity ??= CreateHiddenLayout(40); }
        set { if (_ChartDensity != value) { _ChartDensity = value; RaisePropertyChanged("ChartDensity"); } }
    }

    public ColumnLayout ChartPeakDensity
    {
        get { return _ChartPeakDensity ??= CreateHiddenLayout(40); }
        set { if (_ChartPeakDensity != value) { _ChartPeakDensity = value; RaisePropertyChanged("ChartPeakDensity"); } }
    }

    public ColumnLayout ChartEndDensity
    {
        get { return _ChartEndDensity ??= CreateHiddenLayout(40); }
        set { if (_ChartEndDensity != value) { _ChartEndDensity = value; RaisePropertyChanged("ChartEndDensity"); } }
    }

    public ColumnLayout ChartSoflan
    {
        get { return _ChartSoflan ??= CreateHiddenLayout(40); }
        set { if (_ChartSoflan != value) { _ChartSoflan = value; RaisePropertyChanged("ChartSoflan"); } }
    }

    public CustomTableColumnSettings()
    {
        Status = new ColumnLayout
        {
            Width = 18
        };
        Level = new ColumnLayout
        {
            Width = 50
        };
        EntryLevel = new ColumnLayout
        {
            Width = 80,
            Visibility = Visibility.Hidden
        };
        Title = new ColumnLayout
        {
            Width = 200
        };
        Artist = new ColumnLayout
        {
            Width = 100
        };
        Genre = new ColumnLayout
        {
            Width = 100
        };
        Mode = new ColumnLayout
        {
            Width = 50
        };
        Tag = new ColumnLayout
        {
            Width = 50
        };
        Url1 = new ColumnLayout
        {
            Width = 40
        };
        Url2 = new ColumnLayout
        {
            Width = 40
        };
        Clear = new ColumnLayout
        {
            Width = 90
        };
        Rank = new ColumnLayout
        {
            Width = 60
        };
        Ranking = new ColumnLayout
        {
            Width = 95
        };
        RankingLastupdate = new ColumnLayout
        {
            Width = 95
        };
        Rate = new ColumnLayout
        {
            Width = 60
        };
        Score = new ColumnLayout
        {
            Width = 40
        };
        Notes = new ColumnLayout
        {
            Width = 40
        };
        Combo = new ColumnLayout
        {
            Width = 40
        };
        Bp = new ColumnLayout
        {
            Width = 40
        };
        ChartDifficulty = CreateHiddenLayout(80);
        ChartMainBpm = CreateHiddenLayout(40);
        ChartMaxBpm = CreateHiddenLayout(40);
        ChartMinBpm = CreateHiddenLayout(40);
        ChartDuration = CreateHiddenLayout(60);
        ChartJudge = CreateHiddenLayout(70);
        ChartJudgePercent = CreateHiddenLayout(60);
        ChartFeature = CreateHiddenLayout(60);
        ChartLongNotes = CreateHiddenLayout(40);
        ChartScratchNotes = CreateHiddenLayout(40);
        ChartTotal = CreateHiddenLayout(40);
        ChartTotalPerNote = CreateHiddenLayout(40);
        ChartDensity = CreateHiddenLayout(40);
        ChartPeakDensity = CreateHiddenLayout(40);
        ChartEndDensity = CreateHiddenLayout(40);
        ChartSoflan = CreateHiddenLayout(40);
        TScore = new ColumnLayout
        {
            Width = 40
        };
        ScoreDifficulty = new ColumnLayout
        {
            Width = 40
        };
        Warning = new ColumnLayout
        {
            Width = 200
        };
        Comment = new ColumnLayout
        {
            Width = 200
        };
        Memo = new ColumnLayout
        {
            Width = 200
        };
        Hash = new ColumnLayout
        {
            Width = 240
        };
        Sha256 = new ColumnLayout
        {
            Width = 480,
            Visibility = Visibility.Hidden
        };
        Folder = new ColumnLayout
        {
            Width = 140
        };
        Path = new ColumnLayout
        {
            Width = 250
        };
        InstallDst = new ColumnLayout
        {
            Width = 250
        };
        InstallDstTitle = new ColumnLayout
        {
            Width = 200
        };
        InstallDstArtist = new ColumnLayout
        {
            Width = 100
        };
        WavHealth = new ColumnLayout
        {
            Width = 40
        };
        BgaHealth = new ColumnLayout
        {
            Width = 40
        };
        MovieHealth = new ColumnLayout
        {
            Width = 40
        };
        CharcterEncoding = new ColumnLayout
        {
            Width = 130
        };
        PlaylistSymbols = new ColumnLayout
        {
            Width = 70
        };
        ApplyColumnOrder(GetAllColumnLayouts());
    }

    public CustomTableColumnSettings(ViewKind type)
        : this()
    {
        switch (type)
        {
            case ViewKind.STANDARD:
                ApplyVisibleColumnOrder(
                    Status,
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
            case ViewKind.UNREGISTERED:
                ApplyVisibleColumnOrder(
                    Status,
                    Warning,
                    Title,
                    Artist,
                    Genre,
                    Mode,
                    Folder,
                    Path,
                    PlaylistSymbols);
                break;
            case ViewKind.ZERO_NOTE:
                ApplyVisibleColumnOrder(
                    Status,
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
            case ViewKind.CHART_INFO_PARSE_ERROR:
                ApplyVisibleColumnOrder(
                    Status,
                    PlaylistSymbols,
                    WavHealth,
                    BgaHealth,
                    MovieHealth,
                    Warning,
                    Title,
                    Artist,
                    Mode,
                    Folder,
                    Path,
                    Hash);
                break;
            case ViewKind.PLAYLIST:
                {
                    ApplyVisibleColumnOrder(
                        Status,
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
            case ViewKind.FULLSCAN:
                {
                    ApplyInstallAndFullScanDefaults();
                    break;
                }
            case ViewKind.DUPLICATE:
                ApplyVisibleColumnOrder(
                    Status,
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
            case ViewKind.ENCODING:
                ApplyVisibleColumnOrder(
                    Status,
                    CharcterEncoding,
                    Title,
                    Artist,
                    Genre,
                    Mode,
                    Folder,
                    Path);
                break;
            case ViewKind.INSTALL:
                {
                    ApplyInstallAndFullScanDefaults();
                    break;
                }
        }
    }

    private ColumnLayout[] GetAllColumnLayouts()
    {
        return
        [
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
        ];
    }

    private void ApplyInstallAndFullScanDefaults()
    {
        ApplyVisibleColumnOrder(
            Status,
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

    private void ApplyVisibleColumnOrder(params ColumnLayout[] visibleLayouts)
    {
        foreach (ColumnLayout layout in GetAllColumnLayouts())
        {
            layout.Visibility = Visibility.Hidden;
        }
        foreach (ColumnLayout layout in visibleLayouts)
        {
            layout.Visibility = Visibility.Visible;
        }
        ApplyColumnOrder(visibleLayouts);
    }

    private void ApplyColumnOrder(params ColumnLayout[] firstLayouts)
    {
        int displayIndex = 0;
        foreach (ColumnLayout layout in firstLayouts)
        {
            layout.DisplayIndex = displayIndex++;
        }
        foreach (ColumnLayout layout in GetAllColumnLayouts())
        {
            if (Array.IndexOf(firstLayouts, layout) < 0)
            {
                layout.DisplayIndex = displayIndex++;
            }
        }
    }

    public void EnsureChartInfoColumnDefaults(ViewKind type)
    {
        EnsureStatusColumnDefaults();
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

    private static ColumnLayout CreateHiddenLayout(int width)
    {
        return new ColumnLayout
        {
            Width = width,
            Visibility = Visibility.Hidden
        };
    }

    private void EnsureStatusColumnDefaults()
    {
        if (_Status == null)
        {
            Status = new ColumnLayout();
        }
        Status.Width = 18;
        Status.Visibility = Visibility.Visible;
        Status.DisplayIndex = 0;
    }
}
