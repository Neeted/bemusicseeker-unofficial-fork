using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Codeplex.Data;
using Livet;
using Livet.EventListeners;
using Microsoft.VisualBasic.FileIO;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;
using Sgml;

namespace BeMusicSeeker.Models;

public class BMSPlaylist : NotificationObject
{
	private enum estimationTableType
	{
		easy,
		normal,
		hard,
		fc
	}

	private string lr2SongDBPath;

	private string lr2ScoreDBPath;

	private Func<LR2Config> lr2config;

	private Func<List<BMSScore>> bmsScores;

	private SemaphoreSlim initSemaphore;

	private ReaderWriterLockSlimWrapper rwlockBMSTablesInitializeAll = new ReaderWriterLockSlimWrapper();

	private ReaderWriterLockSlimWrapper rwlockBMSTablesInitializeMin = new ReaderWriterLockSlimWrapper();

	private ReaderWriterLockSlimWrapper rwlockBMSTables = new ReaderWriterLockSlimWrapper();

	private PropertyChangedEventListener listenerForRwlockBMSTablesInitializedAll;

	private PropertyChangedEventListener listenerForRwlockBMSTablesInitializedMin;

	private PropertyChangedEventListener listenerForRwlockBMSTables;

	private DispatcherCollection<BMSTable> _BMSTables = new DispatcherCollection<BMSTable>(DispatcherHelper.UIDispatcher);

	private static Uri estimationJsonUri = new Uri("http://walkure.net/hakkyou/data/bms.json", UriKind.Absolute);

	private static string recommendJsonUriStr = "http://walkure.net/hakkyou/recommended_json.cgi?id=";

	private static Uri walkureUpdateUri = new Uri("http://walkure.net/hakkyou/mle.cgi", UriKind.Absolute);

	private static Uri insaneUri = new Uri("http://www.ribbit.xyz/bms/tables/insane.html");

	private static Uri overjoyUri = new Uri("http://www.ribbit.xyz/bms/tables/overjoy.html");

	private object insaneTableLock = new object();

	private BMSTable _insaneTable;

	private object overjoyTableLock = new object();

	private BMSTable _overjoyTable;

	private static readonly Dictionary<int, string> insaneGrade = new Dictionary<int, string>
	{
		{ 4934, "0000000000200000000000000000519096a1536917e1f7f12a85d3dd7eb64932c65d0badebb7738e350022a59a1f0637c5605fc262eb9023b82c14d14cc837f8c46a81cb184f5a804c119930d6eba748" },
		{ 4935, "00000000002000000000000000005190e6af04686aeaf0a0fa2698cb0b0111ad5dc1e3e22e4fc735f010e35f2ae77480093a8d66b89944db9e42aa2321b4f63739d0732ef7fee9ad0c8b044ccbe8a396" },
		{ 4936, "00000000002000000000000000005190323e391cb09c023da7fe439d12bc6defc67ba013af164f2cdec7c8c98c90d8f53c4e5a90478a6a60b430e9816c942ffda8d67366d1e603ff3000af761aef0e53" },
		{ 4937, "0000000000200000000000000000519096641e3e89ca6c61b1882ebf04ab4ad179dc444b48299814462ff23a2bf3ab79732abafcb87ac588b64a9be39c6deb5c2e1cc5e6bb96bff8a92f2defce49e70a" },
		{ 4938, "00000000002000000000000000005190556ed0c159434dd1e7a252086482cfd2cd24fd54b865c744428da2513643dd8d17ee7a35750c8e1b1bdfe3a7ca3a311963947bce9af12ca7f84492b20ca21928" },
		{ 4939, "000000000020000000000000000051906de6909c19156221d729b5e965c5cc2adab5146a185e036cb1514b71edf1032a755c4087937b7223c31cd17cedfce7888ae06bf384dbe6956b1fc76c071ff86f" },
		{ 4940, "00000000002000000000000000005190bcf7607db2955c8979b54a0981b5eeb6fd493e9ff008dc10636a55dc4f2a081439f78361dc62f7ac355fb73e628bb336bcb640aa649d12c1ba16cd70f94ff0f8" },
		{ 4941, "00000000002000000000000000005190683340fac1d376bea11c0848531ed6dd1f5cf9788ce30603c5ffe261f55d6c3d462c47942b654985da2e94c6e31a238b18a3d4f7f8b1d35277e759e5643a4757" },
		{ 4942, "00000000002000000000000000005190cd78da88201c01afb934bcba55f955cbadec1bcc37806f155cdae08d873c9f770ab22f8cc36b18e6cce19f5f0906d71eee21ce9f5b84794c69e64e6d6d27caed" },
		{ 4943, "000000000020000000000000000051901a1ca14aededce99bd136da649fbec7ac0d7baaaeb1b0ba9b17b94b587d2fb7067bc996815b98296a44fe0f802a6115da6738157b2dc1689bea1f6123660d723" },
		{ 4944, "00000000002000000000000000005190032b0582871a07c604d8cba171f9579715e44dfea14cb6812f7fcb56a3a0c409151c0d2bb94ab56dfb753410ae70598e7300591ce85f888fbefe6984dc4938c3" },
		{ 4945, "00000000002000000000000000005190c07125de4ed7fbe7cb066cc41e50e51efc7d46e7bbc9f6afd26d05e3bf2ef555b3887714270e28988ce900e4b9300994d1877ad5dc0134b27eb0238da5721eed" },
		{ 11099, "00000000002000000000000000005190cfad3baadce9e02c45021963453d7c9477d23be22b2370925c573d922276bce0188a99f74ab71804f2e360dcf484545cc46a81cb184f5a804c119930d6eba748" },
		{ 11100, "0000000000200000000000000000519010420967a85371e65db57967e6c696cdebc5946c3de24a01048d1c90cbd5c9d645446f6c96bdb081a2054b80cd8f720839d0732ef7fee9ad0c8b044ccbe8a396" },
		{ 11101, "00000000002000000000000000005190d6fea1389a86fc14eb354fcc5e60e03ee58e3f718d089ec37caa0f2c7149a54b1e049d189ef9bef79824f55764517a58fb975909eb0f60a9190c7a1007515d4b" },
		{ 11102, "00000000002000000000000000005190603aae2c3681cc2562b9b53b50408c6979dc444b48299814462ff23a2bf3ab79ff5d7235ba643bc85b804a3df2778590451ed38cdba0323388027129f5929645" },
		{ 11103, "00000000002000000000000000005190f0feb9654ae044c0e95ebf68e2497c2e51e0a29d5c7fc9cdf5e5e8eee1ad11241cf2e138abd7199d6e4656447cfaed6c8e0df85dbf59c4753346f1582e2d4422" },
		{ 11104, "00000000002000000000000000005190a4b59ca055a856b9a200ccfd1bf5c7bca0f48e96286e0ce275a33fd8cb851ff0c4134c29746e1ece168159ba4ae88d4567a76e7d26f3c3ea99fdc6f912f83021" },
		{ 11105, "00000000002000000000000000005190f96cf3af3d9356407bff483d27f4aecca29f9500cebd16cbb8d14b69ace79db1871c6aed53c5dd3ac35686d9a92af4f440673cc8ea628a9452f6ae9ac74d5817" },
		{ 11106, "00000000002000000000000000005190e9fc0fbbde68bfd10f2d289751a1fc6a3a500560e6ace69e08b51736ff1be0ca8a8019d432ab4dd9bde4e65faf1b663f7fd4d1b5a767fd97343129157782a95f" },
		{ 11107, "0000000000200000000000000000519047de06762f16d164426e3197c5de967ca718de9a2cd2f88deafbd40f5929abb45077cd14b4759d0e8d61e854eac4e88ac87682922d1b87622b34fe7a7ffba52c" },
		{ 11108, "0000000000200000000000000000519019fbf62d711282d81208f97f7135e2a23791d42c00cdfa135df3779eb0f78505b46b23fa8e2ad96e02763f687bb5a494efae90d527a71d753ea7ad2847ec2006" },
		{ 11109, "00000000002000000000000000005190d70e2549e841819708d279d2478bb1e25e3711f2f576f3929156c9594b87efaca56ddb2169246817fb7527363fb9c687cefb4735c26f9488438021b4d7fdca31" },
		{ 11110, "00000000002000000000000000005190f872dd65dd08638b06d80470a3233fb91b72e8f6439a698e78f94be16470b7892371263af3b0d644fba62526c9f494818a8a6c2f3511eb0876a6c9a2027d7bbe" }
	};

	private object estimationTableLock = new object();

	private List<BMSTableEntry> _easyEntries;

	private List<BMSTableEntry> _normalEntries;

	private List<BMSTableEntry> _hardEntries;

	private List<BMSTableEntry> _fcEntries;

	private Regex workAroundRegex = new Regex("\"(?<id>\\d+)\":{", RegexOptions.Compiled);

	public DispatcherCollection<BMSTable> BMSTables
	{
		get
		{
			return _BMSTables;
		}
		set
		{
			if (_BMSTables != value)
			{
				_BMSTables = value;
				RaisePropertyChanged("BMSTables");
			}
		}
	}

	public bool IsWriteLockHeldBMSTablesInitializeAll
	{
		get
		{
			if (BMSTables != null && rwlockBMSTablesInitializeAll.LockingWriteCount == 0)
			{
				return rwlockBMSTablesInitializeAll.WaitingWriteCount > 0;
			}
			return true;
		}
	}

