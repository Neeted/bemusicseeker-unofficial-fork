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
		IsRootFolder.Width = 70;
	}
}
