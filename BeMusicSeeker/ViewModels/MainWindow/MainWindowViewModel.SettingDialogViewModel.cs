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
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Codeplex.Data;
using Livet;
using Livet.Commands;
using Livet.EventListeners;
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

/// <summary>
/// Contains the settings dialog portion of <see cref="MainWindowViewModel"/> while preserving the nested public type shape.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Holds editable settings state for the settings dialog before values are applied to the application settings store.
    /// </summary>
    public partial class SettingDialogViewModel : ViewModel
    {
        /// <summary>
        /// Describes which application areas must be restarted or reloaded after settings are saved.
        /// </summary>
        [Flags]
        public enum RestartMode
        {
            /// <summary>
            /// No restart or reload is required.
            /// </summary>
            None = 0,

            /// <summary>
            /// Folder-backed library data must be reloaded.
            /// </summary>
            FolderOnly = 1,

            /// <summary>
            /// A full application restart or full reload is required.
            /// </summary>
            All = 2,

            /// <summary>
            /// Score data must be reloaded.
            /// </summary>
            ScoreOnly = 4
        }

        private readonly MainWindowViewModel ownerViewModel;

        private readonly PropertyChangedEventListener ownerViewModelEventListener;

        private readonly PropertyChangedEventListener resourceServiceEventListener;

        private bool tempOperationModeLR2DB;

        private string tempLR2RootPath;

        private readonly Dictionary<string, Point> lr2bodyResolutions = new()
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

        private Lr2PlayHistorySchemaCheckResult lr2PlayHistorySchemaCheckResult;

        private string lr2PlayHistoryScoreDbPath;

        private bool? lr2PlayHistorySchemaCheckOperationMode;

        private bool tempUseBeatorajaScoreDb;

        private string tempBeatorajaRootPath;

        private string tempBeatorajaPlayerId;

        private string tempBeatorajaScoreDbPath;

        private bool tempEnableBeatorajaBmtOutput;

        private bool tempKeepBeatorajaBmtFilesWhenOutputDisabled;

        private string tempBeatorajaBmtHashOutputMode;

        private string tempBeatorajaBmtTablePath;

        private bool tempRegisterBeatorajaBmtUrls;

        private string tempuBMplayPath;

        private string tempBMIIDXViewPath;

        private bool tempUsePlayeruBMplay;

        private bool tempUsePlayerLR2body;

        private bool tempUsePlayerBMIIDXView;

        private bool tempIsSaveLR2bodyWindowPosition;

        private string tempLR2CustomFolderOutputDir;

        private string tempLR2CustomFolderAdditionalOutputBaseDirs;

        private int tempPlaylistDefaultIgnoreFolderOutput;

        private string tempBMSInstallDir;

        private string tempLR2CustomFolderAsRootOutputDir;

        private Uri tempTableListURL;

        private bool tempEnablePlaylistUrlCompletion;

        private bool tempOverwritePlaylistUrlsWithCompletion;

        private bool tempEnableStellaFullPlaylistUrlCompletion;

        private string tempPlaylistMd5UrlMappingTsvUri;

        private string tempPlayHistoryDisplayTargetSetsJson;

        private string tempPlayHistoryDisplayTargetSetDraftsJson;

        private PlayHistoryFolderDisplayPresetEditor selectedPlayHistoryFolderDisplayPreset;

        private bool isPlayHistoryFolderDisplayPresetPlaylistOptionsDirty = true;

        private bool tempIsLR2BackupEnabled;

        private string tempLR2BackupPath;

        private Backup.Target tempLR2BackupTarget;

        private int tempLR2BackupSpan;

        private int tempLR2BackupNum;

        private bool tempUseExternalWebBrowser;

        private bool tempUseExternalPanelImage;

        private string tempAppearanceTheme;

        private double tempCustomTableFontSize;

        private double tempCustomTableRowHeight;

        private double tempCustomTableHeaderHeight;

        private readonly IReadOnlyList<AppearanceThemeOption> appearanceThemeOptions;

        private readonly IReadOnlyList<BeatorajaBmtHashOutputModeOption> beatorajaBmtHashOutputModeOptions;

        private bool tempShowScoreViewerRegisterConfirmMsg;

        private bool tempShowDiffBMSInstallConfirmMsg;

        private bool tempShowDuplicateFileCheckConfirmMsg;

        private bool tempShowRecommUpdatedMsg;

        private bool tempScanBmsFilesOnStartup;

        private bool tempSkipInitPlaylistLoad;

        private bool tempStartupSelectInstallPending;

        private bool tempEnableReadOptimizedPragmas;

        private bool tempEstimateOfflineScoreRanking;

        private bool tempUpdateLr2IrRankingCacheOnStartup;

        private bool tempEnableDownloadLr2IrScoreAndDetectUnsent;

        private bool tempEnableAutoInstall;

        private bool tempKeepInstallablePackagesPending;

        private bool tempAutoApplyAmbiguousInstallDestination;

        private bool tempDeletePendingPackageSourceAfterInstall;

        private bool tempEnableSmartComponentOverwrite;

        private bool tempKeepSmartOverwriteProtectedFilesByRenaming;

        private string tempStagefilePath;

        private static readonly string defaultFolderNameFormat = "[%ARTIST%] %TITLE%";

        private string tempFolderNameFormat;

        private bool tempUseOnlyShiftJISChars;

        private SampleRate tempEncoderSampleRate;

        private int tempEncoderIndex;

        private SampleFormat tempEncoderFormat;

        private BMSAutoPlayWriter.Normalization tempEncoderNormalization;

        private float tempEncoderQuality;

        private float tempEncoderAmplifier;

        private string tempEncoderExeDir;

        private static readonly string defaultEncodeFileNameFormat = "[%ARTIST%] %TITLE%";

        private string tempEncodeFileNameFormat;

        private int tempPlayerDriverIndex;

        private List<BassAudioPlayer.DeviceDescriptor> playerDeviceNames;

        private string tempPlayerDevice;

        private string tempPlayerDeviceName;

        private SampleRate tempPlayerSampleRate;

        private SampleFormat tempPlayerFormat;

        private float tempPlayerBufferSize;

        private bool tempPlayerWASAPIParam;

        private bool operationModeLR2DB;

        private bool isBMSDirectoryAdded;

        private bool isBMSDirectoryRemoved;

        private ListenerCommand<string> _RemoveDirCommand;

        private bool isSearchRootsChanged;

        private string tempLR2ConfigBmsSearchRoots;

        private string tempStandaloneBmsRootPaths;

        private string selectedStandaloneBmsRootPath;

        private string selectedLR2ConfigBmsDirectory;

        private string selectedCustomFolderAdditionalOutputBaseDir;

        private string selectedCustomFolderAdditionalOutputBaseName;

        private readonly Dictionary<string, string> pendingCustomFolderAdditionalOutputBaseRenames = new(StringComparer.OrdinalIgnoreCase);

        private string tempLanguage;

        private string tempLanguageDisplayName;

        public bool OperationModeLR2DB
        {
            get
            {
                return operationModeLR2DB;
            }
            set
            {
                if (operationModeLR2DB != value)
                {
                    if (!ownerViewModel.HasActiveLibraryProfile)
                    {
                        SetOperationModeSelection(value);
                        return;
                    }
                    ConfirmAndRestartForOperationModeChange(value);
                }
            }
        }

        public bool CanUseLr2Features => OperationModeLR2DB;

        public bool IsOperationModeChanged => tempOperationModeLR2DB != OperationModeLR2DB;

        public bool CanSaveSettings => CheckValidationForSave();

        private void RaiseValidationStateChanged()
        {
            RaisePropertyChanged(() => CanSaveSettings);
        }

        private void SetOperationModeSelection(bool value)
        {
            operationModeLR2DB = value;
            RaisePropertyChanged("OperationModeLR2DB");
            RaisePropertyChanged(() => IsOperationModeChanged);
            RaisePropertyChanged(() => CanUseLr2Features);
            RaisePropertyChanged(() => AvailableBMSDirectories);
            RaisePropertyChanged(() => SelectedBmsSearchRootPath);
            RaisePropertyChanged(() => IsBmsSearchRootEditorEnabled);
            RaisePropertyChanged(() => BMSInstallDir);
            RaiseValidationStateChanged();
            ownerViewModel.RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
            ResetLr2PlayHistorySchemaStatus();
        }

        private void ConfirmAndRestartForOperationModeChange(bool value)
        {
            if (!MainWindowViewModel.ShowUiConfirmation(
                BeMusicSeeker.Properties.Resources.Confirm_RestartForOperationModeChange,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxImage.Question,
                MessageBoxButton.OKCancel,
                "Operation mode restart confirmation"))
            {
                SetOperationModeSelection(operationModeLR2DB);
                return;
            }
            try
            {
                SaveOperationModeForRestart(value);
                ((App)System.Windows.Application.Current).RestartApplication();
            }
            catch (Exception ex)
            {
                ShowUiMessage(
                    BeMusicSeeker.Properties.Resources.Error_RestartApplicationFailed + Environment.NewLine + Environment.NewLine + ex.Message,
                    BeMusicSeeker.Properties.Resources.Error,
                    MessageBoxImage.Hand,
                    "Restart failure notification");
                System.Windows.Application.Current.Shutdown();
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
                    if (!string.IsNullOrWhiteSpace(LR2ConfigXmlPath) && LR2ConfigXmlPath.EndsWith(".xmh", StringComparison.OrdinalIgnoreCase) && File.Exists(text))
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
                return Settings.Default.LR2RootPath;
            }
            set
            {
                if (Settings.Default.LR2RootPath == value)
                {
                    return;
                }
                if (IsLR2PlayerRootPathValid(value))
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
                    if (IsLR2SongDBPathValid(lR2SongDBPath))
                    {
                        LR2SongDBPath = lR2SongDBPath;
                    }
                    LR2ConfigXmlPath = empty;
                }
                else
                {
                    Settings.Default.LR2RootPath = null;
                }
                RaisePropertyChanged("LR2RootPath");
                RaisePropertyChanged(() => LR2bodyPath);
                RaiseValidationStateChanged();
                ResetLr2PlayHistorySchemaStatus();
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public ObservableCollection<string> StandaloneBmsRootPathList { get; } = [];

        public ObservableCollection<string> CustomFolderAdditionalOutputBaseDirList { get; } = [];

        /// <summary>
        /// 設定ダイアログで編集する play history FOLDER 表示プリセットの draft 一覧です。
        /// OK まで user.config へ保存しないことで、Cancel 時の設定復元コストを抑えます。
        /// </summary>
        public ObservableCollection<PlayHistoryFolderDisplayPresetEditor> PlayHistoryFolderDisplayPresets { get; } = [];

        /// <summary>
        /// 選択中プリセットへ追加できる playlist 候補です。
        /// BMSTables の再読み込みに追従するため、選択中プリセットから都度再構築します。
        /// </summary>
        public ObservableCollection<PlayHistoryFolderPresetPlaylistOption> PlayHistoryFolderDisplayPresetPlaylistOptions { get; } = [];

        /// <summary>
        /// 設定ダイアログで現在編集している play history FOLDER 表示プリセットです。
        /// </summary>
        public PlayHistoryFolderDisplayPresetEditor SelectedPlayHistoryFolderDisplayPreset
        {
            get
            {
                if (selectedPlayHistoryFolderDisplayPreset != null
                    && !PlayHistoryFolderDisplayPresets.Contains(selectedPlayHistoryFolderDisplayPreset))
                {
                    selectedPlayHistoryFolderDisplayPreset = null;
                }
                selectedPlayHistoryFolderDisplayPreset ??= PlayHistoryFolderDisplayPresets.FirstOrDefault();
                return selectedPlayHistoryFolderDisplayPreset;
            }
            set
            {
                if (!ReferenceEquals(selectedPlayHistoryFolderDisplayPreset, value))
                {
                    selectedPlayHistoryFolderDisplayPreset = value;
                    MarkPlayHistoryFolderDisplayPresetPlaylistOptionsDirty();
                    RaisePropertyChanged();
                    RaisePropertyChanged(() => CanEditPlayHistoryFolderDisplayPreset);
                    RaisePropertyChanged(() => CanRemovePlayHistoryFolderDisplayPreset);
                    RaiseValidationStateChanged();
                }
            }
        }

        /// <summary>
        /// 選択中プリセットへ編集操作を適用できるかどうかを取得します。
        /// </summary>
        public bool CanEditPlayHistoryFolderDisplayPreset => SelectedPlayHistoryFolderDisplayPreset != null;

        /// <summary>
        /// 選択中プリセットを削除できるかどうかを取得します。
        /// </summary>
        public bool CanRemovePlayHistoryFolderDisplayPreset => SelectedPlayHistoryFolderDisplayPreset != null;

        public string SelectedStandaloneBmsRootPath
        {
            get
            {
                return selectedStandaloneBmsRootPath;
            }
            set
            {
                if (!string.Equals(selectedStandaloneBmsRootPath, value, StringComparison.Ordinal))
                {
                    selectedStandaloneBmsRootPath = value;
                    RaisePropertyChanged(() => SelectedStandaloneBmsRootPath);
                    RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                }
            }
        }

        public string SelectedBmsSearchRootPath
        {
            get
            {
                if (!OperationModeLR2DB)
                {
                    return SelectedStandaloneBmsRootPath;
                }
                List<string> lr2Directories = LR2ConfigBMSDirectories;
                if (!lr2Directories.Contains(selectedLR2ConfigBmsDirectory, StringComparer.OrdinalIgnoreCase))
                {
                    selectedLR2ConfigBmsDirectory = lr2Directories.FirstOrDefault();
                }
                return selectedLR2ConfigBmsDirectory;
            }
            set
            {
                if (!OperationModeLR2DB)
                {
                    SelectedStandaloneBmsRootPath = value;
                    RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                    return;
                }
                if (!string.Equals(selectedLR2ConfigBmsDirectory, value, StringComparison.Ordinal))
                {
                    selectedLR2ConfigBmsDirectory = value;
                    RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                }
            }
        }

        public bool IsBmsSearchRootEditorEnabled => !OperationModeLR2DB || lr2config != null;

        internal Lr2PlayHistorySchemaCheckResult Lr2PlayHistorySchemaCheckResult => lr2PlayHistorySchemaCheckResult;

        public string Lr2PlayHistoryScoreDbPath => lr2PlayHistoryScoreDbPath ?? string.Empty;

        public string Lr2PlayHistorySchemaStatusText => Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaCheckResult).StatusText;

        public string Lr2PlayHistorySchemaMessage => Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaCheckResult).Message;

        public string Lr2PlayHistorySchemaDetailText
        {
            get
            {
                string message = Lr2PlayHistorySchemaMessage;
                string scoreDbPath = Lr2PlayHistoryScoreDbPath;
                if (string.IsNullOrWhiteSpace(scoreDbPath))
                {
                    return message;
                }
                return string.IsNullOrWhiteSpace(message)
                    ? scoreDbPath
                    : message + Environment.NewLine + scoreDbPath;
            }
        }

        public bool CanInstallLr2PlayHistorySchema => Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaCheckResult).CanInstall;

        public bool CanRepairLr2PlayHistorySchema => Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaCheckResult).CanRepair;

        /// <summary>
        /// LR2 play history schema に対して enable/repair のどちらかを実行できるかを返します。
        /// UI では状態別にボタンを分けず、現在の schema 状態に応じて同じ操作境界へ集約します。
        /// </summary>
        public bool CanInstallOrRepairLr2PlayHistorySchema =>
            OperationModeLR2DB
            && !string.IsNullOrWhiteSpace(Lr2PlayHistoryScoreDbPath)
            && (lr2PlayHistorySchemaCheckResult == null || CanInstallLr2PlayHistorySchema || CanRepairLr2PlayHistorySchema);

        /// <summary>
        /// LR2 play history schema の enable/repair 統合ボタンに表示する文言を返します。
        /// </summary>
        public string Lr2PlayHistorySchemaInstallOrRepairButtonText
        {
            get
            {
                Lr2PlayHistorySchemaStatusPresentation presentation = Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaCheckResult);
                if (presentation.CanInstall)
                {
                    return BeMusicSeeker.Properties.Resources.Lr2_play_history_schema_install;
                }
                if (presentation.CanRepair)
                {
                    return BeMusicSeeker.Properties.Resources.Lr2_play_history_schema_repair;
                }
                return BeMusicSeeker.Properties.Resources.Lr2_play_history_schema_install_or_repair;
            }
        }

        /// <summary>
        /// Backup タブから LR2 play history schema のアンインストール操作を開始できるかを返します。
        /// destructive な選択は押下後の確認ダイアログへ閉じ込めます。
        /// </summary>
        public bool CanUninstallLr2PlayHistorySchema => OperationModeLR2DB && !string.IsNullOrWhiteSpace(Lr2PlayHistoryScoreDbPath);

        internal void ResetLr2PlayHistorySchemaStatus()
        {
            string nextScoreDbPath = ResolveLr2PlayHistoryScoreDbPath();
            bool nextOperationMode = OperationModeLR2DB;
            if (lr2PlayHistorySchemaCheckResult != null
                && lr2PlayHistorySchemaCheckOperationMode == nextOperationMode
                && string.Equals(lr2PlayHistoryScoreDbPath ?? string.Empty, nextScoreDbPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                RaiseLr2PlayHistorySchemaStatusChanged();
                return;
            }
            lr2PlayHistoryScoreDbPath = nextScoreDbPath;
            lr2PlayHistorySchemaCheckResult = null;
            lr2PlayHistorySchemaCheckOperationMode = null;
            RaiseLr2PlayHistorySchemaStatusChanged();
        }

        internal void ClearLr2PlayHistorySchemaStatus()
        {
            lr2PlayHistoryScoreDbPath = ResolveLr2PlayHistoryScoreDbPath();
            lr2PlayHistorySchemaCheckResult = null;
            lr2PlayHistorySchemaCheckOperationMode = null;
            RaiseLr2PlayHistorySchemaStatusChanged();
        }

        internal void RefreshLr2PlayHistorySchemaStatusPresentation()
        {
            ResetLr2PlayHistorySchemaStatus();
        }

        internal Lr2PlayHistorySchemaCheckResult InstallOrRepairLr2PlayHistorySchemaCore()
        {
            string scoreDbPath = ResolveLr2PlayHistoryScoreDbPath();
            LogLr2PlayHistorySchema("install_or_repair_start", scoreDbPath, OperationModeLR2DB);
            Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, OperationModeLR2DB);
            LogLr2PlayHistorySchema("install_or_repair_done", result, OperationModeLR2DB);
            return result;
        }

        /// <summary>
        /// LR2 score DB に入れた play history schema を指定モードで削除し、削除後の状態を返します。
        /// </summary>
        /// <param name="uninstallMode">trigger のみ削除するか、履歴 table も削除するか。</param>
        /// <returns>削除後に再確認した schema 状態。</returns>
        internal Lr2PlayHistorySchemaCheckResult UninstallLr2PlayHistorySchemaCore(Lr2PlayHistorySchemaUninstallMode uninstallMode)
        {
            string scoreDbPath = ResolveLr2PlayHistoryScoreDbPath();
            LogLr2PlayHistorySchema("uninstall_start_" + uninstallMode, scoreDbPath, OperationModeLR2DB);
            Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().Uninstall(scoreDbPath, OperationModeLR2DB, uninstallMode);
            LogLr2PlayHistorySchema("uninstall_done", result, OperationModeLR2DB);
            return result;
        }

        internal void ApplyLr2PlayHistorySchemaCheckResult(Lr2PlayHistorySchemaCheckResult result)
        {
            lr2PlayHistoryScoreDbPath = result?.ScoreDbPath ?? ResolveLr2PlayHistoryScoreDbPath();
            lr2PlayHistorySchemaCheckResult = result;
            lr2PlayHistorySchemaCheckOperationMode = OperationModeLR2DB;
            RaiseLr2PlayHistorySchemaStatusChanged();
        }

        internal bool HasFreshLr2PlayHistorySchemaCheckResult(string scoreDbPath, bool isLr2LinkedProfile)
        {
            return lr2PlayHistorySchemaCheckResult != null
                && lr2PlayHistorySchemaCheckOperationMode == isLr2LinkedProfile
                && string.Equals(lr2PlayHistoryScoreDbPath ?? string.Empty, scoreDbPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        internal Lr2PlayHistorySchemaCheckResult CheckLr2PlayHistorySchemaCore()
        {
            return CheckLr2PlayHistorySchemaCore(ResolveLr2PlayHistoryScoreDbPath(), OperationModeLR2DB);
        }

        internal Lr2PlayHistorySchemaCheckResult CheckLr2PlayHistorySchemaCore(string scoreDbPath, bool isLr2LinkedProfile)
        {
            Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().Check(scoreDbPath, isLr2LinkedProfile);
            LogLr2PlayHistorySchema("check", result, isLr2LinkedProfile);
            return result;
        }

        private string ResolveLr2PlayHistoryScoreDbPath()
        {
            if (!OperationModeLR2DB)
            {
                return null;
            }
            return Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(Settings.Default.LR2RootPath, () => lr2config?.GetPlayerId());
        }

        private void RaiseLr2PlayHistorySchemaStatusChanged()
        {
            RaisePropertyChanged(() => Lr2PlayHistorySchemaCheckResult);
            RaisePropertyChanged(() => Lr2PlayHistoryScoreDbPath);
            RaisePropertyChanged(() => Lr2PlayHistorySchemaStatusText);
            RaisePropertyChanged(() => Lr2PlayHistorySchemaMessage);
            RaisePropertyChanged(() => Lr2PlayHistorySchemaDetailText);
            RaisePropertyChanged(() => CanInstallLr2PlayHistorySchema);
            RaisePropertyChanged(() => CanRepairLr2PlayHistorySchema);
            RaisePropertyChanged(() => CanInstallOrRepairLr2PlayHistorySchema);
            RaisePropertyChanged(() => Lr2PlayHistorySchemaInstallOrRepairButtonText);
            RaisePropertyChanged(() => CanUninstallLr2PlayHistorySchema);
        }

        private static void LogLr2PlayHistorySchema(string action, Lr2PlayHistorySchemaCheckResult result, bool isLr2LinkedProfile)
        {
            LogLr2PlayHistorySchema(action, result?.ScoreDbPath, isLr2LinkedProfile, result?.Status.ToString(), result?.Message);
        }

        private static void LogLr2PlayHistorySchema(string action, string scoreDbPath, bool isLr2LinkedProfile)
        {
            LogLr2PlayHistorySchema(action, scoreDbPath, isLr2LinkedProfile, status: null, message: null);
        }

        private static void LogLr2PlayHistorySchema(string action, string scoreDbPath, bool isLr2LinkedProfile, string status, string message)
        {
            NLogWrapper.FileLogger?.Info("play_history_schema_" + (action ?? string.Empty)
                + " linked=" + isLr2LinkedProfile
                + " status=" + QuoteLr2PlayHistorySchemaLogValue(status)
                + " path=" + QuoteLr2PlayHistorySchemaLogValue(scoreDbPath)
                + " message=" + QuoteLr2PlayHistorySchemaLogValue(message));
        }

        private static string QuoteLr2PlayHistorySchemaLogValue(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        public string SelectedCustomFolderAdditionalOutputBaseDir
        {
            get
            {
                if (!CustomFolderAdditionalOutputBaseDirList.Contains(selectedCustomFolderAdditionalOutputBaseDir, StringComparer.OrdinalIgnoreCase))
                {
                    selectedCustomFolderAdditionalOutputBaseDir = CustomFolderAdditionalOutputBaseDirList.FirstOrDefault();
                    selectedCustomFolderAdditionalOutputBaseName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(selectedCustomFolderAdditionalOutputBaseDir);
                }
                return selectedCustomFolderAdditionalOutputBaseDir;
            }
            set
            {
                if (!string.Equals(selectedCustomFolderAdditionalOutputBaseDir, value, StringComparison.Ordinal))
                {
                    selectedCustomFolderAdditionalOutputBaseDir = value;
                    selectedCustomFolderAdditionalOutputBaseName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(value);
                    RaisePropertyChanged(() => SelectedCustomFolderAdditionalOutputBaseDir);
                    RaisePropertyChanged(() => SelectedCustomFolderAdditionalOutputBaseName);
                }
            }
        }

        public string SelectedCustomFolderAdditionalOutputBaseName
        {
            get
            {
                if (selectedCustomFolderAdditionalOutputBaseName == null)
                {
                    selectedCustomFolderAdditionalOutputBaseName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(SelectedCustomFolderAdditionalOutputBaseDir);
                }
                return selectedCustomFolderAdditionalOutputBaseName;
            }
            set
            {
                value = value?.Trim();
                if (!string.Equals(selectedCustomFolderAdditionalOutputBaseName, value, StringComparison.Ordinal))
                {
                    selectedCustomFolderAdditionalOutputBaseName = value;
                    RaisePropertyChanged(() => SelectedCustomFolderAdditionalOutputBaseName);
                }
            }
        }

        public string LR2SongDBPath
        {
            get
            {
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public string LR2ConfigXmlPath
        {
            get
            {
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
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                RaisePropertyChanged(() => IsBmsSearchRootEditorEnabled);
                RaisePropertyChanged(() => BMSInstallDir);
                RaiseValidationStateChanged();
                ResetLr2PlayHistorySchemaStatus();
            }
        }

        public bool UseBeatorajaScoreDb
        {
            get
            {
                return Settings.Default.UseBeatorajaScoreDb;
            }
            set
            {
                if (Settings.Default.UseBeatorajaScoreDb != value)
                {
                    Settings.Default.UseBeatorajaScoreDb = value;
                    RaisePropertyChanged("UseBeatorajaScoreDb");
                    RaiseValidationStateChanged();
                }
            }
        }

        public string BeatorajaRootPath
        {
            get
            {
                return Settings.Default.BeatorajaRootPath;
            }
            set
            {
                string path = value ?? string.Empty;
                if (Settings.Default.BeatorajaRootPath == path)
                {
                    return;
                }
                Settings.Default.BeatorajaRootPath = path;
                RefreshBeatorajaDerivedSettings();
                RaisePropertyChanged("BeatorajaRootPath");
                RaisePropertyChanged(() => AvailableBeatorajaPlayers);
                RaisePropertyChanged(() => BeatorajaPlayerId);
                RaisePropertyChanged(() => BeatorajaScoreDbPath);
                RaisePropertyChanged(() => BeatorajaBmtTablePath);
                RaiseValidationStateChanged();
            }
        }

        public List<string> AvailableBeatorajaPlayers
        {
            get
            {
                return BeatorajaConfigService.GetPlayerIds(Settings.Default.BeatorajaRootPath);
            }
        }

        public string BeatorajaPlayerId
        {
            get
            {
                return Settings.Default.BeatorajaPlayerId;
            }
            set
            {
                string playerId = value ?? string.Empty;
                if (Settings.Default.BeatorajaPlayerId == playerId)
                {
                    return;
                }
                Settings.Default.BeatorajaPlayerId = playerId;
                RefreshBeatorajaDerivedSettings();
                RaisePropertyChanged("BeatorajaPlayerId");
                RaisePropertyChanged(() => BeatorajaScoreDbPath);
                RaiseValidationStateChanged();
            }
        }

        public string BeatorajaScoreDbPath
        {
            get
            {
                return Settings.Default.BeatorajaScoreDbPath;
            }
            set
            {
                RaisePropertyChanged("BeatorajaScoreDbPath");
                RaiseValidationStateChanged();
            }
        }

        public bool EnableBeatorajaBmtOutput
        {
            get
            {
                return Settings.Default.EnableBeatorajaBmtOutput;
            }
            set
            {
                if (Settings.Default.EnableBeatorajaBmtOutput != value)
                {
                    if (value
                        && !Settings.Default.EnableBeatorajaBmtOutput
                        && ownerViewModel.HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(BeatorajaRootPath)
                        && !MainWindowViewModel.ShowUiConfirmation(
                            BeMusicSeeker.Properties.Resources.Confirm_enable_beatoraja_bmt_output_before_table_url_import,
                            BeMusicSeeker.Properties.Resources.Confirm,
                            MessageBoxImage.Exclamation,
                            MessageBoxButton.OKCancel,
                            "beatoraja BMT output enable before Table URL import confirmation"))
                    {
                        RaisePropertyChanged("EnableBeatorajaBmtOutput");
                        return;
                    }
                    Settings.Default.EnableBeatorajaBmtOutput = value;
                    RaisePropertyChanged("EnableBeatorajaBmtOutput");
                    RaiseValidationStateChanged();
                }
            }
        }

        public bool KeepBeatorajaBmtFilesWhenOutputDisabled
        {
            get
            {
                return Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled;
            }
            set
            {
                if (Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled != value)
                {
                    Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled = value;
                    RaisePropertyChanged("KeepBeatorajaBmtFilesWhenOutputDisabled");
                    RaiseValidationStateChanged();
                }
            }
        }

        public string BeatorajaBmtTablePath
        {
            get
            {
                return Settings.Default.BeatorajaBmtTablePath;
            }
            set
            {
                RaisePropertyChanged("BeatorajaBmtTablePath");
                RaiseValidationStateChanged();
            }
        }

        public bool RegisterBeatorajaBmtUrls
        {
            get
            {
                return Settings.Default.RegisterBeatorajaBmtUrls;
            }
            set
            {
                if (Settings.Default.RegisterBeatorajaBmtUrls != value)
                {
                    Settings.Default.RegisterBeatorajaBmtUrls = value;
                    RaisePropertyChanged("RegisterBeatorajaBmtUrls");
                    RaiseValidationStateChanged();
                }
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
                    RaisePropertyChanged(() => AvailableBMSDirectories);
                    RaisePropertyChanged(() => SelectedBmsSearchRootPath);
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public string BMIIDXViewPath
        {
            get
            {
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
                    RaiseValidationStateChanged();
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
                if (value)
                {
                    SetPlayerSelection(usePlayeruBMplay: true, usePlayerLR2body: false, usePlayerBMIIDXView: false);
                }
                else if (Settings.Default.UsePlayeruBMplay)
                {
                    SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: Settings.Default.UsePlayerLR2body, usePlayerBMIIDXView: Settings.Default.UsePlayerBMIIDXView);
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
                if (value)
                {
                    SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: true, usePlayerBMIIDXView: false);
                }
                else if (Settings.Default.UsePlayerLR2body)
                {
                    SetPlayerSelection(usePlayeruBMplay: Settings.Default.UsePlayeruBMplay, usePlayerLR2body: false, usePlayerBMIIDXView: Settings.Default.UsePlayerBMIIDXView);
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
                if (value)
                {
                    SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: false, usePlayerBMIIDXView: true);
                }
                else if (Settings.Default.UsePlayerBMIIDXView)
                {
                    SetPlayerSelection(usePlayeruBMplay: Settings.Default.UsePlayeruBMplay, usePlayerLR2body: Settings.Default.UsePlayerLR2body, usePlayerBMIIDXView: false);
                }
            }
        }

        public bool UseInternalPlayer
        {
            get
            {
                return !Settings.Default.UsePlayeruBMplay
                    && !Settings.Default.UsePlayerLR2body
                    && !Settings.Default.UsePlayerBMIIDXView;
            }
            set
            {
                if (value)
                {
                    SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: false, usePlayerBMIIDXView: false);
                }
            }
        }

        private void SetPlayerSelection(bool usePlayeruBMplay, bool usePlayerLR2body, bool usePlayerBMIIDXView)
        {
            if (Settings.Default.UsePlayeruBMplay == usePlayeruBMplay
                && Settings.Default.UsePlayerLR2body == usePlayerLR2body
                && Settings.Default.UsePlayerBMIIDXView == usePlayerBMIIDXView)
            {
                return;
            }
            Settings.Default.UsePlayeruBMplay = usePlayeruBMplay;
            Settings.Default.UsePlayerLR2body = usePlayerLR2body;
            Settings.Default.UsePlayerBMIIDXView = usePlayerBMIIDXView;
            RaisePropertyChanged(() => UseInternalPlayer);
            RaisePropertyChanged(() => UsePlayeruBMplay);
            RaisePropertyChanged(() => UsePlayerLR2body);
            RaisePropertyChanged(() => UsePlayerBMIIDXView);
            RaiseValidationStateChanged();
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
                if (lr2config != null)
                {
                    return GetLR2UserBmsSearchRootDirectories();
                }
                return [];
            }
            private set
            {
            }
        }

        private List<string> GetLR2UserBmsSearchRootDirectories()
        {
            return [.. lr2config.GetBMSSearchDirectoriesForChangeTracking()
                .Where(directory => !IsUserVisibleBmsSearchRootExcludedPath(directory))];
        }

        private List<string> GetExistingLR2UserBmsSearchRootDirectories()
        {
            return [.. lr2config.GetBMSSearchDirectoriesReadOnly()
                .Where(directory => !IsUserVisibleBmsSearchRootExcludedPath(directory))];
        }

        internal bool IsManagedCustomFolderSearchRootPath(string path)
        {
            return IsUserVisibleBmsSearchRootExcludedPath(path);
        }

        private bool IsUserVisibleBmsSearchRootExcludedPath(string path)
        {
            return ContainsSameOrChildDirectory(GetUserVisibleBmsSearchRootExcludedDirectories(includePreviousSettings: true), path);
        }

        private IReadOnlyList<string> GetUserVisibleBmsSearchRootExcludedDirectories(bool includePreviousSettings)
        {
            var paths = new List<string>
            {
                LR2CustomFolderOutputDir,
                LR2CustomFolderAsRootOutputDir
            };
            paths.AddRange(CustomFolderAdditionalOutputBaseDirList);
            paths.AddRange(CreateRootFolderOutputDirectories(LR2CustomFolderAsRootOutputDir));

            if (includePreviousSettings)
            {
                paths.Add(tempLR2CustomFolderOutputDir);
                paths.Add(tempLR2CustomFolderAsRootOutputDir);
                paths.AddRange(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(tempLR2CustomFolderAdditionalOutputBaseDirs));
                paths.AddRange(CreateRootFolderOutputDirectories(tempLR2CustomFolderAsRootOutputDir));
            }

            return CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(paths);
        }

        private IEnumerable<string> CreateRootFolderOutputDirectories(string rootOutputBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootOutputBaseDirectory) || ownerViewModel.BMSTables == null)
            {
                return [];
            }

            return [.. ownerViewModel.BMSTables
                .Where(table => table != null && table.is_root_folder && !string.IsNullOrWhiteSpace(table.Output_dir))
                .Select(table => Path.Combine(rootOutputBaseDirectory, table.Output_dir))];
        }

        private static bool ContainsSameOrChildDirectory(IEnumerable<string> parentDirectories, string targetDirectory)
        {
            string normalizedTargetDirectory = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(targetDirectory);
            return !string.IsNullOrWhiteSpace(normalizedTargetDirectory)
                && parentDirectories != null
                && parentDirectories.Any(parentDirectory =>
                {
                    string normalizedParentDirectory = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(parentDirectory);
                    return !string.IsNullOrWhiteSpace(normalizedParentDirectory)
                        && IsSameOrChildPath(normalizedTargetDirectory, normalizedParentDirectory);
                });
        }

        public List<string> AvailableBMSDirectories
        {
            get
            {
                if (OperationModeLR2DB)
                {
                    return LR2ConfigBMSDirectories;
                }
                return [.. StandaloneBmsRootPathList];
            }
        }

        public string LR2CustomFolderOutputDir
        {
            get
            {
                return Settings.Default.LR2CustomFolderOutputBaseDir;
            }
            set
            {
                if (value != null && !(Settings.Default.LR2CustomFolderOutputBaseDir == value))
                {
                    string previousPath = Settings.Default.LR2CustomFolderOutputBaseDir;
                    if (ValidateCustomFolderOutputBaseDir(value, out string errMsg))
                    {
                        Settings.Default.LR2CustomFolderOutputBaseDir = value;
                        NotifyNormalOutputBaseChanged(previousPath, value);
                    }
                    else
                    {
                        ShowSettingValidationError(errMsg);
                    }
                    RaisePropertyChanged("LR2CustomFolderOutputDir");
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                    RaisePropertyChanged(() => AvailableBMSDirectories);
                    RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                    RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
                    RaiseValidationStateChanged();
                }
            }
        }

        private void NotifyNormalOutputBaseChanged(string previousPath, string currentPath)
        {
            if (!OperationModeLR2DB
                || string.IsNullOrWhiteSpace(previousPath)
                || string.IsNullOrWhiteSpace(currentPath)
                || string.Equals(
                    CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(previousPath),
                    CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(currentPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            MainWindowViewModel.ShowUiMessage(
                string.Format(
                    CultureInfo.CurrentCulture,
                    BeMusicSeeker.Properties.Resources.Msg_CustomFolderNormalOutputBaseChangedSearchRootRemovedFormat,
                    previousPath),
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "Normal custom folder output base changed warning");
        }

        public string BMSInstallDir
        {
            get
            {
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public string LR2CustomFolderAsRootOutputDir
        {
            get
            {
                return Settings.Default.LR2CustomFolderOutputBaseDirRootType;
            }
            set
            {
                if (value != null && !(Settings.Default.LR2CustomFolderOutputBaseDirRootType == value))
                {
                    if (ValidateCustomFolderAsRootOutputBaseDir(value, out string errMsg))
                    {
                        Settings.Default.LR2CustomFolderOutputBaseDirRootType = value;
                    }
                    else
                    {
                        ShowSettingValidationError(errMsg);
                    }
                    RaisePropertyChanged("LR2CustomFolderAsRootOutputDir");
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                    RaisePropertyChanged(() => AvailableBMSDirectories);
                    RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                    RaiseValidationStateChanged();
                }
            }
        }

        public bool DefaultOutputAllSongsFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder, value, nameof(DefaultOutputAllSongsFolder));
        }

        public bool DefaultOutputUserFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.UserFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.UserFolder, value, nameof(DefaultOutputUserFolder));
        }

        public bool DefaultOutputLevelFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder, value, nameof(DefaultOutputLevelFolder));
        }

        public bool DefaultOutputAlphabetFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder, value, nameof(DefaultOutputAlphabetFolder));
        }

        public bool DefaultOutputClearFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder, value, nameof(DefaultOutputClearFolder));
        }

        public bool DefaultOutputDJLevelFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder, value, nameof(DefaultOutputDJLevelFolder));
        }

        public bool DefaultOutputCategoryAllFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder, value, nameof(DefaultOutputCategoryAllFolder));
        }

        public bool DefaultOutputOtherFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder, value, nameof(DefaultOutputOtherFolder));
        }

        public bool DefaultOutputRandomFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.RandomFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.RandomFolder, value, nameof(DefaultOutputRandomFolder));
        }

        public bool DefaultOutputBpmSortFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder, value, nameof(DefaultOutputBpmSortFolder));
        }

        public bool DefaultOutputBpSortFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder, value, nameof(DefaultOutputBpSortFolder));
        }

        public bool DefaultOutputPlayCountSortFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder, value, nameof(DefaultOutputPlayCountSortFolder));
        }

        public bool DefaultOutputLastPlaySortFolder
        {
            get => IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder);
            set => SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder, value, nameof(DefaultOutputLastPlaySortFolder));
        }

        private bool IsDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType type)
        {
            return LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(
                BMSPlaylist.NormalizeNewPlaylistIgnoreFolderOutputDefault(Settings.Default.PlaylistDefaultIgnoreFolderOutput),
                type);
        }

        private void SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType type, bool enabled, string propertyName)
        {
            LR2SongDBExtended.playlist.CustomFolderType mask =
                BMSPlaylist.NormalizeNewPlaylistIgnoreFolderOutputDefault(Settings.Default.PlaylistDefaultIgnoreFolderOutput);
            mask = enabled ? mask & ~type : mask | type;
            int nextValue = (int)BMSPlaylist.NormalizeNewPlaylistIgnoreFolderOutputDefault((int)mask);
            if (Settings.Default.PlaylistDefaultIgnoreFolderOutput != nextValue)
            {
                Settings.Default.PlaylistDefaultIgnoreFolderOutput = nextValue;
                RaisePropertyChanged(propertyName);
            }
        }

        private void RaiseDefaultCustomFolderOutputPropertiesChanged()
        {
            RaisePropertyChanged(() => DefaultOutputAllSongsFolder);
            RaisePropertyChanged(() => DefaultOutputUserFolder);
            RaisePropertyChanged(() => DefaultOutputLevelFolder);
            RaisePropertyChanged(() => DefaultOutputAlphabetFolder);
            RaisePropertyChanged(() => DefaultOutputClearFolder);
            RaisePropertyChanged(() => DefaultOutputDJLevelFolder);
            RaisePropertyChanged(() => DefaultOutputCategoryAllFolder);
            RaisePropertyChanged(() => DefaultOutputOtherFolder);
            RaisePropertyChanged(() => DefaultOutputRandomFolder);
            RaisePropertyChanged(() => DefaultOutputBpmSortFolder);
            RaisePropertyChanged(() => DefaultOutputBpSortFolder);
            RaisePropertyChanged(() => DefaultOutputPlayCountSortFolder);
            RaisePropertyChanged(() => DefaultOutputLastPlaySortFolder);
        }

        private void ShowSettingValidationError(string errMsg)
        {
            if (string.IsNullOrWhiteSpace(errMsg))
            {
                return;
            }
            MainWindowViewModel.ShowUiMessage(errMsg, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
        }

        internal bool ConfirmCustomFolderOutputBaseJukeboxAdoptionBeforeSave(out bool hasConflicts)
        {
            IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> conflicts = CollectCustomFolderOutputBaseJukeboxAdoptionConflicts();
            hasConflicts = conflicts.Count > 0;
            if (conflicts.Count == 0)
            {
                return true;
            }

            return MainWindowViewModel.ShowUiConfirmation(
                BuildCustomFolderOutputBaseJukeboxAdoptionMessage(conflicts),
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                MessageBoxButton.OKCancel,
                "Custom folder output base adoption confirmation");
        }

        private IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> CollectCustomFolderOutputBaseJukeboxAdoptionConflicts()
        {
            if (!OperationModeLR2DB || (lr2config == null && !IsLR2ConfigXmlPathValid()))
            {
                return [];
            }

            LR2Config config;
            try
            {
                config = lr2config ?? new LR2Config(Settings.Default.LR2ConfigXmlPath);
            }
            catch
            {
                return [];
            }

            return CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(
                CreateCurrentCustomFolderOutputBaseCandidates(),
                config.GetBMSSearchDirectoriesForChangeTracking(),
                GetPreviousNonNormalManagedCustomFolderSearchRootDirectories());
        }

        private bool HasCustomFolderOutputBaseJukeboxAdoptionConflicts()
        {
            return CollectCustomFolderOutputBaseJukeboxAdoptionConflicts().Count > 0;
        }

        private bool ValidateLR2BmsSearchRootsDoNotOverlap(out string errMsg)
        {
            errMsg = string.Empty;
            if (!OperationModeLR2DB || (lr2config == null && !IsLR2ConfigXmlPathValid()))
            {
                return true;
            }

            LR2Config config;
            try
            {
                config = lr2config ?? new LR2Config(Settings.Default.LR2ConfigXmlPath);
            }
            catch
            {
                return true;
            }

            IReadOnlyList<NestedBmsSearchRootConflict> conflicts =
                CollectNestedBmsSearchRootConflicts(config.GetBMSSearchDirectoriesForChangeTracking());
            if (conflicts.Count == 0)
            {
                return true;
            }

            errMsg = BuildNestedBmsSearchRootConflictMessage(conflicts);
            return false;
        }

        internal static IReadOnlyList<NestedBmsSearchRootConflict> CollectNestedBmsSearchRootConflicts(IEnumerable<string> roots)
        {
            IReadOnlyList<string> normalizedRoots = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(roots);
            var conflicts = new List<NestedBmsSearchRootConflict>();
            for (int leftIndex = 0; leftIndex < normalizedRoots.Count; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < normalizedRoots.Count; rightIndex++)
                {
                    string left = normalizedRoots[leftIndex];
                    string right = normalizedRoots[rightIndex];
                    if (IsSameOrChildPath(right, left))
                    {
                        conflicts.Add(new NestedBmsSearchRootConflict(left, right));
                    }
                    else if (IsSameOrChildPath(left, right))
                    {
                        conflicts.Add(new NestedBmsSearchRootConflict(right, left));
                    }
                }
            }
            return conflicts;
        }

        private static string BuildNestedBmsSearchRootConflictMessage(IReadOnlyList<NestedBmsSearchRootConflict> conflicts)
        {
            const int maxVisibleConflictCount = 10;
            IReadOnlyList<NestedBmsSearchRootConflict> safeConflicts = conflicts ?? [];
            string conflictLines = string.Join(
                Environment.NewLine,
                safeConflicts
                    .Take(maxVisibleConflictCount)
                    .Select(conflict => FormatResource(
                        BeMusicSeeker.Properties.Resources.Validation_NestedBmsSearchRootPathLineFormat,
                        conflict.ParentPath,
                        conflict.ChildPath)));
            int omittedCount = safeConflicts.Count - maxVisibleConflictCount;
            if (omittedCount > 0)
            {
                conflictLines += Environment.NewLine
                    + FormatResource(BeMusicSeeker.Properties.Resources.Confirm_CustomFolderOutputBaseJukeboxAdoptionOmittedLineFormat, omittedCount);
            }
            return FormatResource(BeMusicSeeker.Properties.Resources.Validation_NestedBmsSearchRootPathsFormat, conflictLines);
        }

        private IReadOnlyList<CustomFolderOutputBaseCandidate> CreateCurrentCustomFolderOutputBaseCandidates()
        {
            var candidates = new List<CustomFolderOutputBaseCandidate>
            {
                new(BeMusicSeeker.Properties.Resources.Label_RootOutputBase, LR2CustomFolderAsRootOutputDir)
            };
            foreach (string path in CustomFolderAdditionalOutputBaseDirList)
            {
                candidates.Add(new CustomFolderOutputBaseCandidate(BeMusicSeeker.Properties.Resources.Label_AdditionalOutputBaseFolder, path));
            }
            return candidates;
        }

        private IReadOnlyList<string> GetPreviousNormalCustomFolderSearchRootDirectories()
        {
            return CustomFolderOutputBaseRegistry.NormalizeBaseDirectories([tempLR2CustomFolderOutputDir]);
        }

        private IReadOnlyList<string> GetPreviousNonNormalManagedCustomFolderSearchRootDirectories()
        {
            var paths = new List<string>
            {
                tempLR2CustomFolderAsRootOutputDir
            };
            paths.AddRange(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(tempLR2CustomFolderAdditionalOutputBaseDirs));
            paths.AddRange(CreateRootFolderOutputDirectories(tempLR2CustomFolderAsRootOutputDir));
            return CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(paths);
        }

        internal static IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(
            IEnumerable<CustomFolderOutputBaseCandidate> candidates,
            IEnumerable<string> jukeboxRoots,
            IEnumerable<string> previousManagedRoots)
        {
            IReadOnlyList<string> normalizedJukeboxRoots = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(jukeboxRoots);
            IReadOnlyList<string> normalizedPreviousManagedRoots = CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(previousManagedRoots);
            var conflicts = new List<CustomFolderOutputBaseJukeboxAdoptionConflict>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CustomFolderOutputBaseCandidate candidate in candidates ?? [])
            {
                string candidatePath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(candidate?.Path);
                if (string.IsNullOrWhiteSpace(candidatePath))
                {
                    continue;
                }

                foreach (string jukeboxRoot in normalizedJukeboxRoots)
                {
                    if (string.IsNullOrWhiteSpace(jukeboxRoot)
                        || IsSameOrChildOfAnyRoot(jukeboxRoot, normalizedPreviousManagedRoots)
                        || !CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(candidatePath, jukeboxRoot))
                    {
                        continue;
                    }

                    string key = (candidate?.Label ?? string.Empty) + "\n" + candidatePath + "\n" + jukeboxRoot;
                    if (seen.Add(key))
                    {
                        conflicts.Add(new CustomFolderOutputBaseJukeboxAdoptionConflict(candidate?.Label, candidatePath, jukeboxRoot));
                    }
                }
            }
            return conflicts;
        }

        private static bool IsSameOrChildOfAnyRoot(string path, IEnumerable<string> roots)
        {
            string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
            return !string.IsNullOrWhiteSpace(normalizedPath)
                && roots != null
                && roots.Any(root => IsSameOrChildPath(normalizedPath, root));
        }

        private static string BuildCustomFolderOutputBaseJukeboxAdoptionMessage(IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> conflicts)
        {
            const int maxVisibleConflictCount = 10;
            IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> safeConflicts = conflicts ?? [];
            string conflictLines = string.Join(
                Environment.NewLine,
                safeConflicts
                    .Take(maxVisibleConflictCount)
                    .Select(conflict => FormatResource(
                        BeMusicSeeker.Properties.Resources.Confirm_CustomFolderOutputBaseJukeboxAdoptionConflictLineFormat,
                        conflict.OutputBaseLabel,
                        conflict.OutputBasePath,
                        conflict.JukeboxRootPath)));
            int omittedCount = safeConflicts.Count - maxVisibleConflictCount;
            if (omittedCount > 0)
            {
                conflictLines += Environment.NewLine
                    + FormatResource(BeMusicSeeker.Properties.Resources.Confirm_CustomFolderOutputBaseJukeboxAdoptionOmittedLineFormat, omittedCount);
            }
            return FormatResource(BeMusicSeeker.Properties.Resources.Confirm_CustomFolderOutputBaseJukeboxAdoptionFormat, conflictLines);
        }

        private static string FormatResource(string format, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }

        internal sealed class CustomFolderOutputBaseCandidate
        {
            internal CustomFolderOutputBaseCandidate(string label, string path)
            {
                Label = label ?? string.Empty;
                Path = path ?? string.Empty;
            }

            internal string Label { get; }

            internal string Path { get; }
        }

        internal sealed class CustomFolderOutputBaseJukeboxAdoptionConflict
        {
            internal CustomFolderOutputBaseJukeboxAdoptionConflict(string outputBaseLabel, string outputBasePath, string jukeboxRootPath)
            {
                OutputBaseLabel = outputBaseLabel ?? string.Empty;
                OutputBasePath = outputBasePath ?? string.Empty;
                JukeboxRootPath = jukeboxRootPath ?? string.Empty;
            }

            internal string OutputBaseLabel { get; }

            internal string OutputBasePath { get; }

            internal string JukeboxRootPath { get; }
        }

        internal readonly struct NestedBmsSearchRootConflict
        {
            internal NestedBmsSearchRootConflict(string parentPath, string childPath)
            {
                ParentPath = parentPath ?? string.Empty;
                ChildPath = childPath ?? string.Empty;
            }

            internal string ParentPath { get; }

            internal string ChildPath { get; }
        }

        private static string FormatSettingValidationMessage(string sectionName, string message)
        {
            return FormatResource(BeMusicSeeker.Properties.Resources.SettingValidation_SectionMessageFormat, sectionName, message);
        }

        private static string FormatPlaylistValidationMessage(string message)
        {
            return FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playlist, message);
        }

        public Uri TableListURL
        {
            get
            {
                if (!IsTableListURLValid())
                {
                    return new Uri(Settings.DefaultTableListUrl);
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
                        MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidTableListUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                    }
                    RaisePropertyChanged("TableListURL");
                    RaiseValidationStateChanged();
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
                    RaiseValidationStateChanged();
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

        public bool EnableStellaFullPlaylistUrlCompletion
        {
            get
            {
                return Settings.Default.EnableStellaFullPlaylistUrlCompletion;
            }
            set
            {
                if (Settings.Default.EnableStellaFullPlaylistUrlCompletion != value)
                {
                    Settings.Default.EnableStellaFullPlaylistUrlCompletion = value;
                    RaisePropertyChanged("EnableStellaFullPlaylistUrlCompletion");
                }
            }
        }

        public string PlaylistMd5UrlMappingTsvUri
        {
            get
            {
                if (Settings.Default.PlaylistMd5UrlMappingTsvUri == null)
                {
                    return PlaylistUrlCompletionSupport.DefaultMd5UrlMappingTsvUri;
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
                    MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidPlaylistMd5UrlMappingTsvUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                }
                RaisePropertyChanged("PlaylistMd5UrlMappingTsvUri");
                RaiseValidationStateChanged();
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public string LR2BackupPath
        {
            get
            {
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
                    RaiseValidationStateChanged();
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public sealed class AppearanceThemeOption : ViewModel
        {
            internal AppearanceThemeOption(string key)
            {
                Key = key;
            }

            public string Key { get; }

            public string DisplayName
            {
                get
                {
                    return string.Equals(Key, AppThemeService.Dark, StringComparison.Ordinal)
                        ? BeMusicSeeker.Properties.Resources.Appearance_theme_dark
                        : BeMusicSeeker.Properties.Resources.Appearance_theme_light;
                }
            }

            internal void RefreshDisplayName()
            {
                RaisePropertyChanged(() => DisplayName);
            }
        }

        public IReadOnlyList<AppearanceThemeOption> AppearanceThemeOptions
        {
            get
            {
                return appearanceThemeOptions;
            }
        }

        public sealed class BeatorajaBmtHashOutputModeOption : ViewModel
        {
            internal BeatorajaBmtHashOutputModeOption(BeMusicSeeker.Models.BeatorajaBmtHashOutputMode mode)
            {
                Key = mode.ToString();
            }

            public string Key { get; }

            public string DisplayName
            {
                get
                {
                    BeMusicSeeker.Models.BeatorajaBmtHashOutputMode mode = BmtTableExportService.NormalizeHashOutputMode(Key);
                    return mode switch
                    {
                        BeMusicSeeker.Models.BeatorajaBmtHashOutputMode.FillMissingMd5Sha256 => BeMusicSeeker.Properties.Resources.Beatoraja_bmt_hash_output_fill_missing,
                        BeMusicSeeker.Models.BeatorajaBmtHashOutputMode.PreferSha256Only => BeMusicSeeker.Properties.Resources.Beatoraja_bmt_hash_output_prefer_sha256_only,
                        _ => BeMusicSeeker.Properties.Resources.Beatoraja_bmt_hash_output_original
                    };
                }
            }

            internal void RefreshDisplayName()
            {
                RaisePropertyChanged(() => DisplayName);
            }
        }

        public IReadOnlyList<BeatorajaBmtHashOutputModeOption> BeatorajaBmtHashOutputModeOptions
        {
            get
            {
                return beatorajaBmtHashOutputModeOptions;
            }
        }

        public string BeatorajaBmtHashOutputMode
        {
            get
            {
                return BmtTableExportService.NormalizeHashOutputMode(Settings.Default.BeatorajaBmtHashOutputMode).ToString();
            }
            set
            {
                string normalizedValue = BmtTableExportService.NormalizeHashOutputMode(value).ToString();
                if (Settings.Default.BeatorajaBmtHashOutputMode != normalizedValue)
                {
                    Settings.Default.BeatorajaBmtHashOutputMode = normalizedValue;
                    RaisePropertyChanged("BeatorajaBmtHashOutputMode");
                    RaiseValidationStateChanged();
                }
            }
        }

        public string AppearanceTheme
        {
            get
            {
                return Settings.Default.AppearanceTheme;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    RaisePropertyChanged("AppearanceTheme");
                    return;
                }
                string normalizedTheme = AppThemeService.NormalizeTheme(value);
                if (Settings.Default.AppearanceTheme != normalizedTheme)
                {
                    Settings.Default.AppearanceTheme = normalizedTheme;
                    AppThemeService.ApplyTheme(normalizedTheme);
                    RaisePropertyChanged("AppearanceTheme");
                }
            }
        }

        public double CustomTableFontSize
        {
            get
            {
                return Settings.Default.CustomTableFontSize;
            }
            set
            {
                double normalizedValue = Settings.NormalizeRange(value, Settings.MinCustomTableFontSize, Settings.MaxCustomTableFontSize, Settings.DefaultCustomTableFontSize);
                if (!Settings.Default.CustomTableFontSize.Equals(normalizedValue))
                {
                    Settings.Default.CustomTableFontSize = normalizedValue;
                    RaisePropertyChanged("CustomTableFontSize");
                }
            }
        }

        public double CustomTableRowHeight
        {
            get
            {
                return Settings.Default.CustomTableRowHeight;
            }
            set
            {
                double normalizedValue = Settings.NormalizeRange(value, Settings.MinCustomTableRowHeight, Settings.MaxCustomTableRowHeight, Settings.DefaultCustomTableRowHeight);
                if (!Settings.Default.CustomTableRowHeight.Equals(normalizedValue))
                {
                    Settings.Default.CustomTableRowHeight = normalizedValue;
                    RaisePropertyChanged("CustomTableRowHeight");
                }
            }
        }

        public double CustomTableHeaderHeight
        {
            get
            {
                return Settings.Default.CustomTableHeaderHeight;
            }
            set
            {
                double normalizedValue = Settings.NormalizeRange(value, Settings.MinCustomTableHeaderHeight, Settings.MaxCustomTableHeaderHeight, Settings.DefaultCustomTableHeaderHeight);
                if (!Settings.Default.CustomTableHeaderHeight.Equals(normalizedValue))
                {
                    Settings.Default.CustomTableHeaderHeight = normalizedValue;
                    RaisePropertyChanged("CustomTableHeaderHeight");
                }
            }
        }

        public void ResetCustomTableAppearanceDefaults()
        {
            CustomTableFontSize = Settings.DefaultCustomTableFontSize;
            CustomTableRowHeight = Settings.DefaultCustomTableRowHeight;
            CustomTableHeaderHeight = Settings.DefaultCustomTableHeaderHeight;
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

        public bool ShowDuplicateFileCheckConfirmMsg
        {
            get
            {
                return Settings.Default.ShowDuplicateFileCheckConfirmMsg;
            }
            set
            {
                if (Settings.Default.ShowDuplicateFileCheckConfirmMsg != value)
                {
                    Settings.Default.ShowDuplicateFileCheckConfirmMsg = value;
                    RaisePropertyChanged("ShowDuplicateFileCheckConfirmMsg");
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

        public bool ScanBmsFilesOnStartup
        {
            get
            {
                return Settings.Default.ScanBmsFilesOnStartup;
            }
            set
            {
                if (Settings.Default.ScanBmsFilesOnStartup == value)
                {
                    return;
                }
                if (!value)
                {
                    if (!MainWindowViewModel.ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_disable_startup_file_scan, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "Startup file scan disable confirmation"))
                    {
                        RaisePropertyChanged("ScanBmsFilesOnStartup");
                        return;
                    }
                }
                Settings.Default.ScanBmsFilesOnStartup = value;
                RaisePropertyChanged("ScanBmsFilesOnStartup");
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
                    if (!MainWindowViewModel.ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_skip_init_playlist_load, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "Initial playlist load skip confirmation"))
                    {
                        RaisePropertyChanged("UpdateLr2IrRankingCacheOnStartup");
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

        public bool EstimateOfflineScoreRanking
        {
            get
            {
                return Settings.Default.EstimateOfflineScoreRanking;
            }
            set
            {
                if (Settings.Default.EstimateOfflineScoreRanking == value)
                {
                    return;
                }
                if (value)
                {
                    if (!MainWindowViewModel.ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_enable_offline_score_ranking_estimation, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "Offline score ranking estimation confirmation"))
                    {
                        return;
                    }
                }
                Settings.Default.EstimateOfflineScoreRanking = value;
                RaisePropertyChanged("EstimateOfflineScoreRanking");
            }
        }

        public bool EnableDownloadLr2IrScoreAndDetectUnsent
        {
            get
            {
                return Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent;
            }
            set
            {
                if (Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent != value)
                {
                    Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = value;
                    RaisePropertyChanged("EnableDownloadLr2IrScoreAndDetectUnsent");
                }
            }
        }

        public bool UpdateLr2IrRankingCacheOnStartup
        {
            get
            {
                return Settings.Default.UpdateLr2IrRankingCacheOnStartup;
            }
            set
            {
                if (Settings.Default.UpdateLr2IrRankingCacheOnStartup == value)
                {
                    return;
                }
                if (value)
                {
                    if (!MainWindowViewModel.ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_enable_lr2ir_ranking_cache_startup_update, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "LR2IR ranking cache startup update confirmation"))
                    {
                        return;
                    }
                }
                Settings.Default.UpdateLr2IrRankingCacheOnStartup = value;
                RaisePropertyChanged("UpdateLr2IrRankingCacheOnStartup");
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public string FolderNameFormat
        {
            get
            {
                if (!IsFolderNameFormatValid())
                {
                    return defaultFolderNameFormat;
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
                        MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidFolderNameFormat, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation);
                    }
                    RaisePropertyChanged("FolderNameFormat");
                    RaiseValidationStateChanged();
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
                        MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Warn_DisableShiftJisFolderNames, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation);
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

        public ReadOnlyObservableCollection<string> EncoderNames => new(["WAVE", "MP3 LAME", "AAC Nero", "Opus", "FLAC", "Ogg Vorbis"]);

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
                        MainWindowViewModel.ShowUiMessage(
                            FormatResource(BeMusicSeeker.Properties.Resources.Warn_EncoderExecutableNotFoundFormat, ((EncoderType)value).GetEncoderFileName()),
                            BeMusicSeeker.Properties.Resources.Warning,
                            MessageBoxImage.Exclamation);
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

        public ReadOnlyDictionary<BMSAutoPlayWriter.Normalization, string> EncoderNormalizationNames => new(new Dictionary<BMSAutoPlayWriter.Normalization, string>
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
                    return defaultEncodeFileNameFormat;
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

        public ReadOnlyObservableCollection<string> PlayerDriverNames => new(
        [
            "DirectSound",
            "WASAPI (" + BeMusicSeeker.Properties.Resources.Shared + ")",
            "WASAPI (" + BeMusicSeeker.Properties.Resources.Exclusive + ")",
            "ASIO"
        ]);

        public int PlayerDriverIndex
        {
            get
            {
                return (int)NormalizePlayerDriver(Settings.Default.PlayerDriver);
            }
            set
            {
                BassAudioPlayer.DeviceDriver driver = NormalizePlayerDriver((BassAudioPlayer.DeviceDriver)value);
                if (Settings.Default.PlayerDriver != driver)
                {
                    Settings.Default.PlayerDriver = driver;
                    playerDeviceNames = [.. BassAudioPlayer.DeviceList[Settings.Default.PlayerDriver]];
                    RaisePropertyChanged("PlayerDriverIndex");
                    RaisePropertyChanged(() => PlayerDeviceNames);
                    RaisePropertyChanged(() => PlayerDevice);
                }
            }
        }

        private static BassAudioPlayer.DeviceDriver NormalizePlayerDriver(BassAudioPlayer.DeviceDriver driver)
        {
            if (driver < BassAudioPlayer.DeviceDriver.DIRECT_SOUND || driver > BassAudioPlayer.DeviceDriver.ASIO)
            {
                return BassAudioPlayer.DeviceDriver.DIRECT_SOUND;
            }
            return driver;
        }

        public List<BassAudioPlayer.DeviceDescriptor> PlayerDeviceNames
        {
            get
            {
                return playerDeviceNames ??= [.. BassAudioPlayer.DeviceList[NormalizePlayerDriver(Settings.Default.PlayerDriver)]];
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
                return ResolvePlayerDeviceDescriptor().Driver ?? Settings.Default.PlayerDevice;
            }
            set
            {
                if (value == null
                    && !string.IsNullOrWhiteSpace(Settings.Default.PlayerDevice)
                    && !PlayerDeviceNames.Any(d => string.Equals(d.Driver, Settings.Default.PlayerDevice, StringComparison.Ordinal)))
                {
                    return;
                }
                if (string.Equals(Settings.Default.PlayerDevice, value, StringComparison.Ordinal))
                {
                    return;
                }
                BassAudioPlayer.DeviceDescriptor deviceDescriptor = PlayerDeviceNames.FirstOrDefault(d => d.Driver == value);
                Settings.Default.PlayerDevice = deviceDescriptor.Driver;
                Settings.Default.PlayerDeviceName = deviceDescriptor.Name;
                RaisePropertyChanged("PlayerDevice");
            }
        }

        private BassAudioPlayer.DeviceDescriptor ResolvePlayerDeviceDescriptor()
        {
            BassAudioPlayer.DeviceDescriptor deviceDescriptor = PlayerDeviceNames.FirstOrDefault(d => d.Driver == Settings.Default.PlayerDevice);
            if (deviceDescriptor.Driver == null && Settings.Default.PlayerDevice != null)
            {
                deviceDescriptor = default;
            }
            if (deviceDescriptor.Driver == null)
            {
                deviceDescriptor = PlayerDeviceNames.FirstOrDefault(d => d.Name == Settings.Default.PlayerDeviceName);
                if (deviceDescriptor.Driver == null)
                {
                    Match match = Regex.Match(Settings.Default.PlayerDeviceName ?? string.Empty, "(.*?)[\\s(\\d-]+(.*?)\\)");
                    if (match.Success)
                    {
                        string p1 = Regex.Escape(match.Groups[1].ToString());
                        string p2 = Regex.Escape(match.Groups[2].ToString());
                        deviceDescriptor = PlayerDeviceNames.FirstOrDefault(d => Regex.IsMatch(d.Name ?? string.Empty, p1 + ".*" + p2));
                    }
                }
            }
            return deviceDescriptor;
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

        public ListenerCommand<string> RemoveDirCommand
        {
            get
            {
                _RemoveDirCommand ??= new ListenerCommand<string>(RemoveBMSDirectoryFromSearchRoots);
                return _RemoveDirCommand;
            }
        }

        public List<string> Languages => [.. App.AvailableCultures.Keys];

        public string Language
        {
            get
            {
                // 表示名が保存されている場合はそれを優先（同一カルチャ名の重複対策）
                string savedDisplayName = Settings.Default.LangDisplayName;
                if (!string.IsNullOrEmpty(savedDisplayName) && App.AvailableCultures.ContainsKey(savedDisplayName))
                    return savedDisplayName;
                return App.AvailableCultures.FirstOrDefault(kv => kv.Value == Settings.Default.Lang).Key;
            }
            set
            {
                if (!App.AvailableCultures.TryGetValue(value ?? string.Empty, out string text))
                {
                    return;
                }
                if (string.Equals(Settings.Default.Lang, text, StringComparison.Ordinal)
                    && string.Equals(Settings.Default.LangDisplayName, value, StringComparison.Ordinal))
                {
                    return;
                }
                Settings.Default.Lang = text;
                Settings.Default.LangDisplayName = value;
                ResourceService.Current.ChangeCulture(text);
                RaisePropertyChanged("Language");
            }
        }

        public SettingDialogViewModel(MainWindowViewModel owner)
        {
            SettingDialogViewModel settingDialogViewModel = this;
            ownerViewModel = owner;
            appearanceThemeOptions =
            [
                new AppearanceThemeOption(AppThemeService.Light),
                new AppearanceThemeOption(AppThemeService.Dark)
            ];
            beatorajaBmtHashOutputModeOptions =
            [
                new BeatorajaBmtHashOutputModeOption(BeMusicSeeker.Models.BeatorajaBmtHashOutputMode.Original),
                new BeatorajaBmtHashOutputModeOption(BeMusicSeeker.Models.BeatorajaBmtHashOutputMode.FillMissingMd5Sha256),
                new BeatorajaBmtHashOutputModeOption(BeMusicSeeker.Models.BeatorajaBmtHashOutputMode.PreferSha256Only)
            ];
            ownerViewModelEventListener = new PropertyChangedEventListener(ownerViewModel);
            ownerViewModelEventListener.RegisterHandler(() => owner.BMSTables, delegate
            {
                settingDialogViewModel.RaisePropertyChanged(() => settingDialogViewModel.LR2ConfigBMSDirectories);
                settingDialogViewModel.RaisePropertyChanged(() => settingDialogViewModel.AvailableBMSDirectories);
                settingDialogViewModel.MarkPlayHistoryFolderDisplayPresetPlaylistOptionsDirty();
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
            resourceServiceEventListener.RegisterHandler(() => ResourceService.Current.Resources, delegate
            {
                foreach (AppearanceThemeOption option in settingDialogViewModel.appearanceThemeOptions)
                {
                    option.RefreshDisplayName();
                }
                foreach (BeatorajaBmtHashOutputModeOption option in settingDialogViewModel.beatorajaBmtHashOutputModeOptions)
                {
                    option.RefreshDisplayName();
                }
                ownerViewModel.RefreshPlayHistoryDisplayTargets(queueRefreshWhenSelectionChanges: false);
            });
            Settings.Default.Reload();
            if (Settings.Default.OperationModeLR2DB)
            {
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
            else
            {
                lr2config = null;
            }
            RefreshStandaloneBmsRootPathsFromSettings();
            RefreshBeatorajaDerivedSettings();
            backupSavedSettings();
            ResetLr2PlayHistorySchemaStatus();
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

        private bool IsLR2PlayerRootPathValid()
        {
            return IsLR2PlayerRootPathValid(Settings.Default.LR2RootPath);
        }

        private bool IsLR2PlayerRootPathValid(string value)
        {
            if (value == null)
            {
                return false;
            }
            string path = Path.Combine(value, "LR2body.exe");
            string path2 = Path.Combine(value, "LRHbody.exe");
            string value2 = Path.Combine(value, "LR2files\\Config\\config.xml");
            string value3 = Path.Combine(value, "LR2files\\Config\\config.xmh");
            return Directory.Exists(value)
                && (File.Exists(path) || File.Exists(path2))
                && (IsLR2ConfigXmlPathValid(value2) || IsLR2ConfigXmlPathValid(value3));
        }

        private bool IsBMSRootPathValid()
        {
            return IsBMSRootPathValid(Settings.Default.BMSRootPath);
        }

        private bool IsBMSRootPathValid(string value)
        {
            return LongPathFileSystem.DirectoryExists(value)
                && Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(value);
        }

        public static IReadOnlyList<string> GetStandaloneBmsRootPathsFromSettings()
        {
            return StandaloneBmsRootPathSettings.Deserialize(
                Settings.Default.StandaloneBmsRootPaths,
                Settings.Default.BMSRootPath);
        }

        public static IReadOnlyList<string> DeserializeStandaloneBmsRootPaths(string serializedPaths, string legacyBmsRootPath = null)
        {
            return StandaloneBmsRootPathSettings.Deserialize(serializedPaths, legacyBmsRootPath);
        }

        public static IReadOnlyList<string> NormalizeStandaloneBmsRootPaths(IEnumerable<string> paths)
        {
            return StandaloneBmsRootPathSettings.Normalize(paths);
        }

        private static IReadOnlyList<string> NormalizeExistingStandaloneBmsRootPaths(IEnumerable<string> paths)
        {
            return StandaloneBmsRootPathSettings.Normalize(paths);
        }

        private static IReadOnlyList<string> NormalizeExistingStandaloneBmsRootPathsWithoutLr2Compatibility(IEnumerable<string> paths)
        {
            return StandaloneBmsRootPathSettings.Normalize(paths);
        }

        private static List<string> GetLr2IncompatibleStandaloneBmsRootPaths(IEnumerable<string> paths)
        {
            return [.. (paths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path => !Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(path))];
        }

        private static void ThrowIfLr2IncompatibleStandaloneBmsRoots(IEnumerable<string> paths)
        {
            List<string> incompatiblePaths = GetLr2IncompatibleStandaloneBmsRootPaths(paths);
            if (incompatiblePaths.Count == 0)
            {
                return;
            }
            throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_Lr2IncompatibleBmsRootPath + Environment.NewLine + string.Join(Environment.NewLine, incompatiblePaths));
        }

        public static string SerializeStandaloneBmsRootPaths(IEnumerable<string> paths)
        {
            return StandaloneBmsRootPathSettings.Serialize(paths);
        }

        private static string SerializeBmsRootPathsForChangeTracking(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return string.Empty;
            }

            List<string> normalized = [];
            foreach (string path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(path.Trim());
                }
                catch
                {
                    fullPath = path.Trim();
                }
                fullPath = TrimDirectorySeparatorUnlessRoot(fullPath);
                if (!normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(fullPath);
                }
            }

            return string.Join(Environment.NewLine, normalized);
        }

        private static bool IsSameOrChildPath(string candidate, string parent)
        {
            if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string parentWithSeparator = TrimDirectorySeparatorUnlessRoot(parent) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static string TrimDirectorySeparatorUnlessRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
            string root = Path.GetPathRoot(path);
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, root?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? root
                : trimmed;
        }

        private void RefreshStandaloneBmsRootPathsFromSettings()
        {
            IReadOnlyList<string> paths = GetStandaloneBmsRootPathsFromSettings();
            bool pathsChanged = !StandaloneBmsRootPathList.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase);
            string selectedPath = paths.FirstOrDefault();
            bool selectedChanged = !string.Equals(SelectedStandaloneBmsRootPath, selectedPath, StringComparison.OrdinalIgnoreCase);
            if (!pathsChanged && !selectedChanged)
            {
                return;
            }
            if (pathsChanged)
            {
                StandaloneBmsRootPathList.Clear();
                foreach (string path in paths)
                {
                    StandaloneBmsRootPathList.Add(path);
                }
                RaisePropertyChanged(() => StandaloneBmsRootPathList);
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaisePropertyChanged(() => SelectedBmsSearchRootPath);
            }
            if (selectedChanged)
            {
                SelectedStandaloneBmsRootPath = selectedPath;
            }
            RaiseValidationStateChanged();
        }

        private void PersistStandaloneBmsRootPathsToSettings()
        {
            Settings.Default.StandaloneBmsRootPaths = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
            string firstRoot = DeserializeStandaloneBmsRootPaths(Settings.Default.StandaloneBmsRootPaths).FirstOrDefault();
            Settings.Default.BMSRootPath = string.IsNullOrWhiteSpace(firstRoot) ? null : firstRoot;
        }

        private void RefreshCustomFolderAdditionalOutputBaseDirsFromSettings()
        {
            IReadOnlyList<string> paths = CustomFolderOutputBaseRegistry.ReadAdditionalBaseDirectories();
            bool pathsChanged = !CustomFolderAdditionalOutputBaseDirList.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase);
            string selectedPath = paths.FirstOrDefault();
            bool selectedChanged = !string.Equals(selectedCustomFolderAdditionalOutputBaseDir, selectedPath, StringComparison.OrdinalIgnoreCase);
            bool pendingRenamesChanged = pendingCustomFolderAdditionalOutputBaseRenames.Count > 0;
            if (!pathsChanged && !selectedChanged && !pendingRenamesChanged)
            {
                return;
            }
            if (pathsChanged)
            {
                CustomFolderAdditionalOutputBaseDirList.Clear();
                foreach (string path in paths)
                {
                    CustomFolderAdditionalOutputBaseDirList.Add(path);
                }
            }
            if (selectedChanged)
            {
                SelectedCustomFolderAdditionalOutputBaseDir = selectedPath;
            }
            if (pendingRenamesChanged)
            {
                pendingCustomFolderAdditionalOutputBaseRenames.Clear();
            }
            RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
        }

        /// <summary>
        /// 選択中プリセットに対応する playlist 候補リストを再構築します。
        /// playlist tree の再読み込み後でも draft の選択状態を維持するため、保存済み参照と現在の BMSTable を照合します。
        /// </summary>
        internal void RefreshPlayHistoryFolderDisplayPresetPlaylistOptions()
        {
            isPlayHistoryFolderDisplayPresetPlaylistOptionsDirty = false;
            PlayHistoryFolderDisplayPresetPlaylistOptions.Clear();
            PlayHistoryFolderDisplayPresetEditor preset = SelectedPlayHistoryFolderDisplayPreset;
            if (preset != null)
            {
                PlayHistoryFolderDisplayPresetSelectionIndex selectionIndex = PlayHistoryFolderDisplayPresetSelectionIndex.Create(preset.Targets);
                foreach (BMSTable table in GetPlayHistoryFolderDisplayPresetTables())
                {
                    PlayHistoryFolderDisplayPresetPlaylistOptions.Add(new PlayHistoryFolderPresetPlaylistOption(
                        table,
                        selectionIndex.Matches(table),
                        ApplyPlayHistoryFolderDisplayPresetPlaylistSelection));
                }
            }
            RaisePropertyChanged(() => PlayHistoryFolderDisplayPresetPlaylistOptions);
        }

        internal void RefreshPlayHistoryFolderDisplayPresetPlaylistOptionsIfDirty()
        {
            if (isPlayHistoryFolderDisplayPresetPlaylistOptionsDirty)
            {
                RefreshPlayHistoryFolderDisplayPresetPlaylistOptions();
            }
        }

        private void MarkPlayHistoryFolderDisplayPresetPlaylistOptionsDirty()
        {
            isPlayHistoryFolderDisplayPresetPlaylistOptionsDirty = true;
        }

        private IReadOnlyList<BMSTable> GetPlayHistoryFolderDisplayPresetTables()
        {
            try
            {
                ownerViewModel.tables?.AcquireReaderLockBMSTables();
                return [.. (ownerViewModel.BMSTables ?? Enumerable.Empty<BMSTable>())
                    .Where(table => table?.playlist_id != null)
                    .OrderBy(table => table.name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(table => table.symbol ?? string.Empty, StringComparer.OrdinalIgnoreCase)];
            }
            finally
            {
                ownerViewModel.tables?.FreeReaderLockBMSTables();
            }
        }

        private void RefreshPlayHistoryFolderDisplayPresetsFromSettings()
        {
            PlayHistoryFolderDisplayPresets.Clear();
            foreach (PlayHistoryDisplayTargetSet targetSet in PlayHistoryDisplayTargetSetStore.Deserialize(Settings.Default.PlayHistoryDisplayTargetSetsJson))
            {
                PlayHistoryFolderDisplayPresets.Add(new PlayHistoryFolderDisplayPresetEditor(targetSet.Name, targetSet.Targets));
            }
            selectedPlayHistoryFolderDisplayPreset = PlayHistoryFolderDisplayPresets.FirstOrDefault();
            PlayHistoryFolderDisplayPresetPlaylistOptions.Clear();
            MarkPlayHistoryFolderDisplayPresetPlaylistOptionsDirty();
            RaisePlayHistoryFolderDisplayPresetPropertiesChanged();
        }

        private string SerializePlayHistoryFolderDisplayPresets()
        {
            HashSet<int> existingPlaylistIds = [.. GetPlayHistoryFolderDisplayPresetTables()
                .Select(table => table.playlist_id.Value)];
            return PlayHistoryDisplayTargetSetStore.Serialize(
                PlayHistoryFolderDisplayPresets
                    .Select(preset => FilterPlayHistoryFolderDisplayPresetTargets(preset.ToTargetSet(), existingPlaylistIds)));
        }

        private static PlayHistoryDisplayTargetSet FilterPlayHistoryFolderDisplayPresetTargets(
            PlayHistoryDisplayTargetSet targetSet,
            ISet<int> existingPlaylistIds)
        {
            return new PlayHistoryDisplayTargetSet
            {
                Name = targetSet?.Name,
                Targets =
                [
                    .. (targetSet?.Targets ?? [])
                        .Where(reference => reference?.PlaylistId is int playlistId && existingPlaylistIds.Contains(playlistId))
                        .Select(reference => new PlayHistoryDisplayTargetReference
                        {
                            PlaylistId = reference.PlaylistId
                        })
                ]
            };
        }

        private string SerializePlayHistoryFolderDisplayPresetDraftsForChangeTracking()
        {
            return PlayHistoryDisplayTargetSetStore.SerializeDraftsForChangeTracking(PlayHistoryFolderDisplayPresets.Select(preset => preset.ToTargetSet()));
        }

        /// <summary>
        /// play history FOLDER 表示プリセットの draft を設定へ保存し、必要な場合だけ表示対象 dropdown を再構築します。
        /// 保存済み JSON と保存用に正規化した draft の差分だけを見るため、無関係な設定保存で PlayHistory 表示を再 filter しません。
        /// </summary>
        /// <returns>保存済み表示対象セットが変更された場合は <c>true</c>。</returns>
        internal bool PersistPlayHistoryFolderDisplayPresetsIfChanged()
        {
            string serializedDisplayTargetSets = SerializePlayHistoryFolderDisplayPresets();
            bool playHistoryDisplayTargetSetsChanged =
                !string.Equals(tempPlayHistoryDisplayTargetSetsJson, serializedDisplayTargetSets, StringComparison.Ordinal);
            Settings.Default.PlayHistoryDisplayTargetSetsJson = serializedDisplayTargetSets;
            if (playHistoryDisplayTargetSetsChanged)
            {
                ownerViewModel.RefreshPlayHistoryDisplayTargetSetsFromSettings(queueRefreshWhenSelectionChanges: true);
            }
            return playHistoryDisplayTargetSetsChanged;
        }

        /// <summary>
        /// 新しい play history FOLDER 表示プリセットを draft に追加します。
        /// </summary>
        public void AddPlayHistoryFolderDisplayPreset()
        {
            PlayHistoryFolderDisplayPresetEditSession session = CreatePlayHistoryFolderDisplayPresetEditSession(null);
            PlayHistoryFolderPresetPlaylistOption firstOption = session.PlaylistOptions.FirstOrDefault();
            if (firstOption != null)
            {
                firstOption.IsSelected = true;
            }
            TryApplyPlayHistoryFolderDisplayPresetEditSession(session, out _);
        }

        /// <summary>
        /// 選択中の play history FOLDER 表示プリセットを draft から削除します。
        /// </summary>
        public void RemoveSelectedPlayHistoryFolderDisplayPreset()
        {
            PlayHistoryFolderDisplayPresetEditor preset = SelectedPlayHistoryFolderDisplayPreset;
            if (preset == null)
            {
                return;
            }
            int index = PlayHistoryFolderDisplayPresets.IndexOf(preset);
            PlayHistoryFolderDisplayPresets.Remove(preset);
            SelectedPlayHistoryFolderDisplayPreset = PlayHistoryFolderDisplayPresets.Count == 0
                ? null
                : PlayHistoryFolderDisplayPresets[Math.Max(0, Math.Min(index, PlayHistoryFolderDisplayPresets.Count - 1))];
            RaisePlayHistoryFolderDisplayPresetPropertiesChanged();
        }

        /// <summary>
        /// チェックボックス一覧の選択状態を選択中プリセットへ反映します。
        /// </summary>
        public void EditSelectedPlayHistoryFolderDisplayPreset()
        {
            ApplyPlayHistoryFolderDisplayPresetPlaylistSelection();
            RaisePlayHistoryFolderDisplayPresetPropertiesChanged();
        }

        /// <summary>
        /// 別ウィンドウで編集する play history FOLDER 表示プリセットの一時セッションを作成します。
        /// </summary>
        /// <param name="preset">編集元プリセット。新規追加時は <c>null</c>。</param>
        /// <returns>元 draft を直接変更しない編集セッション。</returns>
        public PlayHistoryFolderDisplayPresetEditSession CreatePlayHistoryFolderDisplayPresetEditSession(PlayHistoryFolderDisplayPresetEditor preset)
        {
            IEnumerable<PlayHistoryDisplayTargetReference> targets = preset?.Targets ?? [];
            PlayHistoryFolderDisplayPresetSelectionIndex selectionIndex = PlayHistoryFolderDisplayPresetSelectionIndex.Create(targets);
            string sessionName = preset == null ? CreateUniquePlayHistoryFolderDisplayPresetName() : preset.Name;
            return new PlayHistoryFolderDisplayPresetEditSession(
                preset,
                sessionName,
                GetPlayHistoryFolderDisplayPresetTables()
                    .Select(table => new PlayHistoryFolderPresetPlaylistOption(
                        table,
                        selectionIndex.Matches(table),
                        _ => RaiseValidationStateChanged())));
        }

        /// <summary>
        /// 別ウィンドウの編集セッションを設定ダイアログの draft へ反映します。
        /// </summary>
        /// <param name="session">編集セッション。</param>
        /// <param name="errMsg">反映できない場合の理由。</param>
        /// <returns>反映できた場合は <c>true</c>。</returns>
        public bool TryApplyPlayHistoryFolderDisplayPresetEditSession(PlayHistoryFolderDisplayPresetEditSession session, out string errMsg)
        {
            if (!ValidatePlayHistoryFolderDisplayPresetEditSession(session, out errMsg))
            {
                return false;
            }
            PlayHistoryDisplayTargetSet targetSet = session.ToTargetSet();
            PlayHistoryFolderDisplayPresetEditor preset = session.SourcePreset;
            if (preset == null)
            {
                preset = new PlayHistoryFolderDisplayPresetEditor(targetSet.Name, targetSet.Targets);
                PlayHistoryFolderDisplayPresets.Add(preset);
            }
            else
            {
                preset.Name = targetSet.Name;
                preset.Targets.Clear();
                foreach (PlayHistoryDisplayTargetReference reference in targetSet.Targets)
                {
                    preset.Targets.Add(reference);
                }
            }
            SelectedPlayHistoryFolderDisplayPreset = preset;
            PlayHistoryFolderDisplayPresetPlaylistOptions.Clear();
            MarkPlayHistoryFolderDisplayPresetPlaylistOptionsDirty();
            RaisePlayHistoryFolderDisplayPresetPropertiesChanged();
            return true;
        }

        private void ApplyPlayHistoryFolderDisplayPresetPlaylistSelection(PlayHistoryFolderPresetPlaylistOption option)
        {
            ApplyPlayHistoryFolderDisplayPresetPlaylistSelection();
        }

        private void ApplyPlayHistoryFolderDisplayPresetPlaylistSelection()
        {
            PlayHistoryFolderDisplayPresetEditor preset = SelectedPlayHistoryFolderDisplayPreset;
            if (preset == null)
            {
                return;
            }
            preset.Targets.Clear();
            foreach (PlayHistoryFolderPresetPlaylistOption option in PlayHistoryFolderDisplayPresetPlaylistOptions.Where(option => option.IsSelected))
            {
                preset.Targets.Add(option.ToReference());
            }
            RaiseValidationStateChanged();
        }

        private string CreateUniquePlayHistoryFolderDisplayPresetName()
        {
            string baseName = BeMusicSeeker.Properties.Resources.Play_history_folder_display_preset_default_name;
            var names = new HashSet<string>(
                PlayHistoryFolderDisplayPresets.Select(preset => preset?.Name).Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName))
            {
                return baseName;
            }
            for (int suffix = 2; suffix < int.MaxValue; suffix++)
            {
                string candidate = baseName + " " + suffix.ToString(CultureInfo.CurrentCulture);
                if (!names.Contains(candidate))
                {
                    return candidate;
                }
            }
            return baseName;
        }

        private bool ValidatePlayHistoryFolderDisplayPresetEditSession(PlayHistoryFolderDisplayPresetEditSession session, out string errMsg)
        {
            errMsg = string.Empty;
            if (session == null)
            {
                errMsg = BeMusicSeeker.Properties.Resources.Error_PlayHistoryFolderPresetNameEmpty;
                return false;
            }
            string name = session.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                errMsg = BeMusicSeeker.Properties.Resources.Error_PlayHistoryFolderPresetNameEmpty;
                return false;
            }
            if (PlayHistoryFolderDisplayPresets.Any(preset =>
                !ReferenceEquals(preset, session.SourcePreset)
                && string.Equals(preset?.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                errMsg = FormatResource(BeMusicSeeker.Properties.Resources.Error_PlayHistoryFolderPresetDuplicateName, name);
                return false;
            }
            if (!session.PlaylistOptions.Any(option => option.IsSelected))
            {
                errMsg = FormatResource(BeMusicSeeker.Properties.Resources.Error_PlayHistoryFolderPresetNoPlaylist, name);
                return false;
            }
            return true;
        }

        private bool ValidatePlayHistoryFolderDisplayPresets(out string errMsg)
        {
            errMsg = string.Empty;
            bool result = true;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlayHistoryFolderDisplayPresetEditor preset in PlayHistoryFolderDisplayPresets)
            {
                string name = preset?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_PlayHistoryFolderPresetNameEmpty) + Environment.NewLine;
                    result = false;
                    continue;
                }
                if (!names.Add(name))
                {
                    errMsg += FormatPlaylistValidationMessage(FormatResource(BeMusicSeeker.Properties.Resources.Error_PlayHistoryFolderPresetDuplicateName, name)) + Environment.NewLine;
                    result = false;
                }
                if (preset.Targets.Count == 0)
                {
                    errMsg += FormatPlaylistValidationMessage(FormatResource(BeMusicSeeker.Properties.Resources.Error_PlayHistoryFolderPresetNoPlaylist, name)) + Environment.NewLine;
                    result = false;
                }
            }
            return result;
        }

        private void RaisePlayHistoryFolderDisplayPresetPropertiesChanged()
        {
            RaisePropertyChanged(() => PlayHistoryFolderDisplayPresets);
            RaisePropertyChanged(() => SelectedPlayHistoryFolderDisplayPreset);
            RaisePropertyChanged(() => CanEditPlayHistoryFolderDisplayPreset);
            RaisePropertyChanged(() => CanRemovePlayHistoryFolderDisplayPreset);
            RaiseValidationStateChanged();
        }

        private sealed class PlayHistoryFolderDisplayPresetSelectionIndex
        {
            private readonly HashSet<int> playlistIds = [];

            private PlayHistoryFolderDisplayPresetSelectionIndex()
            {
            }

            /// <summary>
            /// 保存済み参照から候補 playlist の選択判定用 index を作成します。
            /// </summary>
            /// <param name="references">選択中プリセットに保存されている playlist 参照。</param>
            /// <returns>playlist id を事前集計した selection index。</returns>
            public static PlayHistoryFolderDisplayPresetSelectionIndex Create(IEnumerable<PlayHistoryDisplayTargetReference> references)
            {
                var index = new PlayHistoryFolderDisplayPresetSelectionIndex();
                foreach (PlayHistoryDisplayTargetReference reference in references ?? [])
                {
                    if (reference == null)
                    {
                        continue;
                    }
                    if (reference.PlaylistId.HasValue)
                    {
                        index.playlistIds.Add(reference.PlaylistId.Value);
                    }
                }
                return index;
            }

            /// <summary>
            /// 指定された playlist が index 内の参照に一致するかどうかを判定します。
            /// </summary>
            /// <param name="table">候補 playlist。</param>
            /// <returns>一致する場合は <c>true</c>。</returns>
            public bool Matches(BMSTable table)
            {
                if (table == null)
                {
                    return false;
                }
                if (table.playlist_id.HasValue && playlistIds.Contains(table.playlist_id.Value))
                {
                    return true;
                }
                return false;
            }
        }

        private void PersistCustomFolderAdditionalOutputBaseDirsToSettings()
        {
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories(CustomFolderAdditionalOutputBaseDirList);
        }

        private void RaiseCustomFolderAdditionalOutputBasePropertiesChanged()
        {
            RaisePropertyChanged(() => CustomFolderAdditionalOutputBaseDirList);
            RaisePropertyChanged(() => SelectedCustomFolderAdditionalOutputBaseDir);
            RaisePropertyChanged(() => SelectedCustomFolderAdditionalOutputBaseName);
            RaisePropertyChanged(() => LR2ConfigBMSDirectories);
            RaisePropertyChanged(() => AvailableBMSDirectories);
            RaisePropertyChanged(() => SelectedBmsSearchRootPath);
            RaisePropertyChanged(() => PlaylistPropertyOutputBaseOptions);
            RaiseValidationStateChanged();
        }

        public IReadOnlyList<PlaylistCustomFolderOutputBaseOption> PlaylistPropertyOutputBaseOptions =>
            MainWindowViewModel.CreatePlaylistCustomFolderOutputBaseOptions(
                LR2CustomFolderOutputDir,
                CustomFolderAdditionalOutputBaseDirList);

        private void ApplyRuntimeSearchRootsForCurrentMode()
        {
            if (ownerViewModel.files == null)
            {
                return;
            }
            if (Settings.Default.OperationModeLR2DB && lr2config != null)
            {
                ownerViewModel.files.SearchTargets = [.. lr2config.GetBMSSearchDirectories()];
                return;
            }
            ownerViewModel.files.SearchTargets = [.. GetStandaloneBmsRootPathsFromSettings()];
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

        private bool IsBeatorajaScoreDbPathValid()
        {
            return BeatorajaConfigService.IsPlayerScoreDbPathValid(Settings.Default.BeatorajaRootPath, Settings.Default.BeatorajaPlayerId);
        }

        private bool IsBeatorajaScoreDbPathValid(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && string.Equals(Path.GetFileName(value), "score.db", StringComparison.OrdinalIgnoreCase)
                && File.Exists(value);
        }

        private bool IsBeatorajaBmtTablePathValid()
        {
            return IsBeatorajaBmtTablePathValid(Settings.Default.BeatorajaBmtTablePath);
        }

        private bool IsBeatorajaBmtTablePathValid(string value)
        {
            return string.IsNullOrWhiteSpace(value) || Directory.Exists(value);
        }

        private bool IsBeatorajaRootPathValid()
        {
            return BeatorajaConfigService.IsBeatorajaRootPathValid(Settings.Default.BeatorajaRootPath);
        }

        private void RefreshBeatorajaDerivedSettings()
        {
            if (!BeatorajaConfigService.IsBeatorajaRootPathValid(Settings.Default.BeatorajaRootPath))
            {
                Settings.Default.BeatorajaScoreDbPath = string.Empty;
                Settings.Default.BeatorajaBmtTablePath = string.Empty;
                return;
            }
            List<string> playerIds = BeatorajaConfigService.GetPlayerIds(Settings.Default.BeatorajaRootPath);
            if (string.IsNullOrWhiteSpace(Settings.Default.BeatorajaPlayerId) || !playerIds.Contains(Settings.Default.BeatorajaPlayerId, StringComparer.OrdinalIgnoreCase))
            {
                string configuredPlayerId = BeatorajaConfigService.GetConfiguredPlayerId(Settings.Default.BeatorajaRootPath);
                Settings.Default.BeatorajaPlayerId = playerIds.Contains(configuredPlayerId, StringComparer.OrdinalIgnoreCase)
                    ? configuredPlayerId
                    : (playerIds.FirstOrDefault() ?? string.Empty);
            }
            Settings.Default.BeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(Settings.Default.BeatorajaRootPath, Settings.Default.BeatorajaPlayerId);
            Settings.Default.BeatorajaBmtTablePath = BeatorajaConfigService.GetTablePath(Settings.Default.BeatorajaRootPath);
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
            return ValidateCustomFolderOutputBaseDir(value, out _);
        }

        private bool IsBMSInstallDirValid()
        {
            return IsBMSInstallDirValid(Settings.Default.BMSInstallDir);
        }

        private bool IsBMSInstallDirValid(string value)
        {
            if (value == null)
            {
                return false;
            }
            if (OperationModeLR2DB && lr2config != null)
            {
                return GetExistingLR2UserBmsSearchRootDirectories().Contains(value, StringComparer.OrdinalIgnoreCase);
            }
            return NormalizeStandaloneBmsRootPaths(StandaloneBmsRootPathList).Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        private bool IsLR2CustomFolderAsRootOutputDirValid()
        {
            return IsLR2CustomFolderAsRootOutputDirValid(Settings.Default.LR2CustomFolderOutputBaseDirRootType);
        }

        private bool IsLR2CustomFolderAsRootOutputDirValid(string value)
        {
            return ValidateCustomFolderAsRootOutputBaseDir(value, out _);
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
            return PlaylistUrlCompletionSupport.TryResolveSourceUri(value, out Uri _);
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

        public void SetRootFolderPathFromPicker(string propertyName, string path)
        {
            if (path == null)
            {
                return;
            }
            try
            {
                if (!path.IsSjisSchemeString())
                {
                    throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_LR2UnicodePathUnsupported);
                }
                SetSettingProperty(propertyName, path);
            }
            catch (ArgumentException ex)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
            catch (Exception ex)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
        }

        public void SetFilePathFromPicker(string propertyName, string path)
        {
            if (path == null)
                return;

            SetSettingProperty(propertyName, path);
        }

        public void SetDirectoryPathFromPicker(string propertyName, string path)
        {
            if (path == null)
                return;

            SetSettingProperty(propertyName, path);
        }

        public void AddBmsSearchRootPathFromMainWindowPicker(string path)
        {
            if (path == null)
            {
                return;
            }
            AddBmsSearchRootPaths([path], null, saveImmediately: true);
            if (isSearchRootsChanged)
            {
                if (!Settings.Default.OperationModeLR2DB)
                {
                    PersistStandaloneBmsRootPathsToSettings();
                    Settings.Default.Save();
                }
                ApplyRuntimeSearchRootsForCurrentMode();
                if (isBMSDirectoryAdded)
                {
                    ownerViewModel.ReloadFileDiff();
                }
                else
                {
                    ownerViewModel.NotifyBmsParentFolderListChanged();
                }
                isSearchRootsChanged = false;
                isBMSDirectoryAdded = false;
            }
            else
            {
                ownerViewModel.NotifyBmsParentFolderListChanged();
            }
        }

        public void AddBmsSearchRootPathFromPicker(string propertyName, string path)
        {
            if (path == null)
            {
                return;
            }
            AddBmsSearchRootPaths([path], ToSettingDialogPropertyPath(propertyName), saveImmediately: false);
        }

        private void SetSettingProperty(string propertyName, string value)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw new ArgumentException("Setting property name is required.", nameof(propertyName));
            }

            PropertyInfo property = GetType().GetProperty(propertyName)
                ?? throw new InvalidOperationException("Unknown setting dialog property: " + propertyName);
            MethodInfo setter = property.GetSetMethod()
                ?? throw new InvalidOperationException("Setting dialog property is not writable: " + propertyName);
            setter.Invoke(this, [value]);
        }

        private static string ToSettingDialogPropertyPath(string propertyName)
        {
            return string.IsNullOrWhiteSpace(propertyName) ? null : "settingDialog." + propertyName;
        }

        public void AddCustomFolderAdditionalOutputBaseDir(string path)
        {
            try
            {
                string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    return;
                }
                ValidateCustomFolderAdditionalOutputBasePath(normalizedPath, oldPath: null);
                if (!CustomFolderAdditionalOutputBaseDirList.Contains(normalizedPath, StringComparer.OrdinalIgnoreCase))
                {
                    CustomFolderAdditionalOutputBaseDirList.Add(normalizedPath);
                }
                SelectedCustomFolderAdditionalOutputBaseDir = normalizedPath;
                PersistCustomFolderAdditionalOutputBaseDirsToSettings();
                RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
        }

        public void RemoveSelectedCustomFolderAdditionalOutputBaseDir()
        {
            string selectedPath = SelectedCustomFolderAdditionalOutputBaseDir;
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }
            string baseName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(selectedPath);
            int referenceCount = CountPlaylistCustomFolderOutputBaseReferences(baseName);
            if (referenceCount > 0)
            {
                if (!MainWindowViewModel.ShowUiConfirmation(
                    FormatResource(BeMusicSeeker.Properties.Resources.Confirm_RemoveAdditionalOutputBaseReferencedFormat, referenceCount),
                    BeMusicSeeker.Properties.Resources.Confirm,
                    MessageBoxImage.Exclamation,
                    MessageBoxButton.OKCancel,
                    "Additional output base removal confirmation"))
                {
                    return;
                }
            }

            List<string> remaining = [.. CustomFolderAdditionalOutputBaseDirList
                .Where(path => !string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase))];
            CustomFolderAdditionalOutputBaseDirList.Clear();
            foreach (string path in remaining)
            {
                CustomFolderAdditionalOutputBaseDirList.Add(path);
            }
            SelectedCustomFolderAdditionalOutputBaseDir = CustomFolderAdditionalOutputBaseDirList.FirstOrDefault();
            PersistCustomFolderAdditionalOutputBaseDirsToSettings();
            RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
        }

        public void RenameSelectedCustomFolderAdditionalOutputBaseDir()
        {
            string selectedPath = SelectedCustomFolderAdditionalOutputBaseDir;
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }
            try
            {
                string oldName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(selectedPath);
                string newPath = CustomFolderOutputBaseRegistry.RenameLastDirectoryName(selectedPath, SelectedCustomFolderAdditionalOutputBaseName);
                string newName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(newPath);
                if (string.Equals(selectedPath, newPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                ValidateCustomFolderAdditionalOutputBasePath(newPath, selectedPath);
                for (int index = 0; index < CustomFolderAdditionalOutputBaseDirList.Count; index++)
                {
                    if (string.Equals(CustomFolderAdditionalOutputBaseDirList[index], selectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        CustomFolderAdditionalOutputBaseDirList[index] = newPath;
                        break;
                    }
                }
                TrackCustomFolderAdditionalOutputBaseRename(oldName, newName);
                SelectedCustomFolderAdditionalOutputBaseDir = newPath;
                PersistCustomFolderAdditionalOutputBaseDirsToSettings();
                RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
        }

        private void TrackCustomFolderAdditionalOutputBaseRename(string oldName, string newName)
        {
            oldName = CustomFolderOutputBaseRegistry.NormalizeBaseName(oldName);
            newName = CustomFolderOutputBaseRegistry.NormalizeBaseName(newName);
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string originalName = pendingCustomFolderAdditionalOutputBaseRenames.FirstOrDefault(pair =>
                string.Equals(pair.Value, oldName, StringComparison.OrdinalIgnoreCase)).Key;
            if (!string.IsNullOrWhiteSpace(originalName))
            {
                if (string.Equals(originalName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    pendingCustomFolderAdditionalOutputBaseRenames.Remove(originalName);
                }
                else
                {
                    pendingCustomFolderAdditionalOutputBaseRenames[originalName] = newName;
                }
                return;
            }

            pendingCustomFolderAdditionalOutputBaseRenames[oldName] = newName;
        }

        private void ValidateCustomFolderAdditionalOutputBasePath(string path, string oldPath)
        {
            string name = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(path);
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_CustomFolderOutputBaseNameEmpty);
            }
            string defaultName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(LR2CustomFolderOutputDir);
            if (!string.IsNullOrWhiteSpace(defaultName)
                && string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_AdditionalOutputBaseNameMatchesNormalOutput);
            }
            if (CustomFolderAdditionalOutputBaseDirList.Any(existingPath =>
                    !string.Equals(existingPath, oldPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(existingPath), name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_AdditionalOutputBaseNameDuplicate);
            }
            ValidateCustomFolderAdditionalOutputBaseSearchRootPath(path, oldPath);
        }

        private bool ValidateCustomFolderAdditionalOutputBaseDirs(out string errMsg)
        {
            errMsg = string.Empty;
            try
            {
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                string defaultName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(LR2CustomFolderOutputDir);
                if (!string.IsNullOrWhiteSpace(defaultName))
                {
                    names.Add(defaultName);
                }

                List<string> normalizedPaths = [.. CustomFolderAdditionalOutputBaseDirList
                    .Select(CustomFolderOutputBaseRegistry.NormalizeDirectoryPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))];
                IReadOnlyList<string> previousAdditionalOutputBasePaths =
                    CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(tempLR2CustomFolderAdditionalOutputBaseDirs);
                foreach (string path in normalizedPaths)
                {
                    string name = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(path);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        errMsg = FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Validation_AdditionalOutputBaseNameEmpty);
                        return false;
                    }
                    if (!names.Add(name))
                    {
                        errMsg = FormatPlaylistValidationMessage(FormatResource(BeMusicSeeker.Properties.Resources.Validation_OutputBaseNameDuplicateFormat, name));
                        return false;
                    }
                    string oldPath = previousAdditionalOutputBasePaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                        ? path
                        : null;
                    ValidateCustomFolderAdditionalOutputBaseSearchRootPath(path, oldPath);
                }

                for (int leftIndex = 0; leftIndex < normalizedPaths.Count; leftIndex++)
                {
                    for (int rightIndex = leftIndex + 1; rightIndex < normalizedPaths.Count; rightIndex++)
                    {
                        if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(normalizedPaths[leftIndex], normalizedPaths[rightIndex]))
                        {
                            errMsg = FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Validation_AdditionalOutputBasesNested);
                            return false;
                        }
                    }
                }
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
            {
                errMsg = FormatPlaylistValidationMessage(ex.Message);
                return false;
            }
        }

        private bool ValidateCustomFolderOutputBaseDir(out string errMsg)
        {
            return ValidateCustomFolderOutputBaseDir(LR2CustomFolderOutputDir, out errMsg);
        }

        private bool ValidateCustomFolderOutputBaseDir(string path, out string errMsg)
        {
            errMsg = string.Empty;
            try
            {
                string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
                CustomFolderOutputBaseSearchRootSyncService.ValidateSjisDirectoryPath(normalizedPath, BeMusicSeeker.Properties.Resources.Label_NormalOutputBaseFolder);

                string name = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(normalizedPath);
                if (string.IsNullOrWhiteSpace(name))
                {
                    errMsg = FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Validation_NormalOutputBaseNameEmpty);
                    return false;
                }

                foreach (string additionalPath in CustomFolderAdditionalOutputBaseDirList)
                {
                    if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(normalizedPath, additionalPath))
                    {
                        errMsg = FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Validation_NormalAndAdditionalOutputBasesNested);
                        return false;
                    }
                }

                ValidateCustomFolderOutputBaseIsNotNestedWithPreviousManagedRoot(
                    normalizedPath,
                    BeMusicSeeker.Properties.Resources.Label_NormalOutputBase,
                    tempLR2CustomFolderOutputDir,
                    BeMusicSeeker.Properties.Resources.Label_PreviousNormalOutputBase,
                    out errMsg);
                if (!string.IsNullOrWhiteSpace(errMsg))
                {
                    return false;
                }

                foreach (string previousAdditionalPath in CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(tempLR2CustomFolderAdditionalOutputBaseDirs))
                {
                    ValidateCustomFolderOutputBaseIsNotNestedWithPreviousManagedRoot(
                        normalizedPath,
                        BeMusicSeeker.Properties.Resources.Label_NormalOutputBase,
                        previousAdditionalPath,
                        BeMusicSeeker.Properties.Resources.Label_PreviousAdditionalOutputBase,
                        out errMsg);
                    if (!string.IsNullOrWhiteSpace(errMsg))
                    {
                        return false;
                    }
                }

                if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(normalizedPath, LR2CustomFolderAsRootOutputDir))
                {
                    errMsg = FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Validation_NormalAndRootOutputBasesNested);
                    return false;
                }

                if (!ValidateNormalOutputBaseDoesNotAdoptUserBmsRoot(normalizedPath, out errMsg))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
            {
                errMsg = FormatPlaylistValidationMessage(ex.Message);
                return false;
            }
        }

        private void ValidateCustomFolderOutputBaseIsNotNestedWithPreviousManagedRoot(
            string outputBasePath,
            string outputBaseLabel,
            string previousManagedRootPath,
            string previousManagedRootLabel,
            out string errMsg)
        {
            errMsg = string.Empty;
            if (string.IsNullOrWhiteSpace(previousManagedRootPath)
                || CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(outputBasePath, previousManagedRootPath))
            {
                return;
            }
            if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(outputBasePath, previousManagedRootPath))
            {
                errMsg = FormatPlaylistValidationMessage(FormatResource(BeMusicSeeker.Properties.Resources.Validation_PreviousManagedRootNestedFormat, outputBaseLabel, previousManagedRootLabel));
            }
        }

        private bool ValidateCustomFolderAsRootOutputBaseDir(out string errMsg)
        {
            return ValidateCustomFolderAsRootOutputBaseDir(LR2CustomFolderAsRootOutputDir, out errMsg);
        }

        private bool ValidateCustomFolderAsRootOutputBaseDir(string path, out string errMsg)
        {
            errMsg = string.Empty;
            try
            {
                string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
                CustomFolderOutputBaseSearchRootSyncService.ValidateSjisDirectoryPath(normalizedPath, BeMusicSeeker.Properties.Resources.Label_RootOutputBaseFolder);

                string name = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(normalizedPath);
                if (string.IsNullOrWhiteSpace(name))
                {
                    errMsg = FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Validation_RootOutputBaseNameEmpty);
                    return false;
                }

                if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(normalizedPath, LR2CustomFolderOutputDir))
                {
                    errMsg = FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Validation_RootAndNormalOutputBasesNested);
                    return false;
                }

                foreach (string additionalPath in CustomFolderAdditionalOutputBaseDirList)
                {
                    if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(normalizedPath, additionalPath))
                    {
                        errMsg = FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Validation_RootAndAdditionalOutputBasesNested);
                        return false;
                    }
                }

                ValidateCustomFolderOutputBaseIsNotNestedWithPreviousManagedRoot(
                    normalizedPath,
                    BeMusicSeeker.Properties.Resources.Label_RootOutputBase,
                    tempLR2CustomFolderAsRootOutputDir,
                    BeMusicSeeker.Properties.Resources.Label_PreviousRootOutputBase,
                    out errMsg);
                if (!string.IsNullOrWhiteSpace(errMsg))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
            {
                errMsg = FormatPlaylistValidationMessage(ex.Message);
                return false;
            }
        }

        private bool ValidateNormalOutputBaseDoesNotAdoptUserBmsRoot(string normalizedPath, out string errMsg)
        {
            errMsg = string.Empty;
            IReadOnlyList<string> previousNormalRoots = GetPreviousNormalCustomFolderSearchRootDirectories();
            if (!OperationModeLR2DB || string.IsNullOrWhiteSpace(normalizedPath))
            {
                return true;
            }
            if (IsSameOrNestedWithAnyRoot(normalizedPath, GetPreviousNonNormalManagedCustomFolderSearchRootDirectories()))
            {
                errMsg = FormatPlaylistValidationMessage(FormatResource(
                    BeMusicSeeker.Properties.Resources.Validation_OutputBaseNestedWithBmsRootFormat,
                    BeMusicSeeker.Properties.Resources.Label_NormalOutputBase));
                return false;
            }

            LR2Config config;
            try
            {
                if (lr2config == null && !IsLR2ConfigXmlPathValid())
                {
                    return true;
                }
                config = lr2config ?? new LR2Config(Settings.Default.LR2ConfigXmlPath);
            }
            catch
            {
                return true;
            }

            foreach (string jukeboxRoot in CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(config.GetBMSSearchDirectoriesForChangeTracking()))
            {
                if (string.IsNullOrWhiteSpace(jukeboxRoot) || IsSameAsAnyRoot(jukeboxRoot, previousNormalRoots))
                {
                    continue;
                }
                if (CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(normalizedPath, jukeboxRoot))
                {
                    errMsg = FormatPlaylistValidationMessage(FormatResource(
                        BeMusicSeeker.Properties.Resources.Validation_OutputBaseSameAsBmsRootFormat,
                        BeMusicSeeker.Properties.Resources.Label_NormalOutputBase));
                    return false;
                }
                if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(normalizedPath, jukeboxRoot))
                {
                    errMsg = FormatPlaylistValidationMessage(FormatResource(
                        BeMusicSeeker.Properties.Resources.Validation_OutputBaseNestedWithBmsRootFormat,
                        BeMusicSeeker.Properties.Resources.Label_NormalOutputBase));
                    return false;
                }
            }
            return true;
        }

        private static bool IsSameAsAnyRoot(string path, IEnumerable<string> roots)
        {
            string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
            return !string.IsNullOrWhiteSpace(normalizedPath)
                && roots != null
                && roots.Any(root => CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(normalizedPath, root));
        }

        private static bool IsSameOrNestedWithAnyRoot(string path, IEnumerable<string> roots)
        {
            string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
            return !string.IsNullOrWhiteSpace(normalizedPath)
                && roots != null
                && roots.Any(root => CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(normalizedPath, root));
        }

        private void ValidateCustomFolderAdditionalOutputBaseSearchRootPath(string path, string oldPath)
        {
            CustomFolderOutputBaseSearchRootSyncService.ValidateSjisDirectoryPath(path);
            ValidateCustomFolderAdditionalOutputBaseIsNotNestedWithOutputBase(path, LR2CustomFolderOutputDir, BeMusicSeeker.Properties.Resources.Label_NormalOutputBase);
            ValidateCustomFolderAdditionalOutputBaseIsNotNestedWithOutputBase(path, LR2CustomFolderAsRootOutputDir, BeMusicSeeker.Properties.Resources.Label_RootOutputBase);
            if (!string.IsNullOrWhiteSpace(oldPath)
                && !CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(path, oldPath)
                && CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(path, oldPath))
            {
                throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_AdditionalOutputBaseNestedWithPreviousAdditional);
            }

            // Existing LR2 <jukebox> roots can be adopted as app-managed custom folder output roots.
            // The destructive-semantics warning is handled once at save time so paths entered before
            // the LR2 config is selected are checked by the same flow.
        }

        private void ValidateCustomFolderAdditionalOutputBaseIsNotNestedWithOutputBase(string additionalOutputBasePath, string outputBasePath, string outputBaseLabel)
        {
            if (string.IsNullOrWhiteSpace(outputBasePath))
            {
                return;
            }
            if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(additionalOutputBasePath, outputBasePath))
            {
                throw new InvalidOperationException(FormatResource(BeMusicSeeker.Properties.Resources.Error_AdditionalOutputBaseNestedWithOutputBaseFormat, outputBaseLabel));
            }
        }

        private int CountPlaylistCustomFolderOutputBaseReferences(string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName) || ownerViewModel.BMSTables == null)
            {
                return 0;
            }
            return ownerViewModel.BMSTables.Count(table =>
                table != null
                && string.Equals(table.custom_folder_output_base_name, baseName, StringComparison.OrdinalIgnoreCase));
        }

        public void AddBmsSearchRootPaths(IEnumerable<string> paths)
        {
            AddBmsSearchRootPaths(paths, null, saveImmediately: false);
        }

        private void AddBmsSearchRootPaths(IEnumerable<string> paths, string settingPropertyPath, bool saveImmediately)
        {
            if (OperationModeLR2DB)
            {
                AddBMSDirectoriesToLR2Config(paths, settingPropertyPath, saveImmediately);
                return;
            }
            AddStandaloneBmsRootPathsCore(paths, settingPropertyPath);
        }

        public void AddStandaloneBmsRootPaths(IEnumerable<string> paths)
        {
            AddStandaloneBmsRootPathsCore(paths, null);
        }

        private void AddStandaloneBmsRootPathsCore(IEnumerable<string> paths, string settingPropertyPath)
        {
            try
            {
                string before = SerializeBmsRootPathsForChangeTracking(StandaloneBmsRootPathList);
                List<string> requestedPaths = [.. NormalizeExistingStandaloneBmsRootPathsWithoutLr2Compatibility(paths ?? [])];
                ThrowIfLr2IncompatibleStandaloneBmsRoots(requestedPaths);
                string requestedPath = requestedPaths.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(requestedPath))
                {
                    return;
                }
                foreach (string path in requestedPaths)
                {
                    if (!StandaloneBmsRootPathList.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        StandaloneBmsRootPathList.Add(path);
                    }
                }
                SelectedStandaloneBmsRootPath = StandaloneBmsRootPathList.FirstOrDefault(path => string.Equals(path, requestedPath, StringComparison.OrdinalIgnoreCase))
                    ?? StandaloneBmsRootPathList.FirstOrDefault(path => IsSameOrChildPath(requestedPath, path))
                    ?? StandaloneBmsRootPathList.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(settingPropertyPath))
                {
                    string name = settingPropertyPath.Substring(settingPropertyPath.LastIndexOf('.') + 1);
                    GetType().GetProperty(name).GetSetMethod().Invoke(this, [requestedPath]);
                }
                string after = SerializeBmsRootPathsForChangeTracking(StandaloneBmsRootPathList);
                isSearchRootsChanged = !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
                isBMSDirectoryAdded = isSearchRootsChanged;
                RaisePropertyChanged(() => StandaloneBmsRootPathList);
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                RaiseValidationStateChanged();
            }
            catch (Exception ex)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
        }

        private void AddBMSDirectoriesToLR2Config(IEnumerable<string> paths, string settingPropertyPath, bool saveImmediately)
        {
            if (lr2config == null)
            {
                return;
            }
            try
            {
                List<string> requestedPaths = [.. NormalizeExistingStandaloneBmsRootPathsWithoutLr2Compatibility(paths ?? [])];
                string requestedPath = requestedPaths.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(requestedPath))
                {
                    return;
                }
                List<string> nonSjisPaths = [.. requestedPaths.Where(path => !path.IsSjisSchemeString())];
                if (nonSjisPaths.Count > 0)
                {
                    throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_LR2UnicodePathUnsupported + Environment.NewLine + string.Join(Environment.NewLine, nonSjisPaths));
                }
                ThrowIfLr2IncompatibleStandaloneBmsRoots(requestedPaths);
                IReadOnlyList<string> managedCustomFolderOutputRoots = GetUserVisibleBmsSearchRootExcludedDirectories(includePreviousSettings: true);
                string conflictingPath = requestedPaths.FirstOrDefault(path =>
                    managedCustomFolderOutputRoots.Any(root => CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(path, root)));
                if (!string.IsNullOrWhiteSpace(conflictingPath))
                {
                    throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_ManagedCustomFolderOutputCannotBeAddedAsBmsRoot + Environment.NewLine + conflictingPath);
                }
                lr2config.AddBMSSearchDirectories(requestedPaths);
                isSearchRootsChanged = true;
                isBMSDirectoryAdded = true;
                selectedLR2ConfigBmsDirectory = requestedPath;
                RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                RaiseValidationStateChanged();
                if (!string.IsNullOrWhiteSpace(settingPropertyPath))
                {
                    string name = settingPropertyPath.Substring(settingPropertyPath.LastIndexOf('.') + 1);
                    GetType().GetProperty(name).GetSetMethod().Invoke(this, [requestedPath]);
                }
                if (saveImmediately)
                {
                    lr2config.Save();
                }
            }
            catch (ArgumentException ex)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
            catch (Exception ex)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
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
                RemoveBMSDirectoryFromLR2Config(dir, saveImmediately: true);
                if (isSearchRootsChanged)
                {
                    ApplyRuntimeSearchRootsForCurrentMode();
                    if (isBMSDirectoryRemoved)
                    {
                        ownerViewModel.ReloadFileDiff();
                    }
                    else
                    {
                        ownerViewModel.NotifyBmsParentFolderListChanged();
                    }
                    isSearchRootsChanged = false;
                    isBMSDirectoryRemoved = false;
                }
                else
                {
                    ownerViewModel.NotifyBmsParentFolderListChanged();
                }
                return;
            }
            RemoveStandaloneBmsRootPath(dir);
            if (isSearchRootsChanged)
            {
                PersistStandaloneBmsRootPathsToSettings();
                Settings.Default.Save();
                ApplyRuntimeSearchRootsForCurrentMode();
                if (isBMSDirectoryRemoved)
                {
                    ownerViewModel.ReloadFileDiff();
                }
                else
                {
                    ownerViewModel.NotifyBmsParentFolderListChanged();
                }
                isSearchRootsChanged = false;
                isBMSDirectoryRemoved = false;
            }
            else
            {
                ownerViewModel.NotifyBmsParentFolderListChanged();
            }
        }

        private void RemoveBMSDirectoryFromSearchRoots(string dir)
        {
            if (OperationModeLR2DB)
            {
                RemoveBMSDirectoryFromLR2Config(dir, saveImmediately: false);
                return;
            }
            RemoveStandaloneBmsRootPath(dir);
        }

        private void RemoveStandaloneBmsRootPath(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }
            try
            {
                string normalizedDir = TrimDirectorySeparatorUnlessRoot(Path.GetFullPath(dir.Trim()));
                if (!string.IsNullOrWhiteSpace(Settings.Default.BMSInstallDir) && IsSameOrChildPath(Settings.Default.BMSInstallDir, normalizedDir))
                {
                    throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_CannotRemoveBmsInstallDir);
                }
                string before = SerializeBmsRootPathsForChangeTracking(StandaloneBmsRootPathList);
                List<string> remaining = [.. StandaloneBmsRootPathList.Where(path => !string.Equals(path, normalizedDir, StringComparison.OrdinalIgnoreCase))];
                StandaloneBmsRootPathList.Clear();
                foreach (string path in remaining)
                {
                    StandaloneBmsRootPathList.Add(path);
                }
                SelectedStandaloneBmsRootPath = StandaloneBmsRootPathList.FirstOrDefault();
                string after = SerializeBmsRootPathsForChangeTracking(StandaloneBmsRootPathList);
                isSearchRootsChanged = !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
                isBMSDirectoryRemoved = isSearchRootsChanged;
                RaisePropertyChanged(() => StandaloneBmsRootPathList);
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                RaisePropertyChanged(() => BMSInstallDir);
                RaiseValidationStateChanged();
            }
            catch (Exception ex)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
        }

        private void RemoveBMSDirectoryFromLR2Config(string dir, bool saveImmediately)
        {
            if (dir == null)
            {
                throw new ArgumentNullException();
            }
            try
            {
                if (LR2CustomFolderOutputDir != null && (LR2CustomFolderOutputDir + Path.DirectorySeparatorChar).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_CannotRemoveCustomFolderOutputDir);
                }
                if (CustomFolderAdditionalOutputBaseDirList.Any(path => path != null && (path + Path.DirectorySeparatorChar).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_CannotRemoveAdditionalOutputBaseDir);
                }
                if (LR2CustomFolderAsRootOutputDir != null && (LR2CustomFolderAsRootOutputDir + Path.DirectorySeparatorChar).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_CannotRemoveRootCustomFolderOutputDir);
                }
                if (CreateRootFolderOutputDirectories(LR2CustomFolderAsRootOutputDir).Any(path => path != null && (path + Path.DirectorySeparatorChar).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_CannotRemoveRootCustomFolderOutputDir);
                }
                if (BMSInstallDir != null && (BMSInstallDir + Path.DirectorySeparatorChar).StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_CannotRemoveBmsInstallDir);
                }
                if (lr2config.RemoveBMSSearchDirectories([dir]))
                {
                    isSearchRootsChanged = true;
                    if (string.Equals(selectedLR2ConfigBmsDirectory, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedLR2ConfigBmsDirectory = LR2ConfigBMSDirectories.FirstOrDefault();
                    }
                    if (saveImmediately)
                    {
                        lr2config.Save();
                    }
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                    RaisePropertyChanged(() => AvailableBMSDirectories);
                    RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                    RaisePropertyChanged(() => BMSInstallDir);
                    RaiseValidationStateChanged();
                    if (ownerViewModel.files?.HasOwnedChartUnderRealPath(dir) == true)
                    {
                        isBMSDirectoryRemoved = true;
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindowViewModel.ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
        }

        public void AudioPlayerInitTest(bool playSound = true)
        {
            ownerViewModel.PlayEndBMSFile(closeProcess: true);
            BassAudioPlayer.DeviceDescriptor desc = (string.IsNullOrWhiteSpace(Settings.Default.PlayerDevice) ? default : new BassAudioPlayer.DeviceDescriptor(Settings.Default.PlayerDeviceName, Settings.Default.PlayerDevice));
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

        [Flags]
        private enum SettingsPostSaveImpact
        {
            None = 0,
            CustomFolderSearchRootSync = 1,
            PlayerRuntime = 2,
            Lr2BackupEnabledNotice = 4,
            PlaylistUrlCompletion = 8,
            Lr2CoreSync = 16,
            ExternalLr2FolderRowsSync = 32,
            BeatorajaBmtExport = 64
        }

        [Flags]
        private enum SettingsSnapshotRefreshScope
        {
            None = 0,
            StandaloneSearchRoots = 1,
            Lr2SearchRoots = 2,
            CustomFolderOutputBase = 4,
            PlayHistoryDisplayPreset = 8,
            OperationMode = 16,
            ValidationState = 32,
            Full = StandaloneSearchRoots | Lr2SearchRoots | CustomFolderOutputBase | PlayHistoryDisplayPreset | OperationMode | ValidationState
        }

        private static SettingsSnapshotRefreshScope BuildSettingsSnapshotRefreshScope(
            bool operationModeChanged,
            bool standaloneSearchRootsChanged,
            bool lr2SearchRootsChanged,
            bool customFolderOutputBaseSettingsChanged,
            bool playHistoryFolderDisplayPresetDraftsChanged,
            bool beatorajaDerivedSettingsSourceChanged,
            bool lr2ConfigBoundaryChanged)
        {
            SettingsSnapshotRefreshScope scope = SettingsSnapshotRefreshScope.None;
            if (operationModeChanged)
            {
                scope |= SettingsSnapshotRefreshScope.Full;
            }
            if (standaloneSearchRootsChanged)
            {
                scope |= SettingsSnapshotRefreshScope.StandaloneSearchRoots | SettingsSnapshotRefreshScope.ValidationState;
            }
            if (lr2SearchRootsChanged)
            {
                scope |= SettingsSnapshotRefreshScope.Lr2SearchRoots | SettingsSnapshotRefreshScope.ValidationState;
            }
            if (lr2ConfigBoundaryChanged)
            {
                scope |= SettingsSnapshotRefreshScope.Lr2SearchRoots | SettingsSnapshotRefreshScope.ValidationState;
            }
            if (customFolderOutputBaseSettingsChanged)
            {
                scope |= SettingsSnapshotRefreshScope.CustomFolderOutputBase | SettingsSnapshotRefreshScope.Lr2SearchRoots | SettingsSnapshotRefreshScope.ValidationState;
            }
            if (playHistoryFolderDisplayPresetDraftsChanged)
            {
                scope |= SettingsSnapshotRefreshScope.PlayHistoryDisplayPreset | SettingsSnapshotRefreshScope.ValidationState;
            }
            if (beatorajaDerivedSettingsSourceChanged)
            {
                scope |= SettingsSnapshotRefreshScope.ValidationState;
            }
            return scope;
        }

        private void backupSavedSettings()
        {
            backupSavedSettingsCore(SettingsSnapshotRefreshScope.Full);
        }

        private void backupSavedSettingsCore(SettingsSnapshotRefreshScope scope)
        {
            var totalStopwatch = Stopwatch.StartNew();
            long standaloneRootsMs = 0L;
            long customFolderBasesMs = 0L;
            long lr2RootsSnapshotMs = 0L;
            long standaloneRootsSnapshotMs = 0L;
            long playHistoryPresetRefreshMs = 0L;
            long playHistoryPresetSnapshotMs = 0L;
            operationModeLR2DB = Settings.Default.OperationModeLR2DB;
            var stepStopwatch = Stopwatch.StartNew();
            if (scope.HasFlag(SettingsSnapshotRefreshScope.StandaloneSearchRoots))
            {
                RefreshStandaloneBmsRootPathsFromSettings();
            }
            standaloneRootsMs = stepStopwatch.ElapsedMilliseconds;
            stepStopwatch.Restart();
            if (scope.HasFlag(SettingsSnapshotRefreshScope.CustomFolderOutputBase))
            {
                RefreshCustomFolderAdditionalOutputBaseDirsFromSettings();
            }
            customFolderBasesMs = stepStopwatch.ElapsedMilliseconds;
            tempOperationModeLR2DB = Settings.Default.OperationModeLR2DB;
            tempLR2RootPath = Settings.Default.LR2RootPath;
            tempLR2SongDBPath = Settings.Default.LR2SongDBPath;
            tempLR2ConfigXmlPath = Settings.Default.LR2ConfigXmlPath;
            tempUseBeatorajaScoreDb = Settings.Default.UseBeatorajaScoreDb;
            tempBeatorajaRootPath = Settings.Default.BeatorajaRootPath;
            tempBeatorajaPlayerId = Settings.Default.BeatorajaPlayerId;
            tempBeatorajaScoreDbPath = Settings.Default.BeatorajaScoreDbPath;
            tempEnableBeatorajaBmtOutput = Settings.Default.EnableBeatorajaBmtOutput;
            tempKeepBeatorajaBmtFilesWhenOutputDisabled = Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled;
            tempBeatorajaBmtHashOutputMode = BeatorajaBmtHashOutputMode;
            tempBeatorajaBmtTablePath = Settings.Default.BeatorajaBmtTablePath;
            tempRegisterBeatorajaBmtUrls = Settings.Default.RegisterBeatorajaBmtUrls;
            tempBMSRootPath = Settings.Default.BMSRootPath;
            stepStopwatch.Restart();
            if (scope.HasFlag(SettingsSnapshotRefreshScope.Lr2SearchRoots))
            {
                tempLR2ConfigBmsSearchRoots = SerializeLR2ConfigBmsSearchRoots();
            }
            lr2RootsSnapshotMs = stepStopwatch.ElapsedMilliseconds;
            stepStopwatch.Restart();
            if (scope.HasFlag(SettingsSnapshotRefreshScope.StandaloneSearchRoots))
            {
                tempStandaloneBmsRootPaths = SerializeBmsRootPathsForChangeTracking(StandaloneBmsRootPathList);
            }
            standaloneRootsSnapshotMs = stepStopwatch.ElapsedMilliseconds;
            tempuBMplayPath = Settings.Default.uBMplayPath;
            tempBMIIDXViewPath = Settings.Default.BMIIDXViewPath;
            tempUsePlayeruBMplay = Settings.Default.UsePlayeruBMplay;
            tempUsePlayerLR2body = Settings.Default.UsePlayerLR2body;
            tempUsePlayerBMIIDXView = Settings.Default.UsePlayerBMIIDXView;
            tempLR2bodyResolution = Settings.Default.LR2bodyResolution;
            tempIsSaveLR2bodyWindowPosition = Settings.Default.IsSaveLR2bodyWindowPosition;
            tempLR2CustomFolderOutputDir = Settings.Default.LR2CustomFolderOutputBaseDir;
            tempLR2CustomFolderAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
            tempLR2CustomFolderAsRootOutputDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
            tempPlaylistDefaultIgnoreFolderOutput = Settings.Default.PlaylistDefaultIgnoreFolderOutput;
            tempBMSInstallDir = Settings.Default.BMSInstallDir;
            tempTableListURL = Settings.Default.TableListURL;
            tempEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
            tempOverwritePlaylistUrlsWithCompletion = Settings.Default.OverwritePlaylistUrlsWithCompletion;
            tempEnableStellaFullPlaylistUrlCompletion = Settings.Default.EnableStellaFullPlaylistUrlCompletion;
            tempPlaylistMd5UrlMappingTsvUri = Settings.Default.PlaylistMd5UrlMappingTsvUri;
            tempPlayHistoryDisplayTargetSetsJson = Settings.Default.PlayHistoryDisplayTargetSetsJson;
            tempIsLR2BackupEnabled = Settings.Default.IsLR2BackupEnabled;
            tempLR2BackupPath = Settings.Default.LR2BackupPath;
            tempLR2BackupTarget = Settings.Default.LR2BackupTarget;
            tempLR2BackupSpan = Settings.Default.LR2BackupSpan;
            tempLR2BackupNum = Settings.Default.LR2BackupNum;
            tempUseExternalWebBrowser = Settings.Default.UseExternalWebBrowser;
            tempUseExternalPanelImage = Settings.Default.UseExternalPanelImage;
            tempAppearanceTheme = AppThemeService.NormalizeTheme(Settings.Default.AppearanceTheme);
            tempCustomTableFontSize = Settings.Default.CustomTableFontSize;
            tempCustomTableRowHeight = Settings.Default.CustomTableRowHeight;
            tempCustomTableHeaderHeight = Settings.Default.CustomTableHeaderHeight;
            tempStagefilePath = Settings.Default.StagefilePath;
            tempFolderNameFormat = Settings.Default.FolderNameFormat;
            tempUseOnlyShiftJISChars = Settings.Default.UseOnlyShiftJISChars;
            tempShowScoreViewerRegisterConfirmMsg = Settings.Default.ShowScoreViewerRegisterConfirmMsg;
            tempShowDiffBMSInstallConfirmMsg = Settings.Default.ShowDiffBMSInstallConfirmMsg;
            tempShowDuplicateFileCheckConfirmMsg = Settings.Default.ShowDuplicateFileCheckConfirmMsg;
            tempShowRecommUpdatedMsg = Settings.Default.ShowRecommUpdatedMsg;
            tempScanBmsFilesOnStartup = Settings.Default.ScanBmsFilesOnStartup;
            tempSkipInitPlaylistLoad = Settings.Default.SkipInitPlaylistLoad;
            tempStartupSelectInstallPending = Settings.Default.StartupSelectInstallPending;
            tempEnableReadOptimizedPragmas = Settings.Default.EnableReadOptimizedPragmas;
            tempEstimateOfflineScoreRanking = Settings.Default.EstimateOfflineScoreRanking;
            tempUpdateLr2IrRankingCacheOnStartup = Settings.Default.UpdateLr2IrRankingCacheOnStartup;
            tempEnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent;
            tempEnableAutoInstall = Settings.Default.AutoInstall;
            tempKeepInstallablePackagesPending = Settings.Default.KeepInstallablePackagesPending;
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
            tempLanguageDisplayName = Settings.Default.LangDisplayName;
            stepStopwatch.Restart();
            if (scope.HasFlag(SettingsSnapshotRefreshScope.PlayHistoryDisplayPreset))
            {
                RefreshPlayHistoryFolderDisplayPresetsFromSettings();
            }
            playHistoryPresetRefreshMs = stepStopwatch.ElapsedMilliseconds;
            stepStopwatch.Restart();
            if (scope.HasFlag(SettingsSnapshotRefreshScope.PlayHistoryDisplayPreset))
            {
                tempPlayHistoryDisplayTargetSetDraftsJson = SerializePlayHistoryFolderDisplayPresetDraftsForChangeTracking();
            }
            playHistoryPresetSnapshotMs = stepStopwatch.ElapsedMilliseconds;
            isSearchRootsChanged = false;
            isBMSDirectoryAdded = false;
            isBMSDirectoryRemoved = false;
            if (scope.HasFlag(SettingsSnapshotRefreshScope.OperationMode))
            {
                RaisePropertyChanged(() => IsOperationModeChanged);
            }
            if (scope.HasFlag(SettingsSnapshotRefreshScope.ValidationState))
            {
                RaiseValidationStateChanged();
            }
            LogSettingsPerformance(
                "settings_backup_snapshot",
                totalStopwatch,
                "scope=" + scope
                + " standaloneRootsMs=" + standaloneRootsMs
                + " customFolderBasesMs=" + customFolderBasesMs
                + " lr2RootsSnapshotMs=" + lr2RootsSnapshotMs
                + " standaloneRootsSnapshotMs=" + standaloneRootsSnapshotMs
                + " playHistoryPresetRefreshMs=" + playHistoryPresetRefreshMs
                + " playHistoryPresetSnapshotMs=" + playHistoryPresetSnapshotMs);
        }

        internal bool HasPendingSettingChanges()
        {
            return HasSettingValueChanges()
                || tempOperationModeLR2DB != operationModeLR2DB
                || HasSearchRootSettingsChanged()
                || HasCustomFolderAdditionalOutputBaseDirsChanged()
                || HasPlayHistoryFolderDisplayPresetDraftsChanged();
        }

        private bool HasSettingValueChanges()
        {
            return HasPathSettingValueChanged(tempLR2RootPath, Settings.Default.LR2RootPath, value => IsLR2RootPathValid(value) || IsLR2PlayerRootPathValid(value))
                || HasPathSettingValueChanged(tempLR2SongDBPath, Settings.Default.LR2SongDBPath, IsLR2SongDBPathValid)
                || HasPathSettingValueChanged(tempLR2ConfigXmlPath, Settings.Default.LR2ConfigXmlPath, IsLR2ConfigXmlPathValid)
                || tempUseBeatorajaScoreDb != Settings.Default.UseBeatorajaScoreDb
                || !string.Equals(tempBeatorajaRootPath, Settings.Default.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaPlayerId, Settings.Default.BeatorajaPlayerId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaScoreDbPath, Settings.Default.BeatorajaScoreDbPath, StringComparison.OrdinalIgnoreCase)
                || tempEnableBeatorajaBmtOutput != Settings.Default.EnableBeatorajaBmtOutput
                || tempKeepBeatorajaBmtFilesWhenOutputDisabled != Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled
                || !string.Equals(tempBeatorajaBmtHashOutputMode, BeatorajaBmtHashOutputMode, StringComparison.Ordinal)
                || !string.Equals(tempBeatorajaBmtTablePath, Settings.Default.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase)
                || tempRegisterBeatorajaBmtUrls != Settings.Default.RegisterBeatorajaBmtUrls
                || HasPathSettingValueChanged(tempBMSRootPath, Settings.Default.BMSRootPath, IsBMSRootPathValid)
                || HasPathSettingValueChanged(tempuBMplayPath, Settings.Default.uBMplayPath, IsuBMplayPathValid)
                || HasPathSettingValueChanged(tempBMIIDXViewPath, Settings.Default.BMIIDXViewPath, IsBMIIDXViewPathValid)
                || tempUsePlayeruBMplay != Settings.Default.UsePlayeruBMplay
                || tempUsePlayerLR2body != Settings.Default.UsePlayerLR2body
                || tempUsePlayerBMIIDXView != Settings.Default.UsePlayerBMIIDXView
                || tempLR2bodyResolution != Settings.Default.LR2bodyResolution
                || tempIsSaveLR2bodyWindowPosition != Settings.Default.IsSaveLR2bodyWindowPosition
                || HasCustomFolderOutputBaseSettingsChanged()
                || tempPlaylistDefaultIgnoreFolderOutput != Settings.Default.PlaylistDefaultIgnoreFolderOutput
                || !string.Equals(tempBMSInstallDir, Settings.Default.BMSInstallDir, StringComparison.OrdinalIgnoreCase)
                || !IsSameUri(tempTableListURL, Settings.Default.TableListURL)
                || tempEnablePlaylistUrlCompletion != Settings.Default.EnablePlaylistUrlCompletion
                || tempOverwritePlaylistUrlsWithCompletion != Settings.Default.OverwritePlaylistUrlsWithCompletion
                || tempEnableStellaFullPlaylistUrlCompletion != Settings.Default.EnableStellaFullPlaylistUrlCompletion
                || !string.Equals(tempPlaylistMd5UrlMappingTsvUri, Settings.Default.PlaylistMd5UrlMappingTsvUri, StringComparison.Ordinal)
                || !string.Equals(tempPlayHistoryDisplayTargetSetsJson, Settings.Default.PlayHistoryDisplayTargetSetsJson, StringComparison.Ordinal)
                || tempIsLR2BackupEnabled != Settings.Default.IsLR2BackupEnabled
                || HasPathSettingValueChanged(tempLR2BackupPath, Settings.Default.LR2BackupPath, IsLR2BackupPathValid)
                || tempLR2BackupTarget != Settings.Default.LR2BackupTarget
                || tempLR2BackupSpan != Settings.Default.LR2BackupSpan
                || tempLR2BackupNum != Settings.Default.LR2BackupNum
                || tempUseExternalWebBrowser != Settings.Default.UseExternalWebBrowser
                || tempUseExternalPanelImage != Settings.Default.UseExternalPanelImage
                || !string.Equals(AppThemeService.NormalizeTheme(tempAppearanceTheme), AppThemeService.NormalizeTheme(Settings.Default.AppearanceTheme), StringComparison.Ordinal)
                || !tempCustomTableFontSize.Equals(Settings.Default.CustomTableFontSize)
                || !tempCustomTableRowHeight.Equals(Settings.Default.CustomTableRowHeight)
                || !tempCustomTableHeaderHeight.Equals(Settings.Default.CustomTableHeaderHeight)
                || HasPathSettingValueChanged(tempStagefilePath, Settings.Default.StagefilePath, IsStagefilePathValid)
                || !string.Equals(tempFolderNameFormat, Settings.Default.FolderNameFormat, StringComparison.Ordinal)
                || tempUseOnlyShiftJISChars != Settings.Default.UseOnlyShiftJISChars
                || tempShowScoreViewerRegisterConfirmMsg != Settings.Default.ShowScoreViewerRegisterConfirmMsg
                || tempShowDiffBMSInstallConfirmMsg != Settings.Default.ShowDiffBMSInstallConfirmMsg
                || tempShowDuplicateFileCheckConfirmMsg != Settings.Default.ShowDuplicateFileCheckConfirmMsg
                || tempShowRecommUpdatedMsg != Settings.Default.ShowRecommUpdatedMsg
                || tempScanBmsFilesOnStartup != Settings.Default.ScanBmsFilesOnStartup
                || tempSkipInitPlaylistLoad != Settings.Default.SkipInitPlaylistLoad
                || tempStartupSelectInstallPending != Settings.Default.StartupSelectInstallPending
                || tempEnableReadOptimizedPragmas != Settings.Default.EnableReadOptimizedPragmas
                || tempEstimateOfflineScoreRanking != Settings.Default.EstimateOfflineScoreRanking
                || tempUpdateLr2IrRankingCacheOnStartup != Settings.Default.UpdateLr2IrRankingCacheOnStartup
                || tempEnableDownloadLr2IrScoreAndDetectUnsent != Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent
                || tempEnableAutoInstall != Settings.Default.AutoInstall
                || tempKeepInstallablePackagesPending != Settings.Default.KeepInstallablePackagesPending
                || tempAutoApplyAmbiguousInstallDestination != Settings.Default.AutoApplyAmbiguousInstallDestination
                || tempDeletePendingPackageSourceAfterInstall != Settings.Default.DeletePendingPackageSourceAfterInstall
                || tempEnableSmartComponentOverwrite != Settings.Default.EnableSmartComponentOverwrite
                || tempKeepSmartOverwriteProtectedFilesByRenaming != Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming
                || tempEncoderSampleRate != Settings.Default.EncoderSampleRate
                || tempEncoderIndex != (int)Settings.Default.Encoder
                || tempEncoderFormat != Settings.Default.EncoderFormat
                || tempEncoderNormalization != Settings.Default.EncoderNormalization
                || !string.Equals(tempEncoderExeDir, Settings.Default.EncoderExeDir, StringComparison.OrdinalIgnoreCase)
                || tempEncoderAmplifier != Settings.Default.EncoderAmplifier
                || tempEncoderQuality != Settings.Default.EncoderQuality
                || !string.Equals(tempEncodeFileNameFormat, Settings.Default.EncodeFileNameFormat, StringComparison.Ordinal)
                || tempPlayerDriverIndex != (int)Settings.Default.PlayerDriver
                || !string.Equals(tempPlayerDevice, Settings.Default.PlayerDevice, StringComparison.Ordinal)
                || !string.Equals(tempPlayerDeviceName, Settings.Default.PlayerDeviceName, StringComparison.Ordinal)
                || tempPlayerSampleRate != Settings.Default.PlayerSampleRate
                || tempPlayerFormat != Settings.Default.PlayerFormat
                || tempPlayerBufferSize != Settings.Default.PlayerBufferSize
                || tempPlayerWASAPIParam != Settings.Default.PlayerWASAPIParam
                || !string.Equals(tempLanguage, Settings.Default.Lang, StringComparison.Ordinal)
                || !string.Equals(tempLanguageDisplayName, Settings.Default.LangDisplayName, StringComparison.Ordinal);
        }

        private bool HasSearchRootSettingsChanged()
        {
            return OperationModeLR2DB ? HasLR2ConfigBmsSearchRootsChanged() : HasStandaloneBmsRootPathsChanged();
        }

        private bool HasLR2ConfigBmsSearchRootsChanged()
        {
            return !string.Equals(tempLR2ConfigBmsSearchRoots, SerializeLR2ConfigBmsSearchRoots(), StringComparison.OrdinalIgnoreCase);
        }

        private bool HasStandaloneBmsRootPathsChanged()
        {
            return !string.Equals(tempStandaloneBmsRootPaths, SerializeBmsRootPathsForChangeTracking(StandaloneBmsRootPathList), StringComparison.OrdinalIgnoreCase);
        }

        private string SerializeLR2ConfigBmsSearchRoots()
        {
            return lr2config == null ? string.Empty : SerializeBmsRootPathsForChangeTracking(lr2config.GetBMSSearchDirectoriesForChangeTracking());
        }

        private bool HasCustomFolderOutputBaseSettingsChanged()
        {
            return !string.Equals(tempLR2CustomFolderOutputDir, Settings.Default.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempLR2CustomFolderAsRootOutputDir, Settings.Default.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase)
                || HasCustomFolderAdditionalOutputBaseDirsChanged();
        }

        private bool HasCustomFolderAdditionalOutputBaseDirsChanged()
        {
            return !string.Equals(tempLR2CustomFolderAdditionalOutputBaseDirs, Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal);
        }

        private bool HasPlayHistoryFolderDisplayPresetDraftsChanged()
        {
            return !string.Equals(tempPlayHistoryDisplayTargetSetDraftsJson, SerializePlayHistoryFolderDisplayPresetDraftsForChangeTracking(), StringComparison.Ordinal);
        }

        private static bool IsSameUri(Uri left, Uri right)
        {
            return string.Equals(left?.OriginalString ?? string.Empty, right?.OriginalString ?? string.Empty, StringComparison.Ordinal);
        }

        private static bool HasPathSettingValueChanged(string savedValue, string currentValue, Func<string, bool> isValidSavedValue)
        {
            if (string.Equals(savedValue, currentValue, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(savedValue) && string.IsNullOrWhiteSpace(currentValue))
            {
                return false;
            }
            return true;
        }

        private SettingsPostSaveImpact BuildSettingsPostSaveImpact(bool customFolderSearchRootSyncNeeded)
        {
            SettingsPostSaveImpact impact = SettingsPostSaveImpact.None;
            if (customFolderSearchRootSyncNeeded)
            {
                impact |= SettingsPostSaveImpact.CustomFolderSearchRootSync;
            }

            bool playerSelectionChanged = tempUsePlayeruBMplay != Settings.Default.UsePlayeruBMplay
                || tempUsePlayerLR2body != Settings.Default.UsePlayerLR2body
                || tempUsePlayerBMIIDXView != Settings.Default.UsePlayerBMIIDXView;
            bool forceInternalPlayerForStandaloneModeChange =
                !Settings.Default.OperationModeLR2DB
                && tempOperationModeLR2DB != Settings.Default.OperationModeLR2DB;
            if (playerSelectionChanged || forceInternalPlayerForStandaloneModeChange)
            {
                impact |= SettingsPostSaveImpact.PlayerRuntime;
            }

            if (Settings.Default.OperationModeLR2DB && Settings.Default.IsLR2BackupEnabled && tempIsLR2BackupEnabled != Settings.Default.IsLR2BackupEnabled)
            {
                impact |= SettingsPostSaveImpact.Lr2BackupEnabledNotice;
            }
            if (tempEnablePlaylistUrlCompletion != Settings.Default.EnablePlaylistUrlCompletion
                || tempOverwritePlaylistUrlsWithCompletion != Settings.Default.OverwritePlaylistUrlsWithCompletion
                || tempEnableStellaFullPlaylistUrlCompletion != Settings.Default.EnableStellaFullPlaylistUrlCompletion
                || !string.Equals(tempPlaylistMd5UrlMappingTsvUri, Settings.Default.PlaylistMd5UrlMappingTsvUri, StringComparison.Ordinal))
            {
                impact |= SettingsPostSaveImpact.PlaylistUrlCompletion;
            }

            bool lr2CoreSyncInputChanged =
                tempOperationModeLR2DB != Settings.Default.OperationModeLR2DB
                || !string.Equals(tempLR2RootPath, Settings.Default.LR2RootPath, StringComparison.OrdinalIgnoreCase);
            bool normalOutputBaseDirChanged =
                !string.Equals(tempLR2CustomFolderOutputDir, Settings.Default.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase);
            bool rootOutputBaseDirChanged =
                !string.Equals(tempLR2CustomFolderAsRootOutputDir, Settings.Default.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase);
            bool additionalOutputBaseDirsChanged =
                !string.Equals(tempLR2CustomFolderAdditionalOutputBaseDirs, Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal);
            if (Settings.Default.OperationModeLR2DB && lr2CoreSyncInputChanged)
            {
                impact |= SettingsPostSaveImpact.Lr2CoreSync;
            }
            else if (Settings.Default.OperationModeLR2DB
                && (normalOutputBaseDirChanged || rootOutputBaseDirChanged || additionalOutputBaseDirsChanged))
            {
                impact |= SettingsPostSaveImpact.ExternalLr2FolderRowsSync;
            }

            if (tempEnableBeatorajaBmtOutput != Settings.Default.EnableBeatorajaBmtOutput
                || tempKeepBeatorajaBmtFilesWhenOutputDisabled != Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled
                || tempRegisterBeatorajaBmtUrls != Settings.Default.RegisterBeatorajaBmtUrls
                || !string.Equals(tempBeatorajaBmtHashOutputMode, BeatorajaBmtHashOutputMode, StringComparison.Ordinal)
                || !string.Equals(tempBeatorajaRootPath, Settings.Default.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaBmtTablePath, Settings.Default.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase))
            {
                impact |= SettingsPostSaveImpact.BeatorajaBmtExport;
            }

            return impact;
        }

        private static bool HasPostSaveImpact(SettingsPostSaveImpact impact)
        {
            return impact != SettingsPostSaveImpact.None;
        }

        private async Task necessaryStepsAfterSaved(SettingsPostSaveImpact impact)
        {
            var totalStopwatch = Stopwatch.StartNew();
            long customFolderSearchRootSyncMs = 0L;
            long playerRuntimeMs = 0L;
            long lr2BackupNoticeMs = 0L;
            long playlistUrlCompletionMs = 0L;
            long lr2GeneratedDataSyncMs = 0L;
            long beatorajaBmtExportMs = 0L;
            bool tablesAvailable = ownerViewModel.tables != null;
            try
            {
                if (ownerViewModel.tables == null)
                {
                    return;
                }
                CustomFolderOutputSettingsSnapshot customFolderOutputSettingsAfterSave = null;
                if (impact.HasFlag(SettingsPostSaveImpact.CustomFolderSearchRootSync))
                {
                    customFolderOutputSettingsAfterSave = ownerViewModel.customFolderOutputSettingsProvider()
                        ?? throw new InvalidOperationException("Custom-folder output settings provider returned null after settings save.");
                }
                if (customFolderOutputSettingsAfterSave?.OperationModeLR2DB == true)
                {
                    var stepStopwatch = Stopwatch.StartNew();
                    while (ownerViewModel.BMSTables == null)
                    {
                        await Task.Delay(100);
                    }
                    CustomFolderOutputBaseSearchRootSyncPlan normalOutputBaseRootSyncPlan = default;
                    CustomFolderOutputBaseSearchRootSyncResult normalOutputBaseRootSyncResult = default;
                    bool rootOutputBaseRootSyncChanged = false;
                    using BMSPlaylist.OperationNotificationScope notificationScope = BMSPlaylist.BeginOperationNotificationScope();
                    try
                    {
                        await Task.Run(delegate
                        {
                            normalOutputBaseRootSyncPlan = PrepareCustomFolderNormalOutputBaseSearchRootSyncWithSettings(customFolderOutputSettingsAfterSave);
                            if (!string.IsNullOrWhiteSpace(tempLR2CustomFolderOutputDir) && !string.IsNullOrWhiteSpace(customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDir) && tempLR2CustomFolderOutputDir != customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDir)
                            {
                                ownerViewModel.tables.ChangeCustomFolderBaseDirectoryWithSettings(
                                    tempLR2CustomFolderOutputDir,
                                    customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDir,
                                    tempLR2CustomFolderAdditionalOutputBaseDirs,
                                    customFolderOutputSettingsAfterSave.LR2CustomFolderAdditionalOutputBaseDirs,
                                    customFolderOutputSettingsAfterSave);
                            }
                            ApplyCustomFolderAdditionalOutputBaseRegistrationChangesWithSettings(customFolderOutputSettingsAfterSave);
                            normalOutputBaseRootSyncResult = CompleteCustomFolderNormalOutputBaseSearchRootSyncWithSettings(
                                normalOutputBaseRootSyncPlan,
                                customFolderOutputSettingsAfterSave);
                            if (!string.IsNullOrWhiteSpace(tempLR2CustomFolderAsRootOutputDir) && !string.IsNullOrWhiteSpace(customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDirRootType) && tempLR2CustomFolderAsRootOutputDir != customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDirRootType)
                            {
                                ownerViewModel.tables.ChangeCustomFolderBaseDirectoryRootWithSettings(
                                    tempLR2CustomFolderAsRootOutputDir,
                                    customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDirRootType,
                                    customFolderOutputSettingsAfterSave);
                            }
                            rootOutputBaseRootSyncChanged = SyncRootCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
                                customFolderOutputSettingsAfterSave);
                        });
                    }
                    catch (Exception ex)
                    {
                        NLogWrapper.FileLogger?.Error(ex, "necessaryStepsAfterSaved failed");
                        throw;
                    }
                    finally
                    {
                        FlushPlaylistOperationNotifications(notificationScope, "custom folder output base sync notification");
                    }
                    ApplyCustomFolderNormalOutputBaseSearchRootSyncResult(normalOutputBaseRootSyncResult);
                    if (rootOutputBaseRootSyncChanged)
                    {
                        isSearchRootsChanged = true;
                        isBMSDirectoryAdded = true;
                        isBMSDirectoryRemoved = true;
                        ApplyRuntimeSearchRootsForCurrentMode();
                        RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                        RaisePropertyChanged(() => AvailableBMSDirectories);
                        RaisePropertyChanged(() => SelectedBmsSearchRootPath);
                        RaiseValidationStateChanged();
                    }
                    customFolderSearchRootSyncMs = stepStopwatch.ElapsedMilliseconds;
                }
                var playerRuntimeStopwatch = Stopwatch.StartNew();
                bool forceInternalPlayerForStandaloneModeChange =
                    !Settings.Default.OperationModeLR2DB
                    && tempOperationModeLR2DB != Settings.Default.OperationModeLR2DB;
                if (impact.HasFlag(SettingsPostSaveImpact.PlayerRuntime))
                {
                    ownerViewModel.PlayEndBMSFile(closeProcess: true);
                    if (!forceInternalPlayerForStandaloneModeChange && Settings.Default.UsePlayeruBMplay)
                    {
                        ownerViewModel.bmsPlayer = new uBMplay(uBMplayPath);
                    }
                    else if (!forceInternalPlayerForStandaloneModeChange && Settings.Default.UsePlayerLR2body)
                    {
                        ownerViewModel.bmsPlayer = new LR2body(LR2bodyPath, new LR2Config(Settings.Default.LR2ConfigXmlPath));
                    }
                    else if (!forceInternalPlayerForStandaloneModeChange && Settings.Default.UsePlayerBMIIDXView)
                    {
                        ownerViewModel.bmsPlayer = new BMIIDXView2015(BMIIDXViewPath);
                    }
                    else
                    {
                        ownerViewModel.bmsPlayer = new InternalBMSAutoPlayerSoundOnly();
                    }
                    ownerViewModel.RaiseInitializationSucceeded();
                }
                playerRuntimeMs = playerRuntimeStopwatch.ElapsedMilliseconds;
                var lr2BackupNoticeStopwatch = Stopwatch.StartNew();
                if (impact.HasFlag(SettingsPostSaveImpact.Lr2BackupEnabledNotice))
                {
                    MainWindowViewModel.ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_LR2ConfigBackupEnabledNextStartup, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxImage.Asterisk);
                }
                lr2BackupNoticeMs = lr2BackupNoticeStopwatch.ElapsedMilliseconds;
                var playlistUrlCompletionStopwatch = Stopwatch.StartNew();
                if (impact.HasFlag(SettingsPostSaveImpact.PlaylistUrlCompletion))
                {
                    ownerViewModel.tables.SchedulePlaylistUrlCompletionRefresh("SettingDialog.SaveSettings");
                }
                playlistUrlCompletionMs = playlistUrlCompletionStopwatch.ElapsedMilliseconds;
                var lr2GeneratedDataSyncStopwatch = Stopwatch.StartNew();
                if (impact.HasFlag(SettingsPostSaveImpact.Lr2CoreSync))
                {
                    ownerViewModel.SyncLr2SongDbSyncFolderDataAfterSettingsChange("SettingDialog.SaveSettings");
                }
                else if (impact.HasFlag(SettingsPostSaveImpact.ExternalLr2FolderRowsSync))
                {
                    ownerViewModel.SyncExternalLr2FolderRowsAfterCustomFolderOutputBaseSettingsChange("SettingDialog.SaveSettings");
                }
                lr2GeneratedDataSyncMs = lr2GeneratedDataSyncStopwatch.ElapsedMilliseconds;
                var beatorajaBmtExportStopwatch = Stopwatch.StartNew();
                if (impact.HasFlag(SettingsPostSaveImpact.BeatorajaBmtExport))
                {
                    bool preserveDisabledBmtOutput = !Settings.Default.EnableBeatorajaBmtOutput && Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled;
                    if (BeatorajaConfigService.IsBeatorajaRootPathValid(tempBeatorajaRootPath)
                        && !string.IsNullOrWhiteSpace(tempBeatorajaBmtTablePath)
                        && !preserveDisabledBmtOutput
                        && (!string.Equals(tempBeatorajaRootPath, Settings.Default.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                            || !Settings.Default.EnableBeatorajaBmtOutput
                            || !Settings.Default.RegisterBeatorajaBmtUrls))
                    {
                        try
                        {
                            BeatorajaConfigService.SyncTableUrls(
                                tempBeatorajaRootPath,
                                [],
                                BmtTableExportService.ReadManagedTableUrls(tempBeatorajaBmtTablePath).Select(entry => entry.Url));
                        }
                        catch (Exception ex)
                        {
                            NLogWrapper.FileLogger?.Warn(ex, "beatoraja_old_table_url_cleanup_failed root=" + (tempBeatorajaRootPath ?? string.Empty));
                        }
                    }
                    ownerViewModel.tables.QueueBeatorajaBmtExportAll("SettingDialog.SaveSettings", tempBeatorajaBmtTablePath);
                }
                beatorajaBmtExportMs = beatorajaBmtExportStopwatch.ElapsedMilliseconds;
            }
            finally
            {
                LogSettingsPerformance(
                    "settings_post_save",
                    totalStopwatch,
                    "impact=" + impact
                    + " tablesAvailable=" + tablesAvailable.ToString().ToLowerInvariant()
                    + " customFolderSearchRootSyncMs=" + customFolderSearchRootSyncMs
                    + " playerRuntimeMs=" + playerRuntimeMs
                    + " lr2BackupNoticeMs=" + lr2BackupNoticeMs
                    + " playlistUrlCompletionMs=" + playlistUrlCompletionMs
                    + " lr2GeneratedDataSyncMs=" + lr2GeneratedDataSyncMs
                    + " beatorajaBmtExportMs=" + beatorajaBmtExportMs);
            }
        }

        private CustomFolderOutputBaseSearchRootSyncPlan PrepareCustomFolderNormalOutputBaseSearchRootSync()
        {
            return PrepareCustomFolderNormalOutputBaseSearchRootSyncWithSettings(
                ownerViewModel.customFolderOutputSettingsProvider()
                    ?? throw new InvalidOperationException("Custom-folder output settings provider returned null."));
        }

        private CustomFolderOutputBaseSearchRootSyncPlan PrepareCustomFolderNormalOutputBaseSearchRootSyncWithSettings(
            CustomFolderOutputSettingsSnapshot settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            if (!settings.OperationModeLR2DB || lr2config == null)
            {
                return default;
            }

            CustomFolderOutputBaseSearchRootSyncPlan plan = CustomFolderOutputBaseSearchRootSyncService.PrepareNormalOutputBaseRoots(
                lr2config,
                tempLR2CustomFolderOutputDir,
                settings.LR2CustomFolderOutputBaseDir,
                tempLR2CustomFolderAdditionalOutputBaseDirs,
                settings.LR2CustomFolderAdditionalOutputBaseDirs,
                []);
            if (plan.Changed)
            {
                lr2config.Save();
            }
            return plan;
        }

        private CustomFolderOutputBaseSearchRootSyncResult CompleteCustomFolderNormalOutputBaseSearchRootSync(CustomFolderOutputBaseSearchRootSyncPlan plan)
        {
            return CompleteCustomFolderNormalOutputBaseSearchRootSyncWithSettings(
                plan,
                ownerViewModel.customFolderOutputSettingsProvider()
                    ?? throw new InvalidOperationException("Custom-folder output settings provider returned null."));
        }

        private CustomFolderOutputBaseSearchRootSyncResult CompleteCustomFolderNormalOutputBaseSearchRootSyncWithSettings(
            CustomFolderOutputBaseSearchRootSyncPlan plan,
            CustomFolderOutputSettingsSnapshot settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            if (!settings.OperationModeLR2DB || lr2config == null)
            {
                return default;
            }

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.CompleteAdditionalOutputBaseRootSync(
                    lr2config,
                    plan);
            if (!result.Changed)
            {
                return result;
            }

            lr2config.Save();
            return result;
        }

        private void ApplyCustomFolderNormalOutputBaseSearchRootSyncResult(CustomFolderOutputBaseSearchRootSyncResult result)
        {
            if (!result.Changed)
            {
                return;
            }

            isSearchRootsChanged = true;
            isBMSDirectoryAdded |= result.AddedCount > 0;
            isBMSDirectoryRemoved |= result.RemovedCount > 0;
            if (!string.IsNullOrWhiteSpace(selectedLR2ConfigBmsDirectory)
                && !lr2config.GetBMSSearchDirectories().Contains(selectedLR2ConfigBmsDirectory, StringComparer.OrdinalIgnoreCase))
            {
                selectedLR2ConfigBmsDirectory = LR2ConfigBMSDirectories.FirstOrDefault();
            }
            ApplyRuntimeSearchRootsForCurrentMode();
            RaisePropertyChanged(() => LR2ConfigBMSDirectories);
            RaisePropertyChanged(() => AvailableBMSDirectories);
            RaisePropertyChanged(() => SelectedBmsSearchRootPath);
            RaiseValidationStateChanged();
        }

        internal bool SyncRootCustomFolderOutputSearchRootsAfterSettingsChange()
        {
            return SyncRootCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
                ownerViewModel.customFolderOutputSettingsProvider()
                    ?? throw new InvalidOperationException("Custom-folder output settings provider returned null."));
        }

        internal bool SyncRootCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
            CustomFolderOutputSettingsSnapshot settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            if (!settings.OperationModeLR2DB || lr2config == null || ownerViewModel.tables == null)
            {
                return false;
            }
            return ownerViewModel.tables.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
                tempLR2CustomFolderAsRootOutputDir,
                lr2config,
                settings);
        }

        private int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges()
        {
            return ApplyCustomFolderAdditionalOutputBaseRegistrationChangesWithSettings(
                ownerViewModel.customFolderOutputSettingsProvider()
                    ?? throw new InvalidOperationException("Custom-folder output settings provider returned null."));
        }

        private int ApplyCustomFolderAdditionalOutputBaseRegistrationChangesWithSettings(
            CustomFolderOutputSettingsSnapshot settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            IReadOnlyList<CustomFolderOutputBaseEntry> oldEntries = CustomFolderOutputBaseRegistry.CreateAdditionalEntries(
                CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(tempLR2CustomFolderAdditionalOutputBaseDirs));
            IReadOnlyList<CustomFolderOutputBaseEntry> newEntries = CustomFolderOutputBaseRegistry.CreateAdditionalEntries(
                CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(settings.LR2CustomFolderAdditionalOutputBaseDirs));
            Dictionary<string, CustomFolderOutputBaseEntry> oldByName = oldEntries
                .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, CustomFolderOutputBaseEntry> newByName = newEntries
                .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var processedTables = new HashSet<BMSTable>();
            var changedTables = new List<BMSTable>();
            var outputDirPathBeforeByTable = new Dictionary<BMSTable, string>();
            var outputBaseDirPathBeforeByTable = new Dictionary<BMSTable, string>();

            foreach (CustomFolderOutputBaseEntry oldEntry in oldEntries)
            {
                string newName = null;
                if (pendingCustomFolderAdditionalOutputBaseRenames.TryGetValue(oldEntry.Name, out string renamedName)
                    && newByName.ContainsKey(renamedName))
                {
                    newName = renamedName;
                }
                else if (newByName.ContainsKey(oldEntry.Name))
                {
                    newName = oldEntry.Name;
                }

                string newBasePath = newName == null
                    ? settings.LR2CustomFolderOutputBaseDir
                    : newByName[newName].Path;
                if (newName != null
                    && string.Equals(oldEntry.Name, newName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(oldEntry.Path), CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(newBasePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ApplyCustomFolderOutputBaseNameChange(
                    oldEntry.Name,
                    oldEntry.Path,
                    newName,
                    processedTables,
                    changedTables,
                    outputDirPathBeforeByTable,
                    outputBaseDirPathBeforeByTable,
                    settings);
            }

            foreach (CustomFolderOutputBaseEntry newEntry in newEntries)
            {
                if (oldByName.ContainsKey(newEntry.Name))
                {
                    continue;
                }

                ApplyCustomFolderOutputBaseNameChange(
                    newEntry.Name,
                    null,
                    newEntry.Name,
                    processedTables,
                    changedTables,
                    outputDirPathBeforeByTable,
                    outputBaseDirPathBeforeByTable,
                    settings);
            }

            if (changedTables.Count > 0)
            {
                ownerViewModel.tables.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                    changedTables,
                    outputDirPathBeforeByTable,
                    "setting_custom_folder_output_base_registration_changed",
                    outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable,
                    settings: settings);
            }
            return changedTables.Count;
        }

        private void ApplyCustomFolderOutputBaseNameChange(
            string oldBaseName,
            string oldBasePath,
            string newBaseName,
            ISet<BMSTable> processedTables,
            ICollection<BMSTable> changedTables,
            IDictionary<BMSTable, string> outputDirPathBeforeByTable,
            IDictionary<BMSTable, string> outputBaseDirPathBeforeByTable,
            CustomFolderOutputSettingsSnapshot settings)
        {
            if (string.IsNullOrWhiteSpace(oldBaseName) || ownerViewModel.BMSTables == null)
            {
                return;
            }

            List<BMSTable> targetTables = [.. ownerViewModel.BMSTables
                .Where(table => table != null
                    && (processedTables == null || !processedTables.Contains(table))
                    && string.Equals(table.custom_folder_output_base_name, oldBaseName, StringComparison.OrdinalIgnoreCase))];
            foreach (BMSTable table in targetTables)
            {
                processedTables?.Add(table);
                string outputDirName = table.Output_dir;
                string beforeDirectory = !string.IsNullOrWhiteSpace(oldBasePath) && !string.IsNullOrWhiteSpace(outputDirName)
                    ? Path.Combine(oldBasePath, outputDirName)
                    : null;
                if (settings.OperationModeLR2DB
                    && !table.is_root_folder
                    && !string.IsNullOrWhiteSpace(beforeDirectory))
                {
                    outputDirPathBeforeByTable?.Add(table, beforeDirectory);
                    outputBaseDirPathBeforeByTable?.Add(table, oldBasePath);
                }
                table.custom_folder_output_base_name = CustomFolderOutputBaseRegistry.NormalizeBaseName(newBaseName);
                changedTables?.Add(table);
            }
        }

        public bool CheckValidation()
        {
            return CheckValidation(out string errMsg);
        }

        public bool CheckValidationForSave()
        {
            return CheckValidationForSave(out string errMsg);
        }

        public bool CheckValidationForSave(out string errMsg)
        {
            if (!HasValidationRelevantSettingChanges())
            {
                errMsg = string.Empty;
                return true;
            }
            return CheckValidation(out errMsg);
        }

        internal bool CheckValidationBeforeSave(out string errMsg)
        {
            if (HasValidationRelevantSettingChanges())
            {
                return CheckValidation(out errMsg);
            }
            return CheckCurrentRequiredSettingsForSave(out errMsg);
        }

        private bool CheckCurrentRequiredSettingsForSave(out string errMsg)
        {
            bool result = true;
            errMsg = string.Empty;
            if (OperationModeLR2DB)
            {
                if (!IsLR2SongDBPathValid() || !IsLR2ConfigXmlPathValid())
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath) + Environment.NewLine;
                    result = false;
                }
                else if (!ValidateLR2BmsSearchRootsDoNotOverlap(out string nestedBmsRootErrMsg))
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, nestedBmsRootErrMsg) + Environment.NewLine;
                    result = false;
                }
                if (string.IsNullOrWhiteSpace(LR2CustomFolderOutputDir))
                {
                    errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_CustomFolderOutputPathNotSet) + Environment.NewLine;
                    result = false;
                }
                else if (!ValidateCustomFolderOutputBaseDir(out string outputBaseDirErrMsg))
                {
                    errMsg = errMsg + outputBaseDirErrMsg + Environment.NewLine;
                    result = false;
                }
                if (string.IsNullOrWhiteSpace(LR2CustomFolderAsRootOutputDir))
                {
                    errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_CustomFolderRootOutputPathNotSet) + Environment.NewLine;
                    result = false;
                }
                else if (!ValidateCustomFolderAsRootOutputBaseDir(out string rootOutputBaseDirErrMsg))
                {
                    errMsg = errMsg + rootOutputBaseDirErrMsg + Environment.NewLine;
                    result = false;
                }
                if (!ValidateCustomFolderAdditionalOutputBaseDirs(out string additionalOutputBaseErrMsg))
                {
                    errMsg = errMsg + additionalOutputBaseErrMsg + Environment.NewLine;
                    result = false;
                }
            }
            else
            {
                IReadOnlyList<string> standaloneRoots = NormalizeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
                if (standaloneRoots.Count == 0)
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidStandaloneBmsRootPaths) + Environment.NewLine;
                    result = false;
                }
                else
                {
                    List<string> incompatibleRoots = GetLr2IncompatibleStandaloneBmsRootPaths(standaloneRoots);
                    if (incompatibleRoots.Count > 0)
                    {
                        errMsg += FormatSettingValidationMessage(
                            BeMusicSeeker.Properties.Resources.General,
                            BeMusicSeeker.Properties.Resources.Error_Lr2IncompatibleBmsRootPath + Environment.NewLine + string.Join(Environment.NewLine, incompatibleRoots)) + Environment.NewLine;
                        result = false;
                    }
                }
            }
            if ((UseBeatorajaScoreDb || EnableBeatorajaBmtOutput) && !IsBeatorajaRootPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaRootPath) + Environment.NewLine;
                result = false;
            }
            if (UseBeatorajaScoreDb && !IsBeatorajaScoreDbPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaScoreDbPath) + Environment.NewLine;
                result = false;
            }
            if (EnableBeatorajaBmtOutput && IsBeatorajaRootPathValid() && string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(Settings.Default.BeatorajaRootPath)))
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaBmtTablePath) + Environment.NewLine;
                result = false;
            }
            if (UseExternalPanelImage && !IsStagefilePathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidStagefilePath) + Environment.NewLine;
                result = false;
            }
            if (UsePlayeruBMplay && !IsuBMplayPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, BeMusicSeeker.Properties.Resources.Error_InvalidUBMPlayExecutablePath) + Environment.NewLine;
                result = false;
            }
            else if (UsePlayerLR2body)
            {
                if (!IsLR2PlayerRootPathValid())
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, BeMusicSeeker.Properties.Resources.Error_InvalidLR2RootPath) + Environment.NewLine;
                    result = false;
                }
                if (!File.Exists(LR2bodyPath))
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, FormatResource(BeMusicSeeker.Properties.Resources.Error_LR2ExecutableNotFoundFormat, LR2bodyPath)) + Environment.NewLine;
                    result = false;
                }
                if ((int)LR2bodyResolution.X <= 0 || (int)LR2bodyResolution.Y <= 0)
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, BeMusicSeeker.Properties.Resources.Error_InvalidLR2WindowSize) + Environment.NewLine;
                    result = false;
                }
            }
            else if (UsePlayerBMIIDXView && !IsBMIIDXViewPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, BeMusicSeeker.Properties.Resources.Error_InvalidBMIIDXViewPath) + Environment.NewLine;
                result = false;
            }
            if (string.IsNullOrWhiteSpace(TableListURL.ToString()))
            {
                errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_TableListUrlNotSet) + Environment.NewLine;
                result = false;
            }
            if (EnablePlaylistUrlCompletion && !IsPlaylistMd5UrlMappingTsvUriValid())
            {
                errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_InvalidPlaylistMd5UrlMappingTsvUri) + Environment.NewLine;
                result = false;
            }
            if (!IsBMSInstallDirValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Install, BeMusicSeeker.Properties.Resources.Error_InvalidBmsInstallDir) + Environment.NewLine;
                result = false;
            }
            if (!IsFolderNameFormatValid())
            {
                FolderNameFormat = defaultFolderNameFormat;
            }
            if (OperationModeLR2DB && IsLR2BackupEnabled && !IsLR2BackupPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Details, BeMusicSeeker.Properties.Resources.Error_LR2BackupPathNotSet) + Environment.NewLine;
                result = false;
            }
            return result;
        }

        private bool HasValidationRelevantSettingChanges()
        {
            return tempOperationModeLR2DB != operationModeLR2DB
                || HasSearchRootSettingsChanged()
                || HasCustomFolderOutputBaseSettingsChanged()
                || HasPlayHistoryFolderDisplayPresetDraftsChanged()
                || HasPathSettingValueChanged(tempLR2RootPath, Settings.Default.LR2RootPath, value => true)
                || HasPathSettingValueChanged(tempLR2SongDBPath, Settings.Default.LR2SongDBPath, value => true)
                || HasPathSettingValueChanged(tempLR2ConfigXmlPath, Settings.Default.LR2ConfigXmlPath, value => true)
                || tempUseBeatorajaScoreDb != Settings.Default.UseBeatorajaScoreDb
                || !string.Equals(tempBeatorajaRootPath, Settings.Default.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaPlayerId, Settings.Default.BeatorajaPlayerId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaScoreDbPath, Settings.Default.BeatorajaScoreDbPath, StringComparison.OrdinalIgnoreCase)
                || tempEnableBeatorajaBmtOutput != Settings.Default.EnableBeatorajaBmtOutput
                || !string.Equals(tempBeatorajaBmtTablePath, Settings.Default.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase)
                || HasPathSettingValueChanged(tempBMSRootPath, Settings.Default.BMSRootPath, value => true)
                || HasPathSettingValueChanged(tempuBMplayPath, Settings.Default.uBMplayPath, value => true)
                || HasPathSettingValueChanged(tempBMIIDXViewPath, Settings.Default.BMIIDXViewPath, value => true)
                || tempUsePlayeruBMplay != Settings.Default.UsePlayeruBMplay
                || tempUsePlayerLR2body != Settings.Default.UsePlayerLR2body
                || tempUsePlayerBMIIDXView != Settings.Default.UsePlayerBMIIDXView
                || !string.Equals(tempBMSInstallDir, Settings.Default.BMSInstallDir, StringComparison.OrdinalIgnoreCase)
                || !IsSameUri(tempTableListURL, Settings.Default.TableListURL)
                || tempEnablePlaylistUrlCompletion != Settings.Default.EnablePlaylistUrlCompletion
                || !string.Equals(tempPlaylistMd5UrlMappingTsvUri, Settings.Default.PlaylistMd5UrlMappingTsvUri, StringComparison.Ordinal)
                || tempIsLR2BackupEnabled != Settings.Default.IsLR2BackupEnabled
                || HasPathSettingValueChanged(tempLR2BackupPath, Settings.Default.LR2BackupPath, value => true)
                || tempUseExternalPanelImage != Settings.Default.UseExternalPanelImage
                || HasPathSettingValueChanged(tempStagefilePath, Settings.Default.StagefilePath, value => true)
                || !string.Equals(tempFolderNameFormat, Settings.Default.FolderNameFormat, StringComparison.Ordinal);
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
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath) + Environment.NewLine;
                    result = false;
                }
                else if (!ValidateLR2BmsSearchRootsDoNotOverlap(out string nestedBmsRootErrMsg))
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, nestedBmsRootErrMsg) + Environment.NewLine;
                    result = false;
                }
            }
            else
            {
                IReadOnlyList<string> standaloneRoots = NormalizeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
                if (standaloneRoots.Count == 0)
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidStandaloneBmsRootPaths) + Environment.NewLine;
                    result = false;
                }
                else
                {
                    List<string> incompatibleRoots = GetLr2IncompatibleStandaloneBmsRootPaths(standaloneRoots);
                    if (incompatibleRoots.Count > 0)
                    {
                        errMsg += FormatSettingValidationMessage(
                            BeMusicSeeker.Properties.Resources.General,
                            BeMusicSeeker.Properties.Resources.Error_Lr2IncompatibleBmsRootPath + Environment.NewLine + string.Join(Environment.NewLine, incompatibleRoots)) + Environment.NewLine;
                        result = false;
                    }
                }
            }
            if ((UseBeatorajaScoreDb || EnableBeatorajaBmtOutput) && !IsBeatorajaRootPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaRootPath) + Environment.NewLine;
                result = false;
            }
            if (UseBeatorajaScoreDb && !IsBeatorajaScoreDbPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaScoreDbPath) + Environment.NewLine;
                result = false;
            }
            if (EnableBeatorajaBmtOutput && IsBeatorajaRootPathValid() && string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(Settings.Default.BeatorajaRootPath)))
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaBmtTablePath) + Environment.NewLine;
                result = false;
            }
            if (UseExternalPanelImage && !IsStagefilePathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.General, BeMusicSeeker.Properties.Resources.Error_InvalidStagefilePath) + Environment.NewLine;
                result = false;
            }
            if (UsePlayeruBMplay && !IsuBMplayPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, BeMusicSeeker.Properties.Resources.Error_InvalidUBMPlayExecutablePath) + Environment.NewLine;
                result = false;
            }
            else if (UsePlayerLR2body)
            {
                if (!IsLR2PlayerRootPathValid())
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, BeMusicSeeker.Properties.Resources.Error_InvalidLR2RootPath) + Environment.NewLine;
                    result = false;
                }
                if (!File.Exists(LR2bodyPath))
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, FormatResource(BeMusicSeeker.Properties.Resources.Error_LR2ExecutableNotFoundFormat, LR2bodyPath)) + Environment.NewLine;
                    result = false;
                }
                if ((int)LR2bodyResolution.X <= 0 || (int)LR2bodyResolution.Y <= 0)
                {
                    errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, BeMusicSeeker.Properties.Resources.Error_InvalidLR2WindowSize) + Environment.NewLine;
                    result = false;
                }
            }
            else if (UsePlayerBMIIDXView && !IsBMIIDXViewPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Playback, BeMusicSeeker.Properties.Resources.Error_InvalidBMIIDXViewPath) + Environment.NewLine;
                result = false;
            }
            if (OperationModeLR2DB)
            {
                if (string.IsNullOrWhiteSpace(LR2CustomFolderOutputDir))
                {
                    errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_CustomFolderOutputPathNotSet) + Environment.NewLine;
                    result = false;
                }
                else if (!ValidateCustomFolderOutputBaseDir(out string outputBaseDirErrMsg))
                {
                    errMsg = errMsg + outputBaseDirErrMsg + Environment.NewLine;
                    result = false;
                }
                if (string.IsNullOrWhiteSpace(LR2CustomFolderAsRootOutputDir))
                {
                    errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_CustomFolderRootOutputPathNotSet) + Environment.NewLine;
                    result = false;
                }
                else if (!ValidateCustomFolderAsRootOutputBaseDir(out string rootOutputBaseDirErrMsg))
                {
                    errMsg = errMsg + rootOutputBaseDirErrMsg + Environment.NewLine;
                    result = false;
                }
                if (!ValidateCustomFolderAdditionalOutputBaseDirs(out string additionalOutputBaseErrMsg))
                {
                    errMsg = errMsg + additionalOutputBaseErrMsg + Environment.NewLine;
                    result = false;
                }
            }
            if (string.IsNullOrWhiteSpace(TableListURL.ToString()))
            {
                errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_TableListUrlNotSet) + Environment.NewLine;
                result = false;
            }
            if (EnablePlaylistUrlCompletion && !IsPlaylistMd5UrlMappingTsvUriValid())
            {
                errMsg += FormatPlaylistValidationMessage(BeMusicSeeker.Properties.Resources.Error_InvalidPlaylistMd5UrlMappingTsvUri) + Environment.NewLine;
                result = false;
            }
            if (!ValidatePlayHistoryFolderDisplayPresets(out string playHistoryPresetErrMsg))
            {
                errMsg += playHistoryPresetErrMsg;
                result = false;
            }
            if (!IsBMSInstallDirValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Install, BeMusicSeeker.Properties.Resources.Error_InvalidBmsInstallDir) + Environment.NewLine;
                result = false;
            }
            if (!IsFolderNameFormatValid())
            {
                FolderNameFormat = defaultFolderNameFormat;
            }
            if (OperationModeLR2DB && IsLR2BackupEnabled && !IsLR2BackupPathValid())
            {
                errMsg += FormatSettingValidationMessage(BeMusicSeeker.Properties.Resources.Details, BeMusicSeeker.Properties.Resources.Error_LR2BackupPathNotSet) + Environment.NewLine;
                result = false;
            }
            return result;
        }

        /// <summary>
        /// 検証済みの設定ダイアログ入力を `Settings.Default` メモリ領域から
        /// 実際の永続化記憶域（または構成ファイル）へ保存し、必要な事後処理（バックアップなど）を実行します。
        /// </summary>
        public async Task SaveSettings()
        {
            await SaveSettingsCore(runPostSaveActions: true);
        }

        public async Task SaveSettingsForInitialInitialize()
        {
            await SaveSettingsCore(runPostSaveActions: false);
            backupSavedSettings();
        }

        private async Task SaveSettingsCore(bool runPostSaveActions)
        {
            await SaveSettingsCore(runPostSaveActions, validate: false);
        }

        private async Task SaveSettingsCore(bool runPostSaveActions, bool validate)
        {
            var totalStopwatch = Stopwatch.StartNew();
            bool userConfigSaved = false;
            bool lr2ConfigSaved = false;
            long userConfigSaveMs = 0L;
            long lr2ConfigSaveMs = 0L;
            bool customFolderSearchRootSyncNeeded = false;
            bool customFolderOutputBaseJukeboxAdoptionNeeded = false;
            bool searchRootsChanged = false;
            bool settingValueChanges = false;
            bool operationModeChanged = false;
            bool validationPassed = false;
            SettingsPostSaveImpact postSaveImpact = SettingsPostSaveImpact.None;
            bool postSaveNeeded = false;
            long postSaveMs = 0L;
            long backupSnapshotMs = 0L;
            if (!validate || CheckValidationForSave())
            {
                validationPassed = true;
                settingValueChanges = HasSettingValueChanges();
                operationModeChanged = tempOperationModeLR2DB != operationModeLR2DB;
                searchRootsChanged = HasSearchRootSettingsChanged();
                bool standaloneSearchRootsChanged = !OperationModeLR2DB && searchRootsChanged;
                bool lr2SearchRootsChanged = OperationModeLR2DB && searchRootsChanged;
                bool customFolderOutputBaseSettingsChanged = OperationModeLR2DB && HasCustomFolderOutputBaseSettingsChanged();
                bool customFolderAdditionalOutputBaseDirsChanged = HasCustomFolderAdditionalOutputBaseDirsChanged();
                bool playHistoryFolderDisplayPresetDraftsChanged = HasPlayHistoryFolderDisplayPresetDraftsChanged();
                bool beatorajaDerivedSettingsSourceChanged = HasBeatorajaDerivedSettingsSourceChanged();
                bool lr2ConfigBoundaryChanged = OperationModeLR2DB
                    && (!string.Equals(tempLR2RootPath, Settings.Default.LR2RootPath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(tempLR2ConfigXmlPath, Settings.Default.LR2ConfigXmlPath, StringComparison.OrdinalIgnoreCase)
                        || tempOperationModeLR2DB != operationModeLR2DB
                        || lr2SearchRootsChanged
                        || customFolderOutputBaseSettingsChanged);
                SettingsSnapshotRefreshScope snapshotRefreshScope = BuildSettingsSnapshotRefreshScope(
                    operationModeChanged,
                    standaloneSearchRootsChanged,
                    lr2SearchRootsChanged,
                    customFolderOutputBaseSettingsChanged,
                    playHistoryFolderDisplayPresetDraftsChanged,
                    beatorajaDerivedSettingsSourceChanged,
                    lr2ConfigBoundaryChanged);
                if (standaloneSearchRootsChanged)
                {
                    PersistStandaloneBmsRootPathsToSettings();
                }
                if (customFolderAdditionalOutputBaseDirsChanged)
                {
                    PersistCustomFolderAdditionalOutputBaseDirsToSettings();
                }
                if (playHistoryFolderDisplayPresetDraftsChanged)
                {
                    PersistPlayHistoryFolderDisplayPresetsIfChanged();
                }
                if (beatorajaDerivedSettingsSourceChanged)
                {
                    RefreshBeatorajaDerivedSettings();
                }
                if (operationModeChanged)
                {
                    Settings.Default.OperationModeLR2DB = operationModeLR2DB;
                }
                bool userConfigNeedsSave = settingValueChanges
                    || operationModeChanged
                    || standaloneSearchRootsChanged
                    || customFolderAdditionalOutputBaseDirsChanged
                    || playHistoryFolderDisplayPresetDraftsChanged
                    || beatorajaDerivedSettingsSourceChanged;
                if (userConfigNeedsSave)
                {
                    var userConfigStopwatch = Stopwatch.StartNew();
                    Settings.Default.Save();
                    userConfigSaveMs = userConfigStopwatch.ElapsedMilliseconds;
                    userConfigSaved = true;
                }
                customFolderOutputBaseJukeboxAdoptionNeeded = HasCustomFolderOutputBaseJukeboxAdoptionConflicts();
                customFolderSearchRootSyncNeeded = lr2ConfigBoundaryChanged
                    || customFolderOutputBaseSettingsChanged
                    || customFolderOutputBaseJukeboxAdoptionNeeded;
                postSaveImpact = BuildSettingsPostSaveImpact(customFolderSearchRootSyncNeeded);
                postSaveNeeded = HasPostSaveImpact(postSaveImpact);
                LogSettingsPerformance(
                    "settings_change_classification",
                    null,
                    "runPostSaveActions=" + runPostSaveActions.ToString().ToLowerInvariant()
                    + " settingValueChanges=" + settingValueChanges.ToString().ToLowerInvariant()
                    + " operationModeChanged=" + operationModeChanged.ToString().ToLowerInvariant()
                    + " searchRootsChanged=" + searchRootsChanged.ToString().ToLowerInvariant()
                    + " standaloneSearchRootsChanged=" + standaloneSearchRootsChanged.ToString().ToLowerInvariant()
                    + " lr2SearchRootsChanged=" + lr2SearchRootsChanged.ToString().ToLowerInvariant()
                    + " customFolderOutputBaseSettingsChanged=" + customFolderOutputBaseSettingsChanged.ToString().ToLowerInvariant()
                    + " customFolderAdditionalOutputBaseDirsChanged=" + customFolderAdditionalOutputBaseDirsChanged.ToString().ToLowerInvariant()
                    + " customFolderOutputBaseJukeboxAdoptionNeeded=" + customFolderOutputBaseJukeboxAdoptionNeeded.ToString().ToLowerInvariant()
                    + " playHistoryFolderDisplayPresetDraftsChanged=" + playHistoryFolderDisplayPresetDraftsChanged.ToString().ToLowerInvariant()
                    + " beatorajaDerivedSettingsSourceChanged=" + beatorajaDerivedSettingsSourceChanged.ToString().ToLowerInvariant()
                    + " lr2ConfigBoundaryChanged=" + lr2ConfigBoundaryChanged.ToString().ToLowerInvariant()
                    + " postSaveImpact=" + postSaveImpact);
                bool lr2ConfigNeedsSave = false;
                if (lr2ConfigBoundaryChanged && operationModeLR2DB && lr2config != null)
                {
                    lr2ConfigNeedsSave = lr2config.EnsureDatabaseAutoReloadManualOnly();
                }
                if ((lr2SearchRootsChanged || lr2ConfigNeedsSave) && lr2config != null)
                {
                    var lr2ConfigStopwatch = Stopwatch.StartNew();
                    lr2config.Save();
                    lr2ConfigSaveMs = lr2ConfigStopwatch.ElapsedMilliseconds;
                    lr2ConfigSaved = true;
                }
                if (runPostSaveActions)
                {
                    var postSaveStopwatch = Stopwatch.StartNew();
                    if (searchRootsChanged)
                    {
                        ApplyRuntimeSearchRootsForCurrentMode();
                    }
                    if (postSaveNeeded)
                    {
                        await necessaryStepsAfterSaved(postSaveImpact);
                    }
                    postSaveMs = postSaveStopwatch.ElapsedMilliseconds;
                    var backupSnapshotStopwatch = Stopwatch.StartNew();
                    backupSavedSettingsCore(snapshotRefreshScope);
                    backupSnapshotMs = backupSnapshotStopwatch.ElapsedMilliseconds;
                }
            }
            LogSettingsPerformance(
                "settings_save",
                totalStopwatch,
                "validationPassed=" + validationPassed.ToString().ToLowerInvariant()
                + " runPostSaveActions=" + runPostSaveActions.ToString().ToLowerInvariant()
                + " settingValueChanges=" + settingValueChanges.ToString().ToLowerInvariant()
                + " operationModeChanged=" + operationModeChanged.ToString().ToLowerInvariant()
                + " searchRootsChanged=" + searchRootsChanged.ToString().ToLowerInvariant()
                + " customFolderSearchRootSyncNeeded=" + customFolderSearchRootSyncNeeded.ToString().ToLowerInvariant()
                + " postSaveImpact=" + postSaveImpact
                + " postSaveNeeded=" + postSaveNeeded.ToString().ToLowerInvariant()
                + " postSaveMs=" + postSaveMs
                + " backupSnapshotMs=" + backupSnapshotMs
                + " userConfigSaved=" + userConfigSaved.ToString().ToLowerInvariant()
                + " userConfigSaveMs=" + userConfigSaveMs
                + " lr2ConfigSaved=" + lr2ConfigSaved.ToString().ToLowerInvariant()
                + " lr2ConfigSaveMs=" + lr2ConfigSaveMs);
        }

        private bool HasBeatorajaDerivedSettingsSourceChanged()
        {
            return tempUseBeatorajaScoreDb != Settings.Default.UseBeatorajaScoreDb
                || tempEnableBeatorajaBmtOutput != Settings.Default.EnableBeatorajaBmtOutput
                || !string.Equals(tempBeatorajaRootPath, Settings.Default.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaPlayerId, Settings.Default.BeatorajaPlayerId, StringComparison.OrdinalIgnoreCase);
        }

        public void SaveOperationModeForRestart(bool operationMode)
        {
            string playHistorySelectedDisplayTargetIdentity = Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
            Settings.Default.Reload();
            Settings.Default.OperationModeLR2DB = operationMode;
            Settings.Default.PlayHistorySelectedDisplayTargetIdentity = playHistorySelectedDisplayTargetIdentity;
            Settings.Default.Save();
        }

        public void ResetSettings()
        {
            operationModeLR2DB = tempOperationModeLR2DB;
            Settings.Default.LR2ConfigXmlPath = tempLR2ConfigXmlPath;
            Settings.Default.OperationModeLR2DB = tempOperationModeLR2DB;
            Settings.Default.LR2RootPath = tempLR2RootPath;
            Settings.Default.LR2SongDBPath = tempLR2SongDBPath;
            Settings.Default.UseBeatorajaScoreDb = tempUseBeatorajaScoreDb;
            Settings.Default.BeatorajaRootPath = tempBeatorajaRootPath;
            Settings.Default.BeatorajaPlayerId = tempBeatorajaPlayerId;
            Settings.Default.BeatorajaScoreDbPath = tempBeatorajaScoreDbPath;
            Settings.Default.EnableBeatorajaBmtOutput = tempEnableBeatorajaBmtOutput;
            Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled = tempKeepBeatorajaBmtFilesWhenOutputDisabled;
            Settings.Default.BeatorajaBmtHashOutputMode = tempBeatorajaBmtHashOutputMode;
            Settings.Default.BeatorajaBmtTablePath = tempBeatorajaBmtTablePath;
            Settings.Default.RegisterBeatorajaBmtUrls = tempRegisterBeatorajaBmtUrls;
            Settings.Default.BMSRootPath = tempBMSRootPath;
            Settings.Default.StandaloneBmsRootPaths = tempStandaloneBmsRootPaths;
            Settings.Default.uBMplayPath = tempuBMplayPath;
            Settings.Default.BMIIDXViewPath = tempBMIIDXViewPath;
            Settings.Default.UsePlayeruBMplay = tempUsePlayeruBMplay;
            Settings.Default.UsePlayerLR2body = tempUsePlayerLR2body;
            Settings.Default.UsePlayerBMIIDXView = tempUsePlayerBMIIDXView;
            Settings.Default.LR2CustomFolderOutputBaseDir = tempLR2CustomFolderOutputDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = tempLR2CustomFolderAdditionalOutputBaseDirs;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = tempLR2CustomFolderAsRootOutputDir;
            Settings.Default.PlaylistDefaultIgnoreFolderOutput = tempPlaylistDefaultIgnoreFolderOutput;
            Settings.Default.BMSInstallDir = tempBMSInstallDir;
            Settings.Default.TableListURL = tempTableListURL;
            Settings.Default.EnablePlaylistUrlCompletion = tempEnablePlaylistUrlCompletion;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = tempOverwritePlaylistUrlsWithCompletion;
            Settings.Default.EnableStellaFullPlaylistUrlCompletion = tempEnableStellaFullPlaylistUrlCompletion;
            Settings.Default.PlaylistMd5UrlMappingTsvUri = tempPlaylistMd5UrlMappingTsvUri;
            Settings.Default.PlayHistoryDisplayTargetSetsJson = tempPlayHistoryDisplayTargetSetsJson;
            Settings.Default.LR2bodyResolution = tempLR2bodyResolution;
            Settings.Default.IsSaveLR2bodyWindowPosition = tempIsSaveLR2bodyWindowPosition;
            Settings.Default.IsLR2BackupEnabled = tempIsLR2BackupEnabled;
            Settings.Default.LR2BackupPath = tempLR2BackupPath;
            Settings.Default.LR2BackupTarget = tempLR2BackupTarget;
            Settings.Default.LR2BackupSpan = tempLR2BackupSpan;
            Settings.Default.LR2BackupNum = tempLR2BackupNum;
            Settings.Default.UseExternalWebBrowser = tempUseExternalWebBrowser;
            Settings.Default.UseExternalPanelImage = tempUseExternalPanelImage;
            string restoredAppearanceTheme = AppThemeService.NormalizeTheme(tempAppearanceTheme);
            Settings.Default.AppearanceTheme = restoredAppearanceTheme;
            AppThemeService.ApplyTheme(restoredAppearanceTheme);
            Settings.Default.CustomTableFontSize = tempCustomTableFontSize;
            Settings.Default.CustomTableRowHeight = tempCustomTableRowHeight;
            Settings.Default.CustomTableHeaderHeight = tempCustomTableHeaderHeight;
            Settings.Default.StagefilePath = tempStagefilePath;
            Settings.Default.FolderNameFormat = tempFolderNameFormat;
            Settings.Default.UseOnlyShiftJISChars = tempUseOnlyShiftJISChars;
            Settings.Default.ShowScoreViewerRegisterConfirmMsg = tempShowScoreViewerRegisterConfirmMsg;
            Settings.Default.ShowDiffBMSInstallConfirmMsg = tempShowDiffBMSInstallConfirmMsg;
            Settings.Default.ShowDuplicateFileCheckConfirmMsg = tempShowDuplicateFileCheckConfirmMsg;
            Settings.Default.ShowRecommUpdatedMsg = tempShowRecommUpdatedMsg;
            Settings.Default.ScanBmsFilesOnStartup = tempScanBmsFilesOnStartup;
            Settings.Default.SkipInitPlaylistLoad = tempSkipInitPlaylistLoad;
            Settings.Default.StartupSelectInstallPending = tempStartupSelectInstallPending;
            Settings.Default.EnableReadOptimizedPragmas = tempEnableReadOptimizedPragmas;
            Settings.Default.EstimateOfflineScoreRanking = tempEstimateOfflineScoreRanking;
            Settings.Default.UpdateLr2IrRankingCacheOnStartup = tempUpdateLr2IrRankingCacheOnStartup;
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = tempEnableDownloadLr2IrScoreAndDetectUnsent;
            Settings.Default.AutoInstall = tempEnableAutoInstall;
            Settings.Default.KeepInstallablePackagesPending = tempKeepInstallablePackagesPending;
            Settings.Default.AutoApplyAmbiguousInstallDestination = tempAutoApplyAmbiguousInstallDestination;
            Settings.Default.DeletePendingPackageSourceAfterInstall = tempDeletePendingPackageSourceAfterInstall;
            Settings.Default.EnableSmartComponentOverwrite = tempEnableSmartComponentOverwrite;
            Settings.Default.KeepSmartOverwriteProtectedFilesByRenaming = tempKeepSmartOverwriteProtectedFilesByRenaming;
            Settings.Default.ScanBmsFilesOnStartup = tempScanBmsFilesOnStartup;
            Settings.Default.EncoderSampleRate = tempEncoderSampleRate;
            Settings.Default.Encoder = (EncoderType)tempEncoderIndex;
            Settings.Default.EncoderFormat = tempEncoderFormat;
            Settings.Default.EncoderNormalization = tempEncoderNormalization;
            Settings.Default.EncoderExeDir = tempEncoderExeDir;
            Settings.Default.EncoderAmplifier = tempEncoderAmplifier;
            Settings.Default.EncoderQuality = tempEncoderQuality;
            Settings.Default.EncodeFileNameFormat = tempEncodeFileNameFormat;
            Settings.Default.PlayerDriver = (BassAudioPlayer.DeviceDriver)tempPlayerDriverIndex;
            if (playerDeviceNames != null)
            {
                playerDeviceNames = [.. BassAudioPlayer.DeviceList[NormalizePlayerDriver(Settings.Default.PlayerDriver)]];
            }
            Settings.Default.PlayerDevice = tempPlayerDevice;
            Settings.Default.PlayerDeviceName = tempPlayerDeviceName;
            Settings.Default.PlayerSampleRate = tempPlayerSampleRate;
            Settings.Default.PlayerFormat = tempPlayerFormat;
            Settings.Default.PlayerBufferSize = tempPlayerBufferSize;
            Settings.Default.PlayerWASAPIParam = tempPlayerWASAPIParam;
            Settings.Default.Lang = tempLanguage;
            Settings.Default.LangDisplayName = tempLanguageDisplayName;
            ResourceService.Current.ChangeCulture(tempLanguage);
            if (tempOperationModeLR2DB)
            {
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
            else
            {
                lr2config = null;
            }
            RefreshStandaloneBmsRootPathsFromSettings();
            RefreshCustomFolderAdditionalOutputBaseDirsFromSettings();
            ResetPlayHistoryFolderDisplayPresetsForCancel();
            RaisePropertyChanged(() => OperationModeLR2DB);
            RaisePropertyChanged(() => CanUseLr2Features);
            ownerViewModel.RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
            RaisePropertyChanged(() => LR2RootPath);
            RaisePropertyChanged(() => BMSRootPath);
            RaisePropertyChanged(() => StandaloneBmsRootPathList);
            RaisePropertyChanged(() => SelectedStandaloneBmsRootPath);
            RaisePropertyChanged(() => AvailableBMSDirectories);
            RaisePropertyChanged(() => SelectedBmsSearchRootPath);
            RaisePropertyChanged(() => IsBmsSearchRootEditorEnabled);
            RaisePropertyChanged(() => LR2SongDBPath);
            RaisePropertyChanged(() => LR2ConfigXmlPath);
            RaisePropertyChanged(() => UseBeatorajaScoreDb);
            RaisePropertyChanged(() => BeatorajaRootPath);
            RaisePropertyChanged(() => AvailableBeatorajaPlayers);
            RaisePropertyChanged(() => BeatorajaPlayerId);
            RaisePropertyChanged(() => BeatorajaScoreDbPath);
            RaisePropertyChanged(() => EnableBeatorajaBmtOutput);
            RaisePropertyChanged(() => KeepBeatorajaBmtFilesWhenOutputDisabled);
            RaisePropertyChanged(() => BeatorajaBmtHashOutputMode);
            RaisePropertyChanged(() => BeatorajaBmtTablePath);
            RaisePropertyChanged(() => RegisterBeatorajaBmtUrls);
            RaisePropertyChanged(() => uBMplayPath);
            RaisePropertyChanged(() => BMIIDXViewPath);
            RaisePropertyChanged(() => UsePlayeruBMplay);
            RaisePropertyChanged(() => UsePlayerLR2body);
            RaisePropertyChanged(() => UsePlayerBMIIDXView);
            RaisePropertyChanged(() => UseInternalPlayer);
            RaisePropertyChanged(() => LR2bodyResolution);
            RaisePropertyChanged(() => IsSaveLR2bodyWindowPosition);
            RaisePropertyChanged(() => LR2ConfigBMSDirectories);
            RaisePropertyChanged(() => LR2CustomFolderOutputDir);
            RaisePropertyChanged(() => BMSInstallDir);
            RaisePropertyChanged(() => LR2CustomFolderAsRootOutputDir);
            RaiseDefaultCustomFolderOutputPropertiesChanged();
            RaisePropertyChanged(() => TableListURL);
            RaisePropertyChanged(() => EnablePlaylistUrlCompletion);
            RaisePropertyChanged(() => OverwritePlaylistUrlsWithCompletion);
            RaisePropertyChanged(() => EnableStellaFullPlaylistUrlCompletion);
            RaisePropertyChanged(() => PlaylistMd5UrlMappingTsvUri);
            RaisePropertyChanged(() => PlayHistoryFolderDisplayPresets);
            RaisePropertyChanged(() => SelectedPlayHistoryFolderDisplayPreset);
            RaisePropertyChanged(() => PlayHistoryFolderDisplayPresetPlaylistOptions);
            RaisePropertyChanged(() => IsLR2BackupEnabled);
            RaisePropertyChanged(() => LR2BackupPath);
            RaisePropertyChanged(() => LR2BackupTarget);
            RaisePropertyChanged(() => LR2BackupSpan);
            RaisePropertyChanged(() => LR2BackupNum);
            RaisePropertyChanged(() => UseExternalWebBrowser);
            RaisePropertyChanged(() => UseExternalPanelImage);
            RaisePropertyChanged(() => AppearanceTheme);
            RaisePropertyChanged(() => CustomTableFontSize);
            RaisePropertyChanged(() => CustomTableRowHeight);
            RaisePropertyChanged(() => CustomTableHeaderHeight);
            RaisePropertyChanged(() => StagefilePath);
            RaisePropertyChanged(() => FolderNameFormat);
            RaisePropertyChanged(() => UseOnlyShiftJISChars);
            RaisePropertyChanged(() => ShowScoreViewerRegisterConfirmMsg);
            RaisePropertyChanged(() => ShowDiffBMSInstallConfirmMsg);
            RaisePropertyChanged(() => ShowDuplicateFileCheckConfirmMsg);
            RaisePropertyChanged(() => ShowRecommUpdatedMsg);
            RaisePropertyChanged(() => ScanBmsFilesOnStartup);
            RaisePropertyChanged(() => SkipInitPlaylistLoad);
            RaisePropertyChanged(() => StartupSelectInstallPending);
            RaisePropertyChanged(() => EnableReadOptimizedPragmas);
            RaisePropertyChanged(() => EstimateOfflineScoreRanking);
            RaisePropertyChanged(() => UpdateLr2IrRankingCacheOnStartup);
            RaisePropertyChanged(() => EnableDownloadLr2IrScoreAndDetectUnsent);
            RaisePropertyChanged(() => EnableAutoInstall);
            RaisePropertyChanged(() => KeepInstallablePackagesPending);
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
            RaisePropertyChanged(() => IsOperationModeChanged);
            RaiseValidationStateChanged();
            backupSavedSettings();
            ResetLr2PlayHistorySchemaStatus();
        }

        /// <summary>
        /// キャンセル時に play history FOLDER 表示プリセットの draft と settings 値を保存済み snapshot へ戻します。
        /// ResetSettings 全体は audio device なども復元するため、この helper は preset UI の回帰テストから副作用なしで呼べる境界にしています。
        /// </summary>
        internal void ResetPlayHistoryFolderDisplayPresetsForCancel()
        {
            Settings.Default.PlayHistoryDisplayTargetSetsJson = tempPlayHistoryDisplayTargetSetsJson;
            RefreshPlayHistoryFolderDisplayPresetsFromSettings();
            tempPlayHistoryDisplayTargetSetDraftsJson = SerializePlayHistoryFolderDisplayPresetDraftsForChangeTracking();
        }

        public RestartMode IsNeedRestartForSaved()
        {
            RestartMode restartMode = RestartMode.None;
            bool scoreSourceChanged = tempUseBeatorajaScoreDb != Settings.Default.UseBeatorajaScoreDb
                || !string.Equals(tempBeatorajaRootPath, Settings.Default.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaPlayerId, Settings.Default.BeatorajaPlayerId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaScoreDbPath, Settings.Default.BeatorajaScoreDbPath, StringComparison.OrdinalIgnoreCase);
            if (tempOperationModeLR2DB != OperationModeLR2DB)
            {
                return RestartMode.All;
            }
            if (OperationModeLR2DB)
            {
                if (tempLR2SongDBPath != Settings.Default.LR2SongDBPath)
                {
                    return RestartMode.All;
                }
                if (tempLR2ConfigXmlPath != Settings.Default.LR2ConfigXmlPath)
                {
                    restartMode |= RestartMode.FolderOnly;
                }
                if (HasLR2ConfigBmsSearchRootsChanged())
                {
                    restartMode |= RestartMode.FolderOnly;
                }
                if (HasCustomFolderOutputBaseSettingsChanged())
                {
                    restartMode |= RestartMode.FolderOnly;
                }
            }
            else if (HasStandaloneBmsRootPathsChanged())
            {
                restartMode |= RestartMode.FolderOnly;
            }
            if (scoreSourceChanged)
            {
                restartMode |= RestartMode.ScoreOnly;
            }
            return restartMode;
        }

        public RestartMode IsNeedRestartForSaveOrCancel()
        {
            return RestartMode.None;
        }
    }

    internal static IReadOnlyList<PlaylistCustomFolderOutputBaseOption> CreatePlaylistCustomFolderOutputBaseOptions(
        string defaultOutputBaseDirectory = null,
        IEnumerable<string> additionalOutputBaseDirectories = null,
        bool includeNoChange = false,
        bool useCurrentSettingsWhenMissing = true)
    {
        List<PlaylistCustomFolderOutputBaseOption> options = [];
        if (includeNoChange)
        {
            options.Add(new PlaylistCustomFolderOutputBaseOption(
                BeMusicSeeker.Properties.Resources.Playlist_summary_bulk_no_change,
                null,
                isNoChange: true));
        }

        string defaultBase = useCurrentSettingsWhenMissing && string.IsNullOrWhiteSpace(defaultOutputBaseDirectory)
            ? Settings.Default.LR2CustomFolderOutputBaseDir
            : defaultOutputBaseDirectory;
        string defaultLabel = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(defaultBase);
        options.Add(new PlaylistCustomFolderOutputBaseOption(
            string.IsNullOrWhiteSpace(defaultLabel) ? BeMusicSeeker.Properties.Resources.Playlist_output?.TrimEnd(':', ' ') : defaultLabel,
            null));

        foreach (CustomFolderOutputBaseEntry entry in CustomFolderOutputBaseRegistry.CreateAdditionalEntries(
            additionalOutputBaseDirectories
                ?? (useCurrentSettingsWhenMissing
                    ? CustomFolderOutputBaseRegistry.ReadAdditionalBaseDirectories()
                    : [])))
        {
            if (!options.Any(option =>
                    !option.IsNoChange
                    && string.Equals(option.Label, entry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new PlaylistCustomFolderOutputBaseOption(entry.Name, entry.Name));
            }
        }

        return options;
    }

    public sealed class PlaylistSummaryBulkBooleanOption
    {
        public PlaylistSummaryBulkBooleanOption(string label, bool? value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        public bool? Value { get; }
    }

    public sealed class PlaylistCustomFolderOutputBaseOption
    {
        public PlaylistCustomFolderOutputBaseOption(string label, string baseName, bool isNoChange = false)
        {
            Label = label ?? string.Empty;
            BaseName = string.IsNullOrWhiteSpace(baseName) ? null : baseName;
            IsNoChange = isNoChange;
        }

        public string Label { get; }

        public string BaseName { get; }

        public bool IsNoChange { get; }
    }

    public sealed class PlaylistSummaryCustomFolderOutputPatch
    {
        public bool? AllSongsFolder { get; set; }

        public bool? UserFolder { get; set; }

        public bool? LevelFolder { get; set; }

        public bool? AlphabetFolder { get; set; }

        public bool? ClearFolder { get; set; }

        public bool? DJLevelFolder { get; set; }

        public bool? CategoryAllFolder { get; set; }

        public bool? OtherFolder { get; set; }

        public bool? RandomFolder { get; set; }

        public bool? BpmSortFolder { get; set; }

        public bool? BpSortFolder { get; set; }

        public bool? PlayCountSortFolder { get; set; }

        public bool? LastPlaySortFolder { get; set; }

        internal IEnumerable<KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>> Enumerate()
        {
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder, AllSongsFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.UserFolder, UserFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder, LevelFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder, AlphabetFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder, ClearFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder, DJLevelFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder, CategoryAllFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder, OtherFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.RandomFolder, RandomFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder, BpmSortFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder, BpSortFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder, PlayCountSortFolder);
            yield return new KeyValuePair<LR2SongDBExtended.playlist.CustomFolderType, bool?>(LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder, LastPlaySortFolder);
        }
    }
}
