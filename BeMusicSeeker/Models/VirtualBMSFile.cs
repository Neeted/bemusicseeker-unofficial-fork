using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading;

namespace BeMusicSeeker.Models;

public class VirtualBMSFile : BMSFile, IDisposable
{
	private static long createdCount;

	private static long disposedCount;

	private BMSTableEntry entry;

	private BMSFile bmsfile;

	private PropertyChangedEventHandler listenerForRealBMSFileChanged;

	private bool isDisposed;

	public Uri Url
	{
		get
		{
			return entry.Url;
		}
		set
		{
			entry.Url = value;
			RaisePropertyChanged("Url");
		}
	}

	public Uri Url_diff
	{
		get
		{
			return entry.Url_diff;
		}
		set
		{
			entry.Url_diff = value;
			RaisePropertyChanged("Url_diff");
		}
	}

	public string name_diff
	{
		get
		{
			return entry.name_diff;
		}
		set
		{
			entry.name_diff = value;
			RaisePropertyChanged("name_diff");
		}
	}

	public string comment
	{
		get
		{
			return entry.comment;
		}
		set
		{
			entry.comment = value;
			RaisePropertyChanged("comment");
		}
	}

	public string memo
	{
		get
		{
			return entry.memo;
		}
		set
		{
			entry.memo = value;
			RaisePropertyChanged("memo");
		}
	}

	public List<string> Org_md5
	{
		get
		{
			return entry.Org_md5;
		}
		set
		{
			entry.Org_md5 = value;
			RaisePropertyChanged("Org_md5");
		}
	}

	public string lr2_bmsid
	{
		get
		{
			return entry.lr2_bmsid;
		}
		set
		{
			entry.lr2_bmsid = value;
			RaisePropertyChanged("lr2_bmsid");
		}
	}

	public override string hash
	{
		get
		{
			if (bmsfile != null && bmsfile.hash != null)
			{
				return bmsfile.hash;
			}
			return entry.md5;
		}
		protected set
		{
			throw new NotImplementedException();
		}
	}

	public override string Title
	{
		get
		{
			if (bmsfile != null && bmsfile.Title != null)
			{
				return bmsfile.Title;
			}
			return entry.title;
		}
		protected set
		{
			throw new NotImplementedException();
		}
	}

	public override string Artist
	{
		get
		{
			if (bmsfile != null && bmsfile.Artist != null)
			{
				return bmsfile.Artist;
			}
			return entry.artist;
		}
		protected set
		{
			throw new NotImplementedException();
		}
	}

