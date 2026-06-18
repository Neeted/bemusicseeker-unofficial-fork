using System;
using System.Windows;
using Livet;

namespace BeMusicSeeker.ViewModels;

[Serializable]
public class PlaylistSummaryColumnSettings : NotificationObject
{
    [Serializable]
    public class ColumnLayout : NotificationObject, ICustomTableColumnLayout
    {
        private int _Width = 80;

        private Visibility _Visibility = Visibility.Visible;

        private int _DisplayIndex = -1;

        public int Width
        {
            get
            {
                return _Width;
            }
            set
            {
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

    private ColumnLayout _PlaylistId = new();

    private ColumnLayout _OutputBase = new();

    private ColumnLayout _Name = new();

    private ColumnLayout _Symbol = new();

    private ColumnLayout _LastUpdate = new();

    private ColumnLayout _TotalCharts = new();

    private ColumnLayout _OwnedCharts = new();

    private ColumnLayout _MissingCharts = new();

    private ColumnLayout _OwnedRatio = new();

    private ColumnLayout _Link = new();

    private ColumnLayout _IsExternalSync = new();

    private ColumnLayout _Status = new();

    private ColumnLayout _IsRootFolder = new();

    private ColumnLayout _BmtSort = new();

    private ColumnLayout _IsBmtOutput = new();

    public ColumnLayout PlaylistId
    {
        get
        {
            return _PlaylistId;
        }
        set
        {
            _PlaylistId = value;
            RaisePropertyChanged("PlaylistId");
        }
    }

    public ColumnLayout OutputBase
    {
        get
        {
            return _OutputBase;
        }
        set
        {
            _OutputBase = value;
            RaisePropertyChanged("OutputBase");
        }
    }

    public ColumnLayout Name
    {
        get
        {
            return _Name;
        }
        set
        {
            _Name = value;
            RaisePropertyChanged("Name");
        }
    }

    public ColumnLayout Symbol
    {
        get
        {
            return _Symbol;
        }
        set
        {
            _Symbol = value;
            RaisePropertyChanged("Symbol");
        }
    }

    public ColumnLayout LastUpdate
    {
        get
        {
            return _LastUpdate;
        }
        set
        {
            _LastUpdate = value;
            RaisePropertyChanged("LastUpdate");
        }
    }

    public ColumnLayout TotalCharts
    {
        get
        {
            return _TotalCharts;
        }
        set
        {
            _TotalCharts = value;
            RaisePropertyChanged("TotalCharts");
        }
    }

    public ColumnLayout OwnedCharts
    {
        get
        {
            return _OwnedCharts;
        }
        set
        {
            _OwnedCharts = value;
            RaisePropertyChanged("OwnedCharts");
        }
    }

    public ColumnLayout MissingCharts
    {
        get
        {
            return _MissingCharts;
        }
        set
        {
            _MissingCharts = value;
            RaisePropertyChanged("MissingCharts");
        }
    }

    public ColumnLayout OwnedRatio
    {
        get
        {
            return _OwnedRatio;
        }
        set
        {
            _OwnedRatio = value;
            RaisePropertyChanged("OwnedRatio");
        }
    }

    public ColumnLayout Link
    {
        get
        {
            return _Link;
        }
        set
        {
            _Link = value;
            RaisePropertyChanged("Link");
        }
    }

    public ColumnLayout IsExternalSync
    {
        get
        {
            return _IsExternalSync;
        }
        set
        {
            _IsExternalSync = value;
            RaisePropertyChanged("IsExternalSync");
        }
    }

    public ColumnLayout Status
    {
        get
        {
            return _Status;
        }
        set
        {
            _Status = value;
            RaisePropertyChanged("Status");
        }
    }

    public ColumnLayout IsRootFolder
    {
        get
        {
            return _IsRootFolder;
        }
        set
        {
            _IsRootFolder = value;
            RaisePropertyChanged("IsRootFolder");
        }
    }

    public ColumnLayout BmtSort
    {
        get
        {
            return _BmtSort;
        }
        set
        {
            _BmtSort = value;
            RaisePropertyChanged("BmtSort");
        }
    }

    public ColumnLayout IsBmtOutput
    {
        get
        {
            return _IsBmtOutput;
        }
        set
        {
            _IsBmtOutput = value;
            RaisePropertyChanged("IsBmtOutput");
        }
    }

    public PlaylistSummaryColumnSettings()
    {
        PlaylistId.Width = 60;
        OutputBase.Width = 100;
        Name.Width = 220;
        Symbol.Width = 70;
        LastUpdate.Width = 145;
        TotalCharts.Width = 80;
        OwnedCharts.Width = 80;
        MissingCharts.Width = 80;
        OwnedRatio.Width = 80;
        Link.Width = 70;
        IsExternalSync.Width = 70;
        Status.Width = 90;
        IsRootFolder.Width = 70;
        BmtSort.Width = 80;
        IsBmtOutput.Width = 95;
        int num = 0;
        PlaylistId.DisplayIndex = num++;
        OutputBase.DisplayIndex = num++;
        Name.DisplayIndex = num++;
        Symbol.DisplayIndex = num++;
        LastUpdate.DisplayIndex = num++;
        TotalCharts.DisplayIndex = num++;
        OwnedCharts.DisplayIndex = num++;
        MissingCharts.DisplayIndex = num++;
        OwnedRatio.DisplayIndex = num++;
        Link.DisplayIndex = num++;
        IsExternalSync.DisplayIndex = num++;
        Status.DisplayIndex = num++;
        IsRootFolder.DisplayIndex = num++;
        BmtSort.DisplayIndex = num++;
        IsBmtOutput.DisplayIndex = num++;
    }

    public void EnsureCompatibility()
    {
        bool flag = _Status == null;
        bool outputBaseMissing = _OutputBase == null;
        PlaylistId ??= new ColumnLayout();
        OutputBase ??= new ColumnLayout();
        Name ??= new ColumnLayout();
        Symbol ??= new ColumnLayout();
        LastUpdate ??= new ColumnLayout();
        TotalCharts ??= new ColumnLayout();
        OwnedCharts ??= new ColumnLayout();
        MissingCharts ??= new ColumnLayout();
        OwnedRatio ??= new ColumnLayout();
        Link ??= new ColumnLayout();
        IsExternalSync ??= new ColumnLayout();
        Status ??= new ColumnLayout();
        IsRootFolder ??= new ColumnLayout();
        BmtSort ??= new ColumnLayout();
        IsBmtOutput ??= new ColumnLayout();
        ApplyDefaultLayout(PlaylistId, 60, 0);
        ApplyDefaultLayout(OutputBase, 100, 1);
        ApplyDefaultLayout(Name, 220, 2);
        ApplyDefaultLayout(Symbol, 70, 3);
        ApplyDefaultLayout(LastUpdate, 145, 4);
        ApplyDefaultLayout(TotalCharts, 80, 5);
        ApplyDefaultLayout(OwnedCharts, 80, 6);
        ApplyDefaultLayout(MissingCharts, 80, 7);
        ApplyDefaultLayout(OwnedRatio, 80, 8);
        ApplyDefaultLayout(Link, 70, 9);
        ApplyDefaultLayout(IsExternalSync, 70, 10);
        ApplyDefaultLayout(Status, 90, 11);
        ApplyDefaultLayout(IsRootFolder, 70, 12);
        ApplyDefaultLayout(BmtSort, 80, 13);
        ApplyDefaultLayout(IsBmtOutput, 95, 14);
        if (outputBaseMissing)
        {
            ShiftDisplayIndexesFrom(Name, Symbol, LastUpdate, TotalCharts, OwnedCharts, MissingCharts, OwnedRatio, Link, IsExternalSync, Status, IsRootFolder, BmtSort, IsBmtOutput);
            OutputBase.DisplayIndex = 1;
        }
        if (flag && IsRootFolder.DisplayIndex <= 10)
        {
            IsRootFolder.DisplayIndex = 12;
        }
    }

    private static void ShiftDisplayIndexesFrom(params ColumnLayout[] layouts)
    {
        foreach (ColumnLayout layout in layouts ?? [])
        {
            if (layout != null && layout.DisplayIndex >= 1)
            {
                layout.DisplayIndex++;
            }
        }
    }

    private static void ApplyDefaultLayout(ColumnLayout layout, int width, int displayIndex)
    {
        if (layout == null)
        {
            return;
        }
        if (layout.Width <= 0)
        {
            layout.Width = width;
        }
        if (layout.DisplayIndex < 0)
        {
            layout.DisplayIndex = displayIndex;
        }
    }
}
