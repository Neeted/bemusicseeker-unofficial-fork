using System;
using System.Windows;
using Livet;

namespace BeMusicSeeker.ViewModels;

[Serializable]
public class PlaylistSummaryColumnSettings : NotificationObject
{
    [Serializable]
    public class ColumnLayout : NotificationObject
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

    private ColumnLayout _PlaylistId = new ColumnLayout();

    private ColumnLayout _Name = new ColumnLayout();

    private ColumnLayout _Symbol = new ColumnLayout();

    private ColumnLayout _LastUpdate = new ColumnLayout();

    private ColumnLayout _TotalCharts = new ColumnLayout();

    private ColumnLayout _OwnedCharts = new ColumnLayout();

    private ColumnLayout _MissingCharts = new ColumnLayout();

    private ColumnLayout _OwnedRatio = new ColumnLayout();

    private ColumnLayout _Link = new ColumnLayout();

    private ColumnLayout _IsExternalSync = new ColumnLayout();

    private ColumnLayout _Status = new ColumnLayout();

    private ColumnLayout _IsRootFolder = new ColumnLayout();

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

    public PlaylistSummaryColumnSettings()
    {
        PlaylistId.Width = 60;
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
        int num = 0;
        PlaylistId.DisplayIndex = num++;
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
    }

    public void EnsureCompatibility()
    {
        bool flag = _Status == null;
        PlaylistId ??= new ColumnLayout();
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
        ApplyDefaultLayout(PlaylistId, 60, 0);
        ApplyDefaultLayout(Name, 220, 1);
        ApplyDefaultLayout(Symbol, 70, 2);
        ApplyDefaultLayout(LastUpdate, 145, 3);
        ApplyDefaultLayout(TotalCharts, 80, 4);
        ApplyDefaultLayout(OwnedCharts, 80, 5);
        ApplyDefaultLayout(MissingCharts, 80, 6);
        ApplyDefaultLayout(OwnedRatio, 80, 7);
        ApplyDefaultLayout(Link, 70, 8);
        ApplyDefaultLayout(IsExternalSync, 70, 9);
        ApplyDefaultLayout(Status, 90, 10);
        ApplyDefaultLayout(IsRootFolder, 70, 11);
        if (flag && IsRootFolder.DisplayIndex <= 10)
        {
            IsRootFolder.DisplayIndex = 11;
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