	public bool IsWriteLockHeldBMSTablesInitializeMin
	{
		get
		{
			if (BMSTables != null && rwlockBMSTablesInitializeMin.LockingWriteCount == 0)
			{
				return rwlockBMSTablesInitializeMin.WaitingWriteCount > 0;
			}
			return true;
		}
	}

	public bool IsWriteLockHeldBMSTables
	{
		get
		{
			if (rwlockBMSTables.LockingWriteCount == 0)
			{
				return rwlockBMSTables.WaitingWriteCount > 0;
			}
			return true;
		}
	}

	public bool IsWriteLockHeldAnyBMSTable
	{
		get
		{
			if (BMSTables != null)
			{
				return BMSTables.Any((BMSTable t) => t.ReaderWriterLock.LockingWriteCount != 0 || t.ReaderWriterLock.WaitingWriteCount > 0);
			}
			return true;
		}
	}

	private BMSTable insaneTable
	{
		get
		{
			lock (insaneTableLock)
			{
				if (_insaneTable == null)
				{
					try
					{
						_insaneTable = LoadExternalTable(insaneUri);
					}
					catch
					{
						_insaneTable = null;
					}
				}
			}
			return _insaneTable;
		}
	}

	private BMSTable overjoyTable
	{
		get
		{
			lock (overjoyTableLock)
			{
				if (_overjoyTable == null)
				{
					try
					{
						_overjoyTable = LoadExternalTable(overjoyUri);
					}
					catch
					{
						_overjoyTable = null;
					}
				}
			}
			return _overjoyTable;
		}
	}

	private List<BMSTableEntry> easyEntries
	{
		get
		{
			lock (estimationTableLock)
			{
				if (_easyEntries == null)
				{
					setEstimationTable();
				}
			}
			return _easyEntries;
		}
	}

	private List<BMSTableEntry> normalEntries
	{
		get
		{
			lock (estimationTableLock)
			{
				if (_normalEntries == null)
				{
					setEstimationTable();
				}
			}
			return _normalEntries;
		}
	}

	private List<BMSTableEntry> hardEntries
	{
		get
		{
			lock (estimationTableLock)
			{
				if (_hardEntries == null)
				{
					setEstimationTable();
				}
			}
			return _hardEntries;
		}
	}

	private List<BMSTableEntry> fcEntries
	{
		get
		{
			lock (estimationTableLock)
			{
				if (_fcEntries == null)
				{
					setEstimationTable();
				}
			}
			return _fcEntries;
		}
	}

	public void AcquireWriterLockBMSTables()
	{
		rwlockBMSTables.EnterWriteLock();
	}

	public void FreeWriterLockBMSTables()
	{
		rwlockBMSTables.ExitWriteLock();
	}

	public void AcquireReaderLockBMSTables()
	{
		rwlockBMSTables.EnterReadLock();
	}

	public void FreeReaderLockBMSTables()
	{
		rwlockBMSTables.ExitReadLock();
	}

