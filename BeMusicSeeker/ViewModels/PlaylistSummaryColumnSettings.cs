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

    private ColumnLayout _CompatPrefix = new();

    private ColumnLayout _Symbol = new();

    private ColumnLayout _LastUpdate = new();

    private ColumnLayout _TotalCharts = new();

    private ColumnLayout _OwnedCharts = new();

    private ColumnLayout _MissingCharts = new();

    private ColumnLayout _OwnedRatio = new();

    private ColumnLayout _Link = new();

    private ColumnLayout _Header = new();

    private ColumnLayout _Data = new();

    private ColumnLayout _IsExternalSync = new();

    private ColumnLayout _Status = new();

    private ColumnLayout _IsRootFolder = new();

    private ColumnLayout _BmtSort = new();

    private ColumnLayout _IsBmtOutput = new();

    private bool _playlistIdLoaded;

    private bool _outputBaseLoaded;

    private bool _nameLoaded;

    private bool _compatPrefixLoaded;

    private bool _symbolLoaded;

    private bool _lastUpdateLoaded;

    private bool _totalChartsLoaded;

    private bool _ownedChartsLoaded;

    private bool _missingChartsLoaded;

    private bool _ownedRatioLoaded;

    private bool _linkLoaded;

    private bool _headerLoaded;

    private bool _dataLoaded;

    private bool _isExternalSyncLoaded;

    private bool _statusLoaded;

    private bool _isRootFolderLoaded;

    private bool _bmtSortLoaded;

    private bool _isBmtOutputLoaded;

    public ColumnLayout PlaylistId
    {
        get
        {
            return _PlaylistId;
        }
        set
        {
            _PlaylistId = value;
            _playlistIdLoaded = true;
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
            _outputBaseLoaded = true;
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
            _nameLoaded = true;
            RaisePropertyChanged("Name");
        }
    }

    /// <summary>
    /// 互換フォルダ接頭辞列の表示設定です。
    /// </summary>
    public ColumnLayout CompatPrefix
    {
        get
        {
            return _CompatPrefix;
        }
        set
        {
            _CompatPrefix = value;
            _compatPrefixLoaded = true;
            RaisePropertyChanged("CompatPrefix");
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
            _symbolLoaded = true;
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
            _lastUpdateLoaded = true;
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
            _totalChartsLoaded = true;
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
            _ownedChartsLoaded = true;
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
            _missingChartsLoaded = true;
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
            _ownedRatioLoaded = true;
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
            _linkLoaded = true;
            RaisePropertyChanged("Link");
        }
    }

    public ColumnLayout Header
    {
        get
        {
            return _Header;
        }
        set
        {
            _Header = value;
            _headerLoaded = true;
            RaisePropertyChanged("Header");
        }
    }

    public ColumnLayout Data
    {
        get
        {
            return _Data;
        }
        set
        {
            _Data = value;
            _dataLoaded = true;
            RaisePropertyChanged("Data");
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
            _isExternalSyncLoaded = true;
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
            _statusLoaded = true;
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
            _isRootFolderLoaded = true;
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
            _bmtSortLoaded = true;
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
            _isBmtOutputLoaded = true;
            RaisePropertyChanged("IsBmtOutput");
        }
    }

    public PlaylistSummaryColumnSettings()
    {
        PlaylistId.Width = 60;
        OutputBase.Width = 100;
        Name.Width = 220;
        CompatPrefix.Width = 80;
        Symbol.Width = 70;
        LastUpdate.Width = 145;
        TotalCharts.Width = 80;
        OwnedCharts.Width = 80;
        MissingCharts.Width = 80;
        OwnedRatio.Width = 80;
        Link.Width = 70;
        Header.Width = 70;
        Header.Visibility = Visibility.Hidden;
        Data.Width = 70;
        Data.Visibility = Visibility.Hidden;
        IsExternalSync.Width = 70;
        Status.Width = 90;
        IsRootFolder.Width = 70;
        BmtSort.Width = 80;
        IsBmtOutput.Width = 95;
        int displayIndex = 0;
        PlaylistId.DisplayIndex = displayIndex++;
        OutputBase.DisplayIndex = displayIndex++;
        Name.DisplayIndex = displayIndex++;
        CompatPrefix.DisplayIndex = displayIndex++;
        Symbol.DisplayIndex = displayIndex++;
        LastUpdate.DisplayIndex = displayIndex++;
        TotalCharts.DisplayIndex = displayIndex++;
        OwnedCharts.DisplayIndex = displayIndex++;
        MissingCharts.DisplayIndex = displayIndex++;
        OwnedRatio.DisplayIndex = displayIndex++;
        Link.DisplayIndex = displayIndex++;
        Header.DisplayIndex = displayIndex++;
        Data.DisplayIndex = displayIndex++;
        IsExternalSync.DisplayIndex = displayIndex++;
        Status.DisplayIndex = displayIndex++;
        IsRootFolder.DisplayIndex = displayIndex++;
        BmtSort.DisplayIndex = displayIndex++;
        IsBmtOutput.DisplayIndex = displayIndex++;
    }

    /// <summary>
    /// 保存済み設定に欠けた列レイアウトがある場合は、列設定初期化と同じ既定レイアウトへ戻します。
    /// </summary>
    public void EnsureCompatibility()
    {
        if (!HasAllLayouts())
        {
            ResetToDefaultLayout();
        }
    }

    /// <summary>
    /// 現在の列定義に必要なすべてのレイアウトが保存済み設定に存在するかどうかを返します。
    /// </summary>
    /// <returns>すべて存在する場合は true。</returns>
    public bool HasAllLayouts()
    {
        return HasLayout(PlaylistId, _playlistIdLoaded)
            && HasLayout(OutputBase, _outputBaseLoaded)
            && HasLayout(Name, _nameLoaded)
            && HasLayout(CompatPrefix, _compatPrefixLoaded)
            && HasLayout(Symbol, _symbolLoaded)
            && HasLayout(LastUpdate, _lastUpdateLoaded)
            && HasLayout(TotalCharts, _totalChartsLoaded)
            && HasLayout(OwnedCharts, _ownedChartsLoaded)
            && HasLayout(MissingCharts, _missingChartsLoaded)
            && HasLayout(OwnedRatio, _ownedRatioLoaded)
            && HasLayout(Link, _linkLoaded)
            && HasLayout(Header, _headerLoaded)
            && HasLayout(Data, _dataLoaded)
            && HasLayout(IsExternalSync, _isExternalSyncLoaded)
            && HasLayout(Status, _statusLoaded)
            && HasLayout(IsRootFolder, _isRootFolderLoaded)
            && HasLayout(BmtSort, _bmtSortLoaded)
            && HasLayout(IsBmtOutput, _isBmtOutputLoaded);
    }

    private static bool HasLayout(ColumnLayout layout, bool loaded)
    {
        return loaded && layout != null;
    }

    private void ResetToDefaultLayout()
    {
        PlaylistId = new ColumnLayout { Width = 60, DisplayIndex = 0 };
        OutputBase = new ColumnLayout { Width = 100, DisplayIndex = 1 };
        Name = new ColumnLayout { Width = 220, DisplayIndex = 2 };
        CompatPrefix = new ColumnLayout { Width = 80, DisplayIndex = 3 };
        Symbol = new ColumnLayout { Width = 70, DisplayIndex = 4 };
        LastUpdate = new ColumnLayout { Width = 145, DisplayIndex = 5 };
        TotalCharts = new ColumnLayout { Width = 80, DisplayIndex = 6 };
        OwnedCharts = new ColumnLayout { Width = 80, DisplayIndex = 7 };
        MissingCharts = new ColumnLayout { Width = 80, DisplayIndex = 8 };
        OwnedRatio = new ColumnLayout { Width = 80, DisplayIndex = 9 };
        Link = new ColumnLayout { Width = 70, DisplayIndex = 10 };
        Header = new ColumnLayout { Width = 70, DisplayIndex = 11, Visibility = Visibility.Hidden };
        Data = new ColumnLayout { Width = 70, DisplayIndex = 12, Visibility = Visibility.Hidden };
        IsExternalSync = new ColumnLayout { Width = 70, DisplayIndex = 13 };
        Status = new ColumnLayout { Width = 90, DisplayIndex = 14 };
        IsRootFolder = new ColumnLayout { Width = 70, DisplayIndex = 15 };
        BmtSort = new ColumnLayout { Width = 80, DisplayIndex = 16 };
        IsBmtOutput = new ColumnLayout { Width = 95, DisplayIndex = 17 };
    }
}
