using System;
using System.IO;
using System.Text.RegularExpressions;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

public class LR2SongDB : SQLiteConnectionEx
{
	[Table("song")]
	public class song : SQLiteTable<song>
	{
		protected string _hash;

		protected string _title;

		protected string _subtitle;

		protected string _artist;

		protected string _subartist;

		private string _genre;

		protected string _tag;

		protected string _path;

		protected string _stagefile;

		protected string _banner;

		protected string _backbmp;

		protected int? _judge;

		protected int? _karinotes;

		public virtual string hash
		{
			get
			{
				return _hash;
			}
			protected set
			{
				if (value != null && md5HashRegex.IsMatch(value))
				{
					if (!(_hash == value))
					{
						_hash = value.ToLowerInvariant();
					}
				}
				else
				{
					_hash = null;
				}
			}
		}

		public virtual string title
		{
			get
			{
				return _title ?? "";
			}
			protected set
			{
				if (!(_title == value))
				{
					_title = value;
				}
			}
		}

		public virtual string subtitle
		{
			get
			{
				return _subtitle ?? "";
			}
			protected set
			{
				if (!(_subtitle == value))
				{
					_subtitle = value;
				}
			}
		}

		public virtual string artist
		{
			get
			{
				return _artist ?? "";
			}
			protected set
			{
				if (!(_artist == value))
				{
					_artist = value;
				}
			}
		}

		public virtual string subartist
		{
			get
			{
				return _subartist ?? "";
			}
			protected set
			{
				if (!(_subartist == value))
				{
					_subartist = value;
				}
			}
		}

		public virtual string genre
		{
			get
			{
				return _genre ?? "";
			}
			protected set
			{
				if (!(_genre == value))
				{
					_genre = value;
					RaisePropertyChanged("genre");
				}
			}
		}

		public virtual string tag
		{
			get
			{
				return _tag ?? "";
			}
			set
			{
				if (!(_tag == value))
				{
					_tag = value;
					RaisePropertyChanged("tag");
				}
			}
		}

		[PrimaryKey]
		public virtual string path
		{
			get
			{
				return _path ?? "";
			}
			set
			{
				if (!(_path == value))
				{
					_path = value;
					RaisePropertyChanged("path");
				}
			}
		}

		public int? type { get; set; }

		public string folder { get; set; }

		public virtual string stagefile
		{
			get
			{
				return _stagefile ?? "";
			}
			protected set
			{
				if (!(_stagefile == value))
				{
					_stagefile = value;
				}
			}
		}

		public virtual string banner
		{
			get
			{
				return _banner ?? "";
			}
			protected set
			{
				if (!(_banner == value))
				{
					_banner = value;
				}
			}
		}

		public virtual string backbmp
		{
			get
			{
				return _backbmp ?? "";
			}
			protected set
			{
				if (!(_backbmp == value))
				{
					_backbmp = value;
				}
			}
		}

		public string parent { get; set; }

		public int? level { get; set; }

		public int? difficulty { get; set; }

		public int? maxbpm { get; protected set; }

		public int? minbpm { get; protected set; }

		public virtual int? mode { get; protected set; }

		public int? judge
		{
			get
			{
				return _judge;
			}
			protected set
			{
				if (_judge != value)
				{
					_judge = value;
				}
			}
		}

		public int? longnote { get; protected set; }

		public int? bga { get; protected set; }

		public int? random { get; protected set; }

		public int? date { get; set; }

		public int? favorite { get; protected set; }

		public int? txt { get; protected set; }

		public int? karinotes
		{
			get
			{
				return _karinotes;
			}
			protected set
			{
				if (_karinotes != value)
				{
					_karinotes = value;
				}
			}
		}

		public int? adddate { get; set; }

		public int? exlevel { get; set; }
	}

	[Table("folder")]
	public class folder : SQLiteTable<folder>
	{
		public string title { get; set; }

		public string subtitle { get; set; }

		public string category { get; set; }

		public string info_a { get; set; }

		public string info_b { get; set; }

		public string command { get; set; }

		[PrimaryKey]
		public string path { get; set; }

		public int? type { get; set; }

		public string banner { get; set; }

		public string parent { get; set; }

		public int? date { get; set; }

		public int? max { get; set; }

		public int? adddate { get; set; }
	}

	[Table("expert")]
	public class expert : SQLiteTable<expert>
	{
		[PrimaryKey]
		public int? id { get; set; }

		public string title { get; set; }

		public int? line { get; set; }

		public string hash { get; set; }

		public int? ir { get; set; }
	}

	[Table("grade")]
	public class grade : expert
	{
	}

	[Table("nonstop")]
	public class nonstop : expert
	{
	}

	public enum JusgeRankType
	{
		VERY_HARD,
		HARD,
		NORMAL,
		EASY
	}

	private string _dbPath;

	public static Regex md5HashRegex = new Regex("^[a-fA-F0-9]{32}$", RegexOptions.Compiled);

	public string DBPath
	{
		get
		{
			return _dbPath;
		}
		private set
		{
			if (_dbPath == value)
			{
				return;
			}
			if (File.Exists(value) && Path.GetFileName(value).ToLower() == "song.db")
			{
				_dbPath = value;
				string directoryName = Path.GetDirectoryName(_dbPath);
				if (string.Equals(Path.GetFileName(directoryName), "Database", StringComparison.OrdinalIgnoreCase))
				{
					string directoryName2 = Path.GetDirectoryName(directoryName);
					if (string.Equals(Path.GetFileName(directoryName2), "LR2files", StringComparison.OrdinalIgnoreCase))
					{
						LR2RootPath = Path.GetDirectoryName(directoryName2);
					}
				}
				return;
			}
			Close();
			throw new FileNotFoundException("ファイルが見つからないか、song.db ではありません。", value);
		}
	}

	public string LR2RootPath { get; private set; }

	public LR2SongDB(string dbPath)
		: base(dbPath)
	{
		DBPath = dbPath;
	}
}
