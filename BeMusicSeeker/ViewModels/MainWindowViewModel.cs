using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Codeplex.Data;
using Livet;
using Livet.Commands;
using Livet.EventListeners;
using Livet.Messaging;
using Livet.Messaging.IO;
using Microsoft.VisualBasic.FileIO;
using NLog;
using Ribbit.BMS;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.ViewModels;

internal readonly struct LibraryRowsBuildMetrics
{
    internal LibraryRowsBuildMetrics(
        int sourceBmsCount,
        int sourceBmsonCount,
        int filteredBmsCount,
        int filteredBmsonCount,
        bool folderFilterApplied,
        long regularFilterMs,
        long bmsonFilterMs,
        long regularRowMaterializeMs,
        long bmsonRowMaterializeMs,
        long concatToListMs,
        long folderMs,
        int folderCount,
        int regularRowCacheHitCount = 0,
        int regularRowCacheMissCount = 0,
        int regularRowCachePrunedCount = 0)
    {
        SourceBmsCount = sourceBmsCount;
        SourceBmsonCount = sourceBmsonCount;
        FilteredBmsCount = filteredBmsCount;
        FilteredBmsonCount = filteredBmsonCount;
        FolderFilterApplied = folderFilterApplied;
        RegularFilterMs = regularFilterMs;
        BmsonFilterMs = bmsonFilterMs;
        RegularRowMaterializeMs = regularRowMaterializeMs;
        BmsonRowMaterializeMs = bmsonRowMaterializeMs;
        ConcatToListMs = concatToListMs;
        FolderMs = folderMs;
        FolderCount = folderCount;
        RegularRowCacheHitCount = regularRowCacheHitCount;
        RegularRowCacheMissCount = regularRowCacheMissCount;
        RegularRowCachePrunedCount = regularRowCachePrunedCount;
    }

    internal int SourceBmsCount { get; }

    internal int SourceBmsonCount { get; }

    internal int FilteredBmsCount { get; }

    internal int FilteredBmsonCount { get; }

    internal bool FolderFilterApplied { get; }

    internal long RegularFilterMs { get; }

    internal long BmsonFilterMs { get; }

    internal long RegularRowMaterializeMs { get; }

    internal long BmsonRowMaterializeMs { get; }

    internal long ConcatToListMs { get; }

    internal long FolderMs { get; }

    internal int FolderCount { get; }

    internal int RegularRowCacheHitCount { get; }

    internal int RegularRowCacheMissCount { get; }

    internal int RegularRowCachePrunedCount { get; }
}

internal readonly struct BmsonLibraryRowCacheSyncResult
{
    internal BmsonLibraryRowCacheSyncResult(bool membershipChanged, bool sortKeyChanged)
    {
        MembershipChanged = membershipChanged;
        SortKeyChanged = sortKeyChanged;
    }

    internal bool MembershipChanged { get; }

    internal bool SortKeyChanged { get; }
}

internal readonly struct NormalLibrarySortCacheKey : IEquatable<NormalLibrarySortCacheKey>
{
    internal NormalLibrarySortCacheKey(long sourceGeneration, long sortKeyGeneration, string columnName, ListSortDirection direction, int rowCount)
    {
        SourceGeneration = sourceGeneration;
        SortKeyGeneration = sortKeyGeneration;
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        RowCount = rowCount;
    }

    internal long SourceGeneration { get; }

    internal long SortKeyGeneration { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal int RowCount { get; }

    public bool Equals(NormalLibrarySortCacheKey other)
    {
        return SourceGeneration == other.SourceGeneration
            && SortKeyGeneration == other.SortKeyGeneration
            && string.Equals(ColumnName, other.ColumnName, StringComparison.Ordinal)
            && Direction == other.Direction
            && RowCount == other.RowCount;
    }

    public override bool Equals(object obj)
    {
        return obj is NormalLibrarySortCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = SourceGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ SortKeyGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(ColumnName ?? string.Empty);
            hashCode = (hashCode * 397) ^ (int)Direction;
            hashCode = (hashCode * 397) ^ RowCount;
            return hashCode;
        }
    }
}

/// <summary>
/// BeMusicSeeker のメイン画面を制御する ViewModel です。
/// ライブラリ（BMSファイル群）やプレイリストの管理、各ビュー状態の維持、内蔵および外部BMSプレイヤー機能の連携のほか、
/// UI (MainWindow) とのデータバインディングやルーティングを担います。
/// </summary>
public class MainWindowViewModel : ViewModel
{
    public class SettingDialogViewModel : ViewModel
    {
        [Flags]
        public enum RestartMode
        {
            None = 0,
            FolderOnly = 1,
            All = 2
        }

        private MainWindowViewModel ownerViewModel;

        private bool tempValidation;

        private PropertyChangedEventListener ownerViewModelEventListener;

        private PropertyChangedEventListener resourceServiceEventListener;

        private bool tempOperationModeLR2DB;

        private string tempLR2RootPath;

        private Dictionary<string, Point> lr2bodyResolutions = new Dictionary<string, Point>
        {
            {
                "  320x180 (16:9)",
                new Point(320.0, 180.0)
            },
            {
                "  480x270 (16:9)",
                new Point(480.0, 270.0)
            },
            {
                "  640x360 (16:9)",
                new Point(640.0, 360.0)
            },
            {
                "  960x540 (16:9)",
                new Point(960.0, 540.0)
            },
            {
                " 1280x720 (16:9)",
                new Point(1280.0, 720.0)
            },
            {
                "1920x1080 (16:9)",
                new Point(1920.0, 1080.0)
            },
            {
                "  320x240 (4:3)",
                new Point(320.0, 240.0)
            },
            {
                "  480x360 (4:3)",
                new Point(480.0, 360.0)
            },
            {
                "  640x480 (4:3)",
                new Point(640.0, 480.0)
            },
            {
                "  960x720 (4:3)",
                new Point(960.0, 720.0)
            },
            {
                " 1280x960 (4:3)",
                new Point(1280.0, 960.0)
            },
            {
                "1600x1200 (4:3)",
                new Point(1600.0, 1200.0)
            }
        };

        private Point tempLR2bodyResolution;

        private string tempBMSRootPath;

        private string tempLR2SongDBPath;

        private string tempLR2ConfigXmlPath;

        private string tempuBMplayPath;

        private string tempBMIIDXViewPath;

        private bool tempUsePlayeruBMplay;

        private bool tempUsePlayerLR2body;

        private bool tempUsePlayerBMIIDXView;

        private bool tempIsSaveLR2bodyWindowPosition;

        private string tempLR2CustomFolderOutputDir;

        private string tempBMSInstallDir;

        private string tempLR2CustomFolderAsRootOutputDir;

        private Uri tempTableListURL;

        private bool tempEnablePlaylistUrlCompletion;

        private bool tempOverwritePlaylistUrlsWithCompletion;

        private string tempPlaylistMd5UrlMappingTsvUri;

        private bool tempIsLR2BackupEnabled;

        private string tempLR2BackupPath;

        private Backup.Target tempLR2BackupTarget;

        private int tempLR2BackupSpan;

        private int tempLR2BackupNum;

        private bool tempUseExternalWebBrowser;

        private bool tempUseExternalPanelImage;

        private bool tempShowScoreViewerRegisterConfirmMsg;

        private bool tempShowDiffBMSInstallConfirmMsg;

        private bool tempShowRecommUpdatedMsg;

        private bool tempSkipInitFileCheck;

        private bool tempSkipInitPlaylistLoad;

        private bool tempStartupSelectInstallPending;

        private bool tempStartupExpandPlaylistTree;

        private bool tempEnableReadOptimizedPragmas;

        private bool tempSkipEstimateOfflineScoreRanking;

        private bool tempEnableAutoInstall;

        private bool tempKeepInstallablePackagesPending;

        private bool tempUseEverythingForPendingPackageSourceScan;

        private bool tempAutoApplyAmbiguousInstallDestination;

        private bool tempDeletePendingPackageSourceAfterInstall;

        private bool tempEnableSmartComponentOverwrite;

        private bool tempKeepSmartOverwriteProtectedFilesByRenaming;

        private string tempStagefilePath;

        private static string defaultFolderNameFormat = "[%ARTIST%] %TITLE%";

        private string tempFolderNameFormat;

        private bool tempUseOnlyShiftJISChars;

        private SampleRate tempEncoderSampleRate;

        private int tempEncoderIndex;

        private SampleFormat tempEncoderFormat;

        private BMSAutoPlayWriter.Normalization tempEncoderNormalization;

        private float tempEncoderQuality;

        private float tempEncoderAmplifier;

        private string tempEncoderExeDir;

        private static string defaultEncodeFileNameFormat = "[%ARTIST%] %TITLE%";

        private string tempEncodeFileNameFormat;

        private int tempPlayerDriverIndex;

        private List<BassAudioPlayer.DeviceDescriptor> playerDeviceNames;

        private string tempPlayerDevice;

        private string tempPlayerDeviceName;

        private SampleRate tempPlayerSampleRate;

        private SampleFormat tempPlayerFormat;

        private float tempPlayerBufferSize;

        private bool tempPlayerWASAPIParam;

        private ListenerCommand<FolderSelectionMessage> _OpenRootFolderCommand;

        private ListenerCommand<OpeningFileSelectionMessage> _OpenFileCommand;

        private ListenerCommand<FolderSelectionMessage> _OpenDirCommand;

        private bool isBMSDirectoryAdded;

        private ListenerCommand<FolderSelectionMessage> _AddBMSDirCommandFromMainWindow;

        private ListenerCommand<FolderSelectionMessage> _AddBMSDirCommand;

        private bool isBMSDirectoryRemoved;

        private ListenerCommand<string> _RemoveDirCommand;

        private string tempLanguage;

        public bool OperationModeLR2DB
        {
            get
            {
                return Settings.Default.OperationModeLR2DB;
            }
            set
            {
                if (Settings.Default.OperationModeLR2DB != value)
                {
                    Settings.Default.OperationModeLR2DB = value;
                    RaisePropertyChanged("OperationModeLR2DB");
                }
            }
        }

        public string LR2bodyPath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LR2RootPath))
                {
                    string path = Path.Combine(LR2RootPath, "LR2body.exe");
                    string text = Path.Combine(LR2RootPath, "LRHbody.exe");
                    if (OperationModeLR2DB && !string.IsNullOrWhiteSpace(LR2ConfigXmlPath) && LR2ConfigXmlPath.EndsWith(".xmh", StringComparison.OrdinalIgnoreCase) && File.Exists(text))
                    {
                        return Path.Combine(LR2RootPath, text);
                    }
                    return Path.Combine(LR2RootPath, path);
                }
                return string.Empty;
            }
        }

        public string LR2RootPath
        {
            get
            {
                if (!IsLR2RootPathValid())
                {
                    Settings.Default.LR2RootPath = null;
                }
                return Settings.Default.LR2RootPath;
            }
            set
            {
                if (Settings.Default.LR2RootPath == value)
                {
                    return;
                }
                if (IsLR2RootPathValid(value))
                {
                    string lR2SongDBPath = Path.Combine(value, "LR2files\\Database\\song.db");
                    string text = Path.Combine(value, "LR2files\\Config\\config.xml");
                    string text2 = Path.Combine(value, "LR2files\\Config\\config.xmh");
                    string empty = string.Empty;
                    if (IsLR2ConfigXmlPathValid(text))
                    {
                        if (IsLR2ConfigXmlPathValid(text2))
                        {
                            DateTime lastWriteTime = File.GetLastWriteTime(text);
                            empty = ((File.GetLastWriteTime(text2) >= lastWriteTime) ? text2 : text);
                        }
                        else
                        {
                            empty = text;
                        }
                    }
                    else
                    {
                        empty = text2;
                    }
                    Settings.Default.LR2RootPath = value;
                    LR2SongDBPath = lR2SongDBPath;
                    LR2ConfigXmlPath = empty;
                }
                else
                {
                    Settings.Default.LR2RootPath = null;
                }
                RaisePropertyChanged("LR2RootPath");
                RaisePropertyChanged(() => LR2bodyPath);
            }
        }

        public Dictionary<string, Point> LR2bodyResolutions => lr2bodyResolutions;

        public Point LR2bodyResolution
        {
            get
            {
                return Settings.Default.LR2bodyResolution;
            }
            set
            {
                Settings.Default.LR2bodyResolution = value;
            }
        }

        public string BMSRootPath
        {
            get
            {
                if (!IsBMSRootPathValid())
                {
                    Settings.Default.BMSRootPath = null;
                }
                return Settings.Default.BMSRootPath;
            }
            set
            {
                if (!(Settings.Default.BMSRootPath == value))
                {
                    if (IsBMSRootPathValid(value))
                    {
                        Settings.Default.BMSRootPath = value;
                    }
                    else
                    {
                        Settings.Default.BMSRootPath = null;
                    }
                    RaisePropertyChanged("BMSRootPath");
                }
            }
        }

        public string LR2SongDBPath
        {
            get
            {
                if (!IsLR2SongDBPathValid())
                {
                    Settings.Default.LR2SongDBPath = null;
                }
                return Settings.Default.LR2SongDBPath;
            }
            set
            {
                if (!(Settings.Default.LR2SongDBPath == value))
                {
                    if (IsLR2SongDBPathValid(value))
                    {
                        Settings.Default.LR2SongDBPath = value;
                    }
                    RaisePropertyChanged("LR2SongDBPath");
                }
            }
        }

        public string LR2ConfigXmlPath
        {
            get
            {
                if (!IsLR2ConfigXmlPathValid())
                {
                    Settings.Default.LR2ConfigXmlPath = null;
                    lr2config = null;
                }
                return Settings.Default.LR2ConfigXmlPath;
            }
            set
            {
                if (Settings.Default.LR2ConfigXmlPath == value)
                {
                    return;
                }
                if (IsLR2ConfigXmlPathValid(value))
                {
                    Settings.Default.LR2ConfigXmlPath = value;
                    try
                    {
                        lr2config = new LR2Config(Settings.Default.LR2ConfigXmlPath);
                    }
                    catch
                    {
                        Settings.Default.LR2ConfigXmlPath = null;
                        lr2config = null;
                    }
                }
                RaisePropertyChanged("LR2ConfigXmlPath");
                RaisePropertyChanged(() => LR2bodyPath);
            }
        }

        private LR2Config lr2config
        {
            get
            {
                return ownerViewModel.lr2config;
            }
            set
            {
                if (ownerViewModel.lr2config != value)
                {
                    ownerViewModel.lr2config = value;
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                    RaisePropertyChanged(() => LR2CustomFolderOutputDir);
                    RaisePropertyChanged(() => LR2CustomFolderAsRootOutputDir);
                    RaisePropertyChanged(() => BMSInstallDir);
                }
            }
        }

        public string uBMplayPath
        {
            get
            {
                if (!IsuBMplayPathValid())
                {
                    Settings.Default.uBMplayPath = null;
                }
                return Settings.Default.uBMplayPath;
            }
            set
            {
                if (!(Settings.Default.uBMplayPath == value))
                {
                    if (IsuBMplayPathValid(value))
                    {
                        Settings.Default.uBMplayPath = value;
                    }
                    RaisePropertyChanged("uBMplayPath");
                }
            }
        }

        public string BMIIDXViewPath
        {
            get
            {
                if (!IsBMIIDXViewPathValid())
                {
                    Settings.Default.BMIIDXViewPath = null;
                }
                return Settings.Default.BMIIDXViewPath;
            }
            set
            {
                if (!(Settings.Default.BMIIDXViewPath == value))
                {
                    if (IsBMIIDXViewPathValid(value))
                    {
                        Settings.Default.BMIIDXViewPath = value;
                    }
                    RaisePropertyChanged("BMIIDXViewPath");
                }
            }
        }

        public bool UsePlayeruBMplay
        {
            get
            {
                return Settings.Default.UsePlayeruBMplay;
            }
            set
            {
                if (Settings.Default.UsePlayeruBMplay != value)
                {
                    Settings.Default.UsePlayeruBMplay = value;
                    RaisePropertyChanged("UsePlayeruBMplay");
                }
            }
        }

        public bool UsePlayerLR2body
        {
            get
            {
                return Settings.Default.UsePlayerLR2body;
            }
            set
            {
                if (Settings.Default.UsePlayerLR2body != value)
                {
                    Settings.Default.UsePlayerLR2body = value;
                    RaisePropertyChanged("UsePlayerLR2body");
                }
            }
        }

        public bool UsePlayerBMIIDXView
        {
            get
            {
                return Settings.Default.UsePlayerBMIIDXView;
            }
            set
            {
                if (Settings.Default.UsePlayerBMIIDXView != value)
                {
                    Settings.Default.UsePlayerBMIIDXView = value;
                    RaisePropertyChanged("UsePlayerBMIIDXView");
                }
            }
        }

        public bool IsSaveLR2bodyWindowPosition
        {
            get
            {
                return Settings.Default.IsSaveLR2bodyWindowPosition;
            }
            set
            {
                if (Settings.Default.IsSaveLR2bodyWindowPosition != value)
                {
                    Settings.Default.IsSaveLR2bodyWindowPosition = value;
                    RaisePropertyChanged("IsSaveLR2bodyWindowPosition");
                }
            }
        }

        public List<string> LR2ConfigBMSDirectories
        {
            get
            {
                string rootDir = LR2CustomFolderAsRootOutputDir;
                string tempRootDir = tempLR2CustomFolderAsRootOutputDir;
                if (lr2config != null)
                {
                    return (from d in lr2config.GetBMSSearchDirectories()
                            where string.IsNullOrWhiteSpace(rootDir) || ownerViewModel.BMSTables == null || ownerViewModel.BMSTables.Where((BMSTable t) => t != null && t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)).All((BMSTable t) => !string.Equals(d, Path.Combine(rootDir, t.Output_dir), StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(tempRootDir) || !string.Equals(d, Path.Combine(tempRootDir, t.Output_dir), StringComparison.OrdinalIgnoreCase)))
                            select d).ToList();
                }
                return null;
            }
            private set
            {
            }
        }

        public string LR2CustomFolderOutputDir
        {
            get
            {
                if (!IsLR2CustomFolderOutputDirValid())
                {
                    Settings.Default.LR2CustomFolderOutputBaseDir = null;
                }
                return Settings.Default.LR2CustomFolderOutputBaseDir;
            }
            set
            {
                if (value != null && !(Settings.Default.LR2CustomFolderOutputBaseDir == value))
                {
                    if (IsLR2CustomFolderOutputDirValid(value))
                    {
                        Settings.Default.LR2CustomFolderOutputBaseDir = value;
                    }
                    RaisePropertyChanged("LR2CustomFolderOutputDir");
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                }
            }
        }

        public string BMSInstallDir
        {
            get
            {
                if (!IsBMSInstallDirValid())
                {
                    Settings.Default.BMSInstallDir = null;
                }
                return Settings.Default.BMSInstallDir;
            }
            set
            {
                if (value != null && !(Settings.Default.BMSInstallDir == value))
                {
                    if (IsBMSInstallDirValid(value))
                    {
                        Settings.Default.BMSInstallDir = value;
                    }
                    RaisePropertyChanged("BMSInstallDir");
                }
            }
        }

        public string LR2CustomFolderAsRootOutputDir
        {
            get
            {
                if (!IsLR2CustomFolderAsRootOutputDirValid())
                {
                    Settings.Default.LR2CustomFolderOutputBaseDirRootType = null;
                }
                return Settings.Default.LR2CustomFolderOutputBaseDirRootType;
            }
            set
            {
                if (value != null && !(Settings.Default.LR2CustomFolderOutputBaseDirRootType == value))
                {
                    if (IsLR2CustomFolderAsRootOutputDirValid(value))
                    {
                        Settings.Default.LR2CustomFolderOutputBaseDirRootType = value;
                    }
                    else
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("既に親ディレクトリが登録されています。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    RaisePropertyChanged("LR2CustomFolderAsRootOutputDir");
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                }
            }
        }

        public Uri TableListURL
        {
            get
            {
                if (!IsTableListURLValid())
                {
                    Settings.Default.TableListURL = new Uri("http://www.ribbit.xyz/bms/tables/table_info.json");
                }
                return Settings.Default.TableListURL;
            }
            set
            {
                if (!(Settings.Default.TableListURL == value))
                {
                    if (IsTableListURLValid(value))
                    {
                        Settings.Default.TableListURL = value;
                    }
                    else
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("難易度表リスト取得URIが正しくありません。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    RaisePropertyChanged("TableListURL");
                }
            }
        }

        public bool EnablePlaylistUrlCompletion
        {
            get
            {
                return Settings.Default.EnablePlaylistUrlCompletion;
            }
            set
            {
                if (Settings.Default.EnablePlaylistUrlCompletion != value)
                {
                    Settings.Default.EnablePlaylistUrlCompletion = value;
                    RaisePropertyChanged("EnablePlaylistUrlCompletion");
                }
            }
        }

        public bool OverwritePlaylistUrlsWithCompletion
        {
            get
            {
                return Settings.Default.OverwritePlaylistUrlsWithCompletion;
            }
            set
            {
                if (Settings.Default.OverwritePlaylistUrlsWithCompletion != value)
                {
                    Settings.Default.OverwritePlaylistUrlsWithCompletion = value;
                    RaisePropertyChanged("OverwritePlaylistUrlsWithCompletion");
                }
            }
        }

        public string PlaylistMd5UrlMappingTsvUri
        {
            get
            {
                if (Settings.Default.PlaylistMd5UrlMappingTsvUri == null)
                {
                    Settings.Default.PlaylistMd5UrlMappingTsvUri = PlaylistUrlCompletionSupport.DefaultMd5UrlMappingTsvUri;
                }
                return Settings.Default.PlaylistMd5UrlMappingTsvUri;
            }
            set
            {
                string normalizedValue = value ?? string.Empty;
                if (string.Equals(Settings.Default.PlaylistMd5UrlMappingTsvUri, normalizedValue, StringComparison.Ordinal))
                {
                    return;
                }
                if (IsPlaylistMd5UrlMappingTsvUriValid(normalizedValue))
                {
                    Settings.Default.PlaylistMd5UrlMappingTsvUri = normalizedValue;
                }
                else
                {
                    ownerViewModel.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Error_InvalidPlaylistMd5UrlMappingTsvUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                }
                RaisePropertyChanged("PlaylistMd5UrlMappingTsvUri");
            }
        }

        public bool IsLR2BackupEnabled
        {
            get
            {
                return Settings.Default.IsLR2BackupEnabled;
            }
            set
            {
                if (Settings.Default.IsLR2BackupEnabled != value)
                {
                    Settings.Default.IsLR2BackupEnabled = value;
                    RaisePropertyChanged("IsLR2BackupEnabled");
                }
            }
        }

        public string LR2BackupPath
        {
            get
            {
                if (!IsLR2BackupPathValid())
                {
                    Settings.Default.LR2BackupPath = null;
                }
                return Settings.Default.LR2BackupPath;
            }
            set
            {
                if (!(Settings.Default.LR2BackupPath == value))
                {
                    if (IsLR2BackupPathValid(value))
                    {
                        Settings.Default.LR2BackupPath = value;
                    }
                    else
                    {
                        Settings.Default.LR2BackupPath = null;
                    }
                    RaisePropertyChanged("LR2BackupPath");
                }
            }
        }

        public Backup.Target LR2BackupTarget
        {
            get
            {
                return Settings.Default.LR2BackupTarget;
            }
            set
            {
                if (Settings.Default.LR2BackupTarget != value)
                {
                    Settings.Default.LR2BackupTarget = value;
                    RaisePropertyChanged("LR2BackupTarget");
                }
            }
        }

        public int LR2BackupSpan
        {
            get
            {
                if (!IsLR2BackupSpanValid())
                {
                    Settings.Default.LR2BackupSpan = 7;
                }
                return Settings.Default.LR2BackupSpan;
            }
            set
            {
                if (Settings.Default.LR2BackupSpan != value)
                {
                    if (IsLR2BackupSpanValid(value))
                    {
                        Settings.Default.LR2BackupSpan = value;
                    }
                    RaisePropertyChanged("LR2BackupSpan");
                }
            }
        }

        public int LR2BackupNum
        {
            get
            {
                if (!IsLR2BackupNumValid())
                {
                    Settings.Default.LR2BackupNum = 1;
                }
                return Settings.Default.LR2BackupNum;
            }
            set
            {
                if (Settings.Default.LR2BackupNum != value)
                {
                    if (IsLR2BackupNumValid(value))
                    {
                        Settings.Default.LR2BackupNum = value;
                    }
                    RaisePropertyChanged("LR2BackupNum");
                }
            }
        }

        public bool UseExternalWebBrowser
        {
            get
            {
                return Settings.Default.UseExternalWebBrowser;
            }
            set
            {
                if (Settings.Default.UseExternalWebBrowser != value)
                {
                    Settings.Default.UseExternalWebBrowser = value;
                    RaisePropertyChanged("UseExternalWebBrowser");
                }
            }
        }

        public bool UseExternalPanelImage
        {
            get
            {
                return Settings.Default.UseExternalPanelImage;
            }
            set
            {
                if (Settings.Default.UseExternalPanelImage != value)
                {
                    Settings.Default.UseExternalPanelImage = value;
                    RaisePropertyChanged("UseExternalPanelImage");
                }
            }
        }

        public bool ShowScoreViewerRegisterConfirmMsg
        {
            get
            {
                return Settings.Default.ShowScoreViewerRegisterConfirmMsg;
            }
            set
            {
                if (Settings.Default.ShowScoreViewerRegisterConfirmMsg != value)
                {
                    Settings.Default.ShowScoreViewerRegisterConfirmMsg = value;
                    RaisePropertyChanged("ShowScoreViewerRegisterConfirmMsg");
                }
            }
        }

        public bool ShowDiffBMSInstallConfirmMsg
        {
            get
            {
                return Settings.Default.ShowDiffBMSInstallConfirmMsg;
            }
            set
            {
                if (Settings.Default.ShowDiffBMSInstallConfirmMsg != value)
                {
                    Settings.Default.ShowDiffBMSInstallConfirmMsg = value;
                    RaisePropertyChanged("ShowDiffBMSInstallConfirmMsg");
                }
            }
        }

        public bool ShowRecommUpdatedMsg
        {
            get
            {
                return Settings.Default.ShowRecommUpdatedMsg;
            }
            set
            {
                if (Settings.Default.ShowRecommUpdatedMsg != value)
                {
                    Settings.Default.ShowRecommUpdatedMsg = value;
                    RaisePropertyChanged("ShowRecommUpdatedMsg");
                }
            }
        }

        public bool SkipInitFileCheck
        {
            get
            {
                return Settings.Default.SkipInitFileCheck;
            }
            set
            {
                if (Settings.Default.SkipInitFileCheck == value)
                {
                    return;
                }
                if (value)
                {
                    ConfirmationMessage confirmationMessage = new ConfirmationMessage("起動時にBMSファイルと構成ファイルの変更チェックを行いません" + Environment.NewLine + "初期化が高速化されますが、手動でフォルダのリロードを行わないと" + Environment.NewLine + "新しく追加した曲の認識やインストール先の推定が出来ません" + Environment.NewLine + Environment.NewLine + "本機能はテスト実装中です" + Environment.NewLine + "再起動後に変更が反映されます", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
                    ownerViewModel.Messenger.Raise(confirmationMessage);
                    if (confirmationMessage.Response != true)
                    {
                        return;
                    }
                }
                Settings.Default.SkipInitFileCheck = value;
                RaisePropertyChanged("SkipInitFileCheck");
            }
        }

        public bool SkipInitPlaylistLoad
        {
            get
            {
                return Settings.Default.SkipInitPlaylistLoad;
            }
            set
            {
                if (Settings.Default.SkipInitPlaylistLoad == value)
                {
                    return;
                }
                if (value)
                {
                    ConfirmationMessage confirmationMessage = new ConfirmationMessage("起動時にプレイリストの更新チェックを行いません" + Environment.NewLine + Environment.NewLine + "本機能はテスト実装中です" + Environment.NewLine + "再起動後に変更が反映されます", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
                    ownerViewModel.Messenger.Raise(confirmationMessage);
                    if (confirmationMessage.Response != true)
                    {
                        return;
                    }
                }
                Settings.Default.SkipInitPlaylistLoad = value;
                RaisePropertyChanged("SkipInitPlaylistLoad");
            }
        }

        public bool StartupSelectInstallPending
        {
            get
            {
                return Settings.Default.StartupSelectInstallPending;
            }
            set
            {
                if (Settings.Default.StartupSelectInstallPending != value)
                {
                    Settings.Default.StartupSelectInstallPending = value;
                    RaisePropertyChanged("StartupSelectInstallPending");
                }
            }
        }

        public bool StartupExpandPlaylistTree
        {
            get
            {
                return Settings.Default.StartupExpandPlaylistTree;
            }
            set
            {
                if (Settings.Default.StartupExpandPlaylistTree != value)
                {
                    Settings.Default.StartupExpandPlaylistTree = value;
                    RaisePropertyChanged("StartupExpandPlaylistTree");
                }
            }
        }

        public bool EnableReadOptimizedPragmas
        {
            get
            {
                return Settings.Default.EnableReadOptimizedPragmas;
            }
            set
            {
                if (Settings.Default.EnableReadOptimizedPragmas != value)
                {
                    Settings.Default.EnableReadOptimizedPragmas = value;
                    RaisePropertyChanged("EnableReadOptimizedPragmas");
                }
            }
        }

        public bool SkipEstimateOfflineScoreRanking
        {
            get
            {
                return Settings.Default.SkipEstimateOfflineScoreRanking;
            }
            set
            {
                if (Settings.Default.SkipEstimateOfflineScoreRanking == value)
                {
                    return;
                }
                if (value)
                {
                    ConfirmationMessage confirmationMessage = new ConfirmationMessage("起動後に未送信スコアのランキング推定を行いません" + Environment.NewLine + "起動後のCPU/DISK使用率が低下しますが、" + Environment.NewLine + "スコア未送信楽曲のランキングが表示されなくなります" + Environment.NewLine + Environment.NewLine + "本機能はテスト実装中です" + Environment.NewLine + "再起動後に変更が反映されます", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
                    ownerViewModel.Messenger.Raise(confirmationMessage);
                    if (confirmationMessage.Response != true)
                    {
                        return;
                    }
                }
                Settings.Default.SkipEstimateOfflineScoreRanking = value;
                RaisePropertyChanged("SkipEstimateOfflineScoreRanking");
            }
        }

        public bool EnableAutoInstall
        {
            get
            {
                return Settings.Default.AutoInstall;
            }
            set
            {
                if (Settings.Default.AutoInstall != value)
                {
                    Settings.Default.AutoInstall = value;
                    RaisePropertyChanged("EnableAutoInstall");
                }
            }
        }

        public bool KeepInstallablePackagesPending
        {
            get
            {
                return Settings.Default.KeepInstallablePackagesPending;
            }
            set
            {
                if (Settings.Default.KeepInstallablePackagesPending != value)
                {
                    Settings.Default.KeepInstallablePackagesPending = value;
                    RaisePropertyChanged("KeepInstallablePackagesPending");
                }
            }
        }

        public bool UseEverythingForPendingPackageSourceScan
        {
            get
            {
                return Settings.Default.UseEverythingForPendingPackageSourceScan;
            }
            set
            {
                if (Settings.Default.UseEverythingForPendingPackageSourceScan != value)
                {
                    Settings.Default.UseEverythingForPendingPackageSourceScan = value;
                    RaisePropertyChanged("UseEverythingForPendingPackageSourceScan");
                }
            }
        }

        public bool AutoApplyAmbiguousInstallDestination
        {
            get
            {
                return Settings.Default.AutoApplyAmbiguousInstallDestination;
            }
            set
            {
                if (Settings.Default.AutoApplyAmbiguousInstallDestination != value)
                {
                    Settings.Default.AutoApplyAmbiguousInstallDestination = value;
                    RaisePropertyChanged("AutoApplyAmbiguousInstallDestination");
                }
            }
        }

        /// <summary>
        /// 推定先への通常インストール後に、元の保留パッケージフォルダを残り物ごと削除するかどうかを取得または設定します。
        /// </summary>
        /// <remarks>
        /// 既定値は false で、既所持譜面が残る場合は従来どおり source フォルダを残します。
        /// </remarks>
        public bool DeletePendingPackageSourceAfterInstall
        {
            get
            {
                return Settings.Default.DeletePendingPackageSourceAfterInstall;
            }
            set
            {
                if (Settings.Default.DeletePendingPackageSourceAfterInstall != value)
                {
                    Settings.Default.DeletePendingPackageSourceAfterInstall = value;
                    RaisePropertyChanged("DeletePendingPackageSourceAfterInstall");
                }
            }
        }

        public bool EnableSmartComponentOverwrite
        {
            get
            {
                return Settings.Default.EnableSmartComponentOverwrite;
            }
            set
            {
                if (Settings.Default.EnableSmartComponentOverwrite != value)
                {
                    Settings.Default.EnableSmartComponentOverwrite = value;
                    RaisePropertyChanged("EnableSmartComponentOverwrite");
                }
            }
        }

        public bool KeepSmartOverwriteProtectedFilesByRenaming
        {
            get
            {
                return Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming;
            }
            set
            {
                if (Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming != value)
                {
                    Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming = value;
                    RaisePropertyChanged("KeepSmartOverwriteProtectedFilesByRenaming");
                }
            }
        }

        public string StagefilePath
        {
            get
            {
                if (!IsStagefilePathValid())
                {
                    Settings.Default.StagefilePath = null;
                }
                return Settings.Default.StagefilePath;
            }
            set
            {
                if (!(Settings.Default.StagefilePath == value))
                {
                    if (IsStagefilePathValid(value))
                    {
                        Settings.Default.StagefilePath = value;
                    }
                    RaisePropertyChanged("StagefilePath");
                }
            }
        }

        public string FolderNameFormat
        {
            get
            {
                if (!IsFolderNameFormatValid())
                {
                    Settings.Default.FolderNameFormat = defaultFolderNameFormat;
                }
                return Settings.Default.FolderNameFormat;
            }
            set
            {
                if (!(Settings.Default.FolderNameFormat == value))
                {
                    value = value.RemoveInvalidFileNameChars();
                    if (IsFolderNameFormatValid(value))
                    {
                        Settings.Default.FolderNameFormat = value;
                    }
                    else
                    {
                        Settings.Default.FolderNameFormat = defaultFolderNameFormat;
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("リネーム書式には「%ARTIST%」または「%TITLE%」を含めて下さい", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    RaisePropertyChanged("FolderNameFormat");
                }
            }
        }

        public bool UseOnlyShiftJISChars
        {
            get
            {
                return Settings.Default.UseOnlyShiftJISChars;
            }
            set
            {
                if (Settings.Default.UseOnlyShiftJISChars != value)
                {
                    if (!value)
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("チェックを外すと、LR2が正常に起動しなかったり" + Environment.NewLine + "作成されたフォルダが認識できなくなる場合があります", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    Settings.Default.UseOnlyShiftJISChars = value;
                    RaisePropertyChanged("UseOnlyShiftJISChars");
                }
            }
        }

        public ReadOnlyDictionary<SampleRate, string> PlayerSampleRateNames { get; } = new ReadOnlyDictionary<SampleRate, string>(new Dictionary<SampleRate, string>
        {
            {
                SampleRate.AUTO,
                "Auto"
            },
            {
                SampleRate.SAMPLE_RATE_11025Hz,
                "11025Hz"
            },
            {
                SampleRate.SAMPLE_RATE_22050Hz,
                "22050Hz"
            },
            {
                SampleRate.SAMPLE_RATE_32000Hz,
                "32000Hz"
            },
            {
                SampleRate.SAMPLE_RATE_44100Hz,
                "44100Hz"
            },
            {
                SampleRate.SAMPLE_RATE_48000Hz,
                "48000Hz"
            },
            {
                SampleRate.SAMPLE_RATE_88200Hz,
                "88200Hz"
            },
            {
                SampleRate.SAMPLE_RATE_96000Hz,
                "96000Hz"
            },
            {
                SampleRate.SAMPLE_RATE_176400Hz,
                "176400Hz"
            },
            {
                SampleRate.SAMPLE_RATE_192000Hz,
                "192000Hz"
            }
        });

        public SampleRate EncoderSampleRate
        {
            get
            {
                return Settings.Default.EncoderSampleRate;
            }
            set
            {
                if (Settings.Default.EncoderSampleRate != value)
                {
                    Settings.Default.EncoderSampleRate = value;
                    RaisePropertyChanged("EncoderSampleRate");
                }
            }
        }

        public ReadOnlyObservableCollection<string> EncoderNames => new ReadOnlyObservableCollection<string>(new ObservableCollection<string> { "WAVE", "MP3 LAME", "AAC Nero", "Opus", "FLAC", "Ogg Vorbis" });

        public int EncoderIndex
        {
            get
            {
                if (!EncoderChecker(Settings.Default.Encoder))
                {
                    Settings.Default.Encoder = EncoderType.WAVE;
                }
                return (int)Settings.Default.Encoder;
            }
            set
            {
                if (Settings.Default.Encoder != (EncoderType)value)
                {
                    if (EncoderChecker((EncoderType)value))
                    {
                        Settings.Default.Encoder = (EncoderType)value;
                    }
                    else
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("エンコード実行ファイル: " + ((EncoderType)value).GetEncoderFileName() + " が見つかりませんでした" + Environment.NewLine + "検索フォルダを正しく指定して下さい", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    RaisePropertyChanged("EncoderIndex");
                }
            }
        }

        public ReadOnlyDictionary<SampleFormat, string> PlayerFormatNames { get; } = new ReadOnlyDictionary<SampleFormat, string>(new Dictionary<SampleFormat, string>
        {
            {
                SampleFormat.AUTO,
                "Auto"
            },
            {
                SampleFormat.SAMPLE_INT_8BIT,
                "8bit"
            },
            {
                SampleFormat.SAMPLE_INT_16BIT,
                "16bit"
            },
            {
                SampleFormat.SAMPLE_INT_24BIT,
                "24bit"
            },
            {
                SampleFormat.SAMPLE_INT_32BIT,
                "32bit"
            },
            {
                SampleFormat.SAMPLE_FLOAT_32BIT,
                "32bit (IEEE Float)"
            }
        });

        public SampleFormat EncoderFormat
        {
            get
            {
                return Settings.Default.EncoderFormat;
            }
            set
            {
                if (Settings.Default.EncoderFormat != value)
                {
                    Settings.Default.EncoderFormat = value;
                    RaisePropertyChanged("EncoderFormat");
                }
            }
        }

        public ReadOnlyDictionary<BMSAutoPlayWriter.Normalization, string> EncoderNormalizationNames => new ReadOnlyDictionary<BMSAutoPlayWriter.Normalization, string>(new Dictionary<BMSAutoPlayWriter.Normalization, string>
        {
            {
                BMSAutoPlayWriter.Normalization.NONE,
                BeMusicSeeker.Properties.Resources.Record_setting_normalize_none
            },
            {
                BMSAutoPlayWriter.Normalization.PEAK_LEVEL,
                BeMusicSeeker.Properties.Resources.Record_setting_normalize_peak
            },
            {
                BMSAutoPlayWriter.Normalization.RMS_VALUE,
                BeMusicSeeker.Properties.Resources.Record_setting_normalize_average
            }
        });

        public BMSAutoPlayWriter.Normalization EncoderNormalization
        {
            get
            {
                return Settings.Default.EncoderNormalization;
            }
            set
            {
                if (Settings.Default.EncoderNormalization != value)
                {
                    Settings.Default.EncoderNormalization = value;
                    RaisePropertyChanged("EncoderNormalization");
                }
            }
        }

        public float EncoderQuality
        {
            get
            {
                return Settings.Default.EncoderQuality;
            }
            set
            {
                if (Settings.Default.EncoderQuality != value)
                {
                    Settings.Default.EncoderQuality = value;
                    RaisePropertyChanged("EncoderQuality");
                }
            }
        }

        public float EncoderAmplifier
        {
            get
            {
                return Settings.Default.EncoderAmplifier;
            }
            set
            {
                if (Settings.Default.EncoderAmplifier != value)
                {
                    Settings.Default.EncoderAmplifier = value;
                    RaisePropertyChanged("EncoderAmplifier");
                }
            }
        }

        public string EncoderExeDir
        {
            get
            {
                return Settings.Default.EncoderExeDir;
            }
            set
            {
                if (!(Settings.Default.EncoderExeDir == value))
                {
                    Settings.Default.EncoderExeDir = value;
                    RaisePropertyChanged("EncoderExeDir");
                    RaisePropertyChanged(() => EncoderIndex);
                }
            }
        }

        public string EncodeFileNameFormat
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Settings.Default.EncodeFileNameFormat))
                {
                    Settings.Default.EncodeFileNameFormat = defaultEncodeFileNameFormat;
                }
                return Settings.Default.EncodeFileNameFormat;
            }
            set
            {
                if (!(Settings.Default.EncodeFileNameFormat == value))
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        value = defaultEncodeFileNameFormat;
                    }
                    value = value.RemoveInvalidFileNameChars();
                    Settings.Default.EncodeFileNameFormat = value;
                    RaisePropertyChanged("EncodeFileNameFormat");
                }
            }
        }

        public ReadOnlyObservableCollection<string> PlayerDriverNames => new ReadOnlyObservableCollection<string>(new ObservableCollection<string>
        {
            "DirectSound",
            "WASAPI (" + BeMusicSeeker.Properties.Resources.Shared + ")",
            "WASAPI (" + BeMusicSeeker.Properties.Resources.Exclusive + ")",
            "ASIO"
        });

        public int PlayerDriverIndex
        {
            get
            {
                if (Settings.Default.PlayerDriver < BassAudioPlayer.DeviceDriver.DIRECT_SOUND || (int)Settings.Default.PlayerDriver >= PlayerDriverNames.Count)
                {
                    Settings.Default.PlayerDriver = BassAudioPlayer.DeviceDriver.DIRECT_SOUND;
                }
                return (int)Settings.Default.PlayerDriver;
            }
            set
            {
                if (Settings.Default.PlayerDriver != (BassAudioPlayer.DeviceDriver)value)
                {
                    if (value < 0 || value >= PlayerDriverNames.Count)
                    {
                        value = 0;
                    }
                    Settings.Default.PlayerDriver = (BassAudioPlayer.DeviceDriver)value;
                    playerDeviceNames = BassAudioPlayer.DeviceList[Settings.Default.PlayerDriver].ToList();
                    RaisePropertyChanged("PlayerDriverIndex");
                    RaisePropertyChanged(() => PlayerDeviceNames);
                    RaisePropertyChanged(() => PlayerDevice);
                }
            }
        }

        public List<BassAudioPlayer.DeviceDescriptor> PlayerDeviceNames
        {
            get
            {
                return playerDeviceNames ?? (playerDeviceNames = BassAudioPlayer.DeviceList[(BassAudioPlayer.DeviceDriver)PlayerDriverIndex].ToList());
            }
            private set
            {
                playerDeviceNames = value;
            }
        }

        public string PlayerDevice
        {
            get
            {
                BassAudioPlayer.DeviceDescriptor deviceDescriptor = PlayerDeviceNames.FirstOrDefault((BassAudioPlayer.DeviceDescriptor d) => d.Driver == Settings.Default.PlayerDevice);
                if (deviceDescriptor.Driver == null)
                {
                    deviceDescriptor = PlayerDeviceNames.FirstOrDefault((BassAudioPlayer.DeviceDescriptor d) => d.Name == Settings.Default.PlayerDeviceName);
                    if (deviceDescriptor.Driver == null)
                    {
                        Match match = Regex.Match(Settings.Default.PlayerDeviceName ?? string.Empty, "(.*?)[\\s(\\d-]+(.*?)\\)");
                        if (match.Success)
                        {
                            string p1 = match.Groups[1].ToString();
                            string p2 = match.Groups[2].ToString();
                            deviceDescriptor = PlayerDeviceNames.FirstOrDefault((BassAudioPlayer.DeviceDescriptor d) => Regex.IsMatch(d.Name ?? string.Empty, p1 + ".*" + p2));
                            Console.WriteLine(deviceDescriptor.Name);
                        }
                    }
                }
                Settings.Default.PlayerDevice = deviceDescriptor.Driver;
                Settings.Default.PlayerDeviceName = deviceDescriptor.Name;
                return Settings.Default.PlayerDevice;
            }
            set
            {
                if (!(Settings.Default.PlayerDevice == value))
                {
                    BassAudioPlayer.DeviceDescriptor deviceDescriptor = PlayerDeviceNames.FirstOrDefault((BassAudioPlayer.DeviceDescriptor d) => d.Driver == value);
                    Settings.Default.PlayerDevice = deviceDescriptor.Driver;
                    Settings.Default.PlayerDeviceName = deviceDescriptor.Name;
                    RaisePropertyChanged("PlayerDevice");
                }
            }
        }

        public SampleRate PlayerSampleRate
        {
            get
            {
                return Settings.Default.PlayerSampleRate;
            }
            set
            {
                if (Settings.Default.PlayerSampleRate != value)
                {
                    Settings.Default.PlayerSampleRate = value;
                    RaisePropertyChanged("PlayerSampleRate");
                }
            }
        }

        public SampleFormat PlayerFormat
        {
            get
            {
                return Settings.Default.PlayerFormat;
            }
            set
            {
                if (Settings.Default.PlayerFormat != value)
                {
                    Settings.Default.PlayerFormat = value;
                    RaisePropertyChanged("PlayerFormat");
                }
            }
        }

        public float PlayerBufferSize
        {
            get
            {
                return Settings.Default.PlayerBufferSize;
            }
            set
            {
                if (Settings.Default.PlayerBufferSize != value)
                {
                    Settings.Default.PlayerBufferSize = value;
                    RaisePropertyChanged("PlayerBufferSize");
                }
            }
        }

        public double PlayerLatency { get; private set; }

        public bool PlayerWASAPIParam
        {
            get
            {
                return Settings.Default.PlayerWASAPIParam;
            }
            set
            {
                if (Settings.Default.PlayerWASAPIParam != value)
                {
                    Settings.Default.PlayerWASAPIParam = value;
                    RaisePropertyChanged("PlayerWASAPIParam");
                }
            }
        }

        public ListenerCommand<FolderSelectionMessage> OpenRootFolderCommand
        {
            get
            {
                if (_OpenRootFolderCommand == null)
                {
                    _OpenRootFolderCommand = new ListenerCommand<FolderSelectionMessage>(OpenRootFolder);
                }
                return _OpenRootFolderCommand;
            }
        }

        public ListenerCommand<OpeningFileSelectionMessage> OpenFileCommand
        {
            get
            {
                if (_OpenFileCommand == null)
                {
                    _OpenFileCommand = new ListenerCommand<OpeningFileSelectionMessage>(OpenFile);
                }
                return _OpenFileCommand;
            }
        }

        public ListenerCommand<FolderSelectionMessage> OpenDirCommand
        {
            get
            {
                if (_OpenDirCommand == null)
                {
                    _OpenDirCommand = new ListenerCommand<FolderSelectionMessage>(OpenDir);
                }
                return _OpenDirCommand;
            }
        }

        public ListenerCommand<FolderSelectionMessage> AddBMSDirCommandFromMainWindow
        {
            get
            {
                if (_AddBMSDirCommandFromMainWindow == null)
                {
                    _AddBMSDirCommandFromMainWindow = new ListenerCommand<FolderSelectionMessage>(AddBMSDirectoryToRootFolderAndSave);
                }
                return _AddBMSDirCommandFromMainWindow;
            }
        }

        public ListenerCommand<FolderSelectionMessage> AddBMSDirCommand
        {
            get
            {
                if (_AddBMSDirCommand == null)
                {
                    _AddBMSDirCommand = new ListenerCommand<FolderSelectionMessage>(AddBMSDirectoryToLR2Config);
                }
                return _AddBMSDirCommand;
            }
        }

        public ListenerCommand<string> RemoveDirCommand
        {
            get
            {
                if (_RemoveDirCommand == null)
                {
                    _RemoveDirCommand = new ListenerCommand<string>(RemoveBMSDirectoryFromLR2Config);
                }
                return _RemoveDirCommand;
            }
        }

        public List<string> Languages => App.AvailableCultures.Keys.ToList();

        public string Language
        {
            get
            {
                // 表示名が保存されている場合はそれを優先（同一カルチャ名の重複対策）
                string savedDisplayName = Settings.Default.LangDisplayName;
                if (!string.IsNullOrEmpty(savedDisplayName) && App.AvailableCultures.ContainsKey(savedDisplayName))
                    return savedDisplayName;
                return App.AvailableCultures.FirstOrDefault((KeyValuePair<string, string> kv) => kv.Value == Settings.Default.Lang).Key;
            }
            set
            {
                try
                {
                    string text = App.AvailableCultures[value];
                    Settings.Default.Lang = text;
                    Settings.Default.LangDisplayName = value;
                    ResourceService.Current.ChangeCulture(text);
                    RaisePropertyChanged("Language");
                }
                catch
                {
                }
            }
        }

        public SettingDialogViewModel(MainWindowViewModel owner)
        {
            SettingDialogViewModel settingDialogViewModel = this;
            ownerViewModel = owner;
            ownerViewModelEventListener = new PropertyChangedEventListener(ownerViewModel);
            ownerViewModelEventListener.RegisterHandler(() => owner.BMSTables, delegate
            {
                settingDialogViewModel.RaisePropertyChanged(() => settingDialogViewModel.LR2ConfigBMSDirectories);
            });
            resourceServiceEventListener = new PropertyChangedEventListener(ResourceService.Current);
            resourceServiceEventListener.RegisterHandler(() => ResourceService.Current.Resources, delegate
            {
                settingDialogViewModel.RaisePropertyChanged(() => settingDialogViewModel.EncoderNormalizationNames);
            });
            resourceServiceEventListener.RegisterHandler(() => ResourceService.Current.Resources, delegate
            {
                settingDialogViewModel.RaisePropertyChanged(() => settingDialogViewModel.EncoderNormalization);
            });
            Settings.Default.Reload();
            try
            {
                lr2config = new LR2Config(Settings.Default.LR2ConfigXmlPath);
            }
            catch
            {
                Settings.Default.LR2ConfigXmlPath = null;
                lr2config = null;
            }
            backupSavedSettings();
        }

        private bool IsLR2RootPathValid()
        {
            return IsLR2RootPathValid(Settings.Default.LR2RootPath);
        }

        private bool IsLR2RootPathValid(string value)
        {
            if (value == null)
            {
                return false;
            }
            string path = Path.Combine(value, "LR2body.exe");
            string path2 = Path.Combine(value, "LRHbody.exe");
            string value2 = Path.Combine(value, "LR2files\\Database\\song.db");
            string value3 = Path.Combine(value, "LR2files\\Config\\config.xml");
            string value4 = Path.Combine(value, "LR2files\\Config\\config.xmh");
            if (Directory.Exists(value) && IsLR2SongDBPathValid(value2) && (IsLR2ConfigXmlPathValid(value3) || IsLR2ConfigXmlPathValid(value4)))
            {
                if (!File.Exists(path))
                {
                    return File.Exists(path2);
                }
                return true;
            }
            return false;
        }

        private bool IsBMSRootPathValid()
        {
            return IsBMSRootPathValid(Settings.Default.BMSRootPath);
        }

        private bool IsBMSRootPathValid(string value)
        {
            return Directory.Exists(value);
        }

        private bool IsLR2SongDBPathValid()
        {
            return IsLR2SongDBPathValid(Settings.Default.LR2SongDBPath);
        }

        private bool IsLR2SongDBPathValid(string value)
        {
            return File.Exists(value);
        }

        private bool IsLR2ConfigXmlPathValid()
        {
            return IsLR2ConfigXmlPathValid(Settings.Default.LR2ConfigXmlPath);
        }

        private bool IsLR2ConfigXmlPathValid(string value)
        {
            return File.Exists(value);
        }

        private bool IsuBMplayPathValid()
        {
            return IsuBMplayPathValid(Settings.Default.uBMplayPath);
        }

        private bool IsuBMplayPathValid(string value)
        {
            return File.Exists(value);
        }

        private bool IsBMIIDXViewPathValid()
        {
            return IsuBMplayPathValid(Settings.Default.BMIIDXViewPath);
        }

        private bool IsBMIIDXViewPathValid(string value)
        {
            return File.Exists(value);
        }

        private bool IsLR2CustomFolderOutputDirValid()
        {
            return IsLR2CustomFolderOutputDirValid(Settings.Default.LR2CustomFolderOutputBaseDir);
        }

        private bool IsLR2CustomFolderOutputDirValid(string value)
        {
            if (lr2config != null && value != null)
            {
                return lr2config.GetBMSSearchDirectories().Contains(value);
            }
            return false;
        }

        private bool IsBMSInstallDirValid()
        {
            return IsBMSInstallDirValid(Settings.Default.BMSInstallDir);
        }

        private bool IsBMSInstallDirValid(string value)
        {
            if (lr2config != null && value != null)
            {
                return lr2config.GetBMSSearchDirectories().Contains(value);
            }
            return false;
        }

        private bool IsLR2CustomFolderAsRootOutputDirValid()
        {
            return IsLR2CustomFolderAsRootOutputDirValid(Settings.Default.LR2CustomFolderOutputBaseDirRootType);
        }

        private bool IsLR2CustomFolderAsRootOutputDirValid(string value)
        {
            if (lr2config != null && value != null)
            {
                return lr2config.GetBMSSearchDirectories().All((string d) => !(value + Path.DirectorySeparatorChar).StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }
            return false;
        }

        private bool IsTableListURLValid()
        {
            return IsTableListURLValid(Settings.Default.TableListURL);
        }

        private bool IsTableListURLValid(Uri value)
        {
            return value != null && Uri.TryCreate(value.OriginalString, UriKind.RelativeOrAbsolute, out value);
        }

        private bool IsPlaylistMd5UrlMappingTsvUriValid()
        {
            return IsPlaylistMd5UrlMappingTsvUriValid(Settings.Default.PlaylistMd5UrlMappingTsvUri);
        }

        private bool IsPlaylistMd5UrlMappingTsvUriValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
            return PlaylistUrlCompletionSupport.TryResolveSourceUri(value, out var _);
        }

        private bool IsLR2BackupPathValid()
        {
            return IsLR2BackupPathValid(Settings.Default.LR2BackupPath);
        }

        private bool IsLR2BackupPathValid(string value)
        {
            return Directory.Exists(value);
        }

        private bool IsLR2BackupSpanValid()
        {
            return IsLR2BackupSpanValid(Settings.Default.LR2BackupSpan);
        }

        private bool IsLR2BackupSpanValid(int value)
        {
            return value > 0;
        }

        private bool IsLR2BackupNumValid()
        {
            return IsLR2BackupNumValid(Settings.Default.LR2BackupNum);
        }

        private bool IsLR2BackupNumValid(int value)
        {
            return value > 0;
        }

        private bool IsStagefilePathValid()
        {
            return IsStagefilePathValid(Settings.Default.StagefilePath);
        }

        private bool IsStagefilePathValid(string value)
        {
            return File.Exists(value);
        }

        private bool IsFolderNameFormatValid()
        {
            return IsFolderNameFormatValid(Settings.Default.FolderNameFormat);
        }

        private bool IsFolderNameFormatValid(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (!value.Contains("%ARTIST%"))
                {
                    return value.Contains("%TITLE%");
                }
                return true;
            }
            return false;
        }

        private bool EncoderChecker(EncoderType encoder)
        {
            string encoderDirectory = BassAudioWriter.EncoderDirectory;
            BassAudioWriter.EncoderDirectory = EncoderExeDir;
            bool result = BassAudioWriter.IsEncoderAvailable(encoder);
            BassAudioWriter.EncoderDirectory = encoderDirectory;
            return result;
        }

        public void OpenRootFolder(FolderSelectionMessage parameter)
        {
            if (parameter.Response == null)
            {
                return;
            }
            string response = parameter.Response;
            try
            {
                if (!response.IsSjisSchemeString())
                {
                    throw new ArgumentException("パスにユニコード文字が含まれているためLR2で認識できません");
                }
                string name = parameter.MessageKey.Substring(parameter.MessageKey.LastIndexOf('.') + 1);
                GetType().GetProperty(name).GetSetMethod().Invoke(this, new object[1] { response });
            }
            catch (ArgumentException ex)
            {
                ownerViewModel.Messenger.Raise(new ConfirmationMessage(ex.Message, "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            }
            catch
            {
            }
        }

        private void OpenFile(OpeningFileSelectionMessage parameter)
        {
            if (parameter.Response != null)
            {
                string name = parameter.MessageKey.Substring(parameter.MessageKey.LastIndexOf('.') + 1);
                GetType().GetProperty(name).GetSetMethod().Invoke(this, new object[1] { parameter.Response.FirstOrDefault() });
            }
        }

        private void OpenDir(FolderSelectionMessage parameter)
        {
            if (parameter.Response != null)
            {
                string name = parameter.MessageKey.Substring(parameter.MessageKey.LastIndexOf('.') + 1);
                GetType().GetProperty(name).GetSetMethod().Invoke(this, new object[1] { parameter.Response });
            }
        }

        private void AddBMSDirectoryToRootFolderAndSave(FolderSelectionMessage parameter)
        {
            if (parameter.Response == null)
            {
                return;
            }
            _ = parameter.Response;
            if (Settings.Default.OperationModeLR2DB)
            {
                AddBMSDirectoryToLR2Config(parameter);
                if (isBMSDirectoryAdded)
                {
                    ownerViewModel.ReloadFiles();
                    isBMSDirectoryAdded = false;
                }
                else
                {
                    ownerViewModel.NotifyBmsParentFolderListChanged();
                }
                return;
            }
            throw new NotImplementedException();
        }

        private void AddBMSDirectoryToLR2Config(FolderSelectionMessage parameter)
        {
            if (lr2config == null || parameter.Response == null)
            {
                return;
            }
            string response = parameter.Response;
            try
            {
                if (!response.IsSjisSchemeString())
                {
                    throw new ArgumentException("パスにユニコード文字が含まれているためLR2で認識できません");
                }
                lr2config.AddBMSSearchDirectories(new string[1] { response });
                isBMSDirectoryAdded = Directory.EnumerateFiles(response, "*", System.IO.SearchOption.AllDirectories).Any((string path) => BeMusicSeeker.Models.BMSFile.bmsExtensions.Any((string ext) => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                if (!string.IsNullOrWhiteSpace(parameter.MessageKey))
                {
                    string name = parameter.MessageKey.Substring(parameter.MessageKey.LastIndexOf('.') + 1);
                    GetType().GetProperty(name).GetSetMethod().Invoke(this, new object[1] { response });
                }
                lr2config.Save();
            }
            catch (ArgumentException ex)
            {
                ownerViewModel.Messenger.Raise(new ConfirmationMessage(ex.Message, "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            }
            catch
            {
            }
        }

        public void RemoveBMSDirectoryFromRootFolderAndSave(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }
            if (Settings.Default.OperationModeLR2DB)
            {
                RemoveBMSDirectoryFromLR2Config(dir);
                if (isBMSDirectoryRemoved)
                {
                    ownerViewModel.ReloadFiles();
                    isBMSDirectoryRemoved = false;
                }
                else
                {
                    ownerViewModel.NotifyBmsParentFolderListChanged();
                }
                return;
            }
            throw new NotImplementedException();
        }

        private void RemoveBMSDirectoryFromLR2Config(string dir)
        {
            if (dir == null)
            {
                throw new ArgumentNullException();
            }
            try
            {
                if (LR2CustomFolderOutputDir != null && (LR2CustomFolderOutputDir + Path.DirectorySeparatorChar).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("カスタムフォルダ出力ディレクトリの登録解除は出来ません");
                }
                if (LR2CustomFolderAsRootOutputDir != null && (LR2CustomFolderAsRootOutputDir + Path.DirectorySeparatorChar).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("カスタムフォルダ(ルート)出力ディレクトリの登録解除は出来ません");
                }
                if (BMSInstallDir != null && (BMSInstallDir + Path.DirectorySeparatorChar).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("BMSインストール先ディレクトリの登録解除は出来ません");
                }
                if (lr2config.RemoveBMSSearchDirectories(new string[1] { dir }))
                {
                    lr2config.Save();
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                    if (ownerViewModel.BMSFiles != null && ownerViewModel.BMSFiles.Any((BeMusicSeeker.Models.BMSFile f) => f.path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        isBMSDirectoryRemoved = true;
                    }
                }
            }
            catch (Exception ex)
            {
                ownerViewModel.Messenger.Raise(new ConfirmationMessage(ex.Message, "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            }
        }

        public void AudioPlayerInitTest(bool playSound = true)
        {
            ownerViewModel.PlayEndBMSFile(closeProcess: true);
            BassAudioPlayer.DeviceDescriptor desc = (string.IsNullOrWhiteSpace(Settings.Default.PlayerDevice) ? default(BassAudioPlayer.DeviceDescriptor) : new BassAudioPlayer.DeviceDescriptor(Settings.Default.PlayerDeviceName, Settings.Default.PlayerDevice));
            BassAudioPlayer.Frequency = Settings.Default.PlayerSampleRate;
            BassAudioPlayer.Format = Settings.Default.PlayerFormat;
            BassAudioPlayer.DeviceVolume = (float)Math.Min(100, Math.Max(0, Settings.Default.uBMplayVolume)) / 100f;
            desc = BassAudioPlayer.Initialize(Settings.Default.PlayerDriver, desc, Settings.Default.PlayerBufferSize, Settings.Default.PlayerWASAPIParam);
            Settings.Default.PlayerDriver = BassAudioPlayer.DriverType;
            if (BassAudioPlayer.DriverType < BassAudioPlayer.DeviceDriver.DIRECT_SOUND)
            {
                Settings.Default.PlayerDriver = BassAudioPlayer.DeviceDriver.DIRECT_SOUND;
                NLogWrapper.TraceLogger.Warn("Sound device not found?");
            }
            Settings.Default.PlayerDevice = desc.Driver;
            Settings.Default.PlayerDeviceName = desc.Name;
            Settings.Default.PlayerSampleRate = BassAudioPlayer.Frequency;
            Settings.Default.PlayerFormat = BassAudioPlayer.Format;
            PlayerLatency = BassAudioPlayer.Latency;
            if (playSound)
            {
                string text = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "test.mp3");
                if (File.Exists(text))
                {
                    BassAudioPlayer bassAudioPlayer = null;
                    try
                    {
                        bassAudioPlayer = new BassAudioPlayer(text);
                        bassAudioPlayer.Play();
                        int num = 0;
                        while (bassAudioPlayer.PlayState != PlayState.Stopped || bassAudioPlayer.CurrentTime == TimeSpan.Zero)
                        {
                            Thread.Sleep(100);
                            num++;
                            if (num == 100)
                            {
                                throw new Exception("No response from sound device");
                            }
                        }
                    }
                    catch (Exception value)
                    {
                        NLogWrapper.TraceLogger.Warn(value);
                    }
                    finally
                    {
                        bassAudioPlayer?.Dispose();
                    }
                }
            }
            BassAudioPlayer.Free();
            RaisePropertyChanged(() => PlayerDriverIndex);
            RaisePropertyChanged(() => PlayerDeviceNames);
            RaisePropertyChanged(() => PlayerDevice);
            RaisePropertyChanged(() => PlayerSampleRate);
            RaisePropertyChanged(() => PlayerFormat);
            RaisePropertyChanged(() => PlayerLatency);
        }

        private void backupSavedSettings()
        {
            tempValidation = CheckValidation();
            tempOperationModeLR2DB = Settings.Default.OperationModeLR2DB;
            tempLR2RootPath = Settings.Default.LR2RootPath;
            tempLR2SongDBPath = Settings.Default.LR2SongDBPath;
            tempLR2ConfigXmlPath = Settings.Default.LR2ConfigXmlPath;
            tempBMSRootPath = Settings.Default.BMSRootPath;
            tempuBMplayPath = Settings.Default.uBMplayPath;
            tempBMIIDXViewPath = Settings.Default.BMIIDXViewPath;
            tempUsePlayeruBMplay = Settings.Default.UsePlayeruBMplay;
            tempUsePlayerLR2body = Settings.Default.UsePlayerLR2body;
            tempUsePlayerBMIIDXView = Settings.Default.UsePlayerBMIIDXView;
            tempLR2bodyResolution = Settings.Default.LR2bodyResolution;
            tempIsSaveLR2bodyWindowPosition = Settings.Default.IsSaveLR2bodyWindowPosition;
            tempLR2CustomFolderOutputDir = Settings.Default.LR2CustomFolderOutputBaseDir;
            tempLR2CustomFolderAsRootOutputDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
            tempBMSInstallDir = Settings.Default.BMSInstallDir;
            tempTableListURL = Settings.Default.TableListURL;
            tempEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
            tempOverwritePlaylistUrlsWithCompletion = Settings.Default.OverwritePlaylistUrlsWithCompletion;
            tempPlaylistMd5UrlMappingTsvUri = Settings.Default.PlaylistMd5UrlMappingTsvUri;
            tempIsLR2BackupEnabled = Settings.Default.IsLR2BackupEnabled;
            tempLR2BackupPath = Settings.Default.LR2BackupPath;
            tempLR2BackupTarget = Settings.Default.LR2BackupTarget;
            tempLR2BackupSpan = Settings.Default.LR2BackupSpan;
            tempLR2BackupNum = Settings.Default.LR2BackupNum;
            tempUseExternalWebBrowser = Settings.Default.UseExternalWebBrowser;
            tempUseExternalPanelImage = Settings.Default.UseExternalPanelImage;
            tempStagefilePath = Settings.Default.StagefilePath;
            tempFolderNameFormat = Settings.Default.FolderNameFormat;
            tempUseOnlyShiftJISChars = Settings.Default.UseOnlyShiftJISChars;
            tempShowScoreViewerRegisterConfirmMsg = Settings.Default.ShowScoreViewerRegisterConfirmMsg;
            tempShowDiffBMSInstallConfirmMsg = Settings.Default.ShowDiffBMSInstallConfirmMsg;
            tempShowRecommUpdatedMsg = Settings.Default.ShowRecommUpdatedMsg;
            tempSkipInitFileCheck = Settings.Default.SkipInitFileCheck;
            tempSkipInitPlaylistLoad = Settings.Default.SkipInitPlaylistLoad;
            tempStartupSelectInstallPending = Settings.Default.StartupSelectInstallPending;
            tempStartupExpandPlaylistTree = Settings.Default.StartupExpandPlaylistTree;
            tempEnableReadOptimizedPragmas = Settings.Default.EnableReadOptimizedPragmas;
            tempSkipEstimateOfflineScoreRanking = Settings.Default.SkipEstimateOfflineScoreRanking;
            tempEnableAutoInstall = Settings.Default.AutoInstall;
            tempKeepInstallablePackagesPending = Settings.Default.KeepInstallablePackagesPending;
            tempUseEverythingForPendingPackageSourceScan = Settings.Default.UseEverythingForPendingPackageSourceScan;
            tempAutoApplyAmbiguousInstallDestination = Settings.Default.AutoApplyAmbiguousInstallDestination;
            tempDeletePendingPackageSourceAfterInstall = Settings.Default.DeletePendingPackageSourceAfterInstall;
            tempEnableSmartComponentOverwrite = Settings.Default.EnableSmartComponentOverwrite;
            tempKeepSmartOverwriteProtectedFilesByRenaming = Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming;
            tempEncoderSampleRate = Settings.Default.EncoderSampleRate;
            tempEncoderIndex = (int)Settings.Default.Encoder;
            tempEncoderFormat = Settings.Default.EncoderFormat;
            tempEncoderNormalization = Settings.Default.EncoderNormalization;
            tempEncoderExeDir = Settings.Default.EncoderExeDir;
            tempEncoderAmplifier = Settings.Default.EncoderAmplifier;
            tempEncoderQuality = Settings.Default.EncoderQuality;
            tempEncodeFileNameFormat = Settings.Default.EncodeFileNameFormat;
            tempPlayerDriverIndex = (int)Settings.Default.PlayerDriver;
            tempPlayerDevice = Settings.Default.PlayerDevice;
            tempPlayerDeviceName = Settings.Default.PlayerDeviceName;
            tempPlayerSampleRate = Settings.Default.PlayerSampleRate;
            tempPlayerFormat = Settings.Default.PlayerFormat;
            tempPlayerBufferSize = Settings.Default.PlayerBufferSize;
            tempPlayerWASAPIParam = Settings.Default.PlayerWASAPIParam;
            tempLanguage = Settings.Default.Lang;
            isBMSDirectoryAdded = false;
            isBMSDirectoryRemoved = false;
        }

        private async Task necessaryStepsAfterSaved()
        {
            if (ownerViewModel.tables == null)
            {
                return;
            }
            if (Settings.Default.OperationModeLR2DB)
            {
                while (ownerViewModel.BMSTables == null)
                {
                    await Task.Delay(100);
                }
                await Task.Run(delegate
                {
                    if (!string.IsNullOrWhiteSpace(tempLR2CustomFolderOutputDir) && !string.IsNullOrWhiteSpace(Settings.Default.LR2CustomFolderOutputBaseDir) && tempLR2CustomFolderOutputDir != Settings.Default.LR2CustomFolderOutputBaseDir)
                    {
                        ownerViewModel.tables.ChangeCustomFolderBaseDirectory(tempLR2CustomFolderOutputDir, Settings.Default.LR2CustomFolderOutputBaseDir);
                    }
                    if (!string.IsNullOrWhiteSpace(tempLR2CustomFolderAsRootOutputDir) && !string.IsNullOrWhiteSpace(Settings.Default.LR2CustomFolderOutputBaseDirRootType) && tempLR2CustomFolderAsRootOutputDir != Settings.Default.LR2CustomFolderOutputBaseDirRootType)
                    {
                        ownerViewModel.tables.ChangeCustomFolderBaseDirectoryRoot(tempLR2CustomFolderAsRootOutputDir, Settings.Default.LR2CustomFolderOutputBaseDirRootType);
                        IEnumerable<string> second = from t in ownerViewModel.tables.BMSTables
                                                     where t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)
                                                     select Path.Combine(tempLR2CustomFolderAsRootOutputDir, t.Output_dir);
                        IEnumerable<string> second2 = from t in ownerViewModel.tables.BMSTables
                                                      where t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)
                                                      select Path.Combine(Settings.Default.LR2CustomFolderOutputBaseDirRootType, t.Output_dir) into d
                                                      where Directory.Exists(d)
                                                      select d;
                        IEnumerable<string> bMSSearchDirectories = lr2config.GetBMSSearchDirectories().Except(second, StringComparer.OrdinalIgnoreCase).Concat(second2)
                            .Distinct();
                        lr2config.SetBMSSearchDirectories(bMSSearchDirectories);
                        lr2config.Save();
                    }
                }).Logging("necessaryStepsAfterSaved");
            }
            if ((!Settings.Default.UsePlayeruBMplay && tempUsePlayeruBMplay != Settings.Default.UsePlayeruBMplay) || (!Settings.Default.UsePlayerBMIIDXView && tempUsePlayerBMIIDXView != Settings.Default.UsePlayerBMIIDXView) || (!Settings.Default.UsePlayerLR2body && tempUsePlayerLR2body != Settings.Default.UsePlayerLR2body))
            {
                ownerViewModel.PlayEndBMSFile(closeProcess: true);
                ownerViewModel.bmsPlayer = new InternalBMSAutoPlayerSoundOnly();
                ownerViewModel.Messenger.Raise(new InteractionMessage("InitializationSuccess"));
            }
            else if (Settings.Default.UsePlayeruBMplay && tempUsePlayeruBMplay != Settings.Default.UsePlayeruBMplay)
            {
                ownerViewModel.PlayEndBMSFile(closeProcess: true);
                ownerViewModel.bmsPlayer = new uBMplay(uBMplayPath);
                ownerViewModel.Messenger.Raise(new InteractionMessage("InitializationSuccess"));
            }
            else if (Settings.Default.UsePlayerLR2body && tempUsePlayerLR2body != Settings.Default.UsePlayerLR2body)
            {
                ownerViewModel.PlayEndBMSFile(closeProcess: true);
                ownerViewModel.bmsPlayer = new LR2body(LR2bodyPath, lr2config);
                ownerViewModel.Messenger.Raise(new InteractionMessage("InitializationSuccess"));
            }
            else if (Settings.Default.UsePlayerBMIIDXView && tempUsePlayerBMIIDXView != Settings.Default.UsePlayerBMIIDXView)
            {
                ownerViewModel.PlayEndBMSFile(closeProcess: true);
                ownerViewModel.bmsPlayer = new BMIIDXView2015(BMIIDXViewPath);
                ownerViewModel.Messenger.Raise(new InteractionMessage("InitializationSuccess"));
            }
            if (((Func<bool>)delegate
            {
                if (Settings.Default.PlayerDriver != (BassAudioPlayer.DeviceDriver)tempPlayerDriverIndex)
                {
                    return true;
                }
                if (Settings.Default.PlayerDevice != tempPlayerDevice || Settings.Default.PlayerSampleRate != tempPlayerSampleRate || Settings.Default.PlayerFormat != tempPlayerFormat)
                {
                    return true;
                }
                if (Settings.Default.PlayerBufferSize != tempPlayerBufferSize && (Settings.Default.PlayerDriver != BassAudioPlayer.DeviceDriver.WASAPI_SHARED || !Settings.Default.PlayerWASAPIParam))
                {
                    return true;
                }
                return (Settings.Default.PlayerWASAPIParam != tempPlayerWASAPIParam && (Settings.Default.PlayerDriver == BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE || Settings.Default.PlayerDriver == BassAudioPlayer.DeviceDriver.WASAPI_SHARED)) ? true : false;
            })())
            {
                AudioPlayerInitTest(playSound: false);
            }
            if (Settings.Default.OperationModeLR2DB && Settings.Default.IsLR2BackupEnabled && tempIsLR2BackupEnabled != Settings.Default.IsLR2BackupEnabled)
            {
                ownerViewModel.Messenger.Raise(new ConfirmationMessage("LR2設定ファイルバックアップ機能は" + Environment.NewLine + "次回起動時から有効になります", "確認", MessageBoxImage.Asterisk, MessageBoxButton.OK, "ConfirmationDialog"));
            }
            if (tempEnablePlaylistUrlCompletion != Settings.Default.EnablePlaylistUrlCompletion || tempOverwritePlaylistUrlsWithCompletion != Settings.Default.OverwritePlaylistUrlsWithCompletion || !string.Equals(tempPlaylistMd5UrlMappingTsvUri, Settings.Default.PlaylistMd5UrlMappingTsvUri, StringComparison.Ordinal))
            {
                ownerViewModel.tables.SchedulePlaylistUrlCompletionRefresh("SettingDialog.SaveSettings");
            }
        }

        public bool CheckValidation()
        {
            string errMsg;
            return CheckValidation(out errMsg);
        }

        /// <summary>
        /// 現在画面に入力されている各種設定値（パスやオプションなど）が正しいフォーマットであり、
        /// アプリケーションから正常にアクセス可能かどうかを検証します。
        /// </summary>
        /// <param name="errMsg">検証エラーがあった場合、その理由を示すエラーメッセージが格納されます。</param>
        /// <returns>すべての設定が有効であると判定された場合は <c>true</c>。無効な項目が含まれる場合は <c>false</c>。</returns>
        public bool CheckValidation(out string errMsg)
        {
            bool result = true;
            errMsg = string.Empty;
            if (OperationModeLR2DB)
            {
                if (!IsLR2SongDBPathValid() || !IsLR2ConfigXmlPathValid())
                {
                    errMsg = errMsg + "一般: LR2のsong.dbまたはconfig.xmlのパスが正しくありません" + Environment.NewLine;
                    result = false;
                }
            }
            else if (!IsBMSRootPathValid())
            {
                errMsg = errMsg + "一般: BMSディレクトリのパスが正しくありません" + Environment.NewLine;
                result = false;
            }
            if (UseExternalPanelImage && !IsStagefilePathValid())
            {
                errMsg = errMsg + "一般: ステージファイルのパスが正しくありません" + Environment.NewLine;
                result = false;
            }
            if (UsePlayeruBMplay && !IsuBMplayPathValid())
            {
                errMsg = errMsg + "再生: uBMplay実行ファイルのパスが正しくありません" + Environment.NewLine;
                result = false;
            }
            else if (UsePlayerLR2body)
            {
                if (!IsLR2RootPathValid())
                {
                    errMsg = errMsg + "再生: LR2ディレクトリのパスが正しくありません" + Environment.NewLine;
                    result = false;
                }
                if (!File.Exists(LR2bodyPath))
                {
                    errMsg = errMsg + "再生: LR2実行ファイルが見つかりません: " + LR2bodyPath + Environment.NewLine;
                    result = false;
                }
                if ((int)LR2bodyResolution.X <= 0 || (int)LR2bodyResolution.Y <= 0)
                {
                    errMsg = errMsg + "再生: LR2ウィンドウサイズが正しくありません" + Environment.NewLine;
                    result = false;
                }
            }
            else if (UsePlayerBMIIDXView && !IsBMIIDXViewPathValid())
            {
                errMsg = errMsg + "再生: BMIIDXViewのパスが正しくありません" + Environment.NewLine;
                result = false;
            }
            if (OperationModeLR2DB)
            {
                if (string.IsNullOrWhiteSpace(LR2CustomFolderOutputDir))
                {
                    errMsg = errMsg + "プレイリスト: カスタムフォルダの出力パスが設定されていません" + Environment.NewLine;
                    result = false;
                }
                if (string.IsNullOrWhiteSpace(LR2CustomFolderAsRootOutputDir))
                {
                    errMsg = errMsg + "プレイリスト: カスタムフォルダ(ROOT)の出力パスが設定されていません" + Environment.NewLine;
                    result = false;
                }
            }
            if (string.IsNullOrWhiteSpace(TableListURL.ToString()))
            {
                errMsg = errMsg + "プレイリスト: 難易度表リスト取得URIが設定されていません" + Environment.NewLine;
                result = false;
            }
            if (EnablePlaylistUrlCompletion && !IsPlaylistMd5UrlMappingTsvUriValid())
            {
                errMsg = errMsg + "プレイリスト: " + BeMusicSeeker.Properties.Resources.Error_InvalidPlaylistMd5UrlMappingTsvUri + Environment.NewLine;
                result = false;
            }
            if (!IsBMSInstallDirValid())
            {
                errMsg = errMsg + "インストール: BMSインストール先が設定されていません" + Environment.NewLine;
                result = false;
            }
            if (!IsFolderNameFormatValid())
            {
                FolderNameFormat = defaultFolderNameFormat;
            }
            if (OperationModeLR2DB && IsLR2BackupEnabled && !IsLR2BackupPathValid())
            {
                errMsg = errMsg + "詳細: LR2バックアップ出力先が設定されていません" + Environment.NewLine;
                result = false;
            }
            return result;
        }

        /// <summary>
        /// 現在の設定ダイアログの入力状態を検証し、問題がなければ `Settings.Default` メモリ領域から
        /// 実際の永続化記憶域（または構成ファイル）へ保存し、必要な事後処理（バックアップなど）を実行します。
        /// </summary>
        public async Task SaveSettings()
        {
            if (CheckValidation())
            {
                Settings.Default.Save();
                await necessaryStepsAfterSaved();
                backupSavedSettings();
            }
        }

        public void ResetSettings()
        {
            Settings.Default.LR2ConfigXmlPath = tempLR2ConfigXmlPath;
            Settings.Default.OperationModeLR2DB = tempOperationModeLR2DB;
            Settings.Default.LR2RootPath = tempLR2RootPath;
            Settings.Default.LR2SongDBPath = tempLR2SongDBPath;
            Settings.Default.BMSRootPath = tempBMSRootPath;
            Settings.Default.uBMplayPath = tempuBMplayPath;
            Settings.Default.BMIIDXViewPath = tempBMIIDXViewPath;
            Settings.Default.UsePlayeruBMplay = tempUsePlayeruBMplay;
            Settings.Default.UsePlayerLR2body = tempUsePlayerLR2body;
            Settings.Default.UsePlayerBMIIDXView = tempUsePlayerBMIIDXView;
            Settings.Default.LR2CustomFolderOutputBaseDir = tempLR2CustomFolderOutputDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = tempLR2CustomFolderAsRootOutputDir;
            Settings.Default.BMSInstallDir = tempBMSInstallDir;
            Settings.Default.TableListURL = tempTableListURL;
            Settings.Default.EnablePlaylistUrlCompletion = tempEnablePlaylistUrlCompletion;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = tempOverwritePlaylistUrlsWithCompletion;
            Settings.Default.PlaylistMd5UrlMappingTsvUri = tempPlaylistMd5UrlMappingTsvUri;
            Settings.Default.LR2bodyResolution = tempLR2bodyResolution;
            Settings.Default.IsSaveLR2bodyWindowPosition = tempIsSaveLR2bodyWindowPosition;
            Settings.Default.IsLR2BackupEnabled = tempIsLR2BackupEnabled;
            Settings.Default.LR2BackupPath = tempLR2BackupPath;
            Settings.Default.LR2BackupTarget = tempLR2BackupTarget;
            Settings.Default.LR2BackupSpan = tempLR2BackupSpan;
            Settings.Default.LR2BackupNum = tempLR2BackupNum;
            Settings.Default.UseExternalWebBrowser = tempUseExternalWebBrowser;
            Settings.Default.UseExternalPanelImage = tempUseExternalPanelImage;
            Settings.Default.StagefilePath = tempStagefilePath;
            Settings.Default.FolderNameFormat = tempFolderNameFormat;
            Settings.Default.UseOnlyShiftJISChars = tempUseOnlyShiftJISChars;
            Settings.Default.ShowScoreViewerRegisterConfirmMsg = tempShowScoreViewerRegisterConfirmMsg;
            Settings.Default.ShowDiffBMSInstallConfirmMsg = tempShowDiffBMSInstallConfirmMsg;
            Settings.Default.ShowRecommUpdatedMsg = tempShowRecommUpdatedMsg;
            Settings.Default.SkipInitFileCheck = tempSkipInitFileCheck;
            Settings.Default.SkipInitPlaylistLoad = tempSkipInitPlaylistLoad;
            Settings.Default.StartupSelectInstallPending = tempStartupSelectInstallPending;
            Settings.Default.StartupExpandPlaylistTree = tempStartupExpandPlaylistTree;
            Settings.Default.EnableReadOptimizedPragmas = tempEnableReadOptimizedPragmas;
            Settings.Default.SkipEstimateOfflineScoreRanking = tempSkipEstimateOfflineScoreRanking;
            Settings.Default.AutoInstall = tempEnableAutoInstall;
            Settings.Default.KeepInstallablePackagesPending = tempKeepInstallablePackagesPending;
            Settings.Default.UseEverythingForPendingPackageSourceScan = tempUseEverythingForPendingPackageSourceScan;
            Settings.Default.AutoApplyAmbiguousInstallDestination = tempAutoApplyAmbiguousInstallDestination;
            Settings.Default.DeletePendingPackageSourceAfterInstall = tempDeletePendingPackageSourceAfterInstall;
            Settings.Default.EnableSmartComponentOverwrite = tempEnableSmartComponentOverwrite;
            Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming = tempKeepSmartOverwriteProtectedFilesByRenaming;
            Settings.Default.SkipInitFileCheck = tempSkipInitFileCheck;
            Settings.Default.EncoderSampleRate = tempEncoderSampleRate;
            Settings.Default.Encoder = (EncoderType)tempEncoderIndex;
            Settings.Default.EncoderFormat = tempEncoderFormat;
            Settings.Default.EncoderNormalization = tempEncoderNormalization;
            Settings.Default.EncoderExeDir = tempEncoderExeDir;
            Settings.Default.EncoderAmplifier = tempEncoderAmplifier;
            Settings.Default.EncoderQuality = tempEncoderQuality;
            Settings.Default.EncodeFileNameFormat = tempEncodeFileNameFormat;
            Settings.Default.PlayerDriver = (BassAudioPlayer.DeviceDriver)tempPlayerDriverIndex;
            playerDeviceNames = BassAudioPlayer.DeviceList[Settings.Default.PlayerDriver].ToList();
            Settings.Default.PlayerDevice = tempPlayerDevice;
            Settings.Default.PlayerDeviceName = tempPlayerDeviceName;
            Settings.Default.PlayerSampleRate = tempPlayerSampleRate;
            Settings.Default.PlayerFormat = tempPlayerFormat;
            Settings.Default.PlayerBufferSize = tempPlayerBufferSize;
            Settings.Default.PlayerWASAPIParam = tempPlayerWASAPIParam;
            Language = tempLanguage;
            try
            {
                lr2config = new LR2Config(Settings.Default.LR2ConfigXmlPath);
            }
            catch
            {
                Settings.Default.LR2ConfigXmlPath = null;
                lr2config = null;
            }
            RaisePropertyChanged(() => OperationModeLR2DB);
            RaisePropertyChanged(() => LR2RootPath);
            RaisePropertyChanged(() => BMSRootPath);
            RaisePropertyChanged(() => LR2SongDBPath);
            RaisePropertyChanged(() => LR2ConfigXmlPath);
            RaisePropertyChanged(() => uBMplayPath);
            RaisePropertyChanged(() => BMIIDXViewPath);
            RaisePropertyChanged(() => UsePlayeruBMplay);
            RaisePropertyChanged(() => UsePlayerLR2body);
            RaisePropertyChanged(() => UsePlayerBMIIDXView);
            RaisePropertyChanged(() => LR2bodyResolution);
            RaisePropertyChanged(() => IsSaveLR2bodyWindowPosition);
            RaisePropertyChanged(() => LR2ConfigBMSDirectories);
            RaisePropertyChanged(() => LR2CustomFolderOutputDir);
            RaisePropertyChanged(() => BMSInstallDir);
            RaisePropertyChanged(() => LR2CustomFolderAsRootOutputDir);
            RaisePropertyChanged(() => TableListURL);
            RaisePropertyChanged(() => EnablePlaylistUrlCompletion);
            RaisePropertyChanged(() => OverwritePlaylistUrlsWithCompletion);
            RaisePropertyChanged(() => PlaylistMd5UrlMappingTsvUri);
            RaisePropertyChanged(() => IsLR2BackupEnabled);
            RaisePropertyChanged(() => LR2BackupPath);
            RaisePropertyChanged(() => LR2BackupTarget);
            RaisePropertyChanged(() => LR2BackupSpan);
            RaisePropertyChanged(() => LR2BackupNum);
            RaisePropertyChanged(() => UseExternalWebBrowser);
            RaisePropertyChanged(() => UseExternalPanelImage);
            RaisePropertyChanged(() => StagefilePath);
            RaisePropertyChanged(() => FolderNameFormat);
            RaisePropertyChanged(() => UseOnlyShiftJISChars);
            RaisePropertyChanged(() => ShowScoreViewerRegisterConfirmMsg);
            RaisePropertyChanged(() => ShowDiffBMSInstallConfirmMsg);
            RaisePropertyChanged(() => ShowRecommUpdatedMsg);
            RaisePropertyChanged(() => SkipInitFileCheck);
            RaisePropertyChanged(() => SkipInitPlaylistLoad);
            RaisePropertyChanged(() => StartupSelectInstallPending);
            RaisePropertyChanged(() => StartupExpandPlaylistTree);
            RaisePropertyChanged(() => EnableReadOptimizedPragmas);
            RaisePropertyChanged(() => SkipEstimateOfflineScoreRanking);
            RaisePropertyChanged(() => EnableAutoInstall);
            RaisePropertyChanged(() => KeepInstallablePackagesPending);
            RaisePropertyChanged(() => UseEverythingForPendingPackageSourceScan);
            RaisePropertyChanged(() => AutoApplyAmbiguousInstallDestination);
            RaisePropertyChanged(() => DeletePendingPackageSourceAfterInstall);
            RaisePropertyChanged(() => EnableSmartComponentOverwrite);
            RaisePropertyChanged(() => KeepSmartOverwriteProtectedFilesByRenaming);
            RaisePropertyChanged(() => EncoderSampleRate);
            RaisePropertyChanged(() => EncoderIndex);
            RaisePropertyChanged(() => EncoderNormalization);
            RaisePropertyChanged(() => EncoderFormat);
            RaisePropertyChanged(() => EncoderExeDir);
            RaisePropertyChanged(() => EncoderAmplifier);
            RaisePropertyChanged(() => EncoderQuality);
            RaisePropertyChanged(() => EncodeFileNameFormat);
            RaisePropertyChanged(() => PlayerDriverIndex);
            RaisePropertyChanged(() => PlayerDevice);
            RaisePropertyChanged(() => PlayerDeviceNames);
            RaisePropertyChanged(() => PlayerDevice);
            RaisePropertyChanged(() => PlayerSampleRate);
            RaisePropertyChanged(() => PlayerFormat);
            RaisePropertyChanged(() => PlayerBufferSize);
            RaisePropertyChanged(() => PlayerWASAPIParam);
            RaisePropertyChanged(() => Languages);
            backupSavedSettings();
        }

        public RestartMode IsNeedRestartForSaved()
        {
            if (tempValidation != CheckValidation())
            {
                return RestartMode.All;
            }
            if (tempOperationModeLR2DB != Settings.Default.OperationModeLR2DB)
            {
                return RestartMode.All;
            }
            if (Settings.Default.OperationModeLR2DB)
            {
                if (tempLR2SongDBPath != Settings.Default.LR2SongDBPath)
                {
                    return RestartMode.All;
                }
                if (tempLR2ConfigXmlPath != Settings.Default.LR2ConfigXmlPath)
                {
                    return RestartMode.FolderOnly;
                }
            }
            else if (tempBMSRootPath != Settings.Default.BMSRootPath)
            {
                return RestartMode.FolderOnly;
            }
            return RestartMode.None;
        }

        public RestartMode IsNeedRestartForSaveOrCancel()
        {
            if (!isBMSDirectoryAdded && !isBMSDirectoryRemoved)
            {
                return RestartMode.None;
            }
            return RestartMode.FolderOnly;
        }
    }

    public class PlaylistPropertyDialogViewModel : ViewModel
    {
        private BMSTable bmsTable;

        private MainWindowViewModel ownerViewModel;

        private bool isForNewTable;

        private DispatcherCollection<string> _folder_order;

        private LR2SongDBExtended.playlist.CustomFolderSortType _folder_sort_key;

        private LR2SongDBExtended.playlist.EntryUnitType _entry_type;

        private string temp_compat_prefix;

        private string temp_name;

        private string temp_symbol;

        private string _compat_prefix;

        private bool _folder_sort_ascending;

        private LR2SongDBExtended.playlist.CustomFolderType _ignore_folder_output;

        private string _name;

        private string _symbol;

        private Uri temp_Page_url;

        private Uri _Page_url;

        private Uri _Header_url;

        private Uri _Data_url;

        private bool temp_is_external_sync;

        private bool _is_external_sync;

        private bool temp_is_root_folder;

        private bool _is_root_folder;

        private string temp_output_dir_full_path;

        private string _output_dir;

        private bool _is_auto_folder_sort;

        public DispatcherCollection<string> folder_order
        {
            get
            {
                return _folder_order;
            }
            set
            {
                if (_folder_order != value)
                {
                    _folder_order = value;
                    RaisePropertyChanged("folder_order");
                }
            }
        }

        public LR2SongDBExtended.playlist.CustomFolderSortType folder_sort_key
        {
            get
            {
                return _folder_sort_key;
            }
            set
            {
                if (_folder_sort_key != value)
                {
                    _folder_sort_key = value;
                    RaisePropertyChanged("folder_sort_key");
                }
            }
        }

        public IEnumerable<string> folder_sort_key_list
        {
            get
            {
                if (entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder)
                {
                    return CustomFolderSortTypeExt.GetDisplayNames().Except(new string[2]
                    {
                        LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL.ToDisplayName(),
                        LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE.ToDisplayName()
                    });
                }
                return CustomFolderSortTypeExt.GetDisplayNames();
            }
            set
            {
            }
        }

        public LR2SongDBExtended.playlist.EntryUnitType entry_type
        {
            get
            {
                return _entry_type;
            }
            set
            {
                if (_entry_type != value)
                {
                    DisableInvalidOutputFolders(value);
                    _entry_type = value;
                    RaisePropertyChanged("entry_type");
                    RaisePropertyChanged(() => folder_sort_key_list);
                }
            }
        }

        public IEnumerable<string> entry_type_list
        {
            get
            {
                return EntryUnitTypeExt.GetDisplayNames();
            }
            set
            {
            }
        }

        public string compat_prefix
        {
            get
            {
                return _compat_prefix;
            }
            set
            {
                if (value == null)
                {
                    value = string.Empty;
                }
                value = value.TrimStart();
                if (!(_compat_prefix == value))
                {
                    _compat_prefix = value;
                    RaisePropertyChanged("compat_prefix");
                }
            }
        }

        public bool folder_sort_ascending
        {
            get
            {
                return _folder_sort_ascending;
            }
            set
            {
                if (_folder_sort_ascending != value)
                {
                    _folder_sort_ascending = value;
                    RaisePropertyChanged("folder_sort_ascending");
                }
            }
        }

        public LR2SongDBExtended.playlist.CustomFolderType ignore_folder_output
        {
            get
            {
                return _ignore_folder_output;
            }
            set
            {
                if (_ignore_folder_output != value)
                {
                    _ignore_folder_output = value;
                    RaisePropertyChanged("ignore_folder_output");
                }
            }
        }

        public DateTime last_update
        {
            get
            {
                return bmsTable.last_update;
            }
            set
            {
            }
        }

        public string name
        {
            get
            {
                if (!IsNameValid())
                {
                    _name = string.Empty;
                }
                return _name;
            }
            set
            {
                value = value.Trim();
                if (!(_name == value))
                {
                    if (IsNameValid(value))
                    {
                        _name = value;
                    }
                    RaisePropertyChanged("name");
                    RaisePropertyChanged(() => output_dir);
                    if (Settings.Default.OperationModeLR2DB && !IsOutputDirValid())
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("出力先フォルダ名が空もしくは重複しています。" + Environment.NewLine + "プレイリスト名または出力先フォルダ名を変更して下さい。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                }
            }
        }

        public string symbol
        {
            get
            {
                return _symbol;
            }
            set
            {
                value = value.Trim();
                if (!(_symbol == value))
                {
                    _symbol = value;
                    RaisePropertyChanged("symbol");
                }
            }
        }

        public Uri Page_url
        {
            get
            {
                if (!IsUrlValid(_Page_url))
                {
                    _Page_url = null;
                }
                return _Page_url;
            }
            set
            {
                if (!(_Page_url == value))
                {
                    if (IsUrlValid(value) && value.IsAbsoluteUri)
                    {
                        _Page_url = value;
                    }
                    else if (string.IsNullOrWhiteSpace(value.ToString()))
                    {
                        _Page_url = null;
                    }
                    else
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("入力されたページURIが正しくありません。" + Environment.NewLine + "絶対URIを入力して下さい。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    RaisePropertyChanged("Page_url");
                }
            }
        }

        public Uri Header_url
        {
            get
            {
                if (!IsUrlValid(_Header_url))
                {
                    _Header_url = null;
                }
                return _Header_url;
            }
            set
            {
                if (!(_Header_url == value))
                {
                    if (IsUrlValid(value))
                    {
                        _Header_url = value;
                    }
                    else
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("入力されたヘッダURIが正しくありません。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    RaisePropertyChanged("Header_url");
                }
            }
        }

        public Uri Data_url
        {
            get
            {
                if (!IsUrlValid(_Data_url))
                {
                    _Data_url = null;
                }
                return _Data_url;
            }
            set
            {
                if (!(_Data_url == value))
                {
                    if (IsUrlValid(value))
                    {
                        _Data_url = value;
                    }
                    else
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("入力されたデータURIが正しくありません。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    RaisePropertyChanged("Data_url");
                }
            }
        }

        public bool is_external_sync
        {
            get
            {
                return _is_external_sync;
            }
            set
            {
                if (_is_external_sync == value)
                {
                    return;
                }
                if (!IsExternal_syncValid(value))
                {
                    value = false;
                    ownerViewModel.Messenger.Raise(new ConfirmationMessage("ページURIまたはヘッダURIが正しくありません。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                }
                if (!_is_external_sync && value)
                {
                    ConfirmationMessage confirmationMessage = new ConfirmationMessage("同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
                    ownerViewModel.Messenger.Raise(confirmationMessage);
                    if (!confirmationMessage.Response.HasValue || !confirmationMessage.Response.Value)
                    {
                        RaisePropertyChanged("is_external_sync");
                        return;
                    }
                }
                else if (_is_external_sync && !value)
                {
                    ConfirmationMessage confirmationMessage2 = new ConfirmationMessage("同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
                    ownerViewModel.Messenger.Raise(confirmationMessage2);
                    if (!confirmationMessage2.Response.HasValue || !confirmationMessage2.Response.Value)
                    {
                        RaisePropertyChanged("is_external_sync");
                        return;
                    }
                }
                _is_external_sync = value;
                RaisePropertyChanged("is_external_sync");
            }
        }

        public bool is_root_folder
        {
            get
            {
                return _is_root_folder;
            }
            set
            {
                if (_is_root_folder != value)
                {
                    _is_root_folder = value;
                    RaisePropertyChanged("is_root_folder");
                }
            }
        }

        public string output_dir
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_output_dir) && _output_dir != name.Trim().ToSjisSchemeString().RemoveInvalidFileNameChars())
                {
                    return _output_dir;
                }
                return name.Trim().ToSjisSchemeString().RemoveInvalidFileNameChars();
            }
            set
            {
                value = value?.Trim().ToSjisSchemeString().RemoveInvalidFileNameChars();
                if (name != null && name.Trim().ToSjisSchemeString().RemoveInvalidFileNameChars() != value)
                {
                    if (!Settings.Default.OperationModeLR2DB || IsOutputDirValid(value))
                    {
                        _output_dir = value;
                    }
                    else
                    {
                        ownerViewModel.Messenger.Raise(new ConfirmationMessage("出力先フォルダ名が空もしくは重複しています。" + Environment.NewLine + "入力した値を確認して下さい。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                }
                else if (string.IsNullOrWhiteSpace(value) || value == name.Trim().ToSjisSchemeString().RemoveInvalidFileNameChars())
                {
                    _output_dir = null;
                }
                RaisePropertyChanged("output_dir");
            }
        }

        public bool is_auto_folder_sort
        {
            get
            {
                return _is_auto_folder_sort;
            }
            set
            {
                if (_is_auto_folder_sort != value)
                {
                    _is_auto_folder_sort = value;
                    RaisePropertyChanged("is_auto_folder_sort");
                }
            }
        }

        public PlaylistPropertyDialogViewModel(MainWindowViewModel _owner, BMSTable _table, bool _isForNewTable = false)
        {
            if (_table == null)
            {
                throw new ArgumentNullException("_table");
            }
            ownerViewModel = _owner;
            bmsTable = _table;
            isForNewTable = _isForNewTable;
            ownerViewModel.tables.AcquireWriterLockBMSTables();
            backupTableProperties();
            loadTableProperties();
        }

        private void DisableInvalidOutputFolders()
        {
            DisableInvalidOutputFolders(entry_type);
        }

        private void DisableInvalidOutputFolders(LR2SongDBExtended.playlist.EntryUnitType value)
        {
            if (value != LR2SongDBExtended.playlist.EntryUnitType.Folder)
            {
                return;
            }
            ignore_folder_output |= LR2SongDBExtended.playlist.CustomFolderType.LevelFolder;
            RaisePropertyChanged(() => ignore_folder_output);
            if (folder_sort_key == LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL || folder_sort_key == LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE)
            {
                folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.NONE;
                RaisePropertyChanged(() => folder_sort_key);
            }
        }

        private bool IsNameValid()
        {
            return IsNameValid(_name);
        }

        private bool IsNameValid(string value)
        {
            return true;
        }

        private bool IsUrlValid(Uri value)
        {
            if (!(value == null) && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return Uri.TryCreate(value.OriginalString, UriKind.RelativeOrAbsolute, out value);
            }
            return true;
        }

        private bool IsExternal_syncValid(bool value)
        {
            if (!value)
            {
                return true;
            }
            if (Page_url != null && Page_url.Scheme == "bmseeker")
            {
                return true;
            }
            if (Header_url != null && Data_url != null && ((Page_url != null && Page_url.IsAbsoluteUri) || Header_url.IsAbsoluteUri))
            {
                return true;
            }
            return false;
        }

        private bool IsOutputDirValid()
        {
            return IsOutputDirValid(output_dir);
        }

        private bool IsOutputDirValid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            if ((from t in ownerViewModel.BMSTables
                 where t != null && t != bmsTable
                 select t.Output_dir).Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        public bool ResetProperties()
        {
            loadTableProperties();
            if (isForNewTable)
            {
                ownerViewModel.tables.RemoveBMSTable(bmsTable);
                return true;
            }
            return CheckValidation();
        }

        public bool CheckValidation()
        {
            if (!IsNameValid())
            {
                return false;
            }
            if ((Page_url != null && (!IsUrlValid(Page_url) || !Page_url.IsAbsoluteUri)) || !IsUrlValid(Header_url) || !IsUrlValid(Data_url))
            {
                return false;
            }
            if (Settings.Default.OperationModeLR2DB)
            {
                if (!IsOutputDirValid())
                {
                    return false;
                }
                DisableInvalidOutputFolders();
            }
            return true;
        }

        public bool SaveProperties()
        {
            if (CheckValidation())
            {
                if (is_auto_folder_sort)
                {
                    bmsTable.Folder_order = new List<string>();
                }
                else
                {
                    bmsTable.Folder_order = new List<string>(folder_order);
                }
                bmsTable.folder_sort_key = folder_sort_key;
                bmsTable.folder_sort_ascending = folder_sort_ascending;
                bmsTable.ignore_folder_output = ignore_folder_output;
                bmsTable.entry_type = entry_type;
                bmsTable.name = name;
                bmsTable.symbol = symbol;
                bmsTable.Page_url = Page_url;
                bmsTable.Header_url = Header_url;
                bmsTable.Data_url = Data_url;
                if (is_external_sync)
                {
                    bmsTable.EnableExternalSync();
                }
                else
                {
                    bmsTable.DisableExternalSync();
                }
                bmsTable.Output_dir = output_dir;
                bmsTable.is_root_folder = is_root_folder;
                bmsTable.compat_prefix = compat_prefix;
                return true;
            }
            return false;
        }

        private void loadTableProperties()
        {
            ownerViewModel?.tables?.EnsurePlaylistEntriesLoaded(bmsTable, "PlaylistPropertyDialogViewModel.loadTableProperties");
            _folder_order = new DispatcherCollection<string>(DispatcherHelper.UIDispatcher);
            _folder_order.AddRange(bmsTable.folder_list);
            RaisePropertyChanged(() => folder_order);
            _folder_sort_key = bmsTable.folder_sort_key;
            RaisePropertyChanged(() => folder_sort_key);
            _folder_sort_ascending = bmsTable.folder_sort_ascending;
            RaisePropertyChanged(() => folder_sort_ascending);
            _ignore_folder_output = bmsTable.ignore_folder_output;
            RaisePropertyChanged(() => ignore_folder_output);
            _entry_type = bmsTable.entry_type;
            RaisePropertyChanged(() => entry_type);
            _name = bmsTable.name;
            RaisePropertyChanged(() => name);
            _symbol = bmsTable.symbol;
            RaisePropertyChanged(() => symbol);
            _Page_url = bmsTable.Page_url;
            RaisePropertyChanged(() => Page_url);
            _Header_url = bmsTable.Header_url;
            RaisePropertyChanged(() => Header_url);
            _Data_url = bmsTable.Data_url;
            RaisePropertyChanged(() => Data_url);
            _is_external_sync = bmsTable.is_external_sync;
            RaisePropertyChanged(() => is_external_sync);
            _output_dir = bmsTable.output_dir;
            RaisePropertyChanged(() => output_dir);
            _is_root_folder = bmsTable.is_root_folder;
            RaisePropertyChanged(() => is_root_folder);
            _is_auto_folder_sort = bmsTable.Folder_order == null || bmsTable.Folder_order.Count() == 0;
            RaisePropertyChanged(() => is_auto_folder_sort);
            _compat_prefix = bmsTable.compat_prefix;
            RaisePropertyChanged(() => compat_prefix);
        }

        private void backupTableProperties()
        {
            temp_output_dir_full_path = BMSPlaylist.GetCustomFolderOutputDirectory(bmsTable);
            temp_is_root_folder = bmsTable.is_root_folder;
            temp_is_external_sync = bmsTable.is_external_sync;
            temp_compat_prefix = bmsTable.compat_prefix;
            temp_name = bmsTable.name;
            temp_symbol = bmsTable.symbol;
            temp_Page_url = bmsTable.Page_url;
        }

        internal async Task ApplyPostSaveUpdatesAsync()
        {
            bool flag = !string.Equals(temp_name, bmsTable.name, StringComparison.Ordinal) || !string.Equals(temp_symbol, bmsTable.symbol, StringComparison.Ordinal);
            if ((!temp_is_external_sync && bmsTable.is_external_sync) || (bmsTable.is_external_sync && temp_compat_prefix != bmsTable.compat_prefix) || (bmsTable.is_external_sync && bmsTable.Page_url != null && temp_Page_url != null && bmsTable.Page_url.ToString() != temp_Page_url.ToString()))
            {
                Uri uri = bmsTable.Page_url ?? bmsTable.Header_url;
                if (uri != null && uri.IsAbsoluteUri)
                {
                    DateTime last_update = bmsTable.last_update;
                    BMSTable sourceTable = bmsTable;
                    ownerViewModel.BeginPlaylistSyncProgressOperation();
                    try
                    {
                        ownerViewModel.UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = 1,
                            CompletedTableCount = 0,
                            CurrentTableName = bmsTable.name,
                            CurrentUri = uri
                        });
                        List<BMSTableEntry> oldEntriesSnapshot;
                        ownerViewModel.tables.EnsurePlaylistEntriesLoaded(bmsTable, "PlaylistPropertyDialogViewModel.ApplyPostSaveUpdatesAsync");
                        using (bmsTable.ReaderWriterLock.GetReaderGuard())
                        {
                            oldEntriesSnapshot = bmsTable.entries.ToList();
                        }
                        bmsTable = await ownerViewModel.tables.ResetBMSTableAsync(bmsTable, uri);
                        ownerViewModel.files.ReplaceReferenceBMSTable(sourceTable, bmsTable, oldEntriesSnapshot);
                        ownerViewModel.UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateSuccess(sourceTable, bmsTable, uri, bmsTable.last_update != last_update));
                        flag = false;
                    }
                    catch (Exception ex)
                    {
                        NLogWrapper.FileLogger?.Warn(ex, "playlist_property_resync_failed table=" + (bmsTable?.name ?? string.Empty) + " uri=" + uri);
                        ownerViewModel.UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateFailure(sourceTable, uri, ex));
                        ownerViewModel.ShowPlaylistLoadFailure(ex);
                    }
                    finally
                    {
                        ownerViewModel.UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = 1,
                            CompletedTableCount = 1,
                            CurrentTableName = bmsTable.name,
                            CurrentUri = uri
                        });
                        ownerViewModel.EndPlaylistSyncProgressOperation();
                    }
                    ownerViewModel.RefreshPlaylistSummaryIfVisible();
                }
            }
            if (flag)
            {
                ownerViewModel.files.RefreshReferenceDisplayForTable(bmsTable);
                ownerViewModel.RefreshPlaylistSummaryIfVisible();
            }
            if (Settings.Default.OperationModeLR2DB)
            {
                string customFolderOutputDirectory = BMSPlaylist.GetCustomFolderOutputDirectory(bmsTable);
                ownerViewModel.tables.MigrateCustomFolderOutputDirectory(bmsTable, temp_output_dir_full_path, customFolderOutputDirectory);
                List<string> bMSSearchDirectories = ownerViewModel.lr2config.GetBMSSearchDirectories();
                if (!temp_is_root_folder && bmsTable.is_root_folder)
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Union(new string[1] { customFolderOutputDirectory }).Distinct(StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
                else if (temp_is_root_folder && !bmsTable.is_root_folder)
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Except(new string[1] { temp_output_dir_full_path }, StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
                else if (temp_is_root_folder && bmsTable.is_root_folder && !customFolderOutputDirectory.Equals(temp_output_dir_full_path, StringComparison.OrdinalIgnoreCase))
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Except(new string[1] { temp_output_dir_full_path }, StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Union(new string[1] { customFolderOutputDirectory }).Distinct(StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
            }
            backupTableProperties();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ownerViewModel.tables.FreeWriterLockBMSTables();
            }
        }
    }

    public class cSortParameters
    {
        public string ColumnsName { get; set; } = "";

        public ListSortDirection Direction { get; set; }
    }

    [Flags]
    public enum PanelState
    {
        TITLE_LARGE = 0,
        TITLE_SMALL = 1,
        BMS_PLAYER = 2,
        MOVIE_PLAYER = 4
    }

    [Flags]
    public enum ModeFilterType
    {
        None = 0,
        _5KEYS = 1,
        _7KEYS = 2,
        _9KEYS = 4,
        _10KEYS = 8,
        _14KEYS = 0x10,
        All = 0x1F
    }

    public enum PlaylistSummaryOwnedFilterType
    {
        All,
        OwnedComplete,
        OwnedIncomplete
    }

    private enum viewUpdateMode
    {
        TreeViewFilterNotChanged = 0,
        PlaylistFilterSelected = 1,
        PlaylistNotOwnedFilterSelected = 2,
        FolderFilterSelected = 17,
        FullScanAllChartsFilterSelected = 32,
        FileMissingFilterSelected = 33,
        FileMissingIgnoredFilterSelected = 34,
        DuplicateFilterSelected = 35,
        GarbledFilterSelected = 36,
        GarbleFixedFilterSelected = 37,
        UnregisteredFilterSelected = 38,
        ZeroNoteFilterSelected = 39,
        NewlyInstalledFolderSelected = 49,
        PendingInstallFolderSelected = 50,
        KeywordFilterUpdated = 65,
        ModeFilterUpdated = 66,
        SortUpdated = 81,
        UpdatedNone = 255
    }

    public enum FolderFilterType
    {
        DirectoryFilter,
        ArtistFilter,
        PlayListFilter,
        FilterNone
    }

    public enum PlaylistFilterType
    {
        PlaylistFilter = 1,
        PlaylistNotOwnedFilterSelected
    }

    /// <summary>
    /// playlist source snapshot の再構築要否を判定する正規化済み snapshot です。
    /// </summary>
    internal readonly struct PlaylistSourceIdentity : IEquatable<PlaylistSourceIdentity>
    {
        internal BMSTable Table { get; }

        internal string FolderName { get; }

        internal PlaylistFilterType FilterType { get; }

        internal long LibraryIndexVersion { get; }

        internal long PlaylistRevision { get; }

        internal int ScoreSnapshotVersion { get; }

        internal int ChartInfoIndexVersion { get; }

        internal bool HasResolvedSelection { get; }

        internal PlaylistSourceIdentity(BMSTable table, string folderName, PlaylistFilterType filterType, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
        {
            Table = table;
            FolderName = folderName;
            FilterType = filterType;
            LibraryIndexVersion = libraryIndexVersion;
            PlaylistRevision = playlistRevision;
            ScoreSnapshotVersion = scoreSnapshotVersion;
            ChartInfoIndexVersion = chartInfoIndexVersion;
            HasResolvedSelection = hasResolvedSelection;
        }

        public bool Equals(PlaylistSourceIdentity other)
        {
            return Table == other.Table && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal) && FilterType == other.FilterType && LibraryIndexVersion == other.LibraryIndexVersion && PlaylistRevision == other.PlaylistRevision && ScoreSnapshotVersion == other.ScoreSnapshotVersion && ChartInfoIndexVersion == other.ChartInfoIndexVersion && HasResolvedSelection == other.HasResolvedSelection;
        }

        internal bool EqualsIgnoringChartInfoIndex(PlaylistSourceIdentity other)
        {
            return Table == other.Table && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal) && FilterType == other.FilterType && LibraryIndexVersion == other.LibraryIndexVersion && PlaylistRevision == other.PlaylistRevision && ScoreSnapshotVersion == other.ScoreSnapshotVersion && HasResolvedSelection == other.HasResolvedSelection;
        }

        public override bool Equals(object obj)
        {
            return obj is PlaylistSourceIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = Table?.GetHashCode() ?? 0;
                hashCode = (hashCode * 397) ^ (FolderName?.GetHashCode() ?? 0);
                hashCode = (hashCode * 397) ^ (int)FilterType;
                hashCode = (hashCode * 397) ^ LibraryIndexVersion.GetHashCode();
                hashCode = (hashCode * 397) ^ PlaylistRevision.GetHashCode();
                hashCode = (hashCode * 397) ^ ScoreSnapshotVersion;
                hashCode = (hashCode * 397) ^ ChartInfoIndexVersion;
                hashCode = (hashCode * 397) ^ HasResolvedSelection.GetHashCode();
                return hashCode;
            }
        }
    }

    /// <summary>
    /// playlist source snapshot から view を再 materialize する条件を表す snapshot です。
    /// </summary>
    internal readonly struct PlaylistPresentationIdentity : IEquatable<PlaylistPresentationIdentity>
    {
        internal string KeywordFilter { get; }

        internal ModeFilterType ModeFilter { get; }

        internal string SortColumnName { get; }

        internal ListSortDirection SortDirection { get; }

        internal PlaylistPresentationIdentity(string keywordFilter, ModeFilterType modeFilter, string sortColumnName, ListSortDirection sortDirection)
        {
            KeywordFilter = keywordFilter;
            ModeFilter = modeFilter;
            SortColumnName = sortColumnName;
            SortDirection = sortDirection;
        }

        public bool Equals(PlaylistPresentationIdentity other)
        {
            return string.Equals(KeywordFilter, other.KeywordFilter, StringComparison.Ordinal) && ModeFilter == other.ModeFilter && string.Equals(SortColumnName, other.SortColumnName, StringComparison.Ordinal) && SortDirection == other.SortDirection;
        }

        public override bool Equals(object obj)
        {
            return obj is PlaylistPresentationIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = KeywordFilter?.GetHashCode() ?? 0;
                hashCode = (hashCode * 397) ^ (int)ModeFilter;
                hashCode = (hashCode * 397) ^ (SortColumnName?.GetHashCode() ?? 0);
                hashCode = (hashCode * 397) ^ (int)SortDirection;
                return hashCode;
            }
        }
    }

    /// <summary>
    /// playlist build 要求の同値判定に使う正規化済み snapshot です。
    /// </summary>
    internal readonly struct PlaylistRequestIdentity : IEquatable<PlaylistRequestIdentity>
    {
        internal PlaylistSourceIdentity SourceIdentity { get; }

        internal PlaylistPresentationIdentity PresentationIdentity { get; }

        internal BMSTable Table => SourceIdentity.Table;

        internal string FolderName => SourceIdentity.FolderName;

        internal PlaylistFilterType FilterType => SourceIdentity.FilterType;

        internal string KeywordFilter => PresentationIdentity.KeywordFilter;

        internal ModeFilterType ModeFilter => PresentationIdentity.ModeFilter;

        internal string SortColumnName => PresentationIdentity.SortColumnName;

        internal ListSortDirection SortDirection => PresentationIdentity.SortDirection;

        internal long LibraryIndexVersion => SourceIdentity.LibraryIndexVersion;

        internal long PlaylistRevision => SourceIdentity.PlaylistRevision;

        internal int ScoreSnapshotVersion => SourceIdentity.ScoreSnapshotVersion;

        internal int ChartInfoIndexVersion => SourceIdentity.ChartInfoIndexVersion;

        internal bool HasResolvedSelection => SourceIdentity.HasResolvedSelection;

        /// <summary>
        /// 正規化済み playlist 要求 identity を生成します。
        /// </summary>
        internal PlaylistRequestIdentity(BMSTable table, string folderName, PlaylistFilterType filterType, string keywordFilter, ModeFilterType modeFilter, string sortColumnName, ListSortDirection sortDirection, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
        {
            SourceIdentity = new PlaylistSourceIdentity(table, folderName, filterType, libraryIndexVersion, playlistRevision, scoreSnapshotVersion, chartInfoIndexVersion, hasResolvedSelection);
            PresentationIdentity = new PlaylistPresentationIdentity(keywordFilter, modeFilter, sortColumnName, sortDirection);
        }

        public bool Equals(PlaylistRequestIdentity other)
        {
            return SourceIdentity.Equals(other.SourceIdentity) && PresentationIdentity.Equals(other.PresentationIdentity);
        }

        public override bool Equals(object obj)
        {
            return obj is PlaylistRequestIdentity other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = SourceIdentity.GetHashCode();
                hashCode = (hashCode * 397) ^ PresentationIdentity.GetHashCode();
                return hashCode;
            }
        }

        public static bool operator ==(PlaylistRequestIdentity left, PlaylistRequestIdentity right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(PlaylistRequestIdentity left, PlaylistRequestIdentity right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// playlist 詳細表示の build 要求 1 件を表します。
    /// worker は常にこの最新要求だけを処理します。
    /// </summary>
    private sealed class PlaylistBuildRequest
    {
        internal int RequestVersion;

        internal viewUpdateMode Mode;

        internal viewUpdateMode RequestedMode;

        internal object Parameter;

        internal PlaylistRequestIdentity Identity;

        internal bool UseCoalescingWindow;

        internal int LastBuiltScoreSnapshotVersion;

        internal string SourceInvalidationReason;
    }

    /// <summary>
    /// playlist source build で再利用するライブラリ索引 snapshot です。
    /// </summary>
    private sealed class PlaylistLibraryIndexSnapshot
    {
        internal long Version;

        internal long BuildElapsedMs;

        internal Dictionary<string, BeMusicSeeker.Models.BMSFile> FilesByHash = new Dictionary<string, BeMusicSeeker.Models.BMSFile>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, BeMusicSeeker.Models.BMSFile> FilesBySha256 = new Dictionary<string, BeMusicSeeker.Models.BMSFile>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, LR2SongDBExtended.bmson_song> BmsonByMd5 = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, LR2SongDBExtended.bmson_song> BmsonBySha256 = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// playlist open 要求時点の library index readiness を表します。
    /// </summary>
    private sealed class PlaylistLibraryIndexReadinessSnapshot
    {
        internal string State = "inline";

        internal long BuildElapsedMs;
    }

    /// <summary>
    /// playlist open 要求時点の readiness 情報です。
    /// </summary>
    private sealed class PlaylistOpenReadinessSnapshot
    {
        internal bool StartupReadyDataReached;

        internal bool StartupReadyUiReached;

        internal bool StartupReadyOperableReached;

        internal bool PlaylistRefDeferredRunning;

        internal int PlaylistRefDeferredLastCompletedVersion;

        internal bool MaintenanceDeferredRunning;

        internal int MaintenanceDeferredLastCompletedVersion;

        internal string PlaylistLibraryIndexState = "inline";

        internal long PlaylistLibraryIndexBuildMs;

        internal bool ScoreSnapshotReady;

        internal int ScoreSnapshotVersion;

        internal bool ScoreHydrationRunning;

        internal int ScoreHydrationCompletedVersion;

        internal bool RankingRefreshRunning;

        internal int RankingRefreshCompletedVersion;
    }

    /// <summary>
    /// 起動・リロード進捗の対象 operation 種別です。
    /// </summary>
    private enum StartupProgressOperationKind
    {
        None,
        Startup,
        ReloadFiles,
        ReloadTables
    }

    /// <summary>
    /// 起動・リロード進捗の内部フェーズ集合です。
    /// </summary>
    [Flags]
    private enum StartupProgressPhase
    {
        None = 0,
        CoreInitializeStarted = 1,
        StartupReadyData = 2,
        StartupReadyUi = 4,
        StartupReadyOperable = 8,
        PlaylistReferenceApplied = 16,
        ExternalPlaylistSyncDone = 32,
        ScoreHydrationDone = 64,
        RankingRefreshDone = 128,
        MaintenanceDeferredDone = 256,
        ChartDigestBackfillDone = 512,
        ChartInfoBackfillDone = 1024,
        ChartInfoHydrationDone = 2048,
        PlaylistEntriesHydrationDone = 4096
    }

    /// <summary>
    /// 起動・リロード進捗の内部状態です。
    /// UI 表示は Completed/Expected フェーズ集合から再計算します。
    /// </summary>
    private sealed class StartupProgressState
    {
        internal long OperationToken;

        internal StartupProgressOperationKind OperationKind;

        internal bool IsActive;

        internal bool IsFailed;

        internal string FailureSubLabel = string.Empty;

        internal StartupProgressPhase CompletedPhases;

        internal StartupProgressPhase ExpectedPhases;

        internal int ScoreHydrationBaselineCompletedVersion;

        internal int ScoreHydrationRequestedBaselineVersion;

        internal int RequiredScoreHydrationCompletedVersion;

        internal int RankingRefreshBaselineCompletedVersion;

        internal int RankingRefreshRequestedBaselineVersion;

        internal int RequiredRankingRefreshCompletedVersion;

        internal int MaintenanceRequestedBaselineVersion;

        internal int ChartDigestBackfillBaselineCompletedVersion;

        internal int ChartInfoBackfillBaselineCompletedVersion;

        internal int ChartInfoHydrationBaselineCompletedVersion;

        internal int PlaylistEntriesHydrationBaselineCompletedVersion;

        internal int RequiredPlaylistReferenceVersion;

        internal int RequiredExternalSyncVersion;

        internal int RequiredPlaylistEntriesHydrationCompletedVersion;

        internal int RequiredMaintenanceCompletedVersion;

        internal int RequiredChartDigestBackfillCompletedVersion;

        internal int RequiredChartInfoBackfillCompletedVersion;

        internal int RequiredChartInfoHydrationCompletedVersion;

        internal int ChartDigestBackfillTotalCount;

        internal int ChartDigestBackfillProcessedCount;

        internal string ChartDigestBackfillCurrentPath = string.Empty;

        internal int ChartInfoBackfillTotalCount;

        internal int ChartInfoBackfillProcessedCount;

        internal string ChartInfoBackfillCurrentPath = string.Empty;

        internal int ChartInfoHydrationTotalCount;

        internal int ChartInfoHydrationAppliedCount;

        internal bool CompletionHideScheduled;

        internal DateTime? LastCompletedAtUtc;
    }

    /// <summary>
    /// playlist open 1 件の request/build/render 相関を保持します。
    /// </summary>
    private sealed class PlaylistOpenInteractionState
    {
        internal int RequestVersion;

        internal PlaylistRequestIdentity Identity;

        internal DateTime RequestedAtUtc;

        internal DateTime? BuildStartedAtUtc;

        internal DateTime? BuildCompletedAtUtc;

        internal long ExpectedSourceGenerationId;

        internal long ExpectedViewGenerationId;

        internal int ViewCount;

        internal bool VisibleCompletedLogged;

        internal PlaylistOpenReadinessSnapshot Readiness = new PlaylistOpenReadinessSnapshot();
    }

    /// <summary>
    /// プレイリスト再読み込みの起点種別です。
    /// full reload 後 cleanup の対象判定とログ分類に利用します。
    /// </summary>
    private enum PlaylistReloadOperationKind
    {
        None,
        StartupFullReload,
        ManualFullReload,
        SinglePlaylistReload
    }

    /// <summary>
    /// プレイリスト再読み込み後 cleanup の pending 要求です。
    /// </summary>
    private sealed class PlaylistReloadCleanupRequest
    {
        internal long CleanupId;

        internal PlaylistReloadOperationKind OperationKind;

        internal int TableCount;

        internal bool WaitForStartupOperable;

        internal bool WaitForSummaryRefresh;

        internal bool WaitForDetailRefresh;

        internal bool GcAllowed;

        internal long RequestedAtTimestamp;
    }

    /// <summary>
    /// playlist score probe の集計メトリクスです。
    /// </summary>
    private sealed class PlaylistScoreProbeMetrics
    {
        internal int ChunkCount;

        internal int TargetCount;

        internal long WaitInitializedMinMs;

        internal long WaitBmsFilesReadMs;

        internal long WaitScoresWriteMs;

        internal long WaitScoreSnapshotReadMs;

        internal long ApplyKnownScoresMs;

        internal int MatchedScoreCount;

        internal long TotalMs;
    }

    /// <summary>
    /// プレイリスト詳細ビューの source snapshot と build 制御状態を保持します。
    /// source は keyword/mode/sort 適用前の正本であり、表示更新は常にこの snapshot から再計算します。
    /// </summary>
    private sealed class PlaylistViewState
    {
        /// <summary>
        /// source snapshot の更新や要求バージョン採番を直列化します。
        /// </summary>
        internal readonly object SyncRoot = new object();

        /// <summary>
        /// プレイリスト source build を単一実行に制限します。
        /// </summary>
        internal readonly SemaphoreSlim BuildGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 現在表示の正本となるプレイリスト行集合です。
        /// </summary>
        internal List<PlaylistDetailSourceRow> SourceRows = new List<PlaylistDetailSourceRow>();

        /// <summary>
        /// 現在一覧へ反映している表示用 snapshot です。
        /// source と別インスタンスで保持し、UI 側の retained reference と source 正本を切り分けます。
        /// </summary>
        internal IList CurrentViewRows = new List<object>();

        /// <summary>
        /// 現在表示中のプレイリストです。
        /// </summary>
        internal BMSTable CurrentTable;

        /// <summary>
        /// 現在表示中のプレイリストフォルダ名です。全体表示時は null です。
        /// </summary>
        internal string CurrentFolderName;

        /// <summary>
        /// 現在表示中のプレイリスト filter 種別です。
        /// </summary>
        internal PlaylistFilterType CurrentFilterType = PlaylistFilterType.PlaylistFilter;

        /// <summary>
        /// 最新要求のみ反映するための要求バージョンです。
        /// </summary>
        internal int RequestVersion;

        /// <summary>
        /// 現在の source build をキャンセルするための token source です。
        /// </summary>
        internal CancellationTokenSource Cancellation = new CancellationTokenSource();

        /// <summary>
        /// 現在 build 中の要求に紐づく token source です。
        /// </summary>
        internal CancellationTokenSource CurrentBuildCancellation;

        /// <summary>
        /// 現在 worker が処理対象として保持している要求です。
        /// quiet window 中の候補も含みます。
        /// </summary>
        internal PlaylistBuildRequest CurrentBuildRequest;

        /// <summary>
        /// 最新要求として待機中の playlist build 要求です。
        /// </summary>
        internal PlaylistBuildRequest PendingRequest;

        /// <summary>
        /// playlist build worker が起動中かどうかです。
        /// </summary>
        internal bool WorkerRunning;

        /// <summary>
        /// 現在の source snapshot 世代です。
        /// </summary>
        internal long SourceGenerationId;

        /// <summary>
        /// 現在採用中の view 世代です。
        /// </summary>
        internal long CurrentViewGenerationId;

        /// <summary>
        /// 現在採用中 view の件数です。
        /// </summary>
        internal int LastAppliedViewCount;

        /// <summary>
        /// 現在採用中 view の identity です。
        /// </summary>
        internal PlaylistRequestIdentity? CurrentViewIdentity;

        /// <summary>
        /// 現在採用中 source snapshot の identity です。
        /// keyword/mode/sort とは独立して、source rebuild 要否を判定します。
        /// </summary>
        internal PlaylistSourceIdentity? CurrentSourceIdentity;

        /// <summary>
        /// 直前に source build を完了した library index 版数です。
        /// </summary>
        internal long LastBuiltLibraryIndexVersion;

        /// <summary>
        /// 直前に source build を完了した playlist 更新版数です。
        /// </summary>
        internal long LastBuiltPlaylistRevision;

        /// <summary>
        /// 直前に source build を完了した score snapshot 版数です。
        /// </summary>
        internal int LastBuiltScoreSnapshotVersion;

        /// <summary>
        /// 直前に source build を完了した chart_info index 版数です。
        /// </summary>
        internal int LastBuiltChartInfoIndexVersion;

        /// <summary>
        /// playlist 内容更新版数です。
        /// </summary>
        internal long PlaylistContentRevision;

        /// <summary>
        /// playlist 行セルを現在編集中かどうかです。
        /// score snapshot 更新時の即時再構築抑止に利用します。
        /// </summary>
        internal bool IsPlaylistCellEditing;

        /// <summary>
        /// 編集中に保留した score snapshot refresh 版数です。
        /// </summary>
        internal int PendingScoreSnapshotRefreshVersion;

        /// <summary>
        /// 直前に置き換えた source snapshot の弱参照です。
        /// </summary>
        internal WeakReference<List<PlaylistDetailSourceRow>> PreviousSourceRowsWeakReference;

        /// <summary>
        /// 直前に置き換えた source snapshot 世代です。
        /// </summary>
        internal long PreviousSourceGenerationId;

        /// <summary>
        /// 直前に置き換えた view の弱参照です。
        /// </summary>
        internal WeakReference<IList> PreviousViewRowsWeakReference;

        /// <summary>
        /// 直前に置き換えた view 世代です。
        /// </summary>
        internal long PreviousViewGenerationId;

        /// <summary>
        /// 現在追跡中の playlist open interaction です。
        /// </summary>
        internal PlaylistOpenInteractionState CurrentOpenInteraction;
    }

    private sealed class StartupBackgroundTaskRequest
    {
        internal string Name;

        internal string Reason;

        internal string Dependency;

        internal string CoalesceKey;

        internal int Priority;

        internal long Version;

        internal Func<Task> Work;
    }

    public enum MaintenanceFilterType
    {
        FullScanAllChartsFilter = 32,
        FileMissingFilter = 33,
        FileMissingIgnoredFilter,
        DuplicateFilter,
        GarbledFilter,
        GarbleFixedFilter,
        UnregisteredFilter,
        ZeroNoteFilter,
        FilterNone
    }

    public enum InstallFilterType
    {
        NewlyInstalledFilter = 49,
        PendingInstallFilter
    }

    [Flags]
    private enum UiRefreshChannel
    {
        None = 0,
        LibraryMainView = 1,
        LibraryFolderTree = 2,
        InstallTree = 4,
        PlaylistTree = 8,
        DuplicateTree = 16
    }

    private PlaylistPropertyDialogViewModel _playlistPropertyDialog;

    private bool initializationCompleted;

    private bool bmsonMigrationApprovedForSession;

    internal const int CurrentBmsonColumnSettingsMigrationVersion = 1;

    private BMSLibrary files;

    private BMSPlaylist tables;

    private LR2Config lr2config;

    private IBMSPlayer bmsPlayer = new InternalBMSAutoPlayerSoundOnly();

    private PropertyChangedEventListener listenerForBMSLibrary;

    private CollectionChangedEventListener listenerForBMSLibraryBMSPackagesPendingCollection;

    private CollectionChangedEventListener listenerForBMSLibraryBMSPackagesInstalledCollection;

    private PropertyChangedEventListener listenerForBMSPlaylist;

    private CollectionChangedEventListener listenerForBMSPlaylistBMSTablesCollection;

    private PropertyChangedEventListener listenerForBMSPlayer;

    private object lockThis = new object();

    private static SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    private object lockCopyFile = new object();

    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.MainWindowViewModel");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private int suppressUiUpdateDepth;

    private UiRefreshChannel suppressedUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel pendingUiRefreshMask = UiRefreshChannel.None;

    private object lockUiSuppression = new object();

    private bool deferredPlaylistSummaryRefreshRequested;

    private bool deferredPlaylistSummaryPresentationRefreshRequested;

    private int deferredPlaylistRefRequestedVersion;

    private bool deferredPlaylistRefRunning;

    private object lockDeferredPlaylistRef = new object();

    private readonly PlaylistViewState playlistViewState = new PlaylistViewState();

    private readonly object playlistLibraryIndexSync = new object();

    private PlaylistLibraryIndexSnapshot playlistLibraryIndexSnapshot;

    private long playlistLibraryIndexVersion;

    private Task<PlaylistLibraryIndexSnapshot> playlistLibraryIndexPrewarmTask;

    private long playlistLibraryIndexPrewarmVersion;

    private CancellationTokenSource playlistLibraryIndexPrewarmCancellation;

    private const int PlaylistLibraryIndexPrewarmDebounceMs = 500;

    private int deferredExternalSyncRequestedVersion;

    private bool deferredExternalSyncRunning;

    private object lockDeferredExternalSync = new object();

    private object lockPlaylistSyncStatuses = new object();

    private readonly Dictionary<string, PlaylistSyncRuntimeStatus> playlistSyncStatuses = new Dictionary<string, PlaylistSyncRuntimeStatus>(StringComparer.OrdinalIgnoreCase);

    private bool deferredLibraryFolderTreeRefreshQueued;

    private object lockDeferredLibraryFolderTreeRefresh = new object();

    private readonly object startupBackgroundTaskLock = new object();

    private readonly List<StartupBackgroundTaskRequest> startupBackgroundTaskQueue = new List<StartupBackgroundTaskRequest>();

    private readonly HashSet<string> startupBackgroundTaskCompletedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private bool startupBackgroundTaskSchedulerStarted;

    private bool startupBackgroundTaskWorkerRunning;

    private long startupBackgroundTaskVersion;

    private Stopwatch startupReadyInstallStopwatch;

    private Stopwatch startupReadyOperableStopwatch;

    private bool startupReadyDataLogged;

    private bool startupReadyUiLogged;

    private bool startupReadyDataReached;

    private bool startupReadyUiReached;

    private bool startupReadyOperableReached;

    private int deferredPlaylistRefLastCompletedVersion;

    private string _WindowTitle = "BeMusicSeeker Unofficial Fork - ";

    private IEnumerable<LibraryChartRow> ChartRowsFolderView;

    private IEnumerable<LibraryChartRow> ChartRowsKeywordFilterView;

    private IEnumerable<LibraryChartRow> ChartRowsModeFilterView;

    private IList _BMSFilesView = new List<object>();

    private cSortParameters _SortParameters;

    private cSortParameters _PlaylistSummarySortParameters;

    private BeMusicSeeker.Models.BMSFile _NowPlayingBMS;

    private int nowPlayingBmsFilesViewIndex = -1;

    private Uri _BrowserSource;

    private string _BrowserHtml;

    private int _SelectedIndexBMSFilesView;

    private dataGridColumnsSettings _ColumnsSettingsBMSFilesView;

    private List<LibraryChartRow> folderSortSourceSnapshot;

    private List<LibraryChartRow> folderSortResultSnapshot;

    private string folderSortColumnName;

    private ListSortDirection? folderSortDirection;

    private readonly NormalLibraryRowCache regularBmsLibraryRowCache;

    private readonly Dictionary<NormalLibrarySortCacheKey, List<LibraryChartRow>> normalLibrarySortCache = new Dictionary<NormalLibrarySortCacheKey, List<LibraryChartRow>>();

    private readonly object normalLibrarySortCacheLock = new object();

    private long normalLibrarySourceGeneration;

    private long normalLibrarySortKeyGeneration;

    private int pendingRegularBmsRowCachePrunedCount;

    private readonly Dictionary<string, LibraryChartRow> bmsonLibraryRowsByPath = new Dictionary<string, LibraryChartRow>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRow> bmsonLibraryRowsBySong = new Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRow>(BmsonSongReferenceComparer.Instance);

    private const long ColumnSettingSlowLogThresholdMs = 100L;

    private const int PlaylistBuildCoalescingWindowMs = 50;

    private const long PlaylistOpenSlowLogThresholdMs = 1000L;

    private const long PlaylistScoreProbeSlowLogThresholdMs = 500L;

    private const long PlaylistScoreProbeChunkSlowLogThresholdMs = 250L;

    private static long mainViewBuildRequestIdSeed;

    private long lastMainViewBuildRequestId;

    private long lastMainViewBuildEndTimestamp;

    private int lastMainViewBuildThreadId;

    private int lastMainViewBuildMode;

    private long lastPlaylistSummaryBuildCompletedTimestamp;

    private long lastPlaylistSummaryBuildElapsedMs;

    private long lastPlaylistDetailBuildCompletedTimestamp;

    private long lastPlaylistDetailBuildElapsedMs;

    private PlaylistSummaryColumnSettings _PlaylistSummaryColumnsSettings;

    private Visibility _ColumnSettingsVisibilityForPlaylist = Visibility.Collapsed;

    private ObservableCollection<PlaylistSummaryRow> _PlaylistSummaryView = new ObservableCollection<PlaylistSummaryRow>();

    private WeakReference<ObservableCollection<PlaylistSummaryRow>> previousPlaylistSummaryViewWeakReference;

    private readonly object lockPlaylistSummaryRowsCache = new object();

    private List<PlaylistSummaryRow> playlistSummaryRowsCache = new List<PlaylistSummaryRow>();

    private bool playlistSummaryRowsCacheValid;

    private readonly Dictionary<string, PlaylistSummaryTableCountCacheEntry> playlistSummaryTableCountCache = new Dictionary<string, PlaylistSummaryTableCountCacheEntry>(StringComparer.OrdinalIgnoreCase);

    private bool _IsPlaylistSummaryMode;

    private bool _IsPlaylistDetailViewActive;

    private bool _UseAsyncBMSFilesViewBinding = true;

    private string _GridHeaderText = string.Empty;

    private string _GridSummaryText = string.Empty;

    private DropInstallQueueProcessor dropInstallQueueProcessor;

    private bool _IsDropInstallQueueActive;

    private string _DropInstallQueueLabel = string.Empty;

    private string _DropInstallQueueSubLabel = string.Empty;

    private bool _DropInstallQueueCanCancel;

    private int _DropInstallQueuePendingBatchCount;

    private bool _IsPendingEstimateQueueActive;

    private string _PendingEstimateQueueLabel = string.Empty;

    private string _PendingEstimateQueueSubLabel = string.Empty;

    private int _PendingEstimateQueuePendingBatchCount;

    private DropInstallQueueStatusSnapshot latestDropInstallQueueStatus = new DropInstallQueueStatusSnapshot();

    private PendingInstallEstimateQueueStatusSnapshot latestPendingEstimateQueueStatus = new PendingInstallEstimateQueueStatusSnapshot();

    private InstallEstimationProgressSnapshot latestInstallEstimationProgress = new InstallEstimationProgressSnapshot();

    private bool _IsInstallPipelineStatusActive;

    private string _InstallPipelineLabel = string.Empty;

    private string _InstallPipelineSubLabel = string.Empty;

    private int _InstallPipelineValue;

    private int _InstallPipelineMaximum = 1;

    private bool _InstallPipelineCanCancel;

    private readonly object playlistSyncProgressLock = new object();

    private int playlistSyncProgressActiveOperationCount;

    private bool _IsPlaylistSyncProgressActive;

    private readonly object lockPlaylistReloadCleanup = new object();

    private PlaylistReloadCleanupRequest pendingPlaylistReloadCleanup;

    private bool playlistReloadCleanupRunning;

    private long playlistReloadCleanupSeed;

    private string _PlaylistSyncProgressLabel = string.Empty;

    private string _PlaylistSyncProgressSubLabel = string.Empty;

    private double _PlaylistSyncProgressValue;

    private double _PlaylistSyncProgressMaximum;

    private readonly object startupProgressLock = new object();

    private StartupProgressState startupProgressState = new StartupProgressState();

    private long startupProgressOperationTokenSeed;

    private bool _IsStartupProgressActive;

    private bool _IsStartupUiInteractionBlocked;

    private string _StartupProgressLabel = string.Empty;

    private string _StartupProgressSubLabel = string.Empty;

    private double _StartupProgressValue;

    private double _StartupProgressMaximum;

    private bool _IsPlaylistTreeExpanded = true;

    private ModeFilterType _ModeFilter = ModeFilterType.All;

    private string _KeywordFilter;

    private string _PlaylistSummaryKeywordFilter = string.Empty;

    private string _KeywordSearchWarningText = string.Empty;

    private string _PlaylistSummaryKeywordSearchWarningText = string.Empty;

    private bool _IsKeywordSearchHelpOpen;

    private bool _IsPlaylistSummaryKeywordSearchHelpOpen;

    private readonly ObservableCollection<KeywordSearchSuggestionItem> _KeywordSearchSuggestions = new ObservableCollection<KeywordSearchSuggestionItem>();

    private readonly ObservableCollection<KeywordSearchSuggestionItem> _PlaylistSummaryKeywordSearchSuggestions = new ObservableCollection<KeywordSearchSuggestionItem>();

    private readonly List<string> keywordSearchHistory = new List<string>();

    private readonly List<string> playlistSummaryKeywordSearchHistory = new List<string>();

    private bool _IsKeywordSearchSuggestionPopupOpen;

    private bool _IsPlaylistSummaryKeywordSearchSuggestionPopupOpen;

    private string _KeywordSearchSuggestionHeaderText = string.Empty;

    private string _PlaylistSummaryKeywordSearchSuggestionHeaderText = string.Empty;

    private PlaylistSummaryOwnedFilterType _PlaylistSummaryOwnedFilter = PlaylistSummaryOwnedFilterType.All;

    private Func<BeMusicSeeker.Models.BMSFile, bool> _FolderFilter;

    private DispatcherCollection<string> _sortedBmsParentFolderList = new DispatcherCollection<string>(DispatcherHelper.UIDispatcher);

    private bool bmsParentFolderListViewInitialized;

    private BMSTableSimpleCategorized _BMSExternalTableListExt;

    private bool _IsLoadingExternalCollectionBMSTables;

    private TimeSpan _CurrentlyPlayingDuration;

    private TimeSpan _CurrentlyPlayingStopTime;

    private TimeSpan _CurrentlyPlayingBmsDuration;

    private TimeSpan _CurrentlyPlayingMusicDuration;

    private int _CurrentlyPlayingCurrentVoices;

    private int _CurrentlyPlayingMaxVoices;

    private int _CurrentlyPlayingNoteDensity;

    private int _CurrentlyPlayingNoteDensityMax;

    private int _CurrentlyPlayingBpm;

    private int _CurrentlyPlayingMinBpm;

    private int _CurrentlyPlayingMaxBpm;

    private double _CurrentlyPlayingTotal;

    private int _CurrentlyPlayingCombo;

    private int _CurrentlyPlayingNotes;

    private int _CurrentlyPlayingMeasure;

    private int _CurrentlyPlayingLastMeasure;

    private viewUpdateMode treeViewFilterTypeSelected = Settings.Default.StartupSelectInstallPending ? viewUpdateMode.PendingInstallFolderSelected : viewUpdateMode.FolderFilterSelected;

    private object treeViewFilterParameterSelected;

    private sealed class DuplicateViewContext
    {
        internal DuplicateViewContextKind Kind { get; }

        internal string Value { get; }

        private DuplicateViewContext(DuplicateViewContextKind kind, string value)
        {
            Kind = kind;
            Value = value;
        }

        internal static DuplicateViewContext ForGroup(string header)
        {
            return new DuplicateViewContext(DuplicateViewContextKind.GroupHeader, header);
        }

        internal static DuplicateViewContext ForFolder(string folderPath)
        {
            return new DuplicateViewContext(DuplicateViewContextKind.FolderPath, folderPath);
        }
    }

    private enum DuplicateViewContextKind
    {
        GroupHeader,
        FolderPath
    }

    private static string scoreRegisterUrl = "https://bms-score-viewer-backend.sayakaisbaka.workers.dev/bms/score/register";

    private static string scoreStatusUrl = "https://bms-score-viewer-backend.sayakaisbaka.workers.dev/bms/score/status?md5=";

    private static string scoreViewUrl = "https://bms-score-viewer.pages.dev/view?md5=";

    public SettingDialogViewModel settingDialog { get; private set; }

    private static void LogUiSuppression(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    private static void LogUiSuppressionWarning(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Warn(message);
        }
    }

    private static void LogDeferredPlaylistReference(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    private static void LogDeferredExternalSync(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    private static void LogInitStage(string stage, string scope)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info("init_stage " + stage + " scope=" + scope);
        }
    }

    private static void LogMainViewBuild(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    /// <summary>
    /// プレイリスト source build の診断ログを出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistSourceBuild(string message)
    {
        LogMainViewBuild("playlist_source_build " + message);
    }

    /// <summary>
    /// プレイリスト source からの view 適用ログを出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistViewApply(string message)
    {
        LogMainViewBuild("playlist_view_apply " + message);
    }

    /// <summary>
    /// playlist source/view の所有権と世代遷移を診断ログへ出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistRetention(string message)
    {
        LogMainViewBuild(message);
    }

    /// <summary>
    /// playlist build request / worker の制御ログを出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistWorker(string message)
    {
        LogMainViewBuild(message);
    }

    /// <summary>
    /// playlist open interaction の summary/slow ログを出力します。
    /// </summary>
    /// <param name="message">出力するログ本文。</param>
    private static void LogPlaylistOpen(string message)
    {
        LogMainViewBuild(message);
    }

    /// <summary>
    /// playlist reload operation と cleanup の診断ログを出力します。
    /// </summary>
    private static void LogPlaylistReload(string message)
    {
        LogMainViewBuild(message);
    }

    /// <summary>
    /// playlist reload operation kind をログ用文字列へ変換します。
    /// </summary>
    private static string GetPlaylistReloadOperationKindText(PlaylistReloadOperationKind operationKind)
    {
        switch (operationKind)
        {
            case PlaylistReloadOperationKind.StartupFullReload:
                return "startup_full";
            case PlaylistReloadOperationKind.ManualFullReload:
                return "manual_full";
            case PlaylistReloadOperationKind.SinglePlaylistReload:
                return "single";
            default:
                return "none";
        }
    }

    /// <summary>
    /// deferred external sync の起点から playlist reload operation kind を判定します。
    /// </summary>
    private static PlaylistReloadOperationKind DeterminePlaylistReloadOperationKind(string reason, bool fromReloadTables)
    {
        if (string.Equals(reason, "Initialize", StringComparison.Ordinal))
        {
            return PlaylistReloadOperationKind.StartupFullReload;
        }
        if (fromReloadTables || string.Equals(reason, "ReloadTables", StringComparison.Ordinal))
        {
            return PlaylistReloadOperationKind.ManualFullReload;
        }
        return PlaylistReloadOperationKind.None;
    }

    /// <summary>
    /// playlist request の folder 名を同値判定向けに正規化します。
    /// </summary>
    internal static string NormalizePlaylistFolderName(string folderName)
    {
        if (folderName == null)
        {
            return null;
        }
        return folderName.Trim();
    }

    /// <summary>
    /// playlist request の keyword filter を同値判定向けに正規化します。
    /// </summary>
    internal static string NormalizePlaylistKeywordFilter(string keywordFilter)
    {
        if (string.IsNullOrWhiteSpace(keywordFilter))
        {
            return string.Empty;
        }
        return keywordFilter.Trim().ToUpperInvariant();
    }

    internal static string BuildKeywordSearchWarningText(string keywordFilter, GridKeywordSearchContext context)
    {
        GridKeywordSearchQuery query = GridKeywordSearchQuery.Parse(keywordFilter);
        IReadOnlyList<GridKeywordSearchDiagnostic> diagnostics = query.GetDiagnostics(context);
        if (diagnostics.Count == 0)
        {
            return string.Empty;
        }
        return string.Join(
            Environment.NewLine,
            diagnostics
                .Select(FormatKeywordSearchDiagnostic)
                .Where((string text) => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal));
    }

    internal static string BuildKeywordSearchHelpText(GridKeywordSearchContext context)
    {
        string fields;
        switch (context)
        {
            case GridKeywordSearchContext.PlaylistDetail:
                fields = BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_playlist_detail;
                break;
            case GridKeywordSearchContext.PlaylistSummary:
                fields = BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_playlist_summary;
                break;
            default:
                fields = BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_bmsfile;
                break;
        }
        return string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_help_template, fields);
    }

    /// <summary>
    /// 検索候補 popup の見出しを返します。
    /// field 補完と履歴は同じ popup に載るため、候補種別に応じて表示を切り替えます。
    /// </summary>
    /// <param name="kind">候補種別。</param>
    /// <returns>popup 見出し。</returns>
    internal static string BuildKeywordSearchSuggestionHeaderText(KeywordSearchSuggestionKind kind)
    {
        return kind == KeywordSearchSuggestionKind.History
            ? BeMusicSeeker.Properties.Resources.Keyword_search_completion_history_header
            : BeMusicSeeker.Properties.Resources.Keyword_search_completion_fields_header;
    }

    /// <summary>
    /// 検索履歴候補を作ります。
    /// </summary>
    /// <param name="history">履歴一覧。</param>
    /// <param name="currentText">現在の検索文字列。</param>
    /// <returns>履歴候補一覧。</returns>
    internal static IReadOnlyList<KeywordSearchSuggestionItem> BuildKeywordSearchHistorySuggestions(IEnumerable<string> history, string currentText)
    {
        string safeText = currentText ?? string.Empty;
        return (history ?? Enumerable.Empty<string>())
            .Where((string entry) => !string.IsNullOrWhiteSpace(entry))
            .Select((string entry) => new KeywordSearchSuggestionItem(KeywordSearchSuggestionKind.History, entry, entry, 0, safeText.Length))
            .ToArray();
    }

    private static string FormatKeywordSearchDiagnostic(GridKeywordSearchDiagnostic diagnostic)
    {
        switch (diagnostic.Kind)
        {
            case GridKeywordSearchDiagnosticKind.UnknownField:
                return string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_unknown_field, diagnostic.Value);
            case GridKeywordSearchDiagnosticKind.EmptyFieldTerm:
                return string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_field_term, diagnostic.Value);
            case GridKeywordSearchDiagnosticKind.EmptyNegation:
                return BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_negation;
            case GridKeywordSearchDiagnosticKind.EmptyOr:
                return BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_or;
            case GridKeywordSearchDiagnosticKind.InvalidRegex:
                return string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_invalid_regex, diagnostic.Value);
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// playlist request の sort 列名を同値判定向けに正規化します。
    /// </summary>
    internal static string NormalizePlaylistSortColumnName(cSortParameters sortParameters)
    {
        return sortParameters?.ColumnsName ?? string.Empty;
    }

    /// <summary>
    /// playlist request の sort 方向を同値判定向けに正規化します。
    /// </summary>
    internal static ListSortDirection NormalizePlaylistSortDirection(cSortParameters sortParameters)
    {
        return sortParameters?.Direction ?? ListSortDirection.Ascending;
    }

    /// <summary>
    /// request identity に source rebuild 前の quiet window を適用するかを返します。
    /// </summary>
    private static bool ShouldUsePlaylistBuildCoalescingWindow(viewUpdateMode mode, viewUpdateMode requestedMode)
    {
        return IsPlaylistViewMode(mode) || requestedMode == viewUpdateMode.TreeViewFilterNotChanged;
    }

    /// <summary>
    /// 現在の UI 条件を反映した playlist request identity を生成します。
    /// </summary>
    internal static PlaylistRequestIdentity CreatePlaylistRequestIdentity(BMSTable table, string folderName, PlaylistFilterType filterType, string keywordFilter, ModeFilterType modeFilter, cSortParameters sortParameters, long libraryIndexVersion, long playlistRevision, int scoreSnapshotVersion, int chartInfoIndexVersion, bool hasResolvedSelection)
    {
        return new PlaylistRequestIdentity(table, NormalizePlaylistFolderName(folderName), filterType, NormalizePlaylistKeywordFilter(keywordFilter), modeFilter, NormalizePlaylistSortColumnName(sortParameters), NormalizePlaylistSortDirection(sortParameters), libraryIndexVersion, playlistRevision, scoreSnapshotVersion, chartInfoIndexVersion, hasResolvedSelection);
    }

    /// <summary>
    /// playlist source rebuild が必要になった理由を返します。
    /// </summary>
    private static string DeterminePlaylistSourceInvalidationReason(bool selectionChanged, bool libraryIndexInvalidated, bool playlistRevisionInvalidated, bool scoreSnapshotInvalidated, bool chartInfoIndexInvalidated, bool sourceMissing)
    {
        if (selectionChanged)
        {
            return "selection_changed";
        }
        if (libraryIndexInvalidated)
        {
            return "library_index";
        }
        if (playlistRevisionInvalidated)
        {
            return "playlist_revision";
        }
        if (scoreSnapshotInvalidated)
        {
            return "score_snapshot_version";
        }
        if (chartInfoIndexInvalidated)
        {
            return "chart_info_index";
        }
        if (sourceMissing)
        {
            return "source_missing";
        }
        return "none";
    }

    /// <summary>
    /// playlist source rebuild 理由判定をテストから呼び出せるようにします。
    /// </summary>
    internal static string DeterminePlaylistSourceInvalidationReasonForTest(bool selectionChanged, bool libraryIndexInvalidated, bool playlistRevisionInvalidated, bool scoreSnapshotInvalidated, bool chartInfoIndexInvalidated, bool sourceMissing)
    {
        return DeterminePlaylistSourceInvalidationReason(selectionChanged, libraryIndexInvalidated, playlistRevisionInvalidated, scoreSnapshotInvalidated, chartInfoIndexInvalidated, sourceMissing);
    }

    /// <summary>
    /// playlist 系表示モードかどうかを判定します。
    /// </summary>
    /// <param name="mode">判定対象モード。</param>
    /// <returns>playlist 系であれば <see langword="true"/>。</returns>
    private static bool IsPlaylistViewMode(viewUpdateMode mode)
    {
        return mode == viewUpdateMode.PlaylistFilterSelected || mode == viewUpdateMode.PlaylistNotOwnedFilterSelected;
    }

    /// <summary>
    /// 現在の更新要求がプレイリスト詳細ビューの再描画経路を使うべきかを判定します。
     /// </summary>
    /// <param name="mode">今回の更新モード。</param>
    /// <param name="currentTreeMode">現在選択中の tree モード。</param>
    /// <returns>プレイリスト詳細ビューの再描画経路を使う場合は <see langword="true"/>。</returns>
    private static bool IsPlaylistTreeActive(viewUpdateMode mode, viewUpdateMode currentTreeMode)
    {
        if (IsPlaylistViewMode(mode))
        {
            return true;
        }
        if (!IsPlaylistViewMode(currentTreeMode))
        {
            return false;
        }
        return mode == viewUpdateMode.TreeViewFilterNotChanged || mode == viewUpdateMode.KeywordFilterUpdated || mode == viewUpdateMode.ModeFilterUpdated || mode == viewUpdateMode.SortUpdated;
    }

    /// <summary>
    /// 指定行集合に含まれる playlist lightweight row 件数を返します。
    /// </summary>
    /// <param name="rows">判定対象行集合。</param>
    /// <returns>playlist row 件数。</returns>
    private static int CountPlaylistDetailRows(IEnumerable rows)
    {
        if (rows == null)
        {
            return 0;
        }
        int count = 0;
        foreach (object row in rows)
        {
            if (row is PlaylistDetailRow)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 指定行集合に含まれる playlist source row 件数を返します。
    /// </summary>
    /// <param name="rows">判定対象行集合。</param>
    /// <returns>source row 件数。</returns>
    private static int CountPlaylistSourceRows(IEnumerable<PlaylistDetailSourceRow> rows)
    {
        return rows?.Count() ?? 0;
    }

    /// <summary>
    /// playlist 詳細表示時の一覧反映方式を更新します。
    /// </summary>
    /// <param name="playlistDetailActive">playlist 詳細表示中かどうか。</param>
    private void UpdateBmsFilesViewBindingMode(bool playlistDetailActive)
    {
        if (_IsPlaylistDetailViewActive != playlistDetailActive)
        {
            _IsPlaylistDetailViewActive = playlistDetailActive;
            RaisePropertyChanged("IsPlaylistDetailViewActive");
        }
        bool nextUseAsyncBinding = !playlistDetailActive;
        if (_UseAsyncBMSFilesViewBinding != nextUseAsyncBinding)
        {
            _UseAsyncBMSFilesViewBinding = nextUseAsyncBinding;
            RaisePropertyChanged("UseAsyncBMSFilesViewBinding");
        }
    }

    /// <summary>
    /// 直前に解放した playlist source/view がまだ生存しているかを診断ログへ出力します。
    /// </summary>
    /// <param name="reason">確認契機。</param>
    private void LogPlaylistWeakReferenceStatus(string reason)
    {
        WeakReference<List<PlaylistDetailSourceRow>> previousSourceWeakReference;
        WeakReference<IList> previousViewWeakReference;
        long previousSourceGenerationId;
        long previousViewGenerationId;
        lock (playlistViewState.SyncRoot)
        {
            previousSourceWeakReference = playlistViewState.PreviousSourceRowsWeakReference;
            previousViewWeakReference = playlistViewState.PreviousViewRowsWeakReference;
            previousSourceGenerationId = playlistViewState.PreviousSourceGenerationId;
            previousViewGenerationId = playlistViewState.PreviousViewGenerationId;
        }
        List<PlaylistDetailSourceRow> previousSourceRows = null;
        IList previousViewRows = null;
        bool previousSourceAlive = previousSourceWeakReference != null && previousSourceWeakReference.TryGetTarget(out previousSourceRows);
        bool previousViewAlive = previousViewWeakReference != null && previousViewWeakReference.TryGetTarget(out previousViewRows);
        LogPlaylistRetention("playlist_weak_reference_check reason=" + reason + " sourceGenerationId=" + previousSourceGenerationId + " sourceAlive=" + previousSourceAlive + " playlistSourceRowCount=" + (previousSourceAlive ? CountPlaylistSourceRows(previousSourceRows) : 0) + " viewGenerationId=" + previousViewGenerationId + " viewAlive=" + previousViewAlive + " playlistViewRowCount=" + (previousViewAlive ? CountPlaylistDetailRows(previousViewRows) : 0));
    }

    /// <summary>
    /// 直前に差し替えた playlist summary collection の弱参照状態を返します。
    /// </summary>
    private bool TryGetPreviousPlaylistSummaryViewState(out bool alive, out int rowCount)
    {
        ObservableCollection<PlaylistSummaryRow> previousSummaryRows = null;
        alive = previousPlaylistSummaryViewWeakReference != null && previousPlaylistSummaryViewWeakReference.TryGetTarget(out previousSummaryRows);
        rowCount = alive ? previousSummaryRows.Count : 0;
        return alive;
    }

    /// <summary>
    /// 現在の playlist detail 表示が full reload 後 cleanup で待機対象になるかを返します。
    /// </summary>
    private bool IsPlaylistDetailRefreshWaitRequired()
    {
        return IsPlaylistViewMode(treeViewFilterTypeSelected);
    }

    /// <summary>
    /// 現在の playlist detail 表示を full reload 後に再構築します。
    /// current source/view の寿命短縮を優先し、通常切り替え経路には影響させません。
    /// </summary>
    private void RefreshPlaylistDetailAfterReloadIfVisible()
    {
        if (!IsPlaylistDetailRefreshWaitRequired())
        {
            return;
        }
        makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
    }

    /// <summary>
    /// playlist reload 完了後 cleanup を full reload 系 operation に対してだけキューします。
    /// </summary>
    private bool QueuePlaylistReloadCleanup(PlaylistReloadOperationKind operationKind, int tableCount)
    {
        if (operationKind != PlaylistReloadOperationKind.StartupFullReload && operationKind != PlaylistReloadOperationKind.ManualFullReload)
        {
            LogPlaylistReload("playlist_reload_cleanup skipped operationKind=" + GetPlaylistReloadOperationKindText(operationKind) + " tableCount=" + tableCount + " reason=not_full_reload");
            return false;
        }
        PlaylistReloadCleanupRequest request = new PlaylistReloadCleanupRequest
        {
            CleanupId = Interlocked.Increment(ref playlistReloadCleanupSeed),
            OperationKind = operationKind,
            TableCount = tableCount,
            WaitForStartupOperable = operationKind == PlaylistReloadOperationKind.StartupFullReload,
            WaitForSummaryRefresh = IsPlaylistSummaryMode,
            WaitForDetailRefresh = IsPlaylistDetailRefreshWaitRequired(),
            GcAllowed = true,
            RequestedAtTimestamp = Stopwatch.GetTimestamp()
        };
        bool shouldStartWorker = false;
        lock (lockPlaylistReloadCleanup)
        {
            pendingPlaylistReloadCleanup = request;
            if (!playlistReloadCleanupRunning)
            {
                playlistReloadCleanupRunning = true;
                shouldStartWorker = true;
            }
        }
        LogPlaylistReload("playlist_reload_cleanup queued cleanupId=" + request.CleanupId + " operationKind=" + GetPlaylistReloadOperationKindText(operationKind) + " tableCount=" + tableCount + " waitForStartupOperable=" + request.WaitForStartupOperable.ToString().ToLowerInvariant() + " waitForSummaryRefresh=" + request.WaitForSummaryRefresh.ToString().ToLowerInvariant() + " waitForDetailRefresh=" + request.WaitForDetailRefresh.ToString().ToLowerInvariant() + " gcAllowed=" + request.GcAllowed.ToString().ToLowerInvariant());
        if (shouldStartWorker)
        {
            Task.Run(ProcessPendingPlaylistReloadCleanupAsync).Logging("ProcessPendingPlaylistReloadCleanupAsync");
        }
        return true;
    }

    /// <summary>
    /// summary collection 差し替えや detail rebuild 後に pending cleanup の実行を促します。
    /// </summary>
    private void TrySchedulePlaylistReloadCleanup()
    {
        bool shouldStartWorker = false;
        lock (lockPlaylistReloadCleanup)
        {
            if (pendingPlaylistReloadCleanup != null && !playlistReloadCleanupRunning)
            {
                playlistReloadCleanupRunning = true;
                shouldStartWorker = true;
            }
        }
        if (shouldStartWorker)
        {
            Task.Run(ProcessPendingPlaylistReloadCleanupAsync).Logging("ProcessPendingPlaylistReloadCleanupAsync");
        }
    }

    /// <summary>
    /// playlist reload cleanup worker ループです。
    /// full reload 完了後だけ UI idle と必要な再反映完了を待ってから後始末を実施します。
    /// </summary>
    private async Task ProcessPendingPlaylistReloadCleanupAsync()
    {
        while (true)
        {
            PlaylistReloadCleanupRequest request;
            lock (lockPlaylistReloadCleanup)
            {
                request = pendingPlaylistReloadCleanup;
                pendingPlaylistReloadCleanup = null;
                if (request == null)
                {
                    playlistReloadCleanupRunning = false;
                    return;
                }
            }
            await WaitForPlaylistReloadCleanupReadinessAsync(request).ConfigureAwait(false);
            bool oldSummaryAlive;
            int oldSummaryRowCount;
            TryGetPreviousPlaylistSummaryViewState(out oldSummaryAlive, out oldSummaryRowCount);
            WeakReference<List<PlaylistDetailSourceRow>> previousSourceWeakReference;
            WeakReference<IList> previousViewWeakReference;
            lock (playlistViewState.SyncRoot)
            {
                previousSourceWeakReference = playlistViewState.PreviousSourceRowsWeakReference;
                previousViewWeakReference = playlistViewState.PreviousViewRowsWeakReference;
            }
            List<PlaylistDetailSourceRow> previousSourceRows = null;
            IList previousViewRows = null;
            bool oldDetailSourceAlive = previousSourceWeakReference != null && previousSourceWeakReference.TryGetTarget(out previousSourceRows);
            bool oldDetailViewAlive = previousViewWeakReference != null && previousViewWeakReference.TryGetTarget(out previousViewRows);
            long managedMemoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: false);
            bool gcInvoked = request.GcAllowed;
            if (gcInvoked)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            long managedMemoryAfterBytes = GC.GetTotalMemory(forceFullCollection: false);
            LogPlaylistReload("playlist_reload_cleanup completed cleanupId=" + request.CleanupId + " operationKind=" + GetPlaylistReloadOperationKindText(request.OperationKind) + " tableCount=" + request.TableCount + " oldSummaryAlive=" + oldSummaryAlive.ToString().ToLowerInvariant() + " oldSummaryRowCount=" + oldSummaryRowCount + " oldDetailSourceAlive=" + oldDetailSourceAlive.ToString().ToLowerInvariant() + " oldDetailSourceRowCount=" + (oldDetailSourceAlive ? CountPlaylistSourceRows(previousSourceRows) : 0) + " oldDetailViewAlive=" + oldDetailViewAlive.ToString().ToLowerInvariant() + " oldDetailViewRowCount=" + (oldDetailViewAlive ? CountPlaylistDetailRows(previousViewRows) : 0) + " managedMemoryBeforeMb=" + (managedMemoryBeforeBytes / 1024L / 1024L) + " managedMemoryAfterMb=" + (managedMemoryAfterBytes / 1024L / 1024L) + " gcInvoked=" + gcInvoked.ToString().ToLowerInvariant());
        }
    }

    /// <summary>
    /// playlist reload cleanup 実行前に必要な UI / build 完了を待機します。
    /// </summary>
    private async Task WaitForPlaylistReloadCleanupReadinessAsync(PlaylistReloadCleanupRequest request)
    {
        if (request.WaitForStartupOperable)
        {
            Stopwatch operableWaitStopwatch = Stopwatch.StartNew();
            while (!startupReadyOperableReached && operableWaitStopwatch.ElapsedMilliseconds < 30000)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        if (request.WaitForSummaryRefresh)
        {
            Stopwatch summaryWaitStopwatch = Stopwatch.StartNew();
            while (Interlocked.Read(ref lastPlaylistSummaryBuildCompletedTimestamp) < request.RequestedAtTimestamp && summaryWaitStopwatch.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        if (request.WaitForDetailRefresh)
        {
            Stopwatch detailWaitStopwatch = Stopwatch.StartNew();
            while (Interlocked.Read(ref lastPlaylistDetailBuildCompletedTimestamp) < request.RequestedAtTimestamp && detailWaitStopwatch.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        if (DispatcherHelper.UIDispatcher != null)
        {
            await DispatcherHelper.UIDispatcher.InvokeAsync(delegate
            {
            }, DispatcherPriority.ContextIdle).Task.ConfigureAwait(false);
            await DispatcherHelper.UIDispatcher.InvokeAsync(delegate
            {
            }, DispatcherPriority.ApplicationIdle).Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// playlist 用ライブラリ索引 snapshot を無効化します。
    /// </summary>
    /// <param name="reason">無効化理由。</param>
    private void InvalidatePlaylistLibraryIndexSnapshot(string reason)
    {
        long nextVersion;
        lock (playlistLibraryIndexSync)
        {
            playlistLibraryIndexSnapshot = null;
            nextVersion = ++playlistLibraryIndexVersion;
        }
        LogPlaylistWorker("playlist_library_index invalidated version=" + nextVersion + " reason=" + reason);
        if (!startupReadyOperableReached)
        {
            LogPlaylistWorker("playlist_library_index_prewarm deferred_until_operable version=" + nextVersion + " reason=" + reason);
            return;
        }
        SchedulePlaylistLibraryIndexPrewarm(nextVersion, reason);
    }

    /// <summary>
    /// playlist filter 種別に対応する列設定モードを返します。
    /// 増分更新契機ではなく、現在表示すべき playlist 列構成を明示するために使用します。
    /// </summary>
    /// <param name="filterType">playlist filter 種別。</param>
    /// <returns>列設定に使う viewUpdateMode。</returns>
    private static viewUpdateMode ResolvePlaylistColumnSettingMode(PlaylistFilterType filterType)
    {
        return (filterType == PlaylistFilterType.PlaylistNotOwnedFilterSelected) ? viewUpdateMode.PlaylistNotOwnedFilterSelected : viewUpdateMode.PlaylistFilterSelected;
    }

    /// <summary>
    /// playlist 列設定モード解決をテストします。
    /// </summary>
    /// <param name="filterType">playlist filter 種別。</param>
    /// <returns>解決された列設定モード。</returns>
    internal static int ResolvePlaylistColumnSettingModeForTest(int filterType)
    {
        return (int)ResolvePlaylistColumnSettingMode((PlaylistFilterType)filterType);
    }

    /// <summary>
    /// playlist 用ライブラリ索引の prewarm を background で開始します。
    /// </summary>
    /// <param name="targetVersion">prewarm 対象版数。</param>
    /// <param name="reason">開始理由。</param>
    private void SchedulePlaylistLibraryIndexPrewarm(long targetVersion, string reason)
    {
        if (BMSFiles == null)
        {
            return;
        }
        bool debounce = ShouldDebouncePlaylistLibraryIndexPrewarm(reason);
        int delayMs = debounce ? PlaylistLibraryIndexPrewarmDebounceMs : 0;
        if (!debounce && string.Equals(reason, "initialize_completed", StringComparison.OrdinalIgnoreCase))
        {
            LogPlaylistWorker("playlist_library_index_prewarm queued version=" + targetVersion + " reason=" + reason + " debounceMs=" + delayMs + " source=scheduler");
            if (QueueStartupBackgroundTask("playlist_library_index_prewarm", reason, null, async delegate
            {
                Task<PlaylistLibraryIndexSnapshot> task = StartPlaylistLibraryIndexPrewarmTask(targetVersion, reason, delayMs, "scheduler");
                if (task != null)
                {
                    await task.ConfigureAwait(false);
                }
            }))
            {
                return;
            }
        }
        StartPlaylistLibraryIndexPrewarmTask(targetVersion, reason, delayMs, debounce ? "debounce" : "inline");
    }

    private Task<PlaylistLibraryIndexSnapshot> StartPlaylistLibraryIndexPrewarmTask(long targetVersion, string reason, int delayMs, string source)
    {
        lock (playlistLibraryIndexSync)
        {
            if (playlistLibraryIndexVersion != targetVersion)
            {
                LogPlaylistWorker("playlist_library_index_prewarm stale_skipped version=" + targetVersion + " currentVersion=" + playlistLibraryIndexVersion + " reason=" + reason + " source=" + source);
                return null;
            }
            if (playlistLibraryIndexPrewarmTask != null && !playlistLibraryIndexPrewarmTask.IsCompleted && playlistLibraryIndexPrewarmVersion == targetVersion)
            {
                return playlistLibraryIndexPrewarmTask;
            }
            if (playlistLibraryIndexPrewarmTask != null && !playlistLibraryIndexPrewarmTask.IsCompleted)
            {
                playlistLibraryIndexPrewarmCancellation?.Cancel();
                LogPlaylistWorker("playlist_library_index_prewarm debounced oldVersion=" + playlistLibraryIndexPrewarmVersion + " newVersion=" + targetVersion + " reason=" + reason);
            }
            playlistLibraryIndexPrewarmCancellation = new CancellationTokenSource();
            CancellationToken prewarmToken = playlistLibraryIndexPrewarmCancellation.Token;
            playlistLibraryIndexPrewarmVersion = targetVersion;
            LogPlaylistWorker("playlist_library_index_prewarm scheduled version=" + targetVersion + " reason=" + reason + " debounceMs=" + delayMs + " source=" + source);
            playlistLibraryIndexPrewarmTask = Task.Run(async delegate
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                LogPlaylistWorker("playlist_library_index_prewarm started version=" + targetVersion + " reason=" + reason + " source=" + source);
                try
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, prewarmToken).ConfigureAwait(false);
                    }
                    prewarmToken.ThrowIfCancellationRequested();
                    PlaylistLibraryIndexSnapshot snapshot = CreatePlaylistLibraryIndexSnapshot(prewarmToken, targetVersion);
                    LogPlaylistWorker("playlist_library_index_prewarm completed version=" + targetVersion + " filesByHashCount=" + snapshot.FilesByHash.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " source=" + source);
                    return snapshot;
                }
                catch (OperationCanceledException)
                {
                    LogPlaylistWorker("playlist_library_index_prewarm cancelled version=" + targetVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " source=" + source);
                    throw;
                }
                catch (Exception ex)
                {
                    LogPlaylistWorker("playlist_library_index_prewarm failed version=" + targetVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " exception=" + ex.GetType().Name + " source=" + source);
                    throw;
                }
            });
            return playlistLibraryIndexPrewarmTask;
        }
    }

    private static bool ShouldDebouncePlaylistLibraryIndexPrewarm(string reason)
    {
        return string.Equals(reason, "library_bmsfiles_changed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "library_bmsons_changed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 現在のライブラリから playlist 用 hash index snapshot を構築します。
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <param name="targetVersion">期待する版数。</param>
    /// <returns>構築された snapshot。</returns>
    internal static LR2SongDBExtended.bmson_song ChoosePreferredBmsonRepresentative(LR2SongDBExtended.bmson_song existing, LR2SongDBExtended.bmson_song candidate)
    {
        if (existing == null)
        {
            return candidate;
        }
        if (candidate == null)
        {
            return existing;
        }
        return string.Compare(candidate.path ?? string.Empty, existing.path ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0 ? candidate : existing;
    }

    internal static LR2SongDBExtended.bmson_song ResolveBmsonForPlaylistEntry(BMSTableEntry entry, IReadOnlyDictionary<string, LR2SongDBExtended.bmson_song> bmsonByMd5, IReadOnlyDictionary<string, LR2SongDBExtended.bmson_song> bmsonBySha256)
    {
        if (entry == null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(entry.md5) && bmsonByMd5 != null && bmsonByMd5.TryGetValue(entry.md5, out LR2SongDBExtended.bmson_song resolvedByMd5))
        {
            return resolvedByMd5;
        }
        if (!string.IsNullOrWhiteSpace(entry.sha256) && bmsonBySha256 != null && bmsonBySha256.TryGetValue(entry.sha256, out LR2SongDBExtended.bmson_song resolvedBySha256))
        {
            return resolvedBySha256;
        }
        return null;
    }

    internal static LR2SongDBExtended.chart_info ResolveChartInfoForPlaylistEntry(BMSTableEntry entry, IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> chartInfoByMd5, IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> chartInfoBySha256)
    {
        if (entry == null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(entry.sha256) && chartInfoBySha256 != null && chartInfoBySha256.TryGetValue(entry.sha256, out LR2SongDBExtended.chart_info resolvedBySha256))
        {
            return resolvedBySha256;
        }
        if (!string.IsNullOrWhiteSpace(entry.md5) && chartInfoByMd5 != null && chartInfoByMd5.TryGetValue(entry.md5, out LR2SongDBExtended.chart_info resolvedByMd5))
        {
            return resolvedByMd5;
        }
        return null;
    }

    internal static bool ShouldCreatePlaylistScoreProbeForTest(bool hasRealFile, bool hasResolvedBmson)
    {
        return ShouldCreatePlaylistScoreProbe(
            hasRealFile ? new PlaylistScoreProbeBmsFile() { path = "owned.bms" } : null,
            hasResolvedBmson ? new LR2SongDBExtended.bmson_song { path = "owned.bmson" } : null);
    }

    private static bool ShouldCreatePlaylistScoreProbe(BeMusicSeeker.Models.BMSFile realFile, LR2SongDBExtended.bmson_song resolvedBmson)
    {
        if (realFile != null && !string.IsNullOrWhiteSpace(realFile.path))
        {
            return false;
        }
        return resolvedBmson == null || string.IsNullOrWhiteSpace(resolvedBmson.path);
    }

    private PlaylistLibraryIndexSnapshot CreatePlaylistLibraryIndexSnapshot(CancellationToken cancellationToken, long targetVersion)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, BeMusicSeeker.Models.BMSFile> filesByHash = new Dictionary<string, BeMusicSeeker.Models.BMSFile>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, BeMusicSeeker.Models.BMSFile> filesBySha256 = new Dictionary<string, BeMusicSeeker.Models.BMSFile>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LR2SongDBExtended.bmson_song> bmsonByMd5 = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LR2SongDBExtended.bmson_song> bmsonBySha256 = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        foreach (BeMusicSeeker.Models.BMSFile file in BMSFiles ?? Enumerable.Empty<BeMusicSeeker.Models.BMSFile>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(file.hash) && !filesByHash.ContainsKey(file.hash))
            {
                filesByHash[file.hash] = file;
            }
            if (!string.IsNullOrWhiteSpace(file.sha256) && !filesBySha256.ContainsKey(file.sha256))
            {
                filesBySha256[file.sha256] = file;
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in files?.BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (song == null || string.IsNullOrWhiteSpace(song.path))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(song.md5))
            {
                bmsonByMd5[song.md5] = ChoosePreferredBmsonRepresentative(bmsonByMd5.TryGetValue(song.md5, out LR2SongDBExtended.bmson_song existingByMd5) ? existingByMd5 : null, song);
            }
            if (!string.IsNullOrWhiteSpace(song.sha256))
            {
                bmsonBySha256[song.sha256] = ChoosePreferredBmsonRepresentative(bmsonBySha256.TryGetValue(song.sha256, out LR2SongDBExtended.bmson_song existingBySha256) ? existingBySha256 : null, song);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistLibraryIndexSnapshot newSnapshot = new PlaylistLibraryIndexSnapshot
        {
            Version = targetVersion,
            BuildElapsedMs = stopwatch.ElapsedMilliseconds,
            FilesByHash = filesByHash,
            FilesBySha256 = filesBySha256,
            BmsonByMd5 = bmsonByMd5,
            BmsonBySha256 = bmsonBySha256
        };
        lock (playlistLibraryIndexSync)
        {
            if (playlistLibraryIndexSnapshot == null && playlistLibraryIndexVersion == targetVersion)
            {
                playlistLibraryIndexSnapshot = newSnapshot;
            }
            return playlistLibraryIndexSnapshot ?? newSnapshot;
        }
    }

    /// <summary>
    /// 現在のライブラリから playlist source build 用の hash index snapshot を取得します。
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>hash index snapshot。</returns>
    private PlaylistLibraryIndexSnapshot GetOrCreatePlaylistLibraryIndexSnapshot(CancellationToken cancellationToken, out string accessKind, out long buildElapsedMs)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistLibraryIndexSnapshot cachedSnapshot;
        long currentVersion;
        Task<PlaylistLibraryIndexSnapshot> prewarmTask;
        lock (playlistLibraryIndexSync)
        {
            cachedSnapshot = playlistLibraryIndexSnapshot;
            currentVersion = playlistLibraryIndexVersion;
            if (cachedSnapshot != null && cachedSnapshot.Version == currentVersion)
            {
                accessKind = "cached";
                buildElapsedMs = cachedSnapshot.BuildElapsedMs;
                return cachedSnapshot;
            }
            prewarmTask = playlistLibraryIndexPrewarmTask != null && playlistLibraryIndexPrewarmVersion == currentVersion ? playlistLibraryIndexPrewarmTask : null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (prewarmTask != null)
        {
            try
            {
                PlaylistLibraryIndexSnapshot prewarmedSnapshot = prewarmTask.GetAwaiter().GetResult();
                if (prewarmedSnapshot != null && prewarmedSnapshot.Version == currentVersion)
                {
                    accessKind = "prewarmed";
                    buildElapsedMs = prewarmedSnapshot.BuildElapsedMs;
                    return prewarmedSnapshot;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }
        PlaylistLibraryIndexSnapshot inlineSnapshot = CreatePlaylistLibraryIndexSnapshot(cancellationToken, currentVersion);
        accessKind = "inline";
        buildElapsedMs = inlineSnapshot.BuildElapsedMs;
        return inlineSnapshot;
    }

    /// <summary>
    /// 現在の playlist library index invalidation 版数を返します。
    /// </summary>
    private long GetPlaylistLibraryIndexVersion()
    {
        lock (playlistLibraryIndexSync)
        {
            return playlistLibraryIndexVersion;
        }
    }

    /// <summary>
    /// 現在の score snapshot 版数を返します。
    /// playlist score 表示の正本差し替え判定に利用します。
    /// </summary>
    private int GetPlaylistScoreSnapshotVersion()
    {
        if (files == null)
        {
            return 0;
        }
        return files.GetScoreRuntimeStateForDiagnostics().SnapshotVersion;
    }

    /// <summary>
    /// 現在の playlist library index readiness を返します。
    /// </summary>
    private PlaylistLibraryIndexReadinessSnapshot CapturePlaylistLibraryIndexReadinessSnapshot()
    {
        PlaylistLibraryIndexSnapshot cachedSnapshot = null;
        Task<PlaylistLibraryIndexSnapshot> prewarmTask = null;
        long currentVersion = 0L;
        long prewarmVersion = 0L;
        lock (playlistLibraryIndexSync)
        {
            cachedSnapshot = playlistLibraryIndexSnapshot;
            prewarmTask = playlistLibraryIndexPrewarmTask;
            currentVersion = playlistLibraryIndexVersion;
            prewarmVersion = playlistLibraryIndexPrewarmVersion;
        }
        if (cachedSnapshot != null && cachedSnapshot.Version == currentVersion)
        {
            return new PlaylistLibraryIndexReadinessSnapshot
            {
                State = "cached",
                BuildElapsedMs = cachedSnapshot.BuildElapsedMs
            };
        }
        if (prewarmTask != null && prewarmVersion == currentVersion)
        {
            if (prewarmTask.Status == TaskStatus.RanToCompletion)
            {
                try
                {
                    PlaylistLibraryIndexSnapshot prewarmedSnapshot = prewarmTask.GetAwaiter().GetResult();
                    if (prewarmedSnapshot != null && prewarmedSnapshot.Version == currentVersion)
                    {
                        return new PlaylistLibraryIndexReadinessSnapshot
                        {
                            State = "prewarmed",
                            BuildElapsedMs = prewarmedSnapshot.BuildElapsedMs
                        };
                    }
                }
                catch
                {
                }
            }
            return new PlaylistLibraryIndexReadinessSnapshot
            {
                State = "prewarmed",
                BuildElapsedMs = 0L
            };
        }
        return new PlaylistLibraryIndexReadinessSnapshot
        {
            State = "inline",
            BuildElapsedMs = 0L
        };
    }

    /// <summary>
    /// 現在の playlist open readiness snapshot を返します。
    /// </summary>
    private PlaylistOpenReadinessSnapshot CapturePlaylistOpenReadinessSnapshot()
    {
        bool playlistRefRunning = false;
        int playlistRefLastCompletedVersion = 0;
        lock (lockDeferredPlaylistRef)
        {
            playlistRefRunning = deferredPlaylistRefRunning;
            playlistRefLastCompletedVersion = deferredPlaylistRefLastCompletedVersion;
        }
        BeMusicSeeker.Models.BMSLibrary.DeferredMaintenanceTableCheckState maintenanceState = default(BeMusicSeeker.Models.BMSLibrary.DeferredMaintenanceTableCheckState);
        if (files != null)
        {
            maintenanceState = files.GetDeferredMaintenanceTableCheckStateForDiagnostics();
        }
        BeMusicSeeker.Models.BMSLibrary.ScoreRuntimeState scoreState = default(BeMusicSeeker.Models.BMSLibrary.ScoreRuntimeState);
        if (files != null)
        {
            scoreState = files.GetScoreRuntimeStateForDiagnostics();
        }
        PlaylistLibraryIndexReadinessSnapshot libraryIndexSnapshot = CapturePlaylistLibraryIndexReadinessSnapshot();
        return new PlaylistOpenReadinessSnapshot
        {
            StartupReadyDataReached = startupReadyDataReached,
            StartupReadyUiReached = startupReadyUiReached,
            StartupReadyOperableReached = startupReadyOperableReached,
            PlaylistRefDeferredRunning = playlistRefRunning,
            PlaylistRefDeferredLastCompletedVersion = playlistRefLastCompletedVersion,
            MaintenanceDeferredRunning = maintenanceState.Running,
            MaintenanceDeferredLastCompletedVersion = maintenanceState.LastCompletedVersion,
            PlaylistLibraryIndexState = libraryIndexSnapshot.State,
            PlaylistLibraryIndexBuildMs = libraryIndexSnapshot.BuildElapsedMs,
            ScoreSnapshotReady = scoreState.SnapshotReady,
            ScoreSnapshotVersion = scoreState.SnapshotVersion,
            ScoreHydrationRunning = scoreState.HydrationRunning,
            ScoreHydrationCompletedVersion = scoreState.HydrationCompletedVersion,
            RankingRefreshRunning = scoreState.RankingRefreshRunning,
            RankingRefreshCompletedVersion = scoreState.RankingRefreshCompletedVersion
        };
    }

    /// <summary>
    /// playlist root / empty-folder 表記をログ向け文字列へ変換します。
    /// </summary>
    private static string FormatPlaylistFolderNameForLog(string folderName)
    {
        if (folderName == null)
        {
            return "(root)";
        }
        if (folderName.Length == 0)
        {
            return "(empty)";
        }
        return folderName;
    }

    /// <summary>
    /// playlist 名をログ向け文字列へ変換します。
    /// </summary>
    private static string FormatPlaylistTableNameForLog(BMSTable table)
    {
        return string.IsNullOrWhiteSpace(table?.name) ? "(null)" : table.name;
    }

    /// <summary>
    /// playlist 選択そのものを表す request かどうかを返します。
    /// </summary>
    private static bool IsPlaylistOpenInteractionRequest(viewUpdateMode requestedMode)
    {
        return requestedMode == viewUpdateMode.PlaylistFilterSelected || requestedMode == viewUpdateMode.PlaylistNotOwnedFilterSelected;
    }

    /// <summary>
    /// playlist open interaction の request と readiness を記録します。
    /// </summary>
    private void TrackPlaylistOpenRequest(PlaylistBuildRequest request)
    {
        if (request == null || !IsPlaylistOpenInteractionRequest(request.RequestedMode))
        {
            return;
        }
        PlaylistOpenReadinessSnapshot readiness = CapturePlaylistOpenReadinessSnapshot();
        PlaylistOpenInteractionState interaction = new PlaylistOpenInteractionState
        {
            RequestVersion = request.RequestVersion,
            Identity = request.Identity,
            RequestedAtUtc = DateTime.UtcNow,
            Readiness = readiness
        };
        lock (playlistViewState.SyncRoot)
        {
            playlistViewState.CurrentOpenInteraction = interaction;
        }
        LogPlaylistOpen("playlist_open_request requestVersion=" + request.RequestVersion + " requestedMode=" + request.RequestedMode + " mode=" + request.Mode + " table=" + FormatPlaylistTableNameForLog(request.Identity.Table) + " folder=" + FormatPlaylistFolderNameForLog(request.Identity.FolderName) + " filterType=" + request.Identity.FilterType);
        LogPlaylistOpen("playlist_open_ready_state requestVersion=" + request.RequestVersion + " startupReadyDataReached=" + readiness.StartupReadyDataReached.ToString().ToLowerInvariant() + " startupReadyUiReached=" + readiness.StartupReadyUiReached.ToString().ToLowerInvariant() + " startupReadyOperableReached=" + readiness.StartupReadyOperableReached.ToString().ToLowerInvariant() + " playlistRefDeferredRunning=" + readiness.PlaylistRefDeferredRunning.ToString().ToLowerInvariant() + " playlistRefDeferredLastCompletedVersion=" + readiness.PlaylistRefDeferredLastCompletedVersion + " maintenanceDeferredRunning=" + readiness.MaintenanceDeferredRunning.ToString().ToLowerInvariant() + " maintenanceDeferredLastCompletedVersion=" + readiness.MaintenanceDeferredLastCompletedVersion + " playlistLibraryIndexState=" + readiness.PlaylistLibraryIndexState + " playlistLibraryIndexBuildMs=" + readiness.PlaylistLibraryIndexBuildMs + " scoreSnapshotReady=" + readiness.ScoreSnapshotReady.ToString().ToLowerInvariant() + " scoreSnapshotVersion=" + readiness.ScoreSnapshotVersion + " scoreHydrationRunning=" + readiness.ScoreHydrationRunning.ToString().ToLowerInvariant() + " scoreHydrationCompletedVersion=" + readiness.ScoreHydrationCompletedVersion + " rankingRefreshRunning=" + readiness.RankingRefreshRunning.ToString().ToLowerInvariant() + " rankingRefreshCompletedVersion=" + readiness.RankingRefreshCompletedVersion);
    }

    /// <summary>
    /// playlist open interaction の build 開始時刻を記録します。
    /// </summary>
    private void TryMarkPlaylistOpenBuildStarted(PlaylistBuildRequest request)
    {
        if (request == null || !IsPlaylistOpenInteractionRequest(request.RequestedMode))
        {
            return;
        }
        lock (playlistViewState.SyncRoot)
        {
            if (playlistViewState.CurrentOpenInteraction != null && playlistViewState.CurrentOpenInteraction.RequestVersion == request.RequestVersion && !playlistViewState.CurrentOpenInteraction.BuildStartedAtUtc.HasValue)
            {
                playlistViewState.CurrentOpenInteraction.BuildStartedAtUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// playlist open interaction の build 完了時刻と採用世代を記録します。
    /// </summary>
    private void TryMarkPlaylistOpenBuildCompleted(PlaylistBuildRequest request, int viewCount)
    {
        if (request == null || !IsPlaylistOpenInteractionRequest(request.RequestedMode))
        {
            return;
        }
        DateTime completedAtUtc = DateTime.UtcNow;
        lock (playlistViewState.SyncRoot)
        {
            if (playlistViewState.CurrentOpenInteraction != null && playlistViewState.CurrentOpenInteraction.RequestVersion == request.RequestVersion)
            {
                if (!playlistViewState.CurrentOpenInteraction.BuildStartedAtUtc.HasValue)
                {
                    playlistViewState.CurrentOpenInteraction.BuildStartedAtUtc = completedAtUtc;
                }
                playlistViewState.CurrentOpenInteraction.BuildCompletedAtUtc = completedAtUtc;
                playlistViewState.CurrentOpenInteraction.ExpectedSourceGenerationId = playlistViewState.SourceGenerationId;
                playlistViewState.CurrentOpenInteraction.ExpectedViewGenerationId = playlistViewState.CurrentViewGenerationId;
                playlistViewState.CurrentOpenInteraction.ViewCount = viewCount;
                playlistViewState.CurrentOpenInteraction.VisibleCompletedLogged = false;
            }
        }
    }

    /// <summary>
    /// playlist open interaction の描画完了をログへ出力します。
    /// </summary>
    /// <param name="checkpoint">UI 側チェックポイント名。</param>
    /// <param name="expectedSourceGenerationId">UI が観測した source 世代。</param>
    /// <param name="expectedViewGenerationId">UI が観測した view 世代。</param>
    public void TryLogPlaylistOpenVisibleCompleted(string checkpoint, long expectedSourceGenerationId, long expectedViewGenerationId)
    {
        if (!installPerformanceLoggingEnabled)
        {
            return;
        }
        PlaylistOpenInteractionState interaction = null;
        lock (playlistViewState.SyncRoot)
        {
            if (playlistViewState.CurrentOpenInteraction == null || playlistViewState.CurrentOpenInteraction.VisibleCompletedLogged || !playlistViewState.CurrentOpenInteraction.BuildCompletedAtUtc.HasValue)
            {
                return;
            }
            if (playlistViewState.CurrentOpenInteraction.ExpectedSourceGenerationId != expectedSourceGenerationId || playlistViewState.CurrentOpenInteraction.ExpectedViewGenerationId != expectedViewGenerationId)
            {
                return;
            }
            playlistViewState.CurrentOpenInteraction.VisibleCompletedLogged = true;
            interaction = playlistViewState.CurrentOpenInteraction;
        }
        long requestToBuildStartMs = interaction.BuildStartedAtUtc.HasValue ? (long)(interaction.BuildStartedAtUtc.Value - interaction.RequestedAtUtc).TotalMilliseconds : -1L;
        long requestToBuildCompleteMs = (long)(interaction.BuildCompletedAtUtc.Value - interaction.RequestedAtUtc).TotalMilliseconds;
        long requestToVisibleRenderMs = (long)(DateTime.UtcNow - interaction.RequestedAtUtc).TotalMilliseconds;
        long buildToVisibleRenderMs = (long)(DateTime.UtcNow - interaction.BuildCompletedAtUtc.Value).TotalMilliseconds;
        LogPlaylistOpen("playlist_open_visible completed requestVersion=" + interaction.RequestVersion + " checkpoint=" + checkpoint + " requestToBuildStartMs=" + requestToBuildStartMs + " requestToBuildCompleteMs=" + requestToBuildCompleteMs + " requestToVisibleRenderMs=" + requestToVisibleRenderMs + " buildToVisibleRenderMs=" + buildToVisibleRenderMs + " viewCount=" + interaction.ViewCount);
        if (requestToVisibleRenderMs >= PlaylistOpenSlowLogThresholdMs)
        {
            LogPlaylistOpen("playlist_open_stage_detail requestVersion=" + interaction.RequestVersion + " checkpoint=" + checkpoint + " requestToBuildStartMs=" + requestToBuildStartMs + " requestToBuildCompleteMs=" + requestToBuildCompleteMs + " requestToVisibleRenderMs=" + requestToVisibleRenderMs + " buildToVisibleRenderMs=" + buildToVisibleRenderMs + " thresholdMs=" + PlaylistOpenSlowLogThresholdMs);
        }
    }

    internal bool TryCreatePlaylistOpenVisibleTiming(long expectedSourceGenerationId, long expectedViewGenerationId, out TableFirstVisibleTiming timing)
    {
        timing = default;
        if (!installPerformanceLoggingEnabled)
        {
            return false;
        }
        PlaylistOpenInteractionState interaction = null;
        lock (playlistViewState.SyncRoot)
        {
            if (playlistViewState.CurrentOpenInteraction == null || !playlistViewState.CurrentOpenInteraction.BuildCompletedAtUtc.HasValue)
            {
                return false;
            }
            if (playlistViewState.CurrentOpenInteraction.ExpectedSourceGenerationId != expectedSourceGenerationId || playlistViewState.CurrentOpenInteraction.ExpectedViewGenerationId != expectedViewGenerationId)
            {
                return false;
            }
            interaction = playlistViewState.CurrentOpenInteraction;
        }
        long requestToBuildStartMs = interaction.BuildStartedAtUtc.HasValue ? (long)(interaction.BuildStartedAtUtc.Value - interaction.RequestedAtUtc).TotalMilliseconds : -1L;
        long requestToBuildCompleteMs = (long)(interaction.BuildCompletedAtUtc.Value - interaction.RequestedAtUtc).TotalMilliseconds;
        DateTime visibleAtUtc = DateTime.UtcNow;
        long requestToVisibleRenderMs = (long)(visibleAtUtc - interaction.RequestedAtUtc).TotalMilliseconds;
        long buildToVisibleRenderMs = (long)(visibleAtUtc - interaction.BuildCompletedAtUtc.Value).TotalMilliseconds;
        timing = new TableFirstVisibleTiming(interaction.RequestVersion, requestToBuildStartMs, requestToBuildCompleteMs, requestToVisibleRenderMs, buildToVisibleRenderMs, interaction.ViewCount);
        return true;
    }

    /// <summary>
    /// 現在の playlist 内容更新版数を返します。
    /// </summary>
    private long GetPlaylistContentRevision()
    {
        lock (playlistViewState.SyncRoot)
        {
            return playlistViewState.PlaylistContentRevision;
        }
    }

    /// <summary>
    /// playlist 内容更新版数を進めます。
    /// </summary>
    /// <param name="reason">更新理由。</param>
    /// <returns>更新後の版数。</returns>
    private long IncrementPlaylistContentRevision(string reason)
    {
        long nextRevision;
        lock (playlistViewState.SyncRoot)
        {
            playlistViewState.PlaylistContentRevision++;
            nextRevision = playlistViewState.PlaylistContentRevision;
        }
        LogPlaylistWorker("playlist_revision incremented revision=" + nextRevision + " reason=" + reason);
        return nextRevision;
    }

    /// <summary>
    /// 現在の UI 条件から playlist build request を生成します。
    /// </summary>
    private PlaylistBuildRequest CreatePlaylistBuildRequest(int requestVersion, viewUpdateMode mode, viewUpdateMode requestedMode, object parameter)
    {
        bool hasResolvedSelection = TryResolvePlaylistSelection(mode, parameter, out BMSTable bmsTable, out string folderName, out PlaylistFilterType filterType);
        long libraryIndexVersion = GetPlaylistLibraryIndexVersion();
        long playlistRevision = GetPlaylistContentRevision();
        int scoreSnapshotVersion = GetPlaylistScoreSnapshotVersion();
        int chartInfoIndexVersion = files?.ChartInfoIndexVersion ?? 0;
        return new PlaylistBuildRequest
        {
            RequestVersion = requestVersion,
            Mode = mode,
            RequestedMode = requestedMode,
            Parameter = parameter,
            Identity = CreatePlaylistRequestIdentity(bmsTable, folderName, filterType, KeywordFilter, ModeFilter, SortParameters, libraryIndexVersion, playlistRevision, scoreSnapshotVersion, chartInfoIndexVersion, hasResolvedSelection),
            UseCoalescingWindow = ShouldUsePlaylistBuildCoalescingWindow(mode, requestedMode)
        };
    }

    /// <summary>
    /// worker が build 前に待つ quiet window 中に pending request を畳み込みます。
    /// </summary>
    /// <param name="request">最初に取得した要求。</param>
    /// <returns>quiet window 終了時点の最新要求。</returns>
    private PlaylistBuildRequest CoalescePlaylistBuildRequest(PlaylistBuildRequest request)
    {
        if (request == null || !request.UseCoalescingWindow || PlaylistBuildCoalescingWindowMs <= 0)
        {
            return request;
        }
        Stopwatch stopwatch = Stopwatch.StartNew();
        int coalescedCount = 0;
        LogPlaylistWorker("playlist_request_coalescing_wait started version=" + request.RequestVersion + " durationMs=" + PlaylistBuildCoalescingWindowMs);
        lock (playlistViewState.SyncRoot)
        {
            while (true)
            {
                int remainingMs = PlaylistBuildCoalescingWindowMs - (int)stopwatch.ElapsedMilliseconds;
                if (remainingMs <= 0)
                {
                    break;
                }
                Monitor.Wait(playlistViewState.SyncRoot, remainingMs);
                if (playlistViewState.PendingRequest == null)
                {
                    continue;
                }
                request = playlistViewState.PendingRequest;
                playlistViewState.PendingRequest = null;
                playlistViewState.CurrentBuildRequest = request;
                coalescedCount++;
            }
        }
        LogPlaylistWorker("playlist_request_coalescing_wait completed version=" + request.RequestVersion + " durationMs=" + stopwatch.ElapsedMilliseconds);
        if (coalescedCount > 0)
        {
            LogPlaylistWorker("playlist_request_coalesced count=" + coalescedCount + " finalVersion=" + request.RequestVersion + " mode=" + request.Mode + " requestedMode=" + request.RequestedMode);
        }
        return request;
    }

    /// <summary>
    /// 最新の playlist build 要求として pending request を上書きし、worker を起動します。
    /// </summary>
    /// <param name="mode">実行モード。</param>
    /// <param name="requestedMode">要求元モード。</param>
    /// <param name="parameter">追加パラメータ。</param>
    /// <returns>採番された要求バージョン。</returns>
    private int RegisterPlaylistSourceBuildRequest(viewUpdateMode mode, viewUpdateMode requestedMode, object parameter)
    {
        CancellationTokenSource previousCancellation = null;
        bool startWorker = false;
        string deduplicatedTarget = null;
        bool ignoredAsNoop = false;
        int lastBuiltScoreSnapshotVersion = 0;
        PlaylistBuildRequest request;
        lock (playlistViewState.SyncRoot)
        {
            int nextRequestVersion = playlistViewState.RequestVersion + 1;
            request = CreatePlaylistBuildRequest(nextRequestVersion, mode, requestedMode, parameter);
            lastBuiltScoreSnapshotVersion = playlistViewState.LastBuiltScoreSnapshotVersion;
            if (playlistViewState.PendingRequest != null && playlistViewState.PendingRequest.Identity == request.Identity)
            {
                deduplicatedTarget = "pending";
            }
            else if (playlistViewState.CurrentBuildRequest != null && playlistViewState.CurrentBuildRequest.Identity == request.Identity)
            {
                deduplicatedTarget = "running";
            }
            else if (playlistViewState.PendingRequest == null && playlistViewState.CurrentBuildRequest == null && playlistViewState.CurrentViewIdentity.HasValue && playlistViewState.CurrentViewIdentity.Value == request.Identity)
            {
                deduplicatedTarget = "current_view";
                ignoredAsNoop = true;
            }
            else
            {
                playlistViewState.RequestVersion = nextRequestVersion;
                previousCancellation = playlistViewState.CurrentBuildCancellation;
                playlistViewState.PendingRequest = request;
                if (!playlistViewState.WorkerRunning)
                {
                    playlistViewState.WorkerRunning = true;
                    startWorker = true;
                }
                Monitor.PulseAll(playlistViewState.SyncRoot);
            }
        }
        LogPlaylistSourceBuild("requested version=" + request.RequestVersion + " mode=" + mode + " parameterType=" + (parameter?.GetType().Name ?? "(null)") + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + lastBuiltScoreSnapshotVersion);
        if (deduplicatedTarget != null)
        {
            LogPlaylistWorker("playlist_request_deduplicated target=" + deduplicatedTarget + " version=" + request.RequestVersion + " mode=" + mode + " requestedMode=" + requestedMode);
            if (ignoredAsNoop)
            {
                LogPlaylistWorker("playlist_request_ignored reason=noop_same_view version=" + request.RequestVersion + " mode=" + mode + " requestedMode=" + requestedMode);
            }
            return request.RequestVersion;
        }
        TrackPlaylistOpenRequest(request);
        try
        {
            previousCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        LogPlaylistWorker("playlist_request_enqueued version=" + request.RequestVersion + " mode=" + mode + " requestedMode=" + requestedMode);
        if (startWorker)
        {
            LogPlaylistWorker("playlist_worker_started version=" + request.RequestVersion + " mode=" + mode + " requestedMode=" + requestedMode);
        }
        if (startWorker)
        {
            Task.Run(ProcessPendingPlaylistBuildRequests).Logging("ProcessPendingPlaylistBuildRequests");
        }
        return request.RequestVersion;
    }

    /// <summary>
    /// 現在処理中の source build 要求が最新かどうかを判定します。
     /// </summary>
    /// <param name="requestVersion">判定対象の要求バージョン。</param>
    /// <returns>最新要求であれば <see langword="true"/>。</returns>
    private bool IsLatestPlaylistSourceBuildRequest(int requestVersion)
    {
        lock (playlistViewState.SyncRoot)
        {
            return requestVersion == playlistViewState.RequestVersion;
        }
    }

    /// <summary>
    /// 現在 pending 中の playlist build 要求を 1 件取得します。
    /// </summary>
    /// <returns>pending request。無い場合は null。</returns>
    private PlaylistBuildRequest DequeuePendingPlaylistBuildRequest()
    {
        lock (playlistViewState.SyncRoot)
        {
            PlaylistBuildRequest request = playlistViewState.PendingRequest;
            playlistViewState.PendingRequest = null;
            playlistViewState.CurrentBuildRequest = request;
            return request;
        }
    }

    /// <summary>
    /// playlist build worker ループです。
    /// 常に最新要求だけを処理し、中間要求は pending 上書きで捨てます。
    /// </summary>
    private void ProcessPendingPlaylistBuildRequests()
    {
        while (true)
        {
            PlaylistBuildRequest request = DequeuePendingPlaylistBuildRequest();
            if (request == null)
            {
                lock (playlistViewState.SyncRoot)
                {
                    if (playlistViewState.PendingRequest == null)
                    {
                        playlistViewState.WorkerRunning = false;
                        playlistViewState.CurrentBuildCancellation?.Dispose();
                        playlistViewState.CurrentBuildCancellation = null;
                        playlistViewState.CurrentBuildRequest = null;
                        playlistViewState.Cancellation = new CancellationTokenSource();
                        LogPlaylistWorker("playlist_worker_stopped");
                        return;
                    }
                    request = playlistViewState.PendingRequest;
                    playlistViewState.PendingRequest = null;
                    playlistViewState.CurrentBuildRequest = request;
                }
            }
            request = CoalescePlaylistBuildRequest(request);
            CancellationTokenSource buildCancellation = new CancellationTokenSource();
            lock (playlistViewState.SyncRoot)
            {
                playlistViewState.CurrentBuildCancellation?.Dispose();
                playlistViewState.CurrentBuildCancellation = buildCancellation;
                playlistViewState.Cancellation = buildCancellation;
                playlistViewState.CurrentBuildRequest = request;
            }
            LogPlaylistWorker("playlist_worker_iteration_started version=" + request.RequestVersion + " mode=" + request.Mode + " requestedMode=" + request.RequestedMode);
            try
            {
                TryMarkPlaylistOpenBuildStarted(request);
                TryBuildPlaylistViewAndApply(request, buildCancellation.Token);
                if (buildCancellation.IsCancellationRequested)
                {
                    LogPlaylistWorker("playlist_worker_iteration_cancelled version=" + request.RequestVersion + " mode=" + request.Mode);
                }
                else
                {
                    LogPlaylistWorker("playlist_worker_iteration_completed version=" + request.RequestVersion + " mode=" + request.Mode);
                }
            }
            catch (OperationCanceledException)
            {
                LogPlaylistWorker("playlist_worker_iteration_cancelled version=" + request.RequestVersion + " mode=" + request.Mode + " stage=exception");
            }
            finally
            {
                buildCancellation.Dispose();
                lock (playlistViewState.SyncRoot)
                {
                    if (ReferenceEquals(playlistViewState.CurrentBuildCancellation, buildCancellation))
                    {
                        playlistViewState.CurrentBuildCancellation = null;
                    }
                    if (ReferenceEquals(playlistViewState.CurrentBuildRequest, request))
                    {
                        playlistViewState.CurrentBuildRequest = null;
                    }
                }
            }
        }
    }

    private static bool IsSameReferenceSequence<T>(List<T> left, List<T> right) where T : class
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }
        for (int i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i]))
            {
                return false;
            }
        }
        return true;
    }

    private void BeginUiUpdateSuppression(UiRefreshChannel mask)
    {
        if (mask == UiRefreshChannel.None)
        {
            return;
        }
        int suppressDepth;
        UiRefreshChannel suppressedMask;
        lock (lockUiSuppression)
        {
            if (suppressUiUpdateDepth == 0)
            {
                pendingUiRefreshMask = UiRefreshChannel.None;
            }
            suppressUiUpdateDepth++;
            suppressedUiRefreshMask |= mask;
            suppressDepth = suppressUiUpdateDepth;
            suppressedMask = suppressedUiRefreshMask;
        }
        LogUiSuppression("ui_suppress begin depth=" + suppressDepth + " mask=" + mask + " suppressed=" + suppressedMask);
    }

    private void RequestUiRefresh(UiRefreshChannel channel)
    {
        TrySuppress(channel);
    }

    private bool TrySuppress(UiRefreshChannel channel)
    {
        if (channel == UiRefreshChannel.None)
        {
            return false;
        }
        bool suppressed = false;
        bool pendingChanged = false;
        int suppressDepth = 0;
        UiRefreshChannel pendingMask = UiRefreshChannel.None;
        lock (lockUiSuppression)
        {
            if (suppressUiUpdateDepth > 0 && (suppressedUiRefreshMask & channel) != 0)
            {
                suppressed = true;
                UiRefreshChannel uiRefreshChannel = pendingUiRefreshMask;
                pendingUiRefreshMask |= channel;
                pendingChanged = uiRefreshChannel != pendingUiRefreshMask;
                suppressDepth = suppressUiUpdateDepth;
                pendingMask = pendingUiRefreshMask;
            }
        }
        if (pendingChanged)
        {
            LogUiSuppression("ui_suppress pending depth=" + suppressDepth + " channel=" + channel + " pending=" + pendingMask);
        }
        return suppressed;
    }

    private bool IsUiUpdateSuppressed()
    {
        lock (lockUiSuppression)
        {
            return suppressUiUpdateDepth > 0;
        }
    }

    private void RequestDeferredPlaylistSummaryRefresh()
    {
        lock (lockUiSuppression)
        {
            deferredPlaylistSummaryRefreshRequested = true;
        }
    }

    private void RequestDeferredPlaylistSummaryPresentationRefresh()
    {
        lock (lockUiSuppression)
        {
            deferredPlaylistSummaryPresentationRefreshRequested = true;
        }
    }

    private bool ConsumeDeferredPlaylistSummaryRefresh()
    {
        lock (lockUiSuppression)
        {
            bool result = deferredPlaylistSummaryRefreshRequested;
            deferredPlaylistSummaryRefreshRequested = false;
            return result;
        }
    }

    private bool ConsumeDeferredPlaylistSummaryPresentationRefresh()
    {
        lock (lockUiSuppression)
        {
            bool result = deferredPlaylistSummaryPresentationRefreshRequested;
            deferredPlaylistSummaryPresentationRefreshRequested = false;
            return result;
        }
    }

    private void InvalidatePlaylistSummaryRowsCache(bool invalidateTableCountCache = true)
    {
        lock (lockPlaylistSummaryRowsCache)
        {
            playlistSummaryRowsCache.Clear();
            if (invalidateTableCountCache)
            {
                playlistSummaryTableCountCache.Clear();
            }
            playlistSummaryRowsCacheValid = false;
        }
    }

    private void SetPlaylistSummaryRowsCache(IEnumerable<PlaylistSummaryRow> rows)
    {
        lock (lockPlaylistSummaryRowsCache)
        {
            playlistSummaryRowsCache = (rows ?? Enumerable.Empty<PlaylistSummaryRow>()).ToList();
            playlistSummaryRowsCacheValid = true;
        }
    }

    private List<PlaylistSummaryRow> GetPlaylistSummaryRowsCacheSnapshot()
    {
        lock (lockPlaylistSummaryRowsCache)
        {
            if (!playlistSummaryRowsCacheValid)
            {
                return null;
            }
            return playlistSummaryRowsCache.ToList();
        }
    }

    private void EndUiUpdateSuppression()
    {
        UiRefreshChannel uiRefreshChannel = UiRefreshChannel.None;
        int suppressDepth = 0;
        lock (lockUiSuppression)
        {
            if (suppressUiUpdateDepth <= 0)
            {
                suppressUiUpdateDepth = 0;
                suppressedUiRefreshMask = UiRefreshChannel.None;
                pendingUiRefreshMask = UiRefreshChannel.None;
                return;
            }
            suppressUiUpdateDepth--;
            suppressDepth = suppressUiUpdateDepth;
            if (suppressUiUpdateDepth == 0)
            {
                uiRefreshChannel = pendingUiRefreshMask;
                pendingUiRefreshMask = UiRefreshChannel.None;
                suppressedUiRefreshMask = UiRefreshChannel.None;
            }
        }
        LogUiSuppression("ui_suppress end depth=" + suppressDepth + " flush=" + uiRefreshChannel);
        if (uiRefreshChannel == UiRefreshChannel.None)
        {
            startupReadyInstallStopwatch = null;
            startupReadyOperableStopwatch = null;
            startupReadyDataLogged = false;
            startupReadyUiLogged = false;
        }
        if (uiRefreshChannel == UiRefreshChannel.None)
        {
            return;
        }
        DispatcherHelper.UIDispatcher.BeginInvoke((Action)delegate
        {
            FlushPendingUiRefresh(uiRefreshChannel);
        });
    }

    private void TryLogStartupReadyData()
    {
        if (startupReadyInstallStopwatch == null || startupReadyDataLogged)
        {
            return;
        }
        LogUiSuppression("startup_ready_data elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds);
        startupReadyDataLogged = true;
        startupReadyDataReached = true;
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyData);
    }

    private void TryLogStartupReadyUi(UiRefreshChannel mask)
    {
        UiRefreshChannel uiRefreshChannel = UiRefreshChannel.InstallTree | UiRefreshChannel.LibraryMainView;
        if ((mask & uiRefreshChannel) != uiRefreshChannel)
        {
            return;
        }
        if (startupReadyInstallStopwatch == null || startupReadyUiLogged)
        {
            return;
        }
        bool flag = (mask & UiRefreshChannel.PlaylistTree) != 0;
        LogUiSuppression("startup_ready_ui elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds + " playlistRefreshed=" + flag.ToString().ToLowerInvariant());
        startupReadyUiLogged = true;
        startupReadyUiReached = true;
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyUi);
    }

    private void TryLogStartupReadyInstall(UiRefreshChannel mask)
    {
        if ((mask & UiRefreshChannel.InstallTree) == 0 || (mask & UiRefreshChannel.LibraryMainView) == 0)
        {
            return;
        }
        if (startupReadyInstallStopwatch == null)
        {
            return;
        }
        LogUiSuppression("startup_ready_install elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds);
        startupReadyInstallStopwatch = null;
        startupReadyDataLogged = false;
        startupReadyUiLogged = false;
    }

    private void TryLogStartupReadyOperable()
    {
        if (startupReadyOperableStopwatch == null)
        {
            return;
        }
        LogUiSuppression("startup_ready_operable elapsedMs=" + startupReadyOperableStopwatch.ElapsedMilliseconds);
        startupReadyOperableStopwatch = null;
        startupReadyOperableReached = true;
        SetStartupUiInteractionBlocked(false);
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable);
        StartStartupBackgroundTaskScheduler();
    }

    private bool QueueStartupBackgroundTask(string name, string reason, string dependency, Func<Task> work)
    {
        if (work == null)
        {
            return false;
        }
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        string normalizedDependency = string.IsNullOrWhiteSpace(dependency) ? null : dependency;
        string coalesceKey = normalizedName;
        long version;
        bool shouldStartWorker = false;
        lock (startupBackgroundTaskLock)
        {
            version = ++startupBackgroundTaskVersion;
            StartupBackgroundTaskRequest existing = startupBackgroundTaskQueue.LastOrDefault((StartupBackgroundTaskRequest item) => string.Equals(item.CoalesceKey, coalesceKey, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Reason = normalizedReason;
                existing.Dependency = normalizedDependency;
                existing.Priority = GetStartupBackgroundTaskPriority(normalizedName);
                existing.Version = version;
                existing.Work = work;
                LogUiSuppression("startup_background_task skipped name=" + normalizedName + " version=" + version + " reason=" + normalizedReason + " coalesceKey=" + coalesceKey + " replaced=true");
            }
            else
            {
                startupBackgroundTaskQueue.Add(new StartupBackgroundTaskRequest
                {
                    Name = normalizedName,
                    Reason = normalizedReason,
                    Dependency = normalizedDependency,
                    CoalesceKey = coalesceKey,
                    Priority = GetStartupBackgroundTaskPriority(normalizedName),
                    Version = version,
                    Work = work
                });
            }
            LogUiSuppression("startup_background_task queue name=" + normalizedName + " version=" + version + " reason=" + normalizedReason + " dependency=" + (normalizedDependency ?? "(none)") + " priority=" + GetStartupBackgroundTaskPriority(normalizedName));
            shouldStartWorker = startupBackgroundTaskSchedulerStarted && !startupBackgroundTaskWorkerRunning;
        }
        if (shouldStartWorker)
        {
            TryStartStartupBackgroundTaskWorker();
        }
        return true;
    }

    private static int GetStartupBackgroundTaskPriority(string name)
    {
        if (string.Equals(name, "playlist_entries_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }
        if (string.Equals(name, "playlist_library_index_prewarm", StringComparison.OrdinalIgnoreCase))
        {
            return 15;
        }
        if (string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }
        if (string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }
        if (string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }
        if (string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }
        return 100;
    }

    private void StartStartupBackgroundTaskScheduler()
    {
        bool shouldStartWorker;
        lock (startupBackgroundTaskLock)
        {
            if (startupBackgroundTaskSchedulerStarted)
            {
                return;
            }
            startupBackgroundTaskSchedulerStarted = true;
            shouldStartWorker = startupBackgroundTaskQueue.Count > 0 && !startupBackgroundTaskWorkerRunning;
        }
        LogUiSuppression("startup_background_task scheduler_start");
        if (shouldStartWorker)
        {
            TryStartStartupBackgroundTaskWorker();
        }
    }

    private void TryStartStartupBackgroundTaskWorker()
    {
        bool shouldStart = false;
        lock (startupBackgroundTaskLock)
        {
            if (startupBackgroundTaskSchedulerStarted && !startupBackgroundTaskWorkerRunning)
            {
                startupBackgroundTaskWorkerRunning = true;
                shouldStart = true;
            }
        }
        if (!shouldStart)
        {
            return;
        }
        Task.Run(async delegate
        {
            while (true)
            {
                StartupBackgroundTaskRequest request = null;
                lock (startupBackgroundTaskLock)
                {
                    int index = -1;
                    int bestPriority = int.MaxValue;
                    long bestVersion = long.MaxValue;
                    for (int i = 0; i < startupBackgroundTaskQueue.Count; i++)
                    {
                        StartupBackgroundTaskRequest candidate = startupBackgroundTaskQueue[i];
                        if (!string.IsNullOrWhiteSpace(candidate.Dependency) && !startupBackgroundTaskCompletedNames.Contains(candidate.Dependency))
                        {
                            continue;
                        }
                        if (candidate.Priority < bestPriority || (candidate.Priority == bestPriority && candidate.Version < bestVersion))
                        {
                            index = i;
                            bestPriority = candidate.Priority;
                            bestVersion = candidate.Version;
                        }
                    }
                    if (index >= 0)
                    {
                        request = startupBackgroundTaskQueue[index];
                        startupBackgroundTaskQueue.RemoveAt(index);
                    }
                    else
                    {
                        startupBackgroundTaskWorkerRunning = false;
                        return;
                    }
                }
                Stopwatch stopwatch = Stopwatch.StartNew();
                LogUiSuppression("startup_background_task start name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " dependency=" + (request.Dependency ?? "(none)"));
                try
                {
                    await request.Work().ConfigureAwait(false);
                    stopwatch.Stop();
                    LogUiSuppression("startup_background_task done name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    LogUiSuppressionWarning("startup_background_task failed name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
                }
                lock (startupBackgroundTaskLock)
                {
                    startupBackgroundTaskCompletedNames.Add(request.Name);
                }
            }
        }).Logging("StartupBackgroundTaskScheduler");
    }

    private void RefreshLibraryMainViewForCurrentFilter()
    {
        if (Enum.IsDefined(typeof(MaintenanceFilterType), (int)treeViewFilterTypeSelected))
        {
            ExecMaintenanceFilter((MaintenanceFilterType)treeViewFilterTypeSelected, treeViewFilterParameterSelected);
        }
        else
        {
            makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
        }
    }

    private void RefreshChartInfoDependentViews()
    {
        SyncBmsonLibraryRowCache(files?.BmsonSongs);
        ResetRegularDerivedViewCaches();
        InvalidatePlaylistSummaryRowsCache();
        RefreshPlaylistSummaryIfVisible();
        RefreshPlaylistDetailAfterReloadIfVisible();
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        RefreshLibraryMainViewForCurrentFilter();
    }

    private void ScheduleDeferredLibraryFolderTreeRefresh()
    {
        bool shouldSchedule = false;
        lock (lockDeferredLibraryFolderTreeRefresh)
        {
            if (!deferredLibraryFolderTreeRefreshQueued)
            {
                deferredLibraryFolderTreeRefreshQueued = true;
                shouldSchedule = true;
            }
        }
        if (!shouldSchedule)
        {
            return;
        }
        Task.Run(delegate
        {
            BMSLibrary.ParentFolderListCacheSnapshot snapshot = null;
            try
            {
                snapshot = files.BuildBMSParentFolderListCacheSnapshot();
            }
            catch (Exception ex)
            {
                LogUiSuppressionWarning("ui_stall_library_folder_tree_prepare_failed message=" + ex.Message);
            }
            DispatcherHelper.UIDispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                bool shouldReschedule = false;
                try
                {
                    bool refreshed = false;
                    if (snapshot != null)
                    {
                        refreshed = files.TryApplyBMSParentFolderListCacheSnapshot(snapshot);
                        if (!refreshed && files.IsBMSParentFolderListCacheDirty())
                        {
                            shouldReschedule = true;
                        }
                    }
                    if (!shouldReschedule)
                    {
                        RefreshBmsParentFolderListView();
                    }
                }
                finally
                {
                    stopwatch.Stop();
                    LogUiSuppression("ui_suppress flush_library_folder_tree_deferred_ms=" + stopwatch.ElapsedMilliseconds);
                    lock (lockDeferredLibraryFolderTreeRefresh)
                    {
                        deferredLibraryFolderTreeRefreshQueued = false;
                    }
                    if (shouldReschedule)
                    {
                        ScheduleDeferredLibraryFolderTreeRefresh();
                    }
                    else
                    {
                        TryLogStartupReadyOperable();
                    }
                }
            });
        });
    }

    /// <summary>
    /// BMS 親フォルダ一覧の安定したソート済みビューを、現在のライブラリ状態から更新します。
    /// 他経路でキャッシュが先に構築された場合でも、ツリーと移動メニューが同じ正本を参照できるようにします。
    /// </summary>
    private bool RefreshBmsParentFolderListView(bool raisePropertyChanged = true)
    {
        IEnumerable<string> source = (files != null) ? files.GetBMSParentFolderListSnapshot() : Enumerable.Empty<string>();
        List<string> sortedParentFolders = source.Where((string path) => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        bool changed = !_sortedBmsParentFolderList.SequenceEqual(sortedParentFolders, StringComparer.OrdinalIgnoreCase);
        if (changed)
        {
            _sortedBmsParentFolderList.Clear();
            _sortedBmsParentFolderList.AddRange(sortedParentFolders);
        }
        bmsParentFolderListViewInitialized = true;
        if (raisePropertyChanged)
        {
            RaisePropertyChanged(() => BMSParentFolderList);
        }
        return changed;
    }

    /// <summary>
    /// BMS 検索ルートディレクトリ設定の変更を UI 側の親フォルダ一覧へ反映させます。
    /// </summary>
    private void NotifyBmsParentFolderListChanged()
    {
        bmsParentFolderListViewInitialized = false;
        if (files != null)
        {
            files.NotifyBMSDirectoriesChanged();
        }
    }

    private void FlushPendingUiRefresh(UiRefreshChannel mask)
    {
        LogUiSuppression("ui_suppress flush mask=" + mask);
        Stopwatch stopwatchTotal = Stopwatch.StartNew();
        long num = 0L;
        long num2 = 0L;
        long num3 = 0L;
        long num4 = 0L;
        long num5 = 0L;
        bool flag = false;
        if ((mask & UiRefreshChannel.InstallTree) != 0)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            RaisePropertyChanged(() => BMSPackagesInstalled);
            RaisePropertyChanged(() => BMSPackagesPending);
            stopwatch.Stop();
            num = stopwatch.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.PlaylistTree) != 0)
        {
            Stopwatch stopwatch2 = Stopwatch.StartNew();
            RaisePropertyChanged(() => BMSTables);
            stopwatch2.Stop();
            num2 = stopwatch2.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.LibraryFolderTree) != 0)
        {
            flag = true;
        }
        if ((mask & UiRefreshChannel.DuplicateTree) != 0)
        {
            Stopwatch stopwatch4 = Stopwatch.StartNew();
            RaisePropertyChanged(() => BMSFilesDuplicated);
            stopwatch4.Stop();
            num4 = stopwatch4.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.LibraryMainView) != 0)
        {
            Stopwatch stopwatch5 = Stopwatch.StartNew();
            RefreshLibraryMainViewForCurrentFilter();
            stopwatch5.Stop();
            num5 = stopwatch5.ElapsedMilliseconds;
        }
        if (num > 1000)
        {
            LogUiSuppressionWarning("ui_stall_install_tree elapsedMs=" + num + " mask=" + mask);
        }
        if (num2 > 1000)
        {
            LogUiSuppressionWarning("ui_stall_playlist_tree elapsedMs=" + num2 + " mask=" + mask);
        }
        if (num5 > 1000)
        {
            LogUiSuppressionWarning("ui_stall_main_view elapsedMs=" + num5 + " filter=" + treeViewFilterTypeSelected + " mask=" + mask);
        }
        stopwatchTotal.Stop();
        LogUiSuppression("ui_suppress flush_install_tree_ms=" + num + " flush_playlist_tree_ms=" + num2 + " flush_library_folder_tree_ms=" + num3 + " flush_duplicate_tree_ms=" + num4 + " flush_library_main_view_ms=" + num5 + " flush_total_ms=" + stopwatchTotal.ElapsedMilliseconds + " deferred_library_folder_tree=" + flag);
        bool playlistSummaryDataRefreshRequired = ((mask & (UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView)) != 0) || ConsumeDeferredPlaylistSummaryRefresh();
        bool playlistSummaryPresentationRefreshRequired = ConsumeDeferredPlaylistSummaryPresentationRefresh();
        if (IsPlaylistSummaryMode && playlistSummaryDataRefreshRequired)
        {
            RebuildPlaylistSummaryView();
        }
        else if (IsPlaylistSummaryMode && playlistSummaryPresentationRefreshRequired)
        {
            RefreshPlaylistSummaryPresentationIfVisible();
        }
        TryLogStartupReadyUi(mask);
        TryLogStartupReadyInstall(mask);
        if (flag)
        {
            ScheduleDeferredLibraryFolderTreeRefresh();
        }
        else
        {
            TryLogStartupReadyOperable();
        }
    }

    private void RunPendingInstallMutation(Action action, IEnumerable<BeMusicSeeker.Models.BMSFile> playbackTargets = null, UiRefreshChannel extraMask = UiRefreshChannel.None)
    {
        if (action == null)
        {
            throw new ArgumentNullException("action");
        }
        if (files == null)
        {
            return;
        }
        UiRefreshChannel mask = UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | extraMask;
        lock (lockCopyFile)
        {
            if (playbackTargets != null)
            {
                stopPlayingBMSFile(playbackTargets);
            }
            BeginUiUpdateSuppression(mask);
            try
            {
                action();
            }
            finally
            {
                EndUiUpdateSuppression();
            }
        }
    }

    private T RunPendingInstallMutation<T>(Func<T> func, IEnumerable<BeMusicSeeker.Models.BMSFile> playbackTargets = null, UiRefreshChannel extraMask = UiRefreshChannel.None)
    {
        if (func == null)
        {
            throw new ArgumentNullException("func");
        }
        if (files == null)
        {
            return default(T);
        }
        UiRefreshChannel mask = UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | extraMask;
        lock (lockCopyFile)
        {
            if (playbackTargets != null)
            {
                stopPlayingBMSFile(playbackTargets);
            }
            BeginUiUpdateSuppression(mask);
            try
            {
                return func();
            }
            finally
            {
                EndUiUpdateSuppression();
            }
        }
    }

    private void HandleBMSPackagesInstalledCollectionChanged()
    {
        if (treeViewFilterTypeSelected == viewUpdateMode.NewlyInstalledFolderSelected)
        {
            if (!TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        }
        if (TrySuppress(UiRefreshChannel.InstallTree))
        {
            return;
        }
        RaisePropertyChanged(() => BMSPackagesInstalled);
    }

    private void HandleBMSPackagesPendingCollectionChanged()
    {
        if (treeViewFilterTypeSelected == viewUpdateMode.PendingInstallFolderSelected)
        {
            if (!TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        }
        if (TrySuppress(UiRefreshChannel.InstallTree))
        {
            return;
        }
        RaisePropertyChanged(() => BMSPackagesPending);
    }

    private void RebindBMSPackagesInstalledCollectionListener()
    {
        if (listenerForBMSLibraryBMSPackagesInstalledCollection is IDisposable disposable)
        {
            disposable.Dispose();
        }
        if (files == null || files.BMSPackagesInstalled == null)
        {
            listenerForBMSLibraryBMSPackagesInstalledCollection = null;
            return;
        }
        listenerForBMSLibraryBMSPackagesInstalledCollection = new CollectionChangedEventListener(files.BMSPackagesInstalled);
        listenerForBMSLibraryBMSPackagesInstalledCollection.RegisterHandler(delegate
        {
            HandleBMSPackagesInstalledCollectionChanged();
        });
    }

    private void RebindBMSPackagesPendingCollectionListener()
    {
        if (listenerForBMSLibraryBMSPackagesPendingCollection is IDisposable disposable)
        {
            disposable.Dispose();
        }
        if (files == null || files.BMSPackagesPending == null)
        {
            listenerForBMSLibraryBMSPackagesPendingCollection = null;
            return;
        }
        listenerForBMSLibraryBMSPackagesPendingCollection = new CollectionChangedEventListener(files.BMSPackagesPending);
        listenerForBMSLibraryBMSPackagesPendingCollection.RegisterHandler(delegate
        {
            HandleBMSPackagesPendingCollectionChanged();
        });
    }

    private void ScheduleDeferredPlaylistReferenceApply(string reason)
    {
        int version = 0;
        bool shouldStartWorker = false;
        lock (lockDeferredPlaylistRef)
        {
            deferredPlaylistRefRequestedVersion++;
            version = deferredPlaylistRefRequestedVersion;
            if (!deferredPlaylistRefRunning)
            {
                deferredPlaylistRefRunning = true;
                shouldStartWorker = true;
            }
        }
        TrackStartupProgressPlaylistReferenceRequest(reason, version);
        LogDeferredPlaylistReference("playlist_ref_deferred queue reason=" + reason + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        Action workBody = delegate
        {
            while (true)
            {
                int requestVersion = 0;
                lock (lockDeferredPlaylistRef)
                {
                    requestVersion = deferredPlaylistRefRequestedVersion;
                }
                DateTime startedAt = DateTime.UtcNow;
                try
                {
                    tables.EnsureAllPlaylistEntriesLoadedAsync("playlist_ref_deferred").GetAwaiter().GetResult();
                    List<BMSTable> list = new List<BMSTable>();
                    tables.AcquireReaderLockBMSTables();
                    try
                    {
                        list = BMSTables.Where((BMSTable t) => t != null).ToList();
                    }
                    finally
                    {
                        tables.FreeReaderLockBMSTables();
                    }
                    LogDeferredPlaylistReference("playlist_ref_deferred run version=" + requestVersion + " tableCount=" + list.Count);
                    files.SynchronizeReferenceBMSTables(list, suppressFilePropertyChanged: true);
                    DispatcherHelper.UIDispatcher.BeginInvoke((Action)delegate
                    {
                        makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
                    });
                    LogDeferredPlaylistReference("playlist_ref_deferred done version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " refreshed=true");
                    deferredPlaylistRefLastCompletedVersion = requestVersion;
                    TryCompleteStartupProgressPlaylistReference(requestVersion);
                }
                catch (Exception ex)
                {
                    LogDeferredPlaylistReference("playlist_ref_deferred failed version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " message=" + ex.Message);
                    deferredPlaylistRefLastCompletedVersion = requestVersion;
                    TryCompleteStartupProgressPlaylistReference(requestVersion);
                }
                lock (lockDeferredPlaylistRef)
                {
                    if (deferredPlaylistRefRequestedVersion == requestVersion)
                    {
                        deferredPlaylistRefRunning = false;
                        break;
                    }
                }
            }
        };
        Func<Task> work = delegate
        {
            workBody();
            return Task.CompletedTask;
        };
        if (QueueStartupBackgroundTask("playlist_ref_apply", reason, "playlist_entries_hydration", work))
        {
            return;
        }
        Task.Run(workBody).Logging("ScheduleDeferredPlaylistReferenceApply");
    }

    private Action<BMSPlaylist.PlaylistTableUpdateContext> CreatePlaylistReferenceReplaceUpdateCallback()
    {
        return delegate(BMSPlaylist.PlaylistTableUpdateContext updateContext)
        {
            if (updateContext == null || !updateContext.Updated || files == null)
            {
                return;
            }
            files.ReplaceReferenceBMSTable(updateContext.OldTable, updateContext.NewTable, updateContext.OldEntriesSnapshot, updateContext.NewEntriesSnapshot);
        };
    }

    private static bool ShouldScheduleDeferredPlaylistReferenceApplyAfterExternalSync(Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction)
    {
        return updateCallbackAction == null;
    }

    private void StartDeferredExternalPlaylistSync(string reason, bool fromReloadTables, Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction = null)
    {
        if (tables == null)
        {
            return;
        }
        PlaylistReloadOperationKind playlistReloadOperationKind = DeterminePlaylistReloadOperationKind(reason, fromReloadTables);
        int version = 0;
        bool shouldStartWorker = false;
        lock (lockDeferredExternalSync)
        {
            deferredExternalSyncRequestedVersion++;
            version = deferredExternalSyncRequestedVersion;
            if (!deferredExternalSyncRunning)
            {
                deferredExternalSyncRunning = true;
                shouldStartWorker = true;
            }
        }
        TrackStartupProgressExternalSyncRequest(reason, version);
        LogDeferredExternalSync("deferred_external_sync queue reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        Func<Task> work = async delegate
        {
            while (true)
            {
                int requestVersion = 0;
                lock (lockDeferredExternalSync)
                {
                    requestVersion = deferredExternalSyncRequestedVersion;
                }
                DateTime startedAt = DateTime.UtcNow;
                try
                {
                    BeginPlaylistSyncProgressOperation();
                    LogPlaylistReload("playlist_reload_operation started operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=" + reason + " tableCount=0 version=" + requestVersion);
                    LogDeferredExternalSync("deferred_external_sync run reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + requestVersion);
                    List<Action<BMSPlaylist.PlaylistTableUpdateContext>> updateCallbackActions = null;
                    if (updateCallbackAction != null)
                    {
                        updateCallbackActions = new List<Action<BMSPlaylist.PlaylistTableUpdateContext>> { updateCallbackAction };
                    }
                    List<BMSTable> list = await tables.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true, updateCallbackActions, delegate (PlaylistSyncAttemptResult result)
                    {
                        UpdatePlaylistSyncRuntimeStatus(result);
                    }, UpdatePlaylistSyncProgressStatus).ConfigureAwait(false);
                    int num = list?.Count ?? 0;
                    if (ShouldScheduleDeferredPlaylistReferenceApplyAfterExternalSync(updateCallbackAction))
                    {
                        ScheduleDeferredPlaylistReferenceApply("DeferredExternalSync:" + reason);
                    }
                    RefreshPlaylistSummaryIfVisible();
                    bool cleanupQueued = QueuePlaylistReloadCleanup(playlistReloadOperationKind, num);
                    LogPlaylistReload("playlist_reload_operation completed operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=" + reason + " tableCount=" + num + " summaryRebuildMs=" + Interlocked.Read(ref lastPlaylistSummaryBuildElapsedMs) + " detailRefreshMs=" + Interlocked.Read(ref lastPlaylistDetailBuildElapsedMs) + " cleanupQueued=" + cleanupQueued.ToString().ToLowerInvariant() + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
                    LogDeferredExternalSync("deferred_external_sync done reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " updatedCount=" + num);
                    TryCompleteStartupProgressExternalSync(requestVersion);
                }
                catch (Exception ex)
                {
                    LogPlaylistReload("playlist_reload_operation failed operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=" + reason + " version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " message=" + ex.Message);
                    LogDeferredExternalSync("deferred_external_sync failed reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " message=" + ex.Message);
                    TryCompleteStartupProgressExternalSync(requestVersion);
                }
                finally
                {
                    EndPlaylistSyncProgressOperation();
                }
                lock (lockDeferredExternalSync)
                {
                    if (deferredExternalSyncRequestedVersion == requestVersion)
                    {
                        deferredExternalSyncRunning = false;
                        break;
                    }
                }
            }
        };
        if (QueueStartupBackgroundTask("external_playlist_sync", reason, "playlist_entries_hydration", work))
        {
            return;
        }
        Task.Run(work).Logging("StartDeferredExternalPlaylistSync");
    }

    public PlaylistPropertyDialogViewModel playlistPropertyDialog
    {
        get
        {
            return _playlistPropertyDialog;
        }
        set
        {
            if (_playlistPropertyDialog != value)
            {
                _playlistPropertyDialog = value;
                RaisePropertyChanged("playlistPropertyDialog");
            }
        }
    }

    public string WindowTitle
    {
        get
        {
            return _WindowTitle;
        }
        set
        {
            if (!(_WindowTitle == value))
            {
                _WindowTitle = value;
                RaisePropertyChanged("WindowTitle");
            }
        }
    }

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFiles
    {
        get
        {
            if (files != null)
            {
                return files.BMSFiles;
            }
            return null;
        }
    }

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesToBeFixed
    {
        get
        {
            if (files != null)
            {
                return files.BMSFilesNeedToBeFixed;
            }
            return null;
        }
    }

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesToBeFixedIgnored
    {
        get
        {
            if (files != null)
            {
                return files.BMSFilesNeedToBeFixedIgnored;
            }
            return null;
        }
    }

    public List<DuplicateGroup> BMSFilesDuplicated
    {
        get
        {
            if (files != null)
            {
                return files.BMSFilesDuplicated;
            }
            return null;
        }
    }

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesGarbled
    {
        get
        {
            if (files != null)
            {
                return files.BMSFilesGarbled;
            }
            return null;
        }
    }

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesGarbleFixed
    {
        get
        {
            if (files != null)
            {
                return files.BMSFilesGarbledFixed;
            }
            return null;
        }
    }

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesUnregistered
    {
        get
        {
            if (files != null)
            {
                return files.BMSFilesUnregistered;
            }
            return null;
        }
    }

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesZeroNote
    {
        get
        {
            if (files != null)
            {
                return files.BMSFilesZeroNote;
            }
            return null;
        }
    }

    public DispatcherCollection<BMSPackage> BMSPackagesInstalled
    {
        get
        {
            if (files != null)
            {
                return files.BMSPackagesInstalled;
            }
            return null;
        }
    }

    public DispatcherCollection<BMSPackage> BMSPackagesPending
    {
        get
        {
            if (files != null)
            {
                return files.BMSPackagesPending;
            }
            return null;
        }
    }

    /// <summary>
    /// ライブラリツリーや移動メニューが共有する、UI 向けの安定したソート済み親フォルダ一覧です。
    /// ライブラリ側の候補 cache から同期される表示用正本であり、昇順表示を保証します。
    /// </summary>
    public DispatcherCollection<string> BMSParentFolderList
    {
        get
        {
            if (files != null && !bmsParentFolderListViewInitialized)
            {
                RefreshBmsParentFolderListView(raisePropertyChanged: false);
            }
            return _sortedBmsParentFolderList;
        }
    }

    /// <summary>
    /// メインリスト上に実際に表示されるBMS楽曲データ群です。
    /// ツリーでのフォルダ選択や、各種フィルタリング（キーワード検索、モード絞り込みなど）による抽出結果が反映されます。
    /// </summary>
    public IList BMSFilesView
    {
        get
        {
            return _BMSFilesView;
        }
        set
        {
            if (_BMSFilesView != value)
            {
                DisposeDisposableRows(_BMSFilesView);
                if (value == null)
                {
                    _BMSFilesView = new List<object>();
                }
                else
                {
                    _BMSFilesView = value;
                }
                RaisePropertyChanged("BMSFilesView");
            }
        }
    }

    private static void DisposeDisposableRows(IEnumerable rows)
    {
        if (rows == null)
        {
            return;
        }
        foreach (object row in rows)
        {
            if (row is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// 通常一覧 / playlist 詳細の chart row view を <see cref="BMSFilesView"/> binding へ差し替えます。
    /// playlist 詳細表示では <see cref="PlaylistViewState.CurrentViewRows"/> を先に更新してから呼び出します。
    /// </summary>
    /// <param name="rows">新しい表示行。</param>
    private void SetChartRowsView(IList rows)
    {
        BMSFilesView = rows;
    }

    /// <summary>
    /// 通常一覧の chart row cache を無効化します。
    /// folder/keyword/mode の各段を再計算する必要がある場合にだけ呼びます。
    /// </summary>
    private void ResetRegularDerivedViewCaches()
    {
        ChartRowsFolderView = null;
        ChartRowsKeywordFilterView = null;
        ChartRowsModeFilterView = null;
        folderSortSourceSnapshot = null;
        folderSortResultSnapshot = null;
        folderSortColumnName = null;
        folderSortDirection = null;
        ClearNormalLibrarySortCache();
    }

    private void IncrementNormalLibrarySourceGeneration(string reason)
    {
        lock (normalLibrarySortCacheLock)
        {
            normalLibrarySourceGeneration++;
            normalLibrarySortCache.Clear();
        }
    }

    private void OnNormalLibrarySortKeyChanged(string propertyName)
    {
        lock (normalLibrarySortCacheLock)
        {
            normalLibrarySortKeyGeneration++;
            normalLibrarySortCache.Clear();
        }
    }

    private void ClearNormalLibrarySortCache()
    {
        lock (normalLibrarySortCacheLock)
        {
            normalLibrarySortCache.Clear();
        }
    }

    private int PruneRegularBmsLibraryRowCache(IEnumerable<BeMusicSeeker.Models.BMSFile> currentFiles)
    {
        if (regularBmsLibraryRowCache == null)
        {
            return 0;
        }
        int pruned = regularBmsLibraryRowCache.Prune(currentFiles);
        pendingRegularBmsRowCachePrunedCount += pruned;
        return pruned;
    }

    /// <summary>
    /// 通常一覧の incremental 更新で folder 段から再構築が必要かを返します。
    /// </summary>
    /// <param name="mode">今回の更新モード。</param>
    /// <returns>regular cache の再構築が必要なら <see langword="true"/>。</returns>
    private static bool ShouldRebuildRegularFolderStage(viewUpdateMode mode, IEnumerable<LibraryChartRow> folderView, IEnumerable<LibraryChartRow> keywordView, IEnumerable<LibraryChartRow> modeView, viewUpdateMode currentTreeMode)
    {
        if (IsPlaylistTreeActive(mode, currentTreeMode))
        {
            return false;
        }
        if (mode < viewUpdateMode.KeywordFilterUpdated)
        {
            return false;
        }
        return folderView == null || keywordView == null || modeView == null;
    }

    /// <summary>
    /// regular cache self-healing 条件のテスト用ラッパです。
    /// </summary>
    internal static bool ShouldRebuildRegularFolderStageForTest(int mode, bool hasFolderView, bool hasKeywordView, bool hasModeView, int currentTreeMode)
    {
        return ShouldRebuildRegularFolderStage((viewUpdateMode)mode, hasFolderView ? Array.Empty<LibraryChartRow>() : null, hasKeywordView ? Array.Empty<LibraryChartRow>() : null, hasModeView ? Array.Empty<LibraryChartRow>() : null, (viewUpdateMode)currentTreeMode);
    }

    /// <summary>
    /// playlist source を破棄し、playlist 専用状態を初期化します。
    /// </summary>
    private void ClearPlaylistSourceRows()
    {
        List<PlaylistDetailSourceRow> sourceRowsToDispose = null;
        IList currentViewRows = null;
        long previousGenerationId = 0L;
        long previousViewGenerationId = 0L;
        lock (playlistViewState.SyncRoot)
        {
            sourceRowsToDispose = playlistViewState.SourceRows;
            previousGenerationId = playlistViewState.SourceGenerationId;
            currentViewRows = playlistViewState.CurrentViewRows;
            previousViewGenerationId = playlistViewState.CurrentViewGenerationId;
            if (sourceRowsToDispose != null)
            {
                playlistViewState.PreviousSourceRowsWeakReference = new WeakReference<List<PlaylistDetailSourceRow>>(sourceRowsToDispose);
                playlistViewState.PreviousSourceGenerationId = previousGenerationId;
            }
            if (currentViewRows != null)
            {
                playlistViewState.PreviousViewRowsWeakReference = new WeakReference<IList>(currentViewRows);
                playlistViewState.PreviousViewGenerationId = previousViewGenerationId;
            }
            playlistViewState.SourceRows = new List<PlaylistDetailSourceRow>();
            playlistViewState.CurrentViewRows = new List<object>();
            playlistViewState.CurrentTable = null;
            playlistViewState.CurrentFolderName = null;
            playlistViewState.CurrentFilterType = PlaylistFilterType.PlaylistFilter;
            playlistViewState.CurrentBuildRequest = null;
            playlistViewState.CurrentViewIdentity = null;
            playlistViewState.CurrentSourceIdentity = null;
            playlistViewState.CurrentOpenInteraction = null;
            playlistViewState.LastBuiltLibraryIndexVersion = 0L;
            playlistViewState.LastBuiltPlaylistRevision = 0L;
            playlistViewState.LastBuiltScoreSnapshotVersion = 0;
            playlistViewState.LastBuiltChartInfoIndexVersion = 0;
            playlistViewState.SourceGenerationId = 0L;
            playlistViewState.CurrentViewGenerationId = 0L;
            playlistViewState.LastAppliedViewCount = 0;
            playlistViewState.IsPlaylistCellEditing = false;
            playlistViewState.PendingScoreSnapshotRefreshVersion = 0;
        }
        LogPlaylistWeakReferenceStatus("before_source_clear");
        LogPlaylistRetention("playlist_source_replace action=clear generationId=" + previousGenerationId + " sourceCount=0 disposedCount=" + CountPlaylistSourceRows(sourceRowsToDispose) + " playlistSourceRowCount=0 playlistViewRowCount=" + CountPlaylistDetailRows(currentViewRows));
    }

    /// <summary>
    /// playlist source snapshot を差し替え、前回 source を返します。
    /// 返却された前回 source は view 差し替え後に呼び出し側で破棄します。
    /// </summary>
    /// <param name="sourceRows">新しい source snapshot。</param>
    /// <param name="currentTable">現在表示中のプレイリスト。</param>
    /// <param name="currentFolderName">現在表示中のプレイリストフォルダ名。</param>
    /// <param name="currentFilterType">現在の playlist filter 種別。</param>
    /// <returns>置き換え前の source snapshot。</returns>
    private List<PlaylistDetailSourceRow> ReplacePlaylistSourceRows(List<PlaylistDetailSourceRow> sourceRows, BMSTable currentTable, string currentFolderName, PlaylistFilterType currentFilterType, PlaylistRequestIdentity requestIdentity)
    {
        List<PlaylistDetailSourceRow> previousSourceRows = null;
        int currentViewRowsAlive = 0;
        long previousGenerationId = 0L;
        long nextGenerationId = 0L;
        lock (playlistViewState.SyncRoot)
        {
            previousSourceRows = playlistViewState.SourceRows;
            previousGenerationId = playlistViewState.SourceGenerationId;
            if (previousSourceRows != null)
            {
                playlistViewState.PreviousSourceRowsWeakReference = new WeakReference<List<PlaylistDetailSourceRow>>(previousSourceRows);
                playlistViewState.PreviousSourceGenerationId = previousGenerationId;
            }
            playlistViewState.SourceRows = sourceRows ?? new List<PlaylistDetailSourceRow>();
            playlistViewState.CurrentTable = currentTable;
            playlistViewState.CurrentFolderName = currentFolderName;
            playlistViewState.CurrentFilterType = currentFilterType;
            playlistViewState.LastBuiltLibraryIndexVersion = requestIdentity.LibraryIndexVersion;
            playlistViewState.LastBuiltPlaylistRevision = requestIdentity.PlaylistRevision;
            playlistViewState.LastBuiltScoreSnapshotVersion = requestIdentity.ScoreSnapshotVersion;
            playlistViewState.LastBuiltChartInfoIndexVersion = requestIdentity.ChartInfoIndexVersion;
            playlistViewState.CurrentSourceIdentity = requestIdentity.SourceIdentity;
            playlistViewState.SourceGenerationId++;
            nextGenerationId = playlistViewState.SourceGenerationId;
            currentViewRowsAlive = CountPlaylistDetailRows(playlistViewState.CurrentViewRows);
        }
        LogPlaylistWeakReferenceStatus("before_source_replace");
        LogPlaylistRetention("playlist_source_replace action=replace generationId=" + nextGenerationId + " previousGenerationId=" + previousGenerationId + " sourceCount=" + (sourceRows?.Count ?? 0) + " disposedCount=" + CountPlaylistSourceRows(previousSourceRows) + " playlistSourceRowCount=" + CountPlaylistSourceRows(sourceRows) + " playlistViewRowCount=" + currentViewRowsAlive);
        return previousSourceRows;
    }

    /// <summary>
    /// playlist 表示用 snapshot を差し替え、直前の view snapshot を返します。
    /// source 正本と view snapshot を別管理にして、WPF が旧 view を保持しても旧 source まで残さないようにします。
    /// </summary>
    /// <param name="viewRows">新しい表示用 snapshot。</param>
    /// <returns>置き換え前の view snapshot。</returns>
    private IList ReplacePlaylistViewRows(IList viewRows, PlaylistRequestIdentity requestIdentity)
    {
        LogPlaylistWeakReferenceStatus("before_view_replace");
        IList previousViewRows = null;
        long previousViewGenerationId = 0L;
        long currentViewGenerationId = 0L;
        int sourceRowsAlive = 0;
        lock (playlistViewState.SyncRoot)
        {
            previousViewRows = playlistViewState.CurrentViewRows;
            previousViewGenerationId = playlistViewState.CurrentViewGenerationId;
            if (previousViewRows != null)
            {
                playlistViewState.PreviousViewRowsWeakReference = new WeakReference<IList>(previousViewRows);
                playlistViewState.PreviousViewGenerationId = previousViewGenerationId;
            }
            playlistViewState.CurrentViewRows = viewRows ?? new List<object>();
            playlistViewState.CurrentViewGenerationId++;
            currentViewGenerationId = playlistViewState.CurrentViewGenerationId;
            playlistViewState.LastAppliedViewCount = playlistViewState.CurrentViewRows.Count;
            playlistViewState.CurrentViewIdentity = requestIdentity;
            sourceRowsAlive = CountPlaylistSourceRows(playlistViewState.SourceRows);
        }
        LogPlaylistRetention(((viewRows == null || viewRows.Count == 0) ? "playlist_view_clear " : "playlist_view_replace ") + "generationId=" + currentViewGenerationId + " previousGenerationId=" + previousViewGenerationId + " sourceCount=" + sourceRowsAlive + " viewCount=" + (viewRows?.Count ?? 0) + " playlistSourceRowCount=" + sourceRowsAlive + " playlistViewRowCount=" + CountPlaylistDetailRows(viewRows) + " previousViewRowsReferenced=" + CountPlaylistDetailRows(previousViewRows) + " disposedCount=" + CountPlaylistDetailRows(previousViewRows) + " selectedIndex=" + SelectedIndexBMSFilesView);
        return previousViewRows;
    }

    /// <summary>
    /// playlist 表示用 snapshot の仮想行を破棄します。
    /// </summary>
    /// <param name="viewRows">破棄する表示用 snapshot。</param>
    private static void DisposePlaylistViewRows(IEnumerable viewRows)
    {
    }

    public cSortParameters SortParameters
    {
        get
        {
            return _SortParameters;
        }
        private set
        {
            if (value == null || _SortParameters == null || !(_SortParameters.ColumnsName == value.ColumnsName) || _SortParameters.Direction != value.Direction)
            {
                _SortParameters = value;
                RaisePropertyChanged("SortParameters");
            }
        }
    }

    public cSortParameters PlaylistSummarySortParameters
    {
        get
        {
            return _PlaylistSummarySortParameters;
        }
        private set
        {
            if (value == null || _PlaylistSummarySortParameters == null || !(_PlaylistSummarySortParameters.ColumnsName == value.ColumnsName) || _PlaylistSummarySortParameters.Direction != value.Direction)
            {
                _PlaylistSummarySortParameters = value;
                RaisePropertyChanged("PlaylistSummarySortParameters");
            }
        }
    }

    /// <summary>
    /// 最新の一覧更新要求を識別するIDを返します。
    /// MainWindow 側の描画遅延計測ログを main_view_build と突き合わせるために使用します。
    /// </summary>
    public long LastMainViewBuildRequestId => Interlocked.Read(ref lastMainViewBuildRequestId);

    /// <summary>
    /// 最新の main_view_build 完了時刻 (Stopwatch タイムスタンプ) を返します。
    /// </summary>
    public long LastMainViewBuildEndTimestamp => Interlocked.Read(ref lastMainViewBuildEndTimestamp);

    /// <summary>
    /// 最新の main_view_build を実行したスレッドIDを返します。
    /// </summary>
    public int LastMainViewBuildThreadId => Volatile.Read(ref lastMainViewBuildThreadId);

    /// <summary>
    /// 最新の main_view_build 実行時モードを int 値で返します。
    /// </summary>
    public int LastMainViewBuildMode => Volatile.Read(ref lastMainViewBuildMode);

    /// <summary>
    /// 現在プレビュー再生（または再生準備）中である BMS ファイルを表す状態プロパティです。
    /// BMSPlayer からのフィードバックやプレビュー指示に応じて操作され、UI上でどの曲が再生中かの表示管理に使われます。
    /// </summary>
    public BeMusicSeeker.Models.BMSFile NowPlayingBMS
    {
        get
        {
            return _NowPlayingBMS;
        }
        set
        {
            if (_NowPlayingBMS != value)
            {
                _NowPlayingBMS = value;
                RaisePropertyChanged("NowPlayingBMS");
            }
        }
    }

    public Uri BrowserSource
    {
        get
        {
            return _BrowserSource;
        }
        set
        {
            if (!(_BrowserSource == value))
            {
                _BrowserSource = value;
                RaisePropertyChanged("BrowserSource");
            }
        }
    }

    public string BrowserHtml
    {
        get
        {
            return _BrowserHtml;
        }
        set
        {
            if (!(_BrowserHtml == value))
            {
                _BrowserHtml = value;
                RaisePropertyChanged("BrowserHtml");
            }
        }
    }

    public int SelectedIndexBMSFilesView
    {
        get
        {
            return _SelectedIndexBMSFilesView;
        }
        set
        {
            if (_SelectedIndexBMSFilesView != value)
            {
                _SelectedIndexBMSFilesView = value;
                RaisePropertyChanged("SelectedIndexBMSFilesView");
            }
        }
    }

    public dataGridColumnsSettings ColumnsSettingsBMSFilesView
    {
        get
        {
            return _ColumnsSettingsBMSFilesView;
        }
        set
        {
            if (ReferenceEquals(_ColumnsSettingsBMSFilesView, value))
            {
                return;
            }
            _ColumnsSettingsBMSFilesView = value;
            RaisePropertyChanged("ColumnsSettingsBMSFilesView");
        }
    }

    public Visibility ColumnSettingsVisibilityForPlaylist
    {
        get
        {
            return _ColumnSettingsVisibilityForPlaylist;
        }
        set
        {
            if (_ColumnSettingsVisibilityForPlaylist != value)
            {
                _ColumnSettingsVisibilityForPlaylist = value;
                RaisePropertyChanged("ColumnSettingsVisibilityForPlaylist");
            }
        }
    }

    /// <summary>
    /// プレイリストサマリー画面 (特定のプレイリスト配下のランプ状況等を集計した表) を構成する行データのコレクションです。
    /// プレイリストの選択状態に応じて動的に集計・更新されます。
    /// </summary>
    public ObservableCollection<PlaylistSummaryRow> PlaylistSummaryView
    {
        get
        {
            return _PlaylistSummaryView;
        }
        set
        {
            if (_PlaylistSummaryView != value)
            {
                ObservableCollection<PlaylistSummaryRow> previousView = _PlaylistSummaryView;
                _PlaylistSummaryView = value ?? new ObservableCollection<PlaylistSummaryRow>();
                if (previousView != null && !ReferenceEquals(previousView, _PlaylistSummaryView))
                {
                    previousPlaylistSummaryViewWeakReference = new WeakReference<ObservableCollection<PlaylistSummaryRow>>(previousView);
                }
                Interlocked.Exchange(ref lastPlaylistSummaryBuildCompletedTimestamp, Stopwatch.GetTimestamp());
                RaisePropertyChanged("PlaylistSummaryView");
                TrySchedulePlaylistReloadCleanup();
            }
        }
    }

    public bool IsPlaylistSummaryMode
    {
        get
        {
            return _IsPlaylistSummaryMode;
        }
        set
        {
            if (_IsPlaylistSummaryMode != value)
            {
                _IsPlaylistSummaryMode = value;
                RaisePropertyChanged("IsPlaylistSummaryMode");
                UpdateKeywordSearchPresentation();
            }
        }
    }

    /// <summary>
    /// 現在のメイン一覧がプレイリスト詳細表示モードかどうかを示します。
    /// </summary>
    public bool IsPlaylistDetailViewActive
    {
        get
        {
            return _IsPlaylistDetailViewActive;
        }
    }

    /// <summary>
    /// メイン一覧の表示コレクションを非同期で張るかどうかを示します。
    /// playlist 詳細表示では同期反映に切り替えて旧 ItemsSource の保持を減らします。
    /// </summary>
    public bool UseAsyncBMSFilesViewBinding
    {
        get
        {
            return _UseAsyncBMSFilesViewBinding;
        }
    }

    public bool IsStartupUiInteractionBlocked
    {
        get
        {
            return _IsStartupUiInteractionBlocked;
        }
    }

    internal void SetStartupUiInteractionBlocked(bool value)
    {
        if (_IsStartupUiInteractionBlocked == value)
        {
            return;
        }
        _IsStartupUiInteractionBlocked = value;
        RaisePropertyChanged("IsStartupUiInteractionBlocked");
    }

    /// <summary>
    /// 現在採用中の playlist source 世代を返します。
    /// </summary>
    public long PlaylistSourceGenerationId
    {
        get
        {
            lock (playlistViewState.SyncRoot)
            {
                return playlistViewState.SourceGenerationId;
            }
        }
    }

    /// <summary>
    /// 現在採用中の playlist view 世代を返します。
    /// </summary>
    public long PlaylistAdoptedViewGenerationId
    {
        get
        {
            lock (playlistViewState.SyncRoot)
            {
                return playlistViewState.CurrentViewGenerationId;
            }
        }
    }

    /// <summary>
    /// playlist 一覧側の描画・待機完了後に retention 状態を追加計測します。
    /// </summary>
    /// <param name="checkpoint">計測契機名。</param>
    /// <param name="expectedSourceGenerationId">UI で観測した source 世代。</param>
    /// <param name="expectedViewGenerationId">UI で観測した view 世代。</param>
    public void LogPlaylistUiRetentionCheckpoint(string checkpoint, long expectedSourceGenerationId, long expectedViewGenerationId)
    {
        if (!installPerformanceLoggingEnabled || !_IsPlaylistDetailViewActive)
        {
            return;
        }
        long currentSourceGenerationId;
        long currentViewGenerationId;
        int sourceRowsAlive;
        int currentViewRowsAlive;
        int lastAppliedViewCount;
        lock (playlistViewState.SyncRoot)
        {
            currentSourceGenerationId = playlistViewState.SourceGenerationId;
            currentViewGenerationId = playlistViewState.CurrentViewGenerationId;
            sourceRowsAlive = CountPlaylistSourceRows(playlistViewState.SourceRows);
            currentViewRowsAlive = CountPlaylistDetailRows(playlistViewState.CurrentViewRows);
            lastAppliedViewCount = playlistViewState.LastAppliedViewCount;
        }
        bool stale = currentSourceGenerationId != expectedSourceGenerationId || currentViewGenerationId != expectedViewGenerationId;
        LogPlaylistRetention("playlist_ui_retention_checkpoint checkpoint=" + checkpoint + " expectedSourceGenerationId=" + expectedSourceGenerationId + " expectedViewGenerationId=" + expectedViewGenerationId + " currentSourceGenerationId=" + currentSourceGenerationId + " currentViewGenerationId=" + currentViewGenerationId + " stale=" + stale + " playlistSourceRowCount=" + sourceRowsAlive + " playlistViewRowCount=" + currentViewRowsAlive + " lastAppliedViewCount=" + lastAppliedViewCount);
        LogPlaylistWeakReferenceStatus("ui_" + checkpoint);
    }

    public string GridHeaderText
    {
        get
        {
            return _GridHeaderText;
        }
        set
        {
            if (!(_GridHeaderText == value))
            {
                _GridHeaderText = value ?? string.Empty;
                RaisePropertyChanged("GridHeaderText");
            }
        }
    }

    public string GridSummaryText
    {
        get
        {
            return _GridSummaryText;
        }
        set
        {
            if (!(_GridSummaryText == value))
            {
                _GridSummaryText = value ?? string.Empty;
                RaisePropertyChanged("GridSummaryText");
            }
        }
    }

    public bool IsDropInstallQueueActive
    {
        get
        {
            return _IsDropInstallQueueActive;
        }
        private set
        {
            if (_IsDropInstallQueueActive != value)
            {
                _IsDropInstallQueueActive = value;
                RaisePropertyChanged("IsDropInstallQueueActive");
            }
        }
    }

    public string DropInstallQueueLabel
    {
        get
        {
            return _DropInstallQueueLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_DropInstallQueueLabel == normalized))
            {
                _DropInstallQueueLabel = normalized;
                RaisePropertyChanged("DropInstallQueueLabel");
            }
        }
    }

    public string DropInstallQueueSubLabel
    {
        get
        {
            return _DropInstallQueueSubLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_DropInstallQueueSubLabel == normalized))
            {
                _DropInstallQueueSubLabel = normalized;
                RaisePropertyChanged("DropInstallQueueSubLabel");
            }
        }
    }

    public bool DropInstallQueueCanCancel
    {
        get
        {
            return _DropInstallQueueCanCancel;
        }
        private set
        {
            if (_DropInstallQueueCanCancel != value)
            {
                _DropInstallQueueCanCancel = value;
                RaisePropertyChanged("DropInstallQueueCanCancel");
            }
        }
    }

    public int DropInstallQueuePendingBatchCount
    {
        get
        {
            return _DropInstallQueuePendingBatchCount;
        }
        private set
        {
            if (_DropInstallQueuePendingBatchCount != value)
            {
                _DropInstallQueuePendingBatchCount = value;
                RaisePropertyChanged("DropInstallQueuePendingBatchCount");
            }
        }
    }

    public bool IsPendingEstimateQueueActive
    {
        get
        {
            return _IsPendingEstimateQueueActive;
        }
        private set
        {
            if (_IsPendingEstimateQueueActive != value)
            {
                _IsPendingEstimateQueueActive = value;
                RaisePropertyChanged("IsPendingEstimateQueueActive");
            }
        }
    }

    public string PendingEstimateQueueLabel
    {
        get
        {
            return _PendingEstimateQueueLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_PendingEstimateQueueLabel == normalized))
            {
                _PendingEstimateQueueLabel = normalized;
                RaisePropertyChanged("PendingEstimateQueueLabel");
            }
        }
    }

    public string PendingEstimateQueueSubLabel
    {
        get
        {
            return _PendingEstimateQueueSubLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_PendingEstimateQueueSubLabel == normalized))
            {
                _PendingEstimateQueueSubLabel = normalized;
                RaisePropertyChanged("PendingEstimateQueueSubLabel");
            }
        }
    }

    public int PendingEstimateQueuePendingBatchCount
    {
        get
        {
            return _PendingEstimateQueuePendingBatchCount;
        }
        private set
        {
            if (_PendingEstimateQueuePendingBatchCount != value)
            {
                _PendingEstimateQueuePendingBatchCount = value;
                RaisePropertyChanged("PendingEstimateQueuePendingBatchCount");
            }
        }
    }

    public bool IsInstallPipelineStatusActive
    {
        get
        {
            return _IsInstallPipelineStatusActive;
        }
        private set
        {
            if (_IsInstallPipelineStatusActive != value)
            {
                _IsInstallPipelineStatusActive = value;
                RaisePropertyChanged("IsInstallPipelineStatusActive");
            }
        }
    }

    public string InstallPipelineLabel
    {
        get
        {
            return _InstallPipelineLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_InstallPipelineLabel == normalized))
            {
                _InstallPipelineLabel = normalized;
                RaisePropertyChanged("InstallPipelineLabel");
            }
        }
    }

    public string InstallPipelineSubLabel
    {
        get
        {
            return _InstallPipelineSubLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_InstallPipelineSubLabel == normalized))
            {
                _InstallPipelineSubLabel = normalized;
                RaisePropertyChanged("InstallPipelineSubLabel");
            }
        }
    }

    public int InstallPipelineValue
    {
        get
        {
            return _InstallPipelineValue;
        }
        private set
        {
            if (_InstallPipelineValue != value)
            {
                _InstallPipelineValue = value;
                RaisePropertyChanged("InstallPipelineValue");
            }
        }
    }

    public int InstallPipelineMaximum
    {
        get
        {
            return _InstallPipelineMaximum;
        }
        private set
        {
            int normalized = Math.Max(1, value);
            if (_InstallPipelineMaximum != normalized)
            {
                _InstallPipelineMaximum = normalized;
                RaisePropertyChanged("InstallPipelineMaximum");
            }
        }
    }

    public bool InstallPipelineCanCancel
    {
        get
        {
            return _InstallPipelineCanCancel;
        }
        private set
        {
            if (_InstallPipelineCanCancel != value)
            {
                _InstallPipelineCanCancel = value;
                RaisePropertyChanged("InstallPipelineCanCancel");
            }
        }
    }

    public bool IsPlaylistSyncProgressActive
    {
        get
        {
            return _IsPlaylistSyncProgressActive;
        }
        private set
        {
            if (_IsPlaylistSyncProgressActive != value)
            {
                _IsPlaylistSyncProgressActive = value;
                RaisePropertyChanged("IsPlaylistSyncProgressActive");
            }
        }
    }

    public string PlaylistSyncProgressLabel
    {
        get
        {
            return _PlaylistSyncProgressLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_PlaylistSyncProgressLabel == normalized))
            {
                _PlaylistSyncProgressLabel = normalized;
                RaisePropertyChanged("PlaylistSyncProgressLabel");
            }
        }
    }

    public string PlaylistSyncProgressSubLabel
    {
        get
        {
            return _PlaylistSyncProgressSubLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_PlaylistSyncProgressSubLabel == normalized))
            {
                _PlaylistSyncProgressSubLabel = normalized;
                RaisePropertyChanged("PlaylistSyncProgressSubLabel");
            }
        }
    }

    public double PlaylistSyncProgressValue
    {
        get
        {
            return _PlaylistSyncProgressValue;
        }
        private set
        {
            if (_PlaylistSyncProgressValue != value)
            {
                _PlaylistSyncProgressValue = value;
                RaisePropertyChanged("PlaylistSyncProgressValue");
            }
        }
    }

    public double PlaylistSyncProgressMaximum
    {
        get
        {
            return _PlaylistSyncProgressMaximum;
        }
        private set
        {
            if (_PlaylistSyncProgressMaximum != value)
            {
                _PlaylistSyncProgressMaximum = value;
                RaisePropertyChanged("PlaylistSyncProgressMaximum");
            }
        }
    }

    /// <summary>
    /// 起動・リロード進捗をステータスバーへ表示中かどうかを返します。
    /// </summary>
    public bool IsStartupProgressActive
    {
        get
        {
            return _IsStartupProgressActive;
        }
        private set
        {
            if (_IsStartupProgressActive != value)
            {
                _IsStartupProgressActive = value;
                RaisePropertyChanged("IsStartupProgressActive");
            }
        }
    }

    /// <summary>
    /// 起動・リロード進捗の主ラベルを返します。
    /// </summary>
    public string StartupProgressLabel
    {
        get
        {
            return _StartupProgressLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_StartupProgressLabel == normalized))
            {
                _StartupProgressLabel = normalized;
                RaisePropertyChanged("StartupProgressLabel");
            }
        }
    }

    /// <summary>
    /// 起動・リロード進捗の補助ラベルを返します。
    /// </summary>
    public string StartupProgressSubLabel
    {
        get
        {
            return _StartupProgressSubLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (!(_StartupProgressSubLabel == normalized))
            {
                _StartupProgressSubLabel = normalized;
                RaisePropertyChanged("StartupProgressSubLabel");
            }
        }
    }

    /// <summary>
    /// 起動・リロード進捗バーの現在値を返します。
    /// </summary>
    public double StartupProgressValue
    {
        get
        {
            return _StartupProgressValue;
        }
        private set
        {
            if (_StartupProgressValue != value)
            {
                _StartupProgressValue = value;
                RaisePropertyChanged("StartupProgressValue");
            }
        }
    }

    /// <summary>
    /// 起動・リロード進捗バーの最大値を返します。
    /// </summary>
    public double StartupProgressMaximum
    {
        get
        {
            return _StartupProgressMaximum;
        }
        private set
        {
            if (_StartupProgressMaximum != value)
            {
                _StartupProgressMaximum = value;
                RaisePropertyChanged("StartupProgressMaximum");
            }
        }
    }

    public PlaylistSummaryColumnSettings PlaylistSummaryColumnsSettings
    {
        get
        {
            return _PlaylistSummaryColumnsSettings;
        }
        set
        {
            if (ReferenceEquals(_PlaylistSummaryColumnsSettings, value))
            {
                return;
            }
            _PlaylistSummaryColumnsSettings = value;
            RaisePropertyChanged("PlaylistSummaryColumnsSettings");
        }
    }

    public bool IsPlaylistTreeExpanded
    {
        get
        {
            return _IsPlaylistTreeExpanded;
        }
        set
        {
            if (_IsPlaylistTreeExpanded != value)
            {
                _IsPlaylistTreeExpanded = value;
                RaisePropertyChanged("IsPlaylistTreeExpanded");
            }
        }
    }

    public ModeFilterType ModeFilter
    {
        get
        {
            return _ModeFilter;
        }
        set
        {
            if (_ModeFilter != value)
            {
                if (value != ModeFilterType.None)
                {
                    _ModeFilter = value;
                }
                RaisePropertyChanged("ModeFilter");
                if (value != ModeFilterType.None)
                {
                    makeBMSFilesView(viewUpdateMode.ModeFilterUpdated);
                }
            }
        }
    }

    public string KeywordFilter
    {
        get
        {
            return _KeywordFilter;
        }
        set
        {
            if (!(_KeywordFilter == value))
            {
                _KeywordFilter = value;
                RaisePropertyChanged("KeywordFilter");
                UpdateKeywordSearchPresentation();
                makeBMSFilesView(viewUpdateMode.KeywordFilterUpdated);
            }
        }
    }

    public string KeywordSearchWarningText => _KeywordSearchWarningText;

    public bool HasKeywordSearchWarning => !string.IsNullOrWhiteSpace(_KeywordSearchWarningText);

    public string KeywordSearchHelpText => BuildKeywordSearchHelpText(GetCurrentKeywordSearchContext());

    public bool IsKeywordSearchHelpOpen
    {
        get
        {
            return _IsKeywordSearchHelpOpen;
        }
        set
        {
            if (_IsKeywordSearchHelpOpen != value)
            {
                _IsKeywordSearchHelpOpen = value;
                RaisePropertyChanged("IsKeywordSearchHelpOpen");
            }
        }
    }

    public string PlaylistSummaryKeywordFilter
    {
        get
        {
            return _PlaylistSummaryKeywordFilter;
        }
        set
        {
            string text = value ?? string.Empty;
            if (!(_PlaylistSummaryKeywordFilter == text))
            {
                _PlaylistSummaryKeywordFilter = text;
                RaisePropertyChanged("PlaylistSummaryKeywordFilter");
                UpdatePlaylistSummaryKeywordSearchPresentation();
                if (IsPlaylistSummaryMode)
                {
                    RefreshPlaylistSummaryPresentationIfVisible();
                }
            }
        }
    }

    public string PlaylistSummaryKeywordSearchWarningText => _PlaylistSummaryKeywordSearchWarningText;

    public bool HasPlaylistSummaryKeywordSearchWarning => !string.IsNullOrWhiteSpace(_PlaylistSummaryKeywordSearchWarningText);

    public string PlaylistSummaryKeywordSearchHelpText => BuildKeywordSearchHelpText(GridKeywordSearchContext.PlaylistSummary);

    public bool IsPlaylistSummaryKeywordSearchHelpOpen
    {
        get
        {
            return _IsPlaylistSummaryKeywordSearchHelpOpen;
        }
        set
        {
            if (_IsPlaylistSummaryKeywordSearchHelpOpen != value)
            {
                _IsPlaylistSummaryKeywordSearchHelpOpen = value;
                RaisePropertyChanged("IsPlaylistSummaryKeywordSearchHelpOpen");
            }
        }
    }

    /// <summary>
    /// 通常検索欄に表示する field 補完・履歴候補です。
    /// </summary>
    public ObservableCollection<KeywordSearchSuggestionItem> KeywordSearchSuggestions => _KeywordSearchSuggestions;

    /// <summary>
    /// プレイリスト一覧検索欄に表示する field 補完・履歴候補です。
    /// </summary>
    public ObservableCollection<KeywordSearchSuggestionItem> PlaylistSummaryKeywordSearchSuggestions => _PlaylistSummaryKeywordSearchSuggestions;

    /// <summary>
    /// 通常検索欄の候補 popup が開いているかどうかを取得または設定します。
    /// </summary>
    public bool IsKeywordSearchSuggestionPopupOpen
    {
        get
        {
            return _IsKeywordSearchSuggestionPopupOpen;
        }
        set
        {
            if (_IsKeywordSearchSuggestionPopupOpen != value)
            {
                _IsKeywordSearchSuggestionPopupOpen = value;
                RaisePropertyChanged("IsKeywordSearchSuggestionPopupOpen");
            }
        }
    }

    /// <summary>
    /// プレイリスト一覧検索欄の候補 popup が開いているかどうかを取得または設定します。
    /// </summary>
    public bool IsPlaylistSummaryKeywordSearchSuggestionPopupOpen
    {
        get
        {
            return _IsPlaylistSummaryKeywordSearchSuggestionPopupOpen;
        }
        set
        {
            if (_IsPlaylistSummaryKeywordSearchSuggestionPopupOpen != value)
            {
                _IsPlaylistSummaryKeywordSearchSuggestionPopupOpen = value;
                RaisePropertyChanged("IsPlaylistSummaryKeywordSearchSuggestionPopupOpen");
            }
        }
    }

    /// <summary>
    /// 通常検索欄の候補 popup 見出しです。
    /// </summary>
    public string KeywordSearchSuggestionHeaderText => _KeywordSearchSuggestionHeaderText;

    /// <summary>
    /// プレイリスト一覧検索欄の候補 popup 見出しです。
    /// </summary>
    public string PlaylistSummaryKeywordSearchSuggestionHeaderText => _PlaylistSummaryKeywordSearchSuggestionHeaderText;

    internal GridKeywordSearchContext CurrentKeywordSearchContext => GetCurrentKeywordSearchContext();

    /// <summary>
    /// 通常検索欄の補完・履歴候補を更新します。
    /// </summary>
    /// <param name="keywordFilter">検索欄の現在値。</param>
    /// <param name="caretIndex">現在の caret 位置。</param>
    /// <param name="forceHistory">field 補完が無い時に履歴を表示するか。</param>
    internal void RefreshKeywordSearchSuggestions(string keywordFilter, int caretIndex, bool forceHistory)
    {
        RefreshKeywordSearchSuggestions(
            _KeywordSearchSuggestions,
            GetCurrentKeywordSearchContext(),
            keywordSearchHistory,
            keywordFilter,
            caretIndex,
            forceHistory,
            isPlaylistSummary: false);
    }

    /// <summary>
    /// プレイリスト一覧検索欄の補完・履歴候補を更新します。
    /// </summary>
    /// <param name="keywordFilter">検索欄の現在値。</param>
    /// <param name="caretIndex">現在の caret 位置。</param>
    /// <param name="forceHistory">field 補完が無い時に履歴を表示するか。</param>
    internal void RefreshPlaylistSummaryKeywordSearchSuggestions(string keywordFilter, int caretIndex, bool forceHistory)
    {
        RefreshKeywordSearchSuggestions(
            _PlaylistSummaryKeywordSearchSuggestions,
            GridKeywordSearchContext.PlaylistSummary,
            playlistSummaryKeywordSearchHistory,
            keywordFilter,
            caretIndex,
            forceHistory,
            isPlaylistSummary: true);
    }

    /// <summary>
    /// 通常検索欄の候補 popup を閉じます。
    /// </summary>
    internal void CloseKeywordSearchSuggestions()
    {
        IsKeywordSearchSuggestionPopupOpen = false;
    }

    /// <summary>
    /// プレイリスト一覧検索欄の候補 popup を閉じます。
    /// </summary>
    internal void ClosePlaylistSummaryKeywordSearchSuggestions()
    {
        IsPlaylistSummaryKeywordSearchSuggestionPopupOpen = false;
    }

    /// <summary>
    /// 通常検索欄の検索履歴へ現在値を追加します。
    /// </summary>
    /// <param name="keywordFilter">保存する検索文字列。</param>
    internal void CommitKeywordSearchHistory(string keywordFilter)
    {
        ReplaceKeywordSearchHistory(keywordSearchHistory, KeywordSearchHistoryStore.AddEntry(keywordSearchHistory, keywordFilter));
        Settings.Default.KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(keywordSearchHistory);
    }

    /// <summary>
    /// プレイリスト一覧検索欄の検索履歴へ現在値を追加します。
    /// </summary>
    /// <param name="keywordFilter">保存する検索文字列。</param>
    internal void CommitPlaylistSummaryKeywordSearchHistory(string keywordFilter)
    {
        ReplaceKeywordSearchHistory(playlistSummaryKeywordSearchHistory, KeywordSearchHistoryStore.AddEntry(playlistSummaryKeywordSearchHistory, keywordFilter));
        Settings.Default.PlaylistSummaryKeywordSearchHistory = KeywordSearchHistoryStore.Serialize(playlistSummaryKeywordSearchHistory);
    }

    private GridKeywordSearchContext GetCurrentKeywordSearchContext()
    {
        return IsPlaylistViewMode(treeViewFilterTypeSelected)
            ? GridKeywordSearchContext.PlaylistDetail
            : GridKeywordSearchContext.BmsFile;
    }

    private void UpdateKeywordSearchPresentation()
    {
        string warningText = BuildKeywordSearchWarningText(KeywordFilter, GetCurrentKeywordSearchContext());
        if (!string.Equals(_KeywordSearchWarningText, warningText, StringComparison.Ordinal))
        {
            _KeywordSearchWarningText = warningText;
            RaisePropertyChanged("KeywordSearchWarningText");
            RaisePropertyChanged("HasKeywordSearchWarning");
        }
        RaisePropertyChanged("KeywordSearchHelpText");
    }

    private void UpdatePlaylistSummaryKeywordSearchPresentation()
    {
        string warningText = BuildKeywordSearchWarningText(PlaylistSummaryKeywordFilter, GridKeywordSearchContext.PlaylistSummary);
        if (!string.Equals(_PlaylistSummaryKeywordSearchWarningText, warningText, StringComparison.Ordinal))
        {
            _PlaylistSummaryKeywordSearchWarningText = warningText;
            RaisePropertyChanged("PlaylistSummaryKeywordSearchWarningText");
            RaisePropertyChanged("HasPlaylistSummaryKeywordSearchWarning");
        }
        RaisePropertyChanged("PlaylistSummaryKeywordSearchHelpText");
    }

    private void RefreshKeywordSearchSuggestions(ObservableCollection<KeywordSearchSuggestionItem> targetSuggestions, GridKeywordSearchContext context, IReadOnlyList<string> history, string keywordFilter, int caretIndex, bool forceHistory, bool isPlaylistSummary)
    {
        GridKeywordSearchCompletionResult fieldCompletion = GridKeywordSearchCompletion.CreateFieldCompletion(keywordFilter, caretIndex, context);
        if (fieldCompletion.Items.Count > 0)
        {
            SetKeywordSearchSuggestions(targetSuggestions, fieldCompletion.Items, KeywordSearchSuggestionKind.Field, isPlaylistSummary);
            return;
        }
        if (forceHistory)
        {
            IReadOnlyList<KeywordSearchSuggestionItem> historySuggestions = BuildKeywordSearchHistorySuggestions(history, keywordFilter);
            SetKeywordSearchSuggestions(targetSuggestions, historySuggestions, KeywordSearchSuggestionKind.History, isPlaylistSummary);
            return;
        }
        SetKeywordSearchSuggestions(targetSuggestions, Array.Empty<KeywordSearchSuggestionItem>(), KeywordSearchSuggestionKind.Field, isPlaylistSummary);
    }

    private void SetKeywordSearchSuggestions(ObservableCollection<KeywordSearchSuggestionItem> targetSuggestions, IReadOnlyList<KeywordSearchSuggestionItem> suggestions, KeywordSearchSuggestionKind kind, bool isPlaylistSummary)
    {
        targetSuggestions.Clear();
        foreach (KeywordSearchSuggestionItem suggestion in suggestions ?? Array.Empty<KeywordSearchSuggestionItem>())
        {
            targetSuggestions.Add(suggestion);
        }
        string headerText = targetSuggestions.Count == 0 ? string.Empty : BuildKeywordSearchSuggestionHeaderText(kind);
        if (isPlaylistSummary)
        {
            _PlaylistSummaryKeywordSearchSuggestionHeaderText = headerText;
            RaisePropertyChanged("PlaylistSummaryKeywordSearchSuggestionHeaderText");
            IsPlaylistSummaryKeywordSearchSuggestionPopupOpen = targetSuggestions.Count > 0;
        }
        else
        {
            _KeywordSearchSuggestionHeaderText = headerText;
            RaisePropertyChanged("KeywordSearchSuggestionHeaderText");
            IsKeywordSearchSuggestionPopupOpen = targetSuggestions.Count > 0;
        }
    }

    private static void ReplaceKeywordSearchHistory(List<string> target, IEnumerable<string> source)
    {
        target.Clear();
        target.AddRange(source ?? Enumerable.Empty<string>());
    }

    public PlaylistSummaryOwnedFilterType PlaylistSummaryOwnedFilter
    {
        get
        {
            return _PlaylistSummaryOwnedFilter;
        }
        set
        {
            if (_PlaylistSummaryOwnedFilter != value)
            {
                _PlaylistSummaryOwnedFilter = value;
                RaisePropertyChanged("PlaylistSummaryOwnedFilter");
                if (IsPlaylistSummaryMode)
                {
                    RefreshPlaylistSummaryPresentationIfVisible();
                }
            }
        }
    }

    private Func<BeMusicSeeker.Models.BMSFile, bool> FolderFilter
    {
        get
        {
            return _FolderFilter;
        }
        set
        {
            _FolderFilter = value;
            RaisePropertyChanged("FolderFilter");
            makeBMSFilesView(viewUpdateMode.FolderFilterSelected);
        }
    }

    /// <summary>
    /// アプリケーション内で認識・ツリー表示されているプレイリスト (BMSTable) 群の Observable なコレクションです。
    /// カスタムフォルダや難易度表等のプレイリスト階層構造全体を保持します。
    /// </summary>
    public DispatcherCollection<BMSTable> BMSTables
    {
        get
        {
            if (tables != null)
            {
                return tables.BMSTables;
            }
            return new DispatcherCollection<BMSTable>(DispatcherHelper.UIDispatcher);
        }
    }

    public BMSTableSimpleCategorized BMSExternalTableListExt
    {
        get
        {
            return _BMSExternalTableListExt;
        }
        private set
        {
            if (_BMSExternalTableListExt != value)
            {
                _BMSExternalTableListExt = value;
                RaisePropertyChanged("BMSExternalTableListExt");
            }
        }
    }

    public bool IsWriteLockHeldInitializeBMSFiles
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldInitializeBMSFiles;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializdBMSFilesHealthStatus
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldInitializdBMSFilesHealthStatus;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldInitializeBMSFilesEncodingInfo;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldInitializeBMSFilesZeroNote
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldInitializeBMSFilesZeroNote;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldBMSFilesPendingInstall
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldBMSFilesPendingInstall;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldBMSFilesDuplicated
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldBMSFilesDuplicated;
            }
            return false;
        }
    }

    public bool IsWriteLockHeldBMSTables
    {
        get
        {
            if (tables != null)
            {
                return tables.IsWriteLockHeldBMSTables;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldBMSTablesInitializeMin
    {
        get
        {
            if (tables != null)
            {
                return tables.IsWriteLockHeldBMSTablesInitializeMin;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldAnyBMSTable
    {
        get
        {
            if (tables != null)
            {
                return tables.IsWriteLockHeldAnyBMSTable;
            }
            return true;
        }
    }

    public bool IsPlaylistUpdating
    {
        get
        {
            if (tables != null)
            {
                return tables.IsPlaylistUpdating;
            }
            return false;
        }
    }

    public bool IsLoadingExternalCollectionBMSTables
    {
        get
        {
            return _IsLoadingExternalCollectionBMSTables;
        }
        set
        {
            if (_IsLoadingExternalCollectionBMSTables != value)
            {
                _IsLoadingExternalCollectionBMSTables = value;
                RaisePropertyChanged("IsLoadingExternalCollectionBMSTables");
            }
        }
    }

    public int LR2ID
    {
        get
        {
            if (files != null)
            {
                return files.LR2ID;
            }
            return 0;
        }
    }

    public bool IS_WIN8OR10 => Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012);

    public TimeSpan CurrentlyPlayingDuration
    {
        get
        {
            return _CurrentlyPlayingDuration;
        }
        set
        {
            if (!(_CurrentlyPlayingDuration == value))
            {
                _CurrentlyPlayingDuration = value;
                RaisePropertyChanged("CurrentlyPlayingDuration");
            }
        }
    }

    public TimeSpan CurrentlyPlayingTime
    {
        get
        {
            return bmsPlayer?.CurrentTime ?? TimeSpan.MinValue;
        }
        set
        {
            if (bmsPlayer == null)
            {
                RaisePropertyChanged("CurrentlyPlayingTime");
                return;
            }
            bmsPlayer.CurrentTime = value;
            RaisePropertyChanged("CurrentlyPlayingTime");
        }
    }

    public TimeSpan CurrentlyPlayingStopTime
    {
        get
        {
            return _CurrentlyPlayingStopTime;
        }
        private set
        {
            if (!(_CurrentlyPlayingStopTime == value))
            {
                _CurrentlyPlayingStopTime = value;
                RaisePropertyChanged("CurrentlyPlayingStopTime");
            }
        }
    }

    public TimeSpan CurrentlyPlayingBmsDuration
    {
        get
        {
            return _CurrentlyPlayingBmsDuration;
        }
        private set
        {
            if (!(_CurrentlyPlayingBmsDuration == value))
            {
                _CurrentlyPlayingBmsDuration = value;
                RaisePropertyChanged("CurrentlyPlayingBmsDuration");
            }
        }
    }

    public TimeSpan CurrentlyPlayingMusicDuration
    {
        get
        {
            return _CurrentlyPlayingMusicDuration;
        }
        private set
        {
            if (!(_CurrentlyPlayingMusicDuration == value))
            {
                _CurrentlyPlayingMusicDuration = value;
                RaisePropertyChanged("CurrentlyPlayingMusicDuration");
            }
        }
    }

    public int CurrentlyPlayingCurrentVoices
    {
        get
        {
            return _CurrentlyPlayingCurrentVoices;
        }
        private set
        {
            if (_CurrentlyPlayingCurrentVoices != value)
            {
                _CurrentlyPlayingCurrentVoices = value;
                RaisePropertyChanged("CurrentlyPlayingCurrentVoices");
            }
        }
    }

    public int CurrentlyPlayingMaxVoices
    {
        get
        {
            return _CurrentlyPlayingMaxVoices;
        }
        private set
        {
            if (_CurrentlyPlayingMaxVoices != value)
            {
                _CurrentlyPlayingMaxVoices = value;
                RaisePropertyChanged("CurrentlyPlayingMaxVoices");
            }
        }
    }

    public int CurrentlyPlayingNoteDensity
    {
        get
        {
            return _CurrentlyPlayingNoteDensity;
        }
        private set
        {
            if (_CurrentlyPlayingNoteDensity != value)
            {
                _CurrentlyPlayingNoteDensity = value;
                RaisePropertyChanged("CurrentlyPlayingNoteDensity");
            }
        }
    }

    public int CurrentlyPlayingNoteDensityMax
    {
        get
        {
            return _CurrentlyPlayingNoteDensityMax;
        }
        private set
        {
            if (_CurrentlyPlayingNoteDensityMax != value)
            {
                _CurrentlyPlayingNoteDensityMax = value;
                RaisePropertyChanged("CurrentlyPlayingNoteDensityMax");
            }
        }
    }

    public int CurrentlyPlayingBpm
    {
        get
        {
            return _CurrentlyPlayingBpm;
        }
        private set
        {
            if (_CurrentlyPlayingBpm != value)
            {
                _CurrentlyPlayingBpm = value;
                RaisePropertyChanged("CurrentlyPlayingBpm");
            }
        }
    }

    public int CurrentlyPlayingMinBpm
    {
        get
        {
            return _CurrentlyPlayingMinBpm;
        }
        private set
        {
            if (_CurrentlyPlayingMinBpm != value)
            {
                _CurrentlyPlayingMinBpm = value;
                RaisePropertyChanged("CurrentlyPlayingMinBpm");
            }
        }
    }

    public int CurrentlyPlayingMaxBpm
    {
        get
        {
            return _CurrentlyPlayingMaxBpm;
        }
        private set
        {
            if (_CurrentlyPlayingMaxBpm != value)
            {
                _CurrentlyPlayingMaxBpm = value;
                RaisePropertyChanged("CurrentlyPlayingMaxBpm");
            }
        }
    }

    public double CurrentlyPlayingTotal
    {
        get
        {
            return _CurrentlyPlayingTotal;
        }
        private set
        {
            if (_CurrentlyPlayingTotal != value)
            {
                _CurrentlyPlayingTotal = value;
                RaisePropertyChanged("CurrentlyPlayingTotal");
            }
        }
    }

    public int CurrentlyPlayingCombo
    {
        get
        {
            return _CurrentlyPlayingCombo;
        }
        private set
        {
            if (_CurrentlyPlayingCombo != value)
            {
                _CurrentlyPlayingCombo = value;
                RaisePropertyChanged("CurrentlyPlayingCombo");
            }
        }
    }

    public int CurrentlyPlayingNotes
    {
        get
        {
            return _CurrentlyPlayingNotes;
        }
        private set
        {
            if (_CurrentlyPlayingNotes != value)
            {
                _CurrentlyPlayingNotes = value;
                RaisePropertyChanged("CurrentlyPlayingNotes");
            }
        }
    }

    public int CurrentlyPlayingMeasure
    {
        get
        {
            return _CurrentlyPlayingMeasure;
        }
        private set
        {
            if (_CurrentlyPlayingMeasure != value)
            {
                _CurrentlyPlayingMeasure = value;
                RaisePropertyChanged("CurrentlyPlayingMeasure");
            }
        }
    }

    public int CurrentlyPlayingLastMeasure
    {
        get
        {
            return _CurrentlyPlayingLastMeasure;
        }
        private set
        {
            if (_CurrentlyPlayingLastMeasure != value)
            {
                _CurrentlyPlayingLastMeasure = value;
                RaisePropertyChanged("CurrentlyPlayingLastMeasure");
            }
        }
    }

    public int PlayerVolume
    {
        get
        {
            return Settings.Default.uBMplayVolume;
        }
        set
        {
            if (Settings.Default.uBMplayVolume != value)
            {
                Settings.Default.uBMplayVolume = value;
                RaisePropertyChanged("PlayerVolume");
                Task.Run(delegate
                {
                    uBMplayVolumeChanged();
                }).Logging("PlayerVolume");
            }
        }
    }

    /// <summary>
    /// <see cref="MainWindowViewModel"/> クラスの新しいインスタンスを初期化します。
    /// 設定情報に基づくプレースホルダーの初期状態設定や、内包される <see cref="SettingDialogViewModel"/> の生成を行います。
    /// </summary>
    public MainWindowViewModel()
    {
        regularBmsLibraryRowCache = new NormalLibraryRowCache(OnNormalLibrarySortKeyChanged);
        _IsPlaylistTreeExpanded = Settings.Default.StartupExpandPlaylistTree;
        ReplaceKeywordSearchHistory(keywordSearchHistory, KeywordSearchHistoryStore.Deserialize(Settings.Default.KeywordSearchHistory));
        ReplaceKeywordSearchHistory(playlistSummaryKeywordSearchHistory, KeywordSearchHistoryStore.Deserialize(Settings.Default.PlaylistSummaryKeywordSearchHistory));
        settingDialog = new SettingDialogViewModel(this);
        dropInstallQueueProcessor = new DropInstallQueueProcessor(ProcessDroppedInstallBatch, UpdateDropInstallQueueStatus, HandleDroppedInstallBatchException);
    }

    /// <summary>
    /// データベース側からプレイリスト情報 (BMSTable) を再読み込みし、コレクションを更新します。<br/>
    /// バックグラウンドで初期化を行い、更新完了後に外部同期などを再スケジュールします。
    /// </summary>
    public async void ReloadTables()
    {
        if (!initializationCompleted)
        {
            return;
        }
        StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);
        await _semaphore.WaitAsync();
        Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction = CreatePlaylistReferenceReplaceUpdateCallback();
        bool scheduleDeferredExternalSync = false;
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree);
            SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
            Action taskAdd1 = delegate
            {
                tables.Initialize(reloadExtPlaylist: false, updateCallbackAction, semaphore);
            };
            await Task.Run(delegate
            {
                files.Initialize(new List<Action> { taskAdd1 }, semaphore, true);
            }).Logging("ReloadTables");
            scheduleDeferredExternalSync = true;
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable);
            _semaphore.Release();
        }
        if (scheduleDeferredExternalSync)
        {
            StartDeferredExternalPlaylistSync("ReloadTables", fromReloadTables: true, updateCallbackAction);
        }
    }

    /// <summary>
    /// データベース側およびファイルシステム上の BMS ファイル情報 (BMSLibrary) を再読み込みし、コレクションを更新します。<br/>
    /// UI スレッドでの不要な描画を抑制しながらバックグラウンドで処理し、プレイリストの参照解決を再スケジュールします。
    /// </summary>
    public async void ReloadFiles()
    {
        if (!initializationCompleted)
        {
            return;
        }
        StartStartupProgressOperation(StartupProgressOperationKind.ReloadFiles);
        LogInitStage("start", "ReloadFiles");
        bool scheduleDeferredPlaylistRef = false;
        await _semaphore.WaitAsync();
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
            LogInitStage("files_initialize_task_start", "ReloadFiles");
            await Task.Run(delegate
            {
                LogInitStage("files_initialize_call", "ReloadFiles");
                files.Initialize(null, null, false);
            }).Logging("ReloadFiles");
            LogInitStage("files_initialize_done", "ReloadFiles");
            scheduleDeferredPlaylistRef = true;
            if (!TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                bmsParentFolderListViewInitialized = false;
                ScheduleDeferredLibraryFolderTreeRefresh();
            }
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable);
            LogInitStage("ui_suppress_end_called", "ReloadFiles");
            _semaphore.Release();
        }
        if (scheduleDeferredPlaylistRef)
        {
            ScheduleDeferredPlaylistReferenceApply("ReloadFiles");
            LogInitStage("deferred_playlist_ref_queued", "ReloadFiles");
        }
    }

    internal static string BuildBmsonMigrationWarningMessage(BmsonMigrationPreflightResult preflightResult)
    {
        if (preflightResult == null)
        {
            throw new ArgumentNullException(nameof(preflightResult));
        }
        return BeMusicSeeker.Properties.Resources.BmsonMigrationWarningMessage;
    }

    internal static bool ApplyBmsonMigrationPreflightForStartup(BmsonMigrationPreflightResult preflightResult, ref bool approvedForSession, Func<string, bool?> confirmWarning, Action ensureSchema, Action shutdown, Action resetColumnSettings = null)
    {
        if (preflightResult == null)
        {
            throw new ArgumentNullException(nameof(preflightResult));
        }
        if (ensureSchema == null)
        {
            throw new ArgumentNullException(nameof(ensureSchema));
        }
        if (preflightResult.WarnRequired && !approvedForSession)
        {
            if (confirmWarning == null)
            {
                throw new ArgumentNullException(nameof(confirmWarning));
            }
            if (confirmWarning(BuildBmsonMigrationWarningMessage(preflightResult)) != true)
            {
                shutdown?.Invoke();
                return false;
            }
            approvedForSession = true;
        }
        ensureSchema();
        if (preflightResult.WarnRequired)
        {
            resetColumnSettings?.Invoke();
        }
        return true;
    }

    internal static bool ResetBmsonColumnSettingsForMigrationIfNeeded(Settings settings, Action saveSettings = null)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        if (settings.BmsonColumnSettingsMigrationVersion >= CurrentBmsonColumnSettingsMigrationVersion)
        {
            return false;
        }
        settings.StandardColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
        settings.ZeroNoteColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.ZERO_NOTE);
        settings.PlaylistColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.PLAYLIST);
        settings.FullScanColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.FULLSCAN);
        settings.DuplicateColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.DUPLICATE);
        settings.EncodingColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.ENCODING);
        settings.InstallColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.INSTALL);
        settings.BmsonColumnSettingsMigrationVersion = CurrentBmsonColumnSettingsMigrationVersion;
        if (saveSettings != null)
        {
            saveSettings();
        }
        else
        {
            settings.Save();
        }
        return true;
    }

    private bool EnsureBmsonMigrationApprovedForStartup()
    {
        LogInitStage("bmson_preflight_inspect_start", "Initialize");
        BmsonMigrationPreflightService bmsonMigrationPreflightService = new BmsonMigrationPreflightService();
        BmsonMigrationPreflightResult preflightResult = bmsonMigrationPreflightService.Inspect(Settings.Default.LR2SongDBPath);
        LogInitStage("bmson_preflight_inspect_done", "Initialize");
        if (preflightResult.RepairRequired && !preflightResult.WarnRequired)
        {
            LogInitStage("bmson_preflight_repair_start", "Initialize");
            new BmsLibraryDbGateway(Settings.Default.LR2SongDBPath).EnsureBmsonSchema();
            LogInitStage("bmson_preflight_repair_done", "Initialize");
            LogInitStage("bmson_preflight_reinspect_start", "Initialize");
            preflightResult = bmsonMigrationPreflightService.Inspect(Settings.Default.LR2SongDBPath);
            LogInitStage("bmson_preflight_reinspect_done", "Initialize");
            if (preflightResult.RepairRequired)
            {
                throw new InvalidOperationException("bmson app-owned schema repair did not converge.");
            }
        }
        return ApplyBmsonMigrationPreflightForStartup(preflightResult, ref bmsonMigrationApprovedForSession, delegate (string message)
        {
            LogInitStage("bmson_preflight_prompt_show", "Initialize");
            ConfirmationMessage confirmationMessage = new ConfirmationMessage(message, BeMusicSeeker.Properties.Resources.BmsonMigrationWarningTitle, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
            base.Messenger.Raise(confirmationMessage);
            LogInitStage("bmson_preflight_prompt_close", "Initialize");
            return confirmationMessage.Response;
        }, delegate
        {
            LogInitStage("bmson_preflight_ensure_schema_start", "Initialize");
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(Settings.Default.LR2SongDBPath);
            if (preflightResult.RepairRequired)
            {
                LogInitStage("bmson_preflight_deferred_repair_start", "Initialize");
                gateway.EnsureBmsonSchema();
                LogInitStage("bmson_preflight_deferred_repair_done", "Initialize");
                LogInitStage("bmson_preflight_deferred_reinspect_start", "Initialize");
                preflightResult = bmsonMigrationPreflightService.Inspect(Settings.Default.LR2SongDBPath);
                LogInitStage("bmson_preflight_deferred_reinspect_done", "Initialize");
                if (preflightResult.RepairRequired)
                {
                    throw new InvalidOperationException("bmson app-owned schema repair did not converge.");
                }
            }
            BMSPlaylist.EnsureSchema(Settings.Default.LR2SongDBPath);
            gateway.EnsureBmsonSchema();
            LogInitStage("bmson_preflight_ensure_schema_done", "Initialize");
        }, delegate
        {
            System.Windows.Application.Current?.Shutdown();
        }, delegate
        {
            if (ResetBmsonColumnSettingsForMigrationIfNeeded(Settings.Default))
            {
                LogInitStage("bmson_column_settings_reset", "Initialize");
            }
        });
    }

    /// <summary>
    /// アプリケーション初期起動時に実行される、メイン初期化ルーチンです。非同期で呼び出されます。<br/>
    /// 設定の妥当性チェック、BMSデータベース (LR2SongDB形式など) との接続、BMSプレイヤーインスタンスの生成、
    /// およびコレクション更新をフックする各種イベントリスナーの登録を順次行います。
    /// </summary>
    public async void Initialize()
    {
        await _semaphore.WaitAsync();
        SetStartupUiInteractionBlocked(true);
        LogInitStage("start", "Initialize");
        initializationCompleted = false;
        _ = string.Empty;
        string text = Assembly.GetEntryAssembly().GetName().Version.ToString();
        WindowTitle = "BeMusicSeeker Unofficial Fork - " + text;
        if (!settingDialog.CheckValidation())
        {
            if (((App)System.Windows.Application.Current).firstStartup)
            {
                DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_settings, BeMusicSeeker.Properties.Resources.Information, MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
            }
            else
            {
                DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_settings_check, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            }
            _semaphore.Release();
            SetStartupUiInteractionBlocked(false);
            base.Messenger.Raise(new InteractionMessage("InitializationException"));
            return;
        }
        try
        {
            if (Settings.Default.OperationModeLR2DB && !EnsureBmsonMigrationApprovedForStartup())
            {
                _semaphore.Release();
                SetStartupUiInteractionBlocked(false);
                return;
            }
        }
        catch (Exception ex)
        {
            DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
            Logger currentClassLogger = LogManager.GetCurrentClassLogger();
            string text2 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text2 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            SetStartupUiInteractionBlocked(false);
            base.Messenger.Raise(new InteractionMessage("InitializationException"));
            return;
        }
        StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        try
        {
            if (Settings.Default.OperationModeLR2DB)
            {
                if (lr2config == null)
                {
                    lr2config = new LR2Config(Settings.Default.LR2ConfigXmlPath);
                }
                string playerId = lr2config.GetPlayerId();
                string text2 = Settings.Default.LR2RootPath + "\\LR2files\\Database\\Score\\" + playerId + ".db";
                if (playerId == null || !File.Exists(text2))
                {
                    text2 = null;
                }
                files = new BMSLibrary(Settings.Default.LR2SongDBPath, () => lr2config, text2);
                tables = new BMSPlaylist(Settings.Default.LR2SongDBPath, () => lr2config, text2, () => files.GetBMSScores());
                files.StartupBackgroundTaskScheduler = QueueStartupBackgroundTask;
                tables.StartupBackgroundTaskScheduler = QueueStartupBackgroundTask;
            }
            else
            {
                files = new BMSLibrary(Settings.Default.LR2SongDBPath);
                files.StartupBackgroundTaskScheduler = QueueStartupBackgroundTask;
                files.SearchTargets.Add(Settings.Default.BMSRootPath);
            }
            if (Settings.Default.UsePlayeruBMplay)
            {
                bmsPlayer = new uBMplay(Settings.Default.uBMplayPath);
            }
            else if (Settings.Default.UsePlayerBMIIDXView)
            {
                bmsPlayer = new BMIIDXView2015(Settings.Default.BMIIDXViewPath);
            }
            else if (Settings.Default.UsePlayerLR2body && File.Exists(settingDialog.LR2bodyPath) && lr2config != null)
            {
                bmsPlayer = new LR2body(settingDialog.LR2bodyPath, lr2config);
            }
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
            Logger currentClassLogger = LogManager.GetCurrentClassLogger();
            string text3 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text3 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            SetStartupUiInteractionBlocked(false);
            base.Messenger.Raise(new InteractionMessage("InitializationException"));
            return;
        }
        ColumnsSettingsBMSFilesView = Settings.Default.StandardColumnsSettings;
        listenerForBMSLibrary = new PropertyChangedEventListener(files);
        listenerForBMSPlaylist = new PropertyChangedEventListener(tables);
        listenerForBMSPlaylistBMSTablesCollection = new CollectionChangedEventListener(tables.BMSTables);
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFiles, delegate
        {
            InvalidatePlaylistLibraryIndexSnapshot("library_bmsfiles_changed");
            PruneRegularBmsLibraryRowCache(files?.BMSFiles);
            IncrementNormalLibrarySourceGeneration("library_bmsfiles_changed");
            ResetRegularDerivedViewCaches();
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                RefreshPlaylistSummaryIfVisible();
                return;
            }
            if (Enum.IsDefined(typeof(MaintenanceFilterType), (int)treeViewFilterTypeSelected))
            {
                MaintenanceFilterType type = (MaintenanceFilterType)treeViewFilterTypeSelected;
                if (type != MaintenanceFilterType.DuplicateFilter)
                {
                    ExecMaintenanceFilter(type);
                }
            }
            else
            {
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
            RefreshPlaylistSummaryIfVisible();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BmsonSongs, delegate
        {
            InvalidatePlaylistLibraryIndexSnapshot("library_bmsons_changed");
            BmsonLibraryRowCacheSyncResult syncResult = SyncBmsonLibraryRowCache(files?.BmsonSongs);
            if (syncResult.SortKeyChanged)
            {
                OnNormalLibrarySortKeyChanged("bmson_sort_key_changed");
            }
            RefreshPlaylistSummaryIfVisible();
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            if (ShouldRefreshPlaylistViewAfterBmsonSongsChanged(treeViewFilterTypeSelected))
            {
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
                return;
            }
            if (!ShouldIncludeBmsonLibraryRowsInMainView(treeViewFilterTypeSelected, treeViewFilterTypeSelected))
            {
                return;
            }
            if (syncResult.MembershipChanged)
            {
                IncrementNormalLibrarySourceGeneration("library_bmsons_membership_changed");
                ResetRegularDerivedViewCaches();
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreHydrationRequestedVersion, delegate
        {
            TrackStartupProgressScoreHydrationRequested(files.ScoreHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreHydrationCompletedVersion, delegate
        {
            TryCompleteStartupProgressScoreHydration(files.ScoreHydrationCompletedVersion);
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            RefreshLibraryMainViewForCurrentFilter();
            RefreshPlaylistSummaryIfVisible();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreSnapshotVersion, delegate
        {
            if (!IsPlaylistDetailViewActive)
            {
                return;
            }
            RequestPlaylistScoreSnapshotRefresh(files.ScoreSnapshotVersion);
            RefreshPlaylistSummaryIfVisible();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.RankingRefreshRequestedVersion, delegate
        {
            TrackStartupProgressRankingRefreshRequested(files.RankingRefreshRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.RankingRefreshCompletedVersion, delegate
        {
            TryCompleteStartupProgressRankingRefresh(files.RankingRefreshCompletedVersion);
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            RefreshLibraryMainViewForCurrentFilter();
            RefreshPlaylistSummaryIfVisible();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.MaintenanceDeferredRequestedVersion, delegate
        {
            TrackStartupProgressMaintenanceRequested(files.MaintenanceDeferredRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.MaintenanceDeferredCompletedVersion, delegate
        {
            TryCompleteStartupProgressMaintenance(files.MaintenanceDeferredCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillRequestedVersion, delegate
        {
            TrackStartupProgressChartDigestBackfillRequested(files.ChartDigestBackfillRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillCompletedVersion, delegate
        {
            TryCompleteStartupProgressChartDigestBackfill(files.ChartDigestBackfillCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillTotalCount, delegate
        {
            UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillProcessedCount, delegate
        {
            UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartDigestBackfillCurrentPath, delegate
        {
            UpdateStartupProgressChartDigestBackfillStatus(files.ChartDigestBackfillTotalCount, files.ChartDigestBackfillProcessedCount, files.ChartDigestBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillRequestedVersion, delegate
        {
            TrackStartupProgressChartInfoBackfillRequested(files.ChartInfoBackfillRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillCompletedVersion, delegate
        {
            TryCompleteStartupProgressChartInfoBackfill(files.ChartInfoBackfillCompletedVersion);
            RefreshChartInfoDependentViews();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillTotalCount, delegate
        {
            UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillProcessedCount, delegate
        {
            UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoBackfillCurrentPath, delegate
        {
            UpdateStartupProgressChartInfoBackfillStatus(files.ChartInfoBackfillTotalCount, files.ChartInfoBackfillProcessedCount, files.ChartInfoBackfillCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationRequestedVersion, delegate
        {
            TrackStartupProgressChartInfoHydrationRequested(files.ChartInfoHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationCompletedVersion, delegate
        {
            TryCompleteStartupProgressChartInfoHydration(files.ChartInfoHydrationCompletedVersion);
            RefreshChartInfoDependentViews();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationTotalCount, delegate
        {
            UpdateStartupProgressChartInfoHydrationStatus(files.ChartInfoHydrationTotalCount, files.ChartInfoHydrationAppliedCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoHydrationAppliedCount, delegate
        {
            UpdateStartupProgressChartInfoHydrationStatus(files.ChartInfoHydrationTotalCount, files.ChartInfoHydrationAppliedCount);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFilesNeedToBeFixed, delegate
        {
            if (treeViewFilterTypeSelected == viewUpdateMode.FileMissingFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFilesNeedToBeFixedIgnored, delegate
        {
            if (treeViewFilterTypeSelected == viewUpdateMode.FileMissingIgnoredFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFilesDuplicated, delegate
        {
            if (!TrySuppress(UiRefreshChannel.DuplicateTree))
            {
                RaisePropertyChanged(() => BMSFilesDuplicated);
            }
            if (treeViewFilterTypeSelected == viewUpdateMode.DuplicateFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                if (files.BMSFilesDuplicated == null)
                {
                    files.SearchBMSFilesDuplicated();
                    return;
                }
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFilesGarbled, delegate
        {
            if (treeViewFilterTypeSelected == viewUpdateMode.GarbledFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFilesGarbledFixed, delegate
        {
            if (treeViewFilterTypeSelected == viewUpdateMode.GarbleFixedFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFilesUnregistered, delegate
        {
            if (treeViewFilterTypeSelected == viewUpdateMode.UnregisteredFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFilesZeroNote, delegate
        {
            if (treeViewFilterTypeSelected == viewUpdateMode.ZeroNoteFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSPackagesInstalled, delegate
        {
            RebindBMSPackagesInstalledCollectionListener();
            HandleBMSPackagesInstalledCollectionChanged();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSPackagesPending, delegate
        {
            RebindBMSPackagesPendingCollectionListener();
            HandleBMSPackagesPendingCollectionChanged();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.PendingEstimateQueueStatusVersion, delegate
        {
            UpdatePendingEstimateQueueStatus(files.GetPendingEstimateQueueStatusSnapshot());
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallEstimationProgressVersion, delegate
        {
            UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        });
        RebindBMSPackagesInstalledCollectionListener();
        RebindBMSPackagesPendingCollectionListener();
        UpdatePendingEstimateQueueStatus(files.GetPendingEstimateQueueStatusSnapshot());
        UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        listenerForBMSLibrary.RegisterHandler(() => files.BMSParentFolderListCacheVersion, delegate
        {
            bmsParentFolderListViewInitialized = false;
            if (TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                return;
            }
            ScheduleDeferredLibraryFolderTreeRefresh();
        });
        listenerForBMSPlaylist.RegisterHandler(() => tables.BMSTables, delegate
        {
            if (TrySuppress(UiRefreshChannel.PlaylistTree))
            {
                RefreshPlaylistSummaryIfVisible();
                return;
            }
            RaisePropertyChanged(() => BMSTables);
            RefreshPlaylistSummaryIfVisible();
        });
        listenerForBMSPlaylistBMSTablesCollection.RegisterHandler(delegate
        {
            if (TrySuppress(UiRefreshChannel.PlaylistTree))
            {
                RefreshPlaylistSummaryIfVisible();
                return;
            }
            RaisePropertyChanged(() => BMSTables);
            RefreshPlaylistSummaryIfVisible();
        });
        listenerForBMSPlaylist.RegisterHandler(() => tables.PlaylistEntriesHydrationCompletedVersion, delegate
        {
            TryCompleteStartupProgressPlaylistEntriesHydration(tables.PlaylistEntriesHydrationCompletedVersion);
            ScheduleDeferredPlaylistReferenceApply("PlaylistEntriesHydration");
            InvalidatePlaylistSummaryRowsCache();
            RefreshPlaylistSummaryIfVisible();
            RefreshPlaylistDetailAfterReloadIfVisible();
        });
        listenerForBMSPlaylist.RegisterHandler(() => tables.PlaylistEntriesHydrationRequestedVersion, delegate
        {
            TrackStartupProgressPlaylistEntriesHydrationRequested(tables.PlaylistEntriesHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldInitializeBMSFiles, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFiles);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldInitializdBMSFilesHealthStatus, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializdBMSFilesHealthStatus);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldInitializeBMSFilesEncodingInfo, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFilesEncodingInfo);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldInitializeBMSFilesZeroNote, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFilesZeroNote);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldBMSFilesPendingInstall, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSFilesPendingInstall);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldBMSFilesDuplicated, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSFilesDuplicated);
        });
        listenerForBMSPlaylist.RegisterHandler(() => tables.IsWriteLockHeldBMSTables, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSTables);
        });
        listenerForBMSPlaylist.RegisterHandler(() => tables.IsWriteLockHeldBMSTablesInitializeMin, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSTablesInitializeMin);
        });
        listenerForBMSPlaylist.RegisterHandler(() => tables.IsPlaylistUpdating, delegate
        {
            RaisePropertyChanged(() => IsPlaylistUpdating);
        });
        base.Messenger.Raise(new InteractionMessage("InitializationSuccess"));
        if (Settings.Default.OperationModeLR2DB && Settings.Default.IsLR2BackupEnabled)
        {
            Backup.Target lR2BackupTarget = Settings.Default.LR2BackupTarget;
            List<string> bkPaths = new List<string>();
            string songDBPath = null;
            List<string> scoreDBPaths = new List<string>();
            if (lR2BackupTarget.HasFlag(Backup.Target.Config) && File.Exists(Settings.Default.LR2ConfigXmlPath))
            {
                bkPaths.Add(Settings.Default.LR2ConfigXmlPath);
            }
            if (lR2BackupTarget.HasFlag(Backup.Target.SongDB) && File.Exists(Settings.Default.LR2SongDBPath))
            {
                songDBPath = Settings.Default.LR2SongDBPath;
                bkPaths.Add(Settings.Default.LR2SongDBPath);
            }
            if (lR2BackupTarget.HasFlag(Backup.Target.ScoreDB) && Directory.Exists(Settings.Default.LR2RootPath + "\\LR2files\\Database\\Score"))
            {
                bkPaths.Add(Settings.Default.LR2RootPath + "\\LR2files\\Database\\Score");
                try
                {
                    scoreDBPaths = Directory.EnumerateFiles(Settings.Default.LR2RootPath + "\\LR2files\\Database\\Score", "*.db", System.IO.SearchOption.AllDirectories).ToList();
                }
                catch
                {
                    scoreDBPaths = new List<string>();
                }
            }
            if (bkPaths.Count > 0)
            {
                await Task.Run(delegate
                {
                    bool flag = false;
                    try
                    {
                        flag = Backup.SaveBackups(Settings.Default.LR2BackupPath, new TimeSpan(Settings.Default.LR2BackupSpan, 0, 0, 0), Settings.Default.LR2BackupNum, bkPaths);
                    }
                    catch (Exception ex2)
                    {
                        base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_backups + Environment.NewLine + ex2.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                    }
                    if (flag)
                    {
                        try
                        {
                            Backup.RebuildDatabase(songDBPath, scoreDBPaths);
                        }
                        catch
                        {
                        }
                    }
                }).Logging("Initialize");
            }
        }
        SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        Action taskAdd1 = delegate
        {
            tables.Initialize(reloadExtPlaylist: false, null, semaphore);
        };
        Action taskAdd2 = delegate
        {
            try
            {
                IsLoadingExternalCollectionBMSTables = true;
                List<BMSTableSimple> bMSTableInfo = BMSPlaylist.GetBMSTableInfo(Settings.Default.TableListURL);
                BMSTableSimpleCategorized bMSTableSimpleCategorized = new BMSTableSimpleCategorized();
                IEnumerable<string> source = bMSTableInfo.Select((BMSTableSimple bMSTableSimple) => bMSTableSimple.tag1).Distinct();
                bMSTableSimpleCategorized.Children = source.Select((string name) => new BMSTableSimpleCategorized
                {
                    name = name
                }).ToList();
                foreach (BMSTableSimpleCategorized child in bMSTableSimpleCategorized.Children)
                {
                    string tag1 = child.name;
                    child.Children = (from tt in bMSTableInfo
                                      where tt.tag1 == tag1 && string.IsNullOrWhiteSpace(tt.tag2)
                                      select new BMSTableSimpleCategorized(tt)).ToList();
                    List<BMSTableSimple> source2 = bMSTableInfo.Where((BMSTableSimple tt) => tt.tag1 == tag1 && !string.IsNullOrWhiteSpace(tt.tag2)).ToList();
                    foreach (string t2 in (from tt in source2.Select((BMSTableSimple tt) => tt.tag2).Distinct()
                                           orderby tt
                                           select tt).ToList())
                    {
                        child.Children.Add(new BMSTableSimpleCategorized
                        {
                            name = t2,
                            Children = (from tt in source2
                                        where tt.tag2 == t2
                                        select new BMSTableSimpleCategorized(tt) into tt
                                        orderby tt.name
                                        select tt).ToList()
                        });
                    }
                }
                BMSExternalTableListExt = bMSTableSimpleCategorized;
                IsLoadingExternalCollectionBMSTables = false;
            }
            catch
            {
            }
        };
        Thread.Yield();
        startupReadyInstallStopwatch = Stopwatch.StartNew();
        startupReadyOperableStopwatch = Stopwatch.StartNew();
        startupReadyDataLogged = false;
        startupReadyUiLogged = false;
        startupReadyDataReached = false;
        startupReadyUiReached = false;
        startupReadyOperableReached = false;
        BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree);
        try
        {
            await Task.Run(delegate
            {
                files.Initialize(new List<Action> { taskAdd1, taskAdd2 }, semaphore);
            }).Logging("Initialize");
            LogInitStage("files_initialize_done", "Initialize");
            TryLogStartupReadyData();
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            LogInitStage("ui_suppress_end_called", "Initialize");
        }
        if (((App)System.Windows.Application.Current).firstStartup)
        {
            ((App)System.Windows.Application.Current).firstStartup = false;
            DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_completed, BeMusicSeeker.Properties.Resources.Information, MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
        }
        initializationCompleted = true;
        SchedulePlaylistLibraryIndexPrewarm(GetPlaylistLibraryIndexVersion(), "initialize_completed");
        _semaphore.Release();
        LogInitStage("deferred_playlist_ref_waiting_for_playlist_entries_hydration", "Initialize");
        if (!Settings.Default.SkipInitPlaylistLoad)
        {
            StartDeferredExternalPlaylistSync("Initialize", fromReloadTables: false, CreatePlaylistReferenceReplaceUpdateCallback());
        }
    }

    public void SetuBMplayPanel()
    {
        listenerForBMSPlayer?.Dispose();
        listenerForBMSPlayer = new PropertyChangedEventListener(bmsPlayer);
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.Duration, delegate
        {
            CurrentlyPlayingDuration = bmsPlayer.Duration;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.CurrentTime, delegate
        {
            RaisePropertyChanged(() => CurrentlyPlayingTime);
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.StopTime, delegate
        {
            CurrentlyPlayingStopTime = bmsPlayer.StopTime;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.BmsDuration, delegate
        {
            CurrentlyPlayingBmsDuration = bmsPlayer.BmsDuration;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.MusicDuration, delegate
        {
            CurrentlyPlayingMusicDuration = bmsPlayer.MusicDuration;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.CurrentVoices, delegate
        {
            CurrentlyPlayingCurrentVoices = bmsPlayer.CurrentVoices;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.MaxVoices, delegate
        {
            CurrentlyPlayingMaxVoices = bmsPlayer.MaxVoices;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.NoteDensity, delegate
        {
            CurrentlyPlayingNoteDensity = bmsPlayer.NoteDensity;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.NoteDensityMax, delegate
        {
            CurrentlyPlayingNoteDensityMax = bmsPlayer.NoteDensityMax;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.Bpm, delegate
        {
            CurrentlyPlayingBpm = bmsPlayer.Bpm;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.MinBpm, delegate
        {
            CurrentlyPlayingMinBpm = bmsPlayer.MinBpm;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.MaxBpm, delegate
        {
            CurrentlyPlayingMaxBpm = bmsPlayer.MaxBpm;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.Total, delegate
        {
            CurrentlyPlayingTotal = bmsPlayer.Total;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.Combo, delegate
        {
            CurrentlyPlayingCombo = bmsPlayer.Combo;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.Notes, delegate
        {
            CurrentlyPlayingNotes = bmsPlayer.Notes;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.Measure, delegate
        {
            CurrentlyPlayingMeasure = bmsPlayer.Measure;
        });
        listenerForBMSPlayer.RegisterHandler(() => bmsPlayer.LastMeasure, delegate
        {
            CurrentlyPlayingLastMeasure = bmsPlayer.LastMeasure;
        });
    }

    public void SetuBMplayPanel(Panel panel)
    {
        if (bmsPlayer != null)
        {
            bmsPlayer.ParentHandle = panel.Handle;
        }
        SetuBMplayPanel();
    }

    public void CloseProcess()
    {
        Settings.Default.Save();
        if (bmsPlayer != null)
        {
            bmsPlayer.CloseProcess();
        }
        bool num = LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0));
        bool flag = LR2ScoreDBExtended.Lock(new TimeSpan(0, 1, 0));
        if (!num || !flag)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_error_close_timeout, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        TempDirectoryPublisher.RemoveAll();
    }

    private void playStartBMSfile(int indexBMSFilesView)
    {
        object row;
        BeMusicSeeker.Models.BMSFile bmsFile;
        try
        {
            row = BMSFilesView[indexBMSFilesView];
            bmsFile = GridRowResolver.GetOperationBmsFile(row);
        }
        catch
        {
            return;
        }
        if (PendingChartEntry.IsBmsonChartFile(bmsFile))
        {
            return;
        }
        nowPlayingBmsFilesViewIndex = indexBMSFilesView;
        if (bmsFile == null || string.IsNullOrWhiteSpace(bmsFile.path) || !File.Exists(bmsFile.path))
        {
            if (NowPlayingBMS != null)
            {
                NowPlayingBMS.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAYALL;
            }
            NowPlayingBMS = null;
            PlayNextBMSfile();
            return;
        }
        if (NowPlayingBMS != null)
        {
            NowPlayingBMS.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAYALL;
        }
        NowPlayingBMS = bmsFile;
        SelectedIndexBMSFilesView = indexBMSFilesView;
        base.Messenger.Raise(new InteractionMessage("CallbackPlayStartBMSfile"));
        if (!string.IsNullOrWhiteSpace(bmsFile.instl_dst) && Directory.Exists(bmsFile.instl_dst))
        {
            BMSPackage bMSPackage = BMSPackagesPending.Where((BMSPackage pkg) => pkg.BMSFiles.Contains(bmsFile)).FirstOrDefault();
            if (bMSPackage == null)
            {
                if (Settings.Default.UsePlayerLR2body && Settings.Default.OperationModeLR2DB)
                {
                    ConfirmationMessage confirmationMessage = new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_warn_play_temp_install, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.YesNo, "ConfirmationDialog");
                    base.Messenger.Raise(confirmationMessage);
                    if (confirmationMessage.Response == false)
                    {
                        return;
                    }
                }
                bMSPackage = new BMSPackage(bmsFile)
                {
                    delete_parent = false
                };
            }
            string fileName = Path.GetFileName(bmsFile.path);
            while (File.Exists(Path.Combine(bmsFile.instl_dst, Path.GetFileName(bmsFile.path))) || Directory.Exists(Path.Combine(bmsFile.instl_dst, Path.GetFileName(bmsFile.path))))
            {
                string text = Path.GetFileNameWithoutExtension(bmsFile.path) + "_" + Path.GetExtension(bmsFile.path);
                try
                {
                    FileSystem.RenameFile(bmsFile.path, text);
                }
                catch
                {
                    return;
                }
                bmsFile.path = Path.Combine(Path.GetDirectoryName(bmsFile.path), text);
            }
            List<string> list = new List<string>();
            if (Directory.Exists(bMSPackage.path))
            {
                string[] extensionsPermitted = BeMusicSeeker.Models.BMSFile.bmsExtensions.Concat(BeMusicSeeker.Models.BMSFile.wavExtensions).Concat(BeMusicSeeker.Models.BMSFile.bgaImageExtensions).ToArray();
                list = (from f in Directory.EnumerateFiles(bMSPackage.path, "*", System.IO.SearchOption.TopDirectoryOnly)
                        where extensionsPermitted.Any((string e) => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                        select f).ToList();
            }
            else
            {
                list.Add(bmsFile.path);
            }
            lock (lockCopyFile)
            {
                if (string.IsNullOrWhiteSpace(bmsFile.instl_dst))
                {
                    return;
                }
                using (new temporarilyCopyFiles(list, bmsFile.instl_dst, 2000))
                {
                    string bmsFilePath = Path.Combine(bmsFile.instl_dst, Path.GetFileName(bmsFile.path));
                    bmsFile.status |= BeMusicSeeker.Models.BMSFile.BMSFileStatus.LOADING;
                    try
                    {
                        bmsPlayer.PlayStart(bmsFilePath, PlayNextBMSfile);
                        bmsFile.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.LOADING;
                        bmsFile.status |= BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAY;
                    }
                    catch (Exception ex)
                    {
                        base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_play + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                        PlayEndBMSFile();
                        return;
                    }
                }
            }
            if (!string.Equals(fileName, Path.GetFileName(bmsFile.path), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    FileSystem.RenameFile(bmsFile.path, fileName);
                }
                catch
                {
                    return;
                }
                bmsFile.path = Path.Combine(Path.GetDirectoryName(bmsFile.path), fileName);
            }
        }
        else
        {
            bmsFile.status |= BeMusicSeeker.Models.BMSFile.BMSFileStatus.LOADING;
            try
            {
                bmsPlayer.PlayStart(bmsFile.path, PlayNextBMSfile);
                bmsFile.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.LOADING;
                bmsFile.status |= BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAY;
            }
            catch (InvalidDataException value)
            {
                NLogWrapper.TraceLogger?.Warn(value);
                if (Settings.Default.RepeatPlayMode && (Settings.Default.SinglePlayMode || indexBMSFilesView == 0))
                {
                    PlayEndBMSFile();
                    return;
                }
                PlayNextBMSfile();
            }
            catch (Exception ex2)
            {
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_play + Environment.NewLine + ex2.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                PlayEndBMSFile();
                return;
            }
        }
        base.Messenger.Raise(new InteractionMessage("CallbackPlayStartedBMSfile"));
    }

    /// <summary>
    /// BMSプレイヤーを起動し、現在選択されているBMSファイル（または指定ファイル）のプレビュー再生を開始します。
    /// 内蔵プレーヤーの場合は状態管理を反映し、外部プレーヤー (uBMPlay, LR2body等) の場合はプロセス起動を中継します。
    /// </summary>
    /// <param name="forceNewPlay">強制的に最初から再生し直す場合は <c>true</c>。一時停止の再開等の場合は <c>false</c>。</param>
    public void PlayStartBMSfile(bool forceNewPlay = true)
    {
        lock (lockThis)
        {
            if (bmsPlayer == null)
            {
                return;
            }
            if (!forceNewPlay && NowPlayingBMS != null)
            {
                if (NowPlayingBMS.status.HasFlag(BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAY))
                {
                    NowPlayingBMS.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAYALL;
                    NowPlayingBMS.status |= BeMusicSeeker.Models.BMSFile.BMSFileStatus.PAUSE;
                }
                else if (NowPlayingBMS.status.HasFlag(BeMusicSeeker.Models.BMSFile.BMSFileStatus.PAUSE))
                {
                    NowPlayingBMS.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAYALL;
                    NowPlayingBMS.status |= BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAY;
                }
                bmsPlayer.PausePlayingBMSfileToggle();
            }
            else
            {
                PlayEndBMSFile();
        if (SelectedIndexBMSFilesView >= 0 && SelectedIndexBMSFilesView < BMSFilesView.Count)
        {
            playStartBMSfile(SelectedIndexBMSFilesView);
        }
            }
        }
    }

    public void PlayNextBMSfile(object sender = null, EventArgs e = null)
    {
        lock (lockThis)
        {
            if (bmsPlayer == null)
            {
                return;
            }
            int num = nowPlayingBmsFilesViewIndex;
            if (num < 0 || num >= BMSFilesView.Count)
            {
                PlayEndBMSFile();
                return;
            }
            if (sender != null && Settings.Default.SinglePlayMode)
            {
                if (!Settings.Default.RepeatPlayMode)
                {
                    PlayEndBMSFile();
                    return;
                }
            }
            else
            {
                string text = (!string.IsNullOrWhiteSpace(NowPlayingBMS?.path) && File.Exists(NowPlayingBMS.path)) ? Path.GetDirectoryName(NowPlayingBMS.path) : num.ToString();
                num++;
                if (Settings.Default.RepeatPlayMode && num == BMSFilesView.Count)
                {
                    num = 0;
                }
                while (Settings.Default.FolderSkipPlayMode && num != BMSFilesView.Count)
                {
                    object candidateRow = BMSFilesView[num];
                    BeMusicSeeker.Models.BMSFile bMSFile = GridRowResolver.GetOperationBmsFile(candidateRow);
                    if (num == nowPlayingBmsFilesViewIndex)
                    {
                        break;
                    }
                    string text2 = (bMSFile != null && !PendingChartEntry.IsBmsonChartFile(bMSFile) && !string.IsNullOrWhiteSpace(bMSFile.path) && File.Exists(bMSFile.path)) ? Path.GetDirectoryName(bMSFile.path) : num.ToString();
                    if (!string.IsNullOrWhiteSpace(text2) && text != text2)
                    {
                        break;
                    }
                    text = text2;
                    num++;
                    if (Settings.Default.RepeatPlayMode && num == BMSFilesView.Count)
                    {
                        num = 0;
                    }
                }
            }
            if (num < BMSFilesView.Count)
            {
                PlayEndBMSFile();
                playStartBMSfile(num);
            }
            else if (sender != null)
            {
                PlayEndBMSFile();
            }
        }
    }

    public void PlayPreviousBMSfile(object sender = null, EventArgs e = null)
    {
        lock (lockThis)
        {
            if (bmsPlayer == null)
            {
                return;
            }
            int num = nowPlayingBmsFilesViewIndex;
            if (num < 0 || num >= BMSFilesView.Count)
            {
                PlayEndBMSFile();
                return;
            }
            if (sender != null && Settings.Default.SinglePlayMode)
            {
                if (!Settings.Default.RepeatPlayMode)
                {
                    PlayEndBMSFile();
                    return;
                }
            }
            else
            {
                string text = (!string.IsNullOrWhiteSpace(NowPlayingBMS?.path) && File.Exists(NowPlayingBMS.path)) ? Path.GetDirectoryName(NowPlayingBMS.path) : num.ToString();
                num--;
                if (Settings.Default.RepeatPlayMode && num == -1)
                {
                    num = BMSFilesView.Count - 1;
                }
                while (Settings.Default.FolderSkipPlayMode && num != -1)
                {
                    object candidateRow = BMSFilesView[num];
                    BeMusicSeeker.Models.BMSFile bMSFile = GridRowResolver.GetOperationBmsFile(candidateRow);
                    if (num == nowPlayingBmsFilesViewIndex)
                    {
                        break;
                    }
                    string text2 = (bMSFile != null && !PendingChartEntry.IsBmsonChartFile(bMSFile) && !string.IsNullOrWhiteSpace(bMSFile.path) && File.Exists(bMSFile.path)) ? Path.GetDirectoryName(bMSFile.path) : num.ToString();
                    if (!string.IsNullOrWhiteSpace(text2) && text != text2)
                    {
                        break;
                    }
                    text = text2;
                    num--;
                    if (Settings.Default.RepeatPlayMode && num == -1)
                    {
                        num = BMSFilesView.Count - 1;
                    }
                }
            }
            if (num >= 0)
            {
                PlayEndBMSFile();
                playStartBMSfile(num);
            }
            else if (sender != null)
            {
                PlayEndBMSFile();
            }
        }
    }

    public void PlayEndBMSFile(bool closeProcess = false)
    {
        lock (lockThis)
        {
            if (closeProcess && bmsPlayer != null)
            {
                bmsPlayer.CloseProcess();
            }
            if (NowPlayingBMS != null)
            {
                NowPlayingBMS.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAYALL;
                NowPlayingBMS = null;
            }
            nowPlayingBmsFilesViewIndex = -1;
        }
    }

    private void stopPlayingBMSFile(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        if (!string.IsNullOrWhiteSpace(NowPlayingBMS?.path))
        {
            string protecDir = Path.GetDirectoryName(NowPlayingBMS.path);
            if (bmsFiles.Where((BeMusicSeeker.Models.BMSFile s) => !string.IsNullOrWhiteSpace(NowPlayingBMS?.path)).Any((BeMusicSeeker.Models.BMSFile s) => protecDir.StartsWith(Path.GetDirectoryName(s.path), StringComparison.OrdinalIgnoreCase)))
            {
                PlayEndBMSFile(closeProcess: true);
            }
        }
    }

    internal void RestartPlayingBMSfileStart()
    {
        lock (lockThis)
        {
            if (bmsPlayer != null)
            {
                bmsPlayer.RestartPlayingBMSfile();
            }
        }
    }

    internal void FastForwardPlayingBMSfileStart()
    {
        if (bmsPlayer != null)
        {
            if (NowPlayingBMS != null)
            {
                NowPlayingBMS.status |= BeMusicSeeker.Models.BMSFile.BMSFileStatus.FORWARD;
            }
            bmsPlayer.FastForwardPlayingBMSfileStart();
        }
    }

    internal void FastForwardPlayingBMSfileEnd()
    {
        if (bmsPlayer != null)
        {
            if (NowPlayingBMS != null)
            {
                NowPlayingBMS.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.FORWARD;
            }
            bmsPlayer.FastForwardPlayingBMSfileEnd();
        }
    }

    internal void FastBackwardPlayingBMSfileStart()
    {
        if (bmsPlayer != null)
        {
            if (NowPlayingBMS != null)
            {
                NowPlayingBMS.status |= BeMusicSeeker.Models.BMSFile.BMSFileStatus.BACKWARD;
            }
            bmsPlayer.FastBackwardPlayingBMSfileStart();
        }
    }

    internal void FastBackwardPlayingBMSfileEnd()
    {
        if (bmsPlayer != null)
        {
            if (NowPlayingBMS != null)
            {
                NowPlayingBMS.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.BACKWARD;
            }
            bmsPlayer.FastBackwardPlayingBMSfileEnd();
        }
    }

    internal void uBMplayShowInfo()
    {
        if (bmsPlayer != null)
        {
            bmsPlayer.ShowInfo();
        }
    }

    internal void uBMplayShowEffect()
    {
        if (bmsPlayer != null)
        {
            bmsPlayer.ShowEffect();
        }
    }

    internal void uBMplayChangePlayside()
    {
        if (bmsPlayer != null)
        {
            bmsPlayer.ChangePlayside();
        }
    }

    internal void uBMplayIncreaseHighSpeed()
    {
        if (bmsPlayer != null)
        {
            bmsPlayer.IncreaseHighSpeed();
        }
    }

    internal void uBMplayDecreaseHighSpeed()
    {
        if (bmsPlayer != null)
        {
            bmsPlayer.DecreaseHighSpeed();
        }
    }

    internal void uBMplayVolumeChanged()
    {
        if (bmsPlayer != null)
        {
            bmsPlayer.VolumeChanged();
        }
    }

    /// <summary>
    /// メインビュー更新共通の callback 発火と性能ログを確定します。
    /// </summary>
    private void FinalizeMainViewBuild(
        Stopwatch viewBuildStopwatch,
        viewUpdateMode mode,
        viewUpdateMode requestedMode,
        object parameter,
        long folderStageMs,
        long keywordStageMs,
        long modeStageMs,
        long sortStageMs,
        bool sortReuse,
        string sortProfile,
        int folderCount,
        int keywordCount,
        int modeCount,
        int viewCount,
        long columnStageMs,
        long callbackStageMs)
    {
        string sortColumn = SortParameters?.ColumnsName ?? "(default_title)";
        string sortDirection = SortParameters?.Direction.ToString() ?? "Ascending";
        string parameterType = parameter?.GetType().Name ?? "(null)";
        bool fastSortEnabled = true;
        bool isPlaylistDetailForLog = IsPlaylistViewMode(mode) || IsPlaylistViewMode(treeViewFilterTypeSelected);
        long mainViewBuildRequestId = Interlocked.Increment(ref mainViewBuildRequestIdSeed);
        long mainViewBuildEndTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref lastMainViewBuildRequestId, mainViewBuildRequestId);
        Interlocked.Exchange(ref lastMainViewBuildEndTimestamp, mainViewBuildEndTimestamp);
        Volatile.Write(ref lastMainViewBuildThreadId, Thread.CurrentThread.ManagedThreadId);
        Volatile.Write(ref lastMainViewBuildMode, (int)mode);
        if (isPlaylistDetailForLog)
        {
            Interlocked.Exchange(ref lastPlaylistDetailBuildCompletedTimestamp, mainViewBuildEndTimestamp);
            Interlocked.Exchange(ref lastPlaylistDetailBuildElapsedMs, viewBuildStopwatch.ElapsedMilliseconds);
        }
        LogMainViewBuild("main_view_build mode=" + mode + " requestedMode=" + requestedMode + " parameterType=" + parameterType + " folderMs=" + folderStageMs + " keywordMs=" + keywordStageMs + " modeMs=" + modeStageMs + " sortMs=" + sortStageMs + " sortReuse=" + sortReuse + " sortProfile=" + sortProfile + " sortEngine=fast fastSortEnabled=" + fastSortEnabled + " isPlaylistDetailView=" + isPlaylistDetailForLog + " columnMs=" + columnStageMs + " callbackMs=" + callbackStageMs + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds + " folderCount=" + folderCount + " keywordCount=" + keywordCount + " modeCount=" + modeCount + " viewCount=" + viewCount + " sortColumn=" + sortColumn + " sortDirection=" + sortDirection);
    }

    /// <summary>
    /// プレイリスト詳細ビューの source snapshot を構築します。
    /// </summary>
    private List<PlaylistDetailSourceRow> BuildPlaylistSourceRows(BMSTable bmsTable, string folderName, bool onlyNotOwned, PlaylistLibraryIndexSnapshot libraryIndexSnapshot, int requestVersion, CancellationToken cancellationToken, ref string cancellationStage, out int scoreUpdateTargetCount, out long entryResolveMs, out long scoreProbeMs, out long sourceMaterializeMs, out PlaylistScoreProbeMetrics scoreProbeMetrics)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        scoreUpdateTargetCount = 0;
        entryResolveMs = 0L;
        scoreProbeMs = 0L;
        sourceMaterializeMs = 0L;
        scoreProbeMetrics = new PlaylistScoreProbeMetrics();
        if (bmsTable == null)
        {
            return new List<PlaylistDetailSourceRow>();
        }
        Stopwatch entryHydrationStopwatch = Stopwatch.StartNew();
        tables?.EnsurePlaylistEntriesLoaded(bmsTable, "BuildPlaylistSourceRows");
        entryHydrationStopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, BeMusicSeeker.Models.BMSFile> filesByHash = libraryIndexSnapshot?.FilesByHash ?? new Dictionary<string, BeMusicSeeker.Models.BMSFile>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, BeMusicSeeker.Models.BMSFile> filesBySha256 = libraryIndexSnapshot?.FilesBySha256 ?? new Dictionary<string, BeMusicSeeker.Models.BMSFile>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LR2SongDBExtended.bmson_song> bmsonByMd5 = libraryIndexSnapshot?.BmsonByMd5 ?? new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LR2SongDBExtended.bmson_song> bmsonBySha256 = libraryIndexSnapshot?.BmsonBySha256 ?? new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase);
        BeMusicSeeker.Models.BMSLibrary.ScoreSnapshot scoreSnapshot = files?.GetScoreSnapshotForDiagnostics();
        IReadOnlyDictionary<string, BeMusicSeeker.Models.BMSScore> scoresByHash = scoreSnapshot?.ScoresByHash ?? new Dictionary<string, BeMusicSeeker.Models.BMSScore>(StringComparer.OrdinalIgnoreCase);
        cancellationStage = "hash_index";
        cancellationToken.ThrowIfCancellationRequested();
        List<(BMSTableEntry entry, BeMusicSeeker.Models.BMSFile realFile, LR2SongDBExtended.bmson_song resolvedBmson, BeMusicSeeker.Models.BMSScore scoreSnapshot)> resolvedEntries = new List<(BMSTableEntry, BeMusicSeeker.Models.BMSFile, LR2SongDBExtended.bmson_song, BeMusicSeeker.Models.BMSScore)>();
        cancellationStage = "entry_resolve";
        foreach (BMSTableEntry entry in bmsTable.GetEntriesExceptDummy())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.is_removed || (folderName != null && entry.folder != folderName))
            {
                continue;
            }
            BeMusicSeeker.Models.BMSFile realFile = null;
            if (!string.IsNullOrWhiteSpace(entry.md5))
            {
                filesByHash.TryGetValue(entry.md5, out realFile);
            }
            if (realFile == null && !string.IsNullOrWhiteSpace(entry.sha256))
            {
                filesBySha256.TryGetValue(entry.sha256, out realFile);
            }
            LR2SongDBExtended.bmson_song resolvedBmson = realFile == null ? ResolveBmsonForPlaylistEntry(entry, bmsonByMd5, bmsonBySha256) : null;
            scoresByHash.TryGetValue(entry.md5 ?? string.Empty, out BeMusicSeeker.Models.BMSScore scoreSnapshotForRow);
            bool isOwned = (realFile != null && !string.IsNullOrWhiteSpace(realFile.path)) || (resolvedBmson != null && !string.IsNullOrWhiteSpace(resolvedBmson.path));
            if (onlyNotOwned && isOwned)
            {
                continue;
            }
            resolvedEntries.Add((entry, realFile, resolvedBmson, scoreSnapshotForRow));
        }
        cancellationToken.ThrowIfCancellationRequested();
        List<(BMSTableEntry entry, BeMusicSeeker.Models.BMSFile realFile, LR2SongDBExtended.bmson_song resolvedBmson, LR2SongDBExtended.chart_info entryChartInfo, PlaylistScoreProbeBmsFile scoreProbe, BeMusicSeeker.Models.BMSScore scoreSnapshot)> preparedEntries = new List<(BMSTableEntry, BeMusicSeeker.Models.BMSFile, LR2SongDBExtended.bmson_song, LR2SongDBExtended.chart_info, PlaylistScoreProbeBmsFile, BeMusicSeeker.Models.BMSScore)>(resolvedEntries.Count);
        Stopwatch chartInfoLookupStopwatch = Stopwatch.StartNew();
        int missingChartInfoResolveTargets = 0;
        int chartInfoResolvedCount = 0;
        int chartInfoIndexVersion = files?.ChartInfoIndexVersion ?? 0;
        foreach ((BMSTableEntry entry, BeMusicSeeker.Models.BMSFile realFile, LR2SongDBExtended.bmson_song resolvedBmson, BeMusicSeeker.Models.BMSScore scoreSnapshotForRow) in resolvedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LR2SongDBExtended.chart_info entryChartInfo = null;
            if (realFile == null && resolvedBmson == null)
            {
                missingChartInfoResolveTargets++;
                entryChartInfo = files?.ResolveChartInfo(entry.sha256, entry.md5);
                if (entryChartInfo != null)
                {
                    chartInfoResolvedCount++;
                }
            }
            PlaylistScoreProbeBmsFile scoreProbe = null;
            if (ShouldCreatePlaylistScoreProbe(realFile, resolvedBmson))
            {
                scoreProbe = new PlaylistScoreProbeBmsFile();
                scoreProbe.ApplyEntrySnapshot(entry, BmsonSongParser.ResolvePlaylistMode(resolvedBmson?.mode_hint));
                scoreUpdateTargetCount++;
            }
            preparedEntries.Add((entry, realFile, resolvedBmson, entryChartInfo, scoreProbe, scoreSnapshotForRow));
        }
        chartInfoLookupStopwatch.Stop();
        LogPlaylistWorker("playlist_chart_info_index_resolve entries=" + resolvedEntries.Count + " targets=" + missingChartInfoResolveTargets + " found=" + chartInfoResolvedCount + " version=" + chartInfoIndexVersion + " elapsedMs=" + chartInfoLookupStopwatch.ElapsedMilliseconds);
        entryResolveMs = stopwatch.ElapsedMilliseconds;

        cancellationToken.ThrowIfCancellationRequested();
        if (scoreUpdateTargetCount > 0)
        {
            cancellationStage = "score_probe_prepare";
            cancellationToken.ThrowIfCancellationRequested();
            List<(BMSTableEntry, PlaylistScoreProbeBmsFile)> scoreProbeRows = preparedEntries.Where((row) => row.scoreProbe != null).Select((row) => (row.entry, row.scoreProbe)).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            cancellationStage = "score_probe";
            scoreProbeMetrics = SetPlaylistScoreProbeSnapshots(scoreProbeRows, requestVersion, cancellationToken);
        }
        scoreProbeMs = stopwatch.ElapsedMilliseconds - entryResolveMs;
        List<PlaylistDetailSourceRow> playlistRows = new List<PlaylistDetailSourceRow>(preparedEntries.Count);
        cancellationStage = "source_row_materialize";
        foreach ((BMSTableEntry entry, BeMusicSeeker.Models.BMSFile realFile, LR2SongDBExtended.bmson_song resolvedBmson, LR2SongDBExtended.chart_info entryChartInfo, PlaylistScoreProbeBmsFile scoreProbe, BeMusicSeeker.Models.BMSScore scoreSnapshotForRow) in preparedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            playlistRows.Add(new PlaylistDetailSourceRow(entry, realFile, resolvedBmson, scoreProbe, scoreSnapshotForRow, entryChartInfo));
        }
        sourceMaterializeMs = stopwatch.ElapsedMilliseconds - entryResolveMs - scoreProbeMs;
        return playlistRows;
    }

    /// <summary>
    /// playlist 未所持行の score probe へ score snapshot を chunk 単位で適用します。
    /// </summary>
    /// <param name="scoreProbeRows">score snapshot を付与する対象行。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    private PlaylistScoreProbeMetrics SetPlaylistScoreProbeSnapshots(IReadOnlyList<(BMSTableEntry entry, PlaylistScoreProbeBmsFile probe)> scoreProbeRows, int requestVersion, CancellationToken cancellationToken)
    {
        PlaylistScoreProbeMetrics summary = new PlaylistScoreProbeMetrics
        {
            TargetCount = scoreProbeRows?.Count ?? 0
        };
        if (scoreProbeRows == null || scoreProbeRows.Count == 0)
        {
            return summary;
        }
        if (files == null)
        {
            return summary;
        }
        const int chunkSize = 1024;
        for (int offset = 0; offset < scoreProbeRows.Count; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(chunkSize, scoreProbeRows.Count - offset);
            List<BeMusicSeeker.Models.BMSFile> chunk = new List<BeMusicSeeker.Models.BMSFile>(count);
            for (int index = 0; index < count; index++)
            {
                chunk.Add(scoreProbeRows[offset + index].probe);
            }
            try
            {
                BeMusicSeeker.Models.BMSLibrary.BmsScoreApplyMetrics chunkMetrics = files.SetBMSScoreWithMetrics(chunk);
                summary.ChunkCount++;
                summary.WaitInitializedMinMs += chunkMetrics.WaitInitializedMinMs;
                summary.WaitBmsFilesReadMs += chunkMetrics.WaitBmsFilesReadMs;
                summary.WaitScoresWriteMs += chunkMetrics.WaitScoresWriteMs;
                summary.WaitScoreSnapshotReadMs += chunkMetrics.WaitScoreSnapshotReadMs;
                summary.ApplyKnownScoresMs += chunkMetrics.ApplyKnownScoresMs;
                summary.MatchedScoreCount += chunkMetrics.MatchedScoreCount;
                summary.TotalMs += chunkMetrics.TotalMs;
                if (chunkMetrics.TotalMs >= PlaylistScoreProbeChunkSlowLogThresholdMs)
                {
                    LogPlaylistWorker("playlist_score_probe_chunk requestVersion=" + requestVersion + " offset=" + offset + " count=" + count + " total=" + scoreProbeRows.Count + " waitInitializedMinMs=" + chunkMetrics.WaitInitializedMinMs + " waitBmsFilesReadMs=" + chunkMetrics.WaitBmsFilesReadMs + " waitScoresWriteMs=" + chunkMetrics.WaitScoresWriteMs + " waitScoreSnapshotReadMs=" + chunkMetrics.WaitScoreSnapshotReadMs + " applyKnownScoresMs=" + chunkMetrics.ApplyKnownScoresMs + " matchedScoreCount=" + chunkMetrics.MatchedScoreCount + " totalMs=" + chunkMetrics.TotalMs + " thresholdMs=" + PlaylistScoreProbeChunkSlowLogThresholdMs);
                }
            }
            catch (OperationCanceledException)
            {
                LogPlaylistWorker("playlist_score_probe_chunk_cancelled requestVersion=" + requestVersion + " offset=" + offset + " count=" + count + " total=" + scoreProbeRows.Count);
                throw;
            }
        }
        return summary;
    }

    /// <summary>
    /// source row から表示用 lightweight row を生成します。
    /// </summary>
    private static PlaylistDetailRow CreatePlaylistViewRowClone(PlaylistDetailSourceRow row)
    {
        return row?.CreateViewRow();
    }

    /// <summary>
    /// source snapshot から表示用 row 一覧を生成します。
    /// </summary>
    private static List<PlaylistDetailRow> CreatePlaylistViewRowsFromSource(IEnumerable<PlaylistDetailSourceRow> rows)
    {
        if (rows == null)
        {
            return new List<PlaylistDetailRow>();
        }
        List<PlaylistDetailRow> clones = new List<PlaylistDetailRow>();
        foreach (PlaylistDetailSourceRow row in rows)
        {
            PlaylistDetailRow playlistRow = CreatePlaylistViewRowClone(row);
            if (playlistRow != null)
            {
                clones.Add(playlistRow);
            }
        }
        return clones;
    }

    /// <summary>
    /// 現在のモード/パラメータからプレイリスト選択情報を解決します。
    /// </summary>
    /// <param name="mode">解決対象モード。</param>
    /// <param name="parameter">更新パラメータ。</param>
    /// <param name="bmsTable">解決されたプレイリスト。</param>
    /// <param name="folderName">解決されたプレイリストフォルダ名。</param>
    /// <param name="filterType">解決された filter 種別。</param>
    /// <returns>プレイリスト選択情報を解決できた場合は <see langword="true"/>。</returns>
    private bool TryResolvePlaylistSelection(viewUpdateMode mode, object parameter, out BMSTable bmsTable, out string folderName, out PlaylistFilterType filterType)
    {
        bmsTable = null;
        folderName = null;
        filterType = PlaylistFilterType.PlaylistFilter;
        if (mode == viewUpdateMode.PlaylistFilterSelected)
        {
            if (parameter == null)
            {
                return true;
            }
            if (parameter is Tuple<BMSTable, string> tuple)
            {
                bmsTable = tuple.Item1;
                folderName = tuple.Item2;
                return true;
            }
            return false;
        }
        if (mode == viewUpdateMode.PlaylistNotOwnedFilterSelected)
        {
            filterType = PlaylistFilterType.PlaylistNotOwnedFilterSelected;
            bmsTable = parameter as BMSTable;
            return parameter == null || bmsTable != null;
        }
        if (treeViewFilterTypeSelected == viewUpdateMode.PlaylistFilterSelected)
        {
            if (treeViewFilterParameterSelected == null)
            {
                return true;
            }
            if (treeViewFilterParameterSelected is Tuple<BMSTable, string> treeTuple)
            {
                bmsTable = treeTuple.Item1;
                folderName = treeTuple.Item2;
                return true;
            }
            return false;
        }
        if (treeViewFilterTypeSelected == viewUpdateMode.PlaylistNotOwnedFilterSelected)
        {
            filterType = PlaylistFilterType.PlaylistNotOwnedFilterSelected;
            bmsTable = treeViewFilterParameterSelected as BMSTable;
            return treeViewFilterParameterSelected == null || bmsTable != null;
        }
        return false;
    }

    /// <summary>
    /// プレイリスト source snapshot から keyword/mode/sort を適用し、表示用行集合を返します。
    /// </summary>
    /// <param name="sourceRows">keyword/mode/sort 適用前の source snapshot。</param>
    /// <param name="keywordFilter">現在の keyword filter。</param>
    /// <param name="modeFilter">現在の mode filter。</param>
    /// <param name="sortParameters">現在の sort 条件。</param>
    /// <param name="sortProfile">利用された sort profile。</param>
    /// <param name="keywordCount">keyword 適用後件数。</param>
    /// <param name="modeCount">mode 適用後件数。</param>
    /// <param name="keywordStageMs">keyword 適用時間。</param>
    /// <param name="modeStageMs">mode 適用時間。</param>
    /// <param name="sortStageMs">sort 適用時間。</param>
    /// <returns>表示用行集合。</returns>
    internal static List<PlaylistDetailRow> ApplyPlaylistViewFromSource(IReadOnlyList<PlaylistDetailSourceRow> sourceRows, string keywordFilter, ModeFilterType modeFilter, cSortParameters sortParameters, out string sortProfile, out int keywordCount, out int modeCount, out long keywordStageMs, out long modeStageMs, out long sortStageMs, out long viewMaterializeMs)
    {
        Stopwatch stageStopwatch = Stopwatch.StartNew();
        IReadOnlyList<PlaylistDetailSourceRow> effectiveSourceRows = sourceRows ?? Array.Empty<PlaylistDetailSourceRow>();
        List<PlaylistDetailSourceRow> keywordRows;
        if (!string.IsNullOrWhiteSpace(keywordFilter))
        {
            GridKeywordSearchQuery query = GridKeywordSearchQuery.Parse(keywordFilter);
            keywordRows = effectiveSourceRows.AsParallel().Where(delegate (PlaylistDetailSourceRow row)
            {
                return query.MatchesPlaylistDetail(row);
            }).ToList();
        }
        else
        {
            keywordRows = (effectiveSourceRows as List<PlaylistDetailSourceRow>) ?? effectiveSourceRows.ToList();
        }
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;
        keywordCount = keywordRows.Count;

        stageStopwatch.Restart();
        List<PlaylistDetailSourceRow> modeRows;
        if (modeFilter != ModeFilterType.All)
        {
            List<int?> modeFlag = new List<int?> { null };
            if ((modeFilter & ModeFilterType._5KEYS) == ModeFilterType._5KEYS)
            {
                modeFlag.Add(5);
            }
            if ((modeFilter & ModeFilterType._7KEYS) == ModeFilterType._7KEYS)
            {
                modeFlag.Add(7);
            }
            if ((modeFilter & ModeFilterType._9KEYS) == ModeFilterType._9KEYS)
            {
                modeFlag.Add(9);
            }
            if ((modeFilter & ModeFilterType._10KEYS) == ModeFilterType._10KEYS)
            {
                modeFlag.Add(10);
            }
            if ((modeFilter & ModeFilterType._14KEYS) == ModeFilterType._14KEYS)
            {
                modeFlag.Add(14);
            }
            modeRows = keywordRows.Where((PlaylistDetailSourceRow file) => modeFlag.Contains(file.mode)).ToList();
        }
        else
        {
            modeRows = keywordRows;
        }
        modeStageMs = stageStopwatch.ElapsedMilliseconds;
        modeCount = modeRows.Count;

        stageStopwatch.Restart();
        List<PlaylistDetailSourceRow> sortedSourceRows = PlaylistDetailSortEngine.Sort(modeRows, sortParameters, out sortProfile);
        sortStageMs = stageStopwatch.ElapsedMilliseconds;
        stageStopwatch.Restart();
        List<PlaylistDetailRow> viewRows = CreatePlaylistViewRowsFromSource(sortedSourceRows);
        viewMaterializeMs = stageStopwatch.ElapsedMilliseconds;
        return viewRows;
    }

    /// <summary>
    /// 現在保持している playlist source snapshot から view を再計算します。
    /// </summary>
    private List<PlaylistDetailRow> ApplyPlaylistViewFromCurrentSource(viewUpdateMode mode, out int sourceCount, out int keywordCount, out int modeCount, out long keywordStageMs, out long modeStageMs, out long sortStageMs, out string sortProfile)
    {
        List<PlaylistDetailSourceRow> sourceRows;
        int currentViewRowsAlive;
        long sourceGenerationId;
        lock (playlistViewState.SyncRoot)
        {
            sourceRows = playlistViewState.SourceRows;
            sourceGenerationId = playlistViewState.SourceGenerationId;
            currentViewRowsAlive = CountPlaylistDetailRows(playlistViewState.CurrentViewRows);
        }
        sourceRows ??= new List<PlaylistDetailSourceRow>();
        sourceCount = sourceRows.Count;
        LogPlaylistViewApply("started mode=" + mode + " sourceGenerationId=" + sourceGenerationId + " sourceCount=" + sourceCount + " playlistSourceRowCount=" + CountPlaylistSourceRows(sourceRows) + " playlistViewRowCount=" + currentViewRowsAlive);
        List<PlaylistDetailRow> finalRows = ApplyPlaylistViewFromSource(sourceRows, KeywordFilter, ModeFilter, SortParameters, out sortProfile, out keywordCount, out modeCount, out keywordStageMs, out modeStageMs, out sortStageMs, out long _);
        LogPlaylistViewApply("completed mode=" + mode + " sourceGenerationId=" + sourceGenerationId + " sourceCount=" + sourceCount + " keywordCount=" + keywordCount + " modeCount=" + modeCount + " viewCount=" + finalRows.Count + " sortProfile=" + sortProfile + " playlistSourceRowCount=" + CountPlaylistSourceRows(sourceRows) + " playlistViewRowCount=" + CountPlaylistDetailRows(finalRows));
        return finalRows;
    }

    /// <summary>
    /// プレイリスト source snapshot を再構築してから view を反映します。
    /// </summary>
    private bool RebuildPlaylistSource(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return false;
        }
        int requestVersion = request.RequestVersion;
        viewUpdateMode mode = request.Mode;
        viewUpdateMode requestedMode = request.RequestedMode;
        object parameter = request.Parameter;
        Stopwatch viewBuildStopwatch = Stopwatch.StartNew();
        long stageStartMs = 0L;
        long folderStageMs = 0L;
        long keywordStageMs = 0L;
        long modeStageMs = 0L;
        long sortStageMs = 0L;
        long columnStageMs = 0L;
        long callbackStageMs = 0L;
        int folderCount = 0;
        int keywordCount = 0;
        int modeCount = 0;
        int viewCount = 0;
        int sourceCount = 0;
        int scoreUpdateTargetCount = 0;
        int disposedSourceRowsCount = 0;
        long libraryIndexMs = 0L;
        long entryResolveMs = 0L;
        long scoreProbeMs = 0L;
        long sourceMaterializeMs = 0L;
        long viewMaterializeMs = 0L;
        PlaylistScoreProbeMetrics scoreProbeMetrics = new PlaylistScoreProbeMetrics();
        string sortProfile = "not_sorted";
        List<PlaylistDetailSourceRow> sourceRows = null;
        List<PlaylistDetailSourceRow> previousSourceRows = null;
        List<PlaylistDetailRow> finalRows = null;
        bool gateEntered = false;
        string cancellationStage = "before_start";
        try
        {
            playlistViewState.BuildGate.Wait(cancellationToken);
            gateEntered = true;
            if (!IsLatestPlaylistSourceBuildRequest(requestVersion))
            {
                LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=stale_before_start mode=" + mode + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "stale_before_start"));
                return true;
            }
            if (!TryResolvePlaylistSelection(mode, parameter, out BMSTable bmsTable, out string folderName, out PlaylistFilterType filterType))
            {
                return false;
            }
            bool onlyNotOwned = filterType == PlaylistFilterType.PlaylistNotOwnedFilterSelected;
            LogPlaylistSourceBuild("started version=" + requestVersion + " mode=" + mode + " parameterType=" + (parameter?.GetType().Name ?? "(null)") + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
            cancellationStage = "hash_index";
            stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            PlaylistLibraryIndexSnapshot libraryIndexSnapshot = GetOrCreatePlaylistLibraryIndexSnapshot(cancellationToken, out string libraryIndexAccess, out long libraryIndexBuildMs);
            libraryIndexMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
            stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            sourceRows = BuildPlaylistSourceRows(bmsTable, folderName, onlyNotOwned, libraryIndexSnapshot, requestVersion, cancellationToken, ref cancellationStage, out scoreUpdateTargetCount, out entryResolveMs, out scoreProbeMs, out sourceMaterializeMs, out scoreProbeMetrics);
            folderStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
            folderCount = sourceRows.Count;
            sourceCount = folderCount;
            LogPlaylistWorker("playlist_score_probe_summary requestVersion=" + requestVersion + " targetCount=" + scoreProbeMetrics.TargetCount + " chunkCount=" + scoreProbeMetrics.ChunkCount + " waitInitializedMinMs=" + scoreProbeMetrics.WaitInitializedMinMs + " waitBmsFilesReadMs=" + scoreProbeMetrics.WaitBmsFilesReadMs + " waitScoresWriteMs=" + scoreProbeMetrics.WaitScoresWriteMs + " waitScoreSnapshotReadMs=" + scoreProbeMetrics.WaitScoreSnapshotReadMs + " applyKnownScoresMs=" + scoreProbeMetrics.ApplyKnownScoresMs + " matchedScoreCount=" + scoreProbeMetrics.MatchedScoreCount + " totalMs=" + scoreProbeMetrics.TotalMs);
            if (scoreProbeMetrics.TotalMs >= PlaylistScoreProbeSlowLogThresholdMs)
            {
                long totalWaitMs = scoreProbeMetrics.WaitInitializedMinMs + scoreProbeMetrics.WaitBmsFilesReadMs + scoreProbeMetrics.WaitScoresWriteMs + scoreProbeMetrics.WaitScoreSnapshotReadMs;
                LogPlaylistWorker("playlist_lock_wait_detail requestVersion=" + requestVersion + " waitInitializedMinMs=" + scoreProbeMetrics.WaitInitializedMinMs + " waitBmsFilesReadMs=" + scoreProbeMetrics.WaitBmsFilesReadMs + " waitScoresWriteMs=" + scoreProbeMetrics.WaitScoresWriteMs + " waitScoreSnapshotReadMs=" + scoreProbeMetrics.WaitScoreSnapshotReadMs + " totalWaitMs=" + totalWaitMs + " applyKnownScoresMs=" + scoreProbeMetrics.ApplyKnownScoresMs + " matchedScoreCount=" + scoreProbeMetrics.MatchedScoreCount + " totalMs=" + scoreProbeMetrics.TotalMs + " thresholdMs=" + PlaylistScoreProbeSlowLogThresholdMs);
            }
            if (cancellationToken.IsCancellationRequested || !IsLatestPlaylistSourceBuildRequest(requestVersion))
            {
                LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=after_build mode=" + mode + " sourceCount=" + sourceCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
                return true;
            }
            cancellationStage = "view_apply";
            LogPlaylistViewApply("started mode=" + mode + " sourceCount=" + sourceCount + " playlistSourceRowCount=" + CountPlaylistSourceRows(sourceRows) + " playlistViewRowCount=" + CountPlaylistDetailRows(playlistViewState.CurrentViewRows));
            finalRows = ApplyPlaylistViewFromSource(sourceRows, KeywordFilter, ModeFilter, SortParameters, out sortProfile, out keywordCount, out modeCount, out keywordStageMs, out modeStageMs, out sortStageMs, out viewMaterializeMs);
            viewCount = finalRows.Count;
            LogPlaylistViewApply("completed mode=" + mode + " sourceCount=" + sourceCount + " keywordCount=" + keywordCount + " modeCount=" + modeCount + " viewCount=" + viewCount + " sortProfile=" + sortProfile + " playlistSourceRowCount=" + CountPlaylistSourceRows(sourceRows) + " playlistViewRowCount=" + CountPlaylistDetailRows(finalRows));
            if (cancellationToken.IsCancellationRequested || !IsLatestPlaylistSourceBuildRequest(requestVersion))
            {
                LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=after_apply mode=" + mode + " sourceCount=" + sourceCount + " viewCount=" + viewCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
                return true;
            }
            cancellationStage = "ui_apply";
            previousSourceRows = ReplacePlaylistSourceRows(sourceRows, bmsTable, folderName, filterType, request.Identity);
            sourceRows = null;
            SelectedIndexBMSFilesView = -1;
            base.Messenger.Raise(new InteractionMessage("PrepareMainTableSwap"));
            stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            loadColumnSetting(ResolvePlaylistColumnSettingMode(filterType));
            columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
            stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            callbackStageMs = 0L;
            ReplacePlaylistViewRows(finalRows, request.Identity);
            SetChartRowsView(finalRows);
            TryMarkPlaylistOpenBuildCompleted(request, viewCount);
            finalRows = null;
            disposedSourceRowsCount = CountPlaylistSourceRows(previousSourceRows);
            previousSourceRows = null;
            FinalizeMainViewBuild(viewBuildStopwatch, mode, requestedMode, parameter, folderStageMs, keywordStageMs, modeStageMs, sortStageMs, sortReuse: false, sortProfile, folderCount, keywordCount, modeCount, viewCount, columnStageMs, callbackStageMs);
            LogPlaylistSourceBuild("completed version=" + requestVersion + " mode=" + mode + " sourceCount=" + sourceCount + " viewCount=" + viewCount + " disposedSourceRows=" + disposedSourceRowsCount + " scoreTargets=" + scoreUpdateTargetCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown") + " libraryIndexMs=" + libraryIndexMs + " libraryIndexAccess=" + libraryIndexAccess + " libraryIndexBuildMs=" + libraryIndexBuildMs + " entryResolveMs=" + entryResolveMs + " scoreProbeMs=" + scoreProbeMs + " scoreProbeChunkCount=" + scoreProbeMetrics.ChunkCount + " scoreProbeWaitInitializedMinMs=" + scoreProbeMetrics.WaitInitializedMinMs + " scoreProbeWaitBmsFilesReadMs=" + scoreProbeMetrics.WaitBmsFilesReadMs + " scoreProbeWaitScoresWriteMs=" + scoreProbeMetrics.WaitScoresWriteMs + " scoreProbeWaitScoreSnapshotReadMs=" + scoreProbeMetrics.WaitScoreSnapshotReadMs + " scoreProbeApplyKnownScoresMs=" + scoreProbeMetrics.ApplyKnownScoresMs + " scoreProbeMatchedScoreCount=" + scoreProbeMetrics.MatchedScoreCount + " sourceMaterializeMs=" + sourceMaterializeMs + " viewMaterializeMs=" + viewMaterializeMs + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException)
        {
            LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=" + cancellationStage + " mode=" + mode + " sourceCount=" + sourceCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
            return true;
        }
        finally
        {
            if (sourceRows != null)
            {
                LogPlaylistSourceBuild("discarded version=" + requestVersion + " mode=" + mode + " discardedRows=" + sourceCount);
            }
            if (finalRows != null)
            {
                DisposePlaylistViewRows(finalRows);
            }
            if (gateEntered)
            {
                playlistViewState.BuildGate.Release();
            }
        }
    }

    /// <summary>
    /// 既存の playlist source snapshot から view のみ再適用します。
    /// </summary>
    private bool ApplyPlaylistViewWithoutSourceRebuild(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return false;
        }
        viewUpdateMode mode = request.Mode;
        viewUpdateMode requestedMode = request.RequestedMode;
        object parameter = request.Parameter;
        Stopwatch viewBuildStopwatch = Stopwatch.StartNew();
        long keywordStageMs = 0L;
        long modeStageMs = 0L;
        long sortStageMs = 0L;
        long columnStageMs = 0L;
        long callbackStageMs = 0L;
        int sourceCount = 0;
        int keywordCount = 0;
        int modeCount = 0;
        int viewCount = 0;
        string sortProfile = "not_sorted";
        cancellationToken.ThrowIfCancellationRequested();
        List<PlaylistDetailRow> finalRows = ApplyPlaylistViewFromCurrentSource(mode, out sourceCount, out keywordCount, out modeCount, out keywordStageMs, out modeStageMs, out sortStageMs, out sortProfile);
        viewCount = finalRows.Count;
        if (cancellationToken.IsCancellationRequested || !IsLatestPlaylistSourceBuildRequest(request.RequestVersion))
        {
            return true;
        }
        SelectedIndexBMSFilesView = -1;
        base.Messenger.Raise(new InteractionMessage("PrepareMainTableSwap"));
        long stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        loadColumnSetting(ResolvePlaylistColumnSettingMode(request.Identity.FilterType));
        columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        callbackStageMs = 0L;
        ReplacePlaylistViewRows(finalRows, request.Identity);
        SetChartRowsView(finalRows);
        TryMarkPlaylistOpenBuildCompleted(request, viewCount);
        FinalizeMainViewBuild(viewBuildStopwatch, treeViewFilterTypeSelected, requestedMode, parameter, folderStageMs: 0L, keywordStageMs, modeStageMs, sortStageMs, sortReuse: false, sortProfile, folderCount: sourceCount, keywordCount, modeCount, viewCount, columnStageMs, callbackStageMs);
        return true;
    }

    private bool TryPatchPlaylistSourceChartInfoIndex(PlaylistBuildRequest request, CancellationToken cancellationToken, out int sourceCount, out int dependencyCount, out int patchedCount, out long elapsedMs)
    {
        sourceCount = 0;
        dependencyCount = 0;
        patchedCount = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<PlaylistDetailSourceRow> sourceRows;
        lock (playlistViewState.SyncRoot)
        {
            sourceRows = playlistViewState.SourceRows;
        }
        if (sourceRows == null)
        {
            elapsedMs = stopwatch.ElapsedMilliseconds;
            return false;
        }
        sourceCount = sourceRows.Count;
        foreach (PlaylistDetailSourceRow row in sourceRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row == null || !row.HasEntryChartInfoDependency)
            {
                continue;
            }
            dependencyCount++;
            LR2SongDBExtended.chart_info resolved = files?.ResolveChartInfo(row.sha256, row.hash);
            if (AreSameChartInfoIdentity(row.EntryChartInfo, resolved))
            {
                continue;
            }
            if (row.SetEntryChartInfo(resolved))
            {
                patchedCount++;
            }
        }
        lock (playlistViewState.SyncRoot)
        {
            playlistViewState.LastBuiltChartInfoIndexVersion = request.Identity.ChartInfoIndexVersion;
            playlistViewState.CurrentSourceIdentity = request.Identity.SourceIdentity;
            playlistViewState.SourceGenerationId++;
        }
        stopwatch.Stop();
        elapsedMs = stopwatch.ElapsedMilliseconds;
        return true;
    }

    private static bool AreSameChartInfoIdentity(LR2SongDBExtended.chart_info existing, LR2SongDBExtended.chart_info incoming)
    {
        if (ReferenceEquals(existing, incoming))
        {
            return true;
        }
        if (existing == null || incoming == null)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(existing.sha256) || string.IsNullOrWhiteSpace(incoming.sha256))
        {
            return false;
        }
        if (existing.parser_version <= 0 || incoming.parser_version <= 0)
        {
            return false;
        }
        if (existing.updated_at == default(DateTime) || incoming.updated_at == default(DateTime))
        {
            return false;
        }
        return string.Equals(existing.sha256, incoming.sha256, StringComparison.OrdinalIgnoreCase)
            && existing.parser_version == incoming.parser_version
            && existing.updated_at == incoming.updated_at;
    }

    /// <summary>
    /// プレイリスト詳細ビューを source rebuild または source 再利用で更新します。
    /// </summary>
    private bool TryBuildPlaylistViewAndApply(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return false;
        }
        viewUpdateMode mode = request.Mode;
        viewUpdateMode requestedMode = request.RequestedMode;
        object parameter = request.Parameter;
        List<PlaylistDetailSourceRow> sourceRows;
        BMSTable currentTable;
        string currentFolderName;
        PlaylistFilterType currentFilterType;
        long lastBuiltLibraryIndexVersion;
        long lastBuiltPlaylistRevision;
        int lastBuiltScoreSnapshotVersion;
        int lastBuiltChartInfoIndexVersion;
        PlaylistSourceIdentity? currentSourceIdentity;
        PlaylistRequestIdentity? currentViewIdentity;
        lock (playlistViewState.SyncRoot)
        {
            sourceRows = playlistViewState.SourceRows;
            currentTable = playlistViewState.CurrentTable;
            currentFolderName = playlistViewState.CurrentFolderName;
            currentFilterType = playlistViewState.CurrentFilterType;
            lastBuiltLibraryIndexVersion = playlistViewState.LastBuiltLibraryIndexVersion;
            lastBuiltPlaylistRevision = playlistViewState.LastBuiltPlaylistRevision;
            lastBuiltScoreSnapshotVersion = playlistViewState.LastBuiltScoreSnapshotVersion;
            lastBuiltChartInfoIndexVersion = playlistViewState.LastBuiltChartInfoIndexVersion;
            currentSourceIdentity = playlistViewState.CurrentSourceIdentity;
            currentViewIdentity = playlistViewState.CurrentViewIdentity;
        }
        bool hasResolvedPlaylistSource = currentSourceIdentity.HasValue || currentTable != null || currentFilterType == PlaylistFilterType.PlaylistNotOwnedFilterSelected;
        bool selectionChanged = !currentSourceIdentity.HasValue || currentTable != request.Identity.Table || !string.Equals(NormalizePlaylistFolderName(currentFolderName), request.Identity.FolderName, StringComparison.Ordinal) || currentFilterType != request.Identity.FilterType || currentSourceIdentity.Value.HasResolvedSelection != request.Identity.HasResolvedSelection;
        bool libraryIndexInvalidated = lastBuiltLibraryIndexVersion != request.Identity.LibraryIndexVersion;
        bool playlistRevisionInvalidated = lastBuiltPlaylistRevision != request.Identity.PlaylistRevision;
        bool scoreSnapshotInvalidated = lastBuiltScoreSnapshotVersion != request.Identity.ScoreSnapshotVersion;
        bool chartInfoIndexInvalidated = lastBuiltChartInfoIndexVersion != request.Identity.ChartInfoIndexVersion;
        bool sourceIdentityInvalidatedExceptChartInfo = !currentSourceIdentity.HasValue || !currentSourceIdentity.Value.EqualsIgnoringChartInfoIndex(request.Identity.SourceIdentity);
        bool presentationIdentityChanged = !currentViewIdentity.HasValue || !currentViewIdentity.Value.PresentationIdentity.Equals(request.Identity.PresentationIdentity);
        bool sourceMissing = sourceRows == null || (sourceRows.Count == 0 && !hasResolvedPlaylistSource);
        bool sourceInvalidated = sourceIdentityInvalidatedExceptChartInfo || libraryIndexInvalidated || playlistRevisionInvalidated || scoreSnapshotInvalidated;
        bool chartInfoOnlyInvalidated = chartInfoIndexInvalidated && !selectionChanged && !sourceInvalidated && !sourceMissing;
        if (chartInfoOnlyInvalidated && TryPatchPlaylistSourceChartInfoIndex(request, cancellationToken, out int patchedSourceCount, out int chartInfoDependencyCount, out int chartInfoPatchedCount, out long chartInfoPatchMs))
        {
            LogPlaylistViewApply("chart_info_patch requestVersion=" + request.RequestVersion + " sourceCount=" + patchedSourceCount + " dependencyCount=" + chartInfoDependencyCount + " patchedCount=" + chartInfoPatchedCount + " chartInfoIndexVersion=" + request.Identity.ChartInfoIndexVersion + " elapsedMs=" + chartInfoPatchMs);
            return ApplyPlaylistViewWithoutSourceRebuild(request, cancellationToken);
        }
        sourceInvalidated = sourceInvalidated || (chartInfoIndexInvalidated && !chartInfoOnlyInvalidated);
        bool requiresSourceRebuild = selectionChanged || sourceInvalidated || sourceMissing;
        if (requiresSourceRebuild)
        {
            request.LastBuiltScoreSnapshotVersion = lastBuiltScoreSnapshotVersion;
            request.SourceInvalidationReason = DeterminePlaylistSourceInvalidationReason(selectionChanged, libraryIndexInvalidated, playlistRevisionInvalidated, scoreSnapshotInvalidated, chartInfoIndexInvalidated, sourceMissing);
            return RebuildPlaylistSource(request, cancellationToken);
        }
        LogPlaylistViewApply("source_reuse requestVersion=" + request.RequestVersion + " presentationChanged=" + presentationIdentityChanged + " sourceIdentityChanged=false keyword=\"" + (request.Identity.KeywordFilter ?? string.Empty).Replace("\"", "\"\"") + "\" modeFilter=" + request.Identity.ModeFilter + " sortColumn=" + (request.Identity.SortColumnName ?? string.Empty) + " sortDirection=" + request.Identity.SortDirection);
        return ApplyPlaylistViewWithoutSourceRebuild(request, cancellationToken);
    }

    /// <summary>
    /// 指定された更新モードとパラメータに基づいて、メインのBMS一覧表示用コレクションを生成・更新します。
    /// ツリーでのフォルダ選択、プレイリストや難易度表の適用、Missingファイル等の保守フィルタ、およびキーワードやキーモードでの絞り込み等を行います。<br/>
    /// このメソッドの実行には、規模に応じて時間がかかるため内部でタイマー計測し遅延を制御・ロギングする機構が含まれています。
    /// </summary>
    /// <param name="mode">更新の契機（どのフィルタや要素が変更されたかを示す更新モード）。</param>
    /// <param name="parameter">選択されたプレイリスト（BMSTable）やフォルダ名などの追加パラメータ、無い場合は null。</param>
    private void makeBMSFilesView(viewUpdateMode mode, object parameter = null)
    {
        Stopwatch viewBuildStopwatch = Stopwatch.StartNew();
        long stageStartMs = 0L;
        long folderStageMs = 0L;
        long keywordStageMs = 0L;
        long modeStageMs = 0L;
        long sortStageMs = 0L;
        long columnStageMs = 0L;
        long callbackStageMs = 0L;
        bool sortReuse = false;
        string sortProfile = "not_sorted";
        int folderCount = 0;
        int keywordCount = 0;
        int modeCount = 0;
        int viewCount = 0;
        viewUpdateMode requestedMode = mode;
        if (mode == viewUpdateMode.TreeViewFilterNotChanged)
        {
            mode = treeViewFilterTypeSelected;
            parameter = treeViewFilterParameterSelected;
        }
        else if (mode < viewUpdateMode.KeywordFilterUpdated)
        {
            if (mode == viewUpdateMode.DuplicateFilterSelected)
            {
                parameter = NormalizeDuplicateViewParameter(parameter);
            }
            treeViewFilterTypeSelected = mode;
            treeViewFilterParameterSelected = parameter;
            UpdateKeywordSearchPresentation();
        }
        if (BMSFiles == null)
        {
            return;
        }
        UpdateBmsFilesViewBindingMode(IsPlaylistTreeActive(mode, treeViewFilterTypeSelected));
        if (IsPlaylistTreeActive(mode, treeViewFilterTypeSelected))
        {
            RegisterPlaylistSourceBuildRequest(mode, requestedMode, parameter);
            return;
        }
        bool includeBmsonRows = ShouldIncludeBmsonLibraryRowsInMainView(mode, treeViewFilterTypeSelected);
        if (includeBmsonRows)
        {
            BmsonLibraryRowCacheSyncResult bmsonSyncResult = SyncBmsonLibraryRowCache(files?.BmsonSongs);
            if (bmsonSyncResult.SortKeyChanged)
            {
                OnNormalLibrarySortKeyChanged("bmson_sort_key_changed");
            }
            if (bmsonSyncResult.MembershipChanged)
            {
                IncrementNormalLibrarySourceGeneration("bmson_membership_changed");
            }
        }
        ClearPlaylistSourceRows();
        if (ShouldRebuildRegularFolderStage(mode, ChartRowsFolderView, ChartRowsKeywordFilterView, ChartRowsModeFilterView, treeViewFilterTypeSelected))
        {
            mode = treeViewFilterTypeSelected;
            parameter = treeViewFilterParameterSelected;
        }
        switch (mode)
        {
            case viewUpdateMode.FolderFilterSelected:
                LibraryRowCacheBuildStats folderRowCacheStats = CreateRegularRowCacheBuildStats();
                ChartRowsFolderView = BuildStandardLibraryRowsForView(BMSFiles, includeBmsonRows ? GetBmsonLibraryRowsSnapshot() : Array.Empty<LibraryChartRow>(), FolderFilter, file => regularBmsLibraryRowCache.GetOrCreate(file, folderRowCacheStats), folderRowCacheStats, out LibraryRowsBuildMetrics folderMetrics);
                LogMainViewFolderDetail(mode, folderMetrics);
                break;
            case viewUpdateMode.FullScanAllChartsFilterSelected:
                LibraryRowCacheBuildStats fullScanRowCacheStats = CreateRegularRowCacheBuildStats();
                ChartRowsFolderView = BuildStandardLibraryRowsForView(BMSFiles, includeBmsonRows ? GetBmsonLibraryRowsSnapshot() : Array.Empty<LibraryChartRow>(), null, file => regularBmsLibraryRowCache.GetOrCreate(file, fullScanRowCacheStats), fullScanRowCacheStats, out LibraryRowsBuildMetrics fullScanMetrics);
                LogMainViewFolderDetail(mode, fullScanMetrics);
                break;
            case viewUpdateMode.FileMissingFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(BMSFilesToBeFixed);
                break;
            case viewUpdateMode.FileMissingIgnoredFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(BMSFilesToBeFixedIgnored);
                break;
            case viewUpdateMode.DuplicateFilterSelected:
                if (BMSFilesDuplicated == null)
                {
                    ChartRowsFolderView = null;
                    break;
                }
                parameter = NormalizeDuplicateViewParameter(parameter);
                if (parameter != null)
                {
                    if (parameter is DuplicateViewContext duplicateContext)
                    {
                        if (duplicateContext.Kind == DuplicateViewContextKind.GroupHeader)
                        {
                            DuplicateGroup duplicateGroup = BMSFilesDuplicated.FirstOrDefault((DuplicateGroup group) => string.Equals(group.Header, duplicateContext.Value, StringComparison.Ordinal));
                            ChartRowsFolderView = ToLibraryChartRows((duplicateGroup != null) ? duplicateGroup.Files : BMSFilesDuplicated.SelectMany((DuplicateGroup g) => g.Files));
                        }
                        else
                        {
                            string dirname2 = duplicateContext.Value;
                            RetryHelper.RetryIfError(delegate
                            {
                                ChartRowsFolderView = ToLibraryChartRows(from f in BMSFilesDuplicated.SelectMany((DuplicateGroup g) => g.Files)
                                                                        where f.path.StartsWith(dirname2 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                                                        select f);
                            }, delegate (Exception ex)
                            {
                                ExceptionDispatchInfo.Capture(ex).Throw();
                            }, delegate
                            {
                                Thread.Sleep(100);
                            }, 100u);
                        }
                    }
                    else if (parameter is List<BeMusicSeeker.Models.BMSFile>)
                    {
                        ChartRowsFolderView = ToLibraryChartRows(parameter as List<BeMusicSeeker.Models.BMSFile>);
                    }
                    else if (parameter is DuplicateGroup)
                    {
                        ChartRowsFolderView = ToLibraryChartRows((parameter as DuplicateGroup).Files);
                    }
                    else
                    {
                        if (!(parameter is string))
                        {
                            break;
                        }
                        string dirname = parameter as string;
                        RetryHelper.RetryIfError(delegate
                        {
                            ChartRowsFolderView = ToLibraryChartRows(from f in BMSFilesDuplicated.SelectMany((DuplicateGroup g) => g.Files)
                                                                    where f.path.StartsWith(dirname + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                                                    select f);
                        }, delegate (Exception ex)
                        {
                            ExceptionDispatchInfo.Capture(ex).Throw();
                        }, delegate
                        {
                            Thread.Sleep(100);
                        }, 100u);
                    }
                    break;
                }
                RetryHelper.RetryIfError(delegate
                {
                    ChartRowsFolderView = ToLibraryChartRows(BMSFilesDuplicated.SelectMany((DuplicateGroup g) => g.Files));
                }, delegate (Exception ex)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 100u);
                break;
            case viewUpdateMode.GarbledFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(BMSFilesGarbled);
                break;
            case viewUpdateMode.GarbleFixedFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(BMSFilesGarbleFixed);
                break;
            case viewUpdateMode.UnregisteredFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(BMSFilesUnregistered);
                break;
            case viewUpdateMode.ZeroNoteFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(BMSFilesZeroNote);
                break;
            case viewUpdateMode.NewlyInstalledFolderSelected:
                if (BMSPackagesInstalled == null)
                {
                    ChartRowsFolderView = null;
                    break;
                }
                if (parameter != null && parameter is BMSPackage)
                {
                    ChartRowsFolderView = ToLibraryChartRows((parameter as BMSPackage).BMSFiles);
                    break;
                }
                RetryHelper.RetryIfError(delegate
                {
                    ChartRowsFolderView = ToLibraryChartRows(BMSPackagesInstalled.SelectMany(delegate (BMSPackage p)
                    {
                        try
                        {
                            if (p != null)
                            {
                                return p.BMSFiles;
                            }
                            return Enumerable.Empty<BeMusicSeeker.Models.BMSFile>();
                        }
                        catch
                        {
                            return Enumerable.Empty<BeMusicSeeker.Models.BMSFile>();
                        }
                    }));
                }, delegate (Exception ex)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 100u);
                break;
            case viewUpdateMode.PendingInstallFolderSelected:
                if (BMSPackagesPending == null)
                {
                    ChartRowsFolderView = null;
                    break;
                }
                if (parameter != null && parameter is BMSPackage)
                {
                    ChartRowsFolderView = ToLibraryChartRows((parameter as BMSPackage).BMSFiles);
                    break;
                }
                RetryHelper.RetryIfError(delegate
                {
                    ChartRowsFolderView = ToLibraryChartRows(BMSPackagesPending.SelectMany(delegate (BMSPackage p)
                    {
                        try
                        {
                            if (p != null)
                            {
                                return p.BMSFiles;
                            }
                            return Enumerable.Empty<BeMusicSeeker.Models.BMSFile>();
                        }
                        catch
                        {
                            return Enumerable.Empty<BeMusicSeeker.Models.BMSFile>();
                        }
                    }));
                }, delegate (Exception ex)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 100u);
                break;
        }
        ChartRowsFolderView = ((ChartRowsFolderView == null) ? new List<LibraryChartRow>() : ChartRowsFolderView.ToList());
        folderStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        folderCount = ChartRowsFolderView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        if (mode <= viewUpdateMode.KeywordFilterUpdated)
        {
            if (!string.IsNullOrWhiteSpace(KeywordFilter))
            {
                ChartRowsKeywordFilterView = new List<LibraryChartRow>();
                GridKeywordSearchQuery query = GridKeywordSearchQuery.Parse(KeywordFilter);
                ChartRowsKeywordFilterView = from r in ChartRowsFolderView.AsParallel()
                                            where query.MatchesLibraryChartRow(r)
                                            select r;
            }
            else
            {
                ChartRowsKeywordFilterView = ChartRowsFolderView;
            }
        }
        ChartRowsKeywordFilterView = ((ChartRowsKeywordFilterView == null) ? new List<LibraryChartRow>() : ChartRowsKeywordFilterView.ToList());
        keywordStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        keywordCount = ChartRowsKeywordFilterView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        if (mode <= viewUpdateMode.ModeFilterUpdated)
        {
            if (ModeFilter != ModeFilterType.All)
            {
                List<int?> modeFlag = new List<int?> { null };
                if ((ModeFilter & ModeFilterType._5KEYS) == ModeFilterType._5KEYS)
                {
                    modeFlag.Add(5);
                }
                if ((ModeFilter & ModeFilterType._7KEYS) == ModeFilterType._7KEYS)
                {
                    modeFlag.Add(7);
                }
                if ((ModeFilter & ModeFilterType._9KEYS) == ModeFilterType._9KEYS)
                {
                    modeFlag.Add(9);
                }
                if ((ModeFilter & ModeFilterType._10KEYS) == ModeFilterType._10KEYS)
                {
                    modeFlag.Add(10);
                }
                if ((ModeFilter & ModeFilterType._14KEYS) == ModeFilterType._14KEYS)
                {
                    modeFlag.Add(14);
                }
                ChartRowsModeFilterView = ChartRowsKeywordFilterView.Where((LibraryChartRow f) => modeFlag.Contains(f.mode));
            }
            else
            {
                ChartRowsModeFilterView = ChartRowsKeywordFilterView;
            }
        }
        ChartRowsModeFilterView = ((ChartRowsModeFilterView == null) ? new List<LibraryChartRow>() : ChartRowsModeFilterView.ToList());
        modeStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        modeCount = ChartRowsModeFilterView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        IList nextRowsView;
        if (mode <= viewUpdateMode.SortUpdated)
        {
            bool useLegacySortForMainView = false;
            bool isPlaylistDetailView = mode == viewUpdateMode.PlaylistFilterSelected || mode == viewUpdateMode.PlaylistNotOwnedFilterSelected || treeViewFilterTypeSelected == viewUpdateMode.PlaylistFilterSelected || treeViewFilterTypeSelected == viewUpdateMode.PlaylistNotOwnedFilterSelected;
            string columnName = nameof(BeMusicSeeker.Models.BMSFile.Title);
            ListSortDirection direction = ListSortDirection.Ascending;
            if (SortParameters != null)
            {
                direction = SortParameters.Direction;
                columnName = SortParameters.ColumnsName;
            }
            if (string.IsNullOrWhiteSpace(columnName))
            {
                columnName = nameof(BeMusicSeeker.Models.BMSFile.Title);
            }
            if (string.Equals(columnName, nameof(BeMusicSeeker.Models.BMSFile.rank), StringComparison.Ordinal))
            {
                columnName = nameof(BeMusicSeeker.Models.BMSFile.rateDouble);
            }
            bool isTreeSelectionRequest = requestedMode != viewUpdateMode.TreeViewFilterNotChanged && requestedMode < viewUpdateMode.KeywordFilterUpdated;
            bool isFolderMode = mode == viewUpdateMode.FolderFilterSelected;
            List<LibraryChartRow> modeFilterList = ChartRowsModeFilterView as List<LibraryChartRow>;
            bool isFullNormalLibraryResult = treeViewFilterTypeSelected == viewUpdateMode.FolderFilterSelected
                && FolderFilter == null
                && string.IsNullOrWhiteSpace(KeywordFilter)
                && ModeFilter == ModeFilterType.All
                && !isPlaylistDetailView
                && modeFilterList != null
                && modeFilterList.Count == folderCount
                && modeFilterList.Count == keywordCount
                && modeFilterList.Count == modeCount;
            if (isFolderMode && isTreeSelectionRequest && modeFilterList != null && folderSortSourceSnapshot != null && folderSortResultSnapshot != null && string.Equals(folderSortColumnName, columnName, StringComparison.Ordinal) && folderSortDirection == direction && IsSameReferenceSequence(modeFilterList, folderSortSourceSnapshot))
            {
                nextRowsView = folderSortResultSnapshot;
                sortReuse = true;
                sortProfile = "reuse";
            }
            else if (TryGetNormalLibrarySortCache(isFullNormalLibraryResult, columnName, direction, modeFilterList?.Count ?? modeCount, out List<LibraryChartRow> cachedRows, out NormalLibrarySortCacheKey sortCacheKey, out _))
            {
                Stopwatch sortCacheStopwatch = Stopwatch.StartNew();
                nextRowsView = cachedRows;
                sortCacheStopwatch.Stop();
                sortReuse = true;
                sortProfile = "reuse";
                LogMainSortDetail(CreateNormalLibrarySortCacheMetrics(sortCacheKey, sortCacheStopwatch.ElapsedMilliseconds, cacheHit: true));
            }
            else
            {
                List<LibraryChartRow> sortedRows = LibraryChartRowSortEngine.SortForMainView(ChartRowsModeFilterView, SortParameters, isPlaylistDetailView, useLegacySortForMainView, out sortProfile, out LibraryChartSortMetrics sortMetrics);
                nextRowsView = sortedRows;
                if (isFullNormalLibraryResult && TryNormalizeNormalLibrarySortCacheColumn(columnName, out string normalizedCacheColumnName))
                {
                    NormalLibrarySortCacheKey newCacheKey;
                    lock (normalLibrarySortCacheLock)
                    {
                        newCacheKey = new NormalLibrarySortCacheKey(normalLibrarySourceGeneration, normalLibrarySortKeyGeneration, normalizedCacheColumnName, direction, sortedRows.Count);
                    }
                    StoreNormalLibrarySortCache(newCacheKey, sortedRows);
                    sortMetrics = new LibraryChartSortMetrics(
                        sortMetrics.RowCount,
                        sortMetrics.ColumnName,
                        sortMetrics.Direction,
                        sortMetrics.PropertyTypeName,
                        sortMetrics.SortProfile,
                        sortMetrics.StringSortKind,
                        sortMetrics.SortMs,
                        sortReuse: false,
                        sortCacheKey: normalizedCacheColumnName,
                        sortCacheGeneration: newCacheKey.SortKeyGeneration,
                        sortCacheHit: false);
                }
                LogMainSortDetail(sortMetrics);
                if (isFolderMode)
                {
                    folderSortResultSnapshot = sortedRows;
                }
            }
            if (isFolderMode)
            {
                if (modeFilterList != null)
                {
                    folderSortSourceSnapshot = modeFilterList;
                }
                else
                {
                    folderSortSourceSnapshot = ChartRowsModeFilterView.ToList();
                }
                folderSortColumnName = columnName;
                folderSortDirection = direction;
            }
        }
        else
        {
            nextRowsView = ChartRowsModeFilterView.ToList();
            sortProfile = "bypass";
        }
        sortStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        viewCount = nextRowsView?.Count ?? 0;
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        if (!ReferenceEquals(BMSFilesView, nextRowsView))
        {
            base.Messenger.Raise(new InteractionMessage("PrepareMainTableSwap"));
        }
        loadColumnSetting((mode < viewUpdateMode.KeywordFilterUpdated) ? mode : treeViewFilterTypeSelected);
        columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        SetChartRowsView(nextRowsView);
        callbackStageMs = 0L;
        string sortColumn = SortParameters?.ColumnsName ?? "(default_title)";
        string sortDirection = SortParameters?.Direction.ToString() ?? "Ascending";
        string parameterType = parameter?.GetType().Name ?? "(null)";
        bool fastSortEnabled = true;
        bool isPlaylistDetailForLog = mode == viewUpdateMode.PlaylistFilterSelected || mode == viewUpdateMode.PlaylistNotOwnedFilterSelected || treeViewFilterTypeSelected == viewUpdateMode.PlaylistFilterSelected || treeViewFilterTypeSelected == viewUpdateMode.PlaylistNotOwnedFilterSelected;
        long mainViewBuildRequestId = Interlocked.Increment(ref mainViewBuildRequestIdSeed);
        long mainViewBuildEndTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref lastMainViewBuildRequestId, mainViewBuildRequestId);
        Interlocked.Exchange(ref lastMainViewBuildEndTimestamp, mainViewBuildEndTimestamp);
        Volatile.Write(ref lastMainViewBuildThreadId, Thread.CurrentThread.ManagedThreadId);
        Volatile.Write(ref lastMainViewBuildMode, (int)mode);
        if (isPlaylistDetailForLog)
        {
            Interlocked.Exchange(ref lastPlaylistDetailBuildCompletedTimestamp, mainViewBuildEndTimestamp);
            Interlocked.Exchange(ref lastPlaylistDetailBuildElapsedMs, viewBuildStopwatch.ElapsedMilliseconds);
        }
        LogMainViewBuild("main_view_build mode=" + mode + " requestedMode=" + requestedMode + " parameterType=" + parameterType + " folderMs=" + folderStageMs + " keywordMs=" + keywordStageMs + " modeMs=" + modeStageMs + " sortMs=" + sortStageMs + " sortReuse=" + sortReuse + " sortProfile=" + sortProfile + " sortEngine=fast fastSortEnabled=" + fastSortEnabled + " isPlaylistDetailView=" + isPlaylistDetailForLog + " columnMs=" + columnStageMs + " callbackMs=" + callbackStageMs + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds + " folderCount=" + folderCount + " keywordCount=" + keywordCount + " modeCount=" + modeCount + " viewCount=" + viewCount + " sortColumn=" + sortColumn + " sortDirection=" + sortDirection);
    }

    internal static List<LibraryChartRow> BuildStandardLibraryRowsForView(
        IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles,
        IEnumerable<LibraryChartRow> bmsonRows,
        Func<BeMusicSeeker.Models.BMSFile, bool> folderFilter)
    {
        return BuildStandardLibraryRowsForView(bmsFiles, bmsonRows, folderFilter, out _);
    }

    internal static List<LibraryChartRow> BuildStandardLibraryRowsForView(
        IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles,
        IEnumerable<LibraryChartRow> bmsonRows,
        Func<BeMusicSeeker.Models.BMSFile, bool> folderFilter,
        out LibraryRowsBuildMetrics metrics)
    {
        return BuildStandardLibraryRowsForView(bmsFiles, bmsonRows, folderFilter, LibraryChartRow.FromBmsFile, null, out metrics);
    }

    internal static List<LibraryChartRow> BuildStandardLibraryRowsForView(
        IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles,
        IEnumerable<LibraryChartRow> bmsonRows,
        Func<BeMusicSeeker.Models.BMSFile, bool> folderFilter,
        Func<BeMusicSeeker.Models.BMSFile, LibraryChartRow> bmsRowFactory,
        LibraryRowCacheBuildStats rowCacheStats,
        out LibraryRowsBuildMetrics metrics)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        long regularFilterMs = 0L;
        long bmsonFilterMs = 0L;
        long regularRowMaterializeMs;
        long bmsonRowMaterializeMs = 0L;
        long concatToListMs;
        int sourceBmsCount = CountIfCheap(bmsFiles);
        int sourceBmsonCount = CountIfCheap(bmsonRows);
        IEnumerable<BeMusicSeeker.Models.BMSFile> regularRows = (bmsFiles ?? Enumerable.Empty<BeMusicSeeker.Models.BMSFile>()).Where((BeMusicSeeker.Models.BMSFile file) => file != null);
        IEnumerable<LibraryChartRow> normalizedBmsonRows = (bmsonRows ?? Enumerable.Empty<LibraryChartRow>()).Where((LibraryChartRow row) => row != null);
        if (folderFilter != null)
        {
            Stopwatch filterStopwatch = Stopwatch.StartNew();
            regularRows = regularRows.AsParallel().Where(folderFilter);
            List<BeMusicSeeker.Models.BMSFile> filteredRegularRows = regularRows.ToList();
            filterStopwatch.Stop();
            regularFilterMs = filterStopwatch.ElapsedMilliseconds;
            regularRows = filteredRegularRows;

            filterStopwatch.Restart();
            normalizedBmsonRows = normalizedBmsonRows.AsParallel().Where((LibraryChartRow row) => folderFilter(row.BmsFile ?? PendingChartEntry.CreateFromBmsonSong(row.BmsonSong)));
            List<LibraryChartRow> filteredBmsonRows = normalizedBmsonRows.ToList();
            filterStopwatch.Stop();
            bmsonFilterMs = filterStopwatch.ElapsedMilliseconds;
            normalizedBmsonRows = filteredBmsonRows;
        }

        Stopwatch materializeStopwatch = Stopwatch.StartNew();
        List<LibraryChartRow> regularLibraryRows = ToLibraryChartRows(regularRows, bmsRowFactory ?? LibraryChartRow.FromBmsFile);
        materializeStopwatch.Stop();
        regularRowMaterializeMs = materializeStopwatch.ElapsedMilliseconds;

        materializeStopwatch.Restart();
        List<LibraryChartRow> bmsonLibraryRows = normalizedBmsonRows.ToList();
        materializeStopwatch.Stop();
        bmsonRowMaterializeMs = materializeStopwatch.ElapsedMilliseconds;

        Stopwatch concatStopwatch = Stopwatch.StartNew();
        List<LibraryChartRow> rows = new List<LibraryChartRow>(regularLibraryRows.Count + bmsonLibraryRows.Count);
        rows.AddRange(regularLibraryRows);
        rows.AddRange(bmsonLibraryRows);
        concatStopwatch.Stop();
        concatToListMs = concatStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();

        metrics = new LibraryRowsBuildMetrics(
            sourceBmsCount,
            sourceBmsonCount,
            regularLibraryRows.Count,
            bmsonLibraryRows.Count,
            folderFilter != null,
            regularFilterMs,
            bmsonFilterMs,
            regularRowMaterializeMs,
            bmsonRowMaterializeMs,
            concatToListMs,
            totalStopwatch.ElapsedMilliseconds,
            rows.Count,
            rowCacheStats?.HitCount ?? 0,
            rowCacheStats?.MissCount ?? 0,
            rowCacheStats?.PrunedCount ?? 0);
        return rows;
    }

    private LibraryRowCacheBuildStats CreateRegularRowCacheBuildStats()
    {
        LibraryRowCacheBuildStats stats = new LibraryRowCacheBuildStats
        {
            PrunedCount = pendingRegularBmsRowCachePrunedCount
        };
        pendingRegularBmsRowCachePrunedCount = 0;
        return stats;
    }

    private static int CountIfCheap<T>(IEnumerable<T> source)
    {
        if (source == null)
        {
            return 0;
        }
        if (source is ICollection<T> genericCollection)
        {
            return genericCollection.Count;
        }
        if (source is ICollection collection)
        {
            return collection.Count;
        }
        return -1;
    }

    private static void LogMainViewFolderDetail(viewUpdateMode mode, LibraryRowsBuildMetrics metrics)
    {
        LogMainViewBuild("main_view_folder_detail mode=" + mode
            + " folderFilterApplied=" + metrics.FolderFilterApplied
            + " sourceBmsCount=" + metrics.SourceBmsCount
            + " sourceBmsonCount=" + metrics.SourceBmsonCount
            + " filteredBmsCount=" + metrics.FilteredBmsCount
            + " filteredBmsonCount=" + metrics.FilteredBmsonCount
            + " regularFilterMs=" + metrics.RegularFilterMs
            + " bmsonFilterMs=" + metrics.BmsonFilterMs
            + " regularRowMaterializeMs=" + metrics.RegularRowMaterializeMs
            + " bmsonRowMaterializeMs=" + metrics.BmsonRowMaterializeMs
            + " concatToListMs=" + metrics.ConcatToListMs
            + " regularRowCacheHitCount=" + metrics.RegularRowCacheHitCount
            + " regularRowCacheMissCount=" + metrics.RegularRowCacheMissCount
            + " regularRowCachePrunedCount=" + metrics.RegularRowCachePrunedCount
            + " folderMs=" + metrics.FolderMs
            + " folderCount=" + metrics.FolderCount);
    }

    private static void LogMainSortDetail(LibraryChartSortMetrics metrics)
    {
        LogMainViewBuild("main_sort_detail rowCount=" + metrics.RowCount
            + " columnName=" + (metrics.ColumnName ?? string.Empty)
            + " direction=" + metrics.Direction
            + " propertyType=" + (metrics.PropertyTypeName ?? "(null)")
            + " sortProfile=" + (metrics.SortProfile ?? string.Empty)
            + " stringSortKind=" + (metrics.StringSortKind ?? string.Empty)
            + " sortReuse=" + metrics.SortReuse
            + " sortCacheKey=" + (metrics.SortCacheKey ?? string.Empty)
            + " sortCacheGeneration=" + metrics.SortCacheGeneration
            + " sortCacheHit=" + metrics.SortCacheHit
            + " sortMs=" + metrics.SortMs);
    }

    private static bool TryNormalizeNormalLibrarySortCacheColumn(string columnName, out string normalizedColumnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            normalizedColumnName = nameof(LibraryChartRow.Title);
            return true;
        }
        if (string.Equals(columnName, nameof(LibraryChartRow.Title), StringComparison.Ordinal))
        {
            normalizedColumnName = nameof(LibraryChartRow.Title);
            return true;
        }
        if (string.Equals(columnName, nameof(LibraryChartRow.path), StringComparison.Ordinal))
        {
            normalizedColumnName = nameof(LibraryChartRow.path);
            return true;
        }
        normalizedColumnName = null;
        return false;
    }

    internal static bool IsNormalLibrarySortCacheCandidateForTest(string columnName)
    {
        return TryNormalizeNormalLibrarySortCacheColumn(columnName, out _);
    }

    private bool TryGetNormalLibrarySortCache(
        bool isEligible,
        string columnName,
        ListSortDirection direction,
        int rowCount,
        out List<LibraryChartRow> rows,
        out NormalLibrarySortCacheKey cacheKey,
        out string cacheColumnName)
    {
        rows = null;
        cacheKey = default;
        cacheColumnName = string.Empty;
        if (!isEligible || !TryNormalizeNormalLibrarySortCacheColumn(columnName, out cacheColumnName))
        {
            return false;
        }
        lock (normalLibrarySortCacheLock)
        {
            cacheKey = new NormalLibrarySortCacheKey(normalLibrarySourceGeneration, normalLibrarySortKeyGeneration, cacheColumnName, direction, rowCount);
            return normalLibrarySortCache.TryGetValue(cacheKey, out rows);
        }
    }

    private void StoreNormalLibrarySortCache(NormalLibrarySortCacheKey cacheKey, List<LibraryChartRow> rows)
    {
        if (rows == null || string.IsNullOrWhiteSpace(cacheKey.ColumnName))
        {
            return;
        }
        lock (normalLibrarySortCacheLock)
        {
            normalLibrarySortCache[cacheKey] = rows;
        }
    }

    private static LibraryChartSortMetrics CreateNormalLibrarySortCacheMetrics(NormalLibrarySortCacheKey cacheKey, long sortMs, bool cacheHit)
    {
        return new LibraryChartSortMetrics(
            cacheKey.RowCount,
            cacheKey.ColumnName,
            cacheKey.Direction,
            nameof(String),
            "library_chart_string_fast_ordinal_ignore_case",
            "ordinal_ignore_case",
            sortMs,
            sortReuse: cacheHit,
            sortCacheKey: cacheKey.ColumnName,
            sortCacheGeneration: cacheKey.SortKeyGeneration,
            sortCacheHit: cacheHit);
    }

    private static List<LibraryChartRow> ToLibraryChartRows(IEnumerable<BeMusicSeeker.Models.BMSFile> files)
    {
        return ToLibraryChartRows(files, LibraryChartRow.FromBmsFile);
    }

    private static List<LibraryChartRow> ToLibraryChartRows(IEnumerable<BeMusicSeeker.Models.BMSFile> files, Func<BeMusicSeeker.Models.BMSFile, LibraryChartRow> rowFactory)
    {
        return (files ?? Enumerable.Empty<BeMusicSeeker.Models.BMSFile>())
            .Select(rowFactory ?? LibraryChartRow.FromBmsFile)
            .Where((LibraryChartRow row) => row != null)
            .ToList();
    }

    private static bool ShouldIncludeBmsonLibraryRowsInMainView(viewUpdateMode mode, viewUpdateMode currentTreeMode)
    {
        if (mode == viewUpdateMode.FullScanAllChartsFilterSelected || currentTreeMode == viewUpdateMode.FullScanAllChartsFilterSelected)
        {
            return true;
        }
        if (IsPlaylistTreeActive(mode, currentTreeMode))
        {
            return false;
        }
        if (Enum.IsDefined(typeof(MaintenanceFilterType), (int)mode))
        {
            return false;
        }
        if (Enum.IsDefined(typeof(InstallFilterType), (int)mode))
        {
            return false;
        }
        return true;
    }

    private static bool ShouldRefreshPlaylistViewAfterBmsonSongsChanged(viewUpdateMode currentTreeMode)
    {
        return IsPlaylistTreeActive(currentTreeMode, currentTreeMode);
    }

    internal static bool ShouldRefreshPlaylistViewAfterBmsonSongsChangedForTest(int currentTreeMode)
    {
        return ShouldRefreshPlaylistViewAfterBmsonSongsChanged((viewUpdateMode)currentTreeMode);
    }

    private static object NormalizeDuplicateViewParameter(object parameter)
    {
        if (parameter == null || parameter is DuplicateViewContext || parameter is List<BeMusicSeeker.Models.BMSFile>)
        {
            return parameter;
        }
        if (parameter is DuplicateGroup duplicateGroup)
        {
            return DuplicateViewContext.ForGroup(duplicateGroup.Header);
        }
        if (parameter is string folderPath)
        {
            return DuplicateViewContext.ForFolder(folderPath);
        }
        return null;
    }

    private List<LibraryChartRow> GetBmsonLibraryRowsSnapshot()
    {
        return bmsonLibraryRowsByPath.Values
            .OrderBy((LibraryChartRow row) => row.path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private BmsonLibraryRowCacheSyncResult SyncBmsonLibraryRowCache(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<LR2SongDBExtended.bmson_song> snapshot = (bmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path))
            .OrderBy((LR2SongDBExtended.bmson_song song) => song.path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        HashSet<string> nextPaths = new HashSet<string>(snapshot.Select((LR2SongDBExtended.bmson_song song) => song.path), StringComparer.OrdinalIgnoreCase);
        bool membershipChanged = bmsonLibraryRowsByPath.Count != nextPaths.Count || bmsonLibraryRowsByPath.Keys.Any((string path) => !nextPaths.Contains(path));
        bool sortKeyChanged = membershipChanged;
        Dictionary<string, LibraryChartRow> nextByPath = new Dictionary<string, LibraryChartRow>(StringComparer.OrdinalIgnoreCase);
        Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRow> nextBySong = new Dictionary<LR2SongDBExtended.bmson_song, LibraryChartRow>(BmsonSongReferenceComparer.Instance);
        foreach (LR2SongDBExtended.bmson_song song in snapshot)
        {
            LibraryChartRow row = null;
            if (!bmsonLibraryRowsBySong.TryGetValue(song, out row))
            {
                bmsonLibraryRowsByPath.TryGetValue(song.path, out row);
            }
            if (row == null)
            {
                row = LibraryChartRow.FromBmsonSong(song);
                membershipChanged = true;
                sortKeyChanged = true;
            }
            else
            {
                string previousTitle = row.Title;
                string previousPath = row.path;
                row.UpdateFromBmsonSong(song);
                if (!string.Equals(previousTitle, row.Title, StringComparison.Ordinal)
                    || !string.Equals(previousPath, row.path, StringComparison.Ordinal))
                {
                    sortKeyChanged = true;
                }
            }
            if (row != null)
            {
                nextByPath[song.path] = row;
                nextBySong[song] = row;
            }
        }
        bmsonLibraryRowsByPath.Clear();
        foreach (KeyValuePair<string, LibraryChartRow> item2 in nextByPath)
        {
            bmsonLibraryRowsByPath[item2.Key] = item2.Value;
        }
        bmsonLibraryRowsBySong.Clear();
        foreach (KeyValuePair<LR2SongDBExtended.bmson_song, LibraryChartRow> item3 in nextBySong)
        {
            bmsonLibraryRowsBySong[item3.Key] = item3.Value;
        }
        return new BmsonLibraryRowCacheSyncResult(membershipChanged, sortKeyChanged);
    }

    private void SyncBmsonLibraryRowCacheWithoutRebuild()
    {
        SyncBmsonLibraryRowCache(files?.BmsonSongs);
    }

    private sealed class BmsonSongReferenceComparer : IEqualityComparer<LR2SongDBExtended.bmson_song>
    {
        internal static readonly BmsonSongReferenceComparer Instance = new BmsonSongReferenceComparer();

        public bool Equals(LR2SongDBExtended.bmson_song x, LR2SongDBExtended.bmson_song y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(LR2SongDBExtended.bmson_song obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }

    public void LoadColumnSetting()
    {
        loadColumnSetting(viewUpdateMode.TreeViewFilterNotChanged, isInit: true);
    }

    private void loadColumnSetting(viewUpdateMode mode, bool isInit = false)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long stageStartMs = stopwatch.ElapsedMilliseconds;
        long normalizeMs = 0L;
        long visibilityMs = 0L;
        long caseEnsureMs = 0L;
        long caseAssignMs = 0L;
        long playlistSummaryEnsureMs = 0L;
        long playlistSummaryAssignMs = 0L;
        Visibility targetColumnSettingsVisibilityForPlaylist = Visibility.Collapsed;
        string caseLabel = "none";

        if (mode == viewUpdateMode.TreeViewFilterNotChanged)
        {
            mode = treeViewFilterTypeSelected;
        }
        normalizeMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        switch (mode)
        {
            case viewUpdateMode.PlaylistFilterSelected:
            case viewUpdateMode.PlaylistNotOwnedFilterSelected:
                caseLabel = "playlist";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.PlaylistColumnsSettings == null)
                {
                    Settings.Default.PlaylistColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.PLAYLIST);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsBMSFilesView = Settings.Default.PlaylistColumnsSettings;
                targetColumnSettingsVisibilityForPlaylist = Visibility.Visible;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.FolderFilterSelected:
            case viewUpdateMode.UnregisteredFilterSelected:
                caseLabel = "standard";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.StandardColumnsSettings == null)
                {
                    Settings.Default.StandardColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsBMSFilesView = Settings.Default.StandardColumnsSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.ZeroNoteFilterSelected:
                caseLabel = "zero-note";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.ZeroNoteColumnsSettings == null)
                {
                    Settings.Default.ZeroNoteColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.ZERO_NOTE);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsBMSFilesView = Settings.Default.ZeroNoteColumnsSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.FileMissingFilterSelected:
            case viewUpdateMode.FileMissingIgnoredFilterSelected:
            case viewUpdateMode.FullScanAllChartsFilterSelected:
            case viewUpdateMode.NewlyInstalledFolderSelected:
                caseLabel = "fullscan";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.FullScanColumnsSettings == null)
                {
                    Settings.Default.FullScanColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.FULLSCAN);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsBMSFilesView = Settings.Default.FullScanColumnsSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.DuplicateFilterSelected:
                caseLabel = "duplicate";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.DuplicateColumnsSettings == null)
                {
                    Settings.Default.DuplicateColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.DUPLICATE);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsBMSFilesView = Settings.Default.DuplicateColumnsSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.GarbledFilterSelected:
            case viewUpdateMode.GarbleFixedFilterSelected:
                caseLabel = "encoding";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.EncodingColumnsSettings == null)
                {
                    Settings.Default.EncodingColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.ENCODING);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsBMSFilesView = Settings.Default.EncodingColumnsSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.PendingInstallFolderSelected:
                caseLabel = "install";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.InstallColumnsSettings == null)
                {
                    Settings.Default.InstallColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.INSTALL);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsBMSFilesView = Settings.Default.InstallColumnsSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
        }
        stageStartMs = stopwatch.ElapsedMilliseconds;
        ColumnSettingsVisibilityForPlaylist = targetColumnSettingsVisibilityForPlaylist;
        visibilityMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        stageStartMs = stopwatch.ElapsedMilliseconds;
        if (Settings.Default.PlaylistSummaryColumnsSettings == null)
        {
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        }
        Settings.Default.PlaylistSummaryColumnsSettings.EnsureCompatibility();
        playlistSummaryEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        stageStartMs = stopwatch.ElapsedMilliseconds;
        PlaylistSummaryColumnsSettings = Settings.Default.PlaylistSummaryColumnsSettings;
        playlistSummaryAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;

        long totalMs = stopwatch.ElapsedMilliseconds;
        if (totalMs >= ColumnSettingSlowLogThresholdMs)
        {
            LogMainViewBuild("column_setting_slow mode=" + mode + " case=" + caseLabel + " isInit=" + isInit + " totalMs=" + totalMs + " normalizeMs=" + normalizeMs + " visibilityMs=" + visibilityMs + " caseEnsureMs=" + caseEnsureMs + " caseAssignMs=" + caseAssignMs + " playlistSummaryEnsureMs=" + playlistSummaryEnsureMs + " playlistSummaryAssignMs=" + playlistSummaryAssignMs + " thresholdMs=" + ColumnSettingSlowLogThresholdMs);
        }
    }

    public void ExecSort(string columnName, ListSortDirection direction)
    {
        if (SortParameters == null || SortParameters.ColumnsName != columnName || SortParameters.Direction != direction)
        {
            SortParameters = new cSortParameters
            {
                ColumnsName = columnName,
                Direction = direction
            };
            makeBMSFilesView(viewUpdateMode.SortUpdated);
        }
    }

    public void ExecPlaylistSummarySort(string columnName, ListSortDirection direction)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            columnName = nameof(PlaylistSummaryRow.Name);
        }
        if (PlaylistSummarySortParameters == null || PlaylistSummarySortParameters.ColumnsName != columnName || PlaylistSummarySortParameters.Direction != direction)
        {
            PlaylistSummarySortParameters = new cSortParameters
            {
                ColumnsName = columnName,
                Direction = direction
            };
            RefreshPlaylistSummaryPresentationIfVisible();
        }
    }

    public void ExecFolderFilter(FolderFilterType type, string filterKey = null)
    {
        SetPlaylistSummaryMode(enabled: false);
        if (!string.IsNullOrWhiteSpace(filterKey))
        {
            switch (type)
            {
                case FolderFilterType.FilterNone:
                    break;
                default:
                    return;
                case FolderFilterType.DirectoryFilter:
                    FolderFilter = (BeMusicSeeker.Models.BMSFile r) => r.path.Contains(filterKey + Path.DirectorySeparatorChar);
                    return;
                case FolderFilterType.ArtistFilter:
                    FolderFilter = (BeMusicSeeker.Models.BMSFile r) => r.Artist.ToUpperInvariant().Contains(filterKey.ToUpperInvariant());
                    return;
            }
        }
        FolderFilter = null;
    }

    public void ExecPlaylistFilter(BMSTable bmsTable, string folderName = null, PlaylistFilterType type = PlaylistFilterType.PlaylistFilter)
    {
        SetPlaylistSummaryMode(enabled: false);
        switch (type)
        {
            case PlaylistFilterType.PlaylistFilter:
                makeBMSFilesView(viewUpdateMode.PlaylistFilterSelected, new Tuple<BMSTable, string>(bmsTable, folderName));
                break;
            case PlaylistFilterType.PlaylistNotOwnedFilterSelected:
                makeBMSFilesView(viewUpdateMode.PlaylistNotOwnedFilterSelected, bmsTable);
                break;
        }
    }

    public void ExecMaintenanceFilter(MaintenanceFilterType type, object parameter = null)
    {
        SetPlaylistSummaryMode(enabled: false);
        if (files != null)
        {
            if (type == MaintenanceFilterType.DuplicateFilter)
            {
                files.SearchBMSFilesDuplicated();
            }
            if (Enum.IsDefined(typeof(viewUpdateMode), (int)type))
            {
                makeBMSFilesView((viewUpdateMode)type, parameter);
            }
        }
    }

    public void ExecInstallFilter(InstallFilterType type, object parameter = null)
    {
        SetPlaylistSummaryMode(enabled: false);
        switch (type)
        {
            case InstallFilterType.NewlyInstalledFilter:
                makeBMSFilesView(viewUpdateMode.NewlyInstalledFolderSelected, parameter);
                break;
            case InstallFilterType.PendingInstallFilter:
                makeBMSFilesView(viewUpdateMode.PendingInstallFolderSelected, parameter);
                break;
        }
    }

    public void FixEncodingBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string encoding = "")
    {
        files.SetBMSFilesEncoding(bmsFiles, encoding);
    }

    public void ForceFileScanCheckBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        ForceResourceHealthCheckCharts(bmsFiles);
    }

    public void ForceResourceHealthCheckCharts(IEnumerable<BeMusicSeeker.Models.BMSFile> chartFiles)
    {
        files.GetChartsNeedResourceFix(chartFiles, forceUpdate: true);
    }

    public void IgnoreFileScanCheckBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        SetChartResourceWarningsIgnored(bmsFiles);
    }

    public void NotIgnoreFileScanCheckBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        SetChartResourceWarningsIgnored(bmsFiles, unset: true);
    }

    public void SetChartResourceWarningsIgnored(IEnumerable<BeMusicSeeker.Models.BMSFile> chartFiles, bool unset = false)
    {
        files.SetChartResourceWarningsIgnored(chartFiles, unset);
    }

    /// <summary>
    /// リンク切れ等の問題がある BMSPackage (インストーラーまたはアーカイブ単位) について、正しいインストール先のディレクトリをヒューリスティックに探索します。
    /// 探索結果は内部の BMSLibrary に対して適用されます。
    /// </summary>
    /// <param name="packages">探索・復旧対象となるBMSパッケージのコレクション。</param>
    public void SearchInstallationDirectoryBMSFiles(IEnumerable<BMSPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        lock (lockCopyFile)
        {
            files.SearchEstimatedInstallationDirectory(packages);
        }
    }

    public void SearchMergeDestinationBMSFiles(IEnumerable<BMSPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<BMSPackage> list = packages.Where((BMSPackage pkg) => pkg != null).ToList();
        lock (lockCopyFile)
        {
            for (int num = 0; num < list.Count; num++)
            {
                files.SearchMergeDestination(list[num]);
            }
        }
    }

    /// <summary>
    /// リンク切れ等の問題がある BMSFile (個別ファイル単位) について、正しいインストール先のディレクトリをヒューリスティックに探索します。
    /// 同一パッケージに属するファイル群はまとめてパッケージ単位で探索が試みられます。
    /// </summary>
    /// <param name="bmsFiles">探索・復旧対象となるBMSファイルのコレクション。</param>
    public void SearchInstallationDirectoryBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        SearchInstallDestinationForPendingCharts(bmsFiles);
    }

    public void SearchInstallDestinationForPendingCharts(IEnumerable<BeMusicSeeker.Models.BMSFile> chartFiles)
    {
        if (chartFiles == null)
        {
            throw new ArgumentNullException("chartFiles");
        }
        lock (lockCopyFile)
        {
            files.SearchEstimatedInstallationDirectory(chartFiles);
        }
    }

    public void SearchMergeDestinationBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        lock (lockCopyFile)
        {
            List<BeMusicSeeker.Models.BMSFile> bmsFiles2 = bmsFiles.Where((BeMusicSeeker.Models.BMSFile bmsInfo) => bmsInfo != null).ToList();
            List<BMSPackage> bMSPackages = getBMSPackages(ref bmsFiles2);
            for (int num = 0; num < bMSPackages.Count; num++)
            {
                files.SearchMergeDestination(bMSPackages[num]);
            }
            if (bmsFiles2.Count > 0)
            {
                files.SearchMergeDestination(bmsFiles2);
            }
        }
    }

    /// <summary>
    /// playlist 詳細表示 row の編集結果を playlist DB へ永続化します。
    /// </summary>
    /// <param name="playlistRow">保存対象 row。</param>
    internal void CommitPlaylistRow(PlaylistDetailRow playlistRow)
    {
        if (playlistRow == null)
        {
            throw new ArgumentNullException(nameof(playlistRow));
        }
        if (playlistRow.ResolvedBmson != null && playlistRow.RealFile == null)
        {
            playlistRow.Entry.MarkAsBmsonPlaylistIdentity(playlistRow.sha256 ?? playlistRow.ResolvedBmson.sha256);
        }
        tables.CommitBMSTableEntry(playlistRow.Entry);
    }

    /// <summary>
    /// 編集済み playlist row の内容を source snapshot へ反映します。
    /// 再 sort/filter 時の正本更新を DB commit より先行させます。
    /// </summary>
    /// <param name="playlistRow">同期対象 row。</param>
    internal void SyncPlaylistSourceRowFromEditedViewRow(PlaylistDetailRow playlistRow)
    {
        if (playlistRow == null)
        {
            throw new ArgumentNullException(nameof(playlistRow));
        }
        bool updated = false;
        lock (playlistViewState.SyncRoot)
        {
            foreach (PlaylistDetailSourceRow sourceRow in playlistViewState.SourceRows ?? Enumerable.Empty<PlaylistDetailSourceRow>())
            {
                if (sourceRow != null && ReferenceEquals(sourceRow.Entry, playlistRow.Entry))
                {
                    sourceRow.SynchronizeEditableSnapshot(playlistRow);
                    updated = true;
                    break;
                }
            }
        }
        if (updated)
        {
            LogPlaylistWorker("playlist_source_snapshot synchronized entryMd5=" + (playlistRow.Entry?.md5 ?? "(null)"));
        }
    }

    /// <summary>
    /// playlist 行セル編集の開始を通知します。
    /// score snapshot 更新による再描画を安全に遅延させるために利用します。
    /// </summary>
    internal void NotifyPlaylistCellEditStarted()
    {
        lock (playlistViewState.SyncRoot)
        {
            playlistViewState.IsPlaylistCellEditing = true;
        }
    }

    /// <summary>
    /// playlist 行セル編集の終了を通知します。
    /// 編集中に保留した score snapshot refresh があれば、編集終了後に 1 回だけ再構築します。
    /// </summary>
    internal void NotifyPlaylistCellEditCompleted()
    {
        int pendingScoreSnapshotVersion = 0;
        int lastBuiltScoreSnapshotVersion = 0;
        lock (playlistViewState.SyncRoot)
        {
            playlistViewState.IsPlaylistCellEditing = false;
            pendingScoreSnapshotVersion = playlistViewState.PendingScoreSnapshotRefreshVersion;
            lastBuiltScoreSnapshotVersion = playlistViewState.LastBuiltScoreSnapshotVersion;
            if (pendingScoreSnapshotVersion > lastBuiltScoreSnapshotVersion)
            {
                playlistViewState.PendingScoreSnapshotRefreshVersion = 0;
            }
        }
        if (!IsPlaylistDetailViewActive || pendingScoreSnapshotVersion <= lastBuiltScoreSnapshotVersion)
        {
            return;
        }
        LogPlaylistWorker("playlist_score_snapshot_refresh_requested scoreSnapshotVersion=" + pendingScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + lastBuiltScoreSnapshotVersion + " deferredByEdit=true");
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
    }

    /// <summary>
    /// score snapshot 更新に伴う playlist detail view の再反映を要求します。
    /// 編集中は保留し、編集終了後に 1 回だけ再構築します。
    /// </summary>
    private void RequestPlaylistScoreSnapshotRefresh(int scoreSnapshotVersion)
    {
        int lastBuiltScoreSnapshotVersion = 0;
        bool deferRefresh = false;
        lock (playlistViewState.SyncRoot)
        {
            lastBuiltScoreSnapshotVersion = playlistViewState.LastBuiltScoreSnapshotVersion;
            if (scoreSnapshotVersion <= lastBuiltScoreSnapshotVersion)
            {
                return;
            }
            if (playlistViewState.IsPlaylistCellEditing)
            {
                playlistViewState.PendingScoreSnapshotRefreshVersion = Math.Max(playlistViewState.PendingScoreSnapshotRefreshVersion, scoreSnapshotVersion);
                deferRefresh = true;
            }
        }
        if (deferRefresh)
        {
            LogPlaylistWorker("playlist_score_snapshot_refresh_deferred scoreSnapshotVersion=" + scoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + lastBuiltScoreSnapshotVersion + " reason=editing");
            return;
        }
        LogPlaylistWorker("playlist_score_snapshot_refresh_requested scoreSnapshotVersion=" + scoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + lastBuiltScoreSnapshotVersion + " deferredByEdit=false");
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
    }

    /// <summary>
    /// playlist entry が持つ level を、対応する実体譜面へ反映します。
    /// </summary>
    /// <param name="bmsTable">参照元 playlist。</param>
    public void ReplaceBMSFileLevelByTableEntryLevel(BMSTable bmsTable)
    {
        if (bmsTable != null && BMSFiles != null)
        {
            IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles = from file in BMSFiles
                                                                 where file != null && !string.IsNullOrWhiteSpace(file.hash) && !string.IsNullOrWhiteSpace(file.path)
                                                                 join entry in from entry in bmsTable.GetEntriesExceptDummy()
                                                                               where !entry.is_removed && entry.level.HasValue
                                                                               select entry on file.hash equals entry.md5
                                                                 select ApplyPlaylistEntryLevel(file, entry.level);
            files.CommitBMSFiles(bmsFiles);
        }
    }

    /// <summary>
    /// playlist entry が持つ level を実体譜面へ反映し、その譜面を返します。
    /// </summary>
    /// <param name="file">更新対象の実体譜面。</param>
    /// <param name="entryLevel">playlist entry 側の level。</param>
    /// <returns>更新後の譜面。</returns>
    private static BeMusicSeeker.Models.BMSFile ApplyPlaylistEntryLevel(BeMusicSeeker.Models.BMSFile file, double? entryLevel)
    {
        if (file == null || !entryLevel.HasValue)
        {
            return null;
        }
        file.level = ((!(entryLevel.Value < 0.0)) ? ((int)entryLevel.Value) : 0);
        return file;
    }

    /// <summary>
    /// 指定されたパスのBMSファイルやアーカイブ群をBMSLibraryへ自動インストール・登録します。
    /// 登録完了後、読み込み済みの各プレイリスト (BMSTable) に対しても新規検出されたファイル群のリファレンス追加（参照解決）を試みます。
    /// </summary>
    /// <param name="installPaths">インストールの対象となるファイルまたはディレクトリパスのコレクション。</param>
    /// <param name="token">処理を中止するためのキャンセレーショントークン。</param>
    /// <param name="onEachCompleted">インストール処理完了時に呼ばれるコールバック。</param>
    public void InstallBMSFiles(IEnumerable<string> installPaths, CancellationToken token = default(CancellationToken), Action<bool> onEachCompleted = null, Action onEachPathProcessed = null)
    {
        if (files == null)
        {
            return;
        }
        string[] normalizedInstallPaths = (installPaths ?? Enumerable.Empty<string>()).Where((string path) => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (normalizedInstallPaths.Length == 0)
        {
            return;
        }
        List<BMSPackage> list = new List<BMSPackage>();
        lock (lockCopyFile)
        {
            try
            {
                if (!token.IsCancellationRequested)
                {
                    list.AddRange(files.InstallBMSFilesAuto(normalizedInstallPaths, token, onEachPathProcessed));
                }
            }
            catch (FileNotFoundException ex)
            {
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_installation + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                onEachCompleted?.Invoke(obj: false);
                return;
            }
            catch
            {
                throw;
            }
        }
        if (list.Count > 0)
        {
            tables.AcquireReaderLockBMSTables();
            try
            {
                files.AddReferenceBMSTables(BMSTables, list.SelectMany((BMSPackage p) => p.BMSFiles));
            }
            finally
            {
                tables.FreeReaderLockBMSTables();
            }
        }
        onEachCompleted?.Invoke(obj: !token.IsCancellationRequested);
    }

    public void EnqueueDroppedInstallPaths(IEnumerable<string> paths)
    {
        dropInstallQueueProcessor?.Enqueue(paths);
    }

    public void CancelDroppedInstallQueue()
    {
        dropInstallQueueProcessor?.CancelAll();
    }

    private void ProcessDroppedInstallBatch(DroppedInstallBatchRequest request, CancellationToken token)
    {
        if (request == null || request.PathCount == 0 || files == null)
        {
            return;
        }
        int completedPathCount = 0;
        InstallBMSFiles(request.Paths, token, null, delegate
        {
            completedPathCount++;
            dropInstallQueueProcessor?.ReportActiveBatchProgress(completedPathCount);
        });
    }

    private void HandleDroppedInstallBatchException(Exception ex)
    {
        if (ex == null)
        {
            return;
        }
        Action action = delegate
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_installation + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
        };
        if (System.Windows.Application.Current?.Dispatcher == null || System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
        }
    }

    private void UpdateDropInstallQueueStatus(DropInstallQueueStatusSnapshot snapshot)
    {
        Action reflect = delegate
        {
            latestDropInstallQueueStatus = snapshot ?? new DropInstallQueueStatusSnapshot();
            bool isActive = snapshot != null && snapshot.IsActive;
            IsDropInstallQueueActive = isActive;
            DropInstallQueueCanCancel = isActive && snapshot.CanCancel;
            DropInstallQueuePendingBatchCount = isActive ? snapshot.PendingBatchCount : 0;
            if (!isActive)
            {
                DropInstallQueueLabel = string.Empty;
                DropInstallQueueSubLabel = string.Empty;
            }
            else
            {
                DropInstallQueueLabel = string.Format(BeMusicSeeker.Properties.Resources.Drop_install_queue_label_format, Math.Max(0, snapshot.CompletedPathCount), Math.Max(0, snapshot.TotalPathCount), snapshot.PendingBatchCount);
                DropInstallQueueSubLabel = snapshot.CurrentDisplayName ?? string.Empty;
            }
            RefreshInstallPipelineStatus();
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
    }

    private void UpdatePendingEstimateQueueStatus(PendingInstallEstimateQueueStatusSnapshot snapshot)
    {
        Action reflect = delegate
        {
            latestPendingEstimateQueueStatus = snapshot?.Clone() ?? new PendingInstallEstimateQueueStatusSnapshot();
            bool isActive = snapshot != null && snapshot.IsActive;
            IsPendingEstimateQueueActive = isActive;
            PendingEstimateQueuePendingBatchCount = isActive ? snapshot.PendingBatchCount : 0;
            if (!isActive)
            {
                PendingEstimateQueueLabel = string.Empty;
                PendingEstimateQueueSubLabel = string.Empty;
            }
            else
            {
                PendingEstimateQueueLabel = string.Format(
                    BeMusicSeeker.Properties.Resources.Pending_estimate_queue_label_format,
                    Math.Max(0, snapshot.CompletedPackageCount),
                    Math.Max(snapshot.CurrentPackageCount, 0),
                    Math.Max(snapshot.PendingBatchCount, 0));
                PendingEstimateQueueSubLabel = snapshot.CurrentDisplayName ?? string.Empty;
            }
            RefreshInstallPipelineStatus();
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
    }

    private void UpdateInstallEstimationProgressStatus(InstallEstimationProgressSnapshot snapshot)
    {
        Action reflect = delegate
        {
            latestInstallEstimationProgress = snapshot?.Clone() ?? new InstallEstimationProgressSnapshot();
            RefreshInstallPipelineStatus();
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
    }

    private void RefreshInstallPipelineStatus()
    {
        bool dropActive = latestDropInstallQueueStatus != null && latestDropInstallQueueStatus.IsActive;
        bool pendingQueueActive = latestPendingEstimateQueueStatus != null && latestPendingEstimateQueueStatus.IsActive;
        bool estimateActive = latestInstallEstimationProgress != null && latestInstallEstimationProgress.IsActive;
        int pendingBatchCount = Math.Max(0, latestDropInstallQueueStatus?.PendingBatchCount ?? 0) + Math.Max(0, latestPendingEstimateQueueStatus?.PendingBatchCount ?? 0);
        if (dropActive)
        {
            IsInstallPipelineStatusActive = true;
            InstallPipelineLabel = string.Format(
                BeMusicSeeker.Properties.Resources.Drop_install_queue_label_format,
                Math.Max(0, latestDropInstallQueueStatus.CompletedPathCount),
                Math.Max(0, latestDropInstallQueueStatus.TotalPathCount),
                pendingBatchCount);
            InstallPipelineSubLabel = latestDropInstallQueueStatus.CurrentDisplayName ?? string.Empty;
            InstallPipelineMaximum = Math.Max(1, latestDropInstallQueueStatus.TotalPathCount);
            InstallPipelineValue = Math.Max(0, latestDropInstallQueueStatus.CompletedPathCount);
            InstallPipelineCanCancel = latestDropInstallQueueStatus.CanCancel;
            return;
        }
        if (estimateActive)
        {
            IsInstallPipelineStatusActive = true;
            InstallPipelineLabel = string.Format(
                BeMusicSeeker.Properties.Resources.Pending_estimate_queue_label_format,
                Math.Max(0, latestInstallEstimationProgress.CompletedWorkCount),
                Math.Max(0, latestInstallEstimationProgress.TotalWorkCount),
                pendingBatchCount);
            InstallPipelineSubLabel = latestInstallEstimationProgress.CurrentDisplayName ?? string.Empty;
            InstallPipelineMaximum = Math.Max(1, latestInstallEstimationProgress.TotalWorkCount);
            InstallPipelineValue = Math.Max(0, latestInstallEstimationProgress.CompletedWorkCount);
            InstallPipelineCanCancel = false;
            return;
        }
        if (pendingQueueActive)
        {
            IsInstallPipelineStatusActive = true;
            InstallPipelineLabel = string.Format(
                BeMusicSeeker.Properties.Resources.Pending_estimate_queue_label_format,
                Math.Max(0, latestPendingEstimateQueueStatus.CompletedPackageCount),
                Math.Max(1, latestPendingEstimateQueueStatus.CurrentPackageCount),
                pendingBatchCount);
            InstallPipelineSubLabel = latestPendingEstimateQueueStatus.CurrentDisplayName ?? string.Empty;
            InstallPipelineMaximum = Math.Max(1, latestPendingEstimateQueueStatus.CurrentPackageCount);
            InstallPipelineValue = Math.Max(0, latestPendingEstimateQueueStatus.CompletedPackageCount);
            InstallPipelineCanCancel = false;
            return;
        }
        IsInstallPipelineStatusActive = false;
        InstallPipelineLabel = string.Empty;
        InstallPipelineSubLabel = string.Empty;
        InstallPipelineValue = 0;
        InstallPipelineMaximum = 1;
        InstallPipelineCanCancel = false;
    }

    private void BeginPlaylistSyncProgressOperation()
    {
        lock (playlistSyncProgressLock)
        {
            playlistSyncProgressActiveOperationCount++;
        }
    }

    private void EndPlaylistSyncProgressOperation()
    {
        bool shouldClear = false;
        lock (playlistSyncProgressLock)
        {
            if (playlistSyncProgressActiveOperationCount > 0)
            {
                playlistSyncProgressActiveOperationCount--;
            }
            shouldClear = playlistSyncProgressActiveOperationCount == 0;
        }
        if (shouldClear)
        {
            UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
            {
                IsActive = false,
                TotalTableCount = 0,
                CompletedTableCount = 0,
                CurrentTableName = string.Empty,
                CurrentUri = null
            });
        }
    }

    private void UpdatePlaylistSyncProgressStatus(PlaylistSyncProgressSnapshot snapshot)
    {
        Action reflect = delegate
        {
            bool isActive = snapshot != null && snapshot.IsActive;
            IsPlaylistSyncProgressActive = isActive;
            if (!isActive)
            {
                PlaylistSyncProgressLabel = string.Empty;
                PlaylistSyncProgressSubLabel = string.Empty;
                PlaylistSyncProgressValue = 0.0;
                PlaylistSyncProgressMaximum = 0.0;
                return;
            }
            int total = Math.Max(snapshot.TotalTableCount, 1);
            int completed = Math.Max(0, Math.Min(snapshot.CompletedTableCount, total));
            PlaylistSyncProgressMaximum = total;
            PlaylistSyncProgressValue = completed;
            PlaylistSyncProgressLabel = (snapshot.TotalTableCount > 0) ? string.Format(BeMusicSeeker.Properties.Resources.Playlist_sync_progress_label_format, completed, total) : BeMusicSeeker.Properties.Resources.Playlist_sync_progress_single_label;
            PlaylistSyncProgressSubLabel = !string.IsNullOrWhiteSpace(snapshot.CurrentTableName) ? snapshot.CurrentTableName : (snapshot.CurrentUri?.ToString() ?? string.Empty);
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
    }

    /// <summary>
    /// 起動・リロード進捗の状態を開始し、基準版数を初期化します。
    /// </summary>
    /// <param name="operationKind">進捗対象の operation 種別。</param>
    private void StartStartupProgressOperation(StartupProgressOperationKind operationKind)
    {
        StartupProgressState state = new StartupProgressState
        {
            OperationKind = operationKind,
            OperationToken = Interlocked.Increment(ref startupProgressOperationTokenSeed),
            IsActive = true,
            CompletedPhases = StartupProgressPhase.CoreInitializeStarted,
            ExpectedPhases = StartupProgressPhase.CoreInitializeStarted | StartupProgressPhase.StartupReadyOperable,
            ScoreHydrationBaselineCompletedVersion = files?.ScoreHydrationCompletedVersion ?? 0,
            ScoreHydrationRequestedBaselineVersion = files?.ScoreHydrationRequestedVersion ?? 0,
            RankingRefreshBaselineCompletedVersion = files?.RankingRefreshCompletedVersion ?? 0,
            RankingRefreshRequestedBaselineVersion = files?.RankingRefreshRequestedVersion ?? 0,
            MaintenanceRequestedBaselineVersion = files?.MaintenanceDeferredRequestedVersion ?? 0,
            ChartDigestBackfillBaselineCompletedVersion = files?.ChartDigestBackfillCompletedVersion ?? 0,
            ChartInfoBackfillBaselineCompletedVersion = files?.ChartInfoBackfillCompletedVersion ?? 0,
            ChartInfoHydrationBaselineCompletedVersion = files?.ChartInfoHydrationCompletedVersion ?? 0,
            PlaylistEntriesHydrationBaselineCompletedVersion = tables?.PlaylistEntriesHydrationCompletedVersion ?? 0
        };
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            state.ExpectedPhases |= StartupProgressPhase.StartupReadyData | StartupProgressPhase.StartupReadyUi;
        }
        lock (startupProgressLock)
        {
            startupProgressState = state;
        }
        RecomputeStartupProgressPresentation();
    }

    /// <summary>
    /// 起動・リロード進捗を失敗表示へ切り替えます。
    /// </summary>
    /// <param name="subLabel">失敗時に表示する補足文言。</param>
    private void FailStartupProgressOperation(string subLabel)
    {
        SetStartupUiInteractionBlocked(false);
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            startupProgressState.IsFailed = true;
            startupProgressState.FailureSubLabel = subLabel ?? string.Empty;
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    /// <summary>
    /// 起動・リロード進捗のフェーズを完了済みにします。
    /// </summary>
    /// <param name="phase">完了したフェーズ。</param>
    private void MarkStartupProgressPhaseCompleted(StartupProgressPhase phase)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            startupProgressState.CompletedPhases |= phase;
            if (phase == StartupProgressPhase.RankingRefreshDone || phase == StartupProgressPhase.MaintenanceDeferredDone)
            {
                startupProgressState.LastCompletedAtUtc = DateTime.UtcNow;
            }
        }
        RecomputeStartupProgressPresentation();
    }

    /// <summary>
    /// 起動・リロード進捗で deferred playlist 参照適用を待機対象に追加します。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    /// <param name="version">要求版数。</param>
    private void TrackStartupProgressPlaylistReferenceRequest(string reason, int version)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !ShouldTrackStartupProgressPlaylistReference(reason, startupProgressState.OperationKind))
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.PlaylistReferenceApplied;
            startupProgressState.RequiredPlaylistReferenceVersion = Math.Max(startupProgressState.RequiredPlaylistReferenceVersion, version);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    /// <summary>
    /// 起動・リロード進捗で deferred playlist 参照適用完了を反映します。
    /// </summary>
    /// <param name="version">完了版数。</param>
    private void TryCompleteStartupProgressPlaylistReference(int version)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.PlaylistReferenceApplied) == 0)
            {
                return;
            }
            shouldComplete = version >= startupProgressState.RequiredPlaylistReferenceVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.PlaylistReferenceApplied);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred 外部プレイリスト同期を待機対象に追加します。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    /// <param name="version">要求版数。</param>
    private void TrackStartupProgressExternalSyncRequest(string reason, int version)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !ShouldTrackStartupProgressExternalSync(reason, startupProgressState.OperationKind))
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.ExternalPlaylistSyncDone;
            startupProgressState.RequiredExternalSyncVersion = Math.Max(startupProgressState.RequiredExternalSyncVersion, version);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    /// <summary>
    /// 起動・リロード進捗で deferred 外部プレイリスト同期完了を反映します。
    /// </summary>
    /// <param name="version">完了版数。</param>
    private void TryCompleteStartupProgressExternalSync(int version)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.ExternalPlaylistSyncDone) == 0)
            {
                return;
            }
            shouldComplete = version >= startupProgressState.RequiredExternalSyncVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ExternalPlaylistSyncDone);
        }
    }

    /// <summary>
    /// maintenance deferred 要求を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="requestedVersion">要求版数。</param>
    private void TrackStartupProgressMaintenanceRequested(int requestedVersion)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || requestedVersion <= startupProgressState.MaintenanceRequestedBaselineVersion)
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.MaintenanceDeferredDone;
            startupProgressState.RequiredMaintenanceCompletedVersion = Math.Max(startupProgressState.RequiredMaintenanceCompletedVersion, requestedVersion);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    private void TrackStartupProgressScoreHydrationRequested(int requestedVersion)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || requestedVersion <= startupProgressState.ScoreHydrationRequestedBaselineVersion)
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.ScoreHydrationDone;
            startupProgressState.RequiredScoreHydrationCompletedVersion = Math.Max(startupProgressState.RequiredScoreHydrationCompletedVersion, requestedVersion);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    private void TrackStartupProgressRankingRefreshRequested(int requestedVersion)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || requestedVersion <= startupProgressState.RankingRefreshRequestedBaselineVersion)
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.RankingRefreshDone;
            startupProgressState.RequiredRankingRefreshCompletedVersion = Math.Max(startupProgressState.RequiredRankingRefreshCompletedVersion, requestedVersion);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    private void TrackStartupProgressChartDigestBackfillRequested(int requestedVersion)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || requestedVersion <= startupProgressState.ChartDigestBackfillBaselineCompletedVersion)
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.ChartDigestBackfillDone;
            startupProgressState.RequiredChartDigestBackfillCompletedVersion = Math.Max(startupProgressState.RequiredChartDigestBackfillCompletedVersion, requestedVersion);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    private void TrackStartupProgressChartInfoBackfillRequested(int requestedVersion)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || requestedVersion <= startupProgressState.ChartInfoBackfillBaselineCompletedVersion)
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.ChartInfoBackfillDone;
            startupProgressState.RequiredChartInfoBackfillCompletedVersion = Math.Max(startupProgressState.RequiredChartInfoBackfillCompletedVersion, requestedVersion);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    private void TrackStartupProgressChartInfoHydrationRequested(int requestedVersion)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || requestedVersion <= startupProgressState.ChartInfoHydrationBaselineCompletedVersion)
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.ChartInfoHydrationDone;
            startupProgressState.RequiredChartInfoHydrationCompletedVersion = Math.Max(startupProgressState.RequiredChartInfoHydrationCompletedVersion, requestedVersion);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    private void TrackStartupProgressPlaylistEntriesHydrationRequested(int requestedVersion)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || requestedVersion <= startupProgressState.PlaylistEntriesHydrationBaselineCompletedVersion)
            {
                return;
            }
            startupProgressState.ExpectedPhases |= StartupProgressPhase.PlaylistEntriesHydrationDone;
            startupProgressState.RequiredPlaylistEntriesHydrationCompletedVersion = Math.Max(startupProgressState.RequiredPlaylistEntriesHydrationCompletedVersion, requestedVersion);
            startupProgressState.CompletionHideScheduled = false;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressChartDigestBackfillStatus(int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.ChartDigestBackfillDone) == 0)
            {
                return;
            }
            startupProgressState.ChartDigestBackfillTotalCount = totalCount;
            startupProgressState.ChartDigestBackfillProcessedCount = processedCount;
            startupProgressState.ChartDigestBackfillCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressChartInfoBackfillStatus(int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.ChartInfoBackfillDone) == 0)
            {
                return;
            }
            startupProgressState.ChartInfoBackfillTotalCount = totalCount;
            startupProgressState.ChartInfoBackfillProcessedCount = processedCount;
            startupProgressState.ChartInfoBackfillCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressChartInfoHydrationStatus(int totalCount, int appliedCount)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.ChartInfoHydrationDone) == 0)
            {
                return;
            }
            startupProgressState.ChartInfoHydrationTotalCount = totalCount;
            startupProgressState.ChartInfoHydrationAppliedCount = appliedCount;
        }
        RecomputeStartupProgressPresentation();
    }

    private void TryCompleteStartupProgressChartDigestBackfill(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.ChartDigestBackfillDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartDigestBackfillCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartDigestBackfillDone);
        }
    }

    private void TryCompleteStartupProgressChartInfoBackfill(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.ChartInfoBackfillDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartInfoBackfillCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartInfoBackfillDone);
        }
    }

    private void TryCompleteStartupProgressChartInfoHydration(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.ChartInfoHydrationDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartInfoHydrationCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartInfoHydrationDone);
        }
    }

    private void TryCompleteStartupProgressPlaylistEntriesHydration(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.PlaylistEntriesHydrationDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredPlaylistEntriesHydrationCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.PlaylistEntriesHydrationDone);
        }
    }

    /// <summary>
    /// maintenance deferred 完了を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    private void TryCompleteStartupProgressMaintenance(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.MaintenanceDeferredDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredMaintenanceCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.MaintenanceDeferredDone);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred score hydration 完了を反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    private void TryCompleteStartupProgressScoreHydration(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.ScoreHydrationDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredScoreHydrationCompletedVersion
                && completedVersion > startupProgressState.ScoreHydrationBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ScoreHydrationDone);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred ranking refresh 完了を反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    private void TryCompleteStartupProgressRankingRefresh(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.RankingRefreshDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredRankingRefreshCompletedVersion
                && completedVersion > startupProgressState.RankingRefreshBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.RankingRefreshDone);
        }
    }

    /// <summary>
    /// 起動・リロード進捗の表示を現在の内部状態から再計算します。
    /// </summary>
    private void RecomputeStartupProgressPresentation()
    {
        bool isActive;
        string label;
        string subLabel;
        double value;
        double maximum;
        bool shouldHideLater = false;
        long hideOperationToken = 0L;
        lock (startupProgressLock)
        {
            StartupProgressState state = startupProgressState;
            isActive = state.IsActive;
            if (!isActive)
            {
                label = string.Empty;
                subLabel = string.Empty;
                value = 0.0;
                maximum = 1.0;
            }
            else
            {
                maximum = Math.Max(1.0, CountExpectedStartupProgressPhases(state));
                value = CountCompletedExpectedStartupProgressPhases(state);
                bool operableCompleted = (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
                bool operationCompleted = !state.IsFailed && AreExpectedStartupProgressPhasesCompleted(state);
                if (state.IsFailed)
                {
                    label = GetStartupProgressFailedLabel(state.OperationKind);
                    subLabel = !string.IsNullOrWhiteSpace(state.FailureSubLabel) ? state.FailureSubLabel : GetStartupProgressSubLabel(state);
                }
                else if (operationCompleted)
                {
                    label = GetStartupProgressCompletedLabel(state.OperationKind);
                    subLabel = string.Empty;
                    if (!state.CompletionHideScheduled)
                    {
                        state.CompletionHideScheduled = true;
                        startupProgressState = state;
                        shouldHideLater = true;
                        hideOperationToken = state.OperationToken;
                    }
                }
                else if (operableCompleted)
                {
                    label = BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_background;
                    subLabel = GetStartupProgressSubLabel(state);
                }
                else
                {
                    label = GetStartupProgressRunningLabel(state.OperationKind);
                    subLabel = GetStartupProgressSubLabel(state);
                }
            }
        }
        Action reflect = delegate
        {
            IsStartupProgressActive = isActive;
            StartupProgressLabel = label;
            StartupProgressSubLabel = subLabel;
            StartupProgressValue = value;
            StartupProgressMaximum = maximum;
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
        if (shouldHideLater)
        {
            ScheduleStartupProgressHide(hideOperationToken);
        }
    }

    /// <summary>
    /// 完了表示後に起動・リロード進捗を非表示にします。
    /// </summary>
    /// <param name="operationToken">非表示対象の operation token。</param>
    private void ScheduleStartupProgressHide(long operationToken)
    {
        Task.Run(async delegate
        {
            await Task.Delay(2000).ConfigureAwait(false);
            bool shouldClear = false;
            lock (startupProgressLock)
            {
                if (startupProgressState.IsActive && !startupProgressState.IsFailed && startupProgressState.OperationToken == operationToken && startupProgressState.CompletionHideScheduled)
                {
                    startupProgressState = new StartupProgressState();
                    shouldClear = true;
                }
            }
            if (shouldClear)
            {
                RecomputeStartupProgressPresentation();
            }
        });
    }

    /// <summary>
    /// operation 種別に応じた進行中ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>進行中ラベル。</returns>
    private static string GetStartupProgressRunningLabel(StartupProgressOperationKind operationKind)
    {
        switch (operationKind)
        {
            case StartupProgressOperationKind.ReloadFiles:
                return BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_files;
            case StartupProgressOperationKind.ReloadTables:
                return BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_tables;
            default:
                return BeMusicSeeker.Properties.Resources.Statusbar_progress_startup;
        }
    }

    /// <summary>
    /// operation 種別に応じた完了ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>完了ラベル。</returns>
    private static string GetStartupProgressCompletedLabel(StartupProgressOperationKind operationKind)
    {
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_reload;
    }

    /// <summary>
    /// operation 種別に応じた失敗ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>失敗ラベル。</returns>
    private static string GetStartupProgressFailedLabel(StartupProgressOperationKind operationKind)
    {
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_reload;
    }

    /// <summary>
    /// 現在の未完了フェーズに対応するサブラベルを返します。
    /// </summary>
    /// <param name="state">進捗状態。</param>
    /// <returns>サブラベル。</returns>
    private static string GetStartupProgressSubLabel(StartupProgressState state)
    {
        if (!IsStartupProgressLibraryLoadCompleted(state))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_library_load;
        }
        if (!IsStartupProgressUiPrepareCompleted(state))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ui_prepare;
        }
        if ((state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) == 0)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ui_prepare;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistEntriesHydrationDone))
        {
            return "プレイリスト読込";
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartInfoHydrationDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info_load + " [" + state.ChartInfoHydrationAppliedCount + "/" + state.ChartInfoHydrationTotalCount + "]";
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartInfoBackfillDone))
        {
            string fileName = string.IsNullOrWhiteSpace(state.ChartInfoBackfillCurrentPath) ? string.Empty : Path.GetFileName(state.ChartInfoBackfillCurrentPath);
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info + " [" + state.ChartInfoBackfillProcessedCount + "/" + state.ChartInfoBackfillTotalCount + "] " + fileName;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartDigestBackfillDone))
        {
            string fileName = string.IsNullOrWhiteSpace(state.ChartDigestBackfillCurrentPath) ? string.Empty : Path.GetFileName(state.ChartDigestBackfillCurrentPath);
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info + " [" + state.ChartDigestBackfillProcessedCount + "/" + state.ChartDigestBackfillTotalCount + "] " + fileName;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistReferenceApplied) || !IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ExternalPlaylistSyncDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_ref;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ScoreHydrationDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_score_hydration;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.RankingRefreshDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ranking_refresh;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.MaintenanceDeferredDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_maintenance;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_background;
    }

    /// <summary>
    /// ライブラリ読込フェーズが完了済みかどうかを返します。
    /// </summary>
    private static bool IsStartupProgressLibraryLoadCompleted(StartupProgressState state)
    {
        if (state.OperationKind == StartupProgressOperationKind.Startup)
        {
            return (state.CompletedPhases & StartupProgressPhase.StartupReadyData) != 0;
        }
        return (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
    }

    /// <summary>
    /// 画面準備フェーズが完了済みかどうかを返します。
    /// </summary>
    private static bool IsStartupProgressUiPrepareCompleted(StartupProgressState state)
    {
        if (state.OperationKind == StartupProgressOperationKind.Startup)
        {
            return (state.CompletedPhases & StartupProgressPhase.StartupReadyUi) != 0;
        }
        return (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
    }

    /// <summary>
    /// プレイリスト参照/保守更新フェーズが完了済みかどうかを返します。
    /// </summary>
    private static bool IsStartupProgressReferencePhaseCompleted(StartupProgressState state)
    {
        return IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistReferenceApplied) && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ExternalPlaylistSyncDone) && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.MaintenanceDeferredDone);
    }

    private static int CountExpectedStartupProgressPhases(StartupProgressState state)
    {
        int count = 0;
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyData, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyUi, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyOperable, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.RankingRefreshDone, ref count);
        return count;
    }

    private static int CountCompletedExpectedStartupProgressPhases(StartupProgressState state)
    {
        int count = 0;
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyData, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyUi, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyOperable, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.RankingRefreshDone, ref count);
        return count;
    }

    private static void CountExpectedStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase, ref int count)
    {
        if (IsStartupProgressPhaseExpected(state, phase))
        {
            count++;
        }
    }

    private static void CountCompletedExpectedStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase, ref int count)
    {
        if (IsStartupProgressPhaseExpected(state, phase) && (state.CompletedPhases & phase) != 0)
        {
            count++;
        }
    }

    private static bool AreExpectedStartupProgressPhasesCompleted(StartupProgressState state)
    {
        StartupProgressPhase expected = state.ExpectedPhases;
        return expected == StartupProgressPhase.None || (state.CompletedPhases & expected) == expected;
    }

    /// <summary>
    /// 指定フェーズが待機対象かどうかを返します。
    /// </summary>
    private static bool IsStartupProgressPhaseExpected(StartupProgressState state, StartupProgressPhase phase)
    {
        return (state.ExpectedPhases & phase) != 0;
    }

    /// <summary>
    /// 指定フェーズが完了済み、または待機対象外かどうかを返します。
    /// </summary>
    private static bool IsStartupProgressPhaseCompletedOrNotExpected(StartupProgressState state, StartupProgressPhase phase)
    {
        return !IsStartupProgressPhaseExpected(state, phase) || (state.CompletedPhases & phase) != 0;
    }

    /// <summary>
    /// deferred playlist 参照要求を現在の operation 進捗へ関連付けるかどうかを返します。
    /// </summary>
    private static bool ShouldTrackStartupProgressPlaylistReference(string reason, StartupProgressOperationKind operationKind)
    {
        switch (operationKind)
        {
            case StartupProgressOperationKind.Startup:
                return string.Equals(reason, "Initialize", StringComparison.Ordinal) || string.Equals(reason, "DeferredExternalSync:Initialize", StringComparison.Ordinal);
            case StartupProgressOperationKind.ReloadFiles:
                return string.Equals(reason, "ReloadFiles", StringComparison.Ordinal);
            case StartupProgressOperationKind.ReloadTables:
                return string.Equals(reason, "DeferredExternalSync:ReloadTables", StringComparison.Ordinal);
            default:
                return false;
        }
    }

    /// <summary>
    /// deferred 外部プレイリスト同期要求を現在の operation 進捗へ関連付けるかどうかを返します。
    /// </summary>
    private static bool ShouldTrackStartupProgressExternalSync(string reason, StartupProgressOperationKind operationKind)
    {
        switch (operationKind)
        {
            case StartupProgressOperationKind.Startup:
                return string.Equals(reason, "Initialize", StringComparison.Ordinal);
            case StartupProgressOperationKind.ReloadTables:
                return string.Equals(reason, "ReloadTables", StringComparison.Ordinal);
            default:
                return false;
        }
    }

    private List<BMSPackage> getBMSPackages(ref List<BeMusicSeeker.Models.BMSFile> bmsFiles, bool isInstalled = false)
    {
        DispatcherCollection<BMSPackage> source = (isInstalled ? BMSPackagesInstalled : BMSPackagesPending);
        List<BeMusicSeeker.Models.BMSFile> list = new List<BeMusicSeeker.Models.BMSFile>();
        List<BMSPackage> list2 = new List<BMSPackage>();
        foreach (BeMusicSeeker.Models.BMSFile file in bmsFiles)
        {
            BMSPackage bMSPackage = source.FirstOrDefault((BMSPackage p) => p.BMSFiles.Contains(file));
            if (bMSPackage == null)
            {
                list.Add(file);
            }
            else
            {
                list2.Add(bMSPackage);
            }
        }
        bmsFiles = list;
        return list2.Distinct().ToList();
    }

    public void ForceInstallBMSFiles(IEnumerable<BMSPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<BMSPackage> list = packages.Where((BMSPackage pkg) => pkg != null).ToList();
        RunPendingInstallMutation(delegate
        {
            files.InstallBMSPackagesForce(list);
        }, list.SelectMany((BMSPackage p) => p.BMSFiles), UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree);
    }

    public void ForceInstallBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        List<BeMusicSeeker.Models.BMSFile> bmsFiles2 = bmsFiles.Where((BeMusicSeeker.Models.BMSFile bmsInfo) => bmsInfo != null).ToList();
        lock (lockCopyFile)
        {
            List<BMSPackage> bMSPackages = getBMSPackages(ref bmsFiles2);
            ForceInstallBMSFiles(bMSPackages);
        }
    }

    public void ManualInstallBMSFiles(IEnumerable<BMSPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<BMSPackage> list = packages.Where((BMSPackage pkg) => pkg != null).ToList();
        RunPendingInstallMutation(delegate
        {
            files.InstallBMSPackagesToEstimatedDir(list);
        }, list.SelectMany((BMSPackage p) => p.BMSFiles), UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree);
    }

    private void SetPlaylistSummaryMode(bool enabled)
    {
        IsPlaylistSummaryMode = enabled;
        if (enabled)
        {
            GridHeaderText = BeMusicSeeker.Properties.Resources.Playlist_summary_header;
        }
        else
        {
            GridHeaderText = string.Empty;
            GridSummaryText = string.Empty;
            ConsumeDeferredPlaylistSummaryRefresh();
            ConsumeDeferredPlaylistSummaryPresentationRefresh();
        }
    }

    private void RefreshPlaylistSummaryIfVisible()
    {
        RefreshPlaylistSummaryDataIfVisible();
    }

    private void RefreshPlaylistSummaryDataIfVisible()
    {
        if (!IsPlaylistSummaryMode)
        {
            return;
        }
        // 表示へ戻るだけなら table count cache は残す。
        // count cache key には playlist entry revision と owned snapshot version が含まれるため、
        // playlist 内容や所持状態が変わった場合は自然に miss する。
        InvalidatePlaylistSummaryRowsCache(invalidateTableCountCache: false);
        if (IsUiUpdateSuppressed())
        {
            RequestDeferredPlaylistSummaryRefresh();
            return;
        }
        RebuildPlaylistSummaryView();
    }

    private void RefreshPlaylistSummaryPresentationIfVisible()
    {
        if (!IsPlaylistSummaryMode)
        {
            return;
        }
        if (IsUiUpdateSuppressed())
        {
            RequestDeferredPlaylistSummaryPresentationRefresh();
            return;
        }
        ApplyPlaylistSummaryPresentation();
    }

    public void SelectPlaylistSummary()
    {
        SetPlaylistSummaryMode(enabled: true);
        RefreshPlaylistSummaryPresentationIfVisible();
    }

    public void RebuildPlaylistSummaryView(bool runAsync = true)
    {
        Action action = delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            BMSLibrary.PlaylistSummaryOwnedHashSnapshot playlistSummaryOwnedHashSnapshot;
            int tableCount;
            int entryScanCount;
            int unloadedTableCount;
            int summaryCacheHitCount;
            int summaryCacheMissCount;
            List<PlaylistSummaryRow> rows = BuildPlaylistSummaryRows(out playlistSummaryOwnedHashSnapshot, out tableCount, out entryScanCount, out unloadedTableCount, out summaryCacheHitCount, out summaryCacheMissCount);
            long buildMs = stopwatch.ElapsedMilliseconds;
            if (unloadedTableCount == 0)
            {
                SetPlaylistSummaryRowsCache(rows);
            }
            string sortColumn = PlaylistSummarySortParameters?.ColumnsName ?? nameof(PlaylistSummaryRow.Name);
            string sortDirection = PlaylistSummarySortParameters?.Direction.ToString() ?? ListSortDirection.Ascending.ToString();
            LogMainViewBuild("playlist_summary_build tableCount=" + tableCount + " unloadedTableCount=" + unloadedTableCount + " entryScanCount=" + entryScanCount + " rawCount=" + rows.Count + " buildMs=" + buildMs + " ownedMd5Count=" + (playlistSummaryOwnedHashSnapshot?.Md5Hashes?.Count ?? 0) + " ownedSha256Count=" + (playlistSummaryOwnedHashSnapshot?.Sha256Hashes?.Count ?? 0) + " ownedSnapshotVersion=" + (playlistSummaryOwnedHashSnapshot?.Version ?? 0) + " ownedHashBuildMs=" + (playlistSummaryOwnedHashSnapshot?.BuildElapsedMs ?? 0L) + " summaryCacheHit=false tableCacheHit=" + summaryCacheHitCount + " tableCacheMiss=" + summaryCacheMissCount + " sortColumn=" + sortColumn + " sortDirection=" + sortDirection);
            LogMainViewBuild("playlist_summary_cache tableCount=" + tableCount + " entryScanCount=" + entryScanCount + " cacheHit=" + summaryCacheHitCount + " cacheMiss=" + summaryCacheMissCount + " elapsedMs=" + buildMs);
            ApplyPlaylistSummaryPresentation(rows, stopwatch, buildMs);
        };
        if (!runAsync)
        {
            action();
        }
        else
        {
            Task.Run(action).Logging("RebuildPlaylistSummaryView");
        }
    }

    private List<PlaylistSummaryRow> BuildPlaylistSummaryRows(out BMSLibrary.PlaylistSummaryOwnedHashSnapshot playlistSummaryOwnedHashSnapshot, out int tableCount, out int entryScanCount, out int unloadedTableCount, out int summaryCacheHitCount, out int summaryCacheMissCount)
    {
        List<PlaylistSummaryRow> rows = new List<PlaylistSummaryRow>();
        entryScanCount = 0;
        summaryCacheHitCount = 0;
        summaryCacheMissCount = 0;
        Dictionary<string, PlaylistSyncRuntimeStatus> playlistSyncStatusSnapshot = GetPlaylistSyncStatusSnapshot();
        playlistSummaryOwnedHashSnapshot = files?.GetPlaylistSummaryOwnedHashSnapshot();
        HashSet<string> ownedMd5Hashes = playlistSummaryOwnedHashSnapshot?.Md5Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ownedSha256Hashes = playlistSummaryOwnedHashSnapshot?.Sha256Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ownedSnapshotVersion = playlistSummaryOwnedHashSnapshot?.Version ?? 0;
        List<BMSTable> tablesSnapshot = new List<BMSTable>();
        unloadedTableCount = 0;
        if (tables != null)
        {
            tables.AcquireReaderLockBMSTables();
            try
            {
                tablesSnapshot = BMSTables.Where((BMSTable t) => t != null).OrderBy((BMSTable t) => t.name ?? string.Empty).ToList();
            }
            finally
            {
                tables.FreeReaderLockBMSTables();
            }
        }
        tableCount = tablesSnapshot.Count;
        foreach (BMSTable table in tablesSnapshot)
        {
            bool entriesLoaded = table.ArePlaylistEntriesLoaded;
            PlaylistSummaryCountResult countResult = new PlaylistSummaryCountResult();
            string countCacheKey = entriesLoaded ? GetPlaylistSummaryTableCountCacheKey(table, ownedSnapshotVersion) : null;
            if (entriesLoaded && TryGetPlaylistSummaryTableCountCache(countCacheKey, out countResult))
            {
                summaryCacheHitCount++;
            }
            else if (entriesLoaded)
            {
                countResult = CalculatePlaylistSummaryCounts(table.GetEntriesExceptDummy(), ownedMd5Hashes, ownedSha256Hashes);
                SetPlaylistSummaryTableCountCache(countCacheKey, countResult);
                summaryCacheMissCount++;
            }
            if (!entriesLoaded)
            {
                unloadedTableCount++;
            }
            entryScanCount += countResult.ScannedEntries;
            int totalCharts = countResult.TotalCharts;
            int ownedCharts = countResult.OwnedCharts;
            PlaylistSyncRuntimeStatus playlistSyncRuntimeStatus = GetPlaylistSyncRuntimeStatus(table, playlistSyncStatusSnapshot);
            string statusDetail = playlistSyncRuntimeStatus.Detail;
            if (!entriesLoaded)
            {
                statusDetail = string.IsNullOrWhiteSpace(statusDetail) ? "プレイリスト読込中" : (statusDetail + " / プレイリスト読込中");
            }
            rows.Add(new PlaylistSummaryRow
            {
                PlaylistId = table.playlist_id,
                Name = table.name ?? string.Empty,
                Symbol = table.symbol ?? string.Empty,
                LastUpdate = table.last_update,
                TotalCharts = totalCharts,
                OwnedCharts = ownedCharts,
                MissingCharts = totalCharts - ownedCharts,
                OwnedRatio = ((totalCharts == 0) ? 0.0 : ((double)ownedCharts * 100.0 / (double)totalCharts)),
                LinkUri = table.Page_url ?? table.GetAbsoluteHeaderUrl(),
                IsExternalSync = table.is_external_sync,
                Status = playlistSyncRuntimeStatus.StatusText,
                StatusDetail = statusDetail,
                StatusSortOrder = playlistSyncRuntimeStatus.StatusSortOrder,
                HasFailureStatus = playlistSyncRuntimeStatus.HasFailureStatus,
                IsRootFolder = table.is_root_folder,
                TableRef = table
            });
        }
        return rows;
    }

    private static string GetPlaylistSummaryTableCountCacheKey(BMSTable table, int ownedSnapshotVersion)
    {
        if (table == null)
        {
            return null;
        }
        string tableKey = table.playlist_id.HasValue
            ? ("id:" + table.playlist_id.Value.ToString(CultureInfo.InvariantCulture))
            : ("name:" + (table.name ?? string.Empty) + "|symbol:" + (table.symbol ?? string.Empty));
        return tableKey
            + "|entryRevision:" + table.PlaylistEntriesRevision.ToString(CultureInfo.InvariantCulture)
            + "|owned:" + ownedSnapshotVersion.ToString(CultureInfo.InvariantCulture)
            + "|state:" + table.PlaylistEntriesLoadState;
    }

    private bool TryGetPlaylistSummaryTableCountCache(string key, out PlaylistSummaryCountResult countResult)
    {
        countResult = default(PlaylistSummaryCountResult);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        lock (lockPlaylistSummaryRowsCache)
        {
            if (!playlistSummaryTableCountCache.TryGetValue(key, out PlaylistSummaryTableCountCacheEntry entry))
            {
                return false;
            }
            countResult = entry.CountResult;
            return true;
        }
    }

    private void SetPlaylistSummaryTableCountCache(string key, PlaylistSummaryCountResult countResult)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        lock (lockPlaylistSummaryRowsCache)
        {
            playlistSummaryTableCountCache[key] = new PlaylistSummaryTableCountCacheEntry
            {
                CountResult = countResult
            };
            if (playlistSummaryTableCountCache.Count > 10000)
            {
                playlistSummaryTableCountCache.Clear();
            }
        }
    }

    private void ApplyPlaylistSummaryPresentation()
    {
        List<PlaylistSummaryRow> cachedRows = GetPlaylistSummaryRowsCacheSnapshot();
        if (cachedRows == null)
        {
            RebuildPlaylistSummaryView();
            return;
        }
        ApplyPlaylistSummaryPresentation(cachedRows, Stopwatch.StartNew(), 0L);
    }

    private void ApplyPlaylistSummaryPresentation(List<PlaylistSummaryRow> rawRows, Stopwatch stopwatch, long buildMs)
    {
        List<PlaylistSummaryRow> safeRawRows = rawRows ?? new List<PlaylistSummaryRow>();
        PlaylistSummaryPresentationResult presentationResult = BuildPlaylistSummaryPresentationRows(safeRawRows, PlaylistSummaryKeywordFilter, PlaylistSummaryOwnedFilter, PlaylistSummarySortParameters, false);
        Action reflect = delegate
        {
            PlaylistSummaryView = new ObservableCollection<PlaylistSummaryRow>(presentationResult.Rows);
            GridSummaryText = string.Format(BeMusicSeeker.Properties.Resources.Playlist_summary_format, presentationResult.Rows.Sum((PlaylistSummaryRow r) => r.TotalCharts), presentationResult.Rows.Count);
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            reflect();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
        }
        Interlocked.Exchange(ref lastPlaylistSummaryBuildElapsedMs, stopwatch.ElapsedMilliseconds);
        if (buildMs > 0)
        {
            LogMainViewBuild("playlist_summary_present inputCount=" + safeRawRows.Count + " filteredCount=" + presentationResult.FilteredCount + " viewCount=" + presentationResult.Rows.Count + " filterMs=" + presentationResult.FilterElapsedMs + " sortMs=" + presentationResult.SortElapsedMs + " totalMs=" + stopwatch.ElapsedMilliseconds + " sortColumn=" + presentationResult.SortColumn + " sortDirection=" + presentationResult.SortDirection + " sortProfile=" + presentationResult.SortProfile + " sortEngine=" + (presentationResult.UseLegacySort ? "legacy" : "fast") + " buildMs=" + buildMs + " summaryCacheHit=false");
        }
        else
        {
            LogMainViewBuild("playlist_summary_present inputCount=" + safeRawRows.Count + " filteredCount=" + presentationResult.FilteredCount + " viewCount=" + presentationResult.Rows.Count + " filterMs=" + presentationResult.FilterElapsedMs + " sortMs=" + presentationResult.SortElapsedMs + " totalMs=" + stopwatch.ElapsedMilliseconds + " sortColumn=" + presentationResult.SortColumn + " sortDirection=" + presentationResult.SortDirection + " sortProfile=" + presentationResult.SortProfile + " sortEngine=" + (presentationResult.UseLegacySort ? "legacy" : "fast") + " summaryCacheHit=true");
        }
    }

    internal static PlaylistSummaryPresentationResult BuildPlaylistSummaryPresentationRows(IEnumerable<PlaylistSummaryRow> rows, string keywordFilter, PlaylistSummaryOwnedFilterType ownedFilter, cSortParameters sortParameters, bool useLegacySort)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<PlaylistSummaryRow> filteredRows = ApplyPlaylistSummaryFilters(rows, keywordFilter, ownedFilter).ToList();
        long filterElapsedMs = stopwatch.ElapsedMilliseconds;
        List<PlaylistSummaryRow> sortedRows = PlaylistSummarySortEngine.Sort(filteredRows, sortParameters, useLegacySort, out string sortProfile);
        long sortElapsedMs = stopwatch.ElapsedMilliseconds - filterElapsedMs;
        return new PlaylistSummaryPresentationResult
        {
            Rows = sortedRows,
            FilteredCount = filteredRows.Count,
            FilterElapsedMs = filterElapsedMs,
            SortElapsedMs = sortElapsedMs,
            SortProfile = sortProfile,
            SortColumn = sortParameters?.ColumnsName ?? nameof(PlaylistSummaryRow.Name),
            SortDirection = sortParameters?.Direction.ToString() ?? ListSortDirection.Ascending.ToString(),
            UseLegacySort = useLegacySort
        };
    }

    internal static PlaylistSummaryCountResult CalculatePlaylistSummaryCounts(IEnumerable<BMSTableEntry> entries, HashSet<string> ownedMd5Hashes, HashSet<string> ownedSha256Hashes)
    {
        PlaylistSummaryCountResult result = default(PlaylistSummaryCountResult);
        HashSet<string> safeOwnedMd5Hashes = ownedMd5Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> safeOwnedSha256Hashes = ownedSha256Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTableEntry entry in entries ?? Enumerable.Empty<BMSTableEntry>())
        {
            result.ScannedEntries++;
            if (entry == null || entry.is_removed)
            {
                continue;
            }
            bool hasMd5 = !string.IsNullOrWhiteSpace(entry.md5);
            bool hasSha256 = !string.IsNullOrWhiteSpace(entry.sha256);
            if (!hasMd5 && !hasSha256)
            {
                continue;
            }
            result.TotalCharts++;
            if (hasMd5)
            {
                if (safeOwnedMd5Hashes.Contains(entry.md5))
                {
                    result.OwnedCharts++;
                }
            }
            else if (safeOwnedSha256Hashes.Contains(entry.sha256))
            {
                result.OwnedCharts++;
            }
        }
        return result;
    }

    internal struct PlaylistSummaryCountResult
    {
        internal int ScannedEntries;

        internal int TotalCharts;

        internal int OwnedCharts;
    }

    private sealed class PlaylistSummaryTableCountCacheEntry
    {
        internal PlaylistSummaryCountResult CountResult;
    }

    internal struct PlaylistSummaryPresentationResult
    {
        internal List<PlaylistSummaryRow> Rows;

        internal int FilteredCount;

        internal long FilterElapsedMs;

        internal long SortElapsedMs;

        internal string SortProfile;

        internal string SortColumn;

        internal string SortDirection;

        internal bool UseLegacySort;
    }

    private IEnumerable<PlaylistSummaryRow> ApplyPlaylistSummaryFilters(IEnumerable<PlaylistSummaryRow> rows)
    {
        return ApplyPlaylistSummaryFilters(rows, PlaylistSummaryKeywordFilter, PlaylistSummaryOwnedFilter);
    }

    internal static IEnumerable<PlaylistSummaryRow> ApplyPlaylistSummaryFilters(IEnumerable<PlaylistSummaryRow> rows, string keywordFilter, PlaylistSummaryOwnedFilterType ownedFilter)
    {
        IEnumerable<PlaylistSummaryRow> source = rows ?? Enumerable.Empty<PlaylistSummaryRow>();
        string text = (keywordFilter ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            GridKeywordSearchQuery query = GridKeywordSearchQuery.Parse(text);
            source = source.Where((PlaylistSummaryRow row) => query.MatchesPlaylistSummary(row));
        }
        return source.Where((PlaylistSummaryRow row) => IsPlaylistSummaryRowMatchedOwnedFilter(row, ownedFilter));
    }

    private bool IsPlaylistSummaryRowMatchedOwnedFilter(PlaylistSummaryRow row)
    {
        return IsPlaylistSummaryRowMatchedOwnedFilter(row, PlaylistSummaryOwnedFilter);
    }

    internal static bool IsPlaylistSummaryRowMatchedOwnedFilter(PlaylistSummaryRow row, PlaylistSummaryOwnedFilterType ownedFilter)
    {
        if (row == null)
        {
            return false;
        }
        switch (ownedFilter)
        {
            case PlaylistSummaryOwnedFilterType.OwnedComplete:
                return row.TotalCharts > 0 && row.OwnedCharts == row.TotalCharts;
            case PlaylistSummaryOwnedFilterType.OwnedIncomplete:
                return row.TotalCharts == 0 || row.OwnedCharts < row.TotalCharts;
            default:
                return true;
        }
    }

    public void ApplyPlaylistSummaryFlags(IEnumerable<PlaylistSummaryRow> rows, bool? isExternalSync = null, bool? isRootFolder = null)
    {
        if (rows == null || tables == null)
        {
            return;
        }
        List<PlaylistSummaryRow> list = rows.Where((PlaylistSummaryRow r) => r?.TableRef != null).GroupBy((PlaylistSummaryRow r) => r.TableRef).Select((IGrouping<BMSTable, PlaylistSummaryRow> g) => g.First()).ToList();
        if (list.Count == 0)
        {
            return;
        }
        bool flag = false;
        foreach (PlaylistSummaryRow item in list)
        {
            BMSTable tableRef = item.TableRef;
            bool flag2 = false;
            bool? nullable = null;
            if (isExternalSync.HasValue)
            {
                bool flag3 = tableRef.is_external_sync != isExternalSync.Value;
                if (isExternalSync.Value)
                {
                    tableRef.EnableExternalSync();
                }
                else
                {
                    tableRef.DisableExternalSync();
                }
                flag2 = flag2 || flag3;
            }
            if (isRootFolder.HasValue && tableRef.is_root_folder != isRootFolder.Value)
            {
                tableRef.is_root_folder = isRootFolder.Value;
                nullable = isRootFolder;
                flag2 = true;
            }
            if (!flag2)
            {
                continue;
            }
            tables.ReOutputCustomFolderAndCommitToDB(tableRef);
            if (Settings.Default.OperationModeLR2DB && nullable.HasValue && lr2config != null && !string.IsNullOrWhiteSpace(tableRef.Output_dir))
            {
                string customFolderOutputDirectory = BMSPlaylist.GetCustomFolderOutputDirectory(tableRef);
                List<string> bMSSearchDirectories = lr2config.GetBMSSearchDirectories();
                if (nullable.Value)
                {
                    lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Union(new string[1] { customFolderOutputDirectory }).Distinct(StringComparer.OrdinalIgnoreCase));
                }
                else
                {
                    lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Except(new string[1] { customFolderOutputDirectory }, StringComparer.OrdinalIgnoreCase));
                }
                lr2config.Save();
            }
            flag = true;
        }
        if (flag)
        {
            RefreshPlaylistSummaryIfVisible();
        }
    }

    public void ResyncPlaylists(IEnumerable<PlaylistSummaryRow> rows)
    {
        ResyncPlaylistsAsync(rows).GetAwaiter().GetResult();
    }

    public Task ResyncPlaylistsAsync(IEnumerable<PlaylistSummaryRow> rows)
    {
        if (rows == null)
        {
            return Task.CompletedTask;
        }
        List<BMSTable> tablesToResync = rows.Where((PlaylistSummaryRow r) => r?.TableRef != null).Select((PlaylistSummaryRow r) => r.TableRef).Distinct().ToList();
        return ResyncPlaylistsAsync(tablesToResync);
    }

    public void ResyncPlaylists(IEnumerable<BMSTable> tablesToResync)
    {
        ResyncPlaylistsAsync(tablesToResync).GetAwaiter().GetResult();
    }

    public async Task ResyncPlaylistsAsync(IEnumerable<BMSTable> tablesToResync)
    {
        if (tablesToResync == null || tables == null || files == null)
        {
            return;
        }
        List<BMSTable> list = tablesToResync.Where((BMSTable t) => t != null).Distinct().Where(delegate(BMSTable item)
        {
            Uri uri2 = item.Page_url ?? item.Header_url;
            return uri2 != null && uri2.IsAbsoluteUri;
        }).ToList();
        if (list.Count == 0)
        {
            return;
        }
        PlaylistReloadOperationKind playlistReloadOperationKind = (list.Count > 1) ? PlaylistReloadOperationKind.ManualFullReload : PlaylistReloadOperationKind.SinglePlaylistReload;
        Stopwatch playlistReloadStopwatch = Stopwatch.StartNew();
        BeginPlaylistSyncProgressOperation();
        try
        {
            LogPlaylistReload("playlist_reload_operation started operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=manual_resync tableCount=" + list.Count);
            int completed = 0;
            UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
            {
                IsActive = true,
                TotalTableCount = list.Count,
                CompletedTableCount = 0,
                CurrentTableName = string.Empty,
                CurrentUri = null
            });
            foreach (BMSTable item in list)
            {
                Uri uri = item.Page_url ?? item.Header_url;
                UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
                {
                    IsActive = true,
                    TotalTableCount = list.Count,
                    CompletedTableCount = completed,
                    CurrentTableName = item.name,
                    CurrentUri = uri
                });
                try
                {
                    DateTime last_update = item.last_update;
                    List<BMSTableEntry> oldEntriesSnapshot;
                    tables.EnsurePlaylistEntriesLoaded(item, "ResyncPlaylistsAsync");
                    using (item.ReaderWriterLock.GetReaderGuard())
                    {
                        oldEntriesSnapshot = item.entries.ToList();
                    }
                    BMSTable bMSTable = await tables.ResetBMSTableAsync(item, uri);
                    files.ReplaceReferenceBMSTable(item, bMSTable, oldEntriesSnapshot);
                    UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateSuccess(item, bMSTable, uri, bMSTable.last_update != last_update));
                }
                catch (Exception ex)
                {
                    NLogWrapper.FileLogger?.Warn(ex, "playlist_manual_resync_failed table=" + (item?.name ?? string.Empty) + " uri=" + (uri?.ToString() ?? string.Empty));
                    UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateFailure(item, uri, ex));
                    ShowPlaylistLoadFailure(ex);
                }
                finally
                {
                    completed++;
                    UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
                    {
                        IsActive = true,
                        TotalTableCount = list.Count,
                        CompletedTableCount = completed,
                        CurrentTableName = item.name,
                        CurrentUri = uri
                    });
                }
            }
            RefreshPlaylistSummaryIfVisible();
            RefreshPlaylistDetailAfterReloadIfVisible();
            bool cleanupQueued = QueuePlaylistReloadCleanup(playlistReloadOperationKind, list.Count);
            LogPlaylistReload("playlist_reload_operation completed operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=manual_resync tableCount=" + list.Count + " summaryRebuildMs=" + Interlocked.Read(ref lastPlaylistSummaryBuildElapsedMs) + " detailRefreshMs=" + Interlocked.Read(ref lastPlaylistDetailBuildElapsedMs) + " cleanupQueued=" + cleanupQueued.ToString().ToLowerInvariant() + " elapsedMs=" + playlistReloadStopwatch.ElapsedMilliseconds);
        }
        finally
        {
            EndPlaylistSyncProgressOperation();
        }
    }

    public void ManualInstallBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        List<BeMusicSeeker.Models.BMSFile> bmsFiles2 = bmsFiles.Where((BeMusicSeeker.Models.BMSFile bmsInfo) => bmsInfo != null).ToList();
        lock (lockCopyFile)
        {
            List<BMSPackage> bMSPackages = getBMSPackages(ref bmsFiles2);
            ManualInstallBMSFiles(bMSPackages);
        }
    }

    public void RemoveBMSPackagesPendingAll()
    {
        RunPendingInstallMutation(delegate
        {
            files.RemoveBMSPackagesPendingAll();
        });
    }

    public void RemoveBMSPackagesPending(IEnumerable<BMSPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        RunPendingInstallMutation(delegate
        {
            files.RemoveBMSPackagesPending(packages);
        });
    }

    public void RemoveBMSPackagesPending(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        List<BeMusicSeeker.Models.BMSFile> bmsFiles2 = bmsFiles.Where((BeMusicSeeker.Models.BMSFile f) => f != null).ToList();
        List<BMSPackage> bMSPackages = getBMSPackages(ref bmsFiles2);
        RemoveBMSPackagesPending(bMSPackages);
    }

    public List<BMSPackage> GetPendingPackagesContainingOnlyInstalledCharts()
    {
        if (files == null)
        {
            return new List<BMSPackage>();
        }
        lock (lockCopyFile)
        {
            return files.GetPendingPackagesContainingOnlyInstalledCharts();
        }
    }

    public List<BeMusicSeeker.Models.BMSFile> GetPendingBMSFilesSnapshot()
    {
        if (files == null)
        {
            return new List<BeMusicSeeker.Models.BMSFile>();
        }
        lock (lockCopyFile)
        {
            return files.GetPendingBMSFilesSnapshot();
        }
    }

    public void DeletePendingPackageSources(IEnumerable<BMSPackage> packages, bool sendToRecycleBin = true, CancellationToken token = default(CancellationToken), Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        RunPendingInstallMutation(delegate
        {
            files.DeletePendingPackageSources(packages, sendToRecycleBin, token, onEachProcessed);
        });
    }

    public void RenamePendingZeroNoteChartsToInvalidExtensions(IEnumerable<BeMusicSeeker.Models.BMSFile> targetFiles, CancellationToken token = default(CancellationToken), Action onEachProcessed = null)
    {
        List<BeMusicSeeker.Models.BMSFile> list = ((targetFiles != null) ? targetFiles.Where((BeMusicSeeker.Models.BMSFile f) => f != null).ToList() : GetPendingBMSFilesSnapshot());
        RunPendingInstallMutation(delegate
        {
            files.RenamePendingZeroNoteChartsToInvalidExtensions(list, token, onEachProcessed);
        }, list);
    }

    public PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(IEnumerable<BMSPackage> packages, CancellationToken token = default(CancellationToken), Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<BMSPackage> list = packages.Where((BMSPackage pkg) => pkg != null).ToList();
        List<BeMusicSeeker.Models.BMSFile> list2 = list.SelectMany((BMSPackage pkg) => pkg.BMSFiles ?? new List<BeMusicSeeker.Models.BMSFile>()).Where((BeMusicSeeker.Models.BMSFile f) => f != null).ToList();
        return RunPendingInstallMutation(() => files.OverwritePendingInstalledOnlyPackagesResources(list, token, onEachProcessed), list2);
    }

    public void RemoveBMSPackagesInstalledAll()
    {
        if (files != null)
        {
            files.RemoveBMSPackagesInstalledAll();
        }
    }

    public void RemoveBMSPackagesInstalled(IEnumerable<BMSPackage> packages)
    {
        if (files != null)
        {
            if (packages == null)
            {
                throw new ArgumentNullException("packages");
            }
            files.RemoveBMSPackagesInstalled(packages);
        }
    }

    public void RemoveBMSPackagesInstalled(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        List<BeMusicSeeker.Models.BMSFile> bmsFiles2 = bmsFiles.Where((BeMusicSeeker.Models.BMSFile f) => f != null).ToList();
        List<BMSPackage> bMSPackages = getBMSPackages(ref bmsFiles2, isInstalled: true);
        RemoveBMSPackagesInstalled(bMSPackages);
    }

    public void SearchCorrectInstallationDirectoryBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        SearchCorrectInstallationDirectoryCharts(bmsFiles);
    }

    public void SearchCorrectInstallationDirectoryCharts(IEnumerable<BeMusicSeeker.Models.BMSFile> chartFiles)
    {
        if (files != null)
        {
            if (chartFiles == null)
            {
                throw new ArgumentNullException("chartFiles");
            }
            files.SearchCorrectInstallationDirectory(chartFiles);
        }
    }

    public void RemoveInstallDestination(IEnumerable<BMSPackage> packages)
    {
        if (files != null)
        {
            if (packages == null)
            {
                throw new ArgumentNullException("packages");
            }
            List<BMSPackage> list = packages.Where((BMSPackage f) => f != null).ToList();
            for (int num = 0; num < list.Count; num++)
            {
                files.RemoveInstallDestination(list[num].BMSFiles);
            }
        }
    }

    public void RemoveInstallDestination(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        ClearInstallDestinationForCharts(bmsFiles);
    }

    public void ClearInstallDestinationForPendingCharts(IEnumerable<BeMusicSeeker.Models.BMSFile> chartFiles)
    {
        ClearInstallDestinationForCharts(chartFiles);
    }

    public void ClearInstallDestinationForCharts(IEnumerable<BeMusicSeeker.Models.BMSFile> chartFiles)
    {
        if (files != null)
        {
            if (chartFiles == null)
            {
                throw new ArgumentNullException("chartFiles");
            }
            List<BeMusicSeeker.Models.BMSFile> bmsFiles2 = chartFiles.Where((BeMusicSeeker.Models.BMSFile f) => f != null).ToList();
            List<BMSPackage> bMSPackages = getBMSPackages(ref bmsFiles2);
            for (int num = 0; num < bMSPackages.Count; num++)
            {
                files.RemoveInstallDestination(bMSPackages[num].BMSFiles);
            }
            files.RemoveInstallDestination(bmsFiles2);
        }
    }

    public bool SetPendingInstallDestination(BeMusicSeeker.Models.BMSFile bmsFile, string destinationDirectory)
    {
        if (files == null)
        {
            return false;
        }
        if (bmsFile == null)
        {
            throw new ArgumentNullException("bmsFile");
        }
        lock (lockCopyFile)
        {
            return files.SetPendingInstallDestination(bmsFile, destinationDirectory);
        }
    }

    public bool TryGetInstalledDirectoryByHash(string hash, out string installDir)
    {
        installDir = null;
        if (files == null)
        {
            return false;
        }
        return files.TryGetInstalledDirectoryByHash(hash, out installDir);
    }

    private void ShowPlaylistLoadFailure(Exception ex)
    {
        string message = BeMusicSeeker.Properties.Resources.Msg_failed_load_playlist;
        if (ex != null && !string.IsNullOrWhiteSpace(ex.Message))
        {
            message = message + Environment.NewLine + ex.Message;
        }
        base.Messenger.Raise(new ConfirmationMessage(message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
    }

    internal void RegistrateExternalPlaylistBMSTable(Uri uri)
    {
        RegistrateExternalPlaylistBMSTableAsync(uri).GetAwaiter().GetResult();
    }

    internal async Task RegistrateExternalPlaylistBMSTableAsync(Uri uri)
    {
        BeginPlaylistSyncProgressOperation();
        BMSTable table;
        try
        {
            UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
            {
                IsActive = true,
                TotalTableCount = 1,
                CompletedTableCount = 0,
                CurrentTableName = string.Empty,
                CurrentUri = uri
            });
            table = await tables.RegistrateExternalTableAsync(uri);
        }
        catch (InvalidOperationException ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "playlist_register_failed uri=" + (uri?.ToString() ?? string.Empty));
            ShowPlaylistLoadFailure(ex);
            return;
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "playlist_register_failed uri=" + (uri?.ToString() ?? string.Empty));
            ShowPlaylistLoadFailure(ex);
            return;
        }
        finally
        {
            UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
            {
                IsActive = true,
                TotalTableCount = 1,
                CompletedTableCount = 1,
                CurrentTableName = string.Empty,
                CurrentUri = uri
            });
            EndPlaylistSyncProgressOperation();
        }
        tables.AcquireReaderLockBMSTables();
        files.AddReferenceBMSTables(table);
        tables.FreeReaderLockBMSTables();
        UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateSuccess(table, table, uri, updated: false));
        RefreshPlaylistSummaryIfVisible();
    }

    private void UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult result)
    {
        if (result == null)
        {
            return;
        }
        PlaylistSyncRuntimeStatus playlistSyncRuntimeStatus = PlaylistSyncStatusMapper.Create(result, DateTime.Now);
        string playlistSyncStatusKey = GetPlaylistSyncStatusKey(result.ResultTable ?? result.SourceTable);
        string playlistSyncStatusKey2 = GetPlaylistSyncStatusKey(result.SourceTable);
        lock (lockPlaylistSyncStatuses)
        {
            if (!string.IsNullOrWhiteSpace(playlistSyncStatusKey2) && !string.Equals(playlistSyncStatusKey2, playlistSyncStatusKey, StringComparison.OrdinalIgnoreCase))
            {
                playlistSyncStatuses.Remove(playlistSyncStatusKey2);
            }
            if (!string.IsNullOrWhiteSpace(playlistSyncStatusKey))
            {
                playlistSyncStatuses[playlistSyncStatusKey] = playlistSyncRuntimeStatus;
            }
            else if (!string.IsNullOrWhiteSpace(playlistSyncStatusKey2))
            {
                playlistSyncStatuses[playlistSyncStatusKey2] = playlistSyncRuntimeStatus;
            }
        }
    }

    private Dictionary<string, PlaylistSyncRuntimeStatus> GetPlaylistSyncStatusSnapshot()
    {
        lock (lockPlaylistSyncStatuses)
        {
            return new Dictionary<string, PlaylistSyncRuntimeStatus>(playlistSyncStatuses, StringComparer.OrdinalIgnoreCase);
        }
    }

    private PlaylistSyncRuntimeStatus GetPlaylistSyncRuntimeStatus(BMSTable table, IDictionary<string, PlaylistSyncRuntimeStatus> snapshot)
    {
        if (table == null || snapshot == null)
        {
            return PlaylistSyncStatusMapper.CreateNone();
        }
        string playlistSyncStatusKey = GetPlaylistSyncStatusKey(table);
        if (string.IsNullOrWhiteSpace(playlistSyncStatusKey))
        {
            return PlaylistSyncStatusMapper.CreateNone();
        }
        if (snapshot.TryGetValue(playlistSyncStatusKey, out var value) && value != null)
        {
            return value;
        }
        return PlaylistSyncStatusMapper.CreateNone();
    }

    private string GetPlaylistSyncStatusKey(BMSTable table)
    {
        if (table == null)
        {
            return null;
        }
        if (table.playlist_id.HasValue)
        {
            return "id:" + table.playlist_id.Value;
        }
        Uri uri = table.Page_url ?? table.Header_url;
        if (uri != null && uri.IsAbsoluteUri)
        {
            return "uri:" + uri.AbsoluteUri;
        }
        if (!string.IsNullOrWhiteSpace(table.name))
        {
            return "name:" + table.name;
        }
        return null;
    }

    internal void RenameFolderBMSTable(BMSTable bmsTable, string foldeNameBefore, string folderNameAfter)
    {
        if (bmsTable.is_external_sync)
        {
            base.Messenger.Raise(new ConfirmationMessage("プレイリストが外部同期モードになっているため" + Environment.NewLine + "フォルダ名を変更することは出来ません。", "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        else
        {
            tables.RenameFolderBMSTable(bmsTable, foldeNameBefore, folderNameAfter);
        }
    }

    internal void RemoveFolderBMSTable(BMSTable bmsTable, string folderNameDelete)
    {
        if (bmsTable.is_external_sync)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_remove_playlist_folder, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        else
        {
            tables.RemoveFolderBMSTable(bmsTable, folderNameDelete);
        }
    }

    internal void CreateNewFolderBMSTable(BMSTable bmsTable)
    {
        if (bmsTable.is_external_sync)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_create_playlist_folder, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        else
        {
            tables.CreateNewFolderBMSTable(bmsTable);
        }
    }

    internal void ExportBMSTable(BMSTable bmsTable, string fileNameHeader, string fileNameData)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        if (fileNameHeader == null)
        {
            throw new ArgumentNullException("fileNameHeader");
        }
        if (fileNameData == null)
        {
            throw new ArgumentNullException("fileNameData");
        }
        tables?.EnsurePlaylistEntriesLoaded(bmsTable, "ExportBMSTable");
        bool flag = false;
        Uri data_url = null;
        if (string.IsNullOrWhiteSpace(bmsTable.Data_url?.ToString()))
        {
            data_url = bmsTable.Data_url;
            bmsTable.Data_url = new Uri(Path.GetFileName(fileNameData), UriKind.Relative);
            flag = true;
        }
        string contents = bmsTable.HeaderToJson();
        dynamic val = bmsTable.DataToJson();
        try
        {
            File.WriteAllText(fileNameHeader, contents);
            File.WriteAllText(fileNameData, val);
        }
        catch
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_save_playlist, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        finally
        {
            if (flag)
            {
                bmsTable.Data_url = data_url;
            }
        }
    }

    internal void AddEntriesToFolderBMSTable(IEnumerable<object> rows, BMSTable bmsTable, string folderName = "")
    {
        if (folderName == null)
        {
            folderName = string.Empty;
        }
        if (rows == null)
        {
            throw new ArgumentNullException(nameof(rows));
        }
        if (bmsTable.is_external_sync)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_add_playlist_entry, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            return;
        }
        tables.EnsurePlaylistEntriesLoaded(bmsTable, "MainWindowViewModel.AddEntriesToFolderBMSTable");
        List<object> sourceRows = rows.Where((object row) => row != null).ToList();
        if (sourceRows.Count == 0)
        {
            return;
        }
        if (sourceRows.All(GridRowResolver.IsPlaylistRow))
        {
            List<BMSTableEntry> list = sourceRows.Select(GridRowResolver.GetPlaylistEntry).Where((BMSTableEntry entry) => entry != null && entry.parent == bmsTable).ToList();
            List<BMSTableEntry> second = list.Where((BMSTableEntry entry) => string.Equals(entry.folder ?? string.Empty, folderName, StringComparison.Ordinal)).ToList();
            if (bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder && string.IsNullOrWhiteSpace(folderName))
            {
                tables.RemoveEntriesBMSTable(list, bmsTable, commitFlag: false);
            }
            else
            {
                sourceRows = sourceRows.Where((object row) => !second.Contains(GridRowResolver.GetPlaylistEntry(row))).ToList();
                if (sourceRows.Count == 0)
                {
                    return;
                }
                tables.RemoveEntriesBMSTable(list.Except(second), bmsTable, commitFlag: false);
            }
        }
        List<BeMusicSeeker.Models.BMSFile> resolvedFiles = sourceRows.Select(GridRowResolver.GetRealBmsFile).Where((BeMusicSeeker.Models.BMSFile file) => file != null).ToList();
        if (bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder && string.IsNullOrWhiteSpace(folderName))
        {
            List<object> playlistEntryRows = sourceRows.Where((object row) => GridRowResolver.GetRealBmsFile(row) == null).ToList();
            List<BeMusicSeeker.Models.BMSFile> source = sourceRows.Select(GridRowResolver.GetRealBmsFile).Where((BeMusicSeeker.Models.BMSFile file) => file != null).ToList();
            tables.AddEntriesToFolderBMSTable(playlistEntryRows.Select((object row) => GridRowResolver.GetPlaylistEntry(row)?.Duplicate()).Where((BMSTableEntry entry) => entry != null), bmsTable, folderName, commitFlag: false);
            if (BMSFiles == null)
            {
                return;
            }
            foreach (IGrouping<string, BeMusicSeeker.Models.BMSFile> item in source.GroupBy((BeMusicSeeker.Models.BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path), StringComparer.OrdinalIgnoreCase).ToList())
            {
                List<string> md5sInTheSameDir = null;
                foreach (BeMusicSeeker.Models.BMSFile item2 in item)
                {
                    md5sInTheSameDir = files.GetMD5sOfTheSameSong(item2);
                    if (md5sInTheSameDir != null)
                    {
                        break;
                    }
                }
                if (md5sInTheSameDir == null)
                {
                    md5sInTheSameDir = new List<string>();
                }
                string text = null;
                if (md5sInTheSameDir.Count > 0)
                {
                    text = bmsTable.folder_list.Where((string folder) => !string.IsNullOrWhiteSpace(folder)).FirstOrDefault((string folder) => (from e in bmsTable.entries
                                                                                                                                                where e.folder == folder
                                                                                                                                                select e.md5).ToList().Intersect(md5sInTheSameDir, StringComparer.OrdinalIgnoreCase).Any());
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = BMSLibrary.GetLCSBMSInfo(item.Select((BeMusicSeeker.Models.BMSFile f) => f.Title));
                    text = tables.CreateNewFolderBMSTable(bmsTable, text, commitFlag: false);
                }
                tables.AddEntriesToFolderBMSTable(item.Select((BeMusicSeeker.Models.BMSFile f) => GridRowResolver.GetPlaylistEntry(f)?.Duplicate() ?? new BMSTableEntry(f)
                {
                    Org_md5 = md5sInTheSameDir
                }), bmsTable, text, commitFlag: false);
            }
        }
        else
        {
            ParallelQuery<BMSTableEntry> bmsEntries = from row in sourceRows.AsParallel()
                                                      let entry = GridRowResolver.GetPlaylistEntry(row)
                                                      let file = GridRowResolver.GetRealBmsFile(row)
                                                      where entry != null || file != null
                                                      select (entry != null) ? entry.Duplicate() : new BMSTableEntry(file)
                                                      {
                                                          Org_md5 = files.GetMD5sOfTheSameSong(file)
                                                      };
            tables.AddEntriesToFolderBMSTable(bmsEntries, bmsTable, folderName, commitFlag: false);
        }
        tables.ReOutputCustomFolderAndCommitToDB(bmsTable);
        updateBMSFilesViewForPlaylist(bmsTable);
        tables.AcquireReaderLockBMSTables();
        files.AddReferenceBMSTables(bmsTable, resolvedFiles);
        tables.FreeReaderLockBMSTables();
    }

    internal void DeleteBMSTableEntries(IEnumerable<BMSTableEntry> bmsEntries, BMSTable bmsTable)
    {
        if (bmsTable.is_external_sync)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_remove_playlist_entry, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            return;
        }
        tables.RemoveEntriesBMSTable(bmsEntries, bmsTable);
        updateBMSFilesViewForPlaylist(bmsTable);
        tables.AcquireReaderLockBMSTables();
        files.RemoveReferenceBMSTables(bmsTable, bmsEntries);
        tables.FreeReaderLockBMSTables();
    }

    private void updateBMSFilesViewForPlaylist(BMSTable bmsTableUpdated)
    {
        RefreshPlaylistSummaryIfVisible();
        BMSTable bMSTable = null;
        if (treeViewFilterTypeSelected == viewUpdateMode.PlaylistFilterSelected)
        {
            if (treeViewFilterParameterSelected == null)
            {
                return;
            }
            bMSTable = ((Tuple<BMSTable, string>)treeViewFilterParameterSelected).Item1;
        }
        else
        {
            if (treeViewFilterTypeSelected != viewUpdateMode.PlaylistNotOwnedFilterSelected || treeViewFilterParameterSelected == null)
            {
                return;
            }
            bMSTable = treeViewFilterParameterSelected as BMSTable;
        }
        if (bMSTable != null && bMSTable == bmsTableUpdated)
        {
            IncrementPlaylistContentRevision("playlist_updated");
            makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
        }
    }

    internal void RemoveBMSTable(BMSTable bmsTable)
    {
        if (Settings.Default.OperationModeLR2DB && !string.IsNullOrWhiteSpace(bmsTable.Output_dir))
        {
            tables.RemoveCustomFolder(bmsTable);
        }
        tables.RemoveBMSTable(bmsTable);
        if (Settings.Default.OperationModeLR2DB && bmsTable.is_root_folder && !string.IsNullOrWhiteSpace(bmsTable.Output_dir))
        {
            string customFolderOutputDirectory = BMSPlaylist.GetCustomFolderOutputDirectory(bmsTable);
            lr2config.RemoveBMSSearchDirectories(new string[1] { customFolderOutputDirectory });
            lr2config.Save();
        }
        files.RemoveReferenceBMSTables(bmsTable);
    }

    internal BMSTable CreateBMSTable()
    {
        return tables.CreateBMSTable();
    }

    internal void BackupBMSTables(string fileName)
    {
        if (BMSTables == null)
        {
            return;
        }
        if (BMSTables.Count() == 0)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_warn_playlist_backup, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OK, "ConfirmationDialog"));
            return;
        }
        try
        {
            string playlistDump = tables.GetPlaylistDump();
            File.WriteAllText(fileName, playlistDump);
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_success_playlist_backup, BeMusicSeeker.Properties.Resources.Success, MessageBoxImage.Asterisk, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        catch (Exception ex)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_playlist_backup + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Failure, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
        }
    }

    internal void RestoreBMSTables(string fileName)
    {
        if (BMSTables == null)
        {
            return;
        }
        string lines = File.ReadAllText(fileName, Encoding.UTF8);
        Action action = delegate
        {
            try
            {
                if (!LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0)))
                {
                    throw new TimeoutException(BeMusicSeeker.Properties.Resources.Msg_error_timeout_dblock_restore);
                }
                tables.LoadPlaylistDump(lines);
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_success_playlist_restore, BeMusicSeeker.Properties.Resources.Success, MessageBoxImage.Asterisk, MessageBoxButton.OK, "ConfirmationDialog"));
            }
            catch (Exception ex)
            {
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            }
        };
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            System.Windows.Application.Current.Dispatcher.Invoke(action, DispatcherPriority.Normal);
        }
    }

    internal void UninstallAllData()
    {
        if (string.IsNullOrWhiteSpace(Settings.Default.LR2SongDBPath) || !File.Exists(Settings.Default.LR2SongDBPath))
        {
            return;
        }
        Action action = delegate
        {
            try
            {
                if (!LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0)))
                {
                    throw new TimeoutException(BeMusicSeeker.Properties.Resources.Msg_error_timeout_dblock_uninstall);
                }
                using (LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(Settings.Default.LR2SongDBPath))
                {
                    string savepoint = lR2SongDBExtended.SaveTransactionPoint();
                    try
                    {
                        lR2SongDBExtended.Uninstall();
                        lR2SongDBExtended.Commit();
                    }
                    catch (Exception)
                    {
                        lR2SongDBExtended.RollbackTo(savepoint);
                        throw;
                    }
                }
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_success_uninstall, BeMusicSeeker.Properties.Resources.Success, MessageBoxImage.Asterisk, MessageBoxButton.OK, "ConfirmationDialog"));
            }
            catch (Exception ex2)
            {
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_uninstall + Environment.NewLine + Environment.NewLine + ex2.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            }
        };
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            System.Windows.Application.Current.Dispatcher.Invoke(action, DispatcherPriority.Normal);
        }
    }

    public void MergeBMSDirectory(string src, string dst)
    {
        lock (lockCopyFile)
        {
            PlayEndBMSFile(closeProcess: true);
            files.MergeBMSDirectory(src, dst);
        }
    }

    public void FixInstallationDirectoryBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        FixInstallationDirectoryCharts(bmsFiles);
    }

    public void FixInstallationDirectoryCharts(IEnumerable<BeMusicSeeker.Models.BMSFile> chartFiles)
    {
        lock (lockCopyFile)
        {
            if (chartFiles == null)
            {
                throw new ArgumentNullException("chartFiles");
            }
            stopPlayingBMSFile(chartFiles);
            files.FixInstallationDirectory(chartFiles);
        }
    }

    public void RemoveBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(bmsFiles);
            files.RemoveBMSFiles(bmsFiles);
        }
    }

    internal void RemoveLibraryCharts(IEnumerable<ChartOperationTarget> targets)
    {
        List<LibraryChartRef> charts = ToLibraryChartRefs(targets, ChartOperationCapabilities.RemoveFromLibrary).ToList();
        if (charts.Count == 0)
        {
            return;
        }
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(charts.Where((LibraryChartRef chart) => chart.Kind == LibraryChartKind.Bms).Select((LibraryChartRef chart) => chart.BmsFile));
            files.RemoveLibraryCharts(charts);
        }
    }

    public void RemovePendingBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, bool sendToRecycleBin = true, bool deleteContainingPackageFoldersWhenNoBms = false)
    {
        RemovePendingCharts(bmsFiles, sendToRecycleBin, deleteContainingPackageFoldersWhenNoBms);
    }

    internal void RemovePendingCharts(IEnumerable<ChartOperationTarget> targets, bool sendToRecycleBin = true, bool deleteContainingPackageFoldersWhenNoBms = false)
    {
        RemovePendingCharts(
            (targets ?? Enumerable.Empty<ChartOperationTarget>())
            .Where((ChartOperationTarget target) => target != null && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
            .Select(ToCompatibilityChartFile)
            .Where((BeMusicSeeker.Models.BMSFile file) => file != null),
            sendToRecycleBin,
            deleteContainingPackageFoldersWhenNoBms);
    }

    public void RemovePendingCharts(IEnumerable<BeMusicSeeker.Models.BMSFile> chartFiles, bool sendToRecycleBin = true, bool deleteContainingPackageFoldersWhenNoBms = false)
    {
        RunPendingInstallMutation(delegate
        {
            files.RemovePendingBMSFiles(chartFiles, sendToRecycleBin, deleteContainingPackageFoldersWhenNoBms);
        }, chartFiles);
    }

    public void RecheckZeroNoteWarnings()
    {
        lock (lockCopyFile)
        {
            if (files == null)
            {
                return;
            }
            files.RecheckZeroNoteWarnings();
        }
    }

    public void RenameBMSFilesExtensions(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string newExt)
    {
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(bmsFiles);
            files.RenameBMSFilesExtensions(bmsFiles, newExt, true);
        }
    }

    public void RenamePendingBMSFilesExtensions(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string newExt)
    {
        RunPendingInstallMutation(delegate
        {
            files.RenamePendingBMSFilesExtensions(bmsFiles, newExt);
        }, bmsFiles);
    }

    public void RenameBMSFolder(BeMusicSeeker.Models.BMSFile bmsFile, string newFolder)
    {
        if (bmsFile == null || string.IsNullOrWhiteSpace(newFolder))
        {
            return;
        }
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(new BeMusicSeeker.Models.BMSFile[1] { bmsFile });
            string directoryNameSimple = DirectoryExt.GetDirectoryNameSimple(bmsFile.path);
            if (!string.IsNullOrWhiteSpace(directoryNameSimple) && Directory.Exists(directoryNameSimple))
            {
                files.RenameBMSFolder(directoryNameSimple, newFolder, false);
                SyncBmsonLibraryRowCacheWithoutRebuild();
            }
        }
    }

    public void AutoRenameAllBMSFolder(string parentDir = null)
    {
        if (BMSFiles == null)
        {
            return;
        }
        IEnumerable<BeMusicSeeker.Models.BMSFile> enumerable = BMSFiles;
        lock (lockCopyFile)
        {
            PlayEndBMSFile(closeProcess: true);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                enumerable = enumerable.Where((BeMusicSeeker.Models.BMSFile f) => f.path.StartsWith(parentDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }
            files.AutoRenameBMSFolder(enumerable);
            SyncBmsonLibraryRowCacheWithoutRebuild();
        }
    }

    public void AutoRenameBMSFolder(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(bmsFiles);
            files.AutoRenameBMSFolder(bmsFiles);
            SyncBmsonLibraryRowCacheWithoutRebuild();
        }
    }

    public void MoveBMSFolder(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string newParentDirectory)
    {
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(bmsFiles);
            files.MoveBMSRootFolder(bmsFiles, newParentDirectory, false);
        }
    }

    internal void MoveLibraryCharts(IEnumerable<ChartOperationTarget> targets, string newParentDirectory)
    {
        List<LibraryChartRef> charts = ToLibraryChartRefs(targets, ChartOperationCapabilities.MoveInLibrary)
            .Where((LibraryChartRef chart) => !string.IsNullOrWhiteSpace(chart.Path))
            .ToList();
        if (charts.Count == 0 || string.IsNullOrWhiteSpace(newParentDirectory))
        {
            return;
        }
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(charts.Where((LibraryChartRef chart) => chart.Kind == LibraryChartKind.Bms).Select((LibraryChartRef chart) => chart.BmsFile));
            files.MoveLibraryRootFolder(charts, newParentDirectory, false);
        }
    }

    private static IEnumerable<LibraryChartRef> ToLibraryChartRefs(IEnumerable<ChartOperationTarget> targets, ChartOperationCapabilities requiredCapability)
    {
        return (targets ?? Enumerable.Empty<ChartOperationTarget>())
            .Where((ChartOperationTarget target) => target != null && target.HasCapability(requiredCapability))
            .Select(ToLibraryChartRef)
            .Where((LibraryChartRef chart) => chart != null);
    }

    private static BeMusicSeeker.Models.BMSFile ToCompatibilityChartFile(ChartOperationTarget target)
    {
        return ToLibraryChartRef(target)?.ToCompatibilityBmsFile();
    }

    private static LibraryChartRef ToLibraryChartRef(ChartOperationTarget target)
    {
        if (target?.Chart == null)
        {
            return null;
        }
        if (target.Chart.BmsFile != null)
        {
            return LibraryChartRef.FromBmsFile(target.Chart.BmsFile);
        }
        if (target.Chart.BmsonSong != null)
        {
            return LibraryChartRef.FromBmsonSong(target.Chart.BmsonSong);
        }
        return null;
    }

    /// <summary>
    /// 選択された BMS ファイル群について、LR2IR (Lunatic Rave 2 Internet Ranking) サーバーから
    /// その BMS ファイルのランキングデータ・キャッシュ情報をダウンロード・更新します。
    /// </summary>
    /// <param name="bmsFiles">LR2IR キャッシュ取得の対象となる BMS ファイルのリスト。</param>
    public void GetLR2IRCacheBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        lock (lockCopyFile)
        {
            if (bmsFiles == null)
            {
                throw new ArgumentNullException("bmsFiles");
            }
            try
            {
                List<string> list = bmsFiles.Select((BeMusicSeeker.Models.BMSFile f) => f.hash).ToList();
                List<BMSLibrary.IRDataCacheInfo> iRDataNeedUpdates = files.GetIRDataNeedUpdates(list);
                if (iRDataNeedUpdates.Count > 0)
                {
                    if (DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_download_ranking_cache + Environment.NewLine + Environment.NewLine + BeMusicSeeker.Properties.Resources.Download + ": " + iRDataNeedUpdates.Count + Environment.NewLine + BeMusicSeeker.Properties.Resources.Skip + ": " + (list.Count - iRDataNeedUpdates.Count) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Size + ": " + FileSizeHelper.GetReadableFileSize(iRDataNeedUpdates.Select((BMSLibrary.IRDataCacheInfo c) => c.size).Sum()), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk, MessageBoxResult.OK) == MessageBoxResult.OK)
                    {
                        List<BMSLibrary.IRDataCacheInfo> list2 = files.DownloadIRData(iRDataNeedUpdates);
                        DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_download_completed + Environment.NewLine + Environment.NewLine + BeMusicSeeker.Properties.Resources.Success + ": " + (iRDataNeedUpdates.Count - list2.Count) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Failure + ": " + list2.Count, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
                    }
                }
                else
                {
                    DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_ranking_cache_notfound, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                }
            }
            catch (InvalidOperationException)
            {
                DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_warn_cache_download, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            }
            catch (Exception ex2)
            {
                DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_error_cache_download + Environment.NewLine + Environment.NewLine + ex2.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
        }
    }

    /// <summary>
    /// 指定されたハッシュ群をキーに LR2IR キャッシュを更新します。
    /// 実ファイル未所持の playlist 行でもランキングデータ更新を行えるようにします。
    /// </summary>
    /// <param name="hashes">更新対象の MD5 ハッシュ一覧。</param>
    public void GetLR2IRCacheHashes(IEnumerable<string> hashes)
    {
        lock (lockCopyFile)
        {
            if (hashes == null)
            {
                throw new ArgumentNullException(nameof(hashes));
            }
            try
            {
                List<string> normalizedHashes = hashes.Where((string hash) => !string.IsNullOrWhiteSpace(hash)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (normalizedHashes.Count == 0)
                {
                    return;
                }
                List<BMSLibrary.IRDataCacheInfo> iRDataNeedUpdates = files.GetIRDataNeedUpdates(normalizedHashes);
                if (iRDataNeedUpdates.Count > 0)
                {
                    if (DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_download_ranking_cache + Environment.NewLine + Environment.NewLine + BeMusicSeeker.Properties.Resources.Download + ": " + iRDataNeedUpdates.Count + Environment.NewLine + BeMusicSeeker.Properties.Resources.Skip + ": " + (normalizedHashes.Count - iRDataNeedUpdates.Count) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Size + ": " + FileSizeHelper.GetReadableFileSize(iRDataNeedUpdates.Select((BMSLibrary.IRDataCacheInfo c) => c.size).Sum()), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk, MessageBoxResult.OK) == MessageBoxResult.OK)
                    {
                        List<BMSLibrary.IRDataCacheInfo> list = files.DownloadIRData(iRDataNeedUpdates);
                        DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_download_completed + Environment.NewLine + Environment.NewLine + BeMusicSeeker.Properties.Resources.Success + ": " + (iRDataNeedUpdates.Count - list.Count) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Failure + ": " + list.Count, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
                    }
                }
                else
                {
                    DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_ranking_cache_notfound, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                }
            }
            catch (InvalidOperationException)
            {
                DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_warn_cache_download, BeMusicSeeker.Properties.Resources.Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            }
            catch (Exception ex)
            {
                DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_error_cache_download + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
        }
    }

    /// <summary>
    /// 実体譜面または互換 row から LR2IR キャッシュを取得します。
    /// </summary>
    /// <param name="bmsFile">対象譜面。</param>
    /// <param name="seaarchAggressively">積極探索するか。</param>
    /// <returns>IR 情報キャッシュ。取得不可時は null。</returns>
    public BMSLibrary.IRSongInfo GetLR2IRSongInfoCache(BeMusicSeeker.Models.BMSFile bmsFile, bool seaarchAggressively = false)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException();
        }
        string text = GridRowResolver.GetHash(bmsFile);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = GridRowResolver.GetLr2BmsId(bmsFile);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("MD5/LR2BMSID not found: " + bmsFile.path);
        }
        try
        {
            return files.GetIRSongInfoCache(text, seaarchAggressively);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 一覧 row から LR2IR キャッシュを取得します。
    /// lightweight playlist row は hash / lr2_bmsid snapshot を使って解決します。
    /// </summary>
    /// <param name="row">対象 row。</param>
    /// <param name="seaarchAggressively">積極探索するか。</param>
    /// <returns>IR 情報キャッシュ。取得不可時は null。</returns>
    public BMSLibrary.IRSongInfo GetLR2IRSongInfoCache(object row, bool seaarchAggressively = false)
    {
        if (row == null)
        {
            throw new ArgumentNullException(nameof(row));
        }
        BeMusicSeeker.Models.BMSFile realFile = GridRowResolver.GetRealBmsFile(row);
        if (realFile != null)
        {
            return GetLR2IRSongInfoCache(realFile, seaarchAggressively);
        }
        string key = GridRowResolver.GetHash(row);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = GridRowResolver.GetLr2BmsId(row);
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("MD5/LR2BMSID not found.");
        }
        try
        {
            return files.GetIRSongInfoCache(key, seaarchAggressively);
        }
        catch
        {
            return null;
        }
    }

    public void SetYoutubeToBrowserSource(string id)
    {
        if (id == null)
        {
            throw new ArgumentNullException("id");
        }
        BrowserHtml = "\r\n<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<!DOCTYPE html\r\n     PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"\r\n    \"DTD/xhtml1-strict.dtd\">\r\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"en\" lang=\"en\">\r\n  <head>\r\n    <meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"/>\r\n  </head>\r\n  <body style=\"margin:0px;padding:0px;overflow:hidden;\">\r\n    <iframe width=\"100%\" height=\"256\" src=\"https://www.youtube.com/embed/" + id + "?rel=0&autoplay=1&vq=highres&iv_load_policy=3&disablekb=1&modestbranding=1&autohide=1&cc_load_policy=0\" frameborder=\"0\" allowfullscreen></iframe>\r\n  </body>\r\n</html>\r\n";
    }

    public void SetNiconicoToBrowserSource(string id)
    {
        if (id == null)
        {
            throw new ArgumentNullException("id");
        }
        BrowserHtml = "\r\n<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<!DOCTYPE html\r\n     PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"\r\n    \"DTD/xhtml1-strict.dtd\">\r\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"en\" lang=\"en\">\r\n  <head>\r\n  <meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"/>\r\n  </head>\r\n  <body style=\"margin:0px;padding:0px;overflow:hidden;\">\r\n    <div style=\"padding:0;background-color:black;margin:0 auto;text-align: center;\"><script type=\"text/javascript\" src=\"http://ext.nicovideo.jp/thumb_watch/" + id + "?h=256\"></script></div>\r\n  </body>\r\n</html>\r\n";
    }

    /// <summary>
    /// 選択された複数のBMSファイルをスコアビューワー（外部連携サイト、通常は BMS Score Viewer）に登録・アップロードします。
    /// 既に登録済みの場合はスキップし、未登録の場合はファイルをアップロードして閲覧可能な状態にします。
    /// 複数ファイルの一括登録時にはユーザーに確認ダイアログを表示します。
    /// </summary>
    /// <param name="bmsFiles">登録対象となるBMSファイルのリスト。</param>
    /// <returns>
    /// 最後に処理されたファイルが正しく登録（または取得）できた場合、そのスコアビューワーの閲覧用URLを返します。
    /// キャンセル時や、対象ファイル全てで処理に失敗した場合は null を返します。
    /// </returns>
    public string RegisterBMSFilesToScoreViewer(List<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException(nameof(bmsFiles));
        }
        return RegisterScoreViewerTargets(bmsFiles.Select((BeMusicSeeker.Models.BMSFile bmsFile) => new ScoreViewerTarget(bmsFile.hash, bmsFile.path, bmsFile.Title)).ToList());
    }

    /// <summary>
    /// Score Viewer 登録対象を hash/path/title ベースで登録します。
    /// 未所持 playlist 行では path が null でも閲覧 URL を返せます。
    /// </summary>
    /// <param name="targets">登録対象の軽量ターゲット一覧。</param>
    /// <returns>最後に処理されたターゲットの閲覧用 URL。失敗時は null。</returns>
    internal string RegisterScoreViewerTargets(List<ScoreViewerTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }
        List<ScoreViewerTarget> normalizedTargets = targets.Where((ScoreViewerTarget target) => target != null && !string.IsNullOrWhiteSpace(target.Hash)).ToList();
        if (normalizedTargets.Count == 0)
        {
            return null;
        }
        ScoreViewerTarget lastTarget = normalizedTargets.Last();
        bool userConfirmedMultiRegister = false;
        string resultViewUrl = null;
        if (normalizedTargets.Count > 1)
        {
            ConfirmationMessage confirmationMessage = new ConfirmationMessage(
                BeMusicSeeker.Properties.Resources.Msg_register_chart + Environment.NewLine + Environment.NewLine + normalizedTargets.Count + " " + BeMusicSeeker.Properties.Resources.Num_chart,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxImage.Asterisk,
                MessageBoxButton.YesNo,
                "ConfirmationDialog");
            base.Messenger.Raise(confirmationMessage);
            if (confirmationMessage.Response != true)
            {
                return null;
            }
            userConfirmedMultiRegister = true;
        }
        foreach (ScoreViewerTarget target in normalizedTargets)
        {
            try
            {
                string currentFileHash = target.Hash;
                if (string.IsNullOrWhiteSpace(target.Path) || !File.Exists(target.Path))
                {
                    if (target == lastTarget)
                    {
                        resultViewUrl = scoreViewUrl + currentFileHash;
                    }
                    continue;
                }
                string statusJson = AppHttpClient.Shared.GetString(new Uri(scoreStatusUrl + currentFileHash), Encoding.UTF8);
                dynamic statusVal = DynamicJson.Parse(statusJson);
                if (statusVal.status == "OK")
                {
                    if (target == lastTarget)
                    {
                        resultViewUrl = scoreViewUrl + currentFileHash;
                    }
                    continue;
                }
                if (!userConfirmedMultiRegister && Settings.Default.ShowScoreViewerRegisterConfirmMsg)
                {
                    ConfirmationMessage uploadConfirmMessage = new ConfirmationMessage(
                        BeMusicSeeker.Properties.Resources.Msg_show_chart + Environment.NewLine + Environment.NewLine + (target.Title ?? string.Empty) + Environment.NewLine + "MD5: " + currentFileHash + Environment.NewLine + Environment.NewLine + "(" + BeMusicSeeker.Properties.Resources.Msg_hide_message + ")",
                        BeMusicSeeker.Properties.Resources.Confirm,
                        MessageBoxImage.Asterisk,
                        MessageBoxButton.YesNo,
                        "ConfirmationDialog");
                    base.Messenger.Raise(uploadConfirmMessage);
                    if (uploadConfirmMessage.Response == true)
                    {
                        userConfirmedMultiRegister = true;
                    }
                    else
                    {
                        continue;
                    }
                }
                string registerResponseJson = AppHttpClient.Shared.PostFile(new Uri(scoreRegisterUrl), target.Path, responseEncoding: Encoding.UTF8, headers: new Dictionary<string, string> { { "Accept", "application/json" } }, logErrorResponseBody: true);
                if (target == lastTarget)
                {
                    dynamic registerResponseVal = DynamicJson.Parse(registerResponseJson);
                    if (registerResponseVal.status == "OK")
                    {
                        currentFileHash = registerResponseVal.md5;
                    }
                    resultViewUrl = scoreViewUrl + currentFileHash;
                }
            }
            catch (Exception ex)
            {
                NLogWrapper.FileLogger?.Warn(ex, "score_viewer_upload_failed path=" + (target.Path ?? string.Empty) + " md5=" + (target.Hash ?? string.Empty));
            }
        }
        if (userConfirmedMultiRegister && !string.IsNullOrWhiteSpace(resultViewUrl))
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_success_register_chart, BeMusicSeeker.Properties.Resources.Information, MessageBoxImage.Asterisk, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        return resultViewUrl;
    }

    public void ConvertBMSToAudioFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string saveDir, CancellationToken token = default(CancellationToken), Action<bool> onEachCompleted = null)
    {
        if (!Directory.Exists(saveDir))
        {
            throw new DirectoryNotFoundException("Directory " + saveDir + " not found");
        }
        bmsFiles = bmsFiles.Materialize();
        PlayEndBMSFile(closeProcess: true);
        BassAudioPlayer.Frequency = Settings.Default.EncoderSampleRate;
        BassAudioPlayer.Format = Settings.Default.EncoderFormat;
        BassAudioWriter.EncoderDirectory = Settings.Default.EncoderExeDir;
        BassAudioWriter.Initialize();
        int num = 0;
        foreach (BeMusicSeeker.Models.BMSFile bmsFile in bmsFiles)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }
            bool obj = true;
            BMSAutoPlayWriter bMSAutoPlayWriter = null;
            try
            {
                num++;
                Ribbit.BMS.BMSFile bMSFile = new Ribbit.BMS.BMSFile(bmsFile.path);
                string text = new Dictionary<string, string>
                {
                    {
                        "%ARTIST%",
                        ((bMSFile.Artist.Trim() ?? string.Empty) + " " + (bMSFile.Subartist?.Trim() ?? string.Empty)).Trim()
                    },
                    {
                        "%TITLE%",
                        ((bMSFile.Title.Trim() ?? string.Empty) + " " + (bMSFile.Subtitle?.Trim() ?? string.Empty)).Trim()
                    },
                    {
                        "%GENRE%",
                        bMSFile.Genre.Trim() ?? string.Empty
                    },
                    {
                        "%NO%",
                        num.ToString().PadLeft(Math.Max(2, bmsFiles.Count().ToString().Length), '0')
                    },
                    {
                        "%FILE%",
                        Path.GetFileName(bmsFile.path)
                    },
                    { "%HASH%", bMSFile.Md5 }
                }.Aggregate(Settings.Default.EncodeFileNameFormat, (string i, KeyValuePair<string, string> r) => i.Replace(r.Key, r.Value)).NaturalNormalizationForFileName().ReplaceInvalidFileNameCharsByWide()
                    .RemoveInvalidFileNameChars()
                    .Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = num.ToString();
                }
                int num2 = saveDir.Length + text.Length;
                if (num2 > 240 && text.Length > num2 - 240 + 5)
                {
                    text = text.Substring(0, text.Length - (num2 - 235));
                }
                string filePathWithoutExtension = Path.Combine(saveDir, text);
                bMSAutoPlayWriter = new BMSAutoPlayWriter(bMSFile);
                if (!BassAudioWriter.IsEncoderAvailable(Settings.Default.Encoder))
                {
                    Settings.Default.Encoder = EncoderType.WAVE;
                }
                bMSAutoPlayWriter.LoadResources();
                bMSAutoPlayWriter.Write(Settings.Default.Encoder, Settings.Default.EncoderQuality, filePathWithoutExtension, Settings.Default.EncoderNormalization, Settings.Default.EncoderAmplifier);
            }
            catch (Exception ex)
            {
                obj = false;
                NLogWrapper.GetLogger()?.Warn(ex.ToString());
            }
            bMSAutoPlayWriter?.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            onEachCompleted?.Invoke(obj);
        }
        BassAudioPlayer.Free();
    }
}