	public override string genre
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.genre;
			}
			return base.genre;
		}
		protected set
		{
			throw new NotImplementedException();
		}
	}

	public override string warning
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.warning;
			}
			return base.warning;
		}
		set
		{
			if (bmsfile == null)
			{
				base.warning = value;
			}
			else
			{
				bmsfile.warning = value;
			}
		}
	}

	public override bool HasZeroNoteMismatchWarning
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.HasZeroNoteMismatchWarning;
			}
			return base.HasZeroNoteMismatchWarning;
		}
		set
		{
			if (bmsfile == null)
			{
				base.HasZeroNoteMismatchWarning = value;
			}
			else
			{
				bmsfile.HasZeroNoteMismatchWarning = value;
			}
		}
	}

	public override bool HasHighlightedWarning
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.HasHighlightedWarning;
			}
			return base.HasHighlightedWarning;
		}
	}

	public override string DisplayWarning
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.DisplayWarning;
			}
			return base.DisplayWarning;
		}
	}

	public override string instl_dst
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.instl_dst;
			}
			return base.instl_dst;
		}
		set
		{
			if (bmsfile == null)
			{
				base.instl_dst = value;
			}
			else
			{
				bmsfile.instl_dst = value;
			}
		}
	}

	public override BMSFileStatus status
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.status;
			}
			return base.status;
		}
		set
		{
			if (bmsfile == null)
			{
				base.status = value;
			}
			else
			{
				bmsfile.status = value;
			}
		}
	}

	public override string tag
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.tag;
			}
			return base.tag;
		}
		set
		{
			if (bmsfile == null)
			{
				base.tag = value;
			}
			else
			{
				bmsfile.tag = value;
			}
		}
	}

	public override string path
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.path;
			}
			return base.path;
		}
		set
		{
			if (bmsfile == null)
			{
				base.path = value;
			}
			else
			{
				bmsfile.path = value;
			}
		}
	}

	public override string stagefile
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.stagefile;
			}
			return base.stagefile;
		}
		protected set
		{
			throw new NotImplementedException();
		}
	}

	public override string banner
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.banner;
			}
			return base.banner;
		}
		protected set
		{
			throw new NotImplementedException();
		}
	}

	public override string backbmp
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.backbmp;
			}
			return base.backbmp;
		}
		protected set
		{
			throw new NotImplementedException();
		}
	}

	public override int? mode
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.mode;
			}
			return base.mode;
		}
		protected set
		{
			throw new NotImplementedException();
		}
	}

	public override List<BMSTable> RefTables
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.RefTables;
			}
			return base.RefTables;
		}
	}

	public override string RefTablesSymbols
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.RefTablesSymbols;
			}
			return base.RefTablesSymbols;
		}
	}

	public override string RefTablesNames
	{
		get
		{
			if (bmsfile != null)
			{
				return bmsfile.RefTablesNames;
			}
			return base.RefTablesNames;
		}
	}

	public override string Level
	{
		get
		{
			string text = entry.level.ToString();
			if (text == null)
			{
				if (bmsfile != null)
				{
					return bmsfile.level.ToString();
				}
				text = base.level.ToString();
			}
			return text;
		}
		set
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				entry.level = null;
			}
			else
			{
				Match match = Regex.Match(value.Trim(), "([+-]?\\d+(\\.\\d*)?|\\.\\d+)");
				if (match.Success)
				{
					entry.level = double.Parse(match.Groups[1].ToString());
				}
			}
			RaisePropertyChanged("Level");
		}
	}

	public override string Folder
	{
		get
		{
			return entry.folder;
		}
		set
		{
			entry.folder = value;
			RaisePropertyChanged("Folder");
		}
	}

	public VirtualBMSFile(BMSTableEntry _entry, BMSFile file = null)
	{
		Interlocked.Increment(ref createdCount);
		entry = _entry;
		bmsfile = (entry.bmsfile = file);
		if (bmsfile != null && bmsfile.hash != null)
		{
			base.maintenanceInfo = bmsfile.maintenanceInfo;
			base.bmsScore = bmsfile.bmsScore;
		}
		registratePropertyChangedEventHandlers();
	}

	private void registratePropertyChangedEventHandlers()
	{
		if (bmsfile == null)
		{
			return;
		}
		DisposeListenerForRealBmsFile();
		listenerForRealBMSFileChanged = OnRealBmsFilePropertyChanged;
		bmsfile.PropertyChanged += listenerForRealBMSFileChanged;
	}

	/// <summary>
	/// 実体譜面の変更を単一ハンドラで仮想行へ転送します。
	/// プレイリスト詳細表示向けに listener 数を抑えつつ、既存表示更新は維持します。
	/// </summary>
	private void OnRealBmsFilePropertyChanged(object sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
		case nameof(BMSFile.tag):
			RaisePropertyChanged(() => tag);
			break;
		case nameof(BMSFile.path):
			RaisePropertyChanged(() => path);
			break;
		case nameof(BMSFile.warning):
			RaisePropertyChanged(() => warning);
			break;
		case nameof(BMSFile.HasZeroNoteMismatchWarning):
			RaisePropertyChanged(() => HasZeroNoteMismatchWarning);
			break;
		case nameof(BMSFile.HasHighlightedWarning):
			RaisePropertyChanged(() => HasHighlightedWarning);
			break;
		case nameof(BMSFile.DisplayWarning):
			RaisePropertyChanged(() => DisplayWarning);
			break;
		case nameof(BMSFile.instl_dst):
			RaisePropertyChanged(() => instl_dst);
			break;
		case nameof(BMSFile.status):
			RaisePropertyChanged(() => status);
			break;
		case nameof(BMSFile.RefTables):
			RaisePropertyChanged(() => RefTables);
			break;
		case nameof(BMSFile.RefTablesSymbols):
			RaisePropertyChanged(() => RefTablesSymbols);
			break;
		case nameof(BMSFile.RefTablesNames):
			RaisePropertyChanged(() => RefTablesNames);
			break;
		case nameof(BMSFile.maintenanceInfo):
			base.maintenanceInfo = bmsfile.maintenanceInfo;
			break;
		case nameof(BMSFile.bmsScore):
			base.bmsScore = bmsfile.bmsScore;
			break;
		}
	}

	private void DisposeListenerForRealBmsFile()
	{
		if (bmsfile != null && listenerForRealBMSFileChanged != null)
		{
			bmsfile.PropertyChanged -= listenerForRealBMSFileChanged;
		}
		listenerForRealBMSFileChanged = null;
	}

	public BMSFile GetNonVirtualBMSFile()
	{
		if (entry != null && bmsfile != null)
		{
			return bmsfile;
		}
		return null;
	}

	public BMSTableEntry ToBMSTableEntry()
	{
		return entry;
	}

	public void Dispose()
	{
		if (!isDisposed)
		{
			isDisposed = true;
			DisposeListenerForRealBmsFile();
			Interlocked.Increment(ref disposedCount);
			GC.SuppressFinalize(this);
		}
	}

	/// <summary>
	/// 診断ログとテスト向けに仮想行のライフサイクル統計を返します。
	/// </summary>
	/// <returns>生成数、破棄数、生存数のスナップショット。</returns>
	internal static (long created, long disposed, long alive) GetLifecycleStats()
	{
		long created = Interlocked.Read(ref createdCount);
		long disposed = Interlocked.Read(ref disposedCount);
		return (created, disposed, created - disposed);
	}

	/// <summary>
	/// テスト向けに仮想行のライフサイクル統計を初期化します。
	/// </summary>
	internal static void ResetLifecycleStatsForTests()
	{
		Interlocked.Exchange(ref createdCount, 0L);
		Interlocked.Exchange(ref disposedCount, 0L);
	}
}
