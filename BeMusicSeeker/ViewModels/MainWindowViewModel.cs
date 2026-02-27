using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using BeMusicSeeker.Models;
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

        private bool tempUseFastSortInDataGridExperimental;

        private bool tempKeepInstallablePackagesPending;

        private bool tempEnableSmartComponentOverwrite;

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

        /// <summary>
        /// BMS 一覧画面のソートで高速化実験経路を使うかどうかを取得または設定します。
        /// </summary>
        /// <remarks>
        /// 既定値は false で、従来の自然順ソートを維持します。
        /// </remarks>
        public bool UseFastSortInDataGridExperimental
        {
            get
            {
                return Settings.Default.UseFastSortInDataGridExperimental;
            }
            set
            {
                if (Settings.Default.UseFastSortInDataGridExperimental != value)
                {
                    Settings.Default.UseFastSortInDataGridExperimental = value;
                    RaisePropertyChanged("UseFastSortInDataGridExperimental");
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
            return Uri.TryCreate(value.OriginalString, UriKind.RelativeOrAbsolute, out value);
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
                    ownerViewModel.RaisePropertyChanged(() => ownerViewModel.BMSParentFolderList);
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
                    ownerViewModel.RaisePropertyChanged(() => ownerViewModel.BMSParentFolderList);
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
            tempUseFastSortInDataGridExperimental = Settings.Default.UseFastSortInDataGridExperimental;
            tempKeepInstallablePackagesPending = Settings.Default.KeepInstallablePackagesPending;
            tempEnableSmartComponentOverwrite = Settings.Default.EnableSmartComponentOverwrite;
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
        }

        public bool CheckValidation()
        {
            string errMsg;
            return CheckValidation(out errMsg);
        }

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
            Settings.Default.UseFastSortInDataGridExperimental = tempUseFastSortInDataGridExperimental;
            Settings.Default.KeepInstallablePackagesPending = tempKeepInstallablePackagesPending;
            Settings.Default.EnableSmartComponentOverwrite = tempEnableSmartComponentOverwrite;
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
            RaisePropertyChanged(() => UseFastSortInDataGridExperimental);
            RaisePropertyChanged(() => KeepInstallablePackagesPending);
            RaisePropertyChanged(() => EnableSmartComponentOverwrite);
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
                tableUpdatesAfterSaved();
                backupTableProperties();
                return true;
            }
            return false;
        }

        private void loadTableProperties()
        {
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
            temp_Page_url = bmsTable.Page_url;
        }

        private void tableUpdatesAfterSaved()
        {
            if ((!temp_is_external_sync && bmsTable.is_external_sync) || (bmsTable.is_external_sync && temp_compat_prefix != bmsTable.compat_prefix) || (bmsTable.is_external_sync && bmsTable.Page_url != null && temp_Page_url != null && bmsTable.Page_url.ToString() != temp_Page_url.ToString()))
            {
                Uri uri = bmsTable.Page_url ?? bmsTable.Header_url;
                if (uri != null && uri.IsAbsoluteUri)
                {
                    try
                    {
                        bmsTable = ownerViewModel.tables.ResetBMSTable(bmsTable, uri);
                    }
                    catch
                    {
                    }
                }
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
        public string ColumnsName = "";

        public ListSortDirection Direction;
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

    public enum MaintenanceFilterType
    {
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

    private int deferredPlaylistRefRequestedVersion;

    private bool deferredPlaylistRefRunning;

    private object lockDeferredPlaylistRef = new object();

    private int deferredExternalSyncRequestedVersion;

    private bool deferredExternalSyncRunning;

    private object lockDeferredExternalSync = new object();

    private bool deferredLibraryFolderTreeRefreshQueued;

    private object lockDeferredLibraryFolderTreeRefresh = new object();

    private Stopwatch startupReadyInstallStopwatch;

    private Stopwatch startupReadyOperableStopwatch;

    private bool startupReadyDataLogged;

    private bool startupReadyUiLogged;

    private string _WindowTitle = "BeMusicSeeker Unofficial Fork - ";

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesFolderView;

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesKeywordFilterView;

    private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesModeFilterView;

    private List<BeMusicSeeker.Models.BMSFile> _BMSFilesView = new List<BeMusicSeeker.Models.BMSFile>();

    private cSortParameters _SortParameters;

    private cSortParameters _PlaylistSummarySortParameters;

    private BeMusicSeeker.Models.BMSFile _NowPlayingBMS;

    private Uri _BrowserSource;

    private string _BrowserHtml;

    private int _SelectedIndexBMSFilesView;

    private dataGridColumnsSettings _ColumnsSettingsBMSFilesView;

    private List<BeMusicSeeker.Models.BMSFile> folderSortSourceSnapshot;

    private List<BeMusicSeeker.Models.BMSFile> folderSortResultSnapshot;

    private string folderSortColumnName;

    private ListSortDirection? folderSortDirection;

    private PlaylistSummaryColumnSettings _PlaylistSummaryColumnsSettings;

    private Visibility _ColumnSettingsVisibilityForPlaylist = Visibility.Collapsed;

    private ObservableCollection<PlaylistSummaryRow> _PlaylistSummaryView = new ObservableCollection<PlaylistSummaryRow>();

    private bool _IsPlaylistSummaryMode;

    private string _GridHeaderText = string.Empty;

    private string _GridSummaryText = string.Empty;

    private bool _IsPlaylistTreeExpanded = true;

    private ModeFilterType _ModeFilter = ModeFilterType.All;

    private string _KeywordFilter;

    private string _PlaylistSummaryKeywordFilter = string.Empty;

    private PlaylistSummaryOwnedFilterType _PlaylistSummaryOwnedFilter = PlaylistSummaryOwnedFilterType.All;

    private Func<BeMusicSeeker.Models.BMSFile, bool> _FolderFilter;

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

    private viewUpdateMode treeViewFilterTypeSelected = viewUpdateMode.FolderFilterSelected;

    private object treeViewFilterParameterSelected;

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

    private static bool IsSameReferenceSequence(List<BeMusicSeeker.Models.BMSFile> left, List<BeMusicSeeker.Models.BMSFile> right)
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

    private bool ConsumeDeferredPlaylistSummaryRefresh()
    {
        lock (lockUiSuppression)
        {
            bool result = deferredPlaylistSummaryRefreshRequested;
            deferredPlaylistSummaryRefreshRequested = false;
            return result;
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
                    if (refreshed)
                    {
                        RaisePropertyChanged(() => BMSParentFolderList);
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
        if (IsPlaylistSummaryMode && (((mask & (UiRefreshChannel.PlaylistTree | UiRefreshChannel.LibraryMainView)) != 0) || ConsumeDeferredPlaylistSummaryRefresh()))
        {
            RebuildPlaylistSummaryView();
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
        LogDeferredPlaylistReference("playlist_ref_deferred queue reason=" + reason + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        Task.Run(delegate
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
                    files.AddReferenceBMSTables(list, null, suppressFilePropertyChanged: true);
                    DispatcherHelper.UIDispatcher.BeginInvoke((Action)delegate
                    {
                        makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
                    });
                    LogDeferredPlaylistReference("playlist_ref_deferred done version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " refreshed=true");
                }
                catch (Exception ex)
                {
                    LogDeferredPlaylistReference("playlist_ref_deferred failed version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " message=" + ex.Message);
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
        }).Logging("ScheduleDeferredPlaylistReferenceApply");
    }

    private void StartDeferredExternalPlaylistSync(string reason, bool fromReloadTables, Action<BMSTable, bool, BMSTable> updateCallbackAction = null)
    {
        if (tables == null)
        {
            return;
        }
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
        LogDeferredExternalSync("deferred_external_sync queue reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        Task.Run(delegate
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
                    LogDeferredExternalSync("deferred_external_sync run reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + requestVersion);
                    List<Action<BMSTable, bool, BMSTable>> updateCallbackActions = null;
                    if (updateCallbackAction != null)
                    {
                        updateCallbackActions = new List<Action<BMSTable, bool, BMSTable>> { updateCallbackAction };
                    }
                    List<BMSTable> list = tables.UpdateBMSTables(reloadExtPlaylist: true, updateCallbackActions);
                    int num = list?.Count ?? 0;
                    ScheduleDeferredPlaylistReferenceApply("DeferredExternalSync:" + reason);
                    LogDeferredExternalSync("deferred_external_sync done reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " updatedCount=" + num);
                }
                catch (Exception ex)
                {
                    LogDeferredExternalSync("deferred_external_sync failed reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " message=" + ex.Message);
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
        }).Logging("StartDeferredExternalPlaylistSync");
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

    public IEnumerable<string> BMSParentFolderList
    {
        get
        {
            if (files != null)
            {
                return files.BMSParentFolderList.OrderBy((string s) => s);
            }
            return null;
        }
    }

    public List<BeMusicSeeker.Models.BMSFile> BMSFilesView
    {
        get
        {
            return _BMSFilesView;
        }
        set
        {
            if (_BMSFilesView != value)
            {
                if (value == null)
                {
                    _BMSFilesView = new List<BeMusicSeeker.Models.BMSFile>();
                }
                else
                {
                    _BMSFilesView = value;
                }
                RaisePropertyChanged("BMSFilesView");
            }
        }
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
            _ColumnsSettingsBMSFilesView = value;
            RaisePropertyChanged("ColumnsSettingsBMSFilesView");
            base.Messenger.Raise(new InteractionMessage("CallbackColumnsSetingsChanged"));
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
                _PlaylistSummaryView = value ?? new ObservableCollection<PlaylistSummaryRow>();
                RaisePropertyChanged("PlaylistSummaryView");
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
            }
        }
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

    public PlaylistSummaryColumnSettings PlaylistSummaryColumnsSettings
    {
        get
        {
            return _PlaylistSummaryColumnsSettings;
        }
        set
        {
            _PlaylistSummaryColumnsSettings = value;
            RaisePropertyChanged("PlaylistSummaryColumnsSettings");
            base.Messenger.Raise(new InteractionMessage("CallbackColumnsSetingsChanged"));
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
                makeBMSFilesView(viewUpdateMode.KeywordFilterUpdated);
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
                if (IsPlaylistSummaryMode)
                {
                    RefreshPlaylistSummaryIfVisible();
                }
            }
        }
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
                    RefreshPlaylistSummaryIfVisible();
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

    public MainWindowViewModel()
    {
        _IsPlaylistTreeExpanded = Settings.Default.StartupExpandPlaylistTree;
        settingDialog = new SettingDialogViewModel(this);
    }

    public async void ReloadTables()
    {
        if (!initializationCompleted)
        {
            return;
        }
        await _semaphore.WaitAsync();
        Action<BMSTable, bool, BMSTable> updateCallbackAction = delegate (BMSTable bmsTable, bool updated, BMSTable oldTable)
        {
            if (updated)
            {
                files.RemoveReferenceBMSTables(oldTable);
                files.AddReferenceBMSTables(bmsTable);
            }
        };
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
        catch
        {
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            _semaphore.Release();
        }
        if (scheduleDeferredExternalSync)
        {
            StartDeferredExternalPlaylistSync("ReloadTables", fromReloadTables: true, updateCallbackAction);
        }
    }

    public async void ReloadFiles()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "ReloadFiles");
        bool scheduleDeferredPlaylistRef = false;
        await _semaphore.WaitAsync();
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
            await Task.Run(delegate
            {
                files.Initialize(null, null, false);
            }).Logging("ReloadFiles");
            LogInitStage("files_initialize_done", "ReloadFiles");
            scheduleDeferredPlaylistRef = true;
            if (!TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                RaisePropertyChanged(() => BMSParentFolderList);
            }
        }
        catch
        {
            throw;
        }
        finally
        {
            EndUiUpdateSuppression();
            LogInitStage("ui_suppress_end_called", "ReloadFiles");
            _semaphore.Release();
        }
        if (scheduleDeferredPlaylistRef)
        {
            ScheduleDeferredPlaylistReferenceApply("ReloadFiles");
            LogInitStage("deferred_playlist_ref_queued", "ReloadFiles");
        }
    }

    public async void Initialize()
    {
        await _semaphore.WaitAsync();
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
            base.Messenger.Raise(new InteractionMessage("InitializationException"));
            return;
        }
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
            }
            else
            {
                files = new BMSLibrary(Settings.Default.LR2SongDBPath);
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
            DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
            Logger currentClassLogger = LogManager.GetCurrentClassLogger();
            string text3 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text3 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            base.Messenger.Raise(new InteractionMessage("InitializationException"));
            return;
        }
        ColumnsSettingsBMSFilesView = Settings.Default.StandardColumnsSettings;
        listenerForBMSLibrary = new PropertyChangedEventListener(files);
        listenerForBMSLibraryBMSPackagesPendingCollection = new CollectionChangedEventListener(files.BMSPackagesPending);
        listenerForBMSLibraryBMSPackagesInstalledCollection = new CollectionChangedEventListener(files.BMSPackagesInstalled);
        listenerForBMSPlaylist = new PropertyChangedEventListener(tables);
        listenerForBMSPlaylistBMSTablesCollection = new CollectionChangedEventListener(tables.BMSTables);
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFiles, delegate
        {
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                RefreshPlaylistSummaryIfVisible();
                return;
            }
            if (Enum.IsDefined(typeof(MaintenanceFilterType), (int)treeViewFilterTypeSelected))
            {
                MaintenanceFilterType type = (MaintenanceFilterType)treeViewFilterTypeSelected;
                ExecMaintenanceFilter(type);
            }
            else
            {
                makeBMSFilesView(viewUpdateMode.TreeViewFilterNotChanged);
            }
            RefreshPlaylistSummaryIfVisible();
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
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSPackagesPending, delegate
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
        });
        listenerForBMSLibraryBMSPackagesInstalledCollection.RegisterHandler(delegate
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
        });
        listenerForBMSLibraryBMSPackagesPendingCollection.RegisterHandler(delegate
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
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BMSParentFolderList, delegate
        {
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
        bool scheduleDeferredPlaylistRef = false;
        Thread.Yield();
        startupReadyInstallStopwatch = Stopwatch.StartNew();
        startupReadyOperableStopwatch = Stopwatch.StartNew();
        startupReadyDataLogged = false;
        startupReadyUiLogged = false;
        BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree);
        try
        {
            await Task.Run(delegate
            {
                files.Initialize(new List<Action> { taskAdd1, taskAdd2 }, semaphore);
            }).Logging("Initialize");
            LogInitStage("files_initialize_done", "Initialize");
            TryLogStartupReadyData();
            scheduleDeferredPlaylistRef = true;
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
        _semaphore.Release();
        if (scheduleDeferredPlaylistRef)
        {
            ScheduleDeferredPlaylistReferenceApply("Initialize");
            LogInitStage("deferred_playlist_ref_queued", "Initialize");
        }
        if (!Settings.Default.SkipInitPlaylistLoad)
        {
            StartDeferredExternalPlaylistSync("Initialize", fromReloadTables: false, null);
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
        BeMusicSeeker.Models.BMSFile bmsFile;
        try
        {
            bmsFile = BMSFilesView.ElementAt(indexBMSFilesView);
        }
        catch
        {
            return;
        }
        if (bmsFile == null || !File.Exists(bmsFile.path))
        {
            if (NowPlayingBMS != null)
            {
                NowPlayingBMS.status &= ~BeMusicSeeker.Models.BMSFile.BMSFileStatus.PLAYALL;
            }
            NowPlayingBMS = bmsFile;
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
                if (SelectedIndexBMSFilesView >= 0 && SelectedIndexBMSFilesView < BMSFilesView.Count())
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
            if (bmsPlayer == null || NowPlayingBMS == null)
            {
                return;
            }
            int num = BMSFilesView.TakeWhile((BeMusicSeeker.Models.BMSFile x) => x != NowPlayingBMS).Count();
            if (num == BMSFilesView.Count())
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
                string text = ((NowPlayingBMS is VirtualBMSFile) ? num.ToString() : (File.Exists(NowPlayingBMS.path) ? Path.GetDirectoryName(NowPlayingBMS.path) : string.Empty));
                num++;
                if (Settings.Default.RepeatPlayMode && num == BMSFilesView.Count())
                {
                    num = 0;
                }
                while (Settings.Default.FolderSkipPlayMode && num != BMSFilesView.Count())
                {
                    BeMusicSeeker.Models.BMSFile bMSFile = BMSFilesView.ElementAt(num);
                    if (NowPlayingBMS == bMSFile)
                    {
                        break;
                    }
                    string text2 = ((bMSFile is VirtualBMSFile && File.Exists(bMSFile.path)) ? num.ToString() : (File.Exists(bMSFile.path) ? Path.GetDirectoryName(bMSFile.path) : string.Empty));
                    if (!string.IsNullOrWhiteSpace(text2) && text != text2)
                    {
                        break;
                    }
                    text = text2;
                    num++;
                    if (Settings.Default.RepeatPlayMode && num == BMSFilesView.Count())
                    {
                        num = 0;
                    }
                }
            }
            if (num < BMSFilesView.Count())
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
            if (bmsPlayer == null || NowPlayingBMS == null)
            {
                return;
            }
            int num = BMSFilesView.TakeWhile((BeMusicSeeker.Models.BMSFile x) => x != NowPlayingBMS).Count();
            if (num == BMSFilesView.Count())
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
                string text = ((NowPlayingBMS is VirtualBMSFile) ? num.ToString() : (File.Exists(NowPlayingBMS.path) ? Path.GetDirectoryName(NowPlayingBMS.path) : string.Empty));
                num--;
                if (Settings.Default.RepeatPlayMode && num == -1)
                {
                    num = BMSFilesView.Count() - 1;
                }
                while (Settings.Default.FolderSkipPlayMode && num != -1)
                {
                    BeMusicSeeker.Models.BMSFile bMSFile = BMSFilesView.ElementAt(num);
                    if (NowPlayingBMS == bMSFile)
                    {
                        break;
                    }
                    string text2 = ((bMSFile is VirtualBMSFile && File.Exists(bMSFile.path)) ? num.ToString() : (File.Exists(bMSFile.path) ? Path.GetDirectoryName(bMSFile.path) : string.Empty));
                    if (!string.IsNullOrWhiteSpace(text2) && text != text2)
                    {
                        break;
                    }
                    text = text2;
                    num--;
                    if (Settings.Default.RepeatPlayMode && num == -1)
                    {
                        num = BMSFilesView.Count() - 1;
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
            treeViewFilterTypeSelected = mode;
            treeViewFilterParameterSelected = parameter;
        }
        if (BMSFiles == null)
        {
            return;
        }
        switch (mode)
        {
            case viewUpdateMode.FolderFilterSelected:
                if (FolderFilter != null && BMSFiles.Count() != 0)
                {
                    BMSFilesFolderView = BMSFiles.AsParallel().Where(FolderFilter);
                }
                else
                {
                    BMSFilesFolderView = BMSFiles;
                }
                break;
            case viewUpdateMode.PlaylistFilterSelected:
                {
                    if (parameter == null)
                    {
                        break;
                    }
                    string folderName = ((Tuple<BMSTable, string>)parameter).Item2;
                    if (((Tuple<BMSTable, string>)parameter).Item1 != null)
                    {
                        BMSFilesFolderView = (from e in ((Tuple<BMSTable, string>)parameter).Item1.GetEntriesExceptDummy()
                                              where (folderName == null || e.folder == folderName) && !e.is_removed
                                              join f in BMSFiles on e.md5 equals f.hash into f
                                              select new
                                              {
                                                  entry = e,
                                                  files = f.DefaultIfEmpty()
                                              } into g
                                              select new VirtualBMSFile(g.entry, g.files.First())).ToList();
                        files.SetBMSScore(BMSFilesFolderView.Where((BeMusicSeeker.Models.BMSFile f) => string.IsNullOrWhiteSpace(f.path)).ToList());
                    }
                    else
                    {
                        BMSFilesFolderView = Enumerable.Empty<BeMusicSeeker.Models.BMSFile>();
                    }
                    break;
                }
            case viewUpdateMode.PlaylistNotOwnedFilterSelected:
                {
                    if (parameter == null)
                    {
                        break;
                    }
                    BMSTable bMSTable = (BMSTable)parameter;
                    if (bMSTable != null)
                    {
                        BMSFilesFolderView = (from e in bMSTable.GetEntriesExceptDummy()
                                              where !e.is_removed
                                              join f in BMSFiles on e.md5 equals f.hash into f
                                              select new
                                              {
                                                  entry = e,
                                                  files = f.DefaultIfEmpty()
                                              } into g
                                              select new VirtualBMSFile(g.entry, g.files.First()) into f
                                              where string.IsNullOrWhiteSpace(f.path)
                                              select f).ToList();
                        files.SetBMSScore(BMSFilesFolderView.ToList());
                    }
                    else
                    {
                        BMSFilesFolderView = Enumerable.Empty<BeMusicSeeker.Models.BMSFile>();
                    }
                    break;
                }
            case viewUpdateMode.FileMissingFilterSelected:
                BMSFilesFolderView = BMSFilesToBeFixed;
                break;
            case viewUpdateMode.FileMissingIgnoredFilterSelected:
                BMSFilesFolderView = BMSFilesToBeFixedIgnored;
                break;
            case viewUpdateMode.DuplicateFilterSelected:
                if (BMSFilesDuplicated == null)
                {
                    BMSFilesFolderView = null;
                    break;
                }
                if (parameter != null)
                {
                    if (parameter is List<BeMusicSeeker.Models.BMSFile>)
                    {
                        BMSFilesFolderView = parameter as List<BeMusicSeeker.Models.BMSFile>;
                    }
                    else if (parameter is DuplicateGroup)
                    {
                        BMSFilesFolderView = (parameter as DuplicateGroup).Files;
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
                            BMSFilesFolderView = from f in BMSFilesDuplicated.SelectMany((DuplicateGroup g) => g.Files)
                                                 where f.path.StartsWith(dirname + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                                 select f;
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
                    BMSFilesFolderView = BMSFilesDuplicated.SelectMany((DuplicateGroup g) => g.Files);
                }, delegate (Exception ex)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 100u);
                break;
            case viewUpdateMode.GarbledFilterSelected:
                BMSFilesFolderView = BMSFilesGarbled;
                break;
            case viewUpdateMode.GarbleFixedFilterSelected:
                BMSFilesFolderView = BMSFilesGarbleFixed;
                break;
            case viewUpdateMode.UnregisteredFilterSelected:
                BMSFilesFolderView = BMSFilesUnregistered;
                break;
            case viewUpdateMode.ZeroNoteFilterSelected:
                BMSFilesFolderView = BMSFilesZeroNote;
                break;
            case viewUpdateMode.NewlyInstalledFolderSelected:
                if (BMSPackagesInstalled == null)
                {
                    BMSFilesFolderView = null;
                    break;
                }
                if (parameter != null && parameter is BMSPackage)
                {
                    BMSFilesFolderView = (parameter as BMSPackage).BMSFiles;
                    break;
                }
                RetryHelper.RetryIfError(delegate
                {
                    BMSFilesFolderView = BMSPackagesInstalled.SelectMany(delegate (BMSPackage p)
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
                    });
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
                    BMSFilesFolderView = null;
                    break;
                }
                if (parameter != null && parameter is BMSPackage)
                {
                    BMSFilesFolderView = (parameter as BMSPackage).BMSFiles;
                    break;
                }
                RetryHelper.RetryIfError(delegate
                {
                    BMSFilesFolderView = BMSPackagesPending.SelectMany(delegate (BMSPackage p)
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
                    });
                }, delegate (Exception ex)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 100u);
                break;
        }
        BMSFilesFolderView = ((BMSFilesFolderView == null) ? new List<BeMusicSeeker.Models.BMSFile>() : BMSFilesFolderView.ToList());
        folderStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        folderCount = BMSFilesFolderView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        if (mode <= viewUpdateMode.KeywordFilterUpdated)
        {
            if (!string.IsNullOrWhiteSpace(KeywordFilter))
            {
                BMSFilesKeywordFilterView = new List<BeMusicSeeker.Models.BMSFile>();
                string keywordUpper = KeywordFilter.ToUpperInvariant();
                BMSFilesKeywordFilterView = from r in BMSFilesFolderView.AsParallel()
                                            where (r.Title + "@" + r.genre + "@" + r.Artist + "@" + r.tag + "@" + r.path + "@" + r.RefTablesSymbols + ((r is VirtualBMSFile) ? (((VirtualBMSFile)r).memo + "@" + ((VirtualBMSFile)r).comment) : string.Empty)).ToUpperInvariant().Contains(keywordUpper)
                                            select r;
            }
            else
            {
                BMSFilesKeywordFilterView = BMSFilesFolderView;
            }
        }
        BMSFilesKeywordFilterView = ((BMSFilesKeywordFilterView == null) ? new List<BeMusicSeeker.Models.BMSFile>() : BMSFilesKeywordFilterView.ToList());
        keywordStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        keywordCount = BMSFilesKeywordFilterView.Count();
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
                BMSFilesModeFilterView = BMSFilesKeywordFilterView.Where((BeMusicSeeker.Models.BMSFile f) => modeFlag.Contains(f.mode));
            }
            else
            {
                BMSFilesModeFilterView = BMSFilesKeywordFilterView;
            }
        }
        BMSFilesModeFilterView = ((BMSFilesModeFilterView == null) ? new List<BeMusicSeeker.Models.BMSFile>() : BMSFilesModeFilterView.ToList());
        modeStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        modeCount = BMSFilesModeFilterView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        if (mode <= viewUpdateMode.SortUpdated)
        {
            bool useLegacySortForDataGrid = !Settings.Default.UseFastSortInDataGridExperimental;
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
            List<BeMusicSeeker.Models.BMSFile> modeFilterList = BMSFilesModeFilterView as List<BeMusicSeeker.Models.BMSFile>;
            if (isFolderMode && isTreeSelectionRequest && modeFilterList != null && folderSortSourceSnapshot != null && folderSortResultSnapshot != null && string.Equals(folderSortColumnName, columnName, StringComparison.Ordinal) && folderSortDirection == direction && IsSameReferenceSequence(modeFilterList, folderSortSourceSnapshot))
            {
                BMSFilesView = folderSortResultSnapshot;
                sortReuse = true;
                sortProfile = "reuse";
            }
            else
            {
                BMSFileSortEngine.UseLegacySortForDataGrid = useLegacySortForDataGrid;
                BMSFilesView = BMSFileSortEngine.SortForMainView(BMSFilesModeFilterView, SortParameters, isPlaylistDetailView, out sortProfile);
                if (isFolderMode)
                {
                    folderSortResultSnapshot = BMSFilesView;
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
                    folderSortSourceSnapshot = BMSFilesModeFilterView.ToList();
                }
                folderSortColumnName = columnName;
                folderSortDirection = direction;
            }
        }
        else
        {
            BMSFilesView = BMSFilesModeFilterView.ToList();
            sortProfile = "bypass";
        }
        sortStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        viewCount = BMSFilesView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        loadColumnSetting((mode < viewUpdateMode.KeywordFilterUpdated) ? mode : treeViewFilterTypeSelected);
        columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        base.Messenger.Raise(new InteractionMessage("CallbackExecSort"));
        callbackStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        string sortColumn = SortParameters?.ColumnsName ?? "(default_title)";
        string sortDirection = SortParameters?.Direction.ToString() ?? "Ascending";
        string parameterType = parameter?.GetType().Name ?? "(null)";
        bool fastSortEnabled = Settings.Default.UseFastSortInDataGridExperimental;
        bool isPlaylistDetailForLog = mode == viewUpdateMode.PlaylistFilterSelected || mode == viewUpdateMode.PlaylistNotOwnedFilterSelected || treeViewFilterTypeSelected == viewUpdateMode.PlaylistFilterSelected || treeViewFilterTypeSelected == viewUpdateMode.PlaylistNotOwnedFilterSelected;
        LogMainViewBuild("main_view_build mode=" + mode + " requestedMode=" + requestedMode + " parameterType=" + parameterType + " folderMs=" + folderStageMs + " keywordMs=" + keywordStageMs + " modeMs=" + modeStageMs + " sortMs=" + sortStageMs + " sortReuse=" + sortReuse + " sortProfile=" + sortProfile + " sortEngine=" + (fastSortEnabled ? "fast" : "legacy") + " fastSortEnabled=" + fastSortEnabled + " isPlaylistDetailView=" + isPlaylistDetailForLog + " columnMs=" + columnStageMs + " callbackMs=" + callbackStageMs + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds + " folderCount=" + folderCount + " keywordCount=" + keywordCount + " modeCount=" + modeCount + " viewCount=" + viewCount + " sortColumn=" + sortColumn + " sortDirection=" + sortDirection);
    }

    public void LoadColumnSetting()
    {
        loadColumnSetting(viewUpdateMode.TreeViewFilterNotChanged, isInit: true);
    }

    private void loadColumnSetting(viewUpdateMode mode, bool isInit = false)
    {
        if (mode == viewUpdateMode.TreeViewFilterNotChanged)
        {
            mode = treeViewFilterTypeSelected;
        }
        ColumnSettingsVisibilityForPlaylist = Visibility.Collapsed;
        switch (mode)
        {
            case viewUpdateMode.PlaylistFilterSelected:
            case viewUpdateMode.PlaylistNotOwnedFilterSelected:
                if (Settings.Default.PlaylistColumnsSettings == null)
                {
                    Settings.Default.PlaylistColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.PLAYLIST);
                }
                ColumnsSettingsBMSFilesView = Settings.Default.PlaylistColumnsSettings;
                ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
                break;
            case viewUpdateMode.FolderFilterSelected:
            case viewUpdateMode.UnregisteredFilterSelected:
            case viewUpdateMode.ZeroNoteFilterSelected:
                if (Settings.Default.StandardColumnsSettings == null)
                {
                    Settings.Default.StandardColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.STANDARD);
                }
                ColumnsSettingsBMSFilesView = Settings.Default.StandardColumnsSettings;
                break;
            case viewUpdateMode.FileMissingFilterSelected:
            case viewUpdateMode.FileMissingIgnoredFilterSelected:
            case viewUpdateMode.NewlyInstalledFolderSelected:
                if (Settings.Default.FullScanColumnsSettings == null)
                {
                    Settings.Default.FullScanColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.FULLSCAN);
                }
                ColumnsSettingsBMSFilesView = Settings.Default.FullScanColumnsSettings;
                break;
            case viewUpdateMode.DuplicateFilterSelected:
                if (Settings.Default.DuplicateColumnsSettings == null)
                {
                    Settings.Default.DuplicateColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.DUPLICATE);
                }
                ColumnsSettingsBMSFilesView = Settings.Default.DuplicateColumnsSettings;
                break;
            case viewUpdateMode.GarbledFilterSelected:
            case viewUpdateMode.GarbleFixedFilterSelected:
                if (Settings.Default.EncodingColumnsSettings == null)
                {
                    Settings.Default.EncodingColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.ENCODING);
                }
                ColumnsSettingsBMSFilesView = Settings.Default.EncodingColumnsSettings;
                break;
            case viewUpdateMode.PendingInstallFolderSelected:
                if (Settings.Default.InstallColumnsSettings == null)
                {
                    Settings.Default.InstallColumnsSettings = new dataGridColumnsSettings(dataGridColumnsSettings.viewType.INSTALL);
                }
                ColumnsSettingsBMSFilesView = Settings.Default.InstallColumnsSettings;
                break;
        }
        if (Settings.Default.PlaylistSummaryColumnsSettings == null)
        {
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        }
        PlaylistSummaryColumnsSettings = Settings.Default.PlaylistSummaryColumnsSettings;
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
            RefreshPlaylistSummaryIfVisible();
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
        files.GetBMSFilesNeedToBeFixed(bmsFiles, forceUpdate: true);
    }

    public void IgnoreFileScanCheckBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        files.SetBMSFilesToBeFixedIgnored(bmsFiles);
    }

    public void NotIgnoreFileScanCheckBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        files.SetBMSFilesToBeFixedIgnored(bmsFiles, unset: true);
    }

    public void SearchInstallationDirectoryBMSFiles(IEnumerable<BMSPackage> packages)
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
                files.SearchEstimatedInstallationDirectory(list[num]);
            }
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

    public void SearchInstallationDirectoryBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        lock (lockCopyFile)
        {
            List<BeMusicSeeker.Models.BMSFile> bmsFiles2 = bmsFiles.Where((BeMusicSeeker.Models.BMSFile bmsInfo) => bmsInfo != null).ToList();
            List<BMSPackage> bMSPackages = getBMSPackages(ref bmsFiles2);
            for (int num = 0; num < bMSPackages.Count; num++)
            {
                files.SearchEstimatedInstallationDirectory(bMSPackages[num]);
            }
            for (int num2 = 0; num2 < bmsFiles2.Count; num2++)
            {
                files.SearchEstimatedInstallationDirectory(bmsFiles2[num2]);
            }
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

    public void CommitBMSFile(BeMusicSeeker.Models.BMSFile bmsFile)
    {
        if (bmsFile is VirtualBMSFile)
        {
            tables.CommitBMSTableEntry(((VirtualBMSFile)bmsFile).ToBMSTableEntry());
            return;
        }
        throw new NotImplementedException();
    }

    public void ReplaceBMSFileLevelByTableEntryLevel(BMSTable bmsTable)
    {
        if (bmsTable != null && BMSFiles != null)
        {
            IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles = from f in (from f in BMSFiles
                                                                            join e in from e in bmsTable.GetEntriesExceptDummy()
                                                                                      where !e.is_removed
                                                                                      select e on f.hash equals e.md5
                                                                            select new VirtualBMSFile(e, f) into vf
                                                                            where !string.IsNullOrWhiteSpace(vf.path)
                                                                            select vf).Select(delegate (VirtualBMSFile vf)
                                                                        {
                                                                            vf.OverwriteBMSFileLevel();
                                                                            return vf.GetNonVirtualBMSFile();
                                                                        })
                                                                 where f != null
                                                                 select f;
            files.CommitBMSFiles(bmsFiles);
        }
    }

    public void InstallBMSFiles(IEnumerable<string> installPaths, CancellationToken token = default(CancellationToken), Action<bool> onEachCompleted = null)
    {
        if (files == null)
        {
            return;
        }
        List<BMSPackage> list = new List<BMSPackage>();
        lock (lockCopyFile)
        {
            try
            {
                foreach (string installPath in installPaths)
                {
                    if (!token.IsCancellationRequested)
                    {
                        list.AddRange(files.InstallBMSFilesAuto(new string[1] { installPath }));
                        onEachCompleted(obj: true);
                        continue;
                    }
                    break;
                }
            }
            catch (FileNotFoundException ex)
            {
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_installation + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
                return;
            }
            catch
            {
                throw;
            }
        }
        tables.AcquireReaderLockBMSTables();
        files.AddReferenceBMSTables(BMSTables, list.SelectMany((BMSPackage p) => p.BMSFiles));
        tables.FreeReaderLockBMSTables();
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
        if (files == null)
        {
            return;
        }
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<BMSPackage> list = packages.Where((BMSPackage pkg) => pkg != null).ToList();
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(packages.SelectMany((BMSPackage p) => p.BMSFiles));
            for (int num = 0; num < list.Count; num++)
            {
                files.InstallBMSPackageForce(list[num]);
            }
        }
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
        if (files == null)
        {
            return;
        }
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<BMSPackage> list = packages.Where((BMSPackage pkg) => pkg != null).ToList();
        lock (lockCopyFile)
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
            try
            {
                stopPlayingBMSFile(packages.SelectMany((BMSPackage p) => p.BMSFiles));
                files.InstallBMSPackagesToEstimatedDir(list);
            }
            finally
            {
                EndUiUpdateSuppression();
            }
        }
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
        }
    }

    private void RefreshPlaylistSummaryIfVisible()
    {
        if (!IsPlaylistSummaryMode)
        {
            return;
        }
        if (IsUiUpdateSuppressed())
        {
            RequestDeferredPlaylistSummaryRefresh();
            return;
        }
        RebuildPlaylistSummaryView();
    }

    public void SelectPlaylistSummary()
    {
        SetPlaylistSummaryMode(enabled: true);
        RefreshPlaylistSummaryIfVisible();
    }

    public void RebuildPlaylistSummaryView(bool runAsync = true)
    {
        Action action = delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<PlaylistSummaryRow> list = new List<PlaylistSummaryRow>();
            HashSet<string> ownedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<BeMusicSeeker.Models.BMSFile> bMSFiles = BMSFiles;
            if (bMSFiles != null)
            {
                foreach (BeMusicSeeker.Models.BMSFile item in bMSFiles)
                {
                    if (!string.IsNullOrWhiteSpace(item?.hash))
                    {
                        ownedHashes.Add(item.hash);
                    }
                }
            }
            List<BMSTable> list2 = new List<BMSTable>();
            if (tables != null)
            {
                tables.AcquireReaderLockBMSTables();
                try
                {
                    list2 = BMSTables.Where((BMSTable t) => t != null).OrderBy((BMSTable t) => t.name ?? string.Empty).ToList();
                }
                finally
                {
                    tables.FreeReaderLockBMSTables();
                }
            }
            foreach (BMSTable item2 in list2)
            {
                HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (BMSTableEntry item3 in item2.GetEntriesExceptDummy())
                {
                    if (!item3.is_removed && !string.IsNullOrWhiteSpace(item3.md5))
                    {
                        hashSet.Add(item3.md5);
                    }
                }
                int count = hashSet.Count;
                int num = hashSet.Count((string m) => ownedHashes.Contains(m));
                list.Add(new PlaylistSummaryRow
                {
                    PlaylistId = item2.playlist_id,
                    Name = item2.name ?? string.Empty,
                    Symbol = item2.symbol ?? string.Empty,
                    LastUpdate = item2.last_update,
                    TotalCharts = count,
                    OwnedCharts = num,
                    MissingCharts = count - num,
                    OwnedRatio = ((count == 0) ? 0.0 : ((double)num * 100.0 / (double)count)),
                    LinkUri = item2.Page_url ?? item2.GetAbsoluteHeaderUrl(),
                    IsExternalSync = item2.is_external_sync,
                    IsRootFolder = item2.is_root_folder,
                    TableRef = item2
                });
            }
            long buildMs = stopwatch.ElapsedMilliseconds;
            List<PlaylistSummaryRow> filteredRows = ApplyPlaylistSummaryFilters(list).ToList();
            long filterMs = stopwatch.ElapsedMilliseconds - buildMs;
            bool useLegacySort = !Settings.Default.UseFastSortInDataGridExperimental;
            List<PlaylistSummaryRow> rows = PlaylistSummarySortEngine.Sort(filteredRows, PlaylistSummarySortParameters, useLegacySort, out string sortProfile);
            long sortMs = stopwatch.ElapsedMilliseconds - buildMs - filterMs;
            Action reflect = delegate
            {
                PlaylistSummaryView = new ObservableCollection<PlaylistSummaryRow>(rows);
                GridSummaryText = string.Format(BeMusicSeeker.Properties.Resources.Playlist_summary_format, rows.Sum((PlaylistSummaryRow r) => r.TotalCharts), rows.Count);
            };
            if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
            {
                reflect();
            }
            else
            {
                DispatcherHelper.UIDispatcher.BeginInvoke(reflect);
            }
            string sortColumn = PlaylistSummarySortParameters?.ColumnsName ?? nameof(PlaylistSummaryRow.Name);
            string sortDirection = PlaylistSummarySortParameters?.Direction.ToString() ?? ListSortDirection.Ascending.ToString();
            LogMainViewBuild("playlist_summary_build tableCount=" + list2.Count + " rawCount=" + list.Count + " filteredCount=" + filteredRows.Count + " viewCount=" + rows.Count + " buildMs=" + buildMs + " filterMs=" + filterMs + " sortMs=" + sortMs + " totalMs=" + stopwatch.ElapsedMilliseconds + " sortColumn=" + sortColumn + " sortDirection=" + sortDirection + " sortProfile=" + sortProfile + " sortEngine=" + (useLegacySort ? "legacy" : "fast") + " fastSortEnabled=" + Settings.Default.UseFastSortInDataGridExperimental);
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

    private IEnumerable<PlaylistSummaryRow> ApplyPlaylistSummaryFilters(IEnumerable<PlaylistSummaryRow> rows)
    {
        IEnumerable<PlaylistSummaryRow> source = rows ?? Enumerable.Empty<PlaylistSummaryRow>();
        string text = (PlaylistSummaryKeywordFilter ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            string keywordUpper = text.ToUpperInvariant();
            source = source.Where((PlaylistSummaryRow row) => IsPlaylistSummaryRowMatchedKeyword(row, keywordUpper));
        }
        return source.Where(IsPlaylistSummaryRowMatchedOwnedFilter);
    }

    private bool IsPlaylistSummaryRowMatchedKeyword(PlaylistSummaryRow row, string keywordUpper)
    {
        if (row == null)
        {
            return false;
        }
        string text = row.PlaylistId?.ToString() ?? string.Empty;
        string text2 = row.Name ?? string.Empty;
        string text3 = row.Symbol ?? string.Empty;
        return text.ToUpperInvariant().Contains(keywordUpper) || text2.ToUpperInvariant().Contains(keywordUpper) || text3.ToUpperInvariant().Contains(keywordUpper);
    }

    private bool IsPlaylistSummaryRowMatchedOwnedFilter(PlaylistSummaryRow row)
    {
        if (row == null)
        {
            return false;
        }
        switch (PlaylistSummaryOwnedFilter)
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
        if (rows == null)
        {
            return;
        }
        List<BMSTable> tablesToResync = rows.Where((PlaylistSummaryRow r) => r?.TableRef != null).Select((PlaylistSummaryRow r) => r.TableRef).Distinct().ToList();
        ResyncPlaylists(tablesToResync);
    }

    public void ResyncPlaylists(IEnumerable<BMSTable> tablesToResync)
    {
        if (tablesToResync == null || tables == null || files == null)
        {
            return;
        }
        List<BMSTable> list = tablesToResync.Where((BMSTable t) => t != null).Distinct().ToList();
        foreach (BMSTable item in list)
        {
            Uri uri = item.Page_url ?? item.Header_url;
            if (uri == null || !uri.IsAbsoluteUri)
            {
                continue;
            }
            try
            {
                BMSTable bMSTable = tables.ResetBMSTable(item, uri);
                files.RemoveReferenceBMSTables(item);
                files.AddReferenceBMSTables(bMSTable);
            }
            catch
            {
            }
        }
        RefreshPlaylistSummaryIfVisible();
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
        if (files != null)
        {
            files.RemoveBMSPackagesPendingAll();
        }
    }

    public void RemoveBMSPackagesPending(IEnumerable<BMSPackage> packages)
    {
        if (files != null)
        {
            if (packages == null)
            {
                throw new ArgumentNullException("packages");
            }
            files.RemoveBMSPackagesPending(packages);
        }
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
        if (files != null)
        {
            if (bmsFiles == null)
            {
                throw new ArgumentNullException("bmsFiles");
            }
            files.SearchCorrectInstallationDirectory(bmsFiles);
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
        if (files != null)
        {
            if (bmsFiles == null)
            {
                throw new ArgumentNullException("bmsFiles");
            }
            List<BeMusicSeeker.Models.BMSFile> bmsFiles2 = bmsFiles.Where((BeMusicSeeker.Models.BMSFile f) => f != null).ToList();
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

    internal void RegistrateExternalPlaylistBMSTable(Uri uri)
    {
        BMSTable table;
        try
        {
            table = tables.RegistrateExternalTable(uri);
        }
        catch (InvalidOperationException ex)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_load_playlist + Environment.NewLine + ex.Message + ex.InnerException, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            return;
        }
        catch
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_load_playlist, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            return;
        }
        tables.AcquireReaderLockBMSTables();
        files.AddReferenceBMSTables(table);
        tables.FreeReaderLockBMSTables();
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

    internal void AddEntriesToFolderBMSTable(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, BMSTable bmsTable, string folderName = "")
    {
        if (folderName == null)
        {
            folderName = string.Empty;
        }
        if (bmsTable.is_external_sync)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_add_playlist_entry, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            return;
        }
        if (bmsFiles.All((BeMusicSeeker.Models.BMSFile f) => f is VirtualBMSFile))
        {
            List<VirtualBMSFile> list = (from VirtualBMSFile vf in bmsFiles
                                         where vf.ToBMSTableEntry().parent == bmsTable
                                         select vf).ToList();
            List<VirtualBMSFile> second = list.Where((VirtualBMSFile vf) => vf.Folder == folderName).ToList();
            if (bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder && string.IsNullOrWhiteSpace(folderName))
            {
                tables.RemoveEntriesBMSTable(list.Select((VirtualBMSFile vf) => vf.ToBMSTableEntry()), bmsTable, commitFlag: false);
            }
            else
            {
                bmsFiles = bmsFiles.Except(second).ToList();
                if (bmsFiles.Count() == 0)
                {
                    return;
                }
                tables.RemoveEntriesBMSTable(from vf in list.Except(second)
                                             select vf.ToBMSTableEntry(), bmsTable, commitFlag: false);
            }
        }
        if (bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder && string.IsNullOrWhiteSpace(folderName))
        {
            List<BeMusicSeeker.Models.BMSFile> list2 = bmsFiles.Where((BeMusicSeeker.Models.BMSFile f) => string.IsNullOrWhiteSpace(f.path) || (f is VirtualBMSFile && ((VirtualBMSFile)f).GetNonVirtualBMSFile() == null)).ToList();
            List<BeMusicSeeker.Models.BMSFile> source = bmsFiles.Except(list2).ToList();
            tables.AddEntriesToFolderBMSTable(list2.Select((BeMusicSeeker.Models.BMSFile f) => (f as VirtualBMSFile).ToBMSTableEntry().Duplicate()), bmsTable, folderName, commitFlag: false);
            if (BMSFiles == null)
            {
                return;
            }
            foreach (IGrouping<string, BeMusicSeeker.Models.BMSFile> item in source.GroupBy((BeMusicSeeker.Models.BMSFile f) => DirectoryExt.GetDirectoryNameSimple(f.path), StringComparer.OrdinalIgnoreCase).ToList())
            {
                List<string> md5sInTheSameDir = null;
                foreach (BeMusicSeeker.Models.BMSFile item2 in item)
                {
                    md5sInTheSameDir = files.GetMD5sOfTheSameSong((item2 is VirtualBMSFile) ? ((VirtualBMSFile)item2).GetNonVirtualBMSFile() : item2);
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
                tables.AddEntriesToFolderBMSTable(item.Select((BeMusicSeeker.Models.BMSFile f) => (f is VirtualBMSFile) ? (f as VirtualBMSFile).ToBMSTableEntry().Duplicate() : new BMSTableEntry(f)
                {
                    Org_md5 = md5sInTheSameDir
                }), bmsTable, text, commitFlag: false);
            }
        }
        else
        {
            ParallelQuery<BMSTableEntry> bmsEntries = from f in bmsFiles.AsParallel()
                                                      select (f is VirtualBMSFile) ? (f as VirtualBMSFile).ToBMSTableEntry().Duplicate() : new BMSTableEntry(f)
                                                      {
                                                          Org_md5 = files.GetMD5sOfTheSameSong(f)
                                                      };
            tables.AddEntriesToFolderBMSTable(bmsEntries, bmsTable, folderName, commitFlag: false);
        }
        tables.ReOutputCustomFolderAndCommitToDB(bmsTable);
        updateBMSFilesViewForPlaylist(bmsTable);
        tables.AcquireReaderLockBMSTables();
        files.AddReferenceBMSTables(bmsTable, bmsFiles);
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
        lock (lockCopyFile)
        {
            if (bmsFiles == null)
            {
                throw new ArgumentNullException("bmsFiles");
            }
            stopPlayingBMSFile(bmsFiles);
            files.FixInstallationDirectory(bmsFiles);
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

    public void RemovePendingBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(bmsFiles);
            files.RemovePendingBMSFiles(bmsFiles);
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
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(bmsFiles);
            files.RenamePendingBMSFilesExtensions(bmsFiles, newExt);
        }
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
        }
    }

    public void AutoRenameBMSFolder(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        lock (lockCopyFile)
        {
            stopPlayingBMSFile(bmsFiles);
            files.AutoRenameBMSFolder(bmsFiles);
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

    public BMSLibrary.IRSongInfo GetLR2IRSongInfoCache(BeMusicSeeker.Models.BMSFile bmsFile, bool seaarchAggressively = false)
    {
        if (bmsFile == null)
        {
            throw new ArgumentNullException();
        }
        string text = null;
        if (!string.IsNullOrWhiteSpace(bmsFile.hash))
        {
            text = bmsFile.hash;
        }
        else
        {
            if (!(bmsFile is VirtualBMSFile) || string.IsNullOrWhiteSpace(((VirtualBMSFile)bmsFile).lr2_bmsid))
            {
                throw new ArgumentException("MD5/LR2BMSID not found: " + bmsFile.path);
            }
            text = ((VirtualBMSFile)bmsFile).lr2_bmsid;
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

    public string RegisterBMSFilesToScoreViewer(List<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        if (bmsFiles == null)
        {
            throw new ArgumentNullException("bmsFiles");
        }
        BeMusicSeeker.Models.BMSFile bMSFile = bmsFiles.Last();
        bool flag = false;
        string text = null;
        GZipWebClient gZipWebClient = new GZipWebClient
        {
            Encoding = Encoding.UTF8
        };
        if (bmsFiles.Count > 1)
        {
            ConfirmationMessage confirmationMessage = new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_register_chart + Environment.NewLine + Environment.NewLine + bmsFiles.Count + " " + BeMusicSeeker.Properties.Resources.Num_chart, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxImage.Asterisk, MessageBoxButton.YesNo, "ConfirmationDialog");
            base.Messenger.Raise(confirmationMessage);
            if (confirmationMessage.Response != true)
            {
                return null;
            }
            flag = true;
        }
        foreach (BeMusicSeeker.Models.BMSFile bmsFile in bmsFiles)
        {
            try
            {
                string text2 = bmsFile.hash;
                if (string.IsNullOrWhiteSpace(bmsFile.path) || !File.Exists(bmsFile.path))
                {
                    goto IL_048f;
                }
                dynamic val = DynamicJson.Parse(gZipWebClient.DownloadString(scoreStatusUrl + bmsFile.hash));
                if (!((val.status != "OK") ? true : false))
                {
                    goto IL_048f;
                }
                if (flag || !Settings.Default.ShowScoreViewerRegisterConfirmMsg)
                {
                    goto IL_02e3;
                }
                ConfirmationMessage confirmationMessage2 = new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_show_chart + Environment.NewLine + Environment.NewLine + bmsFile.Title + Environment.NewLine + "MD5: " + bmsFile.hash + Environment.NewLine + Environment.NewLine + "(" + BeMusicSeeker.Properties.Resources.Msg_hide_message + ")", BeMusicSeeker.Properties.Resources.Confirm, MessageBoxImage.Asterisk, MessageBoxButton.YesNo, "ConfirmationDialog");
                base.Messenger.Raise(confirmationMessage2);
                if (confirmationMessage2.Response == true)
                {
                    flag = true;
                    goto IL_02e3;
                }
                goto end_IL_00d9;
            IL_048f:
                if (bmsFile == bMSFile)
                {
                    text = scoreViewUrl + text2;
                }
                goto end_IL_00d9;
            IL_02e3:
                string json = Encoding.UTF8.GetString(gZipWebClient.UploadFile(scoreRegisterUrl, bmsFile.path));
                if (bmsFile != bMSFile)
                {
                    goto IL_048f;
                }
                dynamic val2 = DynamicJson.Parse(json);
                if (val2.status != "OK")
                {
                    continue;
                }
                text2 = val2.md5;
                goto IL_048f;
            end_IL_00d9:;
            }
            catch (Exception)
            {
                if (bmsFile != bMSFile)
                {
                    gZipWebClient = new GZipWebClient();
                }
            }
        }
        if (flag && !string.IsNullOrWhiteSpace(text))
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_success_register_chart, BeMusicSeeker.Properties.Resources.Information, MessageBoxImage.Asterisk, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        return text;
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