	public BMSPlaylist(string _lr2SongDB, Func<LR2Config> getLR2Config = null, string _lr2ScoreDB = null, Func<List<BMSScore>> getBMSScores = null)
	{
		if (_lr2SongDB == null)
		{
			throw new ArgumentNullException("_LR2SongDB");
		}
		if (!File.Exists(_lr2SongDB))
		{
			throw new ArgumentException("LR2 song DB が見つかりませんでした。パス: " + _lr2SongDB, "_LR2SongDB");
		}
		if (_lr2ScoreDB != null && !File.Exists(_lr2ScoreDB))
		{
			throw new ArgumentException("LR2 score DB が見つかりませんでした。パス: " + _lr2ScoreDB, "_lr2ScoreDB");
		}
		lr2SongDBPath = _lr2SongDB;
		lr2ScoreDBPath = _lr2ScoreDB;
		lr2config = ((getLR2Config != null) ? getLR2Config : ((Func<LR2Config>)(() => (LR2Config)null)));
		bmsScores = ((getBMSScores != null) ? getBMSScores : ((Func<List<BMSScore>>)(() => (List<BMSScore>)null)));
		using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
		{
			lR2SongDBExtended.CreateTable<LR2SongDBExtended.playlist>();
			lR2SongDBExtended.CreateTable<LR2SongDBExtended.playlist_entry>();
			lR2SongDBExtended.Execute("DROP INDEX IF EXISTS 'playlist_entry_idx';");
			lR2SongDBExtended.CreateIndex("playlist_entry_idx_uniq", SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName(), new string[6]
			{
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.lr2_bmsid),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.title),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
			}, unique: true);
			lR2SongDBExtended.CreateIndex("playlist_entry_idx_id", SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName(), new string[2]
			{
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
			});
			lR2SongDBExtended.CreateIndex("playlist_entry_idx_folder", SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName(), new string[3]
			{
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
			});
			lR2SongDBExtended.CreateIndex("playlist_entry_idx_title", SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName(), new string[3]
			{
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.title),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
			});
			lR2SongDBExtended.CreateIndex("playlist_entry_idx_md5", SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName(), new string[3]
			{
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
			});
			lR2SongDBExtended.CreateIndex("playlist_entry_idx_level", SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName(), new string[3]
			{
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.level),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
			});
			lR2SongDBExtended.CreateIndex("playlist_entry_idx_adddate", SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName(), new string[3]
			{
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.adddate),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
				SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
			});
		}
		listenerForRwlockBMSTablesInitializedAll = new PropertyChangedEventListener(rwlockBMSTablesInitializeAll);
		listenerForRwlockBMSTablesInitializedMin = new PropertyChangedEventListener(rwlockBMSTablesInitializeMin);
		listenerForRwlockBMSTables = new PropertyChangedEventListener(rwlockBMSTables);
		listenerForRwlockBMSTablesInitializedAll.RegisterHandler(() => rwlockBMSTablesInitializeAll.LockingWriteCount, delegate
		{
			RaisePropertyChanged(() => IsWriteLockHeldBMSTablesInitializeAll);
		});
		listenerForRwlockBMSTablesInitializedMin.RegisterHandler(() => rwlockBMSTablesInitializeMin.LockingWriteCount, delegate
		{
			RaisePropertyChanged(() => IsWriteLockHeldBMSTablesInitializeMin);
		});
		listenerForRwlockBMSTables.RegisterHandler(() => rwlockBMSTables.LockingWriteCount, delegate
		{
			RaisePropertyChanged(() => IsWriteLockHeldBMSTables);
		});
	}

	public void Initialize(bool reloadExtPlaylist = true, Action<BMSTable, bool, BMSTable> updateCallbackAction = null, SemaphoreSlim semaphore = null)
	{
		if (semaphore != null)
		{
			initSemaphore = semaphore;
		}
		using (rwlockBMSTablesInitializeAll.GetWriterGuard())
		{
			using (rwlockBMSTables.GetWriterGuard())
			{
				using (rwlockBMSTablesInitializeMin.GetWriterGuard())
				{
					if (BMSTables.Count == 0)
					{
						List<BMSTable> list;
						List<BMSTableEntry> source;
						using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath))
						{
							list = (from t in lR2SongDBExtended.Table<BMSTable>()
								orderby t.name
								select t).ToList();
							source = lR2SongDBExtended.Table<BMSTableEntry>().ToList();
						}
						foreach (BMSTable table in list)
						{
							table.entries = source.Where((BMSTableEntry e) => e.playlist_id == table.playlist_id).ToList();
						}
						BMSTables.AddRange(list);
					}
				}
			}
			if (initSemaphore != null)
			{
				initSemaphore.Release();
			}
			object folderoutLock = new object();
			Action<BMSTable, bool, BMSTable> item = delegate(BMSTable bMSTable, bool updated, BMSTable oldtable)
			{
				if (Settings.Default.OperationModeLR2DB)
				{
					using (bMSTable.ReaderWriterLock.GetWriterGuard())
					{
						if (!string.IsNullOrWhiteSpace(bMSTable.Output_dir))
						{
							string customFolderOutputDirectory = GetCustomFolderOutputDirectory(bMSTable);
							if (((App)Application.Current).forceReinitializationCustomFolders || updated || !Directory.Exists(customFolderOutputDirectory) || Directory.EnumerateFiles(customFolderOutputDirectory, "*.lr2folder", System.IO.SearchOption.TopDirectoryOnly).Count() == 0)
							{
								lock (folderoutLock)
								{
									string customFolderOutputDirectory2 = GetCustomFolderOutputDirectory(oldtable);
									if (customFolderOutputDirectory != customFolderOutputDirectory2)
									{
										removeCustomFolder(customFolderOutputDirectory2);
									}
									removeCustomFolder(customFolderOutputDirectory);
									createCustomFolder(bMSTable, customFolderOutputDirectory);
									return;
								}
							}
						}
					}
				}
			};
			UpdateBMSTables(reloadExtPlaylist, new List<Action<BMSTable, bool, BMSTable>> { item, updateCallbackAction });
			using (rwlockBMSTables.GetReaderGuard())
			{
				if (Settings.Default.OperationModeLR2DB)
				{
					IEnumerable<string> second = from t in BMSTables
						where t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)
						select Path.Combine(Settings.Default.LR2CustomFolderOutputBaseDirRootType, t.Output_dir);
					List<string> bMSSearchDirectories = lr2config().GetBMSSearchDirectories();
					lr2config().SetBMSSearchDirectories(bMSSearchDirectories.Union(second).Distinct(StringComparer.OrdinalIgnoreCase));
					lr2config().Save();
				}
			}
		}
		initSemaphore = null;
	}

	public BMSTable LoadWalkureTable(Uri pageUri, BMSTable baseTable = null)
	{
		if (!pageUri.IsAbsoluteUri || pageUri.Scheme != "bmseeker")
		{
			throw new ArgumentException("Schemeはbmseekerである必要があります", "pageUri");
		}
		BMSTable bMSTable = new BMSTable
		{
			Page_url = pageUri
		};
		if (baseTable != null)
		{
			bMSTable.compat_prefix = baseTable.compat_prefix;
			bMSTable.playlist_id = baseTable.playlist_id;
		}
		string absolutePath = pageUri.AbsolutePath;
		if (!(absolutePath == "table.estimation"))
		{
			if (!(absolutePath == "table.recommended"))
			{
				throw new ArgumentException("非対応のURIです", pageUri.ToString());
			}
			string input = Uri.UnescapeDataString(pageUri.Query);
			Regex regex = new Regex("id=(\\d+)");
			Regex regex2 = new Regex("mode=([^&]+)");
			Regex regex3 = new Regex("filter=([^&]+)");
			Regex regex4 = new Regex("name=([^&]+)");
			Regex regex5 = new Regex("base=([^&]+)");
			Match match = regex.Match(input);
			Match match2 = regex2.Match(input);
			Match match3 = regex3.Match(input);
			Match match4 = regex4.Match(input);
			Match match5 = regex5.Match(input);
			int lr2id = 0;
			string mode = null;
			string filter = null;
			string displayName = null;
			string baseline = null;
			if (match.Success)
			{
				lr2id = int.Parse(match.Groups[1].ToString());
			}
			if (match2.Success)
			{
				mode = match2.Groups[1].ToString();
			}
			if (match3.Success)
			{
				filter = match3.Groups[1].ToString();
			}
			if (match4.Success)
			{
				displayName = match4.Groups[1].ToString();
			}
			if (match5.Success)
			{
				baseline = match5.Groups[1].ToString();
			}
			loadRecommendedTable(bMSTable, baseTable, mode, filter, displayName, lr2id, baseline);
		}
		else
		{
			switch (pageUri.Query.TrimStart('?'))
			{
			case "type=easy":
				loadEstimationTable(bMSTable, estimationTableType.easy);
				break;
			case "type=normal":
				loadEstimationTable(bMSTable, estimationTableType.normal);
				break;
			case "type=hard":
				loadEstimationTable(bMSTable, estimationTableType.hard);
				break;
			case "type=fc":
				loadEstimationTable(bMSTable, estimationTableType.fc);
				break;
			default:
				throw new ArgumentException("非対応のURIです", pageUri.ToString());
			}
		}
		if (baseTable != null)
		{
			bMSTable.symbol = baseTable.symbol;
			bMSTable.ignore_folder_output = baseTable.ignore_folder_output;
			bMSTable.is_external_sync = baseTable.is_external_sync;
			bMSTable.is_root_folder = baseTable.is_root_folder;
		}
		return bMSTable;
	}

	private void setEstimationTable()
	{
		string input = new GZipWebClient
		{
			Encoding = Encoding.UTF8
		}.DownloadString(estimationJsonUri);
		input = workAroundRegex.Replace(input, "\"key${id}\":{");
		dynamic data_json = DynamicJson.Parse(input);
		BMSTable insane = insaneTable;
		if (insane == null)
		{
			throw new InvalidOperationException("Load insane table failed");
		}
		BMSTable bMSTable = overjoyTable;
		if (bMSTable == null)
		{
			throw new InvalidOperationException("Load overjoy table failed");
		}
		var inner = (from e in ((IEnumerable<string>)data_json.GetDynamicMemberNames()).Select(delegate(string item)
			{
				try
				{
					return new
					{
						type = (string)data_json[item].type,
						bmsid = ((data_json[item].IsDefined("bmsid")) ? ((int)data_json[item].bmsid).ToString() : null),
						hoshi = ((data_json[item].IsDefined("bmsid")) ? new
						{
							easy = (double?)data_json[item].hoshi.easy,
							normal = (double?)data_json[item].hoshi.normal,
							hard = (double?)data_json[item].hoshi.hard,
							fc = (double?)data_json[item].hoshi.fc
						} : null)
					};
				}
				catch
				{
					return (_003C_003Ef__AnonymousType3<string, string, _003C_003Ef__AnonymousType4<double?, double?, double?, double?>>)null;
				}
			})
			where e != null && e.type != "course"
			select e).ToList();
		var source = insane.entries.Concat(bMSTable.entries).GroupJoin(inner, (BMSTableEntry e) => e.lr2_bmsid, w => w.bmsid, (BMSTableEntry e, w) =>
		{
			BMSTableEntry bMSTableEntry = e.Duplicate();
			bMSTableEntry.folder = ((e.parent == insane) ? "INSANE " : "Overjoy ") + e.parent.symbol + e.parent.ConvertBackFolderNameToCompatibleLevelName(e.folder);
			return new
			{
				entry = bMSTableEntry,
				estimation = w.DefaultIfEmpty()
			};
		}).ToList();
		_easyEntries = source.SelectMany(grp => grp.estimation, (grp, a) =>
		{
			BMSTableEntry bMSTableEntry = grp.entry.Duplicate();
			if (a != null)
			{
				bMSTableEntry.level = a.hoshi.easy;
			}
			else
			{
				bMSTableEntry.level = null;
			}
			return bMSTableEntry;
		}).ToList();
		_normalEntries = source.SelectMany(grp => grp.estimation, (grp, a) =>
		{
			BMSTableEntry bMSTableEntry = grp.entry.Duplicate();
			if (a != null)
			{
				bMSTableEntry.level = a.hoshi.normal;
			}
			else
			{
				bMSTableEntry.level = null;
			}
			return bMSTableEntry;
		}).ToList();
		_hardEntries = source.SelectMany(grp => grp.estimation, (grp, a) =>
		{
			BMSTableEntry bMSTableEntry = grp.entry.Duplicate();
			if (a != null)
			{
				bMSTableEntry.level = a.hoshi.hard;
			}
			else
			{
				bMSTableEntry.level = null;
			}
			return bMSTableEntry;
		}).ToList();
		_fcEntries = source.SelectMany(grp => grp.estimation, (grp, a) =>
		{
			BMSTableEntry bMSTableEntry = grp.entry.Duplicate();
			if (a != null)
			{
				bMSTableEntry.level = a.hoshi.fc;
			}
			else
			{
				bMSTableEntry.level = null;
			}
			return bMSTableEntry;
		}).ToList();
	}

	private void loadEstimationTable(BMSTable table, estimationTableType type)
	{
		table.folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL;
		table.folder_sort_ascending = true;
		table.is_external_sync = true;
		table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder;
		switch (type)
		{
		case estimationTableType.easy:
		{
			table.entries = easyEntries;
			string name = (table.org_name = "発狂BMS難度推定表 EASY");
			table.name = name;
			name = (table.org_symbol = "E★");
			table.symbol = name;
			break;
		}
		case estimationTableType.normal:
		{
			table.entries = normalEntries;
			string name = (table.org_name = "発狂BMS難度推定表 NORMAL");
			table.name = name;
			name = (table.org_symbol = "N★");
			table.symbol = name;
			break;
		}
		case estimationTableType.hard:
		{
			table.entries = hardEntries;
			string name = (table.org_name = "発狂BMS難度推定表 HARD");
			table.name = name;
			name = (table.org_symbol = "H★");
			table.symbol = name;
			break;
		}
		case estimationTableType.fc:
		{
			table.entries = fcEntries;
			string name = (table.org_name = "発狂BMS難度推定表 FC");
			table.name = name;
			name = (table.org_symbol = "F★");
			table.symbol = name;
			break;
		}
		default:
			throw new ArgumentException("非対応のtypeです " + type, "type");
		}
	}

	private void loadRecommendedTable(BMSTable table, BMSTable baseTable = null, string mode = null, string filter = null, string displayName = null, int lr2id = 0, string baseline = null)
	{
		string name = string.Empty;
		if (lr2id == 0)
		{
			if (lr2ScoreDBPath == null)
			{
				throw new InvalidOperationException("スコアDB接続に失敗しました");
			}
			try
			{
				using LR2ScoreDB lR2ScoreDB = new LR2ScoreDBExtended(lr2ScoreDBPath);
				LR2ScoreDB.player player = lR2ScoreDB.Table<LR2ScoreDB.player>().ToList().FirstOrDefault();
				lr2id = player.irid.Value;
				name = player.name;
			}
			catch (Exception)
			{
				lr2id = 0;
			}
		}
		else
		{
			mode = "readonly";
		}
		if (lr2id == 0)
		{
			throw new InvalidOperationException("LR2IDの取得またはスコアDB接続に失敗しました");
		}
		if (mode != "readonly")
		{
			try
			{
				RetryHelper.RetryIfError(delegate
				{
					updatedClearedSongs(mode, lr2id, name, filter, baseline);
				}, delegate(Exception source)
				{
					ExceptionDispatchInfo.Capture(source).Throw();
				}, delegate
				{
					Thread.Sleep(100);
				}, 5u);
			}
			catch (Exception ex2)
			{
				DispatcherMessageBox.Show("リコメンドの更新に失敗しました" + Environment.NewLine + ex2.Message, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
			}
		}
		Uri address = new Uri(recommendJsonUriStr + lr2id, UriKind.Absolute);
		dynamic val = DynamicJson.Parse(new GZipWebClient
		{
			Encoding = Encoding.UTF8
		}.DownloadString(address));
		if ((string)val.status != "success")
		{
			DispatcherMessageBox.Show("リコメンドの取得に失敗しました" + Environment.NewLine + (string)val.message, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
			throw new InvalidOperationException("リコメンドの取得に失敗しました");
		}
		double num = (double)val.hoshi;
		DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((int)val.last_modified).ToLocalTime();
		name = ((string)val.name).Replace('〜', '～');
		BMSTable insane = insaneTable;
		if (insane == null)
		{
			throw new InvalidOperationException("Load insane table failed");
		}
		BMSTable bMSTable = overjoyTable;
		if (bMSTable == null)
		{
			throw new InvalidOperationException("Load overjoy table failed");
		}
		IEnumerable<BMSTableEntry> inner = from e in insane.entries.Concat(bMSTable.entries)
			where !string.IsNullOrWhiteSpace(e.md5) && !string.IsNullOrWhiteSpace(e.lr2_bmsid)
			group e by e.md5 into e
			select e.FirstOrDefault((BMSTableEntry f) => f.parent == insane) ?? e.First();
		List<BMSTableEntry> entries = (from e in ((object[])val.recommended).Select(delegate(dynamic item)
			{
				try
				{
					return new
					{
						type = (string)item.bms.type,
						bmsid = ((item.bms.IsDefined("bmsid")) ? ((int)item.bms.bmsid).ToString() : null),
						new_lamp = (string)item.new_lamp,
						percent = (double?)item.p
					};
				}
				catch
				{
					return (_003C_003Ef__AnonymousType6<string, string, string, double?>)null;
				}
			})
			where e != null && e.type != "course"
			select e).ToList().Join(inner, r => r.bmsid, (BMSTableEntry e) => e.lr2_bmsid, (r, BMSTableEntry e) =>
		{
			BMSTableEntry bMSTableEntry = e.Duplicate();
			bMSTableEntry.folder = r.new_lamp.ToUpperInvariant();
			bMSTableEntry.level = r.percent;
			return bMSTableEntry;
		}).ToList();
		table.folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL;
		table.folder_sort_ascending = false;
		table.is_external_sync = true;
		table.entries = entries;
		table.last_update = DateTime.Parse(dateTime.ToString());
		table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.LevelFolder | LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder;
		table.Folder_order = new List<string> { "EASY", "NORMAL", "HARD", "FC" };
		string name2 = (table.org_name = "リコメンド " + (displayName ?? name) + " ★" + num.ToString("F2"));
		table.name = name2;
		name2 = (table.org_symbol = "R★");
		table.symbol = name2;
		try
		{
			if (baseTable == null || !Settings.Default.ShowRecommUpdatedMsg)
			{
				return;
			}
			Match match = new Regex("★(\\d+(?:\\.\\d+)?)").Match(baseTable.org_name);
			if (match.Success)
			{
				double num2 = double.Parse(match.Groups[1].ToString());
				if (num2 != num)
				{
					DispatcherMessageBox.Show("あなたの実力: ★" + num.ToString("F2") + (num - num2).ToString(" (+#0.00); (-#0.00);") + Environment.NewLine + "(更新: " + dateTime.ToString() + ")" + Environment.NewLine + Environment.NewLine + "(このメッセージは[Ctrl]+[C]でコピーできます)", "リコメンドが更新されました", MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
				}
			}
		}
		catch
		{
		}
	}

	private void updatedClearedSongs(string mode, int lr2Id, string name, string filter, string baseline)
	{
		if (string.IsNullOrWhiteSpace(mode) || mode == "readonly")
		{
			return;
		}
		BMSTable obj = insaneTable ?? throw new InvalidOperationException("Load insane table failed");
		BMSTable bMSTable = overjoyTable;
		if (bMSTable == null)
		{
			throw new InvalidOperationException("Load overjoy table failed");
		}
		var enumerable = from e in obj.entries.Concat(bMSTable.entries)
			where !string.IsNullOrWhiteSpace(e.md5) && !string.IsNullOrWhiteSpace(e.lr2_bmsid)
			group e by e.md5 into g
			select new
			{
				md5 = g.Key,
				bmsid = g.First().lr2_bmsid
			};
		if (initSemaphore != null)
		{
			initSemaphore.Wait();
		}
		if (initSemaphore != null)
		{
			initSemaphore.Release();
		}
		List<BMSScore> list = bmsScores();
		if (list == null)
		{
			throw new InvalidOperationException("ローカルスコアデータが取得されていません");
		}
		int thresh = filter switch
		{
			"hard" => 4, 
			"clear" => 3, 
			"easy" => 2, 
			_ => 1, 
		};
		var lampsBMS = (from t in enumerable
			join s in list on t.md5 equals s.hash
			select new
			{
				bmsid = t.bmsid,
				lamp = (int)((s.clear == LR2ScoreDB.score.ClearType.PA) ? LR2ScoreDB.score.ClearType.FC : s.clear),
				rank = (int)s.rank
			} into s
			where s.lamp >= thresh && s.lamp <= 5 && s.rank != 0
			select s).ToList();
		var lampsGrade = (from t in insaneGrade
			join s in list on t.Value equals s.hash
			select new
			{
				bmsid = (t.Key + 100000000).ToString(),
				lamp = (int)((s.clear == LR2ScoreDB.score.ClearType.PA) ? LR2ScoreDB.score.ClearType.FC : s.clear),
				rank = (int)s.rank
			} into s
			where s.lamp >= thresh && s.lamp <= 5 && s.rank != 0
			select s).ToList();
		if (baseline == "failed")
		{
			lampsBMS = enumerable.Select(t =>
			{
				var anon = lampsBMS.FirstOrDefault(m => m.bmsid == t.bmsid);
				return new
				{
					bmsid = t.bmsid,
					lamp = (anon?.lamp ?? 1),
					rank = (anon?.rank ?? 0)
				};
			}).ToList();
			lampsGrade = insaneGrade.Select(delegate(KeyValuePair<int, string> t)
			{
				var anon = lampsGrade.FirstOrDefault(h => h.bmsid == (t.Key + 100000000).ToString());
				return new
				{
					bmsid = (t.Key + 100000000).ToString(),
					lamp = (anon?.lamp ?? 1),
					rank = (anon?.rank ?? 0)
				};
			}).ToList();
		}
		IEnumerable<string> values = from e in lampsBMS.Concat(lampsGrade)
			select e.bmsid + "-" + e.lamp;
		new GZipWebClient().UploadValues(data: new NameValueCollection
		{
			{ "name", name },
			{
				"id",
				lr2Id.ToString()
			},
			{
				"data",
				string.Join(",", values)
			}
		}, address: walkureUpdateUri);
	}

	private List<string> makeCustomFolderTextsOtherFolder(BMSTable bmsTable)
	{
		SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.playcount);
		SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.playcount);
		Dictionary<string, LR2SongDBExtended.playlist.CustomFolderSortType> source = new Dictionary<string, LR2SongDBExtended.playlist.CustomFolderSortType>
		{
			{
				"MY BEST",
				LR2SongDBExtended.playlist.CustomFolderSortType.PLAYCOUNT
			},
			{
				"NEW SONGS",
				LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE
			}
		};
		string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
		string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
		string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
		string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
		List<string> list = source.Select((KeyValuePair<string, LR2SongDBExtended.playlist.CustomFolderSortType> f) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "ORDER BY " + makeCustomFolderCmdSort(f.Value, asc: false, bmsTable.playlist_id), bmsTable.name, f.Key, 40)).ToList();
		string tableName = SQLiteTable<LR2SongDBExtended.ir_score>.GetTableName();
		string columnName = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.hash);
		string columnName2 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.clear);
		string columnName3 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.combo);
		string columnName4 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.pg);
		string columnName5 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.gr);
		string columnName6 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.gd);
		string columnName7 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.bd);
		string columnName8 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.pr);
		string columnName9 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.minbp);
		string customFolderText = getCustomFolderText("song.hash in (SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0) AND NOT EXISTS (SELECT " + columnName + " FROM " + tableName + " WHERE song.hash = " + tableName + "." + columnName + " AND score.clear = " + tableName + "." + columnName2 + " AND maxcombo = " + columnName3 + " AND perfect = " + columnName4 + " AND great = " + columnName5 + " AND good = " + columnName6 + " AND bad = " + columnName7 + " AND poor = " + columnName8 + " AND score.minbp = " + tableName + "." + columnName9 + ") AND score.clear IS NOT NULL ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, "UNSENT SONGS");
		list.Add(customFolderText);
		string customFolderText2 = getCustomFolderText("song.hash in (SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 1) ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, "REMOVED SONGS");
		list.Add(customFolderText2);
		return list;
	}

	private List<string> makeCustomFolderTextsCategoryAllFolder(BMSTable bmsTable)
	{
		string columnName = SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song e) => e.longnote);
		string columnName2 = SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song e) => e.judge);
		List<string[]> list = new List<string[]>();
		list.Add(new string[2]
		{
			"ALL LONG NOTES",
			columnName + " = 1"
		});
		list.Add(new string[2]
		{
			"ALL VERY HARD JUDGES",
			columnName2 + " = " + 0
		});
		list.Add(new string[2]
		{
			"ALL HARD JUDGES",
			columnName2 + " = " + 1
		});
		list.Add(new string[2]
		{
			"ALL NORMAL JUDGES",
			columnName2 + " = " + 2
		});
		list.Add(new string[2]
		{
			"ALL EASY JUDGES",
			columnName2 + " >= " + 3
		});
		List<string[]> source = list;
		string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
		string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
		string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
		string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
		return source.Select((string[] a) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "AND (" + a[1] + ")", bmsTable.name, a[0])).ToList();
	}

	private List<string> makeCustomFolderTextsDJLevelFolder(BMSTable bmsTable)
	{
		string columnName = SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.rank);
		List<string[]> list = new List<string[]>();
		list.Add(new string[2]
		{
			"DJ LEVEL AAA",
			columnName + " = " + 8
		});
		list.Add(new string[2]
		{
			"DJ LEVEL AA",
			columnName + " = " + 7
		});
		list.Add(new string[2]
		{
			"DJ LEVEL A",
			columnName + " = " + 6
		});
		list.Add(new string[2]
		{
			"DJ LEVEL B",
			columnName + " = " + 5
		});
		list.Add(new string[2]
		{
			"DJ LEVEL C",
			columnName + " = " + 4
		});
		list.Add(new string[2]
		{
			"DJ LEVEL D",
			columnName + " = " + 3
		});
		list.Add(new string[2]
		{
			"DJ LEVEL E",
			columnName + " = " + 2
		});
		list.Add(new string[2]
		{
			"DJ LEVEL F",
			columnName + " = " + 1
		});
		list.Add(new string[2]
		{
			"NO SCORE",
			columnName + " = " + 0 + " OR " + columnName + " IS NULL "
		});
		List<string[]> source = list;
		string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
		string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
		string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
		string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
		return source.Select((string[] r) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "AND (" + r[1] + ") ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.SCORE, asc: false), bmsTable.name, r[0])).ToList();
	}

	private List<string> makeCustomFolderTextsClearFolder(BMSTable bmsTable)
	{
		string columnName = SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.clear);
		string columnName2 = SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.rank);
		string columnName3 = SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.op_history);
		List<string[]> list = new List<string[]>();
		list.Add(new string[2]
		{
			"PERFECT ATTACK CLEAR",
			columnName + " = " + 5 + " AND " + columnName3 + " & 16 != 0"
		});
		list.Add(new string[2]
		{
			"FULL COMBO CLEAR",
			columnName + " = " + 5
		});
		list.Add(new string[2]
		{
			"HARD CLEAR",
			columnName + " = " + 4
		});
		list.Add(new string[2]
		{
			"CLEAR",
			columnName + " = " + 3
		});
		list.Add(new string[2]
		{
			"EASY CLEAR",
			columnName + " = " + 2 + " AND " + columnName2 + " != " + 0
		});
		list.Add(new string[2]
		{
			"ASSIST CLEAR",
			columnName + " >= " + 2 + " AND " + columnName2 + " = " + 0
		});
		list.Add(new string[2]
		{
			"FAILED",
			columnName + " = " + 1
		});
		list.Add(new string[2]
		{
			"NO PLAY",
			columnName + " IS NULL"
		});
		List<string[]> source = list;
		string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
		string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
		string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
		string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
		return source.Select((string[] c) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "AND (" + c[1] + ") ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.MISS, asc: true), bmsTable.name, c[0])).ToList();
	}

	private List<string> makeCustomFolderTextsAlphabetFolder(BMSTable bmsTable)
	{
		Dictionary<string, char[]> dict = new Dictionary<string, char[]>
		{
			{
				"A.B.C.D.",
				new char[2] { 'A', 'E' }
			},
			{
				"E.F.G.H.",
				new char[2] { 'E', 'I' }
			},
			{
				"I.J.K.L.",
				new char[2] { 'I', 'M' }
			},
			{
				"M.N.O.P.",
				new char[2] { 'M', 'Q' }
			},
			{
				"Q.R.S.T.",
				new char[2] { 'Q', 'U' }
			},
			{
				"U.V.W.X.Y.Z.",
				new char[2] { 'U', '[' }
			}
		};
		string title = "OTHERS";
		char[] array = new char[2] { 'A', '[' };
		string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
		string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
		string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
		string columnNameTitle = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.title);
		string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
		List<string> list = (from t in dict.Keys
			orderby t
			select getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")) " : " ") + "AND UPPER(" + columnNameTitle + ") BETWEEN " + sqlQuote(dict[t][0].ToString()) + " AND " + sqlQuote(dict[t][1].ToString()) + " AND UPPER(" + columnNameTitle + ") != " + sqlQuote(dict[t][1].ToString()) + ((bmsTable.entry_type != LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ") " : " ") + "ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, t)).ToList();
		string command = ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")) " : " ") + "AND (UPPER(" + columnNameTitle + ") NOT BETWEEN " + sqlQuote(array[0].ToString()) + " AND " + sqlQuote(array[1].ToString()) + " OR UPPER(" + columnNameTitle + ") = " + sqlQuote(array[1].ToString()) + ")" + ((bmsTable.entry_type != LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ") " : " ") + "ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true);
		list.Add(getCustomFolderText(command, bmsTable.name, title));
		return list;
	}

	private List<string> makeCustomFolderTextsLevelFolder(BMSTable bmsTable)
	{
		List<int> source = (from e in (from e in bmsTable.entries
				where e.level.HasValue
				select (int)Math.Floor(e.level.Value)).Distinct()
			orderby e
			select e).ToList();
		string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
		string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
		string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
		string columnNameLevel = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.level);
		string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
		List<string> list = source.Select(delegate(int l)
		{
			string text = "CASE WHEN " + columnNameLevel + " >= 0 OR CAST(" + columnNameLevel + " AS INTEGER) = " + columnNameLevel + " THEN CAST(" + columnNameLevel + " AS INTEGER) ELSE CAST(" + columnNameLevel + " - 1.0 AS INTEGER) END";
			return getCustomFolderText("song.hash in (SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + text + " = " + l + " AND " + columnNameIsRemoved + " = 0)ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, asc: true, bmsTable.playlist_id) + "," + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, "LEVEL " + l);
		}).ToList();
		if (bmsTable.entries.Any((BMSTableEntry e) => !e.level.HasValue))
		{
			string command = "song.hash in (SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameLevel + " IS NULL AND " + columnNameIsRemoved + " = 0)ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, asc: true, bmsTable.playlist_id) + "," + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true);
			list.Add(getCustomFolderText(command, bmsTable.name, "LEVEL ???"));
		}
		return list;
	}

	private List<string> makeCustomFolderTextsUserFolder(BMSTable bmsTable)
	{
		List<string> folder_list = bmsTable.folder_list;
		LR2SongDBExtended.playlist.CustomFolderSortType sortType = bmsTable.folder_sort_key;
		bool sortDirAsc = bmsTable.folder_sort_ascending;
		string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
		string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
		string columnNameFolder = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder);
		string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
		string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
		return folder_list.Select((string f) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash   in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameFolder + " = " + sqlQuote(f) + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + ((sortType == LR2SongDBExtended.playlist.CustomFolderSortType.NONE) ? string.Empty : (" ORDER BY " + makeCustomFolderCmdSort(sortType, sortDirAsc, bmsTable.playlist_id, f))), bmsTable.name, string.IsNullOrWhiteSpace(f) ? bmsTable.name : f)).ToList();
	}

	private string makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType ftype, bool asc, int? playlist_id = null, string folder = null)
	{
		if (ftype == LR2SongDBExtended.playlist.CustomFolderSortType.NONE)
		{
			return string.Empty;
		}
		string text = (asc ? "ASC" : "DESC");
		string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
		string columnName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
		string columnName2 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
		switch (ftype)
		{
		case LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL:
		case LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE:
		{
			if (!playlist_id.HasValue)
			{
				throw new ArgumentNullException("playlist_id");
			}
			string columnName3 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
			if (string.IsNullOrWhiteSpace(folder))
			{
				return "(SELECT " + ftype.ToColumnName() + " FROM " + tableName + " WHERE " + columnName2 + " = song.hash AND " + columnName3 + " = " + playlist_id + " AND " + columnName + " = 0) " + text;
			}
			string columnName4 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder);
			return "(SELECT " + ftype.ToColumnName() + " FROM " + tableName + " WHERE " + columnName2 + " = song.hash AND " + columnName3 + " = " + playlist_id + " AND " + columnName4 + " = " + sqlQuote(folder) + " AND " + columnName + " = 0) " + text;
		}
		case LR2SongDBExtended.playlist.CustomFolderSortType.SCORE:
		case LR2SongDBExtended.playlist.CustomFolderSortType.MISS:
		case LR2SongDBExtended.playlist.CustomFolderSortType.PLAYCOUNT:
			return ftype.ToColumnName() + " " + text;
		case LR2SongDBExtended.playlist.CustomFolderSortType.TITLE:
		case LR2SongDBExtended.playlist.CustomFolderSortType.ARTIST:
			return "UPPER(" + ftype.ToColumnName() + ") " + text;
		default:
			return string.Empty;
		}
	}

	public void ChangeCustomFolderBaseDirectory(string outputDirBaseBefore, string outputDirBaseAfter)
	{
		using (rwlockBMSTables.GetReaderGuard())
		{
			foreach (BMSTable item in BMSTables.Where((BMSTable t) => !t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)))
			{
				using (item.ReaderWriterLock.GetWriterGuard())
				{
					removeCustomFolder(Path.Combine(outputDirBaseBefore, item.Output_dir), Path.Combine(outputDirBaseAfter, item.Output_dir));
					createCustomFolder(item, Path.Combine(outputDirBaseAfter, item.Output_dir));
				}
			}
		}
	}

	public void ChangeCustomFolderBaseDirectoryRoot(string outputDirBaseBefore, string outputDirBaseAfter)
	{
		using (rwlockBMSTables.GetReaderGuard())
		{
			foreach (BMSTable item in BMSTables.Where((BMSTable t) => t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)))
			{
				using (item.ReaderWriterLock.GetWriterGuard())
				{
					removeCustomFolder(Path.Combine(outputDirBaseBefore, item.Output_dir), Path.Combine(outputDirBaseAfter, item.Output_dir));
					createCustomFolder(item, Path.Combine(outputDirBaseAfter, item.Output_dir));
				}
			}
		}
	}

	public void MigrateCustomFolderOutputDirectory(BMSTable bmsTable, string outputDirPathBefore, string outputDirPathAfter = null)
	{
		if (!Settings.Default.OperationModeLR2DB)
		{
			throw new InvalidOperationException("Properties.Settings.Default.OperationModeLR2DB is not true");
		}
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		if (string.IsNullOrWhiteSpace(bmsTable.Output_dir))
		{
			throw new ArgumentException("bmsTable.Output_dir");
		}
		if (string.IsNullOrWhiteSpace(outputDirPathAfter))
		{
			outputDirPathAfter = GetCustomFolderOutputDirectory(bmsTable);
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				commitBMSTableHeaderOnly(bmsTable);
				removeCustomFolder(outputDirPathBefore, outputDirPathAfter);
				createCustomFolder(bmsTable, outputDirPathAfter);
			}
		}
	}

	public void ReOutputCustomFolder(BMSTable bmsTable)
	{
		if (!Settings.Default.OperationModeLR2DB)
		{
			throw new InvalidOperationException("Properties.Settings.Default.OperationModeLR2DB is not true");
		}
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		if (string.IsNullOrWhiteSpace(bmsTable.Output_dir))
		{
			throw new ArgumentException("bmsTable.Output_dir");
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				CommitBMSTable(bmsTable);
				removeCustomFolder(GetCustomFolderOutputDirectory(bmsTable), GetCustomFolderOutputDirectory(bmsTable));
				createCustomFolder(bmsTable, GetCustomFolderOutputDirectory(bmsTable));
			}
		}
	}

	private void createCustomFolder(BMSTable bmsTable, string outputDir)
	{
		List<string> list = new List<string>();
		foreach (Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>> item in new List<Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>>
		{
			new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.UserFolder, makeCustomFolderTextsUserFolder),
			new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder, makeCustomFolderTextsLevelFolder),
			new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder, makeCustomFolderTextsAlphabetFolder),
			new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder, makeCustomFolderTextsClearFolder),
			new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder, makeCustomFolderTextsDJLevelFolder),
			new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder, makeCustomFolderTextsCategoryAllFolder),
			new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder, makeCustomFolderTextsOtherFolder)
		})
		{
			if ((item.Item1 & bmsTable.ignore_folder_output) == 0)
			{
				list.AddRange(item.Item2(bmsTable));
			}
		}
		list = list.ToList();
		if (list.Count() == 0)
		{
			try
			{
				if (Directory.Exists(outputDir))
				{
					FileSystem.DeleteDirectory(outputDir, DeleteDirectoryOption.ThrowIfDirectoryNonEmpty);
				}
				return;
			}
			catch
			{
				return;
			}
		}
		int num = 0;
		try
		{
			Directory.CreateDirectory(outputDir);
			foreach (string item2 in list)
			{
				File.WriteAllText(Path.Combine(outputDir, $"{num:D4}" + ".lr2folder"), item2, Encoding.GetEncoding("shift_jis"));
				num++;
			}
		}
		catch
		{
			DispatcherMessageBox.Show("カスタムフォルダの出力に失敗しました。" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + "対象: " + bmsTable.name + Environment.NewLine + "出力先: " + outputDir, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
		}
	}

	public void RemoveCustomFolder(BMSTable bmsTable)
	{
		if (!Settings.Default.OperationModeLR2DB)
		{
			throw new InvalidOperationException("Properties.Settings.Default.OperationModeLR2DB is not true");
		}
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		if (string.IsNullOrWhiteSpace(bmsTable.Output_dir))
		{
			throw new ArgumentException("bmsTable.Output_dir");
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				removeCustomFolder(GetCustomFolderOutputDirectory(bmsTable));
			}
		}
	}

	private void removeCustomFolder(string targetDir, string newDir = null)
	{
		if (!Directory.Exists(targetDir))
		{
			return;
		}
		try
		{
			foreach (string item in Directory.EnumerateFiles(targetDir, "*.lr2folder", System.IO.SearchOption.TopDirectoryOnly))
			{
				FileSystem.DeleteFile(item, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
			}
			if ((newDir == null || !targetDir.Equals(newDir, StringComparison.OrdinalIgnoreCase)) && Directory.GetFileSystemEntries(targetDir).Count() == 0)
			{
				FileSystem.DeleteDirectory(targetDir, DeleteDirectoryOption.ThrowIfDirectoryNonEmpty);
			}
		}
		catch
		{
			DispatcherMessageBox.Show("ファイルまたはディレクトリの削除に失敗しました。" + Environment.NewLine + "読み取り専用属性がついていないか、" + Environment.NewLine + "正常にアクセスできるか確認して下さい。" + Environment.NewLine + targetDir, "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
		}
		LR2SongDBExtended lr2Song = new LR2SongDBExtended(lr2SongDBPath);
		try
		{
			lr2Song.BeginTransaction();
			(from f in lr2Song.Table<LR2SongDB.folder>().ToList()
				where !string.Equals(f.path, targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && f.path.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase)
				select f.path).ToList().ForEach(delegate(string p)
			{
				lr2Song.Delete<LR2SongDB.folder>(p);
			});
			List<LR2SongDB.folder> source = (from f in lr2Song.Table<LR2SongDB.folder>().ToList()
				where string.Equals(f.path, targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
				select f).ToList();
			if (source.Count() > 0)
			{
				LR2SongDB.folder folder = source.First();
				lr2Song.Delete<LR2SongDB.folder>(folder.path);
				if (!string.IsNullOrWhiteSpace(newDir) && string.Equals(Path.GetDirectoryName(targetDir), Path.GetDirectoryName(newDir), StringComparison.OrdinalIgnoreCase))
				{
					folder.path = folder.path.ReplaceFromStart(targetDir, newDir, isIgnoreCase: true);
					folder.date = null;
					folder.adddate = null;
					lr2Song.InsertOrReplace(folder, typeof(LR2SongDB.folder));
				}
			}
			lr2Song.Commit();
		}
		finally
		{
			if (lr2Song != null)
			{
				((IDisposable)lr2Song).Dispose();
			}
		}
	}

	public BMSTable RegistrateExternalTable(Uri pageUri)
	{
		if (!pageUri.IsAbsoluteUri)
		{
			throw new InvalidOperationException("pageUri.IsAbsoluteUri is not true");
		}
		BMSTable bMSTable;
		using (rwlockBMSTablesInitializeMin.GetReaderGuard())
		{
			using (rwlockBMSTables.GetWriterGuard())
			{
				bMSTable = LoadExternalTable(pageUri);
				if (bMSTable.last_update == default(DateTime))
				{
					bMSTable.last_update = DateTime.Now;
				}
				if (BMSTables.Select((BMSTable t) => t.name).Contains(bMSTable.name))
				{
					throw new InvalidOperationException("既に同名のプレイリストが存在します。");
				}
				if (string.IsNullOrWhiteSpace(bMSTable.Output_dir))
				{
					throw new InvalidOperationException("出力先ディレクトリ名が空になっています。");
				}
				CommitBMSTable(bMSTable);
				BMSTables.Add(bMSTable);
				if (Settings.Default.OperationModeLR2DB)
				{
					string customFolderOutputDirectory = GetCustomFolderOutputDirectory(bMSTable);
					removeCustomFolder(customFolderOutputDirectory);
					createCustomFolder(bMSTable, customFolderOutputDirectory);
				}
			}
		}
		return bMSTable;
	}

	public List<BMSTable> UpdateBMSTables(bool reloadExtPlaylist = true, List<Action<BMSTable, bool, BMSTable>> updateCallbackActions = null)
	{
		List<BMSTable> updatedTables = new List<BMSTable>();
		using (rwlockBMSTables.GetWriterGuard())
		{
			object lockObject = new object();
			Task.WaitAll(BMSTables.Select(delegate(BMSTable table)
			{
				BMSTable newTable = table;
				return Task.Run(delegate
				{
					Uri uri = table.Page_url ?? table.Header_url;
					bool arg = false;
					if (reloadExtPlaylist && table.is_external_sync && uri != null && uri.IsAbsoluteUri)
					{
						try
						{
							using (table.ReaderWriterLock.GetWriterGuard())
							{
								newTable = updateBMSTable(table, uri);
								using (newTable.ReaderWriterLock.GetWriterGuard())
								{
									lock (lockObject)
									{
										BMSTables[BMSTables.IndexOf(table)] = newTable;
										if (newTable.last_update != table.last_update)
										{
											arg = true;
											updatedTables.Add(newTable);
										}
									}
									CommitBMSTable(newTable);
								}
							}
						}
						catch
						{
						}
					}
					try
					{
						if (updateCallbackActions != null)
						{
							foreach (Action<BMSTable, bool, BMSTable> item in updateCallbackActions.Where((Action<BMSTable, bool, BMSTable> a) => a != null))
							{
								item(newTable, arg, table);
							}
							return;
						}
					}
					catch
					{
					}
				}).Logging("UpdateBMSTables", "D:\\Sync\\Repository\\BeMusicSeeker\\BeMusicSeeker\\Models\\BMSPlaylist.cs", 1484);
			}).ToArray());
			return updatedTables;
		}
	}

	public BMSTable ResetBMSTable(BMSTable bmsTable, Uri pageUri = null)
	{
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		using (rwlockBMSTables.GetWriterGuard())
		{
			using (bmsTable.ReaderWriterLock.GetWriterGuard())
			{
				BMSTable bMSTable = reloadBMSTable(bmsTable, pageUri);
				using (bMSTable.ReaderWriterLock.GetWriterGuard())
				{
					BMSTables[BMSTables.IndexOf(bmsTable)] = bMSTable;
					CommitBMSTable(bMSTable);
					return bMSTable;
				}
			}
		}
	}

	public void RemoveBMSTable(BMSTable bmsTable)
	{
		using (rwlockBMSTables.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				BMSTables.RemoveExt(bmsTable);
				deleteBMSTable(bmsTable);
			}
		}
	}

	public BMSTable CreateBMSTable()
	{
		BMSTable bMSTable = new BMSTable
		{
			last_update = DateTime.Now
		};
		using (rwlockBMSTablesInitializeMin.GetReaderGuard())
		{
			using (rwlockBMSTables.GetWriterGuard())
			{
				BMSTables.Add(bMSTable);
				return bMSTable;
			}
		}
	}

	internal void RenameFolderBMSTable(BMSTable bmsTable, string foldeNameBefore, string folderNameAfter, bool commitFlag = true)
	{
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				bmsTable.RenameFolder(foldeNameBefore, folderNameAfter);
				if (commitFlag)
				{
					ReOutputCustomFolderAndCommitToDB(bmsTable);
				}
			}
		}
	}

	internal void RemoveFolderBMSTable(BMSTable bmsTable, string folderNameDelete, bool commitFlag = true)
	{
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				bmsTable.RemoveFolder(folderNameDelete);
				if (commitFlag)
				{
					ReOutputCustomFolderAndCommitToDB(bmsTable);
				}
			}
		}
	}

	internal string CreateNewFolderBMSTable(BMSTable bmsTable, string newfolder = null, bool commitFlag = true)
	{
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (!BMSTables.Contains(bmsTable))
			{
				return null;
			}
			string result = bmsTable.CreateNewFolder(newfolder);
			if (commitFlag)
			{
				ReOutputCustomFolderAndCommitToDB(bmsTable);
			}
			return result;
		}
	}

	internal void AddEntriesToFolderBMSTable(IEnumerable<BMSTableEntry> bmsEntries, BMSTable bmsTable, string folderName, bool commitFlag = true)
	{
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				bmsTable.AddBMSTableEntriesToFolder(bmsEntries, folderName);
				if (commitFlag)
				{
					ReOutputCustomFolderAndCommitToDB(bmsTable);
				}
			}
		}
	}

	internal void RemoveEntriesBMSTable(IEnumerable<BMSTableEntry> bmsEntries, BMSTable bmsTable, bool commitFlag = true)
	{
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				bmsTable.RemoveBMSTableEntries(bmsEntries);
				if (commitFlag)
				{
					ReOutputCustomFolderAndCommitToDB(bmsTable);
				}
			}
		}
	}

	internal void ReOutputCustomFolderAndCommitToDB(BMSTable bmsTable)
	{
		if (bmsTable == null)
		{
			throw new ArgumentNullException("bmsTable");
		}
		using (bmsTable.ReaderWriterLock.GetWriterGuard())
		{
			if (BMSTables.Contains(bmsTable))
			{
				if (Settings.Default.OperationModeLR2DB)
				{
					ReOutputCustomFolder(bmsTable);
				}
				else
				{
					CommitBMSTable(bmsTable);
				}
			}
		}
	}

	public BMSTable LoadExternalTable(Uri pageUri, BMSTable baseTable = null)
	{
		if (pageUri == null || !pageUri.IsAbsoluteUri)
		{
			throw new ArgumentException("URIは絶対URIである必要があります", "pageUri");
		}
		if (pageUri.Scheme == "bmseeker")
		{
			return LoadWalkureTable(pageUri, baseTable);
		}
		Uri uri = null;
		try
		{
			StringBuilder sb = new StringBuilder();
			try
			{
				XDocument xDocument;
				using (SgmlReader reader = new SgmlReader
				{
					Href = pageUri.AbsoluteUri,
					IgnoreDtd = true,
					ErrorLog = new StringWriter(sb)
				})
				{
					xDocument = XDocument.Load(reader);
				}
				XNamespace xNamespace = xDocument.Root.Name.Namespace;
				uri = new Uri((from item in xDocument.Descendants(xNamespace + "meta")
					let attrName = item.Attribute("name")
					let attrCont = item.Attribute("content")
					where attrName != null && attrCont != null && attrName.Value == "bmstable" && !string.IsNullOrWhiteSpace(attrCont.Value)
					select attrCont.Value).FirstOrDefault(), UriKind.RelativeOrAbsolute);
			}
			catch (Exception)
			{
				string input;
				try
				{
					input = ((!pageUri.IsFile) ? new GZipWebClient
					{
						Encoding = Encoding.UTF8
					}.DownloadString(pageUri.AbsoluteUri) : File.ReadAllText(pageUri.LocalPath, Encoding.UTF8));
				}
				catch
				{
					throw;
				}
				Match match = new Regex("name\\s*=\\s*\"bmstable\"[^<>]*content\\s*=\\s*\"([^?\"<>]+)[\"?<>]", RegexOptions.IgnoreCase).Match(input);
				if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
				{
					throw new InvalidOperationException();
				}
				uri = new Uri(match.Groups[1].Value, UriKind.RelativeOrAbsolute);
			}
		}
		catch
		{
			uri = pageUri;
			pageUri = null;
		}
		Uri uri2 = ((!uri.IsAbsoluteUri) ? new Uri(pageUri, uri) : uri);
		string header_json;
		try
		{
			header_json = ((!uri2.IsFile) ? new GZipWebClient
			{
				Encoding = Encoding.UTF8
			}.DownloadString(uri2) : File.ReadAllText(uri2.LocalPath, Encoding.UTF8));
		}
		catch
		{
			throw;
		}
		BMSTable bMSTable = new BMSTable();
		if (baseTable != null)
		{
			bMSTable.compat_prefix = baseTable.compat_prefix;
		}
		bMSTable.LoadHeaderJSON(header_json, pageUri, uri);
		if (baseTable != null)
		{
			bMSTable.playlist_id = baseTable.playlist_id;
			bMSTable.name = baseTable.name;
			bMSTable.symbol = baseTable.symbol;
			bMSTable.ignore_folder_output = baseTable.ignore_folder_output;
			bMSTable.is_external_sync = baseTable.is_external_sync;
			bMSTable.Output_dir = baseTable.Output_dir;
			bMSTable.is_root_folder = baseTable.is_root_folder;
		}
		string data_json;
		try
		{
			data_json = new GZipWebClient
			{
				Encoding = Encoding.UTF8
			}.DownloadString(bMSTable.GetAbsoluteDataUrl());
		}
		catch
		{
			throw;
		}
		bMSTable.LoadDataJSON(data_json);
		return bMSTable;
	}

	private BMSTable updateBMSTable(BMSTable oldTable, Uri pageUri = null)
	{
		if (pageUri == null)
		{
			pageUri = oldTable.Page_url ?? oldTable.Header_url;
		}
		BMSTable newTable = reloadBMSTable(oldTable, pageUri);
		List<BMSTableEntry> list = oldTable.entries.Where(delegate(BMSTableEntry oe)
		{
			List<BMSTableEntry> source = newTable.entries.Where((BMSTableEntry ne) => (!string.IsNullOrWhiteSpace(ne.md5) && !string.IsNullOrWhiteSpace(oe.md5) && ne.md5 == oe.md5) || (string.IsNullOrWhiteSpace(ne.md5) && string.IsNullOrWhiteSpace(oe.md5) && !string.IsNullOrWhiteSpace(ne.lr2_bmsid) && !string.IsNullOrWhiteSpace(oe.lr2_bmsid) && ne.lr2_bmsid == oe.lr2_bmsid) || (string.IsNullOrWhiteSpace(ne.md5) && string.IsNullOrWhiteSpace(oe.md5) && string.IsNullOrWhiteSpace(ne.lr2_bmsid) && string.IsNullOrWhiteSpace(oe.lr2_bmsid) && !string.IsNullOrWhiteSpace(ne.title) && !string.IsNullOrWhiteSpace(oe.title) && ne.title == oe.title)).ToList();
			if (source.Count() == 0)
			{
				return false;
			}
			BMSTableEntry bMSTableEntry;
			if (source.Count() > 1)
			{
				List<BMSTableEntry> source2 = source.Where((BMSTableEntry ne) => ne.folder == oe.folder).ToList();
				bMSTableEntry = ((source2.Count() <= 0) ? source.OrderByDescending((BMSTableEntry ne) => Math.Abs(((!ne.level.HasValue) ? 0.0 : oe.level.Value) - ((!oe.level.HasValue) ? 0.0 : oe.level.Value))).First() : source2.First());
			}
			else
			{
				bMSTableEntry = source.First();
			}
			bMSTableEntry.memo = oe.memo;
			bMSTableEntry.adddate = oe.adddate;
			return true;
		}).ToList();
		if (newTable.last_update == default(DateTime) || newTable.last_update <= oldTable.last_update)
		{
			List<string> folder_list = oldTable.folder_list;
			List<string> folder_list2 = newTable.folder_list;
			if (newTable.entries.Count != list.Count || list.Count != oldTable.entries.Where((BMSTableEntry e) => !e.is_removed).Count() || folder_list.Except(folder_list2).Any() || folder_list2.Except(folder_list).Any())
			{
				newTable.last_update = DateTime.Now;
			}
			else
			{
				newTable.last_update = ((oldTable.last_update == default(DateTime)) ? DateTime.Now : oldTable.last_update);
			}
		}
		List<BMSTableEntry> list2 = oldTable.entries.Except(list).ToList();
		foreach (BMSTableEntry item in list2)
		{
			item.is_removed = true;
		}
		newTable.entries = newTable.entries.Concat(list2).ToList();
		return newTable;
	}

	private BMSTable reloadBMSTable(BMSTable bmsTable, Uri pageUri = null)
	{
		if (pageUri == null)
		{
			pageUri = bmsTable.Page_url ?? bmsTable.Header_url;
		}
		return LoadExternalTable(pageUri, bmsTable);
	}

	private void CommitBMSTable(BMSTable bmsTable)
	{
		CommitBMSTable(new BMSTable[1] { bmsTable });
	}

	private void CommitBMSTable(IEnumerable<BMSTable> bmsTables)
	{
		try
		{
			LR2SongDBExtended lr2Song = new LR2SongDBExtended(lr2SongDBPath);
			try
			{
				lr2Song.BeginTransaction();
				foreach (BMSTable bmsTable in bmsTables)
				{
					lr2Song.InsertOrReplace(bmsTable, typeof(LR2SongDBExtended.playlist));
					lr2Song.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id) + " = " + bmsTable.playlist_id + ";");
					bmsTable.entries.ForEach(delegate(BMSTableEntry e)
					{
						lr2Song.InsertOrReplace(e, typeof(LR2SongDBExtended.playlist_entry));
					});
				}
				lr2Song.Commit();
			}
			finally
			{
				if (lr2Song != null)
				{
					((IDisposable)lr2Song).Dispose();
				}
			}
		}
		catch
		{
			throw;
		}
	}

	public void CommitBMSTableEntry(BMSTableEntry entry)
	{
		if (!entry.playlist_id.HasValue)
		{
			return;
		}
		try
		{
			using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
			lR2SongDBExtended.BeginTransaction();
			lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id) + " = " + entry.playlist_id + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5) + ((entry.md5 == null) ? " IS NULL " : (" = " + sqlQuote(entry.md5))) + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder) + " = " + sqlQuote(entry.folder) + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.lr2_bmsid) + ((entry.lr2_bmsid == null) ? " IS NULL " : (" = " + sqlQuote(entry.lr2_bmsid))) + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.title) + ((entry.title == null) ? " IS NULL " : (" = " + sqlQuote(entry.title))) + ";");
			lR2SongDBExtended.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
			lR2SongDBExtended.Commit();
		}
		catch
		{
			throw;
		}
	}

	private void deleteBMSTable(BMSTable bmsTable)
	{
		deleteBMSTable(new BMSTable[1] { bmsTable });
	}

	private void deleteBMSTable(IEnumerable<BMSTable> bmsTables)
	{
		try
		{
			using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
			lR2SongDBExtended.BeginTransaction();
			foreach (BMSTable bmsTable in bmsTables)
			{
				if (bmsTable.playlist_id.HasValue)
				{
					lR2SongDBExtended.Delete<LR2SongDBExtended.playlist>(bmsTable.playlist_id);
					lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id) + " = " + bmsTable.playlist_id + ";");
				}
			}
			lR2SongDBExtended.Commit();
		}
		catch
		{
			throw;
		}
	}

	private void commitBMSTableHeaderOnly(BMSTable bmsTable)
	{
		try
		{
			using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
			lR2SongDBExtended.BeginTransaction();
			lR2SongDBExtended.InsertOrReplace(bmsTable, typeof(LR2SongDBExtended.playlist));
			lR2SongDBExtended.Commit();
		}
		catch
		{
			throw;
		}
	}

	public string GetPlaylistDump()
	{
		using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
		return string.Join("\v" + Environment.NewLine, from c in lR2SongDBExtended.Dump<LR2SongDBExtended.playlist>()
			select c.Replace("\v" + Environment.NewLine, Environment.NewLine)) + "\v" + Environment.NewLine + string.Join("\v" + Environment.NewLine, from c in lR2SongDBExtended.Dump<LR2SongDBExtended.playlist_entry>()
			select c.Replace("\v" + Environment.NewLine, Environment.NewLine));
	}

	public void LoadPlaylistDump(string sql)
	{
		using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
		using (new StringReader(sql))
		{
			string savepoint = lR2SongDBExtended.SaveTransactionPoint();
			try
			{
				lR2SongDBExtended.DropTable<LR2SongDBExtended.playlist>();
				lR2SongDBExtended.DropTable<LR2SongDBExtended.playlist_entry>();
				lR2SongDBExtended.CreateTable<LR2SongDBExtended.playlist>();
				lR2SongDBExtended.CreateTable<LR2SongDBExtended.playlist_entry>();
				string[] source = sql.Split(new string[1] { "\v" + Environment.NewLine }, StringSplitOptions.None);
				if (source.Count() <= 1)
				{
					throw new InvalidDataException("バックアップデータが不正です");
				}
				foreach (string item in source.Where((string s) => !string.IsNullOrWhiteSpace(s)))
				{
					lR2SongDBExtended.Execute(item);
				}
				lR2SongDBExtended.Commit();
			}
			catch (Exception)
			{
				lR2SongDBExtended.RollbackTo(savepoint);
				throw;
			}
		}
	}

	public static List<BMSTableSimple> GetBMSTableInfo(Uri tableinfoUri)
	{
		if (!tableinfoUri.IsAbsoluteUri)
		{
			throw new InvalidOperationException("tableinfoUri.IsAbsoluteUri is not true");
		}
		string json;
		try
		{
			json = new GZipWebClient
			{
				Encoding = Encoding.UTF8
			}.DownloadString(tableinfoUri);
		}
		catch
		{
			throw;
		}
		object[] source;
		try
		{
			source = (object[])DynamicJson.Parse(json);
		}
		catch
		{
			throw new ArgumentException("パースに失敗しました。", "tableinfoUri");
		}
		return source.Select((dynamic e) => new BMSTableSimple(e)).ToList();
	}

	private static string sqlQuote(string str = null)
	{
		if (!string.IsNullOrWhiteSpace(str))
		{
			return "'" + str.Replace("'", "''") + "'";
		}
		return "''";
	}

	private static string getCustomFolderText(string command, string category, string title, int maxtracks = 0)
	{
		return "#COMMAND " + command + Environment.NewLine + "#MAXTRACKS " + maxtracks + Environment.NewLine + "#CATEGORY " + category + Environment.NewLine + "#TITLE " + title + Environment.NewLine + "#INFORMATION_A " + Environment.NewLine + "#INFORMATION_B " + Environment.NewLine + Environment.NewLine;
	}

	public static string GetCustomFolderOutputDirectory(BMSTable bmsTable)
	{
		try
		{
			return Path.Combine(bmsTable.is_root_folder ? Settings.Default.LR2CustomFolderOutputBaseDirRootType : Settings.Default.LR2CustomFolderOutputBaseDir, bmsTable.Output_dir);
		}
		catch (ArgumentNullException)
		{
			DispatcherMessageBox.Show("カスタムフォルダ出力先ディレクトリの不正を検出しました。" + Environment.NewLine + "正しく出力先が指定されているか確認して下さい。", "警告", MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
			throw;
		}
	}
}
