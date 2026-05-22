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

/// <summary>
/// Holds package chart sources accepted by the virtual chart-list pipeline.
/// </summary>
internal sealed class PackageChartSourceSnapshot
{
    /// <summary>
    /// Initializes a snapshot whose package entries are the live virtual row factory input.
    /// </summary>
    /// <param name="entries">Package chart entries.</param>
    internal PackageChartSourceSnapshot(IReadOnlyList<PackageChartEntry> entries)
    {
        Entries = entries ?? [];
    }

    /// <summary>
    /// Gets live package chart entries used by the virtual package subset.
    /// </summary>
    internal IReadOnlyList<PackageChartEntry> Entries { get; }
}

internal readonly struct BmsonLibraryRowCacheSyncResult
{
    internal BmsonLibraryRowCacheSyncResult(bool membershipChanged, bool sortKeyChanged, bool sourceIdentityChanged = false, bool sourceReferenceChanged = false)
    {
        MembershipChanged = membershipChanged;
        SortKeyChanged = sortKeyChanged;
        SourceIdentityChanged = sourceIdentityChanged;
        SourceReferenceChanged = sourceReferenceChanged;
    }

    internal bool MembershipChanged { get; }

    internal bool SortKeyChanged { get; }

    internal bool SourceIdentityChanged { get; }

    internal bool SourceReferenceChanged { get; }

    internal bool SourceChanged => MembershipChanged || SourceIdentityChanged || SourceReferenceChanged;
}

internal readonly struct NormalLibrarySortCacheKey : IEquatable<NormalLibrarySortCacheKey>
{
    internal NormalLibrarySortCacheKey(long sourceGeneration, long sortKeyGeneration, string columnName, ListSortDirection direction, int rowCount)
        : this(sourceGeneration, sortKeyGeneration, 0, 0, 0, columnName, direction, rowCount)
    {
    }

    internal NormalLibrarySortCacheKey(long sourceGeneration, long sortKeyGeneration, int scoreGeneration, int chartInfoGeneration, int maintenanceGeneration, string columnName, ListSortDirection direction, int rowCount)
    {
        SourceGeneration = sourceGeneration;
        SortKeyGeneration = sortKeyGeneration;
        ScoreGeneration = scoreGeneration;
        ChartInfoGeneration = chartInfoGeneration;
        MaintenanceGeneration = maintenanceGeneration;
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        RowCount = rowCount;
    }

    internal long SourceGeneration { get; }

    internal long SortKeyGeneration { get; }

    internal int ScoreGeneration { get; }

    internal int ChartInfoGeneration { get; }

    internal int MaintenanceGeneration { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal int RowCount { get; }

    public bool Equals(NormalLibrarySortCacheKey other)
    {
        return SourceGeneration == other.SourceGeneration
            && SortKeyGeneration == other.SortKeyGeneration
            && ScoreGeneration == other.ScoreGeneration
            && ChartInfoGeneration == other.ChartInfoGeneration
            && MaintenanceGeneration == other.MaintenanceGeneration
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
            hashCode = (hashCode * 397) ^ ScoreGeneration;
            hashCode = (hashCode * 397) ^ ChartInfoGeneration;
            hashCode = (hashCode * 397) ^ MaintenanceGeneration;
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(ColumnName ?? string.Empty);
            hashCode = (hashCode * 397) ^ (int)Direction;
            hashCode = (hashCode * 397) ^ RowCount;
            return hashCode;
        }
    }
}

internal readonly struct VirtualChartSubsetSortCacheKey : IEquatable<VirtualChartSubsetSortCacheKey>
{
    internal VirtualChartSubsetSortCacheKey(
        long sourceGeneration,
        long sortKeyGeneration,
        int scoreGeneration,
        int chartInfoGeneration,
        int maintenanceGeneration,
        int treeMode,
        string subsetName,
        long sourceRowsSignature,
        string columnName,
        ListSortDirection direction,
        int rowCount)
    {
        SourceGeneration = sourceGeneration;
        SortKeyGeneration = sortKeyGeneration;
        ScoreGeneration = scoreGeneration;
        ChartInfoGeneration = chartInfoGeneration;
        MaintenanceGeneration = maintenanceGeneration;
        TreeMode = treeMode;
        SubsetName = subsetName ?? string.Empty;
        SourceRowsSignature = sourceRowsSignature;
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        RowCount = rowCount;
    }

    internal long SourceGeneration { get; }

    internal long SortKeyGeneration { get; }

    internal int ScoreGeneration { get; }

    internal int ChartInfoGeneration { get; }

    internal int MaintenanceGeneration { get; }

    internal int TreeMode { get; }

    internal string SubsetName { get; }

    internal long SourceRowsSignature { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal int RowCount { get; }

    public bool Equals(VirtualChartSubsetSortCacheKey other)
    {
        return SourceGeneration == other.SourceGeneration
            && SortKeyGeneration == other.SortKeyGeneration
            && ScoreGeneration == other.ScoreGeneration
            && ChartInfoGeneration == other.ChartInfoGeneration
            && MaintenanceGeneration == other.MaintenanceGeneration
            && TreeMode == other.TreeMode
            && string.Equals(SubsetName, other.SubsetName, StringComparison.Ordinal)
            && SourceRowsSignature == other.SourceRowsSignature
            && string.Equals(ColumnName, other.ColumnName, StringComparison.Ordinal)
            && Direction == other.Direction
            && RowCount == other.RowCount;
    }

    public override bool Equals(object obj)
    {
        return obj is VirtualChartSubsetSortCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = SourceGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ SortKeyGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ ScoreGeneration;
            hashCode = (hashCode * 397) ^ ChartInfoGeneration;
            hashCode = (hashCode * 397) ^ MaintenanceGeneration;
            hashCode = (hashCode * 397) ^ TreeMode;
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(SubsetName ?? string.Empty);
            hashCode = (hashCode * 397) ^ SourceRowsSignature.GetHashCode();
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(ColumnName ?? string.Empty);
            hashCode = (hashCode * 397) ^ (int)Direction;
            hashCode = (hashCode * 397) ^ RowCount;
            return hashCode;
        }
    }
}

internal readonly struct MainViewSummaryCacheKey : IEquatable<MainViewSummaryCacheKey>
{
    internal MainViewSummaryCacheKey(long sourceGeneration, long sortKeyGeneration, int rowCount, bool includeBmsonRows, string filterIdentity)
    {
        SourceGeneration = sourceGeneration;
        SortKeyGeneration = sortKeyGeneration;
        RowCount = rowCount;
        IncludeBmsonRows = includeBmsonRows;
        FilterIdentity = filterIdentity ?? string.Empty;
    }

    internal long SourceGeneration { get; }

    internal long SortKeyGeneration { get; }

    internal int RowCount { get; }

    internal bool IncludeBmsonRows { get; }

    internal string FilterIdentity { get; }

    public bool Equals(MainViewSummaryCacheKey other)
    {
        return SourceGeneration == other.SourceGeneration
            && SortKeyGeneration == other.SortKeyGeneration
            && RowCount == other.RowCount
            && IncludeBmsonRows == other.IncludeBmsonRows
            && string.Equals(FilterIdentity, other.FilterIdentity, StringComparison.Ordinal);
    }

    public override bool Equals(object obj)
    {
        return obj is MainViewSummaryCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = SourceGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ SortKeyGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ RowCount;
            hashCode = (hashCode * 397) ^ IncludeBmsonRows.GetHashCode();
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(FilterIdentity ?? string.Empty);
            return hashCode;
        }
    }
}

internal readonly struct VirtualNormalLibrarySortDescriptor : IEquatable<VirtualNormalLibrarySortDescriptor>
{
    internal VirtualNormalLibrarySortDescriptor(string columnName, ListSortDirection direction, int prewarmPriority = 0)
    {
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        PrewarmPriority = prewarmPriority;
    }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal int PrewarmPriority { get; }

    public bool Equals(VirtualNormalLibrarySortDescriptor other)
    {
        return string.Equals(ColumnName, other.ColumnName, StringComparison.Ordinal)
            && Direction == other.Direction
            && PrewarmPriority == other.PrewarmPriority;
    }

    public override bool Equals(object obj)
    {
        return obj is VirtualNormalLibrarySortDescriptor other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = ColumnName != null ? StringComparer.Ordinal.GetHashCode(ColumnName) : 0;
            hashCode = (hashCode * 397) ^ (int)Direction;
            hashCode = (hashCode * 397) ^ PrewarmPriority;
            return hashCode;
        }
    }
}

internal enum MainViewDataDependency
{
    Unknown,
    SourceMembership,
    IdentitySortKey,
    ChartInfo,
    Score,
    Maintenance,
    Warning
}

internal enum MainViewRefreshAction
{
    Refresh,
    RefreshDisplay,
    SkipMainViewRefresh
}

internal readonly struct MainViewRefreshDecision
{
    internal MainViewRefreshDecision(
        MainViewRefreshAction action,
        MainViewDataDependency dependency,
        MainViewDataDependency sortDependency,
        string reason,
        string detail)
    {
        Action = action;
        Dependency = dependency;
        SortDependency = sortDependency;
        Reason = reason ?? string.Empty;
        Detail = detail ?? string.Empty;
    }

    internal MainViewRefreshAction Action { get; }

    internal MainViewDataDependency Dependency { get; }

    internal MainViewDataDependency SortDependency { get; }

    internal string Reason { get; }

    internal string Detail { get; }

    internal bool ShouldRefresh => Action == MainViewRefreshAction.Refresh;

    internal bool ShouldRefreshDisplay => Action == MainViewRefreshAction.RefreshDisplay;
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
            All = 2,
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

        private bool tempUseBeatorajaScoreDb;

        private string tempBeatorajaRootPath;

        private string tempBeatorajaPlayerId;

        private string tempBeatorajaScoreDbPath;

        private bool tempEnableBeatorajaBmtOutput;

        private string tempBeatorajaBmtTablePath;

        private bool tempRegisterBeatorajaBmtUrls;

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

        private bool tempEnableStellaFullPlaylistUrlCompletion;

        private string tempPlaylistMd5UrlMappingTsvUri;

        private bool tempIsLR2BackupEnabled;

        private string tempLR2BackupPath;

        private Backup.Target tempLR2BackupTarget;

        private int tempLR2BackupSpan;

        private int tempLR2BackupNum;

        private bool tempUseExternalWebBrowser;

        private bool tempUseExternalPanelImage;

        private string tempAppearanceTheme;

        private readonly IReadOnlyList<AppearanceThemeOption> appearanceThemeOptions;

        private bool tempShowScoreViewerRegisterConfirmMsg;

        private bool tempShowDiffBMSInstallConfirmMsg;

        private bool tempShowRecommUpdatedMsg;

        private bool tempSkipInitFileCheck;

        private bool tempSkipInitPlaylistLoad;

        private bool tempStartupSelectInstallPending;

        private bool tempEnableReadOptimizedPragmas;

        private bool tempSkipEstimateOfflineScoreRanking;

        private bool tempEnableDownloadLr2IrScoreAndDetectUnsent;

        private bool tempEnableAutoInstall;

        private bool tempKeepInstallablePackagesPending;

        private bool tempUseEverythingForPendingPackageSourceScan;

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

        private ListenerCommand<FolderSelectionMessage> _OpenRootFolderCommand;

        private ListenerCommand<OpeningFileSelectionMessage> _OpenFileCommand;

        private ListenerCommand<FolderSelectionMessage> _OpenDirCommand;

        private bool operationModeLR2DB;

        private bool isBMSDirectoryAdded;

        private ListenerCommand<FolderSelectionMessage> _AddBMSDirCommandFromMainWindow;

        private ListenerCommand<FolderSelectionMessage> _AddBMSDirCommand;

        private bool isBMSDirectoryRemoved;

        private ListenerCommand<string> _RemoveDirCommand;

        private bool isSearchRootsChanged;

        private string tempStandaloneBmsRootPaths;

        private string selectedStandaloneBmsRootPath;

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

        public bool CanSaveSettings => CheckValidation();

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
            RaisePropertyChanged(() => BMSInstallDir);
            RaiseValidationStateChanged();
        }

        private void ConfirmAndRestartForOperationModeChange(bool value)
        {
            var confirmationMessage = new ConfirmationMessage(
                BeMusicSeeker.Properties.Resources.Confirm_RestartForOperationModeChange,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxImage.Question,
                MessageBoxButton.OKCancel,
                "ConfirmationDialog");
            ownerViewModel.Messenger.Raise(confirmationMessage);
            if (confirmationMessage.Response != true)
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
                DispatcherMessageBox.Show(
                    BeMusicSeeker.Properties.Resources.Error_RestartApplicationFailed + Environment.NewLine + Environment.NewLine + ex.Message,
                    BeMusicSeeker.Properties.Resources.Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Hand);
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
                if (!IsLR2RootPathValid() && !IsLR2PlayerRootPathValid())
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public ObservableCollection<string> StandaloneBmsRootPathList { get; } = [];

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
                    RaiseValidationStateChanged();
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
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaisePropertyChanged(() => BMSInstallDir);
                RaiseValidationStateChanged();
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
                    Settings.Default.EnableBeatorajaBmtOutput = value;
                    RaisePropertyChanged("EnableBeatorajaBmtOutput");
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
                    RaiseValidationStateChanged();
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
                if (Settings.Default.UsePlayeruBMplay != value)
                {
                    Settings.Default.UsePlayeruBMplay = value;
                    RaisePropertyChanged("UsePlayeruBMplay");
                    RaiseValidationStateChanged();
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
                    RaiseValidationStateChanged();
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
                    RaiseValidationStateChanged();
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
                    return [.. (from d in lr2config.GetBMSSearchDirectories()
                            where string.IsNullOrWhiteSpace(rootDir) || ownerViewModel.BMSTables == null || ownerViewModel.BMSTables.Where(t => t != null && t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)).All(t => !string.Equals(d, Path.Combine(rootDir, t.Output_dir), StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(tempRootDir) || !string.Equals(d, Path.Combine(tempRootDir, t.Output_dir), StringComparison.OrdinalIgnoreCase)))
                            select d)];
                }
                return [];
            }
            private set
            {
            }
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
                    if (IsLR2CustomFolderOutputDirValid(value))
                    {
                        Settings.Default.LR2CustomFolderOutputBaseDir = value;
                    }
                    RaisePropertyChanged("LR2CustomFolderOutputDir");
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                    RaiseValidationStateChanged();
                }
            }
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
                    RaiseValidationStateChanged();
                }
            }
        }

        public Uri TableListURL
        {
            get
            {
                if (!IsTableListURLValid())
                {
                    Settings.Default.TableListURL = new Uri(Settings.DefaultTableListUrl);
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
                    var confirmationMessage = new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_confirm_skip_init_file_check, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
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
                    var confirmationMessage = new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_confirm_skip_init_playlist_load, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
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
                    var confirmationMessage = new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_confirm_skip_offline_score_ranking_estimation, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
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
                    playerDeviceNames = [.. BassAudioPlayer.DeviceList[Settings.Default.PlayerDriver]];
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
                return playerDeviceNames ??= [.. BassAudioPlayer.DeviceList[(BassAudioPlayer.DeviceDriver)PlayerDriverIndex]];
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
                BassAudioPlayer.DeviceDescriptor deviceDescriptor = PlayerDeviceNames.FirstOrDefault(d => d.Driver == Settings.Default.PlayerDevice);
                if (deviceDescriptor.Driver == null)
                {
                    deviceDescriptor = PlayerDeviceNames.FirstOrDefault(d => d.Name == Settings.Default.PlayerDeviceName);
                    if (deviceDescriptor.Driver == null)
                    {
                        Match match = Regex.Match(Settings.Default.PlayerDeviceName ?? string.Empty, "(.*?)[\\s(\\d-]+(.*?)\\)");
                        if (match.Success)
                        {
                            string p1 = match.Groups[1].ToString();
                            string p2 = match.Groups[2].ToString();
                            deviceDescriptor = PlayerDeviceNames.FirstOrDefault(d => Regex.IsMatch(d.Name ?? string.Empty, p1 + ".*" + p2));
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
                    BassAudioPlayer.DeviceDescriptor deviceDescriptor = PlayerDeviceNames.FirstOrDefault(d => d.Driver == value);
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
                _OpenRootFolderCommand ??= new ListenerCommand<FolderSelectionMessage>(OpenRootFolder);
                return _OpenRootFolderCommand;
            }
        }

        public ListenerCommand<OpeningFileSelectionMessage> OpenFileCommand
        {
            get
            {
                _OpenFileCommand ??= new ListenerCommand<OpeningFileSelectionMessage>(OpenFile);
                return _OpenFileCommand;
            }
        }

        public ListenerCommand<FolderSelectionMessage> OpenDirCommand
        {
            get
            {
                _OpenDirCommand ??= new ListenerCommand<FolderSelectionMessage>(OpenDir);
                return _OpenDirCommand;
            }
        }

        public ListenerCommand<FolderSelectionMessage> AddBMSDirCommandFromMainWindow
        {
            get
            {
                _AddBMSDirCommandFromMainWindow ??= new ListenerCommand<FolderSelectionMessage>(AddBMSDirectoryToRootFolderAndSave);
                return _AddBMSDirCommandFromMainWindow;
            }
        }

        public ListenerCommand<FolderSelectionMessage> AddBMSDirCommand
        {
            get
            {
                _AddBMSDirCommand ??= new ListenerCommand<FolderSelectionMessage>(AddBMSDirectoryToSearchRoots);
                return _AddBMSDirCommand;
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
            appearanceThemeOptions =
            [
                new AppearanceThemeOption(AppThemeService.Light),
                new AppearanceThemeOption(AppThemeService.Dark)
            ];
            ownerViewModelEventListener = new PropertyChangedEventListener(ownerViewModel);
            ownerViewModelEventListener.RegisterHandler(() => owner.BMSTables, delegate
            {
                settingDialogViewModel.RaisePropertyChanged(() => settingDialogViewModel.LR2ConfigBMSDirectories);
                settingDialogViewModel.RaisePropertyChanged(() => settingDialogViewModel.AvailableBMSDirectories);
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
            return Directory.Exists(value);
        }

        public static IReadOnlyList<string> GetStandaloneBmsRootPathsFromSettings()
        {
            return DeserializeStandaloneBmsRootPaths(Settings.Default.StandaloneBmsRootPaths, Settings.Default.BMSRootPath);
        }

        public static IReadOnlyList<string> DeserializeStandaloneBmsRootPaths(string serializedPaths, string legacyBmsRootPath = null)
        {
            List<string> paths = [];
            if (!string.IsNullOrWhiteSpace(serializedPaths))
            {
                paths.AddRange(serializedPaths.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries));
            }
            if (paths.Count == 0 && !string.IsNullOrWhiteSpace(legacyBmsRootPath) && Directory.Exists(legacyBmsRootPath))
            {
                paths.Add(legacyBmsRootPath);
            }
            return NormalizeStandaloneBmsRootPaths(paths);
        }

        public static IReadOnlyList<string> NormalizeStandaloneBmsRootPaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                return [];
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
                    continue;
                }
                fullPath = TrimDirectorySeparatorUnlessRoot(fullPath);
                if (!Directory.Exists(fullPath))
                {
                    continue;
                }
                if (!normalized.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(fullPath);
                }
            }

            return normalized;
        }

        public static string SerializeStandaloneBmsRootPaths(IEnumerable<string> paths)
        {
            return string.Join(Environment.NewLine, NormalizeStandaloneBmsRootPaths(paths));
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
            StandaloneBmsRootPathList.Clear();
            foreach (string path in GetStandaloneBmsRootPathsFromSettings())
            {
                StandaloneBmsRootPathList.Add(path);
            }
            SelectedStandaloneBmsRootPath = StandaloneBmsRootPathList.FirstOrDefault();
            RaisePropertyChanged(() => StandaloneBmsRootPathList);
            RaisePropertyChanged(() => AvailableBMSDirectories);
            RaiseValidationStateChanged();
        }

        private void PersistStandaloneBmsRootPathsToSettings()
        {
            Settings.Default.StandaloneBmsRootPaths = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
            string firstRoot = DeserializeStandaloneBmsRootPaths(Settings.Default.StandaloneBmsRootPaths).FirstOrDefault();
            Settings.Default.BMSRootPath = string.IsNullOrWhiteSpace(firstRoot) ? null : firstRoot;
        }

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
            if (value == null)
            {
                return false;
            }
            if (OperationModeLR2DB && lr2config != null)
            {
                return lr2config.GetBMSSearchDirectories().Contains(value);
            }
            return NormalizeStandaloneBmsRootPaths(StandaloneBmsRootPathList).Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        private bool IsLR2CustomFolderAsRootOutputDirValid()
        {
            return IsLR2CustomFolderAsRootOutputDirValid(Settings.Default.LR2CustomFolderOutputBaseDirRootType);
        }

        private bool IsLR2CustomFolderAsRootOutputDirValid(string value)
        {
            if (lr2config != null && value != null)
            {
                return lr2config.GetBMSSearchDirectories().All(d => !(value + Path.DirectorySeparatorChar).StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
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
                GetType().GetProperty(name).GetSetMethod().Invoke(this, [response]);
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
                GetType().GetProperty(name).GetSetMethod().Invoke(this, [parameter.Response.FirstOrDefault()]);
            }
        }

        private void OpenDir(FolderSelectionMessage parameter)
        {
            if (parameter.Response != null)
            {
                string name = parameter.MessageKey.Substring(parameter.MessageKey.LastIndexOf('.') + 1);
                GetType().GetProperty(name).GetSetMethod().Invoke(this, [parameter.Response]);
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
                AddBMSDirectoryToLR2Config(parameter, saveImmediately: true);
                if (isSearchRootsChanged)
                {
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
                return;
            }
            AddStandaloneBmsRootPath(parameter);
            if (isSearchRootsChanged)
            {
                PersistStandaloneBmsRootPathsToSettings();
                Settings.Default.Save();
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

        private void AddBMSDirectoryToSearchRoots(FolderSelectionMessage parameter)
        {
            if (OperationModeLR2DB)
            {
                AddBMSDirectoryToLR2Config(parameter, saveImmediately: false);
                return;
            }
            AddStandaloneBmsRootPath(parameter);
        }

        private void AddStandaloneBmsRootPath(FolderSelectionMessage parameter)
        {
            if (parameter?.Response == null)
            {
                return;
            }
            AddStandaloneBmsRootPathsCore([parameter.Response], parameter.MessageKey);
        }

        public void AddStandaloneBmsRootPaths(IEnumerable<string> paths)
        {
            AddStandaloneBmsRootPathsCore(paths, null);
        }

        private void AddStandaloneBmsRootPathsCore(IEnumerable<string> paths, string messageKey)
        {
            try
            {
                string before = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
                List<string> requestedPaths = [.. NormalizeStandaloneBmsRootPaths(paths ?? [])];
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
                if (!string.IsNullOrWhiteSpace(messageKey))
                {
                    string name = messageKey.Substring(messageKey.LastIndexOf('.') + 1);
                    GetType().GetProperty(name).GetSetMethod().Invoke(this, [requestedPath]);
                }
                string after = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
                isSearchRootsChanged = !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
                isBMSDirectoryAdded = isSearchRootsChanged;
                RaisePropertyChanged(() => StandaloneBmsRootPathList);
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaiseValidationStateChanged();
            }
            catch (Exception ex)
            {
                ownerViewModel.Messenger.Raise(new ConfirmationMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            }
        }

        private void AddBMSDirectoryToLR2Config(FolderSelectionMessage parameter, bool saveImmediately)
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
                lr2config.AddBMSSearchDirectories([response]);
                isSearchRootsChanged = true;
                isBMSDirectoryAdded = Directory.EnumerateFiles(response, "*", System.IO.SearchOption.AllDirectories).Any(path => BeMusicSeeker.Models.ChartFileKindResolver.BmsExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));
                RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaiseValidationStateChanged();
                if (!string.IsNullOrWhiteSpace(parameter.MessageKey))
                {
                    string name = parameter.MessageKey.Substring(parameter.MessageKey.LastIndexOf('.') + 1);
                    GetType().GetProperty(name).GetSetMethod().Invoke(this, [response]);
                }
                if (saveImmediately)
                {
                    lr2config.Save();
                }
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
                    throw new InvalidOperationException("BMSインストール先ディレクトリの登録解除は出来ません");
                }
                string before = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
                List<string> remaining = [.. StandaloneBmsRootPathList.Where(path => !string.Equals(path, normalizedDir, StringComparison.OrdinalIgnoreCase))];
                StandaloneBmsRootPathList.Clear();
                foreach (string path in remaining)
                {
                    StandaloneBmsRootPathList.Add(path);
                }
                SelectedStandaloneBmsRootPath = StandaloneBmsRootPathList.FirstOrDefault();
                string after = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
                isSearchRootsChanged = !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
                isBMSDirectoryRemoved = isSearchRootsChanged;
                RaisePropertyChanged(() => StandaloneBmsRootPathList);
                RaisePropertyChanged(() => AvailableBMSDirectories);
                RaisePropertyChanged(() => BMSInstallDir);
                RaiseValidationStateChanged();
            }
            catch (Exception ex)
            {
                ownerViewModel.Messenger.Raise(new ConfirmationMessage(ex.Message, "エラー", MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
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
                if (lr2config.RemoveBMSSearchDirectories([dir]))
                {
                    isSearchRootsChanged = true;
                    if (saveImmediately)
                    {
                        lr2config.Save();
                    }
                    RaisePropertyChanged(() => LR2ConfigBMSDirectories);
                    RaisePropertyChanged(() => AvailableBMSDirectories);
                    RaisePropertyChanged(() => BMSInstallDir);
                    RaiseValidationStateChanged();
                    if (ownerViewModel.BMSFiles != null && ownerViewModel.BMSFiles.Any(f => f.path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
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

        private void backupSavedSettings()
        {
            operationModeLR2DB = Settings.Default.OperationModeLR2DB;
            RefreshStandaloneBmsRootPathsFromSettings();
            tempOperationModeLR2DB = Settings.Default.OperationModeLR2DB;
            tempLR2RootPath = Settings.Default.LR2RootPath;
            tempLR2SongDBPath = Settings.Default.LR2SongDBPath;
            tempLR2ConfigXmlPath = Settings.Default.LR2ConfigXmlPath;
            tempUseBeatorajaScoreDb = Settings.Default.UseBeatorajaScoreDb;
            tempBeatorajaRootPath = Settings.Default.BeatorajaRootPath;
            tempBeatorajaPlayerId = Settings.Default.BeatorajaPlayerId;
            tempBeatorajaScoreDbPath = Settings.Default.BeatorajaScoreDbPath;
            tempEnableBeatorajaBmtOutput = Settings.Default.EnableBeatorajaBmtOutput;
            tempBeatorajaBmtTablePath = Settings.Default.BeatorajaBmtTablePath;
            tempRegisterBeatorajaBmtUrls = Settings.Default.RegisterBeatorajaBmtUrls;
            tempBMSRootPath = Settings.Default.BMSRootPath;
            tempStandaloneBmsRootPaths = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
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
            tempEnableStellaFullPlaylistUrlCompletion = Settings.Default.EnableStellaFullPlaylistUrlCompletion;
            tempPlaylistMd5UrlMappingTsvUri = Settings.Default.PlaylistMd5UrlMappingTsvUri;
            tempIsLR2BackupEnabled = Settings.Default.IsLR2BackupEnabled;
            tempLR2BackupPath = Settings.Default.LR2BackupPath;
            tempLR2BackupTarget = Settings.Default.LR2BackupTarget;
            tempLR2BackupSpan = Settings.Default.LR2BackupSpan;
            tempLR2BackupNum = Settings.Default.LR2BackupNum;
            tempUseExternalWebBrowser = Settings.Default.UseExternalWebBrowser;
            tempUseExternalPanelImage = Settings.Default.UseExternalPanelImage;
            tempAppearanceTheme = AppThemeService.NormalizeTheme(Settings.Default.AppearanceTheme);
            tempStagefilePath = Settings.Default.StagefilePath;
            tempFolderNameFormat = Settings.Default.FolderNameFormat;
            tempUseOnlyShiftJISChars = Settings.Default.UseOnlyShiftJISChars;
            tempShowScoreViewerRegisterConfirmMsg = Settings.Default.ShowScoreViewerRegisterConfirmMsg;
            tempShowDiffBMSInstallConfirmMsg = Settings.Default.ShowDiffBMSInstallConfirmMsg;
            tempShowRecommUpdatedMsg = Settings.Default.ShowRecommUpdatedMsg;
            tempSkipInitFileCheck = Settings.Default.SkipInitFileCheck;
            tempSkipInitPlaylistLoad = Settings.Default.SkipInitPlaylistLoad;
            tempStartupSelectInstallPending = Settings.Default.StartupSelectInstallPending;
            tempEnableReadOptimizedPragmas = Settings.Default.EnableReadOptimizedPragmas;
            tempSkipEstimateOfflineScoreRanking = Settings.Default.SkipEstimateOfflineScoreRanking;
            tempEnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent;
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
            tempLanguageDisplayName = Settings.Default.LangDisplayName;
            isSearchRootsChanged = false;
            isBMSDirectoryAdded = false;
            isBMSDirectoryRemoved = false;
            RaisePropertyChanged(() => IsOperationModeChanged);
            RaiseValidationStateChanged();
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
            if ((!Settings.Default.UsePlayeruBMplay && tempUsePlayeruBMplay != Settings.Default.UsePlayeruBMplay) || (!Settings.Default.UsePlayerBMIIDXView && tempUsePlayerBMIIDXView != Settings.Default.UsePlayerBMIIDXView) || (!Settings.Default.UsePlayerLR2body && tempUsePlayerLR2body != Settings.Default.UsePlayerLR2body) || (!Settings.Default.OperationModeLR2DB && tempOperationModeLR2DB != Settings.Default.OperationModeLR2DB))
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
                ownerViewModel.bmsPlayer = new LR2body(LR2bodyPath, new LR2Config(Settings.Default.LR2ConfigXmlPath));
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
                return (Settings.Default.PlayerWASAPIParam != tempPlayerWASAPIParam && (Settings.Default.PlayerDriver == BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE || Settings.Default.PlayerDriver == BassAudioPlayer.DeviceDriver.WASAPI_SHARED));
            })())
            {
                AudioPlayerInitTest(playSound: false);
            }
            if (Settings.Default.OperationModeLR2DB && Settings.Default.IsLR2BackupEnabled && tempIsLR2BackupEnabled != Settings.Default.IsLR2BackupEnabled)
            {
                ownerViewModel.Messenger.Raise(new ConfirmationMessage("LR2設定ファイルバックアップ機能は" + Environment.NewLine + "次回起動時から有効になります", "確認", MessageBoxImage.Asterisk, MessageBoxButton.OK, "ConfirmationDialog"));
            }
            if (tempEnablePlaylistUrlCompletion != Settings.Default.EnablePlaylistUrlCompletion || tempOverwritePlaylistUrlsWithCompletion != Settings.Default.OverwritePlaylistUrlsWithCompletion || tempEnableStellaFullPlaylistUrlCompletion != Settings.Default.EnableStellaFullPlaylistUrlCompletion || !string.Equals(tempPlaylistMd5UrlMappingTsvUri, Settings.Default.PlaylistMd5UrlMappingTsvUri, StringComparison.Ordinal))
            {
                ownerViewModel.tables.SchedulePlaylistUrlCompletionRefresh("SettingDialog.SaveSettings");
            }
            if (tempEnableBeatorajaBmtOutput != Settings.Default.EnableBeatorajaBmtOutput
                || tempRegisterBeatorajaBmtUrls != Settings.Default.RegisterBeatorajaBmtUrls
                || !string.Equals(tempBeatorajaRootPath, Settings.Default.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(tempBeatorajaBmtTablePath, Settings.Default.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase))
            {
                if (BeatorajaConfigService.IsBeatorajaRootPathValid(tempBeatorajaRootPath)
                    && !string.IsNullOrWhiteSpace(tempBeatorajaBmtTablePath)
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
        }

        public bool CheckValidation()
        {
            return CheckValidation(out string errMsg);
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
            else if (NormalizeStandaloneBmsRootPaths(StandaloneBmsRootPathList).Count == 0)
            {
                errMsg = errMsg + BeMusicSeeker.Properties.Resources.General + ": " + BeMusicSeeker.Properties.Resources.Error_InvalidStandaloneBmsRootPaths + Environment.NewLine;
                result = false;
            }
            if ((UseBeatorajaScoreDb || EnableBeatorajaBmtOutput) && !IsBeatorajaRootPathValid())
            {
                errMsg = errMsg + BeMusicSeeker.Properties.Resources.General + ": " + BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaRootPath + Environment.NewLine;
                result = false;
            }
            if (UseBeatorajaScoreDb && !IsBeatorajaScoreDbPathValid())
            {
                errMsg = errMsg + BeMusicSeeker.Properties.Resources.General + ": " + BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaScoreDbPath + Environment.NewLine;
                result = false;
            }
            if (EnableBeatorajaBmtOutput && IsBeatorajaRootPathValid() && string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(Settings.Default.BeatorajaRootPath)))
            {
                errMsg = errMsg + BeMusicSeeker.Properties.Resources.General + ": " + BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaBmtTablePath + Environment.NewLine;
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
                if (!IsLR2PlayerRootPathValid())
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
                errMsg = errMsg + BeMusicSeeker.Properties.Resources.Install + ": " + BeMusicSeeker.Properties.Resources.Error_InvalidBmsInstallDir + Environment.NewLine;
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
            await SaveSettingsCore(runPostSaveActions: true);
        }

        public async Task SaveSettingsForInitialInitialize()
        {
            await SaveSettingsCore(runPostSaveActions: false);
            backupSavedSettings();
        }

        private async Task SaveSettingsCore(bool runPostSaveActions)
        {
            if (CheckValidation())
            {
                bool searchRootsChanged = isSearchRootsChanged;
                bool lr2SearchRootsChanged = OperationModeLR2DB && searchRootsChanged;
                PersistStandaloneBmsRootPathsToSettings();
                RefreshBeatorajaDerivedSettings();
                Settings.Default.OperationModeLR2DB = operationModeLR2DB;
                Settings.Default.Save();
                if (lr2SearchRootsChanged && lr2config != null)
                {
                    lr2config.Save();
                }
                if (runPostSaveActions)
                {
                    if (searchRootsChanged)
                    {
                        ApplyRuntimeSearchRootsForCurrentMode();
                    }
                    await necessaryStepsAfterSaved();
                    backupSavedSettings();
                }
            }
        }

        public void SaveOperationModeForRestart(bool operationMode)
        {
            Settings.Default.Reload();
            Settings.Default.OperationModeLR2DB = operationMode;
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
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = tempLR2CustomFolderAsRootOutputDir;
            Settings.Default.BMSInstallDir = tempBMSInstallDir;
            Settings.Default.TableListURL = tempTableListURL;
            Settings.Default.EnablePlaylistUrlCompletion = tempEnablePlaylistUrlCompletion;
            Settings.Default.OverwritePlaylistUrlsWithCompletion = tempOverwritePlaylistUrlsWithCompletion;
            Settings.Default.EnableStellaFullPlaylistUrlCompletion = tempEnableStellaFullPlaylistUrlCompletion;
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
            string restoredAppearanceTheme = AppThemeService.NormalizeTheme(tempAppearanceTheme);
            Settings.Default.AppearanceTheme = restoredAppearanceTheme;
            AppThemeService.ApplyTheme(restoredAppearanceTheme);
            Settings.Default.StagefilePath = tempStagefilePath;
            Settings.Default.FolderNameFormat = tempFolderNameFormat;
            Settings.Default.UseOnlyShiftJISChars = tempUseOnlyShiftJISChars;
            Settings.Default.ShowScoreViewerRegisterConfirmMsg = tempShowScoreViewerRegisterConfirmMsg;
            Settings.Default.ShowDiffBMSInstallConfirmMsg = tempShowDiffBMSInstallConfirmMsg;
            Settings.Default.ShowRecommUpdatedMsg = tempShowRecommUpdatedMsg;
            Settings.Default.SkipInitFileCheck = tempSkipInitFileCheck;
            Settings.Default.SkipInitPlaylistLoad = tempSkipInitPlaylistLoad;
            Settings.Default.StartupSelectInstallPending = tempStartupSelectInstallPending;
            Settings.Default.EnableReadOptimizedPragmas = tempEnableReadOptimizedPragmas;
            Settings.Default.SkipEstimateOfflineScoreRanking = tempSkipEstimateOfflineScoreRanking;
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = tempEnableDownloadLr2IrScoreAndDetectUnsent;
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
            playerDeviceNames = [.. BassAudioPlayer.DeviceList[Settings.Default.PlayerDriver]];
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
            RaisePropertyChanged(() => OperationModeLR2DB);
            RaisePropertyChanged(() => CanUseLr2Features);
            RaisePropertyChanged(() => LR2RootPath);
            RaisePropertyChanged(() => BMSRootPath);
            RaisePropertyChanged(() => StandaloneBmsRootPathList);
            RaisePropertyChanged(() => SelectedStandaloneBmsRootPath);
            RaisePropertyChanged(() => AvailableBMSDirectories);
            RaisePropertyChanged(() => LR2SongDBPath);
            RaisePropertyChanged(() => LR2ConfigXmlPath);
            RaisePropertyChanged(() => UseBeatorajaScoreDb);
            RaisePropertyChanged(() => BeatorajaRootPath);
            RaisePropertyChanged(() => AvailableBeatorajaPlayers);
            RaisePropertyChanged(() => BeatorajaPlayerId);
            RaisePropertyChanged(() => BeatorajaScoreDbPath);
            RaisePropertyChanged(() => EnableBeatorajaBmtOutput);
            RaisePropertyChanged(() => BeatorajaBmtTablePath);
            RaisePropertyChanged(() => RegisterBeatorajaBmtUrls);
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
            RaisePropertyChanged(() => EnableStellaFullPlaylistUrlCompletion);
            RaisePropertyChanged(() => PlaylistMd5UrlMappingTsvUri);
            RaisePropertyChanged(() => IsLR2BackupEnabled);
            RaisePropertyChanged(() => LR2BackupPath);
            RaisePropertyChanged(() => LR2BackupTarget);
            RaisePropertyChanged(() => LR2BackupSpan);
            RaisePropertyChanged(() => LR2BackupNum);
            RaisePropertyChanged(() => UseExternalWebBrowser);
            RaisePropertyChanged(() => UseExternalPanelImage);
            RaisePropertyChanged(() => AppearanceTheme);
            RaisePropertyChanged(() => StagefilePath);
            RaisePropertyChanged(() => FolderNameFormat);
            RaisePropertyChanged(() => UseOnlyShiftJISChars);
            RaisePropertyChanged(() => ShowScoreViewerRegisterConfirmMsg);
            RaisePropertyChanged(() => ShowDiffBMSInstallConfirmMsg);
            RaisePropertyChanged(() => ShowRecommUpdatedMsg);
            RaisePropertyChanged(() => SkipInitFileCheck);
            RaisePropertyChanged(() => SkipInitPlaylistLoad);
            RaisePropertyChanged(() => StartupSelectInstallPending);
            RaisePropertyChanged(() => EnableReadOptimizedPragmas);
            RaisePropertyChanged(() => SkipEstimateOfflineScoreRanking);
            RaisePropertyChanged(() => EnableDownloadLr2IrScoreAndDetectUnsent);
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
            RaisePropertyChanged(() => IsOperationModeChanged);
            RaiseValidationStateChanged();
            backupSavedSettings();
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
                if (isSearchRootsChanged)
                {
                    restartMode |= RestartMode.FolderOnly;
                }
            }
            else if (!string.Equals(tempStandaloneBmsRootPaths, SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList), StringComparison.OrdinalIgnoreCase))
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

    public class PlaylistPropertyDialogViewModel : ViewModel
    {
        private BMSTable bmsTable;

        private readonly MainWindowViewModel ownerViewModel;

        private readonly bool isForNewTable;

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
                    return CustomFolderSortTypeExt.GetDisplayNames().Except(
                    [
                        LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL.ToDisplayName(),
                        LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE.ToDisplayName()
                    ]);
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
                value ??= string.Empty;
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
                    var confirmationMessage = new ConfirmationMessage("同期モードに設定するとローカルの変更が失われます。" + Environment.NewLine + "よろしいですか？", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
                    ownerViewModel.Messenger.Raise(confirmationMessage);
                    if (!confirmationMessage.Response.HasValue || !confirmationMessage.Response.Value)
                    {
                        RaisePropertyChanged("is_external_sync");
                        return;
                    }
                }
                else if (_is_external_sync && !value)
                {
                    var confirmationMessage2 = new ConfirmationMessage("同期モードを解除するとリモートの変更が反映されなくなります。" + Environment.NewLine + "よろしいですか？", "警告", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
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
            ownerViewModel = _owner;
            bmsTable = _table ?? throw new ArgumentNullException("_table");
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
                    bmsTable.Folder_order = [];
                }
                else
                {
                    bmsTable.Folder_order = [.. folder_order];
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
            temp_output_dir_full_path = Settings.Default.OperationModeLR2DB ? BMSPlaylist.GetCustomFolderOutputDirectory(bmsTable) : null;
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
                            oldEntriesSnapshot = [.. bmsTable.entries];
                        }
                        bmsTable = await ownerViewModel.tables.ResetBMSTableAsync(bmsTable, uri);
                        ownerViewModel.files.ReplaceReferenceBMSTable(sourceTable, bmsTable, oldEntriesSnapshot);
                        ownerViewModel.InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
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
                    ownerViewModel.RefreshPlaylistSummaryIfVisible("playlist_property_resync", invalidateTableCountCache: true);
                }
            }
            if (flag)
            {
                ownerViewModel.files.RefreshReferenceDisplayForTable(bmsTable);
                ownerViewModel.InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
                ownerViewModel.RefreshPlaylistSummaryIfVisible("playlist_property_changed", invalidateTableCountCache: true);
            }
            ownerViewModel.tables.CommitBMSTableHeaderToDB(bmsTable);
            if (Settings.Default.OperationModeLR2DB)
            {
                string customFolderOutputDirectory = BMSPlaylist.GetCustomFolderOutputDirectory(bmsTable);
                ownerViewModel.tables.MigrateCustomFolderOutputDirectory(bmsTable, temp_output_dir_full_path, customFolderOutputDirectory);
                List<string> bMSSearchDirectories = ownerViewModel.lr2config.GetBMSSearchDirectories();
                if (!temp_is_root_folder && bmsTable.is_root_folder)
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Union([customFolderOutputDirectory]).Distinct(StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
                else if (temp_is_root_folder && !bmsTable.is_root_folder)
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Except([temp_output_dir_full_path], StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
                else if (temp_is_root_folder && bmsTable.is_root_folder && !customFolderOutputDirectory.Equals(temp_output_dir_full_path, StringComparison.OrdinalIgnoreCase))
                {
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Except([temp_output_dir_full_path], StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Union([customFolderOutputDirectory]).Distinct(StringComparer.OrdinalIgnoreCase));
                    ownerViewModel.lr2config.Save();
                }
            }
            ownerViewModel.tables.QueueBeatorajaBmtExportForTable(bmsTable, "PlaylistPropertyDialog.SaveProperties");
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

    internal enum viewUpdateMode
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
        ChartInfoParseErrorFilterSelected = 40,
        NewlyInstalledFolderSelected = 49,
        PendingInstallFolderSelected = 50,
        KeywordFilterUpdated = 65,
        ModeFilterUpdated = 66,
        SortUpdated = 81,
        UpdatedNone = 255
    }

    internal enum MainViewOperationSection
    {
        Library,
        Playlist,
        InstallPending,
        InstallInstalled,
        FullScanCheck,
        ChartInfoParseError
    }

    public enum FolderFilterType
    {
        DirectoryFilter,
        ArtistFilter,
        PlayListFilter,
        FilterNone
    }

    internal sealed class NormalLibraryTreeFilter
    {
        private NormalLibraryTreeFilter(FolderFilterType type, string term, string identity)
        {
            Type = type;
            Term = term ?? string.Empty;
            Identity = identity ?? string.Empty;
        }

        internal FolderFilterType Type { get; }

        internal string Term { get; }

        internal string Identity { get; }

        internal static NormalLibraryTreeFilter Create(FolderFilterType type, string filterKey)
        {
            if (string.IsNullOrWhiteSpace(filterKey))
            {
                return null;
            }
            switch (type)
            {
                case FolderFilterType.DirectoryFilter:
                    string directoryTerm = EnsureTrailingDirectorySeparator(filterKey);
                    return new NormalLibraryTreeFilter(type, directoryTerm, "directory:" + directoryTerm);
                case FolderFilterType.ArtistFilter:
                    return new NormalLibraryTreeFilter(type, filterKey, "artist:" + filterKey);
                default:
                    return null;
            }
        }

        internal bool Matches(ChartListSourceRow row)
        {
            if (row == null)
            {
                return false;
            }
            return Type switch
            {
                FolderFilterType.DirectoryFilter => ContainsIgnoreCase(row.Path, Term),
                FolderFilterType.ArtistFilter => ContainsIgnoreCase(row.Artist, Term),
                _ => false,
            };
        }

        internal bool Matches(LibraryChartRow row)
        {
            if (row == null)
            {
                return false;
            }
            return Type switch
            {
                FolderFilterType.DirectoryFilter => ContainsIgnoreCase(row.path, Term),
                FolderFilterType.ArtistFilter => ContainsIgnoreCase(row.Artist, Term),
                _ => false,
            };
        }

        internal bool Matches(ChartFile chart)
        {
            if (chart == null)
            {
                return false;
            }
            return Type switch
            {
                FolderFilterType.DirectoryFilter => ContainsIgnoreCase(chart.Path, Term),
                FolderFilterType.ArtistFilter => ContainsIgnoreCase(chart.Artist, Term),
                _ => false,
            };
        }

        private static string EnsureTrailingDirectorySeparator(string path)
        {
            string normalizedPath = (path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedPath + Path.DirectorySeparatorChar;
        }

        private static bool ContainsIgnoreCase(string value, string term)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(term)
                && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }
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

        internal Dictionary<string, LibraryChartRef> ChartsByMd5 = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, LibraryChartRef> ChartsBySha256 = new(StringComparer.OrdinalIgnoreCase);
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

        internal bool MaintenanceHydrationRunning;

        internal int MaintenanceHydrationLastCompletedVersion;

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
        ReloadFileDiff,
        ScoreOnly,
        FullReinitialize,
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
        PlaylistEntriesHydrationDone = 4096,
        LibraryDatabaseLoadDone = 8192,
        LibraryFileEnumerationDone = 16384,
        LibraryFileDiffDone = 32768,
        InstallableMaintenanceDeferredDone = 65536
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

        internal StartupProgressPhase RequestedPhases;

        internal StartupProgressPhase SkippedPhases;

        internal int ScoreHydrationBaselineCompletedVersion;

        internal int ScoreHydrationRequestedBaselineVersion;

        internal int RequiredScoreHydrationCompletedVersion;

        internal int RankingRefreshBaselineCompletedVersion;

        internal int RankingRefreshRequestedBaselineVersion;

        internal int RequiredRankingRefreshCompletedVersion;

        internal int MaintenanceRequestedBaselineVersion;

        internal int InstallableMaintenanceRequestedBaselineVersion;

        internal int ChartDigestBackfillBaselineCompletedVersion;

        internal int ChartInfoBackfillBaselineCompletedVersion;

        internal int ChartInfoHydrationBaselineCompletedVersion;

        internal int PlaylistEntriesHydrationBaselineCompletedVersion;

        internal int LibraryDatabaseLoadBaselineCompletedVersion;

        internal int LibraryFileEnumerationBaselineCompletedVersion;

        internal int LibraryFileDiffBaselineCompletedVersion;

        internal int RequiredPlaylistReferenceVersion;

        internal int RequiredExternalSyncVersion;

        internal int RequiredPlaylistEntriesHydrationCompletedVersion;

        internal int RequiredMaintenanceCompletedVersion;

        internal int RequiredInstallableMaintenanceCompletedVersion;

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

        internal BMSLibrary.LibraryInitializationProgressStage LibraryInitializationProgressStage;

        internal string LibraryInitializationProgressScannerLabel = string.Empty;

        internal int LibraryInitializationProgressTotalCount;

        internal int LibraryInitializationProgressProcessedCount;

        internal string LibraryInitializationProgressCurrentPath = string.Empty;

        internal bool CompletionHideScheduled;

        internal DateTime? LastCompletedAtUtc;
    }

    internal sealed class StartupProgressTestResult
    {
        internal int ExpectedCount { get; set; }

        internal int CompletedCount { get; set; }

        internal int RequestedCount { get; set; }

        internal int SkippedCount { get; set; }

        internal int IgnoredRequestCount { get; set; }

        internal int IgnoredCompleteCount { get; set; }

        internal string Label { get; set; } = string.Empty;

        internal string SubLabel { get; set; } = string.Empty;

        internal bool IsCompleted { get; set; }
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

        internal PlaylistOpenReadinessSnapshot Readiness = new();
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
        internal int TargetCount;

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
        internal readonly object SyncRoot = new();

        /// <summary>
        /// プレイリスト source build を単一実行に制限します。
        /// </summary>
        internal readonly SemaphoreSlim BuildGate = new(1, 1);

        /// <summary>
        /// 現在表示の正本となるプレイリスト行集合です。
        /// </summary>
        internal List<PlaylistDetailSourceRow> SourceRows = [];

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
        internal CancellationTokenSource Cancellation = new();

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

        internal string Lane;

        internal int Priority;

        internal long Version;

        internal Func<Task> Work;
    }

    private sealed class StartupBackgroundTaskMetric
    {
        internal string Name = string.Empty;

        internal string Reason = string.Empty;

        internal string Dependency = string.Empty;

        internal string Lane = string.Empty;

        internal long QueuedCount;

        internal long StartedCount;

        internal long CompletedCount;

        internal long FailedCount;

        internal long TotalElapsedMs;

        internal long LastElapsedMs;

        internal string LastStatus = string.Empty;

        internal string LastDetail = string.Empty;
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
        ChartInfoParseErrorFilter,
        FilterNone = 255
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

    private const UiRefreshChannel StartupDeferredPresentationChannels =
        UiRefreshChannel.LibraryMainView
        | UiRefreshChannel.LibraryFolderTree
        | UiRefreshChannel.PlaylistTree
        | UiRefreshChannel.DuplicateTree;

    private const UiRefreshChannel StartupBasicPresentationChannels =
        UiRefreshChannel.LibraryFolderTree
        | UiRefreshChannel.PlaylistTree;

    private const string StartupUiSuppressFlushReason = "startup_ui_suppress_flush";

    private PlaylistPropertyDialogViewModel _playlistPropertyDialog;

    private bool initializationCompleted;

    public bool IsInitializationCompleted => initializationCompleted;

    private bool hasActiveLibraryProfile;

    public bool HasActiveLibraryProfile => hasActiveLibraryProfile;

    private bool bmsonMigrationApprovedForSession;

    private bool initialSetupCompletionMessagePending;

    private BMSLibrary files;

    private BMSPlaylist tables;

    private LR2Config lr2config;

    private IBMSPlayer bmsPlayer = new InternalBMSAutoPlayerSoundOnly();

    private PropertyChangedEventListener listenerForBMSLibrary;

    private CollectionChangedEventListener listenerForBMSLibraryChartPackagesPendingCollection;

    private CollectionChangedEventListener listenerForBMSLibraryChartPackagesInstalledCollection;

    private PropertyChangedEventListener listenerForBMSPlaylist;

    private CollectionChangedEventListener listenerForBMSPlaylistBMSTablesCollection;

    private PropertyChangedEventListener listenerForBMSPlayer;

    private readonly object lockThis = new();

    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    private readonly object lockCopyFile = new();

    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.MainWindowViewModel");

    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    private int suppressUiUpdateDepth;

    private UiRefreshChannel suppressedUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel pendingUiRefreshMask = UiRefreshChannel.None;

    private UiRefreshChannel deferredStartupPresentationMask = UiRefreshChannel.None;

    private readonly object lockUiSuppression = new();

    private bool deferredPlaylistSummaryRefreshRequested;

    private bool deferredPlaylistSummaryPresentationRefreshRequested;

    private int deferredPlaylistRefRequestedVersion;

    private bool deferredPlaylistRefRunning;

    private readonly object lockDeferredPlaylistRef = new();

    private readonly PlaylistViewState playlistViewState = new();

    private readonly object playlistLibraryIndexSync = new();

    private PlaylistLibraryIndexSnapshot playlistLibraryIndexSnapshot;

    private long playlistLibraryIndexVersion;

    private Task<PlaylistLibraryIndexSnapshot> playlistLibraryIndexPrewarmTask;

    private long playlistLibraryIndexPrewarmVersion;

    private CancellationTokenSource playlistLibraryIndexPrewarmCancellation;

    private const int PlaylistLibraryIndexPrewarmDebounceMs = 500;

    private int deferredExternalSyncRequestedVersion;

    private bool deferredExternalSyncRunning;

    private readonly object lockDeferredExternalSync = new();

    private readonly object lockPlaylistSyncStatuses = new();

    private readonly Dictionary<string, PlaylistSyncRuntimeStatus> playlistSyncStatuses = new(StringComparer.OrdinalIgnoreCase);

    private readonly ExternalPlaylistImportQueue externalPlaylistImportQueue = new();

    private bool deferredLibraryFolderTreeRefreshQueued;

    private readonly object lockDeferredLibraryFolderTreeRefresh = new();

    private readonly object startupBackgroundTaskLock = new();

    private readonly List<StartupBackgroundTaskRequest> startupBackgroundTaskQueue = [];

    private readonly HashSet<string> startupBackgroundTaskCompletedNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, int> startupBackgroundTaskRunningCountByLane = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, StartupBackgroundTaskMetric> startupBackgroundTaskMetrics = new(StringComparer.OrdinalIgnoreCase);

    private bool startupBackgroundTaskSchedulerStarted;

    private int startupBackgroundTaskRunningCount;

    private long startupBackgroundTaskVersion;

    private Stopwatch startupInitializationCompleteStopwatch;

    private bool startupInitializationCompleteLogged;

    private bool startupInitializationCompleteRetryQueued;

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

    private IList _ChartRowsView = new List<object>();

    private cSortParameters _SortParameters;

    private cSortParameters _PlaylistSummarySortParameters;

    private BeMusicSeeker.Models.BMSFile _NowPlayingBMS;

    private int nowPlayingChartRowsViewIndex = -1;

    private Uri _BrowserSource;

    private string _BrowserHtml;

    private int _SelectedIndexChartRowsView;

    private CustomTableColumnSettings _ColumnsSettingsChartRowsView;

    private List<LibraryChartRow> folderSortSourceSnapshot;

    private List<LibraryChartRow> folderSortResultSnapshot;

    private string folderSortColumnName;

    private ListSortDirection? folderSortDirection;

    private readonly NormalLibraryRowCache regularBmsLibraryRowCache;

    private readonly Dictionary<NormalLibrarySortCacheKey, List<LibraryChartRow>> normalLibrarySortCache = [];

    private readonly object normalLibrarySortCacheLock = new();

    private readonly Dictionary<NormalLibrarySortCacheKey, ChartListOrder> virtualNormalLibraryOrderCache = [];

    private readonly Dictionary<VirtualChartSubsetSortCacheKey, ChartListOrder> virtualChartSubsetOrderCache = [];

    private List<ChartListSourceRow> virtualNormalLibrarySourceRowCache;

    private bool virtualNormalLibrarySourceRowCacheAvailable;

    private long virtualNormalLibrarySourceRowCacheSourceGeneration;

    private bool virtualNormalLibrarySourceRowCacheIncludeBmsonRows;

    private int virtualNormalLibrarySourceRowCacheRowCount;

    private readonly object virtualNormalLibraryOrderPrewarmLock = new();

    private Task virtualNormalLibraryOrderPrewarmTask;

    private int virtualNormalLibraryOrderPrewarmRunId;

    private const int StartupVirtualNormalLibraryOrderPrewarmMaxPriority = 1;

    private int chartInfoProjectionVersionCache;

    private int scoreSnapshotProjectionVersionCache;

    private long normalLibrarySourceGeneration;

    private long normalLibrarySortKeyGeneration;

    private viewUpdateMode? lastAppliedMainColumnSettingMode;

    private int pendingRegularBmsRowCachePrunedCount;

    private readonly Dictionary<string, ChartFileTransientState> chartTransientStatesByKey = new(StringComparer.OrdinalIgnoreCase);

    private readonly object mainSummaryFolderCountCacheLock = new();

    private readonly Dictionary<MainViewSummaryCacheKey, int> mainSummaryFolderCountCache = [];

    private readonly HashSet<MainViewSummaryCacheKey> mainSummaryFolderCountRunning = [];

    private int mainSummaryFolderCountRunId;

    private const long ColumnSettingSlowLogThresholdMs = 100L;

    private const int PlaylistBuildCoalescingWindowMs = 50;

    private const long PlaylistOpenSlowLogThresholdMs = 1000L;

    private const long PlaylistScoreProbeSlowLogThresholdMs = 500L;

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

    private ObservableCollection<PlaylistSummaryRow> _PlaylistSummaryView = [];

    private WeakReference<ObservableCollection<PlaylistSummaryRow>> previousPlaylistSummaryViewWeakReference;

    private readonly object lockPlaylistSummaryRowsCache = new();

    private List<PlaylistSummaryRow> playlistSummaryRowsCache = [];

    private bool playlistSummaryRowsCacheValid;

    private readonly Dictionary<string, PlaylistSummaryTableCountCacheEntry> playlistSummaryTableCountCache = new(StringComparer.OrdinalIgnoreCase);

    private bool _IsPlaylistSummaryMode;

    private bool _IsPlaylistDetailViewActive;

    private bool _UseAsyncChartRowsViewBinding = true;

    private string _GridHeaderText = string.Empty;

    private string _GridSummaryText = string.Empty;

    private readonly DropInstallQueueProcessor dropInstallQueueProcessor;

    private bool _IsDropInstallQueueActive;

    private string _DropInstallQueueLabel = string.Empty;

    private string _DropInstallQueueSubLabel = string.Empty;

    private bool _DropInstallQueueCanCancel;

    private int _DropInstallQueuePendingBatchCount;

    private bool _IsPendingEstimateQueueActive;

    private string _PendingEstimateQueueLabel = string.Empty;

    private string _PendingEstimateQueueSubLabel = string.Empty;

    private int _PendingEstimateQueuePendingBatchCount;

    private DropInstallQueueStatusSnapshot latestDropInstallQueueStatus = new();

    private PendingInstallEstimateQueueStatusSnapshot latestPendingEstimateQueueStatus = new();

    private InstallEstimationProgressSnapshot latestInstallEstimationProgress = new();

    private bool _IsInstallPipelineStatusActive;

    private string _InstallPipelineLabel = string.Empty;

    private string _InstallPipelineSubLabel = string.Empty;

    private int _InstallPipelineValue;

    private int _InstallPipelineMaximum = 1;

    private bool _InstallPipelineCanCancel;

    private CancellationTokenSource maintenanceRescanCancellationTokenSource;

    private bool _IsMaintenanceRescanProgressActive;

    private string _MaintenanceRescanLabel = string.Empty;

    private string _MaintenanceRescanSubLabel = string.Empty;

    private double _MaintenanceRescanValue;

    private double _MaintenanceRescanMaximum = 1.0;

    private bool _MaintenanceRescanCanCancel;

    private readonly object playlistSyncProgressLock = new();

    private int playlistSyncProgressActiveOperationCount;

    private bool _IsPlaylistSyncProgressActive;

    private readonly object lockPlaylistReloadCleanup = new();

    private PlaylistReloadCleanupRequest pendingPlaylistReloadCleanup;

    private bool playlistReloadCleanupRunning;

    private long playlistReloadCleanupSeed;

    private string _PlaylistSyncProgressLabel = string.Empty;

    private string _PlaylistSyncProgressSubLabel = string.Empty;

    private double _PlaylistSyncProgressValue;

    private double _PlaylistSyncProgressMaximum;

    private readonly object startupProgressLock = new();

    private StartupProgressState startupProgressState = new();

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

    private readonly ObservableCollection<KeywordSearchSuggestionItem> _KeywordSearchSuggestions = [];

    private readonly ObservableCollection<KeywordSearchSuggestionItem> _PlaylistSummaryKeywordSearchSuggestions = [];

    private readonly List<string> keywordSearchHistory = [];

    private readonly List<string> playlistSummaryKeywordSearchHistory = [];

    private bool _IsKeywordSearchSuggestionPopupOpen;

    private bool _IsPlaylistSummaryKeywordSearchSuggestionPopupOpen;

    private string _KeywordSearchSuggestionHeaderText = string.Empty;

    private string _PlaylistSummaryKeywordSearchSuggestionHeaderText = string.Empty;

    private PlaylistSummaryOwnedFilterType _PlaylistSummaryOwnedFilter = PlaylistSummaryOwnedFilterType.All;

    private NormalLibraryTreeFilter virtualNormalLibraryTreeFilter;

    private readonly DispatcherCollection<string> _sortedBmsParentFolderList = new(DispatcherHelper.UIDispatcher);

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

    private static readonly string scoreRegisterUrl = "https://bms-score-viewer-backend.sayakaisbaka.workers.dev/bms/score/register";

    private static readonly string scoreStatusUrl = "https://bms-score-viewer-backend.sayakaisbaka.workers.dev/bms/score/status?md5=";

    private static readonly string scoreViewUrl = "https://bms-score-viewer.pages.dev/view?md5=";

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
        return operationKind switch
        {
            PlaylistReloadOperationKind.StartupFullReload => "startup_full",
            PlaylistReloadOperationKind.ManualFullReload => "manual_full",
            PlaylistReloadOperationKind.SinglePlaylistReload => "single",
            _ => "none",
        };
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
        var query = GridKeywordSearchQuery.Parse(keywordFilter);
        IReadOnlyList<GridKeywordSearchDiagnostic> diagnostics = query.GetDiagnostics(context);
        if (diagnostics.Count == 0)
        {
            return string.Empty;
        }
        return string.Join(
            Environment.NewLine,
            diagnostics
                .Select(FormatKeywordSearchDiagnostic)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal));
    }

    internal static string BuildKeywordSearchHelpText(GridKeywordSearchContext context)
    {
        string fields = context switch
        {
            GridKeywordSearchContext.PlaylistDetail => BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_playlist_detail,
            GridKeywordSearchContext.PlaylistSummary => BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_playlist_summary,
            _ => BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_bmsfile,
        };
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
        return kind switch
        {
            KeywordSearchSuggestionKind.History => BeMusicSeeker.Properties.Resources.Keyword_search_completion_history_header,
            KeywordSearchSuggestionKind.Value => BeMusicSeeker.Properties.Resources.Keyword_search_completion_playlist_names_header,
            _ => BeMusicSeeker.Properties.Resources.Keyword_search_completion_fields_header,
        };
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
        return [.. (history ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => new KeywordSearchSuggestionItem(KeywordSearchSuggestionKind.History, entry, entry, 0, safeText.Length))];
    }

    private static string FormatKeywordSearchDiagnostic(GridKeywordSearchDiagnostic diagnostic)
    {
        return diagnostic.Kind switch
        {
            GridKeywordSearchDiagnosticKind.UnknownField => string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_unknown_field, diagnostic.Value),
            GridKeywordSearchDiagnosticKind.EmptyFieldTerm => string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_field_term, diagnostic.Value),
            GridKeywordSearchDiagnosticKind.EmptyNegation => BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_negation,
            GridKeywordSearchDiagnosticKind.EmptyOr => BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_or,
            GridKeywordSearchDiagnosticKind.InvalidRegex => string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_invalid_regex, diagnostic.Value),
            _ => string.Empty,
        };
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
        if (rows is IChartListViewMetadata metadata)
        {
            return metadata.RowCount;
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

    private static int CountDistinctFoldersForPlaylistSourceRows(IEnumerable<PlaylistDetailSourceRow> rows)
    {
        if (rows == null)
        {
            return -1;
        }
        return rows
            .Select(row => row?.Folder)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
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
        if (_UseAsyncChartRowsViewBinding != nextUseAsyncBinding)
        {
            _UseAsyncChartRowsViewBinding = nextUseAsyncBinding;
            RaisePropertyChanged("UseAsyncChartRowsViewBinding");
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
        RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
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
        var request = new PlaylistReloadCleanupRequest
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
            TryGetPreviousPlaylistSummaryViewState(out bool oldSummaryAlive, out int oldSummaryRowCount);
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
            var operableWaitStopwatch = Stopwatch.StartNew();
            while (!startupReadyOperableReached && operableWaitStopwatch.ElapsedMilliseconds < 30000)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        if (request.WaitForSummaryRefresh)
        {
            var summaryWaitStopwatch = Stopwatch.StartNew();
            while (Interlocked.Read(ref lastPlaylistSummaryBuildCompletedTimestamp) < request.RequestedAtTimestamp && summaryWaitStopwatch.ElapsedMilliseconds < 10000)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        if (request.WaitForDetailRefresh)
        {
            var detailWaitStopwatch = Stopwatch.StartNew();
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
    /// main view 更新契機を、実際に適用する列設定モードへ解決します。
    /// </summary>
    private static viewUpdateMode ResolveMainColumnSettingMode(viewUpdateMode mode, viewUpdateMode currentTreeMode)
    {
        return mode switch
        {
            viewUpdateMode.TreeViewFilterNotChanged or viewUpdateMode.KeywordFilterUpdated or viewUpdateMode.ModeFilterUpdated or viewUpdateMode.SortUpdated => currentTreeMode,
            _ => mode,
        };
    }

    internal static int ResolveMainColumnSettingModeForTest(int mode, int currentTreeMode)
    {
        return (int)ResolveMainColumnSettingMode((viewUpdateMode)mode, (viewUpdateMode)currentTreeMode);
    }

    internal static bool ShouldReuseMainColumnSettingForTest(int resolvedMode, int? lastAppliedMode, bool targetSettingsReady, bool playlistSummarySettingsReady, bool isInit)
    {
        viewUpdateMode? typedLastAppliedMode = lastAppliedMode.HasValue ? (viewUpdateMode?)((viewUpdateMode)lastAppliedMode.Value) : null;
        return CanReuseMainColumnSetting((viewUpdateMode)resolvedMode, typedLastAppliedMode, targetSettingsReady, playlistSummarySettingsReady, isInit);
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
                return Task.FromResult<PlaylistLibraryIndexSnapshot>(null);
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
                var stopwatch = Stopwatch.StartNew();
                LogPlaylistWorker("playlist_library_index_prewarm started version=" + targetVersion + " reason=" + reason + " source=" + source);
                try
                {
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, prewarmToken).ConfigureAwait(false);
                    }
                    prewarmToken.ThrowIfCancellationRequested();
                    PlaylistLibraryIndexSnapshot snapshot = CreatePlaylistLibraryIndexSnapshot(prewarmToken, targetVersion);
                    LogPlaylistWorker("playlist_library_index_prewarm completed version=" + targetVersion + " chartsByMd5Count=" + snapshot.ChartsByMd5.Count + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " source=" + source);
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
        return string.Equals(reason, "library_charts_changed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason, "library_bmsons_changed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 現在のライブラリから playlist 用 hash index snapshot を構築します。
    /// </summary>
    /// <returns>構築された snapshot。</returns>
    /// <summary>
    /// playlist library index 内で同じ hash に一致した chart representative を選択します。
    /// </summary>
    /// <param name="existing">現在の代表 chart。</param>
    /// <param name="candidate">新しい候補 chart。</param>
    /// <returns>採用する代表 chart。</returns>
    internal static LibraryChartRef ChoosePreferredPlaylistChartRepresentative(LibraryChartRef existing, LibraryChartRef candidate)
    {
        if (existing == null)
        {
            return candidate;
        }
        if (candidate == null)
        {
            return existing;
        }
        return string.Compare(candidate.Path ?? string.Empty, existing.Path ?? string.Empty, StringComparison.OrdinalIgnoreCase) < 0 ? candidate : existing;
    }

    /// <summary>
    /// playlist entry の md5 / sha256 から、現在ライブラリに存在する chart を解決します。
    /// </summary>
    /// <param name="entry">解決対象の playlist entry。</param>
    /// <param name="chartsByMd5">md5 で引ける chart index。</param>
    /// <param name="chartsBySha256">sha256 で引ける chart index。</param>
    /// <returns>一致した chart。見つからない場合は null。</returns>
    internal static LibraryChartRef ResolveChartForPlaylistEntry(
        BMSTableEntry entry,
        IReadOnlyDictionary<string, LibraryChartRef> chartsByMd5,
        IReadOnlyDictionary<string, LibraryChartRef> chartsBySha256)
    {
        if (entry == null)
        {
            return null;
        }
        LibraryChartRef resolvedByMd5 = null;
        if (!string.IsNullOrWhiteSpace(entry.md5) && chartsByMd5 != null)
        {
            chartsByMd5.TryGetValue(entry.md5, out resolvedByMd5);
        }
        if (resolvedByMd5 != null)
        {
            return resolvedByMd5;
        }
        LibraryChartRef resolvedBySha256 = null;
        if (!string.IsNullOrWhiteSpace(entry.sha256) && chartsBySha256 != null)
        {
            chartsBySha256.TryGetValue(entry.sha256, out resolvedBySha256);
        }
        return resolvedBySha256;
    }

    /// <summary>
    /// playlist library index へ chart を md5 / sha256 の両方で登録します。
    /// </summary>
    /// <param name="chartsByMd5">md5 index。</param>
    /// <param name="chartsBySha256">sha256 index。</param>
    /// <param name="chart">登録対象 chart。</param>
    private static void AddPlaylistLibraryIndexChart(
        Dictionary<string, LibraryChartRef> chartsByMd5,
        Dictionary<string, LibraryChartRef> chartsBySha256,
        LibraryChartRef chart)
    {
        if (chart == null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(chart.Md5))
        {
            chartsByMd5[chart.Md5] = ChoosePreferredPlaylistChartRepresentative(
                chartsByMd5.TryGetValue(chart.Md5, out LibraryChartRef existingByMd5) ? existingByMd5 : null,
                chart);
        }
        if (!string.IsNullOrWhiteSpace(chart.Sha256))
        {
            chartsBySha256[chart.Sha256] = ChoosePreferredPlaylistChartRepresentative(
                chartsBySha256.TryGetValue(chart.Sha256, out LibraryChartRef existingBySha256) ? existingBySha256 : null,
                chart);
        }
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

    private static BeMusicSeeker.Models.BMSScore ResolvePlaylistEntryScoreSnapshot(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        LR2SongDBExtended.chart_info entryChartInfo,
        BeMusicSeeker.Models.BMSLibrary.ScoreSnapshot scoreSnapshot,
        IReadOnlyDictionary<string, BeMusicSeeker.Models.BMSScore> scoresByHash,
        IReadOnlyDictionary<string, BeMusicSeeker.Models.BMSScore> scoresBySha256)
    {
        if (entry == null || scoreSnapshot == null)
        {
            return null;
        }
        if (scoreSnapshot.ActiveScoreSource == ActiveScoreSource.Lr2)
        {
            string hash = FirstNonEmpty(resolvedChart?.Md5, entry.md5);
            if (scoresByHash != null && scoresByHash.TryGetValue(hash, out BeMusicSeeker.Models.BMSScore lr2Score))
            {
                return lr2Score;
            }
            return null;
        }
        if (scoreSnapshot.ActiveScoreSource == ActiveScoreSource.Beatoraja && scoresBySha256 != null)
        {
            string sha256 = FirstNonEmpty(resolvedChart?.Sha256, entry.sha256, entryChartInfo?.sha256);
            if (scoresBySha256.TryGetValue(sha256, out BeMusicSeeker.Models.BMSScore beatorajaScore))
            {
                string hash = FirstNonEmpty(resolvedChart?.Md5, entry.md5);
                return string.IsNullOrWhiteSpace(hash)
                    ? beatorajaScore
                    : BmsLibraryIrService.CloneScoreForFileHash(beatorajaScore, hash);
            }
        }
        return null;
    }

    private static ChartFile ResolvePlaylistChartSnapshot(LibraryChartRef chartRef, IDictionary<LibraryChartRef, ChartFile> cache)
    {
        if (chartRef == null)
        {
            return null;
        }
        if (cache != null && cache.TryGetValue(chartRef, out ChartFile cachedChart))
        {
            return cachedChart;
        }
        ChartFile chart = chartRef.ToChartFileIdentity();
        if (cache != null)
        {
            cache[chartRef] = chart;
        }
        return chart;
    }

    internal static BeMusicSeeker.Models.BMSScore ResolvePlaylistEntryScoreSnapshotForTest(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        LR2SongDBExtended.chart_info entryChartInfo,
        BeMusicSeeker.Models.BMSLibrary.ScoreSnapshot scoreSnapshot,
        IReadOnlyDictionary<string, BeMusicSeeker.Models.BMSScore> scoresByHash,
        IReadOnlyDictionary<string, BeMusicSeeker.Models.BMSScore> scoresBySha256)
    {
        return ResolvePlaylistEntryScoreSnapshot(entry, resolvedChart, entryChartInfo, scoreSnapshot, scoresByHash, scoresBySha256);
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        if (candidates == null)
        {
            return string.Empty;
        }
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }
        return string.Empty;
    }

    private PlaylistLibraryIndexSnapshot CreatePlaylistLibraryIndexSnapshot(CancellationToken cancellationToken, long targetVersion)
    {
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        var chartsByMd5 = new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
        var chartsBySha256 = new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
        foreach (BeMusicSeeker.Models.BMSFile file in BMSFiles ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file == null)
            {
                continue;
            }
            AddPlaylistLibraryIndexChart(
                chartsByMd5,
                chartsBySha256,
                LibraryChartRef.FromBmsFile(file));
        }
        foreach (LR2SongDBExtended.bmson_song song in files?.BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (song == null || string.IsNullOrWhiteSpace(song.path))
            {
                continue;
            }
            AddPlaylistLibraryIndexChart(
                chartsByMd5,
                chartsBySha256,
                LibraryChartRef.FromBmsonSong(song));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var newSnapshot = new PlaylistLibraryIndexSnapshot
        {
            Version = targetVersion,
            BuildElapsedMs = stopwatch.ElapsedMilliseconds,
            ChartsByMd5 = chartsByMd5,
            ChartsBySha256 = chartsBySha256
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
        BeMusicSeeker.Models.BMSLibrary.ScoreRuntimeState scoreState = default;
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
            MaintenanceHydrationRunning = files?.MaintenanceHydrationRunning ?? false,
            MaintenanceHydrationLastCompletedVersion = files?.MaintenanceHydrationCompletedVersion ?? 0,
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
        var interaction = new PlaylistOpenInteractionState
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
        LogPlaylistOpen("playlist_open_ready_state requestVersion=" + request.RequestVersion + " startupReadyDataReached=" + readiness.StartupReadyDataReached.ToString().ToLowerInvariant() + " startupReadyUiReached=" + readiness.StartupReadyUiReached.ToString().ToLowerInvariant() + " startupReadyOperableReached=" + readiness.StartupReadyOperableReached.ToString().ToLowerInvariant() + " playlistRefDeferredRunning=" + readiness.PlaylistRefDeferredRunning.ToString().ToLowerInvariant() + " playlistRefDeferredLastCompletedVersion=" + readiness.PlaylistRefDeferredLastCompletedVersion + " maintenanceHydrationRunning=" + readiness.MaintenanceHydrationRunning.ToString().ToLowerInvariant() + " maintenanceHydrationLastCompletedVersion=" + readiness.MaintenanceHydrationLastCompletedVersion + " playlistLibraryIndexState=" + readiness.PlaylistLibraryIndexState + " playlistLibraryIndexBuildMs=" + readiness.PlaylistLibraryIndexBuildMs + " scoreSnapshotReady=" + readiness.ScoreSnapshotReady.ToString().ToLowerInvariant() + " scoreSnapshotVersion=" + readiness.ScoreSnapshotVersion + " scoreHydrationRunning=" + readiness.ScoreHydrationRunning.ToString().ToLowerInvariant() + " scoreHydrationCompletedVersion=" + readiness.ScoreHydrationCompletedVersion + " rankingRefreshRunning=" + readiness.RankingRefreshRunning.ToString().ToLowerInvariant() + " rankingRefreshCompletedVersion=" + readiness.RankingRefreshCompletedVersion);
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
        var stopwatch = Stopwatch.StartNew();
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
            var buildCancellation = new CancellationTokenSource();
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

    private bool TryDeferStartupPresentationRefresh(UiRefreshChannel channel, string reason)
    {
        if (channel == UiRefreshChannel.None)
        {
            return false;
        }
        long operationToken = GetActiveStartupProgressOperationToken();
        if (!ShouldDeferStartupPresentationRefresh(channel, operationToken))
        {
            return false;
        }
        UiRefreshChannel deferredChannel = channel & StartupDeferredPresentationChannels;
        if (deferredChannel == UiRefreshChannel.None)
        {
            return false;
        }
        UiRefreshChannel pendingMask;
        lock (lockUiSuppression)
        {
            deferredStartupPresentationMask |= deferredChannel;
            pendingMask = deferredStartupPresentationMask;
        }
        LogUiSuppression("startup_presentation_deferred reason=" + (reason ?? string.Empty) + " channel=" + deferredChannel + " pending=" + pendingMask);
        return true;
    }

    private bool ShouldDeferStartupPresentationRefresh(UiRefreshChannel channel, long operationToken)
    {
        if ((channel & StartupDeferredPresentationChannels) == 0)
        {
            return false;
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return false;
        }
        bool initializationCompleteLogged;
        lock (startupBackgroundTaskLock)
        {
            initializationCompleteLogged = startupInitializationCompleteLogged;
        }
        if (initializationCompleteLogged)
        {
            return false;
        }
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive && startupProgressState.OperationKind == StartupProgressOperationKind.Startup;
        }
    }

    private UiRefreshChannel DeferStartupPresentationChannels(UiRefreshChannel mask, long operationToken, string reason)
    {
        UiRefreshChannel deferredChannel = GetStartupPresentationDeferredChannels(mask, reason, CanShowStartupBasicLibraryMainView(treeViewFilterTypeSelected));
        if (deferredChannel == UiRefreshChannel.None || !ShouldDeferStartupPresentationRefresh(deferredChannel, operationToken))
        {
            return mask;
        }
        UiRefreshChannel pendingMask;
        lock (lockUiSuppression)
        {
            deferredStartupPresentationMask |= deferredChannel;
            pendingMask = deferredStartupPresentationMask;
        }
        LogUiSuppression("startup_presentation_deferred reason=" + (reason ?? string.Empty) + " channel=" + deferredChannel + " pending=" + pendingMask);
        return mask & ~deferredChannel;
    }

    private static bool CanShowStartupBasicLibraryMainView(viewUpdateMode currentTreeMode)
    {
        return currentTreeMode == viewUpdateMode.FolderFilterSelected
            || currentTreeMode == viewUpdateMode.FullScanAllChartsFilterSelected;
    }

    private static UiRefreshChannel GetStartupBasicPresentationChannels(bool includeLibraryMainView)
    {
        UiRefreshChannel channels = StartupBasicPresentationChannels;
        if (includeLibraryMainView)
        {
            channels |= UiRefreshChannel.LibraryMainView;
        }
        return channels;
    }

    private static UiRefreshChannel GetStartupPresentationDeferredChannels(UiRefreshChannel mask, string reason, bool includeBasicLibraryMainView)
    {
        UiRefreshChannel deferredChannel = mask & StartupDeferredPresentationChannels;
        if (string.Equals(reason, StartupUiSuppressFlushReason, StringComparison.Ordinal))
        {
            deferredChannel &= ~GetStartupBasicPresentationChannels(includeBasicLibraryMainView);
        }
        return deferredChannel;
    }

    internal static bool IsStartupPresentationDeferredForTest(
        viewUpdateMode currentTreeMode,
        bool startupUiSuppressFlush,
        bool libraryMainView,
        bool libraryFolderTree,
        bool playlistTree,
        bool duplicateTree)
    {
        UiRefreshChannel mask = UiRefreshChannel.None;
        if (libraryMainView)
        {
            mask |= UiRefreshChannel.LibraryMainView;
        }
        if (libraryFolderTree)
        {
            mask |= UiRefreshChannel.LibraryFolderTree;
        }
        if (playlistTree)
        {
            mask |= UiRefreshChannel.PlaylistTree;
        }
        if (duplicateTree)
        {
            mask |= UiRefreshChannel.DuplicateTree;
        }
        string reason = startupUiSuppressFlush ? StartupUiSuppressFlushReason : "background_hydration";
        return GetStartupPresentationDeferredChannels(mask, reason, CanShowStartupBasicLibraryMainView(currentTreeMode)) != UiRefreshChannel.None;
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

    internal struct PlaylistSummaryDataRefreshDecision
    {
        public bool InvalidateRowsCache { get; set; }

        public bool InvalidateTableCountCache { get; set; }

        public bool RebuildImmediately { get; set; }

        public bool RequestDeferredRefresh { get; set; }
    }

    private static PlaylistSummaryDataRefreshDecision BuildPlaylistSummaryDataRefreshDecision(bool isPlaylistSummaryMode, bool isUiUpdateSuppressed, bool invalidateTableCountCache)
    {
        return new PlaylistSummaryDataRefreshDecision
        {
            InvalidateRowsCache = true,
            InvalidateTableCountCache = invalidateTableCountCache,
            RebuildImmediately = isPlaylistSummaryMode && !isUiUpdateSuppressed,
            RequestDeferredRefresh = isPlaylistSummaryMode && isUiUpdateSuppressed
        };
    }

    internal static PlaylistSummaryDataRefreshDecision BuildPlaylistSummaryDataRefreshDecisionForTest(bool isPlaylistSummaryMode, bool isUiUpdateSuppressed, bool invalidateTableCountCache)
    {
        return BuildPlaylistSummaryDataRefreshDecision(isPlaylistSummaryMode, isUiUpdateSuppressed, invalidateTableCountCache);
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

    private void InvalidatePlaylistSummaryData(string reason, bool invalidateTableCountCache = false)
    {
        InvalidatePlaylistSummaryRowsCache(invalidateTableCountCache);
    }

    private void SetPlaylistSummaryRowsCache(IEnumerable<PlaylistSummaryRow> rows)
    {
        lock (lockPlaylistSummaryRowsCache)
        {
            playlistSummaryRowsCache = [.. (rows ?? [])];
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
            return [.. playlistSummaryRowsCache];
        }
    }

    private long GetActiveStartupProgressOperationToken()
    {
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive ? startupProgressState.OperationToken : 0L;
        }
    }

    private bool IsStartupProgressOperationTokenCurrent(long operationToken)
    {
        if (operationToken == 0L)
        {
            return true;
        }
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive && startupProgressState.OperationToken == operationToken;
        }
    }

    private void EndUiUpdateSuppression()
    {
        long operationToken = GetActiveStartupProgressOperationToken();
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
            if (!IsStartupProgressOperationTokenCurrent(operationToken))
            {
                LogUiSuppression("ui_suppress flush_skipped_stale token=" + operationToken + " mask=" + uiRefreshChannel);
                return;
            }
            FlushPendingUiRefresh(uiRefreshChannel, operationToken);
        });
    }

    private void TryLogStartupReadyData()
    {
        TryLogStartupReadyData(GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyData(long operationToken)
    {
        if (startupReadyInstallStopwatch == null || startupReadyDataLogged)
        {
            return;
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
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
        TryLogStartupReadyUi(mask, GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyUi(UiRefreshChannel mask, long operationToken)
    {
        if (!IsStartupReadyUiMaskSatisfied(mask))
        {
            return;
        }
        if (startupReadyInstallStopwatch == null || startupReadyUiLogged)
        {
            return;
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        bool flag = (mask & UiRefreshChannel.PlaylistTree) != 0;
        LogUiSuppression("startup_ready_ui elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds + " playlistRefreshed=" + flag.ToString().ToLowerInvariant());
        startupReadyUiLogged = true;
        startupReadyUiReached = true;
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyUi);
    }

    private static bool IsStartupReadyUiMaskSatisfied(UiRefreshChannel mask)
    {
        return (mask & UiRefreshChannel.InstallTree) != 0;
    }

    internal static bool IsStartupReadyUiMaskSatisfiedForTest(bool installTree, bool libraryMainView, bool playlistTree)
    {
        UiRefreshChannel mask = UiRefreshChannel.None;
        if (installTree)
        {
            mask |= UiRefreshChannel.InstallTree;
        }
        if (libraryMainView)
        {
            mask |= UiRefreshChannel.LibraryMainView;
        }
        if (playlistTree)
        {
            mask |= UiRefreshChannel.PlaylistTree;
        }
        return IsStartupReadyUiMaskSatisfied(mask);
    }

    private void TryLogStartupReadyInstall(UiRefreshChannel mask)
    {
        TryLogStartupReadyInstall(mask, GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyInstall(UiRefreshChannel mask, long operationToken)
    {
        if (!IsStartupReadyInstallMaskSatisfied(mask))
        {
            return;
        }
        if (startupReadyInstallStopwatch == null)
        {
            return;
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        LogUiSuppression("startup_ready_install elapsedMs=" + startupReadyInstallStopwatch.ElapsedMilliseconds);
        startupReadyInstallStopwatch = null;
        startupReadyDataLogged = false;
        startupReadyUiLogged = false;
    }

    private static bool IsStartupReadyInstallMaskSatisfied(UiRefreshChannel mask)
    {
        return (mask & UiRefreshChannel.InstallTree) != 0;
    }

    private void TryLogStartupReadyOperable()
    {
        TryLogStartupReadyOperable(GetActiveStartupProgressOperationToken());
    }

    private void TryLogStartupReadyOperable(long operationToken)
    {
        if (startupReadyOperableStopwatch == null)
        {
            return;
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
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
        string normalizedLane = GetStartupBackgroundTaskLane(normalizedName);
        string coalesceKey = normalizedName;
        long version;
        bool shouldStartWorker = false;
        lock (startupBackgroundTaskLock)
        {
            RecordStartupBackgroundTaskQueuedUnsafe(normalizedName, normalizedReason, normalizedDependency, normalizedLane);
            version = ++startupBackgroundTaskVersion;
            StartupBackgroundTaskRequest existing = startupBackgroundTaskQueue.LastOrDefault(item => string.Equals(item.CoalesceKey, coalesceKey, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Reason = normalizedReason;
                existing.Dependency = normalizedDependency;
                existing.Lane = normalizedLane;
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
                    Lane = normalizedLane,
                    CoalesceKey = coalesceKey,
                    Priority = GetStartupBackgroundTaskPriority(normalizedName),
                    Version = version,
                    Work = work
                });
            }
            LogUiSuppression("startup_background_task queue name=" + normalizedName + " version=" + version + " reason=" + normalizedReason + " dependency=" + (normalizedDependency ?? "(none)") + " lane=" + normalizedLane + " priority=" + GetStartupBackgroundTaskPriority(normalizedName));
            shouldStartWorker = startupBackgroundTaskSchedulerStarted;
        }
        if (shouldStartWorker)
        {
            TryStartStartupBackgroundTaskWorkers();
        }
        return true;
    }

    private StartupBackgroundTaskMetric GetOrCreateStartupBackgroundTaskMetricUnsafe(string name)
    {
        string normalizedName = string.IsNullOrWhiteSpace(name) ? "unknown" : name;
        if (!startupBackgroundTaskMetrics.TryGetValue(normalizedName, out StartupBackgroundTaskMetric metric))
        {
            metric = new StartupBackgroundTaskMetric
            {
                Name = normalizedName
            };
            startupBackgroundTaskMetrics[normalizedName] = metric;
        }
        return metric;
    }

    private void RecordStartupBackgroundTaskQueuedUnsafe(string name, string reason, string dependency, string lane)
    {
        StartupBackgroundTaskMetric metric = GetOrCreateStartupBackgroundTaskMetricUnsafe(name);
        metric.QueuedCount++;
        metric.Reason = reason ?? string.Empty;
        metric.Dependency = dependency ?? string.Empty;
        metric.Lane = lane ?? string.Empty;
        metric.LastStatus = "queued";
    }

    private void RecordStartupBackgroundTaskStarted(string name)
    {
        lock (startupBackgroundTaskLock)
        {
            StartupBackgroundTaskMetric metric = GetOrCreateStartupBackgroundTaskMetricUnsafe(name);
            metric.StartedCount++;
            metric.LastStatus = "running";
        }
    }

    private void RecordStartupBackgroundTaskCompleted(string name, string status, long elapsedMs, bool failed, string detail)
    {
        lock (startupBackgroundTaskLock)
        {
            StartupBackgroundTaskMetric metric = GetOrCreateStartupBackgroundTaskMetricUnsafe(name);
            if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                metric.QueuedCount++;
                metric.Reason = detail ?? string.Empty;
                metric.Lane = GetStartupBackgroundTaskLane(name);
                metric.LastStatus = "queued";
                return;
            }
            if (string.Equals(status, "start", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                metric.StartedCount++;
                if (string.IsNullOrWhiteSpace(metric.Lane))
                {
                    metric.Lane = GetStartupBackgroundTaskLane(name);
                }
                metric.LastStatus = "running";
                metric.LastDetail = detail ?? string.Empty;
                return;
            }
            if (failed)
            {
                metric.FailedCount++;
            }
            else
            {
                metric.CompletedCount++;
            }
            metric.LastStatus = string.IsNullOrWhiteSpace(status) ? (failed ? "failed" : "done") : status;
            if (string.IsNullOrWhiteSpace(metric.Lane))
            {
                metric.Lane = GetStartupBackgroundTaskLane(name);
            }
            metric.LastElapsedMs = Math.Max(0L, elapsedMs);
            metric.TotalElapsedMs += Math.Max(0L, elapsedMs);
            metric.LastDetail = detail ?? string.Empty;
        }
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
        if (string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return 55;
        }
        if (string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }
        return 100;
    }

    private static string GetStartupBackgroundTaskLane(string name)
    {
        if (string.Equals(name, "playlist_entries_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "chart_info_hydration", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "maintenance_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return "read_hydration";
        }
        if (string.Equals(name, "playlist_url_completion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "playlist_ref_apply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "external_playlist_sync", StringComparison.OrdinalIgnoreCase))
        {
            return "playlist_followup";
        }
        if (string.Equals(name, "installable_maintenance", StringComparison.OrdinalIgnoreCase))
        {
            return "dependent_maintenance";
        }
        return "default";
    }

    private static int GetStartupBackgroundTaskLaneConcurrency(string lane)
    {
        if (string.Equals(lane, "read_hydration", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        return 1;
    }

    private static int GetStartupBackgroundTaskTotalConcurrency()
    {
        return 3;
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
            shouldStartWorker = startupBackgroundTaskQueue.Count > 0;
        }
        LogUiSuppression("startup_background_task scheduler_start");
        if (shouldStartWorker)
        {
            TryStartStartupBackgroundTaskWorkers();
        }
    }

    private void TryStartStartupBackgroundTaskWorkers()
    {
        while (true)
        {
            StartupBackgroundTaskRequest request = null;
            int laneRunningCount = 0;
            int totalRunningCount = 0;
            lock (startupBackgroundTaskLock)
            {
                if (!startupBackgroundTaskSchedulerStarted
                    || startupBackgroundTaskRunningCount >= GetStartupBackgroundTaskTotalConcurrency())
                {
                    return;
                }
                int index = FindNextStartupBackgroundTaskIndexUnsafe();
                if (index < 0)
                {
                    return;
                }
                request = startupBackgroundTaskQueue[index];
                startupBackgroundTaskQueue.RemoveAt(index);
                startupBackgroundTaskRunningCount++;
                startupBackgroundTaskRunningCountByLane.TryGetValue(request.Lane, out int runningInLane);
                startupBackgroundTaskRunningCountByLane[request.Lane] = runningInLane + 1;
                laneRunningCount = runningInLane + 1;
                totalRunningCount = startupBackgroundTaskRunningCount;
            }
            StartStartupBackgroundTaskWorker(request, laneRunningCount, totalRunningCount);
        }
    }

    private int FindNextStartupBackgroundTaskIndexUnsafe()
    {
        int index = -1;
        int bestPriority = int.MaxValue;
        long bestVersion = long.MaxValue;
        for (int i = 0; i < startupBackgroundTaskQueue.Count; i++)
        {
            StartupBackgroundTaskRequest candidate = startupBackgroundTaskQueue[i];
            if (!AreStartupBackgroundDependenciesCompletedUnsafe(candidate.Dependency)
                || !CanStartStartupBackgroundTaskInLaneUnsafe(candidate.Lane))
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
        return index;
    }

    private bool CanStartStartupBackgroundTaskInLaneUnsafe(string lane)
    {
        string normalizedLane = string.IsNullOrWhiteSpace(lane) ? "default" : lane;
        startupBackgroundTaskRunningCountByLane.TryGetValue(normalizedLane, out int runningCount);
        return runningCount < GetStartupBackgroundTaskLaneConcurrency(normalizedLane);
    }

    private void StartStartupBackgroundTaskWorker(StartupBackgroundTaskRequest request, int laneRunningCount, int totalRunningCount)
    {
        Task.Run(async delegate
        {
            var stopwatch = Stopwatch.StartNew();
            LogUiSuppression("startup_background_task start name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " dependency=" + (request.Dependency ?? "(none)") + " lane=" + request.Lane + " laneRunning=" + laneRunningCount + " totalRunning=" + totalRunningCount);
            RecordStartupBackgroundTaskStarted(request.Name);
            try
            {
                await request.Work().ConfigureAwait(false);
                stopwatch.Stop();
                LogUiSuppression("startup_background_task done name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " lane=" + request.Lane + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                RecordStartupBackgroundTaskCompleted(request.Name, "done", stopwatch.ElapsedMilliseconds, failed: false, detail: "reason=" + request.Reason);
                StartupMemoryPressureService.LogCheckpoint(LogUiSuppression, "startup_background_task", request.Name + "_done");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogUiSuppressionWarning("startup_background_task failed name=" + request.Name + " version=" + request.Version + " reason=" + request.Reason + " lane=" + request.Lane + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + ex.Message);
                RecordStartupBackgroundTaskCompleted(request.Name, "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
                StartupMemoryPressureService.LogCheckpoint(LogUiSuppression, "startup_background_task", request.Name + "_failed");
            }
            finally
            {
                lock (startupBackgroundTaskLock)
                {
                    startupBackgroundTaskCompletedNames.Add(request.Name);
                    startupBackgroundTaskRunningCount = Math.Max(0, startupBackgroundTaskRunningCount - 1);
                    if (!string.IsNullOrWhiteSpace(request.Lane)
                        && startupBackgroundTaskRunningCountByLane.TryGetValue(request.Lane, out int runningInLane))
                    {
                        runningInLane = Math.Max(0, runningInLane - 1);
                        if (runningInLane == 0)
                        {
                            startupBackgroundTaskRunningCountByLane.Remove(request.Lane);
                        }
                        else
                        {
                            startupBackgroundTaskRunningCountByLane[request.Lane] = runningInLane;
                        }
                    }
                }
                TryStartStartupBackgroundTaskWorkers();
            }
        }).Logging("StartupBackgroundTaskScheduler");
    }

    private bool AreStartupBackgroundDependenciesCompletedUnsafe(string dependency)
    {
        if (string.IsNullOrWhiteSpace(dependency))
        {
            return true;
        }
        string[] dependencies = dependency.Split([','], StringSplitOptions.RemoveEmptyEntries);
        foreach (string item in dependencies)
        {
            string dependencyName = item.Trim();
            if (dependencyName.Length > 0 && !startupBackgroundTaskCompletedNames.Contains(dependencyName))
            {
                return false;
            }
        }
        return true;
    }

    private void RefreshLibraryMainViewForCurrentFilter()
    {
        if (Enum.IsDefined(typeof(MaintenanceFilterType), (int)treeViewFilterTypeSelected))
        {
            ExecMaintenanceFilter((MaintenanceFilterType)treeViewFilterTypeSelected, treeViewFilterParameterSelected);
        }
        else
        {
            RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
        }
    }

    private void RefreshLibraryMainViewForDataDependency(MainViewDataDependency dependency, string reason)
    {
        MainViewRefreshDecision decision = BuildMainViewRefreshDecision(
            treeViewFilterTypeSelected,
            virtualNormalLibraryTreeFilter != null,
            KeywordFilter,
            ModeFilter,
            SortParameters?.ColumnsName,
            IsPlaylistDetailViewActive,
            dependency,
            reason);
        LogMainViewBuild("main_view_refresh_decision reason=" + decision.Reason
            + " dependency=" + decision.Dependency
            + " sortDependency=" + decision.SortDependency
            + " action=" + decision.Action
            + " detail=" + decision.Detail
            + " mode=" + treeViewFilterTypeSelected
            + " sortColumn=" + (SortParameters?.ColumnsName ?? "(default_title)")
            + " keywordEmpty=" + string.IsNullOrWhiteSpace(KeywordFilter).ToString().ToLowerInvariant()
            + " modeFilter=" + ModeFilter
            + " folderFilterApplied=" + (virtualNormalLibraryTreeFilter != null).ToString().ToLowerInvariant()
            + " isPlaylistDetailView=" + IsPlaylistDetailViewActive.ToString().ToLowerInvariant());
        if (!decision.ShouldRefresh)
        {
            if (decision.ShouldRefreshDisplay)
            {
                if (!TryRefreshMainViewDisplayForDataDependency(dependency))
                {
                    if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, reason))
                    {
                        return;
                    }
                    RefreshLibraryMainViewForCurrentFilter();
                }
            }
            return;
        }
        if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, reason))
        {
            return;
        }
        RefreshLibraryMainViewForCurrentFilter();
    }

    private bool TryRefreshMainViewDisplayForDataDependency(MainViewDataDependency dependency)
    {
        if (ChartRowsView is not ChartListVirtualView virtualView)
        {
            return false;
        }
        virtualView.ForEachRealizedRow(row => row.RefreshDisplayForDataDependency(dependency));
        base.Messenger.Raise(new InteractionMessage("RefreshMainTableDisplay"));
        return true;
    }

    internal static MainViewRefreshDecision BuildMainViewRefreshDecisionForTest(
        viewUpdateMode currentMode,
        bool folderFilterApplied,
        string keywordFilter,
        ModeFilterType modeFilter,
        string sortColumnName,
        bool isPlaylistDetailView,
        MainViewDataDependency dependency,
        string reason)
    {
        return BuildMainViewRefreshDecision(currentMode, folderFilterApplied, keywordFilter, modeFilter, sortColumnName, isPlaylistDetailView, dependency, reason);
    }

    private static MainViewRefreshDecision BuildMainViewRefreshDecision(
        viewUpdateMode currentMode,
        bool folderFilterApplied,
        string keywordFilter,
        ModeFilterType modeFilter,
        string sortColumnName,
        bool isPlaylistDetailView,
        MainViewDataDependency dependency,
        string reason)
    {
        MainViewDataDependency sortDependency = GetMainViewSortColumnDependency(sortColumnName);
        bool fullNormalLibraryView = currentMode == viewUpdateMode.FolderFilterSelected
            && !folderFilterApplied
            && string.IsNullOrWhiteSpace(keywordFilter)
            && modeFilter == ModeFilterType.All
            && !isPlaylistDetailView;
        if (!fullNormalLibraryView)
        {
            return new MainViewRefreshDecision(MainViewRefreshAction.Refresh, dependency, sortDependency, reason, "not_full_normal_library");
        }
        if (IsMainViewDisplayRefreshEnough(sortDependency, dependency))
        {
            return new MainViewRefreshDecision(MainViewRefreshAction.RefreshDisplay, dependency, sortDependency, reason, "dependency_update_does_not_affect_current_sort_or_filter");
        }
        return new MainViewRefreshDecision(MainViewRefreshAction.Refresh, dependency, sortDependency, reason, "dependency_affects_current_view");
    }

    private static bool IsMainViewDisplayRefreshEnough(MainViewDataDependency sortDependency, MainViewDataDependency changedDependency)
    {
        if (sortDependency == MainViewDataDependency.Unknown || sortDependency == changedDependency)
        {
            return false;
        }
        switch (changedDependency)
        {
            case MainViewDataDependency.ChartInfo:
            case MainViewDataDependency.Score:
            case MainViewDataDependency.Maintenance:
            case MainViewDataDependency.Warning:
                break;
            default:
                return false;
        }
        return sortDependency == MainViewDataDependency.IdentitySortKey
            || sortDependency == MainViewDataDependency.ChartInfo
            || sortDependency == MainViewDataDependency.Score
            || sortDependency == MainViewDataDependency.Maintenance
            || sortDependency == MainViewDataDependency.Warning;
    }

    internal static MainViewDataDependency GetMainViewSortColumnDependencyForTest(string columnName)
    {
        return GetMainViewSortColumnDependency(columnName);
    }

    private static MainViewDataDependency GetMainViewSortColumnDependency(string columnName)
    {
        if (ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata))
        {
            return metadata.Dependency;
        }
        if (string.Equals(columnName, nameof(LibraryChartRow.instl_dst), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.InstallDestinationTitle), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.InstallDestinationArtist), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.RefTablesSymbols), StringComparison.Ordinal))
        {
            return MainViewDataDependency.IdentitySortKey;
        }
        if (IsMainViewScoreSortColumn(columnName))
        {
            return MainViewDataDependency.Score;
        }
        if (IsMainViewChartInfoSortColumn(columnName))
        {
            return MainViewDataDependency.ChartInfo;
        }
        if (IsMainViewMaintenanceSortColumn(columnName))
        {
            return MainViewDataDependency.Maintenance;
        }
        if (IsMainViewWarningSortColumn(columnName))
        {
            return MainViewDataDependency.Warning;
        }
        return MainViewDataDependency.Unknown;
    }

    private static bool IsMainViewScoreSortColumn(string columnName)
    {
        return string.Equals(columnName, nameof(LibraryChartRow.clear), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rank), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rate), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rateDouble), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.score), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.totalnotes), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.maxcombo), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.minbp), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ranking), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rankingNum), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rankingString), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.rankingLastupdate), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.stddevVal), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.scoreDifficulty), StringComparison.Ordinal);
    }

    private static bool IsMainViewChartInfoSortColumn(string columnName)
    {
        return string.Equals(columnName, nameof(LibraryChartRow.ChartLevelSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartDifficultySortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartMainBpmSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartMaxBpmSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartMinBpmSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartDurationSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartJudgeSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartFeatureSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartNotes), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartLongNotes), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartScratchNotes), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartTotalSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartTotalPerNoteSortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartDensitySortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartPeakDensitySortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartEndDensitySortKey), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.ChartSoflanCount), StringComparison.Ordinal);
    }

    private static bool IsMainViewMaintenanceSortColumn(string columnName)
    {
        return string.Equals(columnName, nameof(LibraryChartRow.WAVHealth), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.BGAHealth), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.MovieHealth), StringComparison.Ordinal)
            || string.Equals(columnName, nameof(LibraryChartRow.encoding), StringComparison.Ordinal);
    }

    private static bool IsMainViewWarningSortColumn(string columnName)
    {
        return string.Equals(columnName, nameof(LibraryChartRow.WarningDigestText), StringComparison.Ordinal);
    }

    private void RefreshChartInfoDependentViews()
    {
        UpdateChartInfoProjectionVersionCache();
        BmsonLibraryRowCacheSyncResult bmsonSyncResult = SyncBmsonLibraryRowCache(files?.BmsonSongs);
        MainViewDataDependency libraryDependency = bmsonSyncResult.SourceChanged
            ? MainViewDataDependency.SourceMembership
            : MainViewDataDependency.ChartInfo;
        if (bmsonSyncResult.SortKeyChanged)
        {
            InvalidateNormalLibrarySortKeysForBmsonSync(bmsonSyncResult);
        }
        if (bmsonSyncResult.SourceChanged)
        {
            IncrementNormalLibrarySourceGeneration(ResolveBmsonSourceGenerationReason(bmsonSyncResult, "bmson"));
        }
        ResetRegularDerivedViewCaches();
        if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "chart_info_dependent_views"))
        {
            RequestDeferredPlaylistSummaryRefresh();
        }
        else
        {
            RefreshPlaylistSummaryIfVisible("chart_info_dependent_views", invalidateTableCountCache: true);
            RefreshPlaylistDetailAfterReloadIfVisible();
        }
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        RefreshLibraryMainViewForDataDependency(libraryDependency, "chart_info_dependent_views");
    }

    private void ScheduleDeferredLibraryFolderTreeRefresh()
    {
        ScheduleDeferredLibraryFolderTreeRefresh(GetActiveStartupProgressOperationToken());
    }

    private void ScheduleDeferredLibraryFolderTreeRefresh(long operationToken)
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
                var stopwatch = Stopwatch.StartNew();
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
                        ScheduleDeferredLibraryFolderTreeRefresh(operationToken);
                    }
                    else
                    {
                        TryLogStartupReadyOperable(operationToken);
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
        List<string> sortedParentFolders = [.. source.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
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
        files?.NotifyBMSDirectoriesChanged();
    }

    private void FlushPendingUiRefresh(UiRefreshChannel mask)
    {
        FlushPendingUiRefresh(mask, GetActiveStartupProgressOperationToken());
    }

    private void FlushPendingUiRefresh(UiRefreshChannel mask, long operationToken)
    {
        FlushPendingUiRefresh(mask, operationToken, allowStartupPresentationDefer: true, logReadiness: true);
    }

    private void FlushPendingUiRefresh(UiRefreshChannel mask, long operationToken, bool allowStartupPresentationDefer, bool logReadiness)
    {
        LogUiSuppression("ui_suppress flush mask=" + mask);
        UiRefreshChannel requestedMask = mask;
        if (allowStartupPresentationDefer)
        {
            mask = DeferStartupPresentationChannels(mask, operationToken, "startup_ui_suppress_flush");
        }
        var stopwatchTotal = Stopwatch.StartNew();
        long num = 0L;
        long num2 = 0L;
        long num3 = 0L;
        long num4 = 0L;
        long num5 = 0L;
        bool flag = false;
        if ((mask & UiRefreshChannel.InstallTree) != 0)
        {
            var stopwatch = Stopwatch.StartNew();
            RaisePropertyChanged(() => ChartPackagesInstalled);
            RaisePropertyChanged(() => ChartPackagesPending);
            stopwatch.Stop();
            num = stopwatch.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.PlaylistTree) != 0)
        {
            var stopwatch2 = Stopwatch.StartNew();
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
            var stopwatch4 = Stopwatch.StartNew();
            RaisePropertyChanged(() => DuplicateChartGroups);
            stopwatch4.Stop();
            num4 = stopwatch4.ElapsedMilliseconds;
        }
        if ((mask & UiRefreshChannel.LibraryMainView) != 0)
        {
            var stopwatch5 = Stopwatch.StartNew();
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
        LogUiSuppression("ui_suppress flush_install_tree_ms=" + num + " flush_playlist_tree_ms=" + num2 + " flush_library_folder_tree_ms=" + num3 + " flush_duplicate_tree_ms=" + num4 + " flush_library_main_view_ms=" + num5 + " flush_total_ms=" + stopwatchTotal.ElapsedMilliseconds + " deferred_library_folder_tree=" + flag + " requested_mask=" + requestedMask + " flushed_mask=" + mask);
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
        if (logReadiness)
        {
            TryLogStartupReadyUi(mask, operationToken);
            TryLogStartupReadyInstall(mask, operationToken);
        }
        if (flag)
        {
            ScheduleDeferredLibraryFolderTreeRefresh(operationToken);
        }
        else if (logReadiness)
        {
            TryLogStartupReadyOperable(operationToken);
        }
    }

    private void RunPendingInstallMutation(Action action, IEnumerable<ChartFile> playbackTargetCharts = null, UiRefreshChannel extraMask = UiRefreshChannel.None)
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
            if (playbackTargetCharts != null)
            {
                stopPlayingChartFiles(playbackTargetCharts);
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

    private T RunPendingInstallMutation<T>(Func<T> func, IEnumerable<ChartFile> playbackTargetCharts = null, UiRefreshChannel extraMask = UiRefreshChannel.None)
    {
        if (func == null)
        {
            throw new ArgumentNullException("func");
        }
        if (files == null)
        {
            return default;
        }
        UiRefreshChannel mask = UiRefreshChannel.LibraryMainView | UiRefreshChannel.InstallTree | extraMask;
        lock (lockCopyFile)
        {
            if (playbackTargetCharts != null)
            {
                stopPlayingChartFiles(playbackTargetCharts);
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

    private void HandleChartPackagesInstalledCollectionChanged()
    {
        if (treeViewFilterTypeSelected == viewUpdateMode.NewlyInstalledFolderSelected)
        {
            if (!TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        }
        if (TrySuppress(UiRefreshChannel.InstallTree))
        {
            return;
        }
        RaisePropertyChanged(() => ChartPackagesInstalled);
    }

    private void HandleChartPackagesPendingCollectionChanged()
    {
        if (treeViewFilterTypeSelected == viewUpdateMode.PendingInstallFolderSelected)
        {
            if (!TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        }
        if (TrySuppress(UiRefreshChannel.InstallTree))
        {
            return;
        }
        RaisePropertyChanged(() => ChartPackagesPending);
    }

    private void RebindChartPackagesInstalledCollectionListener()
    {
        if (listenerForBMSLibraryChartPackagesInstalledCollection is IDisposable disposable)
        {
            disposable.Dispose();
        }
        if (files == null || files.ChartPackagesInstalled == null)
        {
            listenerForBMSLibraryChartPackagesInstalledCollection = null;
            return;
        }
        listenerForBMSLibraryChartPackagesInstalledCollection = new CollectionChangedEventListener(files.ChartPackagesInstalled);
        listenerForBMSLibraryChartPackagesInstalledCollection.RegisterHandler(delegate
        {
            HandleChartPackagesInstalledCollectionChanged();
        });
    }

    private void RebindChartPackagesPendingCollectionListener()
    {
        if (listenerForBMSLibraryChartPackagesPendingCollection is IDisposable disposable)
        {
            disposable.Dispose();
        }
        if (files == null || files.ChartPackagesPending == null)
        {
            listenerForBMSLibraryChartPackagesPendingCollection = null;
            return;
        }
        listenerForBMSLibraryChartPackagesPendingCollection = new CollectionChangedEventListener(files.ChartPackagesPending);
        listenerForBMSLibraryChartPackagesPendingCollection.RegisterHandler(delegate
        {
            HandleChartPackagesPendingCollectionChanged();
        });
    }

    private void ScheduleDeferredPlaylistReferenceApply(string reason)
    {
        ScheduleDeferredPlaylistReferenceApply(reason, GetActiveStartupProgressOperationToken());
    }

    private void ScheduleDeferredPlaylistReferenceApply(string reason, long operationToken)
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
        if (IsStartupProgressOperationTokenCurrent(operationToken))
        {
            TrackStartupProgressPlaylistReferenceRequest(reason, version);
            TrackStartupProgressPlaylistEntriesHydrationDirectRequest(version, "playlist_ref_deferred:" + reason);
        }
        LogDeferredPlaylistReference("playlist_ref_deferred queue reason=" + reason + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        void workBody()
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
                    if (IsStartupProgressOperationTokenCurrent(operationToken))
                    {
                        TryCompleteStartupProgressPlaylistEntriesHydration(requestVersion);
                    }
                    List<BMSTable> list = [];
                    tables.AcquireReaderLockBMSTables();
                    try
                    {
                        list = [.. BMSTables.Where(t => t != null)];
                    }
                    finally
                    {
                        tables.FreeReaderLockBMSTables();
                    }
                    LogDeferredPlaylistReference("playlist_ref_deferred run version=" + requestVersion + " tableCount=" + list.Count);
                    files.SynchronizeReferenceBMSTables(list);
                    InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
                    int presentationRequestVersion = requestVersion;
                    DispatcherHelper.UIDispatcher.BeginInvoke((Action)delegate
                    {
                        if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree, "playlist_ref_apply_completed"))
                        {
                            LogDeferredPlaylistReference("playlist_ref_deferred presentation_deferred version=" + presentationRequestVersion);
                            return;
                        }
                        RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
                    });
                    LogDeferredPlaylistReference("playlist_ref_deferred done version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " presentation=queued");
                    deferredPlaylistRefLastCompletedVersion = requestVersion;
                    if (IsStartupProgressOperationTokenCurrent(operationToken))
                    {
                        TryCompleteStartupProgressPlaylistReference(requestVersion);
                    }
                }
                catch (Exception ex)
                {
                    LogDeferredPlaylistReference("playlist_ref_deferred failed version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " message=" + ex.Message);
                    deferredPlaylistRefLastCompletedVersion = requestVersion;
                    if (IsStartupProgressOperationTokenCurrent(operationToken))
                    {
                        TryCompleteStartupProgressPlaylistEntriesHydration(requestVersion);
                        TryCompleteStartupProgressPlaylistReference(requestVersion);
                    }
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
        }
        Task work()
        {
            workBody();
            return Task.CompletedTask;
        }
        if (QueueStartupBackgroundTask("playlist_ref_apply", reason, null, work))
        {
            return;
        }
        Task.Run(workBody).Logging("ScheduleDeferredPlaylistReferenceApply");
    }

    private Action<BMSPlaylist.PlaylistTableUpdateContext> CreatePlaylistReferenceReplaceUpdateCallback()
    {
        return delegate (BMSPlaylist.PlaylistTableUpdateContext updateContext)
        {
            if (updateContext == null || (!updateContext.Updated && !updateContext.ReferenceEntriesChanged) || files == null)
            {
                return;
            }
            files.ReplaceReferenceBMSTable(updateContext.OldTable, updateContext.NewTable, updateContext.OldEntriesSnapshot, updateContext.NewEntriesSnapshot);
            InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
        };
    }

    private static bool ShouldScheduleDeferredPlaylistReferenceApplyAfterExternalSync(Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction)
    {
        return updateCallbackAction == null;
    }

    private void StartDeferredExternalPlaylistSync(string reason, bool fromReloadTables, Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction = null)
    {
        StartDeferredExternalPlaylistSync(reason, fromReloadTables, updateCallbackAction, GetActiveStartupProgressOperationToken());
    }

    private void StartDeferredExternalPlaylistSync(string reason, bool fromReloadTables, Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction, long operationToken)
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
        if (IsStartupProgressOperationTokenCurrent(operationToken))
        {
            TrackStartupProgressExternalSyncRequest(reason, version);
            if (updateCallbackAction != null)
            {
                TrackStartupProgressPlaylistReferenceRequest("DeferredExternalSync:" + reason, version);
            }
        }
        LogDeferredExternalSync("deferred_external_sync queue reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }
        async Task work()
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
                        updateCallbackActions = [updateCallbackAction];
                    }
                    List<BMSTable> list = await tables.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true, updateCallbackActions, delegate (PlaylistSyncAttemptResult result)
                    {
                        UpdatePlaylistSyncRuntimeStatus(result);
                    }, UpdatePlaylistSyncProgressStatus).ConfigureAwait(false);
                    int num = list?.Count ?? 0;
                    tables.QueueBeatorajaBmtExportAll("DeferredExternalSync:" + reason);
                    if (ShouldScheduleDeferredPlaylistReferenceApplyAfterExternalSync(updateCallbackAction))
                    {
                        ScheduleDeferredPlaylistReferenceApply("DeferredExternalSync:" + reason, operationToken);
                    }
                    else if (IsStartupProgressOperationTokenCurrent(operationToken))
                    {
                        TryCompleteStartupProgressPlaylistReference(requestVersion);
                    }
                    RefreshPlaylistSummaryIfVisible("deferred_external_sync", invalidateTableCountCache: true);
                    bool cleanupQueued = QueuePlaylistReloadCleanup(playlistReloadOperationKind, num);
                    LogPlaylistReload("playlist_reload_operation completed operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=" + reason + " tableCount=" + num + " summaryRebuildMs=" + Interlocked.Read(ref lastPlaylistSummaryBuildElapsedMs) + " detailRefreshMs=" + Interlocked.Read(ref lastPlaylistDetailBuildElapsedMs) + " cleanupQueued=" + cleanupQueued.ToString().ToLowerInvariant() + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
                    LogDeferredExternalSync("deferred_external_sync done reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " updatedCount=" + num);
                    if (IsStartupProgressOperationTokenCurrent(operationToken))
                    {
                        if (updateCallbackAction != null)
                        {
                            TryCompleteStartupProgressPlaylistReference(requestVersion);
                        }
                        TryCompleteStartupProgressExternalSync(requestVersion);
                    }
                }
                catch (Exception ex)
                {
                    LogPlaylistReload("playlist_reload_operation failed operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=" + reason + " version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " message=" + ex.Message);
                    LogDeferredExternalSync("deferred_external_sync failed reason=" + reason + " fromReloadTables=" + fromReloadTables.ToString().ToLowerInvariant() + " version=" + requestVersion + " elapsedMs=" + (long)(DateTime.UtcNow - startedAt).TotalMilliseconds + " message=" + ex.Message);
                    if (IsStartupProgressOperationTokenCurrent(operationToken))
                    {
                        TryCompleteStartupProgressExternalSync(requestVersion);
                    }
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
        }
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

    private IEnumerable<ChartFile> ChartFilesNeedResourceFix
    {
        get
        {
            if (files != null)
            {
                return files.ChartFilesNeedResourceFix;
            }
            return null;
        }
    }

    private IEnumerable<ChartFile> ChartFilesNeedResourceFixIgnored
    {
        get
        {
            if (files != null)
            {
                return files.ChartFilesNeedResourceFixIgnored;
            }
            return null;
        }
    }

    public List<DuplicateGroup> DuplicateChartGroups
    {
        get
        {
            if (files != null)
            {
                return files.DuplicateChartGroups;
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

    private IEnumerable<ChartFile> ChartFilesZeroNote
    {
        get
        {
            if (files != null)
            {
                return files.ChartFilesZeroNote;
            }
            return null;
        }
    }

    private IEnumerable<ChartFile> ChartInfoParseFailedChartFiles
    {
        get
        {
            if (files != null)
            {
                return files.ChartInfoParseFailedChartFiles;
            }
            return null;
        }
    }

    public DispatcherCollection<ChartPackage> ChartPackagesInstalled
    {
        get
        {
            if (files != null)
            {
                return files.ChartPackagesInstalled;
            }
            return null;
        }
    }

    public DispatcherCollection<ChartPackage> ChartPackagesPending
    {
        get
        {
            if (files != null)
            {
                return files.ChartPackagesPending;
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
    /// メインリスト上に実際に表示される chart row 群です。
    /// ツリーでのフォルダ選択や、各種フィルタリング（キーワード検索、モード絞り込みなど）による抽出結果が反映されます。
    /// </summary>
    public IList ChartRowsView
    {
        get
        {
            return _ChartRowsView;
        }
        set
        {
            if (_ChartRowsView != value)
            {
                DisposeDisposableRows(_ChartRowsView);
                if (value == null)
                {
                    _ChartRowsView = new List<object>();
                }
                else
                {
                    _ChartRowsView = value;
                }
                RaisePropertyChanged("ChartRowsView");
            }
        }
    }

    private static void DisposeDisposableRows(IEnumerable rows)
    {
        if (rows == null)
        {
            return;
        }
        if (rows is IChartListViewMetadata metadata)
        {
            metadata.DisposeRealizedRows();
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
    /// 通常一覧 / playlist 詳細の chart row view を <see cref="ChartRowsView"/> binding へ差し替えます。
    /// playlist 詳細表示では <see cref="PlaylistViewState.CurrentViewRows"/> を先に更新してから呼び出します。
    /// </summary>
    /// <param name="rows">新しい表示行。</param>
    private void SetChartRowsView(IList rows)
    {
        ChartRowsView = rows;
        UpdateMainGridSummaryText(rows);
    }

    private void SetChartRowsView(IList rows, int distinctFolderCount)
    {
        ChartRowsView = rows;
        UpdateMainGridSummaryText(rows?.Count ?? 0, distinctFolderCount);
    }

    private void UpdateMainGridSummaryText(IList rows)
    {
        if (IsPlaylistSummaryMode)
        {
            return;
        }
        if (rows == null)
        {
            GridSummaryText = string.Empty;
            return;
        }
        if (rows is IChartListViewMetadata metadata)
        {
            UpdateMainGridSummaryText(metadata.RowCount, metadata.DistinctFolderCount);
            return;
        }
        UpdateMainGridSummaryText(rows.Count, CountDistinctFoldersForRows(rows));
    }

    private void UpdateMainGridSummaryText(int rowCount, int distinctFolderCount)
    {
        if (IsPlaylistSummaryMode)
        {
            return;
        }
        GridSummaryText = FormatMainGridSummaryText(rowCount, distinctFolderCount);
    }

    private static string FormatMainGridSummaryText(int rowCount, int distinctFolderCount)
    {
        string text = "[" + rowCount + BeMusicSeeker.Properties.Resources.Num_songs;
        if (distinctFolderCount > 1)
        {
            return text + " / " + distinctFolderCount + BeMusicSeeker.Properties.Resources.Num_folders + "]";
        }
        return text + "]";
    }

    internal static string FormatMainGridSummaryTextForTest(int rowCount, int distinctFolderCount)
    {
        return FormatMainGridSummaryText(rowCount, distinctFolderCount);
    }

    private static int CountDistinctFoldersForRows(IEnumerable rows)
    {
        if (rows == null)
        {
            return -1;
        }
        return rows.Cast<object>()
            .Select(GetMainGridSummaryFolderName)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static string GetMainGridSummaryFolderName(object row)
    {
        if (row is BeMusicSeeker.Models.BMSFile bmsFile)
        {
            return bmsFile.Folder;
        }
        if (row is PlaylistDetailRow playlistDetailRow)
        {
            return playlistDetailRow.Folder;
        }
        if (row is LibraryChartRow libraryChartRow)
        {
            return libraryChartRow.Folder;
        }
        return string.Empty;
    }

    private static int CountDistinctFoldersForSourceRows(IEnumerable<ChartListSourceRow> rows)
    {
        if (rows == null)
        {
            return -1;
        }
        return rows.Select(row => row?.Folder)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static IReadOnlyList<ChartListSourceRow> SelectSourceRowsByOrder(IReadOnlyList<ChartListSourceRow> sourceRows, IReadOnlyList<int> orderedIndexes)
    {
        if (sourceRows == null || orderedIndexes == null)
        {
            return [];
        }
        return [.. orderedIndexes
            .Where(index => index >= 0 && index < sourceRows.Count)
            .Select(index => sourceRows[index])
            .Where(row => row != null)];
    }

    private static int[] ApplyVirtualNormalLibraryFilters(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        Func<ChartListSourceRow, bool> folderFilter,
        GridKeywordSearchQuery keywordQuery,
        ModeFilterType modeFilter,
        out int folderFilteredCount,
        out int keywordFilteredCount,
        out int modeFilteredCount,
        out long folderStageMs,
        out long keywordStageMs,
        out long modeStageMs)
    {
        int[] viewOrderedIndexes = [.. (orderedIndexes ?? []).Where(index => sourceRows != null && index >= 0 && index < sourceRows.Count)];

        var stageStopwatch = Stopwatch.StartNew();
        if (folderFilter != null)
        {
            var folderFilteredIndexes = new List<int>(viewOrderedIndexes.Length);
            foreach (int index in viewOrderedIndexes)
            {
                ChartListSourceRow row = sourceRows[index];
                if (row != null && folderFilter(row))
                {
                    folderFilteredIndexes.Add(index);
                }
            }
            viewOrderedIndexes = [.. folderFilteredIndexes];
        }
        folderFilteredCount = viewOrderedIndexes.Length;
        folderStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        if (keywordQuery != null && keywordQuery.HasTokens)
        {
            viewOrderedIndexes = viewOrderedIndexes
                .AsParallel()
                .AsOrdered()
                .Where(index => keywordQuery.MatchesChartListSourceRow(sourceRows[index]))
                .ToArray();
        }
        keywordFilteredCount = viewOrderedIndexes.Length;
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        if (modeFilter != ModeFilterType.All)
        {
            HashSet<int?> modeValues = CreateModeFilterValueSet(modeFilter);
            viewOrderedIndexes = [.. viewOrderedIndexes
                .Where(index =>
                {
                    ChartListSourceRow row = sourceRows[index];
                    return row != null && modeValues.Contains(row.Mode);
                })];
        }
        modeFilteredCount = viewOrderedIndexes.Length;
        modeStageMs = stageStopwatch.ElapsedMilliseconds;
        return viewOrderedIndexes;
    }

    internal static int[] ApplyVirtualNormalLibraryFiltersForTest(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        Func<ChartListSourceRow, bool> folderFilter,
        GridKeywordSearchQuery keywordQuery,
        ModeFilterType modeFilter,
        out int folderFilteredCount,
        out int keywordFilteredCount,
        out int modeFilteredCount)
    {
        return ApplyVirtualNormalLibraryFilters(
            sourceRows,
            orderedIndexes,
            folderFilter,
            keywordQuery,
            modeFilter,
            out folderFilteredCount,
            out keywordFilteredCount,
            out modeFilteredCount,
            out _,
            out _,
            out _);
    }

    private static int[] ApplyVirtualChartSubsetFilters(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        GridKeywordSearchQuery keywordQuery,
        ModeFilterType modeFilter,
        out int keywordFilteredCount,
        out int modeFilteredCount,
        out long keywordStageMs,
        out long modeStageMs)
    {
        int[] viewOrderedIndexes = [.. (orderedIndexes ?? []).Where(index => sourceRows != null && index >= 0 && index < sourceRows.Count)];

        var stageStopwatch = Stopwatch.StartNew();
        if (keywordQuery != null && keywordQuery.HasTokens)
        {
            viewOrderedIndexes = viewOrderedIndexes
                .AsParallel()
                .AsOrdered()
                .Where(index => keywordQuery.MatchesChartListSourceRow(sourceRows[index]))
                .ToArray();
        }
        keywordFilteredCount = viewOrderedIndexes.Length;
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        if (modeFilter != ModeFilterType.All)
        {
            HashSet<int?> modeValues = CreateModeFilterValueSet(modeFilter);
            viewOrderedIndexes = [.. viewOrderedIndexes
                .Where(index =>
                {
                    ChartListSourceRow row = sourceRows[index];
                    return row != null && modeValues.Contains(row.Mode);
                })];
        }
        modeFilteredCount = viewOrderedIndexes.Length;
        modeStageMs = stageStopwatch.ElapsedMilliseconds;
        return viewOrderedIndexes;
    }

    internal static int[] ApplyVirtualChartSubsetFiltersForTest(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        GridKeywordSearchQuery keywordQuery,
        ModeFilterType modeFilter,
        out int keywordFilteredCount,
        out int modeFilteredCount)
    {
        return ApplyVirtualChartSubsetFilters(
            sourceRows,
            orderedIndexes,
            keywordQuery,
            modeFilter,
            out keywordFilteredCount,
            out modeFilteredCount,
            out _,
            out _);
    }

    private bool TryGetMainSummaryFolderCount(MainViewSummaryCacheKey key, out int distinctFolderCount)
    {
        lock (mainSummaryFolderCountCacheLock)
        {
            return mainSummaryFolderCountCache.TryGetValue(key, out distinctFolderCount);
        }
    }

    private void ScheduleMainSummaryFolderCount(MainViewSummaryCacheKey key, IReadOnlyList<ChartListSourceRow> sourceRows, IList expectedRowsView, string reason)
    {
        if (sourceRows == null)
        {
            return;
        }
        int runId;
        lock (mainSummaryFolderCountCacheLock)
        {
            if (mainSummaryFolderCountCache.ContainsKey(key))
            {
                return;
            }
            if (!mainSummaryFolderCountRunning.Add(key))
            {
                LogMainViewBuild("main_summary_folder_count queued reason=" + (reason ?? string.Empty)
                    + " rowCount=" + key.RowCount
                    + " skipped=already_running");
                return;
            }
            runId = ++mainSummaryFolderCountRunId;
        }
        LogMainViewBuild("main_summary_folder_count queued reason=" + (reason ?? string.Empty)
            + " runId=" + runId
            + " rowCount=" + key.RowCount
            + " cacheHit=False");
        Task.Run(() => RunMainSummaryFolderCount(runId, key, sourceRows, expectedRowsView, reason));
    }

    private void RunMainSummaryFolderCount(int runId, MainViewSummaryCacheKey key, IReadOnlyList<ChartListSourceRow> sourceRows, IList expectedRowsView, string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            LogMainViewBuild("main_summary_folder_count start reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " rowCount=" + key.RowCount);
            int distinctFolderCount = CountDistinctFoldersForSourceRows(sourceRows);
            stopwatch.Stop();
            if (!IsCurrentMainSummaryFolderCountKey(key))
            {
                lock (mainSummaryFolderCountCacheLock)
                {
                    mainSummaryFolderCountRunning.Remove(key);
                }
                LogMainViewBuild("main_summary_folder_count stale_skipped reason=" + (reason ?? string.Empty)
                    + " runId=" + runId
                    + " rowCount=" + key.RowCount
                    + " distinctFolderCount=" + distinctFolderCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                return;
            }
            lock (mainSummaryFolderCountCacheLock)
            {
                mainSummaryFolderCountCache[key] = distinctFolderCount;
                mainSummaryFolderCountRunning.Remove(key);
            }
            LogMainViewBuild("main_summary_folder_count done reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " rowCount=" + key.RowCount
                + " distinctFolderCount=" + distinctFolderCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " cacheHit=False");
            Action reflect = () =>
            {
                if (ReferenceEquals(ChartRowsView, expectedRowsView) && IsCurrentMainSummaryFolderCountKey(key))
                {
                    UpdateMainGridSummaryText(key.RowCount, distinctFolderCount);
                }
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
        catch (Exception ex)
        {
            stopwatch.Stop();
            lock (mainSummaryFolderCountCacheLock)
            {
                mainSummaryFolderCountRunning.Remove(key);
            }
            LogMainViewBuild("main_summary_folder_count failed reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " rowCount=" + key.RowCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + SanitizeStartupBackgroundSummaryValue(ex.Message));
        }
    }

    private bool IsCurrentMainSummaryFolderCountKey(MainViewSummaryCacheKey key)
    {
        lock (normalLibrarySortCacheLock)
        {
            return normalLibrarySourceGeneration == key.SourceGeneration
                && normalLibrarySortKeyGeneration == key.SortKeyGeneration;
        }
    }

    internal static bool IsMainSummaryFolderCountStaleForTest(MainViewSummaryCacheKey key, long currentSourceGeneration, long currentSortKeyGeneration)
    {
        return key.SourceGeneration != currentSourceGeneration || key.SortKeyGeneration != currentSortKeyGeneration;
    }

    private void ClearMainSummaryFolderCountCache()
    {
        lock (mainSummaryFolderCountCacheLock)
        {
            mainSummaryFolderCountCache.Clear();
        }
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
    }

    private void IncrementNormalLibrarySourceGeneration(string reason)
    {
        int cacheCount;
        lock (normalLibrarySortCacheLock)
        {
            normalLibrarySourceGeneration++;
            cacheCount = GetNormalLibraryCacheCountLocked();
            normalLibrarySortCache.Clear();
            ClearVirtualNormalLibraryCachesLocked();
        }
        ClearMainSummaryFolderCountCache();
        LogNormalLibrarySortCacheInvalidation("source", reason, cacheCount);
    }

    internal MainViewOperationSection CurrentMainViewOperationSection => ResolveMainViewOperationSection(treeViewFilterTypeSelected);

    internal ChartOperationSourceScope CurrentMainViewChartOperationSourceScope => ResolveMainViewChartOperationSourceScope(CurrentMainViewOperationSection);

    internal static MainViewOperationSection ResolveMainViewOperationSection(viewUpdateMode mode)
    {
        return mode switch
        {
            viewUpdateMode.PendingInstallFolderSelected => MainViewOperationSection.InstallPending,
            viewUpdateMode.NewlyInstalledFolderSelected => MainViewOperationSection.InstallInstalled,
            viewUpdateMode.PlaylistFilterSelected or viewUpdateMode.PlaylistNotOwnedFilterSelected => MainViewOperationSection.Playlist,
            viewUpdateMode.FullScanAllChartsFilterSelected or viewUpdateMode.FileMissingFilterSelected or viewUpdateMode.FileMissingIgnoredFilterSelected => MainViewOperationSection.FullScanCheck,
            viewUpdateMode.ChartInfoParseErrorFilterSelected => MainViewOperationSection.ChartInfoParseError,
            _ => MainViewOperationSection.Library,
        };
    }

    internal static ChartOperationSourceScope ResolveMainViewChartOperationSourceScope(MainViewOperationSection section)
    {
        return section switch
        {
            MainViewOperationSection.InstallPending => ChartOperationSourceScope.PendingPackage,
            MainViewOperationSection.InstallInstalled => ChartOperationSourceScope.NewlyInstalledPackage,
            _ => ChartOperationSourceScope.Library,
        };
    }

    private void OnNormalLibrarySortKeyChanged(string propertyName)
    {
        int cacheCount;
        bool clearSourceRows = ShouldClearVirtualNormalLibrarySourceRowsForSortKeyChange(propertyName);
        lock (normalLibrarySortCacheLock)
        {
            normalLibrarySortKeyGeneration++;
            cacheCount = GetNormalLibraryCacheCountLocked();
            normalLibrarySortCache.Clear();
            ClearVirtualNormalLibraryOrderCachesLocked();
            if (clearSourceRows)
            {
                ClearVirtualNormalLibrarySourceRowsLocked();
            }
        }
        ClearMainSummaryFolderCountCache();
        LogNormalLibrarySortCacheInvalidation("sort_key", propertyName, cacheCount);
    }

    private void InvalidateNormalLibrarySortKeys(string reason)
    {
        if (string.Equals(reason, NormalLibraryReferenceTablesChangedReason, StringComparison.Ordinal))
        {
            NotifyBmsonPlaylistReferenceDisplayChanged();
        }
        OnNormalLibrarySortKeyChanged(reason ?? string.Empty);
    }

    private void InvalidateNormalLibrarySortKeysForBmsonSync(BmsonLibraryRowCacheSyncResult result, string reason = null)
    {
        if (!result.SortKeyChanged)
        {
            return;
        }

        InvalidateNormalLibrarySortKeys(result.SourceIdentityChanged
            ? NormalLibraryBmsonSourceIdentityChangedReason
            : (string.IsNullOrWhiteSpace(reason) ? NormalLibraryBmsonSortKeyChangedReason : reason));
    }

    private const string NormalLibraryBmsPathChangedReason = "bms_path_changed";

    private const string NormalLibraryBmsTitleChangedReason = "bms_title_changed";

    private const string NormalLibraryBmsonPathChangedReason = "bmson_path_changed";

    private const string NormalLibraryBmsonSortKeyChangedReason = "bmson_sort_key_changed";

    private const string NormalLibraryChartInfoDigestBackfilledReason = "chart_info_digest_backfilled";

    private const string NormalLibraryInstallDestinationChangedReason = "install_destination_changed";

    private const string NormalLibraryReferenceTablesChangedReason = "ref_tables_changed";

    private const string NormalLibraryMaintenanceChangedReason = "maintenance_changed";

    private const string NormalLibraryWarningChangedReason = "warning_changed";

    private static IReadOnlyList<string> GetNormalLibraryPathSortKeyInvalidationReasons(bool hasBmsPathMutation, bool hasBmsonPathMutation)
    {
        var reasons = new List<string>(2);
        if (hasBmsPathMutation)
        {
            reasons.Add(NormalLibraryBmsPathChangedReason);
        }
        if (hasBmsonPathMutation)
        {
            reasons.Add(NormalLibraryBmsonPathChangedReason);
        }
        return reasons;
    }

    internal static IReadOnlyList<string> GetNormalLibraryPathSortKeyInvalidationReasonsForTest(bool hasBmsPathMutation, bool hasBmsonPathMutation)
    {
        return GetNormalLibraryPathSortKeyInvalidationReasons(hasBmsPathMutation, hasBmsonPathMutation);
    }

    internal static IReadOnlyList<string> GetNormalLibrarySortKeyInvalidationReasonsForTest()
    {
        return
        [
            NormalLibraryBmsTitleChangedReason,
            NormalLibraryBmsPathChangedReason,
            NormalLibraryBmsonPathChangedReason,
            NormalLibraryBmsonSourceIdentityChangedReason,
            NormalLibraryBmsonSortKeyChangedReason,
            NormalLibraryChartInfoDigestBackfilledReason,
            NormalLibraryInstallDestinationChangedReason,
            NormalLibraryReferenceTablesChangedReason,
            NormalLibraryMaintenanceChangedReason,
            NormalLibraryWarningChangedReason
        ];
    }

    private const string NormalLibraryBmsonSourceIdentityChangedReason = "bmson_source_identity_changed";

    private void InvalidateNormalLibrarySortKeysAfterPathMutation(bool hasBmsPathMutation, bool hasBmsonPathMutation)
    {
        IReadOnlyList<string> reasons = GetNormalLibraryPathSortKeyInvalidationReasons(hasBmsPathMutation, hasBmsonPathMutation);
        foreach (string reason in reasons)
        {
            if (string.Equals(reason, NormalLibraryBmsPathChangedReason, StringComparison.Ordinal))
            {
                InvalidateNormalLibrarySortKeys(reason);
            }
            else if (string.Equals(reason, NormalLibraryBmsonPathChangedReason, StringComparison.Ordinal))
            {
                SyncBmsonLibraryRowCacheWithoutRebuild(reason);
            }
        }
    }

    private void ClearNormalLibrarySortCache()
    {
        int cacheCount;
        lock (normalLibrarySortCacheLock)
        {
            cacheCount = GetNormalLibraryCacheCountLocked();
            normalLibrarySortCache.Clear();
            ClearVirtualNormalLibraryCachesLocked();
        }
        ClearMainSummaryFolderCountCache();
        LogNormalLibrarySortCacheInvalidation("clear", "explicit", cacheCount);
    }

    private void NotifyBmsonPlaylistReferenceDisplayChanged()
    {
        Action notify = delegate
        {
            if (ChartRowsView is ChartListVirtualView virtualView)
            {
                virtualView.ForEachRealizedRow(RaiseBmsonPlaylistReferenceDisplayChanged);
            }
            else
            {
                foreach (LibraryChartRow row in (ChartRowsView ?? new List<object>()).OfType<LibraryChartRow>())
                {
                    RaiseBmsonPlaylistReferenceDisplayChanged(row);
                }
            }
            foreach (LibraryChartRow row in regularBmsLibraryRowCache?.SnapshotRows() ?? [])
            {
                RaiseBmsonPlaylistReferenceDisplayChanged(row);
            }
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            notify();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(notify);
        }
    }

    private static void RaiseBmsonPlaylistReferenceDisplayChanged(LibraryChartRow row)
    {
        row?.RaisePlaylistReferenceDisplayChanged();
    }

    private int GetNormalLibraryCacheCountLocked()
    {
        return normalLibrarySortCache.Count
            + virtualNormalLibraryOrderCache.Count
            + virtualChartSubsetOrderCache.Count
            + (virtualNormalLibrarySourceRowCacheAvailable ? 1 : 0);
    }

    private void ClearVirtualNormalLibraryCachesLocked()
    {
        ClearVirtualNormalLibraryOrderCachesLocked();
        ClearVirtualNormalLibrarySourceRowsLocked();
    }

    private void ClearVirtualNormalLibraryOrderCachesLocked()
    {
        virtualNormalLibraryOrderCache.Clear();
        virtualChartSubsetOrderCache.Clear();
    }

    private void ClearVirtualNormalLibrarySourceRowsLocked()
    {
        virtualNormalLibrarySourceRowCache = null;
        virtualNormalLibrarySourceRowCacheAvailable = false;
        virtualNormalLibrarySourceRowCacheRowCount = 0;
    }

    private static bool ShouldClearVirtualNormalLibrarySourceRowsForSortKeyChange(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.IndexOf(NormalLibraryBmsPathChangedReason, StringComparison.Ordinal) >= 0
            || reason.IndexOf(NormalLibraryBmsTitleChangedReason, StringComparison.Ordinal) >= 0
            || reason.IndexOf(NormalLibraryBmsonPathChangedReason, StringComparison.Ordinal) >= 0
            || reason.IndexOf(NormalLibraryBmsonSourceIdentityChangedReason, StringComparison.Ordinal) >= 0;
    }

    internal static bool ShouldClearVirtualNormalLibrarySourceRowsForSortKeyChangeForTest(string reason)
    {
        return ShouldClearVirtualNormalLibrarySourceRowsForSortKeyChange(reason);
    }

    private void LogNormalLibrarySortCacheInvalidation(string reason, string detail, int cacheCountBefore)
    {
        LogMainViewBuild("normal_library_sort_cache_invalidate reason=" + (reason ?? string.Empty)
            + " detail=" + (detail ?? string.Empty)
            + " cacheCountBefore=" + cacheCountBefore
            + " sourceGeneration=" + normalLibrarySourceGeneration
            + " sortKeyGeneration=" + normalLibrarySortKeyGeneration);
    }

    private int PruneRegularBmsLibraryRowCacheByBmsFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> currentFiles)
    {
        if (regularBmsLibraryRowCache == null || regularBmsLibraryRowCache.Count == 0)
        {
            return 0;
        }
        int pruned = regularBmsLibraryRowCache.PruneBmsFiles(currentFiles);
        pendingRegularBmsRowCachePrunedCount += pruned;
        return pruned;
    }

    private LibraryChartRow CreateLibraryChartRowWithResourceHealthProjection(ChartFile chart)
    {
        var row = LibraryChartRow.FromChartFile(chart);
        ApplyLibraryChartRowProviders(row);
        return row;
    }

    private LibraryChartRow GetOrCreateRegularBmsLibraryRow(ChartFile chart, LibraryRowCacheBuildStats stats)
    {
        LibraryChartRow row = regularBmsLibraryRowCache.GetOrCreate(chart, stats);
        ApplyLibraryChartRowProviders(row);
        return row;
    }

    private LibraryChartRow GetOrCreateStandardLibraryRow(ChartFile chart, LibraryRowCacheBuildStats stats)
    {
        if (chart == null)
        {
            return null;
        }
        BeMusicSeeker.Models.BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return GetOrCreateRegularBmsLibraryRow(chart, stats);
        }
        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            LibraryChartRow row = regularBmsLibraryRowCache.GetOrCreate(chart, stats) ?? LibraryChartRow.FromBmsonSong(bmsonSong);
            ApplyLibraryChartRowProviders(row);
            return row;
        }

        LibraryChartRow projectedRow = LibraryChartRow.FromChartFile(chart);
        ApplyLibraryChartRowProviders(projectedRow);
        return projectedRow;
    }

    private void ApplyLibraryChartRowProviders(LibraryChartRow row)
    {
        row?.SetChartTransientStateProvider(TryGetSharedChartTransientState);
        row?.SetChartInfoProjectionProvider(ResolveChartInfoForProjection);
        ApplyResourceHealthProjectionProvider(row);
        ApplyPlaylistReferenceDisplayProvider(row);
    }

    private void ApplyLibraryChartRowProviders(IEnumerable<LibraryChartRow> rows)
    {
        foreach (LibraryChartRow row in rows ?? [])
        {
            ApplyLibraryChartRowProviders(row);
        }
    }

    private ChartFileTransientState TryGetSharedChartTransientState(ChartFile chart, bool includeWarningSnapshot)
    {
        string key = GetSharedChartStateKey(chart);
        if (string.IsNullOrWhiteSpace(key)
            || !chartTransientStatesByKey.TryGetValue(key, out ChartFileTransientState state)
            || state == null)
        {
            return ChartFileTransientState.Empty;
        }
        return includeWarningSnapshot ? state : state.WithoutWarnings();
    }

    private void PruneSharedChartTransientStateCache(IEnumerable<ChartFile> currentCharts)
    {
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in currentCharts ?? [])
        {
            AddSharedChartStateKey(currentKeys, GetSharedChartStateKey(chart));
        }
        PruneSharedChartTransientStateCache(currentKeys);
    }

    private void PruneSharedChartTransientStateCache(ISet<string> currentKeys)
    {
        foreach (string key in chartTransientStatesByKey.Keys.ToList())
        {
            if (currentKeys?.Contains(key) != true)
            {
                chartTransientStatesByKey.Remove(key);
            }
        }
    }

    private static string GetSharedChartStateKey(ChartFile chart)
    {
        return ChartFileRuntimeStateKey.Create(chart);
    }

    private void PruneSharedChartTransientStateCacheToCurrentStorageRows(
        IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        if (chartTransientStatesByKey.Count == 0)
        {
            return;
        }
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BeMusicSeeker.Models.BMSFile file in bmsFiles ?? [])
        {
            AddSharedChartStateKey(currentKeys, ChartFileRuntimeStateKey.Create(file));
        }
        foreach (LR2SongDBExtended.bmson_song song in bmsonSongs ?? [])
        {
            AddSharedChartStateKey(currentKeys, ChartFileRuntimeStateKey.Create(song));
        }
        PruneSharedChartTransientStateCache(currentKeys);
    }

    private static void AddSharedChartStateKey(ISet<string> keys, string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            keys?.Add(key);
        }
    }

    private void UpdateSharedChartTransientStates(IEnumerable<ChartFile> charts, bool forceInstallDestinationProjection = false)
    {
        foreach (ChartFile chart in charts ?? [])
        {
            string key = GetSharedChartStateKey(chart);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            ChartFileTransientState state = ChartFileTransientState.FromInstallDestinationState(
                chart,
                includeWarningSnapshot: true,
                forceInstallDestinationProjection: forceInstallDestinationProjection,
                forceWarningProjection: forceInstallDestinationProjection);
            if (state.HasState)
            {
                chartTransientStatesByKey[key] = state;
            }
            else
            {
                chartTransientStatesByKey.Remove(key);
            }
        }
    }

    private void ApplyResourceHealthProjectionProvider(LibraryChartRow row)
    {
        row?.SetResourceHealthProjectionProvider(GetResourceHealthProjectionForRow);
    }

    private void ApplyPlaylistReferenceDisplayProvider(LibraryChartRow row)
    {
        row?.SetPlaylistReferenceDisplayProvider(GetPlaylistReferenceDisplayForRow);
    }

    private PlaylistReferenceDisplay GetPlaylistReferenceDisplayForRow(LibraryChartRow row)
    {
        return GetPlaylistReferenceDisplayForChart(row?.Chart);
    }

    private PlaylistReferenceDisplay GetPlaylistReferenceDisplayForSourceRow(ChartListSourceRow row)
    {
        return row == null
            ? PlaylistReferenceDisplay.Empty
            : files?.GetPlaylistReferenceDisplay(row.Hash, row.Sha256) ?? PlaylistReferenceDisplay.Empty;
    }

    private PlaylistReferenceDisplay GetPlaylistReferenceDisplayForChart(ChartFile chart)
    {
        return files?.GetPlaylistReferenceDisplay(chart) ?? PlaylistReferenceDisplay.Empty;
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoForProjection(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }
        return files?.ResolveChartInfo(chart.Sha256, chart.Md5);
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoForSourceRow(ChartListSourceRow row)
    {
        return row == null ? null : files?.ResolveChartInfo(row.Sha256, row.Hash);
    }

    private int GetChartInfoProjectionVersion()
    {
        return Volatile.Read(ref chartInfoProjectionVersionCache);
    }

    private int GetScoreSnapshotProjectionVersion()
    {
        return Volatile.Read(ref scoreSnapshotProjectionVersionCache);
    }

    private void UpdateChartInfoProjectionVersionCache()
    {
        Volatile.Write(ref chartInfoProjectionVersionCache, files?.ChartInfoIndexVersion ?? 0);
    }

    private void UpdateScoreSnapshotProjectionVersionCache()
    {
        Volatile.Write(ref scoreSnapshotProjectionVersionCache, files?.ScoreSnapshotVersion ?? 0);
    }

    private LibraryChartRow CreateVirtualNormalLibraryRow(ChartListSourceRow sourceRow)
    {
        if (sourceRow == null)
        {
            return null;
        }
        ChartFile chart = sourceRow.Chart;
        var row = regularBmsLibraryRowCache.GetOrCreate(chart, null) ?? LibraryChartRow.FromChartFile(chart);
        ApplyLibraryChartRowProviders(row);
        return row;
    }

    private bool TryApplyVirtualDefaultNormalLibraryView(viewUpdateMode mode, viewUpdateMode requestedMode, object parameter, bool includeBmsonRows, Stopwatch viewBuildStopwatch)
    {
        if (!TryResolveVirtualDefaultNormalLibraryRequest(mode, SortParameters, out string normalizedSortColumn, out ListSortDirection sortDirection, out string fallbackReason))
        {
            LogVirtualNormalLibraryFallback(mode, requestedMode, includeBmsonRows, fallbackReason);
            return false;
        }
        ResetRegularDerivedViewCaches();
        UpdateChartInfoProjectionVersionCache();
        UpdateScoreSnapshotProjectionVersionCache();

        long stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        List<ChartListSourceRow> sourceRows = GetOrCreateVirtualNormalLibrarySourceRows(
            includeBmsonRows,
            out bool sourceRowsCacheHit,
            out long sourceRowsSourceGeneration,
            out long sourceRowsSortKeyGeneration);
        long sourceRowsMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        var keywordQuery = GridKeywordSearchQuery.Parse(KeywordFilter);
        NormalLibraryTreeFilter effectiveTreeFilter = ShouldApplyVirtualNormalLibraryFolderFilter(treeViewFilterTypeSelected) ? virtualNormalLibraryTreeFilter : null;
        Func<ChartListSourceRow, bool> effectiveFolderFilter = effectiveTreeFilter == null ? null : new Func<ChartListSourceRow, bool>(effectiveTreeFilter.Matches);
        string filterIdentity = CreateVirtualNormalLibraryFilterIdentity(
            effectiveTreeFilter?.Identity,
            KeywordFilter,
            ModeFilter,
            GetScoreSnapshotProjectionVersion(),
            GetChartInfoProjectionVersion());
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        ChartListOrder fullOrder = GetOrCreateVirtualNormalLibraryOrder(
            sourceRows,
            normalizedSortColumn,
            sortDirection,
            sourceRowsSourceGeneration,
            sourceRowsSortKeyGeneration,
            useCache: true,
            out bool sortCacheHit,
            out NormalLibrarySortCacheKey sortCacheKey,
            out long orderCacheLookupMs,
            out long orderBuildMs);
        long sortStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;

        int[] viewOrderedIndexes = ApplyVirtualNormalLibraryFilters(
            sourceRows,
            fullOrder.Indexes,
            effectiveFolderFilter,
            keywordQuery,
            ModeFilter,
            out int folderFilteredCount,
            out int keywordFilteredCount,
            out int modeFilteredCount,
            out long folderStageMs,
            out long keywordStageMs,
            out long modeStageMs);

        ChartListOrder order = fullOrder.WithIndexes(viewOrderedIndexes);

        var summaryKey = new MainViewSummaryCacheKey(
            sourceRowsSourceGeneration,
            sourceRowsSortKeyGeneration,
            modeFilteredCount,
            includeBmsonRows,
            filterIdentity);
        bool summaryCacheHit = TryGetMainSummaryFolderCount(summaryKey, out int distinctFolderCount);
        if (!summaryCacheHit)
        {
            distinctFolderCount = -1;
        }

        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        var nextRowsView = new ChartListVirtualView(sourceRows, order, CreateVirtualNormalLibraryRow, distinctFolderCount);
        long prepareSwapMs = 0L;
        if (!ReferenceEquals(ChartRowsView, nextRowsView))
        {
            long prepareStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            base.Messenger.Raise(new InteractionMessage("PrepareMainTableSwap"));
            prepareSwapMs = viewBuildStopwatch.ElapsedMilliseconds - prepareStartMs;
        }
        long columnSettingStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        bool columnSettingReuse = ApplyMainColumnSettingForViewUpdate(mode);
        long columnSettingMs = viewBuildStopwatch.ElapsedMilliseconds - columnSettingStartMs;
        long setViewStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        SetChartRowsView(nextRowsView, distinctFolderCount);
        long setViewMs = viewBuildStopwatch.ElapsedMilliseconds - setViewStartMs;
        long columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        if (!summaryCacheHit)
        {
            ScheduleMainSummaryFolderCount(summaryKey, SelectSourceRowsByOrder(sourceRows, viewOrderedIndexes), nextRowsView, mode.ToString());
        }

        long mainViewBuildRequestId = Interlocked.Increment(ref mainViewBuildRequestIdSeed);
        long mainViewBuildEndTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref lastMainViewBuildRequestId, mainViewBuildRequestId);
        Interlocked.Exchange(ref lastMainViewBuildEndTimestamp, mainViewBuildEndTimestamp);
        Volatile.Write(ref lastMainViewBuildThreadId, Thread.CurrentThread.ManagedThreadId);
        Volatile.Write(ref lastMainViewBuildMode, (int)mode);

        LogMainSortDetail(new LibraryChartSortMetrics(
            order.Count,
            order.ColumnName,
            order.Direction,
            order.PropertyTypeName,
            order.SortProfile,
            order.StringSortKind,
            sortStageMs,
            sortReuse: sortCacheHit,
            sortCacheKey: order.ColumnName,
            sortCacheGeneration: GetSortCacheGenerationForLog(sortCacheKey),
            sortCacheHit: sortCacheHit,
            orderCacheLookupMs: orderCacheLookupMs,
            orderBuildMs: orderBuildMs));

        string sortColumn = SortParameters?.ColumnsName ?? "(default_title)";
        string sortDirectionText = SortParameters?.Direction.ToString() ?? "Ascending";
        string parameterType = parameter?.GetType().Name ?? "(null)";
        LogMainViewBuild("main_view_build mode=" + mode
            + " requestedMode=" + requestedMode
            + " parameterType=" + parameterType
            + " folderMs=" + folderStageMs
            + " keywordMs=" + keywordStageMs
            + " modeMs=" + modeStageMs
            + " sortMs=" + sortStageMs
            + " sortReuse=" + sortCacheHit
            + " sortProfile=" + order.SortProfile + (sortCacheHit ? "_reuse" : string.Empty)
            + " sortEngine=virtual fastSortEnabled=True"
            + " isPlaylistDetailView=False"
            + " sourceRowsMs=" + sourceRowsMs
            + " columnMs=" + columnStageMs
            + " prepareSwapMs=" + prepareSwapMs
            + " columnSettingMs=" + columnSettingMs
            + " setViewMs=" + setViewMs
            + " columnSettingReuse=" + columnSettingReuse
            + " callbackMs=0"
            + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds
            + " folderCount=" + folderFilteredCount
            + " keywordCount=" + keywordFilteredCount
            + " modeCount=" + modeFilteredCount
            + " viewCount=" + nextRowsView.Count
            + " sortColumn=" + sortColumn
            + " sortDirection=" + sortDirectionText
            + " virtual=True"
            + " sourceRows=" + sourceRows.Count
            + " orderedRows=" + order.Count
            + " viewRowsCreated=" + nextRowsView.RealizedRowCount
            + " distinctFolderCount=" + distinctFolderCount
            + " summaryFolderCountReuse=" + summaryCacheHit
            + " sourceRowsReuse=" + sourceRowsCacheHit);
        return true;
    }

    private bool TryApplyVirtualChartSubsetLibraryView(viewUpdateMode mode, viewUpdateMode requestedMode, object parameter, Stopwatch viewBuildStopwatch)
    {
        viewUpdateMode treeMode = treeViewFilterTypeSelected;
        object subsetParameter = GetVirtualChartSubsetParameter(treeMode, parameter);
        if (!IsVirtualChartSubsetRequestModeSupported(mode, treeMode)
            || !TryGetVirtualChartSubsetSourceFiles(
                treeMode,
                subsetParameter,
                out IEnumerable<ChartFile> subsetCharts,
                out IEnumerable<PackageChartEntry> subsetEntries,
                out ChartListSourceProjectionMode subsetProjectionMode,
                out string subsetName))
        {
            return false;
        }
        if (!TryResolveVirtualSortRequest(SortParameters, out string normalizedSortColumn, out ListSortDirection sortDirection))
        {
            LogVirtualChartSubsetFallback(mode, requestedMode, treeMode, "unsupported_sort_column");
            return false;
        }

        ResetRegularDerivedViewCaches();
        UpdateChartInfoProjectionVersionCache();
        UpdateScoreSnapshotProjectionVersionCache();

        long sourceGenerationAtLookup;
        long sortKeyGenerationAtLookup;
        lock (normalLibrarySortCacheLock)
        {
            sourceGenerationAtLookup = normalLibrarySourceGeneration;
            sortKeyGenerationAtLookup = normalLibrarySortKeyGeneration;
        }

        long stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        bool applyResourceHealthProjection = ShouldApplyResourceHealthProjectionForVirtualSubset(treeMode);
        List<ChartListSourceRow> sourceRows = subsetEntries != null
            ? ChartListSourceRow.BuildPackageRows(
                subsetEntries,
                applyResourceHealthProjection ? GetResourceHealthProjectionForSourceRow : null,
                GetPlaylistReferenceDisplayForSourceRow,
                TryGetSharedChartTransientState,
                ResolveChartInfoForProjection,
                ResolveChartInfoForSourceRow,
                GetChartInfoProjectionVersion,
                GetScoreSnapshotProjectionVersion)
            : ChartListSourceRow.BuildStandardLibraryRows(
                subsetCharts,
                subsetProjectionMode,
                applyResourceHealthProjection ? GetResourceHealthProjectionForSourceRow : null,
                GetPlaylistReferenceDisplayForSourceRow,
                TryGetSharedChartTransientState,
                ResolveChartInfoForProjection,
                ResolveChartInfoForSourceRow,
                GetChartInfoProjectionVersion,
                GetScoreSnapshotProjectionVersion,
                ResolveScoreSnapshotForSourceRow);
        long sourceRowsMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        long folderStageMs = sourceRowsMs;
        int folderCount = sourceRows.Count;
        long sourceRowsSignature = ComputeVirtualChartSubsetSourceRowsSignature(sourceRows);

        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        ChartListOrder fullOrder = GetOrCreateVirtualChartSubsetOrder(
            sourceRows,
            normalizedSortColumn,
            sortDirection,
            treeMode,
            subsetName,
            sourceRowsSignature,
            sourceGenerationAtLookup,
            sortKeyGenerationAtLookup,
            out bool sortCacheHit,
            out VirtualChartSubsetSortCacheKey sortCacheKey,
            out long orderCacheLookupMs,
            out long orderBuildMs);
        long sortStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;

        var keywordQuery = GridKeywordSearchQuery.Parse(KeywordFilter);
        int[] viewOrderedIndexes = ApplyVirtualChartSubsetFilters(
            sourceRows,
            fullOrder.Indexes,
            keywordQuery,
            ModeFilter,
            out int keywordCount,
            out int modeCount,
            out long keywordStageMs,
            out long modeStageMs);

        ChartListOrder order = fullOrder.WithIndexes(viewOrderedIndexes);
        IReadOnlyList<ChartListSourceRow> orderedRows = SelectSourceRowsByOrder(sourceRows, viewOrderedIndexes);
        int distinctFolderCount = CountDistinctFoldersForSourceRows(orderedRows);

        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        var nextRowsView = new ChartListVirtualView(
            sourceRows,
            order,
            row => CreateVirtualChartSubsetRow(row, applyResourceHealthProjection),
            distinctFolderCount);
        long prepareSwapMs = 0L;
        if (!ReferenceEquals(ChartRowsView, nextRowsView))
        {
            long prepareStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            base.Messenger.Raise(new InteractionMessage("PrepareMainTableSwap"));
            prepareSwapMs = viewBuildStopwatch.ElapsedMilliseconds - prepareStartMs;
        }
        long columnSettingStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        bool columnSettingReuse = ApplyMainColumnSettingForViewUpdate(mode);
        long columnSettingMs = viewBuildStopwatch.ElapsedMilliseconds - columnSettingStartMs;
        long setViewStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        SetChartRowsView(nextRowsView, distinctFolderCount);
        long setViewMs = viewBuildStopwatch.ElapsedMilliseconds - setViewStartMs;
        long columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        if (applyResourceHealthProjection)
        {
            LogResourceHealthProjection(mode, nextRowsView.Count);
        }

        long mainViewBuildRequestId = Interlocked.Increment(ref mainViewBuildRequestIdSeed);
        long mainViewBuildEndTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref lastMainViewBuildRequestId, mainViewBuildRequestId);
        Interlocked.Exchange(ref lastMainViewBuildEndTimestamp, mainViewBuildEndTimestamp);
        Volatile.Write(ref lastMainViewBuildThreadId, Thread.CurrentThread.ManagedThreadId);
        Volatile.Write(ref lastMainViewBuildMode, (int)mode);

        LogMainSortDetail(new LibraryChartSortMetrics(
            order.Count,
            order.ColumnName,
            order.Direction,
            order.PropertyTypeName,
            order.SortProfile,
            order.StringSortKind,
            sortStageMs,
            sortReuse: sortCacheHit,
            sortCacheKey: order.ColumnName,
            sortCacheGeneration: GetSortCacheGenerationForLog(sortCacheKey),
            sortCacheHit: sortCacheHit,
            orderCacheLookupMs: orderCacheLookupMs,
            orderBuildMs: orderBuildMs));

        string sortColumn = SortParameters?.ColumnsName ?? "(default_title)";
        string sortDirectionText = SortParameters?.Direction.ToString() ?? "Ascending";
        string parameterType = subsetParameter?.GetType().Name ?? "(null)";
        LogMainViewBuild("main_view_build mode=" + mode
            + " requestedMode=" + requestedMode
            + " treeMode=" + treeMode
            + " subset=" + subsetName
            + " parameterType=" + parameterType
            + " folderMs=" + folderStageMs
            + " keywordMs=" + keywordStageMs
            + " modeMs=" + modeStageMs
            + " sortMs=" + sortStageMs
            + " sortReuse=" + sortCacheHit
            + " sortProfile=" + order.SortProfile + (sortCacheHit ? "_reuse" : string.Empty)
            + " sortEngine=virtual fastSortEnabled=True"
            + " isPlaylistDetailView=False"
            + " sourceRowsMs=" + sourceRowsMs
            + " columnMs=" + columnStageMs
            + " prepareSwapMs=" + prepareSwapMs
            + " columnSettingMs=" + columnSettingMs
            + " setViewMs=" + setViewMs
            + " columnSettingReuse=" + columnSettingReuse
            + " callbackMs=0"
            + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds
            + " folderCount=" + folderCount
            + " keywordCount=" + keywordCount
            + " modeCount=" + modeCount
            + " viewCount=" + nextRowsView.Count
            + " sortColumn=" + sortColumn
            + " sortDirection=" + sortDirectionText
            + " virtual=True"
            + " sourceRows=" + sourceRows.Count
            + " orderedRows=" + order.Count
            + " viewRowsCreated=" + nextRowsView.RealizedRowCount
            + " distinctFolderCount=" + distinctFolderCount
            + " sourceRowsSignature=" + sourceRowsSignature);
        return true;
    }

    private object GetVirtualChartSubsetParameter(viewUpdateMode treeMode, object parameter)
    {
        if (treeMode != viewUpdateMode.DuplicateFilterSelected)
        {
            if (treeMode == viewUpdateMode.NewlyInstalledFolderSelected
                || treeMode == viewUpdateMode.PendingInstallFolderSelected)
            {
                return treeViewFilterParameterSelected ?? parameter;
            }
            return parameter;
        }
        return NormalizeDuplicateViewParameter(treeViewFilterParameterSelected ?? parameter);
    }

    private LibraryChartRow CreateVirtualChartSubsetRow(ChartListSourceRow sourceRow, bool applyResourceHealthProjection)
    {
        if (sourceRow == null)
        {
            return null;
        }
        LibraryChartRow row;
        if (sourceRow.PackageEntry != null)
        {
            row = LibraryChartRow.FromPackageChartEntry(sourceRow.PackageEntry);
        }
        else
        {
            ChartFile chart = sourceRow.Chart;
            row = LibraryChartRow.FromChartFile(chart, sourceRow.HideResourceHealthDigestWhenInstallDestinationSet);
        }

        if (applyResourceHealthProjection)
        {
            ApplyResourceHealthProjectionProvider(row);
        }
        row?.SetChartInfoProjectionProvider(ResolveChartInfoForProjection);
        ApplyPlaylistReferenceDisplayProvider(row);
        return row;
    }

    private static List<ChartFile> CreateStandardLibraryChartSnapshot(
        IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        return ChartFileProjection.FromStorageRows(
            bmsFiles,
            bmsonSongs,
            includeWarningSnapshot: false,
            requireBmsonPath: true,
            orderBmsonByPath: true,
            includeResourceReferences: false,
            includeScoreSnapshot: false);
    }

    private static List<ChartFile> CreateStandardLibraryChartIdentitySnapshot(
        IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        return
        [
            .. ChartFileProjection.FromBmsStorageOwnerIdentities(bmsFiles),
            .. (bmsonSongs ?? [])
                .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
                .OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)
                .Select(ChartFileProjection.FromBmsonStorageOwnerIdentity)
                .Where(chart => chart != null)
        ];
    }

    private static List<ChartFile> CreateBmsChartSnapshot(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles)
    {
        return ChartFileProjection.FromBmsFiles(
            bmsFiles,
            includeWarningSnapshot: false,
            includeResourceReferences: false,
            includeScoreSnapshot: true);
    }

    private List<ChartListSourceRow> GetOrCreateVirtualNormalLibrarySourceRows(
        bool includeBmsonRows,
        out bool cacheHit,
        out long sourceGeneration,
        out long sortKeyGeneration)
    {
        int bmsRowCount = CountIfCheap(BMSFiles);
        int bmsonRowCount = includeBmsonRows ? CountIfCheap(files?.BmsonSongs) : 0;
        int expectedRowCount = (bmsRowCount >= 0 && bmsonRowCount >= 0) ? bmsRowCount + bmsonRowCount : -1;
        long sourceGenerationAtLookup;
        long sortKeyGenerationAtLookup;
        lock (normalLibrarySortCacheLock)
        {
            sourceGenerationAtLookup = normalLibrarySourceGeneration;
            sortKeyGenerationAtLookup = normalLibrarySortKeyGeneration;
            if (virtualNormalLibrarySourceRowCacheAvailable
                && virtualNormalLibrarySourceRowCache != null
                && virtualNormalLibrarySourceRowCacheSourceGeneration == sourceGenerationAtLookup
                && virtualNormalLibrarySourceRowCacheIncludeBmsonRows == includeBmsonRows
                && (expectedRowCount < 0 || virtualNormalLibrarySourceRowCacheRowCount == expectedRowCount))
            {
                cacheHit = true;
                sourceGeneration = sourceGenerationAtLookup;
                sortKeyGeneration = sortKeyGenerationAtLookup;
                return virtualNormalLibrarySourceRowCache;
            }
        }

        List<ChartListSourceRow> sourceRows = BuildVirtualNormalLibrarySourceRows(includeBmsonRows, expectedRowCount);
        lock (normalLibrarySortCacheLock)
        {
            if (normalLibrarySourceGeneration == sourceGenerationAtLookup
                && normalLibrarySortKeyGeneration == sortKeyGenerationAtLookup)
            {
                virtualNormalLibrarySourceRowCache = sourceRows;
                virtualNormalLibrarySourceRowCacheAvailable = true;
                virtualNormalLibrarySourceRowCacheSourceGeneration = sourceGenerationAtLookup;
                virtualNormalLibrarySourceRowCacheIncludeBmsonRows = includeBmsonRows;
                virtualNormalLibrarySourceRowCacheRowCount = sourceRows.Count;
            }
        }
        cacheHit = false;
        sourceGeneration = sourceGenerationAtLookup;
        sortKeyGeneration = sortKeyGenerationAtLookup;
        return sourceRows;
    }

    private List<ChartListSourceRow> BuildVirtualNormalLibrarySourceRows(bool includeBmsonRows, int expectedRowCount)
    {
        var sourceRows = expectedRowCount > 0
            ? new List<ChartListSourceRow>(expectedRowCount)
            : [];
        AppendVirtualNormalLibrarySourceRows(BMSFiles, sourceRows);
        if (includeBmsonRows)
        {
            AppendVirtualNormalLibrarySourceRows(files?.BmsonSongs, sourceRows);
        }
        return sourceRows;
    }

    private void AppendVirtualNormalLibrarySourceRows(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, ICollection<ChartListSourceRow> sourceRows)
    {
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = GetResourceHealthProjectionForSourceRow;
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = GetPlaylistReferenceDisplayForSourceRow;
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = TryGetSharedChartTransientState;
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = ResolveChartInfoForProjection;
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = ResolveChartInfoForSourceRow;
        Func<int> chartInfoProjectionVersionProvider = GetChartInfoProjectionVersion;
        Func<int> scoreSnapshotVersionProvider = GetScoreSnapshotProjectionVersion;
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = ResolveScoreSnapshotForSourceRow;
        foreach (BeMusicSeeker.Models.BMSFile file in bmsFiles ?? [])
        {
            if (file == null)
            {
                continue;
            }
            ChartListSourceRow row = ChartListSourceRow.FromBmsStorageOwner(
                file,
                resourceHealthProjectionProvider,
                playlistReferenceDisplayProvider,
                chartTransientStateProvider,
                chartInfoProjectionProvider,
                chartInfoRowProjectionProvider,
                chartInfoProjectionVersionProvider,
                scoreSnapshotVersionProvider,
                scoreSnapshotProjectionProvider);
            if (row != null)
            {
                sourceRows.Add(row);
            }
        }
    }

    private void AppendVirtualNormalLibrarySourceRows(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs, ICollection<ChartListSourceRow> sourceRows)
    {
        Func<ChartListSourceRow, ResourceHealthWarningProjection> resourceHealthProjectionProvider = GetResourceHealthProjectionForSourceRow;
        Func<ChartListSourceRow, PlaylistReferenceDisplay> playlistReferenceDisplayProvider = GetPlaylistReferenceDisplayForSourceRow;
        Func<ChartFile, bool, ChartFileTransientState> chartTransientStateProvider = TryGetSharedChartTransientState;
        Func<ChartFile, LR2SongDBExtended.chart_info> chartInfoProjectionProvider = ResolveChartInfoForProjection;
        Func<ChartListSourceRow, LR2SongDBExtended.chart_info> chartInfoRowProjectionProvider = ResolveChartInfoForSourceRow;
        Func<int> chartInfoProjectionVersionProvider = GetChartInfoProjectionVersion;
        Func<int> scoreSnapshotVersionProvider = GetScoreSnapshotProjectionVersion;
        Func<ChartListSourceRow, ChartScoreSnapshot> scoreSnapshotProjectionProvider = ResolveScoreSnapshotForSourceRow;
        foreach (LR2SongDBExtended.bmson_song song in (bmsonSongs ?? []).Where(song => song != null && !string.IsNullOrWhiteSpace(song.path)).OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase))
        {
            ChartListSourceRow row = ChartListSourceRow.FromBmsonStorageOwner(
                song,
                resourceHealthProjectionProvider,
                playlistReferenceDisplayProvider,
                chartTransientStateProvider,
                chartInfoProjectionProvider,
                chartInfoRowProjectionProvider,
                chartInfoProjectionVersionProvider,
                scoreSnapshotVersionProvider,
                scoreSnapshotProjectionProvider);
            if (row != null)
            {
                sourceRows.Add(row);
            }
        }
    }

    private ChartListOrder GetOrCreateVirtualNormalLibraryOrder(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        string columnName,
        ListSortDirection direction,
        long sourceGeneration,
        long sortKeyGeneration,
        bool useCache,
        out bool cacheHit,
        out NormalLibrarySortCacheKey cacheKey,
        out long orderCacheLookupMs,
        out long orderBuildMs)
    {
        var lookupStopwatch = Stopwatch.StartNew();
        int rowCount = sourceRows?.Count ?? 0;
        if (!ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out string normalizedColumnName))
        {
            throw new ArgumentException("Unsupported virtual normal library sort column.", nameof(columnName));
        }

        if (useCache)
        {
            lock (normalLibrarySortCacheLock)
            {
                cacheKey = CreateNormalLibrarySortCacheKey(
                    sourceGeneration,
                    sortKeyGeneration,
                    normalizedColumnName,
                    direction,
                    rowCount);
                if (virtualNormalLibraryOrderCache.TryGetValue(cacheKey, out ChartListOrder cachedOrder)
                    && cachedOrder != null)
                {
                    lookupStopwatch.Stop();
                    cacheHit = true;
                    orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
                    orderBuildMs = 0L;
                    return cachedOrder;
                }
            }
        }
        else
        {
            cacheKey = default;
        }
        lookupStopwatch.Stop();
        var buildStopwatch = Stopwatch.StartNew();
        if (!ChartListOrder.TryCreate(sourceRows, normalizedColumnName, direction, out ChartListOrder order))
        {
            throw new ArgumentException("Unsupported virtual normal library sort column.", nameof(columnName));
        }
        buildStopwatch.Stop();
        if (useCache)
        {
            lock (normalLibrarySortCacheLock)
            {
                if (normalLibrarySourceGeneration == sourceGeneration
                    && normalLibrarySortKeyGeneration == sortKeyGeneration)
                {
                    virtualNormalLibraryOrderCache[cacheKey] = order;
                }
            }
        }
        cacheHit = false;
        orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
        orderBuildMs = buildStopwatch.ElapsedMilliseconds;
        return order;
    }

    private ChartListOrder GetOrCreateVirtualChartSubsetOrder(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        string columnName,
        ListSortDirection direction,
        viewUpdateMode treeMode,
        string subsetName,
        long sourceRowsSignature,
        long sourceGeneration,
        long sortKeyGeneration,
        out bool cacheHit,
        out VirtualChartSubsetSortCacheKey cacheKey,
        out long orderCacheLookupMs,
        out long orderBuildMs)
    {
        var lookupStopwatch = Stopwatch.StartNew();
        int rowCount = sourceRows?.Count ?? 0;
        if (!ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out string normalizedColumnName))
        {
            throw new ArgumentException("Unsupported virtual chart subset sort column.", nameof(columnName));
        }

        cacheKey = CreateVirtualChartSubsetSortCacheKey(
            sourceGeneration,
            sortKeyGeneration,
            treeMode,
            subsetName,
            sourceRowsSignature,
            normalizedColumnName,
            direction,
            rowCount);
        lock (normalLibrarySortCacheLock)
        {
            if (virtualChartSubsetOrderCache.TryGetValue(cacheKey, out ChartListOrder cachedOrder)
                && cachedOrder != null)
            {
                lookupStopwatch.Stop();
                cacheHit = true;
                orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
                orderBuildMs = 0L;
                return cachedOrder;
            }
        }

        lookupStopwatch.Stop();
        var buildStopwatch = Stopwatch.StartNew();
        if (!ChartListOrder.TryCreate(sourceRows, normalizedColumnName, direction, out ChartListOrder order))
        {
            throw new ArgumentException("Unsupported virtual chart subset sort column.", nameof(columnName));
        }
        buildStopwatch.Stop();
        lock (normalLibrarySortCacheLock)
        {
            if (normalLibrarySourceGeneration == sourceGeneration
                && normalLibrarySortKeyGeneration == sortKeyGeneration)
            {
                virtualChartSubsetOrderCache[cacheKey] = order;
            }
        }

        cacheHit = false;
        orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
        orderBuildMs = buildStopwatch.ElapsedMilliseconds;
        return order;
    }

    private NormalLibrarySortCacheKey CreateNormalLibrarySortCacheKey(
        long sourceGeneration,
        long sortKeyGeneration,
        string columnName,
        ListSortDirection direction,
        int rowCount)
    {
        ResolveVirtualSortDependencyGenerations(
            columnName,
            out int scoreGeneration,
            out int chartInfoGeneration,
            out int maintenanceGeneration);
        return new NormalLibrarySortCacheKey(
            sourceGeneration,
            sortKeyGeneration,
            scoreGeneration,
            chartInfoGeneration,
            maintenanceGeneration,
            columnName,
            direction,
            rowCount);
    }

    private VirtualChartSubsetSortCacheKey CreateVirtualChartSubsetSortCacheKey(
        long sourceGeneration,
        long sortKeyGeneration,
        viewUpdateMode treeMode,
        string subsetName,
        long sourceRowsSignature,
        string columnName,
        ListSortDirection direction,
        int rowCount)
    {
        ResolveVirtualSortDependencyGenerations(
            columnName,
            out int scoreGeneration,
            out int chartInfoGeneration,
            out int maintenanceGeneration);
        return new VirtualChartSubsetSortCacheKey(
            sourceGeneration,
            sortKeyGeneration,
            scoreGeneration,
            chartInfoGeneration,
            maintenanceGeneration,
            (int)treeMode,
            subsetName,
            sourceRowsSignature,
            columnName,
            direction,
            rowCount);
    }

    private void ResolveVirtualSortDependencyGenerations(string columnName, out int scoreGeneration, out int chartInfoGeneration, out int maintenanceGeneration)
    {
        scoreGeneration = 0;
        chartInfoGeneration = 0;
        maintenanceGeneration = 0;
        if (ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata))
        {
            switch (metadata.Dependency)
            {
                case MainViewDataDependency.Score:
                    scoreGeneration = files?.ScoreSnapshotVersion ?? 0;
                    break;
                case MainViewDataDependency.ChartInfo:
                    chartInfoGeneration = files?.ChartInfoIndexVersion ?? 0;
                    break;
                case MainViewDataDependency.Maintenance:
                    maintenanceGeneration = files?.MaintenanceHydrationCompletedVersion ?? 0;
                    break;
            }
        }
    }

    private static long GetSortCacheGenerationForLog(NormalLibrarySortCacheKey key)
    {
        if (key.ScoreGeneration != 0)
        {
            return key.ScoreGeneration;
        }
        if (key.ChartInfoGeneration != 0)
        {
            return key.ChartInfoGeneration;
        }
        if (key.MaintenanceGeneration != 0)
        {
            return key.MaintenanceGeneration;
        }
        return key.SortKeyGeneration;
    }

    private static long GetSortCacheGenerationForLog(VirtualChartSubsetSortCacheKey key)
    {
        if (key.ScoreGeneration != 0)
        {
            return key.ScoreGeneration;
        }
        if (key.ChartInfoGeneration != 0)
        {
            return key.ChartInfoGeneration;
        }
        if (key.MaintenanceGeneration != 0)
        {
            return key.MaintenanceGeneration;
        }
        return key.SortKeyGeneration;
    }

    internal static long ComputeVirtualChartSubsetSourceRowsSignatureForTest(IReadOnlyList<ChartListSourceRow> sourceRows)
    {
        return ComputeVirtualChartSubsetSourceRowsSignature(sourceRows);
    }

    private static long ComputeVirtualChartSubsetSourceRowsSignature(IReadOnlyList<ChartListSourceRow> sourceRows)
    {
        unchecked
        {
            long hash = 17L;
            hash = (hash * 397L) ^ (sourceRows?.Count ?? 0);
            if (sourceRows == null)
            {
                return hash;
            }

            foreach (ChartListSourceRow row in sourceRows)
            {
                hash = (hash * 397L) ^ (row?.Kind == ChartFileKind.Bms ? 1 : 2);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Path ?? string.Empty);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Hash ?? string.Empty);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Sha256 ?? string.Empty);
                hash = (hash * 397L) ^ (row?.PackageEntry?.ProjectionVersion ?? 0);
            }
            return hash;
        }
    }

    private IReadOnlyList<VirtualNormalLibrarySortDescriptor> CreateDefaultVirtualNormalLibrarySortPrewarmDescriptors()
    {
        return CreateVirtualNormalLibrarySortPrewarmDescriptors(StartupVirtualNormalLibraryOrderPrewarmMaxPriority);
    }

    private static IReadOnlyList<VirtualNormalLibrarySortDescriptor> CreateVirtualNormalLibrarySortPrewarmDescriptors(int maxPrewarmPriority)
    {
        return [.. ChartListOrder.GetVirtualSortColumnMetadata()
            .Select((column, index) => new { Column = column, Index = index })
            .Where(item => item.Column.PrewarmPriority > 0)
            .Where(item => item.Column.PrewarmPriority <= maxPrewarmPriority)
            .OrderBy(item => item.Column.PrewarmPriority)
            .ThenBy(item => GetVirtualNormalLibraryPrewarmOrder(item.Column.NormalizedColumnName))
            .ThenBy(item => item.Index)
            .SelectMany(item => new[]
            {
                new VirtualNormalLibrarySortDescriptor(item.Column.NormalizedColumnName, ListSortDirection.Ascending, item.Column.PrewarmPriority),
                new VirtualNormalLibrarySortDescriptor(item.Column.NormalizedColumnName, ListSortDirection.Descending, item.Column.PrewarmPriority)
            })];
    }

    private static int GetVirtualNormalLibraryPrewarmOrder(string columnName)
    {
        return columnName switch
        {
            nameof(LibraryChartRow.Title) => 0,
            nameof(LibraryChartRow.Folder) => 1,
            nameof(LibraryChartRow.path) => 2,
            nameof(LibraryChartRow.Artist) => 3,
            nameof(LibraryChartRow.clear) => 0,
            nameof(LibraryChartRow.rateDouble) => 1,
            nameof(LibraryChartRow.minbp) => 2,
            nameof(LibraryChartRow.ChartJudgeSortKey) => 3,
            nameof(LibraryChartRow.ChartNotes) => 4,
            nameof(LibraryChartRow.ChartLongNotes) => 5,
            nameof(LibraryChartRow.ChartScratchNotes) => 6,
            nameof(LibraryChartRow.ChartMainBpmSortKey) => 7,
            nameof(LibraryChartRow.ChartMinBpmSortKey) => 8,
            nameof(LibraryChartRow.ChartMaxBpmSortKey) => 9,
            nameof(LibraryChartRow.ChartSoflanCount) => 10,
            nameof(LibraryChartRow.ChartTotalSortKey) => 11,
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey) => 12,
            nameof(LibraryChartRow.ChartDurationSortKey) => 13,
            nameof(LibraryChartRow.ChartDensitySortKey) => 14,
            nameof(LibraryChartRow.ChartPeakDensitySortKey) => 15,
            nameof(LibraryChartRow.ChartEndDensitySortKey) => 16,
            _ => int.MaxValue,
        };
    }

    internal static IReadOnlyList<VirtualNormalLibrarySortDescriptor> CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest()
    {
        return CreateVirtualNormalLibrarySortPrewarmDescriptors(StartupVirtualNormalLibraryOrderPrewarmMaxPriority);
    }

    internal static IReadOnlyList<VirtualNormalLibrarySortDescriptor> CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest(CustomTableColumnSettings settings)
    {
        _ = settings;
        return CreateDefaultVirtualNormalLibrarySortPrewarmDescriptorsForTest();
    }

    internal static int ResolveVirtualNormalLibraryOrderPrewarmDegreeForTest(int descriptorCount)
    {
        return ResolveVirtualNormalLibraryOrderPrewarmDegree(descriptorCount);
    }

    private static int ResolveVirtualNormalLibraryOrderPrewarmDegree(int descriptorCount)
    {
        if (descriptorCount <= 1)
        {
            return 1;
        }
        int processorDegree = Math.Max(1, Environment.ProcessorCount - 1);
        return Math.Max(1, Math.Min(Math.Min(processorDegree, 4), descriptorCount));
    }

    internal static bool IsVirtualNormalLibraryPrewarmStaleForTest(long expectedSourceGeneration, long expectedSortKeyGeneration, long currentSourceGeneration, long currentSortKeyGeneration)
    {
        return IsVirtualNormalLibraryGenerationStale(expectedSourceGeneration, expectedSortKeyGeneration, currentSourceGeneration, currentSortKeyGeneration);
    }

    private bool IsCurrentVirtualNormalLibraryGeneration(long sourceGeneration, long sortKeyGeneration)
    {
        lock (normalLibrarySortCacheLock)
        {
            return !IsVirtualNormalLibraryGenerationStale(sourceGeneration, sortKeyGeneration, normalLibrarySourceGeneration, normalLibrarySortKeyGeneration);
        }
    }

    private static bool IsVirtualNormalLibraryGenerationStale(long expectedSourceGeneration, long expectedSortKeyGeneration, long currentSourceGeneration, long currentSortKeyGeneration)
    {
        return expectedSourceGeneration != currentSourceGeneration || expectedSortKeyGeneration != currentSortKeyGeneration;
    }

    private void ScheduleVirtualNormalLibraryOrderPrewarm(string reason)
    {
        IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors = CreateDefaultVirtualNormalLibrarySortPrewarmDescriptors();
        int degree = ResolveVirtualNormalLibraryOrderPrewarmDegree(descriptors.Count);
        Task runningTask;
        int runId;
        lock (virtualNormalLibraryOrderPrewarmLock)
        {
            runningTask = virtualNormalLibraryOrderPrewarmTask;
            if (runningTask != null && !runningTask.IsCompleted)
            {
                LogMainViewBuild("virtual_order_prewarm queued reason=" + (reason ?? string.Empty)
                    + " descriptorCount=" + descriptors.Count
                    + " degree=" + degree
                    + " priority1=" + CountPrewarmDescriptorsByPriority(descriptors, 1)
                    + " priority2=" + CountPrewarmDescriptorsByPriority(descriptors, 2)
                    + " priority3=" + CountPrewarmDescriptorsByPriority(descriptors, 3)
                    + " skipped=already_running");
                return;
            }
            runId = ++virtualNormalLibraryOrderPrewarmRunId;
            LogMainViewBuild("virtual_order_prewarm queued reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " descriptorCount=" + descriptors.Count
                + " degree=" + degree
                + " priority1=" + CountPrewarmDescriptorsByPriority(descriptors, 1)
                + " priority2=" + CountPrewarmDescriptorsByPriority(descriptors, 2)
                + " priority3=" + CountPrewarmDescriptorsByPriority(descriptors, 3));
            virtualNormalLibraryOrderPrewarmTask = Task.Run(() => RunVirtualNormalLibraryOrderPrewarm(runId, reason, descriptors));
        }
    }

    private static int CountPrewarmDescriptorsByPriority(IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors, int priority)
    {
        return descriptors?.Count(descriptor => descriptor.PrewarmPriority == priority) ?? 0;
    }

    private void RunVirtualNormalLibraryOrderPrewarm(int runId, string reason, IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors)
    {
        var stopwatch = Stopwatch.StartNew();
        int cacheHitCount = 0;
        int builtCount = 0;
        int descriptorCount = descriptors?.Count ?? 0;
        int degree = ResolveVirtualNormalLibraryOrderPrewarmDegree(descriptorCount);
        int priority1Count = CountPrewarmDescriptorsByPriority(descriptors, 1);
        int priority2Count = CountPrewarmDescriptorsByPriority(descriptors, 2);
        int priority3Count = CountPrewarmDescriptorsByPriority(descriptors, 3);
        bool sourceRowsCacheHit = false;
        int rowCount = 0;
        long sourceGeneration = 0L;
        long sortKeyGeneration = 0L;
        int staleDetected = 0;
        try
        {
            LogMainViewBuild("virtual_order_prewarm start reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " descriptorCount=" + descriptorCount
                + " degree=" + degree
                + " priority1=" + priority1Count
                + " priority2=" + priority2Count
                + " priority3=" + priority3Count);
            bool includeBmsonRows = ShouldIncludeBmsonLibraryRowsInMainView(viewUpdateMode.FolderFilterSelected, viewUpdateMode.FolderFilterSelected);
            List<ChartListSourceRow> sourceRows = GetOrCreateVirtualNormalLibrarySourceRows(
                includeBmsonRows,
                out sourceRowsCacheHit,
                out sourceGeneration,
                out sortKeyGeneration);
            rowCount = sourceRows?.Count ?? 0;
            if (!IsCurrentVirtualNormalLibraryGeneration(sourceGeneration, sortKeyGeneration))
            {
                stopwatch.Stop();
                LogMainViewBuild("virtual_order_prewarm stale_skipped reason=" + (reason ?? string.Empty)
                    + " runId=" + runId
                    + " descriptorCount=" + descriptorCount
                    + " degree=" + degree
                    + " completedDescriptors=0"
                    + " rowCount=" + rowCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                return;
            }
            Parallel.ForEach(
                descriptors ?? [],
                new ParallelOptions { MaxDegreeOfParallelism = degree },
                (descriptor, loopState) =>
                {
                    if (!IsCurrentVirtualNormalLibraryGeneration(sourceGeneration, sortKeyGeneration))
                    {
                        Interlocked.Exchange(ref staleDetected, 1);
                        loopState.Stop();
                        return;
                    }
                    _ = GetOrCreateVirtualNormalLibraryOrder(
                        sourceRows,
                        descriptor.ColumnName,
                        descriptor.Direction,
                        sourceGeneration,
                        sortKeyGeneration,
                        useCache: true,
                        out bool cacheHit,
                        out _,
                        out _,
                        out _);
                    if (cacheHit)
                    {
                        Interlocked.Increment(ref cacheHitCount);
                    }
                    else
                    {
                        Interlocked.Increment(ref builtCount);
                    }
                });
            stopwatch.Stop();
            if (Volatile.Read(ref staleDetected) != 0 || !IsCurrentVirtualNormalLibraryGeneration(sourceGeneration, sortKeyGeneration))
            {
                LogMainViewBuild("virtual_order_prewarm stale_skipped reason=" + (reason ?? string.Empty)
                    + " runId=" + runId
                    + " descriptorCount=" + descriptorCount
                    + " degree=" + degree
                    + " completedDescriptors=" + (cacheHitCount + builtCount)
                    + " rowCount=" + rowCount
                    + " cacheHit=" + cacheHitCount
                    + " built=" + builtCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                return;
            }
            LogMainViewBuild("virtual_order_prewarm done reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " descriptorCount=" + descriptorCount
                + " degree=" + degree
                + " priority1=" + priority1Count
                + " priority2=" + priority2Count
                + " priority3=" + priority3Count
                + " rowCount=" + rowCount
                + " sourceRowsReuse=" + sourceRowsCacheHit
                + " cacheHit=" + cacheHitCount
                + " built=" + builtCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogMainViewBuild("virtual_order_prewarm failed reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " descriptorCount=" + descriptorCount
                + " degree=" + degree
                + " rowCount=" + rowCount
                + " cacheHit=" + cacheHitCount
                + " built=" + builtCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + SanitizeStartupBackgroundSummaryValue(ex.Message));
        }
    }

    private void LogVirtualNormalLibraryFallback(viewUpdateMode mode, viewUpdateMode requestedMode, bool includeBmsonRows, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)
            || !ShouldLogVirtualNormalLibraryFallback(mode, treeViewFilterTypeSelected))
        {
            return;
        }
        LogMainViewBuild("main_view_virtual_fallback reason=" + reason
            + " mode=" + mode
            + " requestedMode=" + requestedMode
            + " treeMode=" + treeViewFilterTypeSelected
            + " sortColumn=" + (SortParameters?.ColumnsName ?? "(default_title)")
            + " sortDirection=" + (SortParameters?.Direction.ToString() ?? "Ascending")
            + " folderFilterApplied=" + (virtualNormalLibraryTreeFilter != null)
            + " keywordLength=" + (KeywordFilter?.Length ?? 0)
            + " modeFilter=" + ModeFilter
            + " includeBmsonRows=" + includeBmsonRows);
    }

    private static bool ShouldLogVirtualNormalLibraryFallback(viewUpdateMode mode, viewUpdateMode currentTreeMode)
    {
        return IsVirtualNormalLibraryRequestModeSupported(mode, currentTreeMode)
            && !IsPlaylistTreeActive(mode, currentTreeMode);
    }

    private void LogVirtualChartSubsetFallback(viewUpdateMode mode, viewUpdateMode requestedMode, viewUpdateMode treeMode, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)
            || !IsVirtualChartSubsetRequestModeSupported(mode, treeMode))
        {
            return;
        }
        LogMainViewBuild("main_view_virtual_fallback reason=" + reason
            + " scope=bms_file_subset"
            + " mode=" + mode
            + " requestedMode=" + requestedMode
            + " treeMode=" + treeMode
            + " sortColumn=" + (SortParameters?.ColumnsName ?? "(default_title)")
            + " sortDirection=" + (SortParameters?.Direction.ToString() ?? "Ascending")
            + " keywordLength=" + (KeywordFilter?.Length ?? 0)
            + " modeFilter=" + ModeFilter);
    }

    private static bool TryResolveVirtualSortRequest(
        cSortParameters sortParameters,
        out string normalizedSortColumn,
        out ListSortDirection sortDirection)
    {
        sortDirection = ListSortDirection.Ascending;
        if (sortParameters == null)
        {
            normalizedSortColumn = nameof(LibraryChartRow.Title);
            return true;
        }
        if (!ChartListOrder.TryNormalizeVirtualSortColumn(sortParameters.ColumnsName, out normalizedSortColumn))
        {
            return false;
        }
        sortDirection = sortParameters.Direction;
        return true;
    }

    private bool TryResolveVirtualDefaultNormalLibraryRequest(
        viewUpdateMode mode,
        cSortParameters sortParameters,
        out string normalizedSortColumn,
        out ListSortDirection sortDirection,
        out string fallbackReason)
    {
        normalizedSortColumn = string.Empty;
        sortDirection = ListSortDirection.Ascending;
        fallbackReason = string.Empty;
        if (!IsVirtualNormalLibraryTreeModeSupported(treeViewFilterTypeSelected))
        {
            fallbackReason = "unsupported_tree_mode";
            return false;
        }
        if (!IsVirtualNormalLibraryRequestModeSupported(mode, treeViewFilterTypeSelected))
        {
            fallbackReason = "unsupported_mode";
            return false;
        }
        if (IsPlaylistTreeActive(mode, treeViewFilterTypeSelected))
        {
            fallbackReason = "playlist_detail";
            return false;
        }
        if (sortParameters == null)
        {
            normalizedSortColumn = nameof(LibraryChartRow.Title);
            return true;
        }
        if (!TryResolveVirtualSortRequest(sortParameters, out normalizedSortColumn, out sortDirection))
        {
            fallbackReason = "unsupported_sort_column";
            return false;
        }
        return true;
    }

    internal static bool IsVirtualNormalLibraryModeSupportedForTest(int mode)
    {
        return IsVirtualNormalLibraryModeSupported((viewUpdateMode)mode);
    }

    internal static bool IsVirtualNormalLibraryTreeModeSupportedForTest(int mode)
    {
        return IsVirtualNormalLibraryTreeModeSupported((viewUpdateMode)mode);
    }

    internal static bool IsVirtualNormalLibraryRequestModeSupportedForTest(int mode, int treeMode)
    {
        return IsVirtualNormalLibraryRequestModeSupported((viewUpdateMode)mode, (viewUpdateMode)treeMode);
    }

    internal static bool ShouldApplyVirtualNormalLibraryFolderFilterForTest(int treeMode)
    {
        return ShouldApplyVirtualNormalLibraryFolderFilter((viewUpdateMode)treeMode);
    }

    internal static Func<ChartListSourceRow, bool> CreateVirtualNormalLibraryFolderFilterForTest(FolderFilterType type, string filterKey, out string identity)
    {
        var filter = NormalLibraryTreeFilter.Create(type, filterKey);
        identity = filter?.Identity;
        return filter == null ? null : new Func<ChartListSourceRow, bool>(filter.Matches);
    }

    internal static bool IsVirtualChartSubsetTreeModeSupportedForTest(int mode)
    {
        return IsVirtualChartSubsetTreeModeSupported((viewUpdateMode)mode);
    }

    internal static bool IsVirtualChartSubsetRequestModeSupportedForTest(int mode, int treeMode)
    {
        return IsVirtualChartSubsetRequestModeSupported((viewUpdateMode)mode, (viewUpdateMode)treeMode);
    }

    internal static bool ShouldApplyResourceHealthProjectionForVirtualSubsetForTest(int mode)
    {
        return ShouldApplyResourceHealthProjectionForVirtualSubset((viewUpdateMode)mode);
    }

    internal static LibraryChartRow CreateLibraryChartRowFromPackageEntryForTest(PackageChartEntry entry)
    {
        return LibraryChartRow.FromPackageChartEntry(entry);
    }

    internal static LibraryChartRow CreateVirtualChartSubsetRowForTest(ChartListSourceRow sourceRow)
    {
        return new MainWindowViewModel().CreateVirtualChartSubsetRow(sourceRow, applyResourceHealthProjection: false);
    }

    private static bool IsVirtualNormalLibraryModeSupported(viewUpdateMode mode)
    {
        return mode == viewUpdateMode.TreeViewFilterNotChanged
            || mode == viewUpdateMode.FolderFilterSelected
            || mode == viewUpdateMode.FullScanAllChartsFilterSelected
            || mode == viewUpdateMode.KeywordFilterUpdated
            || mode == viewUpdateMode.ModeFilterUpdated
            || mode == viewUpdateMode.SortUpdated;
    }

    private static bool IsVirtualNormalLibraryRequestModeSupported(viewUpdateMode mode, viewUpdateMode treeMode)
    {
        return IsVirtualNormalLibraryTreeModeSupported(treeMode)
            && (mode == treeMode
                || mode == viewUpdateMode.TreeViewFilterNotChanged
                || mode == viewUpdateMode.KeywordFilterUpdated
                || mode == viewUpdateMode.ModeFilterUpdated
                || mode == viewUpdateMode.SortUpdated);
    }

    private static bool IsVirtualNormalLibraryTreeModeSupported(viewUpdateMode mode)
    {
        return mode == viewUpdateMode.FolderFilterSelected
            || mode == viewUpdateMode.FullScanAllChartsFilterSelected;
    }

    private static bool ShouldApplyVirtualNormalLibraryFolderFilter(viewUpdateMode treeMode)
    {
        return treeMode != viewUpdateMode.FullScanAllChartsFilterSelected;
    }

    private static bool IsVirtualChartSubsetRequestModeSupported(viewUpdateMode mode, viewUpdateMode treeMode)
    {
        return IsVirtualChartSubsetTreeModeSupported(treeMode)
            && (mode == treeMode
                || mode == viewUpdateMode.TreeViewFilterNotChanged
                || mode == viewUpdateMode.KeywordFilterUpdated
                || mode == viewUpdateMode.ModeFilterUpdated
                || mode == viewUpdateMode.SortUpdated);
    }

    private static bool IsVirtualChartSubsetTreeModeSupported(viewUpdateMode mode)
    {
        return mode == viewUpdateMode.FileMissingFilterSelected
            || mode == viewUpdateMode.FileMissingIgnoredFilterSelected
            || mode == viewUpdateMode.DuplicateFilterSelected
            || mode == viewUpdateMode.GarbledFilterSelected
            || mode == viewUpdateMode.GarbleFixedFilterSelected
            || mode == viewUpdateMode.UnregisteredFilterSelected
            || mode == viewUpdateMode.ZeroNoteFilterSelected
            || mode == viewUpdateMode.ChartInfoParseErrorFilterSelected
            || mode == viewUpdateMode.NewlyInstalledFolderSelected
            || mode == viewUpdateMode.PendingInstallFolderSelected;
    }

    private static bool IsVirtualPackageSubsetTreeMode(viewUpdateMode mode)
    {
        return mode == viewUpdateMode.NewlyInstalledFolderSelected
            || mode == viewUpdateMode.PendingInstallFolderSelected;
    }

    private bool TryGetVirtualChartSubsetSourceFiles(
        viewUpdateMode treeMode,
        object parameter,
        out IEnumerable<ChartFile> sourceCharts,
        out IEnumerable<PackageChartEntry> sourceEntries,
        out ChartListSourceProjectionMode sourceProjectionMode,
        out string subsetName)
    {
        sourceCharts = null;
        sourceEntries = null;
        sourceProjectionMode = ChartListSourceProjectionMode.PreserveSourceProjection;
        switch (treeMode)
        {
            case viewUpdateMode.FileMissingFilterSelected:
                sourceCharts = ChartFilesNeedResourceFix;
                subsetName = "file_missing";
                return true;
            case viewUpdateMode.FileMissingIgnoredFilterSelected:
                sourceCharts = ChartFilesNeedResourceFixIgnored;
                subsetName = "file_missing_ignored";
                return true;
            case viewUpdateMode.DuplicateFilterSelected:
                return TryGetVirtualDuplicateSourceCharts(parameter, out sourceCharts, out subsetName);
            case viewUpdateMode.GarbledFilterSelected:
                sourceCharts = CreateBmsChartSnapshot(BMSFilesGarbled);
                sourceProjectionMode = ChartListSourceProjectionMode.OwnerBacked;
                subsetName = "garbled";
                return true;
            case viewUpdateMode.GarbleFixedFilterSelected:
                sourceCharts = CreateBmsChartSnapshot(BMSFilesGarbleFixed);
                sourceProjectionMode = ChartListSourceProjectionMode.OwnerBacked;
                subsetName = "garble_fixed";
                return true;
            case viewUpdateMode.UnregisteredFilterSelected:
                sourceCharts = CreateBmsChartSnapshot(BMSFilesUnregistered);
                sourceProjectionMode = ChartListSourceProjectionMode.OwnerBacked;
                subsetName = "unregistered";
                return true;
            case viewUpdateMode.ZeroNoteFilterSelected:
                sourceCharts = ChartFilesZeroNote;
                sourceProjectionMode = ChartListSourceProjectionMode.OwnerBacked;
                subsetName = "zero_note";
                return true;
            case viewUpdateMode.ChartInfoParseErrorFilterSelected:
                sourceCharts = ChartInfoParseFailedChartFiles;
                subsetName = "chart_info_parse_error";
                return true;
            case viewUpdateMode.NewlyInstalledFolderSelected:
                return TryGetVirtualPackageSourceFiles(
                    ChartPackagesInstalled,
                    parameter,
                    "newly_installed_all",
                    "newly_installed_package",
                    out sourceCharts,
                    out sourceEntries,
                    out subsetName);
            case viewUpdateMode.PendingInstallFolderSelected:
                return TryGetVirtualPackageSourceFiles(
                    ChartPackagesPending,
                    parameter,
                    "pending_install_all",
                    "pending_install_package",
                    out sourceCharts,
                    out sourceEntries,
                    out subsetName);
            default:
                sourceCharts = null;
                sourceEntries = null;
                subsetName = string.Empty;
                return false;
        }
    }

    private bool TryGetVirtualPackageSourceFiles(
        IEnumerable<ChartPackage> packages,
        object parameter,
        string allSubsetName,
        string packageSubsetName,
        out IEnumerable<ChartFile> sourceCharts,
        out IEnumerable<PackageChartEntry> sourceEntries,
        out string subsetName)
    {
        if (packages == null)
        {
            sourceCharts = [];
            sourceEntries = [];
            subsetName = allSubsetName;
            return true;
        }
        if (parameter is ChartPackage package)
        {
            PackageChartSourceSnapshot packageSnapshot = CreatePackageChartSourceSnapshot(package);
            sourceCharts = [];
            sourceEntries = packageSnapshot.Entries;
            subsetName = packageSubsetName;
            return true;
        }

        PackageChartSourceSnapshot snapshot = CreatePackageChartSourceSnapshot(packages);
        sourceCharts = [];
        sourceEntries = snapshot.Entries;
        subsetName = allSubsetName;
        return true;
    }

    /// <summary>
    /// Creates a virtual-list source snapshot from package chart entries without materializing adapterless bmson rows.
    /// </summary>
    /// <param name="packages">Packages whose chart entries should be exposed to the virtual list.</param>
    /// <returns>A chart source snapshot for the virtual list.</returns>
    internal static PackageChartSourceSnapshot CreatePackageChartSourceSnapshot(IEnumerable<ChartPackage> packages)
    {
        PackageChartSourceSnapshot snapshot = null;
        RetryHelper.RetryIfError(delegate
        {
            snapshot = CreatePackageChartSourceSnapshotCore(packages);
        }, delegate (Exception ex)
        {
            ExceptionDispatchInfo.Capture(ex).Throw();
        }, delegate
        {
            Thread.Sleep(100);
        }, 100u);
        return snapshot ?? new PackageChartSourceSnapshot([]);
    }

    private static PackageChartSourceSnapshot CreatePackageChartSourceSnapshot(ChartPackage package)
    {
        return CreatePackageChartSourceSnapshotFromEntries(package?.ChartEntries);
    }

    private static PackageChartSourceSnapshot CreatePackageChartSourceSnapshotCore(IEnumerable<ChartPackage> packages)
    {
        List<PackageChartEntry> entries = [];
        foreach (ChartPackage package in packages ?? [])
        {
            try
            {
                if (package != null)
                {
                    AppendPackageChartSourceEntries(package.ChartEntries, entries);
                }
            }
            catch
            {
            }
        }
        return new PackageChartSourceSnapshot(entries);
    }

    private static PackageChartSourceSnapshot CreatePackageChartSourceSnapshotFromEntries(IEnumerable<PackageChartEntry> entries)
    {
        List<PackageChartEntry> chartEntries = [];
        AppendPackageChartSourceEntries(entries, chartEntries);
        return new PackageChartSourceSnapshot(chartEntries);
    }

    private static void AppendPackageChartSourceEntries(
        IEnumerable<PackageChartEntry> entries,
        ICollection<PackageChartEntry> chartEntries)
    {
        foreach (PackageChartEntry entry in entries ?? [])
        {
            ChartFile chart = entry?.Chart;
            if (chart != null)
            {
                chartEntries.Add(entry);
            }
        }
    }

    private static List<PackageChartEntry> CreatePackageChartEntrySnapshot(IEnumerable<ChartPackage> packages)
    {
        List<PackageChartEntry> snapshot = null;
        RetryHelper.RetryIfError(delegate
        {
            snapshot = CreatePackageChartEntrySnapshotCore(packages);
        }, delegate (Exception ex)
        {
            ExceptionDispatchInfo.Capture(ex).Throw();
        }, delegate
        {
            Thread.Sleep(100);
        }, 100u);
        return snapshot ?? [];
    }

    private static List<PackageChartEntry> CreatePackageChartEntrySnapshotCore(IEnumerable<ChartPackage> packages)
    {
        List<PackageChartEntry> snapshot = [];
        foreach (ChartPackage package in packages ?? [])
        {
            try
            {
                if (package != null)
                {
                    snapshot.AddRange(package.ChartEntries);
                }
            }
            catch
            {
            }
        }
        return snapshot;
    }

    /// <summary>
    /// Creates chart playback-stop targets from package entries without materializing bmson compatibility adapters.
    /// </summary>
    /// <param name="packages">Packages whose entries may overlap the currently playing chart directory.</param>
    /// <returns>Charts that should be considered before package mutation.</returns>
    internal static List<ChartFile> CreatePackagePlaybackTargetSnapshot(IEnumerable<ChartPackage> packages)
    {
        return [.. (packages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries)
            .Select(entry => entry?.Chart)
            .Where(chart => chart != null)];
    }

    private bool TryGetVirtualDuplicateSourceCharts(
        object parameter,
        out IEnumerable<ChartFile> sourceCharts,
        out string subsetName)
    {
        return TryGetVirtualDuplicateSourceChartsCore(DuplicateChartGroups, parameter, out sourceCharts, out subsetName);
    }

    private static bool TryGetVirtualDuplicateSourceChartsCore(
        IEnumerable<DuplicateGroup> duplicateGroups,
        object parameter,
        out IEnumerable<ChartFile> sourceCharts,
        out string subsetName)
    {
        if (duplicateGroups == null)
        {
            sourceCharts = [];
            subsetName = "duplicate_empty";
            return true;
        }

        List<DuplicateGroup> groupSnapshot = [.. duplicateGroups.Where(group => group != null)];
        object normalizedParameter = NormalizeDuplicateViewParameter(parameter);
        if (normalizedParameter == null)
        {
            sourceCharts = CreateDuplicateChartFileSnapshot(groupSnapshot);
            subsetName = "duplicate_all";
            return true;
        }
        if (normalizedParameter is DuplicateViewContext duplicateContext)
        {
            if (duplicateContext.Kind == DuplicateViewContextKind.GroupHeader)
            {
                DuplicateGroup duplicateGroup = groupSnapshot.FirstOrDefault(group => string.Equals(group.Header, duplicateContext.Value, StringComparison.Ordinal));
                sourceCharts = duplicateGroup != null ? duplicateGroup.ChartFiles : CreateDuplicateChartFileSnapshot(groupSnapshot);
                subsetName = "duplicate_group";
                return true;
            }

            sourceCharts = CreateDuplicateFolderChartFileSnapshot(groupSnapshot, duplicateContext.Value);
            subsetName = "duplicate_folder";
            return true;
        }
        if (normalizedParameter is DuplicateGroup groupParameter)
        {
            sourceCharts = groupParameter.ChartFiles;
            subsetName = "duplicate_group";
            return true;
        }
        if (normalizedParameter is string folderPath)
        {
            sourceCharts = CreateDuplicateFolderChartFileSnapshot(groupSnapshot, folderPath);
            subsetName = "duplicate_folder";
            return true;
        }

        sourceCharts = null;
        subsetName = string.Empty;
        return false;
    }

    private List<ChartFile> CreateDuplicateChartFileSnapshot()
    {
        return CreateDuplicateChartFileSnapshot(DuplicateChartGroups);
    }

    private static List<ChartFile> CreateDuplicateChartFileSnapshot(IEnumerable<DuplicateGroup> duplicateGroups)
    {
        return [.. (duplicateGroups ?? []).SelectMany(group => group.ChartFiles)];
    }

    private List<ChartFile> CreateDuplicateFolderChartFileSnapshot(string folderPath)
    {
        return CreateDuplicateFolderChartFileSnapshot(DuplicateChartGroups, folderPath);
    }

    private static List<ChartFile> CreateDuplicateFolderChartFileSnapshot(IEnumerable<DuplicateGroup> duplicateGroups, string folderPath)
    {
        string folderPrefix = folderPath + Path.DirectorySeparatorChar;
        return [.. (duplicateGroups ?? [])
            .SelectMany(group => group.ChartFiles)
            .Where(chart => !string.IsNullOrWhiteSpace(chart.Path) && chart.Path.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))];
    }

    private static bool ShouldApplyResourceHealthProjectionForVirtualSubset(viewUpdateMode treeMode)
    {
        return treeMode == viewUpdateMode.FileMissingFilterSelected
            || treeMode == viewUpdateMode.FileMissingIgnoredFilterSelected
            || treeMode == viewUpdateMode.NewlyInstalledFolderSelected;
    }

    private static string CreateVirtualNormalLibraryFilterIdentity(string folderFilterIdentity, string keywordFilter, ModeFilterType modeFilter, int scoreSnapshotVersion, int chartInfoIndexVersion)
    {
        if (string.IsNullOrEmpty(folderFilterIdentity) && string.IsNullOrWhiteSpace(keywordFilter) && modeFilter == ModeFilterType.All)
        {
            return "normal_default";
        }
        string normalizedKeywordFilter = keywordFilter ?? string.Empty;
        bool hasKeywordFilter = !string.IsNullOrWhiteSpace(normalizedKeywordFilter);
        string folderIdentity = string.IsNullOrEmpty(folderFilterIdentity)
            ? "none"
            : folderFilterIdentity;
        string identity = "normal_filter:folder=" + folderIdentity
            + ";keyword=" + StringComparer.Ordinal.GetHashCode(normalizedKeywordFilter).ToString(CultureInfo.InvariantCulture);
        if (hasKeywordFilter)
        {
            identity += ";score=" + scoreSnapshotVersion.ToString(CultureInfo.InvariantCulture)
                + ";chart=" + chartInfoIndexVersion.ToString(CultureInfo.InvariantCulture);
        }
        return identity
            + ";mode=" + ((int)modeFilter).ToString(CultureInfo.InvariantCulture);
    }

    internal static string CreateVirtualNormalLibraryFilterIdentityForTest(string folderFilterIdentity, string keywordFilter, ModeFilterType modeFilter, int scoreSnapshotVersion, int chartInfoIndexVersion)
    {
        return CreateVirtualNormalLibraryFilterIdentity(folderFilterIdentity, keywordFilter, modeFilter, scoreSnapshotVersion, chartInfoIndexVersion);
    }

    internal static List<ChartListSourceRow> CreateDuplicateVirtualSourceRowsForTest(IEnumerable<DuplicateGroup> duplicateGroups, object parameter)
    {
        if (!TryGetVirtualDuplicateSourceChartsCore(duplicateGroups, parameter, out IEnumerable<ChartFile> sourceCharts, out _))
        {
            return [];
        }
        return ChartListSourceRow.BuildStandardLibraryRows(
            sourceCharts,
            ChartListSourceProjectionMode.PreserveSourceProjection,
            resourceHealthProjectionProvider: null,
            playlistReferenceDisplayProvider: null);
    }

    private static HashSet<int?> CreateModeFilterValueSet(ModeFilterType modeFilter)
    {
        HashSet<int?> modeValues = [null];
        if ((modeFilter & ModeFilterType._5KEYS) == ModeFilterType._5KEYS)
        {
            modeValues.Add(5);
        }
        if ((modeFilter & ModeFilterType._7KEYS) == ModeFilterType._7KEYS)
        {
            modeValues.Add(7);
        }
        if ((modeFilter & ModeFilterType._9KEYS) == ModeFilterType._9KEYS)
        {
            modeValues.Add(9);
        }
        if ((modeFilter & ModeFilterType._10KEYS) == ModeFilterType._10KEYS)
        {
            modeValues.Add(10);
        }
        if ((modeFilter & ModeFilterType._14KEYS) == ModeFilterType._14KEYS)
        {
            modeValues.Add(14);
        }
        return modeValues;
    }

    private ResourceHealthWarningProjection GetResourceHealthProjectionForRow(LibraryChartRow row)
    {
        ChartFile chart = row?.Chart;
        if (files == null || chart == null)
        {
            return ResourceHealthWarningProjection.Empty;
        }
        return files.TryGetCurrentResourceHealthWarningProjection(chart);
    }

    private ResourceHealthWarningProjection GetResourceHealthProjectionForSourceRow(ChartListSourceRow row)
    {
        if (files == null || row == null)
        {
            return ResourceHealthWarningProjection.Empty;
        }
        return files.TryGetCurrentResourceHealthWarningProjection(row.Kind, row.Path, row.Hash);
    }

    private ChartScoreSnapshot ResolveScoreSnapshotForSourceRow(ChartListSourceRow row)
    {
        if (files == null || row == null)
        {
            return ChartScoreSnapshot.MissingChart;
        }
        return files.ResolveChartScoreSnapshot(row.Kind, row.Path, row.Hash, row.Sha256);
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
        lock (playlistViewState.SyncRoot)
        {
            sourceRowsToDispose = playlistViewState.SourceRows;
            previousGenerationId = playlistViewState.SourceGenerationId;
            currentViewRows = playlistViewState.CurrentViewRows;
            long previousViewGenerationId = playlistViewState.CurrentViewGenerationId;
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
            playlistViewState.SourceRows = [];
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
            playlistViewState.SourceRows = sourceRows ?? [];
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
        LogPlaylistRetention(((viewRows == null || viewRows.Count == 0) ? "playlist_view_clear " : "playlist_view_replace ") + "generationId=" + currentViewGenerationId + " previousGenerationId=" + previousViewGenerationId + " sourceCount=" + sourceRowsAlive + " viewCount=" + (viewRows?.Count ?? 0) + " playlistSourceRowCount=" + sourceRowsAlive + " playlistViewRowCount=" + CountPlaylistDetailRows(viewRows) + " previousViewRowsReferenced=" + CountPlaylistDetailRows(previousViewRows) + " disposedCount=" + CountPlaylistDetailRows(previousViewRows) + " selectedIndex=" + SelectedIndexChartRowsView);
        return previousViewRows;
    }

    /// <summary>
    /// playlist 表示用 snapshot の仮想行を破棄します。
    /// </summary>
    /// <param name="viewRows">破棄する表示用 snapshot。</param>
    private static void DisposePlaylistViewRows(IEnumerable viewRows)
    {
        DisposeDisposableRows(viewRows);
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

    public int SelectedIndexChartRowsView
    {
        get
        {
            return _SelectedIndexChartRowsView;
        }
        set
        {
            if (_SelectedIndexChartRowsView != value)
            {
                _SelectedIndexChartRowsView = value;
                RaisePropertyChanged("SelectedIndexChartRowsView");
            }
        }
    }

    public CustomTableColumnSettings ColumnsSettingsChartRowsView
    {
        get
        {
            return _ColumnsSettingsChartRowsView;
        }
        set
        {
            if (ReferenceEquals(_ColumnsSettingsChartRowsView, value))
            {
                return;
            }
            _ColumnsSettingsChartRowsView = value;
            RaisePropertyChanged("ColumnsSettingsChartRowsView");
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
                _PlaylistSummaryView = value ?? [];
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
    public bool UseAsyncChartRowsViewBinding
    {
        get
        {
            return _UseAsyncChartRowsViewBinding;
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
        RaisePropertyChanged("IsLibraryOperationInProgress");
    }

    public bool IsLibraryOperationInProgress
    {
        get
        {
            lock (startupProgressLock)
            {
                return _IsStartupUiInteractionBlocked || startupProgressState.IsActive;
            }
        }
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

    public bool IsMaintenanceRescanProgressActive
    {
        get
        {
            return _IsMaintenanceRescanProgressActive;
        }
        private set
        {
            if (_IsMaintenanceRescanProgressActive != value)
            {
                _IsMaintenanceRescanProgressActive = value;
                RaisePropertyChanged("IsMaintenanceRescanProgressActive");
            }
        }
    }

    public string MaintenanceRescanLabel
    {
        get
        {
            return _MaintenanceRescanLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (_MaintenanceRescanLabel != normalized)
            {
                _MaintenanceRescanLabel = normalized;
                RaisePropertyChanged("MaintenanceRescanLabel");
            }
        }
    }

    public string MaintenanceRescanSubLabel
    {
        get
        {
            return _MaintenanceRescanSubLabel;
        }
        private set
        {
            string normalized = value ?? string.Empty;
            if (_MaintenanceRescanSubLabel != normalized)
            {
                _MaintenanceRescanSubLabel = normalized;
                RaisePropertyChanged("MaintenanceRescanSubLabel");
            }
        }
    }

    public double MaintenanceRescanValue
    {
        get
        {
            return _MaintenanceRescanValue;
        }
        private set
        {
            if (_MaintenanceRescanValue != value)
            {
                _MaintenanceRescanValue = value;
                RaisePropertyChanged("MaintenanceRescanValue");
            }
        }
    }

    public double MaintenanceRescanMaximum
    {
        get
        {
            return _MaintenanceRescanMaximum;
        }
        private set
        {
            double normalized = Math.Max(1.0, value);
            if (_MaintenanceRescanMaximum != normalized)
            {
                _MaintenanceRescanMaximum = normalized;
                RaisePropertyChanged("MaintenanceRescanMaximum");
            }
        }
    }

    public bool MaintenanceRescanCanCancel
    {
        get
        {
            return _MaintenanceRescanCanCancel;
        }
        private set
        {
            if (_MaintenanceRescanCanCancel != value)
            {
                _MaintenanceRescanCanCancel = value;
                RaisePropertyChanged("MaintenanceRescanCanCancel");
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
                RaisePropertyChanged("IsLibraryOperationInProgress");
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
                    RefreshChartRowsView(viewUpdateMode.ModeFilterUpdated);
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
                RefreshChartRowsView(viewUpdateMode.KeywordFilterUpdated);
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
            : GridKeywordSearchContext.ChartList;
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
        if (GridKeywordSearchCompletion.IsPlaylistValueCompletionContext(keywordFilter, caretIndex, context))
        {
            GridKeywordSearchCompletionResult playlistValueCompletion = GridKeywordSearchCompletion.CreatePlaylistValueCompletion(keywordFilter, caretIndex, context, GetKeywordSearchPlaylistNameCandidates(context));
            if (playlistValueCompletion.Items.Count > 0)
            {
                SetKeywordSearchSuggestions(targetSuggestions, playlistValueCompletion.Items, KeywordSearchSuggestionKind.Value, isPlaylistSummary);
                return;
            }
        }
        if (forceHistory)
        {
            IReadOnlyList<KeywordSearchSuggestionItem> historySuggestions = BuildKeywordSearchHistorySuggestions(history, keywordFilter);
            SetKeywordSearchSuggestions(targetSuggestions, historySuggestions, KeywordSearchSuggestionKind.History, isPlaylistSummary);
            return;
        }
        SetKeywordSearchSuggestions(targetSuggestions, [], KeywordSearchSuggestionKind.Field, isPlaylistSummary);
    }

    private IReadOnlyList<string> GetKeywordSearchPlaylistNameCandidates(GridKeywordSearchContext context)
    {
        if (context == GridKeywordSearchContext.PlaylistSummary || tables == null)
        {
            return [];
        }
        bool lockAcquired = false;
        try
        {
            tables.AcquireReaderLockBMSTables();
            lockAcquired = true;
            return [.. (BMSTables ?? Enumerable.Empty<BMSTable>())
                .Where(table => !string.IsNullOrWhiteSpace(table?.name))
                .Select(table => table.name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        }
        finally
        {
            if (lockAcquired)
            {
                tables.FreeReaderLockBMSTables();
            }
        }
    }

    private void SetKeywordSearchSuggestions(ObservableCollection<KeywordSearchSuggestionItem> targetSuggestions, IReadOnlyList<KeywordSearchSuggestionItem> suggestions, KeywordSearchSuggestionKind kind, bool isPlaylistSummary)
    {
        targetSuggestions.Clear();
        foreach (KeywordSearchSuggestionItem suggestion in suggestions ?? [])
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
        target.AddRange(source ?? []);
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

    private void SetNormalLibraryTreeFilter(NormalLibraryTreeFilter filter)
    {
        virtualNormalLibraryTreeFilter = filter;
        RaisePropertyChanged("FolderFilter");
        RefreshChartRowsView(viewUpdateMode.FolderFilterSelected);
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

    public bool IsWriteLockHeldPendingInstallCharts
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldPendingInstallCharts;
            }
            return true;
        }
    }

    public bool IsWriteLockHeldDuplicateChartGroups
    {
        get
        {
            if (files != null)
            {
                return files.IsWriteLockHeldDuplicateChartGroups;
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
        regularBmsLibraryRowCache = new NormalLibraryRowCache();
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
        await _semaphore.WaitAsync();
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);
        Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction = CreatePlaylistReferenceReplaceUpdateCallback();
        bool scheduleDeferredExternalSync = false;
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.PlaylistTree | UiRefreshChannel.DuplicateTree);
            await Task.Run(delegate
            {
                tables.ReloadTables(updateCallbackAction, queueBeatorajaBmtExportAfterHydration: false);
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
            StartDeferredExternalPlaylistSync("ReloadTables", fromReloadTables: true, updateCallbackAction, operationToken);
        }
        SkipUnrequestedStartupProgressPhases(
            "ReloadTables:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistReferenceApplied);
    }

    /// <summary>
    /// score source / score.db 設定変更を、playlist/table reload を伴わずに反映します。
    /// </summary>
    public async void ReloadScoresOnly()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "ReloadScoresOnly");
        await _semaphore.WaitAsync();
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.ScoreOnly);
        bool refreshViews = false;
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView);
            LogInitStage("score_reload_task_start", "ReloadScoresOnly");
            await Task.Run(delegate
            {
                LogInitStage("score_reload_call", "ReloadScoresOnly");
                files.InitializeScoresOnly(null);
            }).Logging("ReloadScoresOnly");
            LogInitStage("score_reload_done", "ReloadScoresOnly");
            refreshViews = true;
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
            LogInitStage("ui_suppress_end_called", "ReloadScoresOnly");
            _semaphore.Release();
        }
        if (refreshViews)
        {
            RefreshLibraryMainViewForCurrentFilter();
            RefreshPlaylistSummaryIfVisible("score_only_reload");
        }
        SkipUnrequestedStartupProgressPhases(
            "ReloadScoresOnly:scheduled",
            operationToken,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone);
    }

    public async void ReloadFileDiff()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "ReloadFileDiff");
        bool scheduleDeferredPlaylistRef = false;
        await _semaphore.WaitAsync();
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff);
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
            LogInitStage("file_diff_reload_task_start", "ReloadFileDiff");
            await Task.Run(delegate
            {
                LogInitStage("file_diff_reload_call", "ReloadFileDiff");
                files.ReloadFileDiff();
            }).Logging("ReloadFileDiff");
            LogInitStage("file_diff_reload_done", "ReloadFileDiff");
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
            LogInitStage("ui_suppress_end_called", "ReloadFileDiff");
            _semaphore.Release();
        }
        if (scheduleDeferredPlaylistRef)
        {
            ScheduleDeferredPlaylistReferenceApply("ReloadFileDiff", operationToken);
            LogInitStage("deferred_playlist_ref_queued", "ReloadFileDiff");
        }
        SkipUnrequestedStartupProgressPhases(
            "ReloadFileDiff:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.PlaylistEntriesHydrationDone);
    }

    public async void ReinitializeLibrary()
    {
        if (!initializationCompleted)
        {
            return;
        }
        LogInitStage("start", "FullReinitialize");
        bool scheduleDeferredPlaylistRef = false;
        await _semaphore.WaitAsync();
        long operationToken = StartStartupProgressOperation(StartupProgressOperationKind.FullReinitialize);
        try
        {
            BeginUiUpdateSuppression(UiRefreshChannel.LibraryMainView | UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.InstallTree | UiRefreshChannel.DuplicateTree);
            LogInitStage("files_initialize_task_start", "FullReinitialize");
            await Task.Run(delegate
            {
                LogInitStage("files_initialize_call", "FullReinitialize");
                files.Reinitialize();
            }).Logging("FullReinitialize");
            LogInitStage("files_initialize_done", "FullReinitialize");
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
            LogInitStage("ui_suppress_end_called", "FullReinitialize");
            _semaphore.Release();
        }
        if (scheduleDeferredPlaylistRef)
        {
            ScheduleDeferredPlaylistReferenceApply("FullReinitialize", operationToken);
            LogInitStage("deferred_playlist_ref_queued", "FullReinitialize");
        }
        SkipUnrequestedStartupProgressPhases(
            "FullReinitialize:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone);
    }

    internal static string BuildAppSchemaRepairWarningMessage(AppSchemaPreflightResult preflightResult)
    {
        if (preflightResult == null)
        {
            throw new ArgumentNullException(nameof(preflightResult));
        }
        return BeMusicSeeker.Properties.Resources.AppSchemaRepairWarningMessage;
    }

    internal static bool ApplyAppSchemaRepairPreflightForStartup(AppSchemaPreflightResult preflightResult, ref bool approvedForSession, Func<string, bool?> confirmWarning, Action applyStartupRepair, Action shutdown)
    {
        if (preflightResult == null)
        {
            throw new ArgumentNullException(nameof(preflightResult));
        }
        if (applyStartupRepair == null)
        {
            throw new ArgumentNullException(nameof(applyStartupRepair));
        }
        if (preflightResult.WarnRequired && !approvedForSession)
        {
            if (confirmWarning == null)
            {
                throw new ArgumentNullException(nameof(confirmWarning));
            }
            if (confirmWarning(BuildAppSchemaRepairWarningMessage(preflightResult)) != true)
            {
                shutdown?.Invoke();
                return false;
            }
            approvedForSession = true;
        }
        applyStartupRepair();
        return true;
    }

    private async Task<bool> EnsureAppSchemaRepairApprovedForStartupAsync()
    {
        LogInitStage("app_schema_preflight_inspect_start", "Initialize");
        var appSchemaPreflightService = new AppSchemaPreflightService();
        AppSchemaPreflightResult preflightResult = appSchemaPreflightService.Inspect(Settings.Default.LR2SongDBPath);
        LogInitStage("app_schema_preflight_inspect_done", "Initialize");
        if (preflightResult.WarnRequired && !bmsonMigrationApprovedForSession)
        {
            LogInitStage("app_schema_preflight_prompt_show", "Initialize");
            var confirmationMessage = new ConfirmationMessage(BuildAppSchemaRepairWarningMessage(preflightResult), BeMusicSeeker.Properties.Resources.AppSchemaRepairWarningTitle, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "ConfirmationDialog");
            base.Messenger.Raise(confirmationMessage);
            LogInitStage("app_schema_preflight_prompt_close", "Initialize");
            if (confirmationMessage.Response != true)
            {
                System.Windows.Application.Current?.Shutdown();
                return false;
            }
            bmsonMigrationApprovedForSession = true;
        }
        await Task.Run(delegate
        {
            ApplyAppSchemaRepairForStartupOrThrow(appSchemaPreflightService, preflightResult);
        }).Logging("AppSchemaStartupRepair");
        return true;
    }

    private void ApplyAppSchemaRepairForStartupOrThrow(AppSchemaPreflightService appSchemaPreflightService, AppSchemaPreflightResult preflightResult)
    {
        var stopwatch = Stopwatch.StartNew();
        LogInitStage("app_schema_repair_start", "Initialize");
        var gateway = new BmsLibraryDbGateway(Settings.Default.LR2SongDBPath);
        long schemaStartMs = stopwatch.ElapsedMilliseconds;
        if (preflightResult.WarnRequired || preflightResult.NeedsAppSchemaVersionRepair || preflightResult.RepairRequired)
        {
            LogInitStage("app_schema_repair_apply_start", "Initialize");
            gateway.RepairAppOwnedSchema();
            LogInitStage("app_schema_repair_apply_done elapsedMs=" + (stopwatch.ElapsedMilliseconds - schemaStartMs), "Initialize");
        }
        else
        {
            LogInitStage("app_schema_ensure_start", "Initialize");
            gateway.EnsureAppOwnedSchema();
            LogInitStage("app_schema_ensure_done elapsedMs=" + (stopwatch.ElapsedMilliseconds - schemaStartMs), "Initialize");
        }
        LogInitStage("app_schema_preflight_final_reinspect_start", "Initialize");
        AppSchemaPreflightResult finalResult = appSchemaPreflightService.Inspect(Settings.Default.LR2SongDBPath);
        LogInitStage("app_schema_preflight_final_reinspect_done", "Initialize");
        if (finalResult.NeedsAppSchemaVersionRepair
            || finalResult.RepairRequired)
        {
            throw new InvalidOperationException("app schema repair did not converge.");
        }
        LogInitStage("app_schema_repair_done elapsedMs=" + stopwatch.ElapsedMilliseconds, "Initialize");
    }

    /// <summary>
    /// アプリケーション初期起動時に実行される、メイン初期化ルーチンです。非同期で呼び出されます。<br/>
    /// 設定の妥当性チェック、BMSデータベース (LR2SongDB形式など) との接続、BMSプレイヤーインスタンスの生成、
    /// およびコレクション更新をフックする各種イベントリスナーの登録を順次行います。
    /// </summary>
    private LibraryProfile CreateLibraryProfileForStartup()
    {
        if (Settings.Default.OperationModeLR2DB)
        {
            lr2config ??= new LR2Config(Settings.Default.LR2ConfigXmlPath);
            string playerId = lr2config.GetPlayerId();
            string scoreDbPath = Settings.Default.LR2RootPath + "\\LR2files\\Database\\Score\\" + playerId + ".db";
            if (playerId == null || !File.Exists(scoreDbPath))
            {
                scoreDbPath = null;
            }
            return new LibraryProfile(
                operationModeLR2DB: true,
                songDbPath: Settings.Default.LR2SongDBPath,
                searchRoots: [],
                lr2ConfigProvider: () => lr2config,
                lr2ScoreDbPath: scoreDbPath,
                canWriteLr2Config: true,
                canOutputLr2Folders: true,
                canUseLr2Backup: true,
                canUseLr2IrScore: true);
        }

        lr2config = null;
        string standaloneSongDbPath = StandaloneLibraryDatabase.EnsurePortableSongDb();
        return new LibraryProfile(
            operationModeLR2DB: false,
            songDbPath: standaloneSongDbPath,
            searchRoots: SettingDialogViewModel.GetStandaloneBmsRootPathsFromSettings(),
            lr2ConfigProvider: null,
            lr2ScoreDbPath: null,
            canWriteLr2Config: false,
            canOutputLr2Folders: false,
            canUseLr2Backup: false,
            canUseLr2IrScore: false);
    }

    private LR2Config CreateLR2PlayerConfig()
    {
        if (Settings.Default.OperationModeLR2DB && lr2config != null)
        {
            return lr2config;
        }
        return new LR2Config(Settings.Default.LR2ConfigXmlPath);
    }

    public async void Initialize()
    {
        await _semaphore.WaitAsync();
        SetStartupUiInteractionBlocked(true);
        LogInitStage("start", "Initialize");
        initializationCompleted = false;
        RaisePropertyChanged(() => IsInitializationCompleted);
        _ = string.Empty;
        string text = Assembly.GetEntryAssembly().GetName().Version.ToString();
        WindowTitle = "BeMusicSeeker Unofficial Fork - " + text;
        if (!settingDialog.CheckValidation())
        {
            if (((App)System.Windows.Application.Current).firstStartup)
            {
                _semaphore.Release();
                SetStartupUiInteractionBlocked(false);
                base.Messenger.Raise(new InteractionMessage("InitialSetupLanguageDialog"));
                return;
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
            if (Settings.Default.OperationModeLR2DB && !await EnsureAppSchemaRepairApprovedForStartupAsync())
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
        long operationToken;
        try
        {
            LibraryProfile libraryProfile = CreateLibraryProfileForStartup();
            files = new BMSLibrary(libraryProfile.SongDbPath, libraryProfile.Lr2ConfigProvider, libraryProfile.Lr2ScoreDbPath);
            tables = new BMSPlaylist(libraryProfile.SongDbPath, libraryProfile.Lr2ConfigProvider, libraryProfile.Lr2ScoreDbPath, () => files.GetBMSScores());
            files.StartupBackgroundTaskScheduler = QueueStartupBackgroundTask;
            files.StartupBackgroundTaskReporter = RecordStartupBackgroundTaskCompleted;
            tables.StartupBackgroundTaskScheduler = QueueStartupBackgroundTask;
            if (!libraryProfile.OperationModeLR2DB)
            {
                files.SearchTargets.AddRange(libraryProfile.SearchRoots);
            }
            if (Settings.Default.UsePlayeruBMplay)
            {
                bmsPlayer = new uBMplay(Settings.Default.uBMplayPath);
            }
            else if (Settings.Default.UsePlayerBMIIDXView)
            {
                bmsPlayer = new BMIIDXView2015(Settings.Default.BMIIDXViewPath);
            }
            else if (Settings.Default.UsePlayerLR2body && File.Exists(settingDialog.LR2bodyPath))
            {
                bmsPlayer = new LR2body(settingDialog.LR2bodyPath, CreateLR2PlayerConfig());
            }
            operationToken = StartStartupProgressOperation(StartupProgressOperationKind.Startup);
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
        ColumnsSettingsChartRowsView = Settings.Default.StandardCustomTableColumnSettings;
        listenerForBMSLibrary = new PropertyChangedEventListener(files);
        listenerForBMSPlaylist = new PropertyChangedEventListener(tables);
        listenerForBMSPlaylistBMSTablesCollection = new CollectionChangedEventListener(tables.BMSTables);
        listenerForBMSLibrary.RegisterHandler(() => files.BMSFiles, delegate
        {
            UpdateSharedChartTransientStates(files?.ConsumeLatestInstallDestinationChangedCharts(), forceInstallDestinationProjection: true);
            InvalidatePlaylistLibraryIndexSnapshot("library_charts_changed");
            PruneRegularBmsLibraryRowCacheByBmsFiles(files?.BMSFiles);
            IncrementNormalLibrarySourceGeneration("library_charts_changed");
            ResetRegularDerivedViewCaches();
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                RefreshPlaylistSummaryIfVisible("library_charts_changed");
                return;
            }
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree, "library_charts_changed"))
            {
                RequestDeferredPlaylistSummaryRefresh();
                return;
            }
            if (Enum.IsDefined(typeof(MaintenanceFilterType), (int)treeViewFilterTypeSelected))
            {
                var type = (MaintenanceFilterType)treeViewFilterTypeSelected;
                if (type != MaintenanceFilterType.DuplicateFilter)
                {
                    ExecMaintenanceFilter(type);
                }
            }
            else
            {
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
            RefreshPlaylistSummaryIfVisible("library_charts_changed");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.BmsonSongs, delegate
        {
            UpdateSharedChartTransientStates(files?.ConsumeLatestInstallDestinationChangedCharts(), forceInstallDestinationProjection: true);
            InvalidatePlaylistLibraryIndexSnapshot("library_bmsons_changed");
            BmsonLibraryRowCacheSyncResult syncResult = SyncBmsonLibraryRowCache(files?.BmsonSongs);
            if (syncResult.SortKeyChanged)
            {
                InvalidateNormalLibrarySortKeysForBmsonSync(syncResult);
            }
            RefreshPlaylistSummaryIfVisible("library_bmsons_changed");
            if (TrySuppress(UiRefreshChannel.LibraryMainView))
            {
                return;
            }
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree, "library_bmsons_changed"))
            {
                RequestDeferredPlaylistSummaryRefresh();
                return;
            }
            if (ShouldRefreshPlaylistViewAfterBmsonSongsChanged(treeViewFilterTypeSelected))
            {
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
                return;
            }
            if (!ShouldIncludeBmsonLibraryRowsInMainView(treeViewFilterTypeSelected, treeViewFilterTypeSelected))
            {
                return;
            }
            if (syncResult.SourceChanged)
            {
                IncrementNormalLibrarySourceGeneration(ResolveBmsonSourceGenerationReason(syncResult, "library_bmsons"));
                ResetRegularDerivedViewCaches();
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgress, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressScannerLabel, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressTotalCount, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressProcessedCount, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgressCurrentPath, delegate
        {
            UpdateStartupProgressLibraryInitializationStatus(files.LibraryInitializationProgress, files.LibraryInitializationProgressScannerLabel, files.LibraryInitializationProgressTotalCount, files.LibraryInitializationProgressProcessedCount, files.LibraryInitializationProgressCurrentPath);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryDatabaseLoadCompletedVersion, delegate
        {
            TryCompleteStartupProgressLibraryDatabaseLoad(files.LibraryDatabaseLoadCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryFileEnumerationCompletedVersion, delegate
        {
            TryCompleteStartupProgressLibraryFileEnumeration(files.LibraryFileEnumerationCompletedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.LibraryFileDiffCompletedVersion, delegate
        {
            TryCompleteStartupProgressLibraryFileDiff(files.LibraryFileDiffCompletedVersion);
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
            RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Score, "score_hydration_completed");
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "score_hydration_completed"))
            {
                RequestDeferredPlaylistSummaryRefresh();
                return;
            }
            RefreshPlaylistSummaryIfVisible("score_hydration_completed");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ScoreSnapshotVersion, delegate
        {
            UpdateScoreSnapshotProjectionVersionCache();
            if (!IsPlaylistDetailViewActive)
            {
                return;
            }
            RequestPlaylistScoreSnapshotRefresh(files.ScoreSnapshotVersion);
            RefreshPlaylistSummaryIfVisible("score_snapshot_changed");
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
            RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Score, "ranking_refresh_completed");
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "ranking_refresh_completed"))
            {
                RequestDeferredPlaylistSummaryRefresh();
                return;
            }
            RefreshPlaylistSummaryIfVisible("ranking_refresh_completed");
        });
        listenerForBMSLibrary.RegisterHandler(() => files.MaintenanceHydrationRequestedVersion, delegate
        {
            TrackStartupProgressMaintenanceRequested(files.MaintenanceHydrationRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.MaintenanceHydrationCompletedVersion, delegate
        {
            TryCompleteStartupProgressMaintenance(files.MaintenanceHydrationCompletedVersion);
            RefreshResourceHealthViewsAfterMaintenanceChanged();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallableMaintenanceDeferredRequestedVersion, delegate
        {
            TrackStartupProgressInstallableMaintenanceRequested(files.InstallableMaintenanceDeferredRequestedVersion);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallableMaintenanceDeferredCompletedVersion, delegate
        {
            TryCompleteStartupProgressInstallableMaintenance(files.InstallableMaintenanceDeferredCompletedVersion);
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
            if ((files?.ChartInfoBackfillDigestBackfilledCount ?? 0) > 0)
            {
                InvalidateNormalLibrarySortKeys(NormalLibraryChartInfoDigestBackfilledReason);
            }
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
        listenerForBMSLibrary.RegisterHandler(() => files.ChartFilesNeedResourceFix, delegate
        {
            InvalidateNormalLibrarySortKeys(NormalLibraryWarningChangedReason);
            if (treeViewFilterTypeSelected == viewUpdateMode.FileMissingFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "chart_files_need_resource_fix_changed"))
                {
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
            else
            {
                RefreshNormalLibraryAfterWarningChanged("chart_files_need_resource_fix_changed");
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartFilesNeedResourceFixIgnored, delegate
        {
            InvalidateNormalLibrarySortKeys(NormalLibraryWarningChangedReason);
            if (treeViewFilterTypeSelected == viewUpdateMode.FileMissingIgnoredFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "chart_files_need_resource_fix_ignored_changed"))
                {
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
            else
            {
                RefreshNormalLibraryAfterWarningChanged("chart_files_need_resource_fix_ignored_changed");
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.DuplicateChartGroups, delegate
        {
            InvalidateNormalLibrarySortKeys(NormalLibraryWarningChangedReason);
            if (!TrySuppress(UiRefreshChannel.DuplicateTree)
                && !TryDeferStartupPresentationRefresh(UiRefreshChannel.DuplicateTree, "bms_files_duplicated_changed"))
            {
                RaisePropertyChanged(() => DuplicateChartGroups);
            }
            if (treeViewFilterTypeSelected == viewUpdateMode.DuplicateFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "bms_files_duplicated_changed"))
                {
                    return;
                }
                if (files.DuplicateChartGroups == null)
                {
                    files.SearchDuplicateChartGroups();
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
            else
            {
                RefreshNormalLibraryAfterWarningChanged("bms_files_duplicated_changed");
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
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "bms_files_garbled_changed"))
                {
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
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
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "bms_files_garbled_fixed_changed"))
                {
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
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
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "bms_files_unregistered_changed"))
                {
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartFilesZeroNote, delegate
        {
            InvalidateNormalLibrarySortKeys(NormalLibraryWarningChangedReason);
            if (treeViewFilterTypeSelected == viewUpdateMode.ZeroNoteFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "bms_files_zero_note_changed"))
                {
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
            else
            {
                RefreshNormalLibraryAfterWarningChanged("bms_files_zero_note_changed");
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartInfoParseFailedChartFiles, delegate
        {
            InvalidateNormalLibrarySortKeys(NormalLibraryWarningChangedReason);
            if (treeViewFilterTypeSelected == viewUpdateMode.ChartInfoParseErrorFilterSelected)
            {
                if (TrySuppress(UiRefreshChannel.LibraryMainView))
                {
                    return;
                }
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "bms_files_chart_info_parse_failed_changed"))
                {
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
            }
            else
            {
                RefreshNormalLibraryAfterWarningChanged("bms_files_chart_info_parse_failed_changed");
            }
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartPackagesInstalled, delegate
        {
            RebindChartPackagesInstalledCollectionListener();
            HandleChartPackagesInstalledCollectionChanged();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.ChartPackagesPending, delegate
        {
            RebindChartPackagesPendingCollectionListener();
            HandleChartPackagesPendingCollectionChanged();
        });
        listenerForBMSLibrary.RegisterHandler(() => files.PendingEstimateQueueStatusVersion, delegate
        {
            UpdatePendingEstimateQueueStatus(files.GetPendingEstimateQueueStatusSnapshot());
        });
        listenerForBMSLibrary.RegisterHandler(() => files.InstallEstimationProgressVersion, delegate
        {
            UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        });
        RebindChartPackagesInstalledCollectionListener();
        RebindChartPackagesPendingCollectionListener();
        UpdatePendingEstimateQueueStatus(files.GetPendingEstimateQueueStatusSnapshot());
        UpdateInstallEstimationProgressStatus(files.GetInstallEstimationProgressSnapshot());
        listenerForBMSLibrary.RegisterHandler(() => files.BMSParentFolderListCacheVersion, delegate
        {
            bmsParentFolderListViewInitialized = false;
            if (TrySuppress(UiRefreshChannel.LibraryFolderTree))
            {
                return;
            }
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryFolderTree, "parent_folder_cache_changed"))
            {
                return;
            }
            ScheduleDeferredLibraryFolderTreeRefresh();
        });
        listenerForBMSPlaylist.RegisterHandler(() => tables.BMSTables, delegate
        {
            if (TrySuppress(UiRefreshChannel.PlaylistTree))
            {
                RefreshPlaylistSummaryIfVisible("playlist_tables_changed", invalidateTableCountCache: true);
                return;
            }
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "playlist_tables_changed"))
            {
                RequestDeferredPlaylistSummaryRefresh();
                return;
            }
            RaisePropertyChanged(() => BMSTables);
            RefreshPlaylistSummaryIfVisible("playlist_tables_changed", invalidateTableCountCache: true);
        });
        listenerForBMSPlaylistBMSTablesCollection.RegisterHandler(delegate
        {
            if (TrySuppress(UiRefreshChannel.PlaylistTree))
            {
                RefreshPlaylistSummaryIfVisible("playlist_tables_collection_changed", invalidateTableCountCache: true);
                return;
            }
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "playlist_tables_collection_changed"))
            {
                RequestDeferredPlaylistSummaryRefresh();
                return;
            }
            RaisePropertyChanged(() => BMSTables);
            RefreshPlaylistSummaryIfVisible("playlist_tables_collection_changed", invalidateTableCountCache: true);
        });
        listenerForBMSPlaylist.RegisterHandler(() => tables.PlaylistEntriesHydrationCompletedVersion, delegate
        {
            TryCompleteStartupProgressPlaylistEntriesHydration(tables.PlaylistEntriesHydrationCompletedVersion);
            ScheduleDeferredPlaylistReferenceApply("PlaylistEntriesHydration");
            if (TryDeferStartupPresentationRefresh(UiRefreshChannel.PlaylistTree, "playlist_entries_hydration_completed"))
            {
                RequestDeferredPlaylistSummaryRefresh();
                return;
            }
            RefreshPlaylistSummaryIfVisible("playlist_entries_hydration_completed", invalidateTableCountCache: true);
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
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldPendingInstallCharts, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldPendingInstallCharts);
        });
        listenerForBMSLibrary.RegisterHandler(() => files.IsWriteLockHeldDuplicateChartGroups, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldDuplicateChartGroups);
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
            List<string> bkPaths = [];
            string songDBPath = null;
            List<string> scoreDBPaths = [];
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
                    scoreDBPaths = [.. Directory.EnumerateFiles(Settings.Default.LR2RootPath + "\\LR2files\\Database\\Score", "*.db", System.IO.SearchOption.AllDirectories)];
                }
                catch
                {
                    scoreDBPaths = [];
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
        var semaphore = new SemaphoreSlim(1, 1);
        void taskAdd1()
        {
            tables.Initialize(reloadExtPlaylist: false, null, semaphore, queueBeatorajaBmtExportAfterHydration: Settings.Default.SkipInitPlaylistLoad);
        }
        void taskAdd2()
        {
            try
            {
                IsLoadingExternalCollectionBMSTables = true;
                List<BMSTableSimple> bMSTableInfo = BMSPlaylist.GetBMSTableInfo(Settings.Default.TableListURL);
                var bMSTableSimpleCategorized = new BMSTableSimpleCategorized();
                IEnumerable<string> source = bMSTableInfo.Select(bMSTableSimple => bMSTableSimple.tag1).Distinct();
                bMSTableSimpleCategorized.Children = [.. source.Select(name => new BMSTableSimpleCategorized
                {
                    name = name
                })];
                foreach (BMSTableSimpleCategorized child in bMSTableSimpleCategorized.Children)
                {
                    string tag1 = child.name;
                    child.Children = [.. (from tt in bMSTableInfo
                                          where tt.tag1 == tag1 && string.IsNullOrWhiteSpace(tt.tag2)
                                          select new BMSTableSimpleCategorized(tt))];
                    List<BMSTableSimple> source2 = [.. bMSTableInfo.Where(tt => tt.tag1 == tag1 && !string.IsNullOrWhiteSpace(tt.tag2))];
                    foreach (string t2 in (from tt in source2.Select(tt => tt.tag2).Distinct()
                                           orderby tt
                                           select tt).ToList())
                    {
                        child.Children.Add(new BMSTableSimpleCategorized
                        {
                            name = t2,
                            Children = [.. (from tt in source2
                                            where tt.tag2 == t2
                                            select new BMSTableSimpleCategorized(tt) into tt
                                            orderby tt.name
                                            select tt)]
                        });
                    }
                }
                BMSExternalTableListExt = bMSTableSimpleCategorized;
                IsLoadingExternalCollectionBMSTables = false;
            }
            catch
            {
            }
        }
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
                files.InitializeStartup([taskAdd1, taskAdd2], semaphore);
            }).Logging("Initialize");
            LogInitStage("files_initialize_done", "Initialize");
            TryLogStartupReadyData();
        }
        catch (Exception ex)
        {
            FailStartupProgressOperation(ex.Message);
            DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + ex.ToString(), BeMusicSeeker.Properties.Resources.Error, MessageBoxButton.OK, MessageBoxImage.Hand);
            Logger currentClassLogger = LogManager.GetCurrentClassLogger();
            string text4 = Assembly.GetEntryAssembly().GetName().Version.ToString();
            currentClassLogger.Error(ex, text4 + " - " + Environment.NewLine + ex.ToString(), null);
            _semaphore.Release();
            SetStartupUiInteractionBlocked(false);
            base.Messenger.Raise(new InteractionMessage("InitializationException"));
            return;
        }
        finally
        {
            EndUiUpdateSuppression();
            LogInitStage("ui_suppress_end_called", "Initialize");
        }
        if (((App)System.Windows.Application.Current).firstStartup)
        {
            ((App)System.Windows.Application.Current).firstStartup = false;
            initialSetupCompletionMessagePending = true;
        }
        initializationCompleted = true;
        hasActiveLibraryProfile = true;
        RaisePropertyChanged(() => IsInitializationCompleted);
        RaisePropertyChanged(() => HasActiveLibraryProfile);
        SchedulePlaylistLibraryIndexPrewarm(GetPlaylistLibraryIndexVersion(), "initialize_completed");
        _semaphore.Release();
        LogInitStage("deferred_playlist_ref_waiting_for_playlist_entries_hydration", "Initialize");
        if (!Settings.Default.SkipInitPlaylistLoad)
        {
            StartDeferredExternalPlaylistSync("Initialize", fromReloadTables: false, CreatePlaylistReferenceReplaceUpdateCallback(), operationToken);
        }
        SkipUnrequestedStartupProgressPhases(
            "Initialize:scheduled",
            operationToken,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone);
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
        bmsPlayer?.CloseProcess();
        bool num = LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0));
        bool flag = LR2ScoreDBExtended.Lock(new TimeSpan(0, 1, 0));
        if (!num || !flag)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_error_close_timeout, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
        }
        TempDirectoryPublisher.RemoveAll();
    }

    private void PlayStartBmsFile(int indexChartRowsView)
    {
        object row;
        BeMusicSeeker.Models.BMSFile bmsFile;
        ChartFile playbackChart = null;
        try
        {
            row = ChartRowsView[indexChartRowsView];
            GridRowResolver.TryGetChartFile(row, out playbackChart);
            GridRowResolver.TryGetBmsPlayerFile(row, out bmsFile);
        }
        catch
        {
            return;
        }
        nowPlayingChartRowsViewIndex = indexChartRowsView;
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
        SelectedIndexChartRowsView = indexChartRowsView;
        base.Messenger.Raise(new InteractionMessage("CallbackPlayStartBMSfile"));
        playbackChart ??= ChartFileProjection.FromBmsFile(bmsFile);
        string installDestination = playbackChart?.InstallDestination;
        if (!string.IsNullOrWhiteSpace(installDestination) && Directory.Exists(installDestination))
        {
            ChartPackage chartPackage = ChartPackagesPending.Where(pkg => ContainsChartTarget(pkg, playbackChart)).FirstOrDefault();
            if (chartPackage == null)
            {
                if (Settings.Default.UsePlayerLR2body && Settings.Default.OperationModeLR2DB)
                {
                    var confirmationMessage = new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_warn_play_temp_install, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.YesNo, "ConfirmationDialog");
                    base.Messenger.Raise(confirmationMessage);
                    if (confirmationMessage.Response == false)
                    {
                        return;
                    }
                }
                chartPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(playbackChart)]);
                chartPackage.delete_parent = false;
            }
            string fileName = Path.GetFileName(bmsFile.path);
            while (File.Exists(Path.Combine(installDestination, Path.GetFileName(bmsFile.path))) || Directory.Exists(Path.Combine(installDestination, Path.GetFileName(bmsFile.path))))
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
            List<string> list = [];
            if (Directory.Exists(chartPackage.path))
            {
                string[] extensionsPermitted =
                [
                    .. BeMusicSeeker.Models.ChartFileKindResolver.BmsExtensions,
                    .. BeMusicSeeker.Models.ChartResourceExtensions.AudioExtensions,
                    .. BeMusicSeeker.Models.ChartResourceExtensions.ImageExtensions,
                ];
                list = [.. (from f in Directory.EnumerateFiles(chartPackage.path, "*", System.IO.SearchOption.TopDirectoryOnly)
                        where extensionsPermitted.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                        select f)];
            }
            else
            {
                list.Add(bmsFile.path);
            }
            lock (lockCopyFile)
            {
                if (string.IsNullOrWhiteSpace(installDestination))
                {
                    return;
                }
                using (new temporarilyCopyFiles(list, installDestination, 2000))
                {
                    string bmsFilePath = Path.Combine(installDestination, Path.GetFileName(bmsFile.path));
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
                if (Settings.Default.RepeatPlayMode && (Settings.Default.SinglePlayMode || indexChartRowsView == 0))
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
                if (SelectedIndexChartRowsView >= 0 && SelectedIndexChartRowsView < ChartRowsView.Count)
                {
                    PlayStartBmsFile(SelectedIndexChartRowsView);
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
            int num = nowPlayingChartRowsViewIndex;
            if (num < 0 || num >= ChartRowsView.Count)
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
                if (Settings.Default.RepeatPlayMode && num == ChartRowsView.Count)
                {
                    num = 0;
                }
                while (Settings.Default.FolderSkipPlayMode && num != ChartRowsView.Count)
                {
                    object candidateRow = ChartRowsView[num];
                    GridRowResolver.TryGetBmsPlayerFile(candidateRow, out BeMusicSeeker.Models.BMSFile bMSFile);
                    if (num == nowPlayingChartRowsViewIndex)
                    {
                        break;
                    }
                    string text2 = (bMSFile != null && !string.IsNullOrWhiteSpace(bMSFile.path) && File.Exists(bMSFile.path)) ? Path.GetDirectoryName(bMSFile.path) : num.ToString();
                    if (!string.IsNullOrWhiteSpace(text2) && text != text2)
                    {
                        break;
                    }
                    text = text2;
                    num++;
                    if (Settings.Default.RepeatPlayMode && num == ChartRowsView.Count)
                    {
                        num = 0;
                    }
                }
            }
            if (num < ChartRowsView.Count)
            {
                PlayEndBMSFile();
                PlayStartBmsFile(num);
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
            int num = nowPlayingChartRowsViewIndex;
            if (num < 0 || num >= ChartRowsView.Count)
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
                    num = ChartRowsView.Count - 1;
                }
                while (Settings.Default.FolderSkipPlayMode && num != -1)
                {
                    object candidateRow = ChartRowsView[num];
                    GridRowResolver.TryGetBmsPlayerFile(candidateRow, out BeMusicSeeker.Models.BMSFile bMSFile);
                    if (num == nowPlayingChartRowsViewIndex)
                    {
                        break;
                    }
                    string text2 = (bMSFile != null && !string.IsNullOrWhiteSpace(bMSFile.path) && File.Exists(bMSFile.path)) ? Path.GetDirectoryName(bMSFile.path) : num.ToString();
                    if (!string.IsNullOrWhiteSpace(text2) && text != text2)
                    {
                        break;
                    }
                    text = text2;
                    num--;
                    if (Settings.Default.RepeatPlayMode && num == -1)
                    {
                        num = ChartRowsView.Count - 1;
                    }
                }
            }
            if (num >= 0)
            {
                PlayEndBMSFile();
                PlayStartBmsFile(num);
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
            nowPlayingChartRowsViewIndex = -1;
        }
    }

    private void stopPlayingChartFiles(IEnumerable<ChartFile> charts)
    {
        if (!string.IsNullOrWhiteSpace(NowPlayingBMS?.path))
        {
            string playingDirectory = Path.GetDirectoryName(NowPlayingBMS.path);
            if ((charts ?? []).Any(chart => IsChartDirectoryUnder(playingDirectory, chart?.Path)))
            {
                PlayEndBMSFile(closeProcess: true);
            }
        }
    }

    private void stopPlayingLibraryCharts(IEnumerable<LibraryChartRef> charts)
    {
        if (!string.IsNullOrWhiteSpace(NowPlayingBMS?.path))
        {
            string playingDirectory = Path.GetDirectoryName(NowPlayingBMS.path);
            if ((charts ?? []).Any(chart => IsChartDirectoryUnder(playingDirectory, chart?.Path)))
            {
                PlayEndBMSFile(closeProcess: true);
            }
        }
    }

    private void stopPlayingChartDirectories(IEnumerable<string> directories)
    {
        if (!string.IsNullOrWhiteSpace(NowPlayingBMS?.path)
            && (directories ?? []).Any(directory => IsChartDirectorySameOrUnder(directory, NowPlayingBMS.path)))
        {
            PlayEndBMSFile(closeProcess: true);
        }
    }

    private static List<ChartFile> GetBmsFormatCharts(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private static List<LibraryChartRef> GetBmsLibraryChartRefs(IEnumerable<LibraryChartRef> charts)
    {
        return [.. (charts ?? []).Where(chart => chart?.Kind == LibraryChartKind.Bms)];
    }

    private static bool IsChartDirectoryUnder(string parentDirectory, string chartPath)
    {
        return IsChartDirectorySameOrUnder(parentDirectory, chartPath);
    }

    private static bool IsChartDirectorySameOrUnder(string parentDirectory, string chartPath)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(chartPath))
        {
            return false;
        }
        string chartDirectory = Path.GetDirectoryName(chartPath);
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return false;
        }
        string normalizedParent = NormalizeDirectoryForPrefixCheck(parentDirectory);
        string normalizedChartDirectory = NormalizeDirectoryForPrefixCheck(chartDirectory);
        return string.Equals(normalizedParent, normalizedChartDirectory, StringComparison.OrdinalIgnoreCase)
            || normalizedParent.StartsWith(normalizedChartDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryForPrefixCheck(string directoryPath)
    {
        try
        {
            return Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    internal void RestartPlayingBMSfileStart()
    {
        lock (lockThis)
        {
            bmsPlayer?.RestartPlayingBMSfile();
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
        bmsPlayer?.ShowInfo();
    }

    internal void uBMplayShowEffect()
    {
        bmsPlayer?.ShowEffect();
    }

    internal void uBMplayChangePlayside()
    {
        bmsPlayer?.ChangePlayside();
    }

    internal void uBMplayIncreaseHighSpeed()
    {
        bmsPlayer?.IncreaseHighSpeed();
    }

    internal void uBMplayDecreaseHighSpeed()
    {
        bmsPlayer?.DecreaseHighSpeed();
    }

    internal void uBMplayVolumeChanged()
    {
        bmsPlayer?.VolumeChanged();
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
        var stopwatch = Stopwatch.StartNew();
        scoreUpdateTargetCount = 0;
        entryResolveMs = 0L;
        scoreProbeMs = 0L;
        sourceMaterializeMs = 0L;
        scoreProbeMetrics = new PlaylistScoreProbeMetrics();
        if (bmsTable == null)
        {
            return [];
        }
        var entryHydrationStopwatch = Stopwatch.StartNew();
        tables?.EnsurePlaylistEntriesLoaded(bmsTable, "BuildPlaylistSourceRows");
        entryHydrationStopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, LibraryChartRef> chartsByMd5 = libraryIndexSnapshot?.ChartsByMd5 ?? new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LibraryChartRef> chartsBySha256 = libraryIndexSnapshot?.ChartsBySha256 ?? new Dictionary<string, LibraryChartRef>(StringComparer.OrdinalIgnoreCase);
        BeMusicSeeker.Models.BMSLibrary.ScoreSnapshot scoreSnapshot = files?.GetScoreSnapshotForDiagnostics();
        IReadOnlyDictionary<string, BeMusicSeeker.Models.BMSScore> scoresByHash = scoreSnapshot?.ActiveScoreSource == ActiveScoreSource.Lr2
            ? scoreSnapshot.ScoresByHash
            : new Dictionary<string, BeMusicSeeker.Models.BMSScore>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, BeMusicSeeker.Models.BMSScore> scoresBySha256 = scoreSnapshot?.ActiveScoreSource == ActiveScoreSource.Beatoraja
            ? scoreSnapshot.ScoresBySha256
            : new Dictionary<string, BeMusicSeeker.Models.BMSScore>(StringComparer.OrdinalIgnoreCase);
        cancellationStage = "hash_index";
        cancellationToken.ThrowIfCancellationRequested();
        List<(BMSTableEntry entry, LibraryChartRef resolvedChart)> resolvedEntries = [];
        cancellationStage = "entry_resolve";
        foreach (BMSTableEntry entry in bmsTable.GetEntriesExceptDummy())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.is_removed || (folderName != null && entry.folder != folderName))
            {
                continue;
            }
            LibraryChartRef resolvedChart = ResolveChartForPlaylistEntry(entry, chartsByMd5, chartsBySha256);
            bool isOwned = !string.IsNullOrWhiteSpace(resolvedChart?.Path);
            if (onlyNotOwned && isOwned)
            {
                continue;
            }
            resolvedEntries.Add((entry, resolvedChart));
        }
        cancellationToken.ThrowIfCancellationRequested();
        List<(BMSTableEntry entry, ChartFile resolvedChart, LR2SongDBExtended.chart_info entryChartInfo)> preparedEntries = new List<(BMSTableEntry, ChartFile, LR2SongDBExtended.chart_info)>(resolvedEntries.Count);
        var resolvedChartSnapshotCache = new Dictionary<LibraryChartRef, ChartFile>();
        var chartInfoLookupStopwatch = Stopwatch.StartNew();
        int missingChartInfoResolveTargets = 0;
        int chartInfoResolvedCount = 0;
        int chartInfoIndexVersion = files?.ChartInfoIndexVersion ?? 0;
        foreach ((BMSTableEntry entry, LibraryChartRef resolvedChartRef) in resolvedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChartFile resolvedChart = ResolvePlaylistChartSnapshot(resolvedChartRef, resolvedChartSnapshotCache);
            LR2SongDBExtended.chart_info entryChartInfo = null;
            if (resolvedChart == null)
            {
                missingChartInfoResolveTargets++;
                entryChartInfo = files?.ResolveChartInfo(entry.sha256, entry.md5);
                if (entryChartInfo != null)
                {
                    chartInfoResolvedCount++;
                }
            }
            preparedEntries.Add((entry, resolvedChart, entryChartInfo));
        }
        chartInfoLookupStopwatch.Stop();
        LogPlaylistWorker("playlist_chart_info_index_resolve entries=" + resolvedEntries.Count + " targets=" + missingChartInfoResolveTargets + " found=" + chartInfoResolvedCount + " version=" + chartInfoIndexVersion + " elapsedMs=" + chartInfoLookupStopwatch.ElapsedMilliseconds);
        entryResolveMs = stopwatch.ElapsedMilliseconds;

        cancellationToken.ThrowIfCancellationRequested();
        cancellationStage = "score_probe";
        var scoreProbeStopwatch = Stopwatch.StartNew();
        List<(BMSTableEntry entry, ChartFile resolvedChart, LR2SongDBExtended.chart_info entryChartInfo, BeMusicSeeker.Models.BMSScore scoreSnapshot)> scoredEntries = new(preparedEntries.Count);
        foreach ((BMSTableEntry entry, ChartFile resolvedChart, LR2SongDBExtended.chart_info entryChartInfo) in preparedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeMusicSeeker.Models.BMSScore scoreSnapshotForRow = ResolvePlaylistEntryScoreSnapshot(entry, resolvedChart, entryChartInfo, scoreSnapshot, scoresByHash, scoresBySha256);
            scoreUpdateTargetCount++;
            if (scoreSnapshotForRow != null)
            {
                scoreProbeMetrics.MatchedScoreCount++;
            }
            scoredEntries.Add((entry, resolvedChart, entryChartInfo, scoreSnapshotForRow));
        }
        scoreProbeStopwatch.Stop();
        scoreProbeMetrics.TargetCount = scoreUpdateTargetCount;
        scoreProbeMetrics.TotalMs = scoreProbeStopwatch.ElapsedMilliseconds;
        scoreProbeMs = scoreProbeStopwatch.ElapsedMilliseconds;
        var playlistRows = new List<PlaylistDetailSourceRow>(scoredEntries.Count);
        cancellationStage = "source_row_materialize";
        foreach ((BMSTableEntry entry, ChartFile resolvedChart, LR2SongDBExtended.chart_info entryChartInfo, BeMusicSeeker.Models.BMSScore scoreSnapshotForRow) in scoredEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            playlistRows.Add(new PlaylistDetailSourceRow(
                entry,
                resolvedChart,
                scoreSnapshotForRow,
                entryChartInfo,
                GetPlaylistReferenceDisplayForChart,
                TryGetSharedChartTransientState,
                ResolveChartInfoForProjection));
        }
        sourceMaterializeMs = stopwatch.ElapsedMilliseconds - entryResolveMs - scoreProbeMs;
        return playlistRows;
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
            return [];
        }
        List<PlaylistDetailRow> clones = [];
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

    private static List<PlaylistDetailSourceRow> ApplyPlaylistSourceRows(
        IReadOnlyList<PlaylistDetailSourceRow> sourceRows,
        string keywordFilter,
        ModeFilterType modeFilter,
        cSortParameters sortParameters,
        out string sortProfile,
        out int keywordCount,
        out int modeCount,
        out long keywordStageMs,
        out long modeStageMs,
        out long sortStageMs)
    {
        var stageStopwatch = Stopwatch.StartNew();
        IReadOnlyList<PlaylistDetailSourceRow> effectiveSourceRows = sourceRows ?? [];
        List<PlaylistDetailSourceRow> keywordRows;
        if (!string.IsNullOrWhiteSpace(keywordFilter))
        {
            var query = GridKeywordSearchQuery.Parse(keywordFilter);
            keywordRows = effectiveSourceRows.AsParallel().Where(delegate (PlaylistDetailSourceRow row)
            {
                return query.MatchesPlaylistDetail(row);
            }).ToList();
        }
        else
        {
            keywordRows = (effectiveSourceRows as List<PlaylistDetailSourceRow>) ?? [.. effectiveSourceRows];
        }
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;
        keywordCount = keywordRows.Count;

        stageStopwatch.Restart();
        List<PlaylistDetailSourceRow> modeRows;
        if (modeFilter != ModeFilterType.All)
        {
            List<int?> modeFlag = [null];
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
            modeRows = [.. keywordRows.Where(file => modeFlag.Contains(file.mode))];
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
        return sortedSourceRows;
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
        List<PlaylistDetailSourceRow> sortedSourceRows = ApplyPlaylistSourceRows(
            sourceRows,
            keywordFilter,
            modeFilter,
            sortParameters,
            out sortProfile,
            out keywordCount,
            out modeCount,
            out keywordStageMs,
            out modeStageMs,
            out sortStageMs);
        var stageStopwatch = Stopwatch.StartNew();
        List<PlaylistDetailRow> viewRows = CreatePlaylistViewRowsFromSource(sortedSourceRows);
        viewMaterializeMs = stageStopwatch.ElapsedMilliseconds;
        return viewRows;
    }

    internal static PlaylistDetailVirtualView ApplyPlaylistVirtualViewFromSource(IReadOnlyList<PlaylistDetailSourceRow> sourceRows, string keywordFilter, ModeFilterType modeFilter, cSortParameters sortParameters, out string sortProfile, out int keywordCount, out int modeCount, out long keywordStageMs, out long modeStageMs, out long sortStageMs, out long viewMaterializeMs)
    {
        var stageStopwatch = Stopwatch.StartNew();
        List<PlaylistDetailSourceRow> sortedSourceRows = ApplyPlaylistSourceRows(
            sourceRows,
            keywordFilter,
            modeFilter,
            sortParameters,
            out sortProfile,
            out keywordCount,
            out modeCount,
            out keywordStageMs,
            out modeStageMs,
            out sortStageMs);
        stageStopwatch.Restart();
        var viewRows = new PlaylistDetailVirtualView(
            sortedSourceRows,
            CountDistinctFoldersForPlaylistSourceRows(sortedSourceRows));
        viewMaterializeMs = stageStopwatch.ElapsedMilliseconds;
        return viewRows;
    }

    /// <summary>
    /// 現在保持している playlist source snapshot から view を再計算します。
    /// </summary>
    private IList ApplyPlaylistViewFromCurrentSource(viewUpdateMode mode, out int sourceCount, out int keywordCount, out int modeCount, out long keywordStageMs, out long modeStageMs, out long sortStageMs, out string sortProfile)
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
        sourceRows ??= [];
        sourceCount = sourceRows.Count;
        LogPlaylistViewApply("started mode=" + mode + " sourceGenerationId=" + sourceGenerationId + " sourceCount=" + sourceCount + " playlistSourceRowCount=" + CountPlaylistSourceRows(sourceRows) + " playlistViewRowCount=" + currentViewRowsAlive);
        IList finalRows = ApplyPlaylistVirtualViewFromSource(sourceRows, KeywordFilter, ModeFilter, SortParameters, out sortProfile, out keywordCount, out modeCount, out keywordStageMs, out modeStageMs, out sortStageMs, out long _);
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
        var viewBuildStopwatch = Stopwatch.StartNew();
        int sourceCount = 0;
        var scoreProbeMetrics = new PlaylistScoreProbeMetrics();
        List<PlaylistDetailSourceRow> sourceRows = null;
        IList finalRows = null;
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
            long stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            PlaylistLibraryIndexSnapshot libraryIndexSnapshot = GetOrCreatePlaylistLibraryIndexSnapshot(cancellationToken, out string libraryIndexAccess, out long libraryIndexBuildMs);
            long libraryIndexMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
            stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            sourceRows = BuildPlaylistSourceRows(bmsTable, folderName, onlyNotOwned, libraryIndexSnapshot, requestVersion, cancellationToken, ref cancellationStage, out int scoreUpdateTargetCount, out long entryResolveMs, out long scoreProbeMs, out long sourceMaterializeMs, out scoreProbeMetrics);
            long folderStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
            int folderCount = sourceRows.Count;
            sourceCount = folderCount;
            LogPlaylistWorker("playlist_score_probe_summary requestVersion=" + requestVersion + " targetCount=" + scoreProbeMetrics.TargetCount + " matchedScoreCount=" + scoreProbeMetrics.MatchedScoreCount + " totalMs=" + scoreProbeMetrics.TotalMs);
            if (scoreProbeMetrics.TotalMs >= PlaylistScoreProbeSlowLogThresholdMs)
            {
                LogPlaylistWorker("playlist_score_probe_detail requestVersion=" + requestVersion + " targetCount=" + scoreProbeMetrics.TargetCount + " matchedScoreCount=" + scoreProbeMetrics.MatchedScoreCount + " totalMs=" + scoreProbeMetrics.TotalMs + " thresholdMs=" + PlaylistScoreProbeSlowLogThresholdMs);
            }
            if (cancellationToken.IsCancellationRequested || !IsLatestPlaylistSourceBuildRequest(requestVersion))
            {
                LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=after_build mode=" + mode + " sourceCount=" + sourceCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
                return true;
            }
            cancellationStage = "view_apply";
            LogPlaylistViewApply("started mode=" + mode + " sourceCount=" + sourceCount + " playlistSourceRowCount=" + CountPlaylistSourceRows(sourceRows) + " playlistViewRowCount=" + CountPlaylistDetailRows(playlistViewState.CurrentViewRows));
            finalRows = ApplyPlaylistVirtualViewFromSource(sourceRows, KeywordFilter, ModeFilter, SortParameters, out string sortProfile, out int keywordCount, out int modeCount, out long keywordStageMs, out long modeStageMs, out long sortStageMs, out long viewMaterializeMs);
            int viewCount = finalRows.Count;
            LogPlaylistViewApply("completed mode=" + mode + " sourceCount=" + sourceCount + " keywordCount=" + keywordCount + " modeCount=" + modeCount + " viewCount=" + viewCount + " sortProfile=" + sortProfile + " playlistSourceRowCount=" + CountPlaylistSourceRows(sourceRows) + " playlistViewRowCount=" + CountPlaylistDetailRows(finalRows));
            if (cancellationToken.IsCancellationRequested || !IsLatestPlaylistSourceBuildRequest(requestVersion))
            {
                LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=after_apply mode=" + mode + " sourceCount=" + sourceCount + " viewCount=" + viewCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
                return true;
            }
            cancellationStage = "ui_apply";
            List<PlaylistDetailSourceRow> previousSourceRows = ReplacePlaylistSourceRows(sourceRows, bmsTable, folderName, filterType, request.Identity);
            sourceRows = null;
            SelectedIndexChartRowsView = -1;
            base.Messenger.Raise(new InteractionMessage("PrepareMainTableSwap"));
            stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            loadColumnSetting(ResolvePlaylistColumnSettingMode(filterType));
            long columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
            stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            long callbackStageMs = 0L;
            ReplacePlaylistViewRows(finalRows, request.Identity);
            SetChartRowsView(finalRows);
            TryMarkPlaylistOpenBuildCompleted(request, viewCount);
            finalRows = null;
            int disposedSourceRowsCount = CountPlaylistSourceRows(previousSourceRows);
            previousSourceRows = null;
            FinalizeMainViewBuild(viewBuildStopwatch, mode, requestedMode, parameter, folderStageMs, keywordStageMs, modeStageMs, sortStageMs, sortReuse: false, sortProfile, folderCount, keywordCount, modeCount, viewCount, columnStageMs, callbackStageMs);
            LogPlaylistSourceBuild("completed version=" + requestVersion + " mode=" + mode + " sourceCount=" + sourceCount + " viewCount=" + viewCount + " disposedSourceRows=" + disposedSourceRowsCount + " scoreTargets=" + scoreUpdateTargetCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown") + " libraryIndexMs=" + libraryIndexMs + " libraryIndexAccess=" + libraryIndexAccess + " libraryIndexBuildMs=" + libraryIndexBuildMs + " entryResolveMs=" + entryResolveMs + " scoreProbeMs=" + scoreProbeMs + " scoreProbeMatchedScoreCount=" + scoreProbeMetrics.MatchedScoreCount + " sourceMaterializeMs=" + sourceMaterializeMs + " viewMaterializeMs=" + viewMaterializeMs + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds);
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
        var viewBuildStopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        IList finalRows = ApplyPlaylistViewFromCurrentSource(mode, out int sourceCount, out int keywordCount, out int modeCount, out long keywordStageMs, out long modeStageMs, out long sortStageMs, out string sortProfile);
        int viewCount = finalRows.Count;
        if (cancellationToken.IsCancellationRequested || !IsLatestPlaylistSourceBuildRequest(request.RequestVersion))
        {
            return true;
        }
        SelectedIndexChartRowsView = -1;
        base.Messenger.Raise(new InteractionMessage("PrepareMainTableSwap"));
        long stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        loadColumnSetting(ResolvePlaylistColumnSettingMode(request.Identity.FilterType));
        long columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        long callbackStageMs = 0L;
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
        var stopwatch = Stopwatch.StartNew();
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
        if (existing.updated_at == default || incoming.updated_at == default)
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
    /// 指定された更新モードとパラメータに基づいて、メインの chart row 表示用コレクションを生成・更新します。
    /// ツリーでのフォルダ選択、プレイリストや難易度表の適用、Missingファイル等の保守フィルタ、およびキーワードやキーモードでの絞り込み等を行います。<br/>
    /// このメソッドの実行には、規模に応じて時間がかかるため内部でタイマー計測し遅延を制御・ロギングする機構が含まれています。
    /// </summary>
    /// <param name="mode">更新の契機（どのフィルタや要素が変更されたかを示す更新モード）。</param>
    /// <param name="parameter">選択されたプレイリスト（BMSTable）やフォルダ名などの追加パラメータ、無い場合は null。</param>
    private void RefreshChartRowsView(viewUpdateMode mode, object parameter = null)
    {
        var viewBuildStopwatch = Stopwatch.StartNew();
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
        MainViewOperationSection previousOperationSection = CurrentMainViewOperationSection;
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
        if (previousOperationSection != CurrentMainViewOperationSection)
        {
            RaisePropertyChanged(() => CurrentMainViewOperationSection);
            RaisePropertyChanged(() => CurrentMainViewChartOperationSourceScope);
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
        ClearPlaylistSourceRows();
        if (includeBmsonRows)
        {
            BmsonLibraryRowCacheSyncResult bmsonSyncResult = SyncBmsonLibraryRowCache(files?.BmsonSongs);
            if (bmsonSyncResult.SortKeyChanged)
            {
                InvalidateNormalLibrarySortKeysForBmsonSync(bmsonSyncResult);
            }
            if (bmsonSyncResult.SourceChanged)
            {
                IncrementNormalLibrarySourceGeneration(ResolveBmsonSourceGenerationReason(bmsonSyncResult, "bmson"));
            }
        }
        if (TryApplyVirtualDefaultNormalLibraryView(mode, requestedMode, parameter, includeBmsonRows, viewBuildStopwatch))
        {
            return;
        }
        if (TryApplyVirtualChartSubsetLibraryView(mode, requestedMode, parameter, viewBuildStopwatch))
        {
            return;
        }
        if (ShouldRebuildRegularFolderStage(mode, ChartRowsFolderView, ChartRowsKeywordFilterView, ChartRowsModeFilterView, treeViewFilterTypeSelected))
        {
            mode = treeViewFilterTypeSelected;
            parameter = treeViewFilterParameterSelected;
        }
        switch (mode)
        {
            case viewUpdateMode.FolderFilterSelected:
                LibraryRowCacheBuildStats folderRowCacheStats = CreateRegularRowCacheBuildStats();
                ChartRowsFolderView = BuildStandardLibraryRowsForView(
                    CreateStandardLibraryChartSnapshot(BMSFiles, includeBmsonRows ? files?.BmsonSongs : null),
                    virtualNormalLibraryTreeFilter,
                    chart => GetOrCreateStandardLibraryRow(chart, folderRowCacheStats),
                    folderRowCacheStats,
                    out LibraryRowsBuildMetrics folderMetrics);
                LogMainViewFolderDetail(mode, folderMetrics);
                break;
            case viewUpdateMode.FullScanAllChartsFilterSelected:
                LibraryRowCacheBuildStats fullScanRowCacheStats = CreateRegularRowCacheBuildStats();
                ChartRowsFolderView = BuildStandardLibraryRowsForView(
                    CreateStandardLibraryChartSnapshot(BMSFiles, includeBmsonRows ? files?.BmsonSongs : null),
                    null,
                    chart => GetOrCreateStandardLibraryRow(chart, fullScanRowCacheStats),
                    fullScanRowCacheStats,
                    out LibraryRowsBuildMetrics fullScanMetrics);
                LogMainViewFolderDetail(mode, fullScanMetrics);
                LogResourceHealthProjection(mode, ChartRowsFolderView);
                break;
            case viewUpdateMode.FileMissingFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(ChartFilesNeedResourceFix, CreateLibraryChartRowWithResourceHealthProjection);
                LogResourceHealthProjection(mode, ChartRowsFolderView);
                break;
            case viewUpdateMode.FileMissingIgnoredFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(ChartFilesNeedResourceFixIgnored, CreateLibraryChartRowWithResourceHealthProjection);
                LogResourceHealthProjection(mode, ChartRowsFolderView);
                break;
            case viewUpdateMode.DuplicateFilterSelected:
                if (DuplicateChartGroups == null)
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
                            DuplicateGroup duplicateGroup = DuplicateChartGroups.FirstOrDefault(group => string.Equals(group.Header, duplicateContext.Value, StringComparison.Ordinal));
                            ChartRowsFolderView = ToLibraryChartRows((duplicateGroup != null) ? duplicateGroup.ChartFiles : DuplicateChartGroups.SelectMany(g => g.ChartFiles));
                        }
                        else
                        {
                            string dirname2 = duplicateContext.Value;
                            RetryHelper.RetryIfError(delegate
                            {
                                ChartRowsFolderView = ToLibraryChartRows(from chart in DuplicateChartGroups.SelectMany(g => g.ChartFiles)
                                                                         where !string.IsNullOrWhiteSpace(chart.Path) && chart.Path.StartsWith(dirname2 + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                                                         select chart);
                            }, delegate (Exception ex)
                            {
                                ExceptionDispatchInfo.Capture(ex).Throw();
                            }, delegate
                            {
                                Thread.Sleep(100);
                            }, 100u);
                        }
                    }
                    else if (parameter is DuplicateGroup)
                    {
                        ChartRowsFolderView = ToLibraryChartRows((parameter as DuplicateGroup).ChartFiles);
                    }
                    else
                    {
                        if (parameter is not string)
                        {
                            break;
                        }
                        string dirname = parameter as string;
                        RetryHelper.RetryIfError(delegate
                        {
                            ChartRowsFolderView = ToLibraryChartRows(from chart in DuplicateChartGroups.SelectMany(g => g.ChartFiles)
                                                                     where !string.IsNullOrWhiteSpace(chart.Path) && chart.Path.StartsWith(dirname + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                                                     select chart);
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
                    ChartRowsFolderView = ToLibraryChartRows(DuplicateChartGroups.SelectMany(g => g.ChartFiles));
                }, delegate (Exception ex)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 100u);
                break;
            case viewUpdateMode.GarbledFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(CreateBmsChartSnapshot(BMSFilesGarbled), CreateBmsLibraryChartRowFromChart);
                break;
            case viewUpdateMode.GarbleFixedFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(CreateBmsChartSnapshot(BMSFilesGarbleFixed), CreateBmsLibraryChartRowFromChart);
                break;
            case viewUpdateMode.UnregisteredFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(CreateBmsChartSnapshot(BMSFilesUnregistered), CreateBmsLibraryChartRowFromChart);
                break;
            case viewUpdateMode.ZeroNoteFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(ChartFilesZeroNote, CreateBmsLibraryChartRowFromChart);
                break;
            case viewUpdateMode.ChartInfoParseErrorFilterSelected:
                ChartRowsFolderView = ToLibraryChartRows(ChartInfoParseFailedChartFiles);
                break;
            case viewUpdateMode.NewlyInstalledFolderSelected:
                if (ChartPackagesInstalled == null)
                {
                    ChartRowsFolderView = null;
                    break;
                }
                if (parameter != null && parameter is ChartPackage)
                {
                    ChartRowsFolderView = ToLibraryChartRows((parameter as ChartPackage)?.ChartEntries, CreateLibraryChartRowFromPackageEntryWithResourceHealthProjection);
                    break;
                }
                RetryHelper.RetryIfError(delegate
                {
                    ChartRowsFolderView = ToLibraryChartRows(CreatePackageChartEntrySnapshot(ChartPackagesInstalled), CreateLibraryChartRowFromPackageEntryWithResourceHealthProjection);
                }, delegate (Exception ex)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 100u);
                break;
            case viewUpdateMode.PendingInstallFolderSelected:
                if (ChartPackagesPending == null)
                {
                    ChartRowsFolderView = null;
                    break;
                }
                if (parameter != null && parameter is ChartPackage)
                {
                    ChartRowsFolderView = ToLibraryChartRows((parameter as ChartPackage)?.ChartEntries, CreateLibraryChartRowFromPackageEntry);
                    break;
                }
                RetryHelper.RetryIfError(delegate
                {
                    ChartRowsFolderView = ToLibraryChartRows(CreatePackageChartEntrySnapshot(ChartPackagesPending), CreateLibraryChartRowFromPackageEntry);
                }, delegate (Exception ex)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 100u);
                break;
        }
        ChartRowsFolderView = ((ChartRowsFolderView == null) ? new List<LibraryChartRow>() : [.. ChartRowsFolderView]);
        folderStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        folderCount = ChartRowsFolderView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        if (mode <= viewUpdateMode.KeywordFilterUpdated)
        {
            if (!string.IsNullOrWhiteSpace(KeywordFilter))
            {
                ChartRowsKeywordFilterView = [];
                var query = GridKeywordSearchQuery.Parse(KeywordFilter);
                ChartRowsKeywordFilterView = from r in ChartRowsFolderView.AsParallel()
                                             where query.MatchesLibraryChartRow(r)
                                             select r;
            }
            else
            {
                ChartRowsKeywordFilterView = ChartRowsFolderView;
            }
        }
        ChartRowsKeywordFilterView = ((ChartRowsKeywordFilterView == null) ? new List<LibraryChartRow>() : [.. ChartRowsKeywordFilterView]);
        keywordStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        keywordCount = ChartRowsKeywordFilterView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        if (mode <= viewUpdateMode.ModeFilterUpdated)
        {
            if (ModeFilter != ModeFilterType.All)
            {
                List<int?> modeFlag = [null];
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
                ChartRowsModeFilterView = ChartRowsKeywordFilterView.Where(f => modeFlag.Contains(f.mode));
            }
            else
            {
                ChartRowsModeFilterView = ChartRowsKeywordFilterView;
            }
        }
        ChartRowsModeFilterView = ((ChartRowsModeFilterView == null) ? new List<LibraryChartRow>() : [.. ChartRowsModeFilterView]);
        modeStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
        modeCount = ChartRowsModeFilterView.Count();
        stageStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        IList nextRowsView;
        if (mode <= viewUpdateMode.SortUpdated)
        {
            bool useLegacySortForMainView = false;
            bool isPlaylistDetailView = mode == viewUpdateMode.PlaylistFilterSelected || mode == viewUpdateMode.PlaylistNotOwnedFilterSelected || treeViewFilterTypeSelected == viewUpdateMode.PlaylistFilterSelected || treeViewFilterTypeSelected == viewUpdateMode.PlaylistNotOwnedFilterSelected;
            string columnName = nameof(LibraryChartRow.Title);
            ListSortDirection direction = ListSortDirection.Ascending;
            if (SortParameters != null)
            {
                direction = SortParameters.Direction;
                columnName = SortParameters.ColumnsName;
            }
            if (string.IsNullOrWhiteSpace(columnName))
            {
                columnName = nameof(LibraryChartRow.Title);
            }
            if (string.Equals(columnName, nameof(LibraryChartRow.rank), StringComparison.Ordinal))
            {
                columnName = nameof(LibraryChartRow.rateDouble);
            }
            bool isTreeSelectionRequest = requestedMode != viewUpdateMode.TreeViewFilterNotChanged && requestedMode < viewUpdateMode.KeywordFilterUpdated;
            bool isFolderMode = mode == viewUpdateMode.FolderFilterSelected;
            var modeFilterList = ChartRowsModeFilterView as List<LibraryChartRow>;
            bool isFullNormalLibraryResult = treeViewFilterTypeSelected == viewUpdateMode.FolderFilterSelected
                && virtualNormalLibraryTreeFilter == null
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
                var sortCacheStopwatch = Stopwatch.StartNew();
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
                    folderSortSourceSnapshot = [.. ChartRowsModeFilterView];
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
        long prepareSwapMs = 0L;
        if (!ReferenceEquals(ChartRowsView, nextRowsView))
        {
            long prepareStartMs = viewBuildStopwatch.ElapsedMilliseconds;
            base.Messenger.Raise(new InteractionMessage("PrepareMainTableSwap"));
            prepareSwapMs = viewBuildStopwatch.ElapsedMilliseconds - prepareStartMs;
        }
        long columnSettingStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        bool columnSettingReuse = ApplyMainColumnSettingForViewUpdate(mode);
        long columnSettingMs = viewBuildStopwatch.ElapsedMilliseconds - columnSettingStartMs;
        long setViewStartMs = viewBuildStopwatch.ElapsedMilliseconds;
        SetChartRowsView(nextRowsView);
        long setViewMs = viewBuildStopwatch.ElapsedMilliseconds - setViewStartMs;
        columnStageMs = viewBuildStopwatch.ElapsedMilliseconds - stageStartMs;
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
        LogMainViewBuild("main_view_build mode=" + mode + " requestedMode=" + requestedMode + " parameterType=" + parameterType + " folderMs=" + folderStageMs + " keywordMs=" + keywordStageMs + " modeMs=" + modeStageMs + " sortMs=" + sortStageMs + " sortReuse=" + sortReuse + " sortProfile=" + sortProfile + " sortEngine=fast fastSortEnabled=" + fastSortEnabled + " isPlaylistDetailView=" + isPlaylistDetailForLog + " columnMs=" + columnStageMs + " prepareSwapMs=" + prepareSwapMs + " columnSettingMs=" + columnSettingMs + " setViewMs=" + setViewMs + " columnSettingReuse=" + columnSettingReuse + " callbackMs=" + callbackStageMs + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds + " folderCount=" + folderCount + " keywordCount=" + keywordCount + " modeCount=" + modeCount + " viewCount=" + viewCount + " sortColumn=" + sortColumn + " sortDirection=" + sortDirection);
    }

    internal static List<LibraryChartRow> BuildStandardLibraryRowsForView(
        IEnumerable<ChartFile> charts,
        NormalLibraryTreeFilter folderFilter,
        Func<ChartFile, LibraryChartRow> rowFactory,
        LibraryRowCacheBuildStats rowCacheStats,
        out LibraryRowsBuildMetrics metrics)
    {
        var totalStopwatch = Stopwatch.StartNew();
        long regularFilterMs = 0L;
        long bmsonFilterMs = 0L;
        long regularRowMaterializeMs;
        long bmsonRowMaterializeMs = 0L;
        long concatToListMs;
        List<ChartFile> sourceCharts = [.. (charts ?? []).Where(chart => chart != null)];
        int sourceBmsCount = sourceCharts.Count(chart => chart.Kind == ChartFileKind.Bms);
        int sourceBmsonCount = sourceCharts.Count(chart => chart.Kind == ChartFileKind.Bmson);
        IEnumerable<ChartFile> regularCharts = sourceCharts.Where(chart => chart.Kind == ChartFileKind.Bms);
        IEnumerable<ChartFile> bmsonCharts = sourceCharts.Where(chart => chart.Kind == ChartFileKind.Bmson);
        if (folderFilter != null)
        {
            var filterStopwatch = Stopwatch.StartNew();
            regularCharts = regularCharts.AsParallel().Where(folderFilter.Matches);
            List<ChartFile> filteredRegularRows = [.. regularCharts];
            filterStopwatch.Stop();
            regularFilterMs = filterStopwatch.ElapsedMilliseconds;
            regularCharts = filteredRegularRows;

            filterStopwatch.Restart();
            bmsonCharts = bmsonCharts.AsParallel().Where(folderFilter.Matches);
            List<ChartFile> filteredBmsonRows = [.. bmsonCharts];
            filterStopwatch.Stop();
            bmsonFilterMs = filterStopwatch.ElapsedMilliseconds;
            bmsonCharts = filteredBmsonRows;
        }

        var materializeStopwatch = Stopwatch.StartNew();
        List<LibraryChartRow> regularLibraryRows = MaterializeLibraryChartRows(regularCharts, rowFactory ?? (chart => LibraryChartRow.FromChartFile(chart)));
        materializeStopwatch.Stop();
        regularRowMaterializeMs = materializeStopwatch.ElapsedMilliseconds;

        materializeStopwatch.Restart();
        List<LibraryChartRow> bmsonLibraryRows = MaterializeLibraryChartRows(bmsonCharts, rowFactory ?? (chart => LibraryChartRow.FromChartFile(chart)));
        materializeStopwatch.Stop();
        bmsonRowMaterializeMs = materializeStopwatch.ElapsedMilliseconds;

        var concatStopwatch = Stopwatch.StartNew();
        List<LibraryChartRow> rows = [.. regularLibraryRows, .. bmsonLibraryRows];
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
        var stats = new LibraryRowCacheBuildStats
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

    private void LogResourceHealthProjection(viewUpdateMode mode, IEnumerable<LibraryChartRow> rows)
    {
        LogResourceHealthProjection(mode, CountIfCheap(rows));
    }

    private void LogResourceHealthProjection(viewUpdateMode mode, int rowCount)
    {
        ResourceHealthIndexSnapshot snapshot = files?.TryGetCurrentResourceHealthIndexSnapshotForView();
        int overlayCount = 0;
        if (snapshot != null)
        {
            overlayCount = mode == viewUpdateMode.FileMissingFilterSelected
                ? snapshot.ActiveTargets.Count
                : mode == viewUpdateMode.FileMissingIgnoredFilterSelected
                    ? snapshot.IgnoredTargets.Count
                    : snapshot.NeedFixCount;
        }
        LogMainViewBuild("resource_health_projection reason=" + mode
            + " rowCount=" + rowCount
            + " overlayCount=" + overlayCount
            + " ignored=" + (snapshot?.IgnoredCount ?? 0)
            + " version=" + (snapshot?.Version ?? 0));
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
            + " orderCacheLookupMs=" + metrics.OrderCacheLookupMs
            + " orderBuildMs=" + metrics.OrderBuildMs
            + " sortMs=" + metrics.SortMs);
    }

    private static bool TryNormalizeNormalLibrarySortCacheColumn(string columnName, out string normalizedColumnName)
    {
        return ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out normalizedColumnName);
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
        string sortProfile = "library_chart_string_fast_ordinal_ignore_case";
        string stringSortKind = "ordinal_ignore_case";
        string propertyTypeName = nameof(String);
        if (ChartListOrder.TryGetVirtualSortColumnMetadata(cacheKey.ColumnName, out ChartListOrderColumnMetadata metadata))
        {
            propertyTypeName = metadata.PropertyTypeName;
            stringSortKind = metadata.StringSortKind;
            if (metadata.KeyKind == ChartListOrderKeyKind.Comparable)
            {
                sortProfile = "library_chart_typed";
            }
        }

        return new LibraryChartSortMetrics(
            cacheKey.RowCount,
            cacheKey.ColumnName,
            cacheKey.Direction,
            propertyTypeName,
            sortProfile,
            stringSortKind,
            sortMs,
            sortReuse: cacheHit,
            sortCacheKey: cacheKey.ColumnName,
            sortCacheGeneration: cacheKey.SortKeyGeneration,
            sortCacheHit: cacheHit);
    }

    private List<LibraryChartRow> ToLibraryChartRows(IEnumerable<ChartFile> charts)
    {
        List<LibraryChartRow> rows = [.. (charts ?? [])
            .Select(chart => LibraryChartRow.FromChartFile(chart))
            .Where(row => row != null)];
        ApplyLibraryChartRowProviders(rows);
        return rows;
    }

    private List<LibraryChartRow> ToLibraryChartRows(IEnumerable<ChartFile> charts, Func<ChartFile, LibraryChartRow> rowFactory)
    {
        List<LibraryChartRow> rows = MaterializeLibraryChartRows(charts, rowFactory);
        ApplyLibraryChartRowProviders(rows);
        return rows;
    }

    private static List<LibraryChartRow> MaterializeLibraryChartRows(IEnumerable<ChartFile> charts, Func<ChartFile, LibraryChartRow> rowFactory)
    {
        return [.. (charts ?? [])
            .Select(rowFactory ?? (chart => LibraryChartRow.FromChartFile(chart)))
            .Where(row => row != null)];
    }

    private List<LibraryChartRow> ToLibraryChartRows(IEnumerable<PackageChartEntry> entries)
    {
        return ToLibraryChartRows(entries, LibraryChartRow.FromPackageChartEntry);
    }

    private List<LibraryChartRow> ToLibraryChartRows(IEnumerable<PackageChartEntry> entries, Func<PackageChartEntry, LibraryChartRow> rowFactory)
    {
        List<LibraryChartRow> rows = [.. (entries ?? [])
            .Select(rowFactory ?? LibraryChartRow.FromPackageChartEntry)
            .Where(row => row != null)];
        ApplyLibraryChartRowProviders(rows);
        return rows;
    }

    private LibraryChartRow CreateBmsLibraryChartRowFromChart(ChartFile chart)
    {
        LibraryChartRow row = LibraryChartRow.FromChartFile(chart);
        ApplyLibraryChartRowProviders(row);
        return row;
    }

    private LibraryChartRow CreateLibraryChartRowFromPackageEntry(PackageChartEntry entry)
    {
        LibraryChartRow row = LibraryChartRow.FromPackageChartEntry(entry);
        ApplyLibraryChartRowProviders(row);
        return row;
    }

    private LibraryChartRow CreateLibraryChartRowFromPackageEntryWithResourceHealthProjection(PackageChartEntry entry)
    {
        return CreateLibraryChartRowFromPackageEntry(entry);
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
        if (parameter == null || parameter is DuplicateViewContext)
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

    private BmsonLibraryRowCacheSyncResult SyncBmsonLibraryRowCache(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        List<LR2SongDBExtended.bmson_song> snapshot = [.. (bmsonSongs ?? [])
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))
            .OrderBy(song => song.path, StringComparer.OrdinalIgnoreCase)];
        PruneSharedChartTransientStateCacheToCurrentStorageRows(BMSFiles, snapshot);
        return regularBmsLibraryRowCache.SyncBmsonRows(snapshot, ApplyLibraryChartRowProviders);
    }

    internal static bool HasBmsonLibrarySortKeyChangedForTest(LibraryChartRow row, LR2SongDBExtended.bmson_song nextSong)
    {
        return NormalLibraryRowCache.HasBmsonLibrarySortKeyChangedForTest(row, nextSong);
    }

    internal static bool HasBmsonLibrarySortKeyChangedForTest(LibraryChartRow row, Action<LR2SongDBExtended.bmson_song> mutateCurrentSong)
    {
        return NormalLibraryRowCache.HasBmsonLibrarySortKeyChangedForTest(row, mutateCurrentSong);
    }

    internal static bool HasBmsonLibrarySourceIdentityChangedForTest(LibraryChartRow row, LR2SongDBExtended.bmson_song nextSong)
    {
        return NormalLibraryRowCache.HasBmsonLibrarySourceIdentityChangedForTest(row, nextSong);
    }

    internal static bool HasBmsonLibrarySourceIdentityChangedForTest(LibraryChartRow row, Action<LR2SongDBExtended.bmson_song> mutateCurrentSong)
    {
        return NormalLibraryRowCache.HasBmsonLibrarySourceIdentityChangedForTest(row, mutateCurrentSong);
    }

    internal static IReadOnlyList<string> GetBmsonLibrarySortKeySnapshotColumnNamesForTest()
    {
        return NormalLibraryRowCache.GetBmsonLibrarySortKeySnapshotColumnNamesForTest();
    }

    internal static IReadOnlyList<string> GetBmsonLibrarySourceIdentitySnapshotColumnNamesForTest()
    {
        return NormalLibraryRowCache.GetBmsonLibrarySourceIdentitySnapshotColumnNamesForTest();
    }

    internal static bool IsNormalLibraryVirtualSortKeyPropertyForTest(string propertyName)
    {
        return ChartListOrder.TryNormalizeVirtualSortColumn(propertyName, out _);
    }

    private void SyncBmsonLibraryRowCacheWithoutRebuild(string reason = "bmson_sync_without_rebuild")
    {
        BmsonLibraryRowCacheSyncResult result = SyncBmsonLibraryRowCache(files?.BmsonSongs);
        if (result.SortKeyChanged)
        {
            InvalidateNormalLibrarySortKeysForBmsonSync(result, reason + "_sort_key_changed");
        }
        if (result.SourceChanged)
        {
            string sourceReason = result.MembershipChanged
                ? reason + "_membership_changed"
                : (result.SourceIdentityChanged ? NormalLibraryBmsonSourceIdentityChangedReason : reason + "_source_reference_changed");
            IncrementNormalLibrarySourceGeneration(sourceReason);
        }
        if (result.SortKeyChanged || result.SourceChanged)
        {
            LogMainViewBuild("normal_library_bmson_sync reason=" + (reason ?? string.Empty)
                + " membershipChanged=" + result.MembershipChanged
                + " sourceIdentityChanged=" + result.SourceIdentityChanged
                + " sourceReferenceChanged=" + result.SourceReferenceChanged
                + " sortKeyChanged=" + result.SortKeyChanged);
        }
    }

    private static string ResolveBmsonSourceGenerationReason(BmsonLibraryRowCacheSyncResult result, string reasonPrefix)
    {
        string prefix = string.IsNullOrWhiteSpace(reasonPrefix) ? "bmson" : reasonPrefix;
        if (result.MembershipChanged)
        {
            return prefix + "_membership_changed";
        }
        if (result.SourceIdentityChanged)
        {
            return NormalLibraryBmsonSourceIdentityChangedReason;
        }
        return prefix + "_source_reference_changed";
    }

    public void LoadColumnSetting()
    {
        loadColumnSetting(viewUpdateMode.TreeViewFilterNotChanged, isInit: true);
    }

    private bool ApplyMainColumnSettingForViewUpdate(viewUpdateMode mode)
    {
        viewUpdateMode resolvedMode = ResolveMainColumnSettingMode(mode, treeViewFilterTypeSelected);
        bool targetSettingsReady = IsMainColumnSettingTargetReady(resolvedMode);
        bool playlistSummarySettingsReady = Settings.Default.PlaylistSummaryColumnsSettings != null;
        if (CanReuseMainColumnSetting(resolvedMode, lastAppliedMainColumnSettingMode, targetSettingsReady, playlistSummarySettingsReady, isInit: false))
        {
            return true;
        }
        loadColumnSetting(resolvedMode);
        return false;
    }

    private static bool CanReuseMainColumnSetting(
        viewUpdateMode resolvedMode,
        viewUpdateMode? lastAppliedMode,
        bool targetSettingsReady,
        bool playlistSummarySettingsReady,
        bool isInit)
    {
        return !isInit
            && lastAppliedMode.HasValue
            && lastAppliedMode.Value == resolvedMode
            && targetSettingsReady
            && playlistSummarySettingsReady;
    }

    private bool IsMainColumnSettingTargetReady(viewUpdateMode mode)
    {
        return mode switch
        {
            viewUpdateMode.PlaylistFilterSelected or viewUpdateMode.PlaylistNotOwnedFilterSelected => Settings.Default.PlaylistCustomTableColumnSettings != null,
            viewUpdateMode.FolderFilterSelected => Settings.Default.StandardCustomTableColumnSettings != null,
            viewUpdateMode.UnregisteredFilterSelected => Settings.Default.UnregisteredCustomTableColumnSettings != null,
            viewUpdateMode.ZeroNoteFilterSelected => Settings.Default.ZeroNoteCustomTableColumnSettings != null,
            viewUpdateMode.ChartInfoParseErrorFilterSelected => Settings.Default.ChartInfoParseErrorCustomTableColumnSettings != null,
            viewUpdateMode.FileMissingFilterSelected or viewUpdateMode.FileMissingIgnoredFilterSelected or viewUpdateMode.FullScanAllChartsFilterSelected or viewUpdateMode.NewlyInstalledFolderSelected => Settings.Default.FullScanCustomTableColumnSettings != null,
            viewUpdateMode.DuplicateFilterSelected => Settings.Default.DuplicateCustomTableColumnSettings != null,
            viewUpdateMode.GarbledFilterSelected or viewUpdateMode.GarbleFixedFilterSelected => Settings.Default.EncodingCustomTableColumnSettings != null,
            viewUpdateMode.PendingInstallFolderSelected => Settings.Default.InstallCustomTableColumnSettings != null,
            _ => false,
        };
    }

    private void loadColumnSetting(viewUpdateMode mode, bool isInit = false)
    {
        var stopwatch = Stopwatch.StartNew();
        long stageStartMs = stopwatch.ElapsedMilliseconds;
        long caseEnsureMs = 0L;
        long caseAssignMs = 0L;
        Visibility targetColumnSettingsVisibilityForPlaylist = Visibility.Collapsed;
        string caseLabel = "none";

        if (mode == viewUpdateMode.TreeViewFilterNotChanged)
        {
            mode = ResolveMainColumnSettingMode(mode, treeViewFilterTypeSelected);
        }
        long normalizeMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        bool modeHandled = true;
        switch (mode)
        {
            case viewUpdateMode.PlaylistFilterSelected:
            case viewUpdateMode.PlaylistNotOwnedFilterSelected:
                caseLabel = "playlist";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.PlaylistCustomTableColumnSettings == null)
                {
                    Settings.Default.PlaylistCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.PlaylistCustomTableColumnSettings;
                targetColumnSettingsVisibilityForPlaylist = Visibility.Visible;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.FolderFilterSelected:
                caseLabel = "standard";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.StandardCustomTableColumnSettings == null)
                {
                    Settings.Default.StandardCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.StandardCustomTableColumnSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.UnregisteredFilterSelected:
                caseLabel = "unregistered";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.UnregisteredCustomTableColumnSettings == null)
                {
                    Settings.Default.UnregisteredCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.UNREGISTERED);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.UnregisteredCustomTableColumnSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.ZeroNoteFilterSelected:
                caseLabel = "zero-note";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.ZeroNoteCustomTableColumnSettings == null)
                {
                    Settings.Default.ZeroNoteCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.ZERO_NOTE);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.ZeroNoteCustomTableColumnSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.ChartInfoParseErrorFilterSelected:
                caseLabel = "chart-info-parse-error";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.ChartInfoParseErrorCustomTableColumnSettings == null)
                {
                    Settings.Default.ChartInfoParseErrorCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.CHART_INFO_PARSE_ERROR);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.ChartInfoParseErrorCustomTableColumnSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.FileMissingFilterSelected:
            case viewUpdateMode.FileMissingIgnoredFilterSelected:
            case viewUpdateMode.FullScanAllChartsFilterSelected:
            case viewUpdateMode.NewlyInstalledFolderSelected:
                caseLabel = "fullscan";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.FullScanCustomTableColumnSettings == null)
                {
                    Settings.Default.FullScanCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.FULLSCAN);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.FullScanCustomTableColumnSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.DuplicateFilterSelected:
                caseLabel = "duplicate";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.DuplicateCustomTableColumnSettings == null)
                {
                    Settings.Default.DuplicateCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.DUPLICATE);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.DuplicateCustomTableColumnSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.GarbledFilterSelected:
            case viewUpdateMode.GarbleFixedFilterSelected:
                caseLabel = "encoding";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.EncodingCustomTableColumnSettings == null)
                {
                    Settings.Default.EncodingCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.ENCODING);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.EncodingCustomTableColumnSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            case viewUpdateMode.PendingInstallFolderSelected:
                caseLabel = "install";
                stageStartMs = stopwatch.ElapsedMilliseconds;
                if (isInit || Settings.Default.InstallCustomTableColumnSettings == null)
                {
                    Settings.Default.InstallCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL);
                }
                caseEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                stageStartMs = stopwatch.ElapsedMilliseconds;
                ColumnsSettingsChartRowsView = Settings.Default.InstallCustomTableColumnSettings;
                caseAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
                break;
            default:
                modeHandled = false;
                break;
        }
        stageStartMs = stopwatch.ElapsedMilliseconds;
        ColumnSettingsVisibilityForPlaylist = targetColumnSettingsVisibilityForPlaylist;
        long visibilityMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        stageStartMs = stopwatch.ElapsedMilliseconds;
        if (Settings.Default.PlaylistSummaryColumnsSettings == null)
        {
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        }
        Settings.Default.PlaylistSummaryColumnsSettings.EnsureCompatibility();
        long playlistSummaryEnsureMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        stageStartMs = stopwatch.ElapsedMilliseconds;
        PlaylistSummaryColumnsSettings = Settings.Default.PlaylistSummaryColumnsSettings;
        long playlistSummaryAssignMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        if (modeHandled)
        {
            lastAppliedMainColumnSettingMode = mode;
        }

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
            RefreshChartRowsView(viewUpdateMode.SortUpdated);
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
                    SetNormalLibraryTreeFilter(NormalLibraryTreeFilter.Create(type, filterKey));
                    return;
                case FolderFilterType.ArtistFilter:
                    SetNormalLibraryTreeFilter(NormalLibraryTreeFilter.Create(type, filterKey));
                    return;
            }
        }
        SetNormalLibraryTreeFilter(null);
    }

    public void ExecPlaylistFilter(BMSTable bmsTable, string folderName = null, PlaylistFilterType type = PlaylistFilterType.PlaylistFilter)
    {
        SetPlaylistSummaryMode(enabled: false);
        switch (type)
        {
            case PlaylistFilterType.PlaylistFilter:
                RefreshChartRowsView(viewUpdateMode.PlaylistFilterSelected, new Tuple<BMSTable, string>(bmsTable, folderName));
                break;
            case PlaylistFilterType.PlaylistNotOwnedFilterSelected:
                RefreshChartRowsView(viewUpdateMode.PlaylistNotOwnedFilterSelected, bmsTable);
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
                files.SearchDuplicateChartGroups();
            }
            if (Enum.IsDefined(typeof(viewUpdateMode), (int)type))
            {
                RefreshChartRowsView((viewUpdateMode)type, parameter);
            }
        }
    }

    public void RemoveChartInfoParseFailuresByMd5(IEnumerable<string> md5s)
    {
        files?.RemoveChartInfoParseFailuresByMd5(NormalizeChartInfoParseFailureMd5s(md5s));
    }

    internal static string[] NormalizeChartInfoParseFailureMd5s(IEnumerable<string> md5s)
    {
        return BMSLibrary.NormalizeChartInfoParseFailureMd5s(md5s);
    }

    public void ExecInstallFilter(InstallFilterType type, object parameter = null)
    {
        SetPlaylistSummaryMode(enabled: false);
        switch (type)
        {
            case InstallFilterType.NewlyInstalledFilter:
                RefreshChartRowsView(viewUpdateMode.NewlyInstalledFolderSelected, parameter);
                break;
            case InstallFilterType.PendingInstallFilter:
                RefreshChartRowsView(viewUpdateMode.PendingInstallFolderSelected, parameter);
                break;
        }
    }

    internal void FixEncodingBMSFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string encoding = "")
    {
        files.SetBMSFilesEncoding(bmsFiles, encoding);
        InvalidateNormalLibrarySortKeys(NormalLibraryBmsTitleChangedReason);
    }

    private void ForceResourceHealthCheckCharts(IEnumerable<ChartFile> charts)
    {
        files.RescanResourceHealthCharts(charts);
        RefreshResourceHealthViewsAfterMaintenanceChanged();
    }

    internal void ForceResourceHealthCheckCharts(ChartOperationTargetSnapshot targets)
    {
        if (targets?.HasTargets != true)
        {
            return;
        }
        ForceResourceHealthCheckCharts(targets.Charts);
    }

    public void StartRescanAllOwnedChartMaintenance()
    {
        if (files == null || IsMaintenanceRescanProgressActive)
        {
            return;
        }
        var cancellationSource = new CancellationTokenSource();
        maintenanceRescanCancellationTokenSource = cancellationSource;
        UpdateMaintenanceRescanProgressStatus(new MaintenanceWorkflowProgress
        {
            TotalCount = 1,
            ProcessedCount = 0
        });
        Task.Run(delegate
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Info("maintenance_rescan start scope=all_owned");
                MaintenanceWorkflowResult result = files.RescanAllOwnedChartMaintenance(delegate (MaintenanceWorkflowProgress progress)
                {
                    if (progress != null && !progress.IsCompleted)
                    {
                        Ribbit.Logging.NLogWrapper.FileLogger?.Info("maintenance_rescan progress scope=all_owned processed=" + progress.ProcessedCount + "/" + progress.TotalCount + " current=" + (progress.CurrentPath ?? string.Empty));
                    }
                    UpdateMaintenanceRescanProgressStatus(progress);
                }, cancellationSource.Token);
                stopwatch.Stop();
                bool canceled = result?.Canceled == true;
                Ribbit.Logging.NLogWrapper.FileLogger?.Info("maintenance_rescan " + (canceled ? "canceled" : "done") + " scope=all_owned elapsedMs=" + stopwatch.ElapsedMilliseconds);
                FinishMaintenanceRescanProgress(canceled);
                RefreshResourceHealthViewsAfterMaintenanceChanged();
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Ribbit.Logging.NLogWrapper.FileLogger?.Info("maintenance_rescan failed scope=all_owned elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + (ex.Message ?? string.Empty).Replace(Environment.NewLine, " "));
                FinishMaintenanceRescanProgress(canceled: true);
                throw;
            }
            finally
            {
                if (maintenanceRescanCancellationTokenSource == cancellationSource)
                {
                    maintenanceRescanCancellationTokenSource = null;
                }
                cancellationSource.Dispose();
            }
        }).Logging("StartRescanAllOwnedChartMaintenance");
    }

    public void CancelMaintenanceRescan()
    {
        maintenanceRescanCancellationTokenSource?.Cancel();
        MaintenanceRescanCanCancel = false;
    }

    private void UpdateMaintenanceRescanProgressStatus(MaintenanceWorkflowProgress progress)
    {
        Action reflect = delegate
        {
            if (progress == null)
            {
                return;
            }
            IsMaintenanceRescanProgressActive = true;
            int total = Math.Max(progress.TotalCount, 1);
            int processed = Math.Max(0, Math.Min(progress.ProcessedCount, total));
            MaintenanceRescanMaximum = total;
            MaintenanceRescanValue = processed;
            MaintenanceRescanLabel = string.Format(BeMusicSeeker.Properties.Resources.Maintenance_rescan_progress_label_format, processed, total);
            MaintenanceRescanSubLabel = progress.CurrentPath ?? string.Empty;
            MaintenanceRescanCanCancel = !progress.IsCompleted && !progress.IsCanceled;
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

    private void FinishMaintenanceRescanProgress(bool canceled)
    {
        Action reflect = delegate
        {
            MaintenanceRescanLabel = canceled
                ? BeMusicSeeker.Properties.Resources.Maintenance_rescan_canceled
                : BeMusicSeeker.Properties.Resources.Maintenance_rescan_complete;
            MaintenanceRescanSubLabel = string.Empty;
            MaintenanceRescanCanCancel = false;
            IsMaintenanceRescanProgressActive = false;
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

    private void RefreshResourceHealthViewsAfterMaintenanceChanged()
    {
        InvalidateNormalLibrarySortKeys(NormalLibraryMaintenanceChangedReason);
        Action refresh = delegate
        {
            if (treeViewFilterTypeSelected == viewUpdateMode.FileMissingFilterSelected
                || treeViewFilterTypeSelected == viewUpdateMode.FileMissingIgnoredFilterSelected
                || treeViewFilterTypeSelected == viewUpdateMode.FullScanAllChartsFilterSelected)
            {
                if (TryDeferStartupPresentationRefresh(UiRefreshChannel.LibraryMainView, "maintenance_hydration_completed"))
                {
                    return;
                }
                RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
                return;
            }
            RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Maintenance, "maintenance_changed");
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            refresh();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(refresh);
        }
    }

    private void RefreshNormalLibraryAfterWarningChanged(string reason)
    {
        if (TrySuppress(UiRefreshChannel.LibraryMainView))
        {
            return;
        }
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.Warning, reason);
    }

    private void SetChartResourceWarningsIgnored(IEnumerable<ChartFile> charts, bool unset = false)
    {
        files.SetChartResourceWarningsIgnored(charts, unset);
    }

    internal void SetChartResourceWarningsIgnored(ChartOperationTargetSnapshot targets, bool unset = false)
    {
        if (targets?.HasTargets != true)
        {
            return;
        }
        SetChartResourceWarningsIgnored(targets.Charts, unset);
    }

    /// <summary>
    /// リンク切れ等の問題がある ChartPackage (インストーラーまたはアーカイブ単位) について、正しいインストール先のディレクトリをヒューリスティックに探索します。
    /// 探索結果は内部の BMSLibrary に対して適用されます。
    /// </summary>
    /// <param name="packages">探索・復旧対象となるBMSパッケージのコレクション。</param>
    public void SearchInstallDestinationForPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        lock (lockCopyFile)
        {
            files.SearchEstimatedInstallationDirectory(packages);
        }
        InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.IdentitySortKey, NormalLibraryInstallDestinationChangedReason);
    }

    public void SearchMergeDestinationForPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<ChartPackage> list = [.. packages.Where(pkg => pkg != null)];
        lock (lockCopyFile)
        {
            for (int num = 0; num < list.Count; num++)
            {
                files.SearchMergeDestinationForPendingPackage(list[num]);
            }
        }
        InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.IdentitySortKey, NormalLibraryInstallDestinationChangedReason);
    }

    internal void SearchInstallDestinationForPendingCharts(PendingInstallDestinationTargetSnapshot targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException("targets");
        }
        if (!targets.HasTargets)
        {
            return;
        }
        lock (lockCopyFile)
        {
            List<ChartOperationTarget> packageTargets = targets.PackageTargets.ToList();
            List<ChartPackage> packages = ExtractChartPackagesFromChartTargets(ref packageTargets);
            if (packages.Count > 0)
            {
                files.SearchEstimatedInstallationDirectory(packages);
            }
            if (targets.LooseEntries.Count > 0)
            {
                files.SearchEstimatedInstallationDirectoryForLooseCharts(targets.LooseEntries);
                UpdateSharedChartTransientStates(targets.LooseEntries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
            }
        }
        InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.IdentitySortKey, NormalLibraryInstallDestinationChangedReason);
    }

    internal void SearchMergeDestinationForPendingCharts(PendingInstallDestinationTargetSnapshot targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException("targets");
        }
        if (!targets.HasTargets)
        {
            return;
        }
        lock (lockCopyFile)
        {
            List<ChartOperationTarget> packageTargets = targets.PackageTargets.ToList();
            List<ChartPackage> packages = ExtractChartPackagesFromChartTargets(ref packageTargets);
            for (int num = 0; num < packages.Count; num++)
            {
                files.SearchMergeDestinationForPendingPackage(packages[num]);
            }
            if (targets.LooseEntries.Count > 0)
            {
                files.SearchMergeDestinationForPendingCharts(targets.LooseEntries);
                UpdateSharedChartTransientStates(targets.LooseEntries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
            }
        }
        InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
        RefreshLibraryMainViewForDataDependency(MainViewDataDependency.IdentitySortKey, NormalLibraryInstallDestinationChangedReason);
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
        ChartFile chart = playlistRow.Chart;
        LR2SongDBExtended.bmson_song bmsonSong = chart?.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            playlistRow.Entry.MarkAsBmsonPlaylistIdentity(playlistRow.sha256 ?? bmsonSong.sha256);
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
        RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
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
        RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
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
    public void InstallChartPackages(IEnumerable<string> installPaths, CancellationToken token = default, Action<bool> onEachCompleted = null, Action onEachPathProcessed = null)
    {
        if (files == null)
        {
            return;
        }
        string[] normalizedInstallPaths = [.. (installPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path))];
        if (normalizedInstallPaths.Length == 0)
        {
            return;
        }
        List<ChartPackage> list = [];
        lock (lockCopyFile)
        {
            try
            {
                if (!token.IsCancellationRequested)
                {
                    list.AddRange(files.InstallChartPackagesAuto(normalizedInstallPaths, token, onEachPathProcessed));
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
                files.AddReferenceBMSTablesToPackageCharts(BMSTables, list);
                InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
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
        InstallChartPackages(request.Paths, token, null, delegate
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
            string labelFormat = !string.IsNullOrWhiteSpace(snapshot.LabelFormat) ? snapshot.LabelFormat : BeMusicSeeker.Properties.Resources.Playlist_sync_progress_label_format;
            string singleLabel = !string.IsNullOrWhiteSpace(snapshot.SingleLabel) ? snapshot.SingleLabel : BeMusicSeeker.Properties.Resources.Playlist_sync_progress_single_label;
            PlaylistSyncProgressLabel = (snapshot.TotalTableCount > 0) ? string.Format(labelFormat, completed, total) : singleLabel;
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
    private long StartStartupProgressOperation(StartupProgressOperationKind operationKind)
    {
        ResetStartupBackgroundTaskSchedulerState(operationKind);
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            startupInitializationCompleteStopwatch = Stopwatch.StartNew();
            startupInitializationCompleteLogged = false;
        }
        var state = new StartupProgressState
        {
            OperationKind = operationKind,
            OperationToken = Interlocked.Increment(ref startupProgressOperationTokenSeed),
            IsActive = true,
            CompletedPhases = StartupProgressPhase.CoreInitializeStarted,
            ExpectedPhases = GetInitialExpectedStartupProgressPhases(operationKind),
            ScoreHydrationBaselineCompletedVersion = files?.ScoreHydrationCompletedVersion ?? 0,
            ScoreHydrationRequestedBaselineVersion = files?.ScoreHydrationRequestedVersion ?? 0,
            RankingRefreshBaselineCompletedVersion = files?.RankingRefreshCompletedVersion ?? 0,
            RankingRefreshRequestedBaselineVersion = files?.RankingRefreshRequestedVersion ?? 0,
            MaintenanceRequestedBaselineVersion = files?.MaintenanceHydrationRequestedVersion ?? 0,
            InstallableMaintenanceRequestedBaselineVersion = files?.InstallableMaintenanceDeferredRequestedVersion ?? 0,
            ChartDigestBackfillBaselineCompletedVersion = files?.ChartDigestBackfillCompletedVersion ?? 0,
            ChartInfoBackfillBaselineCompletedVersion = files?.ChartInfoBackfillCompletedVersion ?? 0,
            ChartInfoHydrationBaselineCompletedVersion = files?.ChartInfoHydrationCompletedVersion ?? 0,
            PlaylistEntriesHydrationBaselineCompletedVersion = tables?.PlaylistEntriesHydrationCompletedVersion ?? 0,
            LibraryDatabaseLoadBaselineCompletedVersion = files?.LibraryDatabaseLoadCompletedVersion ?? 0,
            LibraryFileEnumerationBaselineCompletedVersion = files?.LibraryFileEnumerationCompletedVersion ?? 0,
            LibraryFileDiffBaselineCompletedVersion = files?.LibraryFileDiffCompletedVersion ?? 0
        };
        lock (startupProgressLock)
        {
            startupProgressState = state;
        }
        RecomputeStartupProgressPresentation();
        return state.OperationToken;
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
            if (phase == StartupProgressPhase.RankingRefreshDone
                || phase == StartupProgressPhase.MaintenanceDeferredDone
                || phase == StartupProgressPhase.InstallableMaintenanceDeferredDone)
            {
                startupProgressState.LastCompletedAtUtc = DateTime.UtcNow;
            }
        }
        RecomputeStartupProgressPresentation();
    }

    private void ResetStartupBackgroundTaskSchedulerState(StartupProgressOperationKind operationKind)
    {
        lock (startupBackgroundTaskLock)
        {
            startupBackgroundTaskQueue.Clear();
            startupBackgroundTaskCompletedNames.Clear();
            startupBackgroundTaskRunningCountByLane.Clear();
            startupBackgroundTaskMetrics.Clear();
            startupBackgroundTaskRunningCount = 0;
            startupBackgroundTaskSchedulerStarted = ShouldStartStartupBackgroundTaskSchedulerAfterReset(operationKind, startupReadyOperableReached);
        }
        lock (lockUiSuppression)
        {
            deferredStartupPresentationMask = UiRefreshChannel.None;
        }
        startupInitializationCompleteStopwatch = null;
        startupInitializationCompleteLogged = false;
        startupInitializationCompleteRetryQueued = false;
    }

    private static bool ShouldStartStartupBackgroundTaskSchedulerAfterReset(StartupProgressOperationKind operationKind, bool operableReached)
    {
        return operationKind != StartupProgressOperationKind.Startup && operableReached;
    }

    private void SkipStartupProgressPhaseIfExpected(StartupProgressPhase phase, string reason)
    {
        bool skipped = false;
        StartupProgressOperationKind operationKind = StartupProgressOperationKind.None;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & phase) == 0 || (startupProgressState.CompletedPhases & phase) != 0)
            {
                return;
            }
            operationKind = startupProgressState.OperationKind;
            startupProgressState.CompletedPhases |= phase;
            startupProgressState.SkippedPhases |= phase;
            skipped = true;
        }
        if (skipped)
        {
            LogUiSuppression("startup_progress_phase_skipped operation=" + operationKind + " phase=" + phase + " reason=" + (reason ?? string.Empty));
            RecomputeStartupProgressPresentation();
        }
    }

    private void SkipUnrequestedStartupProgressPhases(string reason, params StartupProgressPhase[] phases)
    {
        SkipUnrequestedStartupProgressPhases(reason, 0L, phases);
    }

    private void SkipUnrequestedStartupProgressPhases(string reason, long operationToken, params StartupProgressPhase[] phases)
    {
        if (phases == null || phases.Length == 0)
        {
            return;
        }
        if (operationToken != 0L && !IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return;
        }
        foreach (StartupProgressPhase phase in phases)
        {
            bool shouldSkip;
            lock (startupProgressLock)
            {
                shouldSkip = startupProgressState.IsActive
                    && (startupProgressState.ExpectedPhases & phase) != 0
                    && (startupProgressState.RequestedPhases & phase) == 0
                    && (startupProgressState.CompletedPhases & phase) == 0;
            }
            if (shouldSkip)
            {
                SkipStartupProgressPhaseIfExpected(phase, reason);
            }
        }
    }

    private bool TryTrackStartupProgressPhaseRequest(StartupProgressPhase phase, int version, string reason, Action<StartupProgressState> updateRequiredVersion)
    {
        bool ignored = false;
        bool requestAfterSkip = false;
        StartupProgressOperationKind operationKind = StartupProgressOperationKind.None;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return false;
            }
            operationKind = startupProgressState.OperationKind;
            if ((startupProgressState.ExpectedPhases & phase) == 0)
            {
                ignored = true;
            }
            else
            {
                startupProgressState.RequestedPhases |= phase;
                updateRequiredVersion?.Invoke(startupProgressState);
                requestAfterSkip = (startupProgressState.SkippedPhases & phase) != 0;
                if ((startupProgressState.CompletedPhases & phase) == 0)
                {
                    startupProgressState.CompletionHideScheduled = false;
                }
            }
        }
        if (ignored)
        {
            LogUiSuppression("startup_progress_request_ignored operation=" + operationKind + " phase=" + phase + " reason=not_expected requestReason=" + (reason ?? string.Empty) + " version=" + version);
            return false;
        }
        if (requestAfterSkip)
        {
            LogUiSuppression("startup_progress_request_after_skip operation=" + operationKind + " phase=" + phase + " requestReason=" + (reason ?? string.Empty) + " version=" + version);
        }
        RecomputeStartupProgressPresentation();
        return true;
    }

    private static bool RequiresStartupProgressRequestBeforeCompletion(StartupProgressPhase phase)
    {
        return phase != StartupProgressPhase.CoreInitializeStarted
            && phase != StartupProgressPhase.StartupReadyData
            && phase != StartupProgressPhase.StartupReadyUi
            && phase != StartupProgressPhase.StartupReadyOperable
            && phase != StartupProgressPhase.LibraryDatabaseLoadDone
            && phase != StartupProgressPhase.LibraryFileEnumerationDone
            && phase != StartupProgressPhase.LibraryFileDiffDone;
    }

    private static bool CanCompleteStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase)
    {
        return (state.ExpectedPhases & phase) != 0
            && (!RequiresStartupProgressRequestBeforeCompletion(phase) || (state.RequestedPhases & phase) != 0);
    }

    /// <summary>
    /// 起動・リロード進捗で deferred playlist 参照適用を待機対象に追加します。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    /// <param name="version">要求版数。</param>
    private void TrackStartupProgressPlaylistReferenceRequest(string reason, int version)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && ShouldTrackStartupProgressPlaylistReference(reason, startupProgressState.OperationKind);
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistReferenceApplied,
            version,
            reason,
            state => state.RequiredPlaylistReferenceVersion = Math.Max(state.RequiredPlaylistReferenceVersion, version));
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.PlaylistReferenceApplied))
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
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && ShouldTrackStartupProgressExternalSync(reason, startupProgressState.OperationKind);
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ExternalPlaylistSyncDone,
            version,
            reason,
            state => state.RequiredExternalSyncVersion = Math.Max(state.RequiredExternalSyncVersion, version));
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ExternalPlaylistSyncDone))
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
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.MaintenanceRequestedBaselineVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.MaintenanceDeferredDone,
            requestedVersion,
            "maintenance_deferred",
            state => state.RequiredMaintenanceCompletedVersion = Math.Max(state.RequiredMaintenanceCompletedVersion, requestedVersion));
    }

    /// <summary>
    /// installable maintenance deferred 要求を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="requestedVersion">要求版数。</param>
    private void TrackStartupProgressInstallableMaintenanceRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.InstallableMaintenanceRequestedBaselineVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.InstallableMaintenanceDeferredDone,
            requestedVersion,
            "installable_maintenance_deferred",
            state => state.RequiredInstallableMaintenanceCompletedVersion = Math.Max(state.RequiredInstallableMaintenanceCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressScoreHydrationRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.ScoreHydrationRequestedBaselineVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ScoreHydrationDone,
            requestedVersion,
            "score_hydration",
            state => state.RequiredScoreHydrationCompletedVersion = Math.Max(state.RequiredScoreHydrationCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressRankingRefreshRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.RankingRefreshRequestedBaselineVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.RankingRefreshDone,
            requestedVersion,
            "ranking_refresh",
            state => state.RequiredRankingRefreshCompletedVersion = Math.Max(state.RequiredRankingRefreshCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressChartDigestBackfillRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.ChartDigestBackfillBaselineCompletedVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartDigestBackfillDone,
            requestedVersion,
            "chart_digest_backfill",
            state => state.RequiredChartDigestBackfillCompletedVersion = Math.Max(state.RequiredChartDigestBackfillCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressChartInfoBackfillRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.ChartInfoBackfillBaselineCompletedVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoBackfillDone,
            requestedVersion,
            "chart_info_backfill",
            state => state.RequiredChartInfoBackfillCompletedVersion = Math.Max(state.RequiredChartInfoBackfillCompletedVersion, requestedVersion));
    }

    private void TrackStartupProgressChartInfoHydrationRequested(int requestedVersion)
    {
        bool shouldTrack;
        int expectedBackfillVersion;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.ChartInfoHydrationBaselineCompletedVersion;
            expectedBackfillVersion = (files?.ChartInfoBackfillRequestedVersion ?? 0) + 1;
        }
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoHydrationDone,
            requestedVersion,
            "chart_info_hydration",
            state => state.RequiredChartInfoHydrationCompletedVersion = Math.Max(state.RequiredChartInfoHydrationCompletedVersion, requestedVersion));
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoBackfillDone,
            expectedBackfillVersion,
            "chart_info_backfill_after_hydration",
            state => state.RequiredChartInfoBackfillCompletedVersion = Math.Max(state.RequiredChartInfoBackfillCompletedVersion, expectedBackfillVersion));
    }

    private void TrackStartupProgressPlaylistEntriesHydrationRequested(int requestedVersion)
    {
        bool shouldTrack;
        lock (startupProgressLock)
        {
            shouldTrack = startupProgressState.IsActive && requestedVersion > startupProgressState.PlaylistEntriesHydrationBaselineCompletedVersion;
        }
        if (!shouldTrack)
        {
            return;
        }
        TrackStartupProgressPlaylistEntriesHydrationDirectRequest(requestedVersion, "playlist_entries_hydration");
    }

    private void TrackStartupProgressPlaylistEntriesHydrationDirectRequest(int requestedVersion, string reason)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            requestedVersion,
            reason,
            state => state.RequiredPlaylistEntriesHydrationCompletedVersion = Math.Max(state.RequiredPlaylistEntriesHydrationCompletedVersion, requestedVersion));
    }

    private void UpdateStartupProgressChartDigestBackfillStatus(int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartDigestBackfillDone))
            {
                return;
            }
            startupProgressState.ChartDigestBackfillTotalCount = totalCount;
            startupProgressState.ChartDigestBackfillProcessedCount = processedCount;
            startupProgressState.ChartDigestBackfillCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressLibraryInitializationStatus(BMSLibrary.LibraryInitializationProgressStage stage, string scannerLabel, int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            startupProgressState.LibraryInitializationProgressStage = stage;
            startupProgressState.LibraryInitializationProgressScannerLabel = scannerLabel ?? string.Empty;
            startupProgressState.LibraryInitializationProgressTotalCount = Math.Max(0, totalCount);
            startupProgressState.LibraryInitializationProgressProcessedCount = Math.Max(0, processedCount);
            startupProgressState.LibraryInitializationProgressCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    private void UpdateStartupProgressChartInfoBackfillStatus(int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoBackfillDone))
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoHydrationDone))
            {
                return;
            }
            startupProgressState.ChartInfoHydrationTotalCount = totalCount;
            startupProgressState.ChartInfoHydrationAppliedCount = appliedCount;
        }
        RecomputeStartupProgressPresentation();
    }

    private void TryCompleteStartupProgressLibraryDatabaseLoad(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryDatabaseLoadDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryDatabaseLoadBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryDatabaseLoadDone);
        }
    }

    private void TryCompleteStartupProgressLibraryFileEnumeration(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryFileEnumerationDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryFileEnumerationBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryFileEnumerationDone);
        }
    }

    private void TryCompleteStartupProgressLibraryFileDiff(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryFileDiffDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryFileDiffBaselineCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryFileDiffDone);
        }
    }

    private void TryCompleteStartupProgressChartDigestBackfill(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartDigestBackfillDone))
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoBackfillDone))
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoHydrationDone))
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.PlaylistEntriesHydrationDone))
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.MaintenanceDeferredDone))
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
    /// installable maintenance deferred 完了を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    private void TryCompleteStartupProgressInstallableMaintenance(int completedVersion)
    {
        bool shouldComplete = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.InstallableMaintenanceDeferredDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredInstallableMaintenanceCompletedVersion;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.InstallableMaintenanceDeferredDone);
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ScoreHydrationDone))
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
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.RankingRefreshDone))
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
        long reflectOperationToken = 0L;
        bool operationCompletedForLog = false;
        StartupProgressOperationKind operationKindForLog = StartupProgressOperationKind.None;
        lock (startupProgressLock)
        {
            StartupProgressState state = startupProgressState;
            isActive = state.IsActive;
            reflectOperationToken = state.OperationToken;
            operationKindForLog = state.OperationKind;
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
                operationCompletedForLog = operationCompleted;
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
                    label = BeMusicSeeker.Properties.Resources.Statusbar_progress_operable_background;
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
            lock (startupProgressLock)
            {
                if (isActive)
                {
                    if (!startupProgressState.IsActive || startupProgressState.OperationToken != reflectOperationToken)
                    {
                        return;
                    }
                }
                else if (startupProgressState.IsActive)
                {
                    return;
                }
            }
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
        if (operationCompletedForLog && operationKindForLog == StartupProgressOperationKind.Startup)
        {
            TryLogStartupInitializationComplete();
        }
    }

    private void TryLogStartupInitializationComplete()
    {
        long elapsedMs;
        lock (startupBackgroundTaskLock)
        {
            if (startupInitializationCompleteLogged || startupInitializationCompleteStopwatch == null)
            {
                return;
            }
            bool schedulerIdle = startupBackgroundTaskQueue.Count == 0 && startupBackgroundTaskRunningCount == 0;
            if (!schedulerIdle)
            {
                QueueStartupInitializationCompleteRetryUnsafe();
                return;
            }
            startupInitializationCompleteLogged = true;
            elapsedMs = startupInitializationCompleteStopwatch.ElapsedMilliseconds;
        }
        LogUiSuppression("startup_initialization_complete elapsedMs=" + elapsedMs);
        LogUiSuppression(BuildStartupBackgroundSummaryLog(elapsedMs));
        if (!QueueDeferredStartupPresentationFlushAfterInitialization())
        {
            ScheduleVirtualNormalLibraryOrderPrewarm("startup_initialization_complete");
            ShowInitialSetupCompletionMessageIfPending();
        }
    }

    private bool QueueDeferredStartupPresentationFlushAfterInitialization()
    {
        UiRefreshChannel mask;
        lock (lockUiSuppression)
        {
            mask = deferredStartupPresentationMask;
            deferredStartupPresentationMask = UiRefreshChannel.None;
        }
        if (mask == UiRefreshChannel.None)
        {
            return false;
        }
        Action flush = delegate
        {
            var stopwatch = Stopwatch.StartNew();
            LogUiSuppression("startup_presentation_flush start mask=" + mask);
            FlushPendingUiRefresh(mask, GetActiveStartupProgressOperationToken(), allowStartupPresentationDefer: false, logReadiness: false);
            stopwatch.Stop();
            LogUiSuppression("startup_presentation_flush done elapsedMs=" + stopwatch.ElapsedMilliseconds + " mask=" + mask);
            ScheduleVirtualNormalLibraryOrderPrewarm("startup_presentation_flush_done");
            ShowInitialSetupCompletionMessageIfPending();
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            flush();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(flush);
        }
        return true;
    }

    private void ShowInitialSetupCompletionMessageIfPending()
    {
        if (!initialSetupCompletionMessagePending)
        {
            return;
        }
        initialSetupCompletionMessagePending = false;
        Action showMessage = delegate
        {
            DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_completed, BeMusicSeeker.Properties.Resources.Information, MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
        };
        if (DispatcherHelper.UIDispatcher == null || DispatcherHelper.UIDispatcher.CheckAccess())
        {
            showMessage();
        }
        else
        {
            DispatcherHelper.UIDispatcher.BeginInvoke(showMessage);
        }
    }

    private void QueueStartupInitializationCompleteRetryUnsafe()
    {
        if (startupInitializationCompleteRetryQueued)
        {
            return;
        }
        startupInitializationCompleteRetryQueued = true;
        Task.Run(async delegate
        {
            await Task.Delay(250).ConfigureAwait(false);
            lock (startupBackgroundTaskLock)
            {
                startupInitializationCompleteRetryQueued = false;
            }
            TryLogStartupInitializationComplete();
        });
    }

    private string BuildStartupBackgroundSummaryLog(long elapsedMs)
    {
        List<StartupBackgroundTaskMetric> metrics;
        lock (startupBackgroundTaskLock)
        {
            metrics = [.. startupBackgroundTaskMetrics.Values
                .OrderBy(metric => metric.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CloneStartupBackgroundTaskMetric)];
        }
        long queued = metrics.Sum(metric => metric.QueuedCount);
        long started = metrics.Sum(metric => metric.StartedCount);
        long completed = metrics.Sum(metric => metric.CompletedCount);
        long failed = metrics.Sum(metric => metric.FailedCount);
        string taskSummary = metrics.Count == 0
            ? "(none)"
            : string.Join(";", metrics.Select(FormatStartupBackgroundTaskMetric));
        return "startup_background_summary elapsedMs=" + elapsedMs
            + " queued=" + queued
            + " started=" + started
            + " completed=" + completed
            + " failed=" + failed
            + " tasks=" + taskSummary;
    }

    private static StartupBackgroundTaskMetric CloneStartupBackgroundTaskMetric(StartupBackgroundTaskMetric metric)
    {
        return new StartupBackgroundTaskMetric
        {
            Name = metric.Name,
            Reason = metric.Reason,
            Dependency = metric.Dependency,
            Lane = metric.Lane,
            QueuedCount = metric.QueuedCount,
            StartedCount = metric.StartedCount,
            CompletedCount = metric.CompletedCount,
            FailedCount = metric.FailedCount,
            TotalElapsedMs = metric.TotalElapsedMs,
            LastElapsedMs = metric.LastElapsedMs,
            LastStatus = metric.LastStatus,
            LastDetail = metric.LastDetail
        };
    }

    private static string FormatStartupBackgroundTaskMetric(StartupBackgroundTaskMetric metric)
    {
        return SanitizeStartupBackgroundSummaryValue(metric.Name)
            + "{queued=" + metric.QueuedCount
            + ",started=" + metric.StartedCount
            + ",completed=" + metric.CompletedCount
            + ",failed=" + metric.FailedCount
            + ",lastStatus=" + SanitizeStartupBackgroundSummaryValue(metric.LastStatus)
            + ",lastMs=" + metric.LastElapsedMs
            + ",totalMs=" + metric.TotalElapsedMs
            + ",reason=" + SanitizeStartupBackgroundSummaryValue(metric.Reason)
            + ",dependency=" + SanitizeStartupBackgroundSummaryValue(metric.Dependency)
            + ",lane=" + SanitizeStartupBackgroundSummaryValue(metric.Lane)
            + ",detail=" + SanitizeStartupBackgroundSummaryValue(metric.LastDetail)
            + "}";
    }

    private static string SanitizeStartupBackgroundSummaryValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }
        return value
            .Replace(Environment.NewLine, " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(";", ",")
            .Replace("{", "(")
            .Replace("}", ")")
            .Replace(" ", "_");
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
        return operationKind switch
        {
            StartupProgressOperationKind.ReloadFileDiff => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_files,
            StartupProgressOperationKind.ScoreOnly => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_scores,
            StartupProgressOperationKind.FullReinitialize => BeMusicSeeker.Properties.Resources.Statusbar_progress_full_reinitialize,
            StartupProgressOperationKind.ReloadTables => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_tables,
            _ => BeMusicSeeker.Properties.Resources.Statusbar_progress_startup,
        };
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
        if (operationKind == StartupProgressOperationKind.FullReinitialize)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_reinitialize;
        }
        if (operationKind == StartupProgressOperationKind.ScoreOnly)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_scores;
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
        if (operationKind == StartupProgressOperationKind.FullReinitialize)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_reinitialize;
        }
        if (operationKind == StartupProgressOperationKind.ScoreOnly)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_scores;
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
            return GetStartupProgressLibraryLoadSubLabel(state);
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
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info_load;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartInfoBackfillDone))
        {
            string fileName = string.IsNullOrWhiteSpace(state.ChartInfoBackfillCurrentPath) ? string.Empty : Path.GetFileName(state.ChartInfoBackfillCurrentPath);
            return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info, state.ChartInfoBackfillProcessedCount, state.ChartInfoBackfillTotalCount, fileName);
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartDigestBackfillDone))
        {
            string fileName = string.IsNullOrWhiteSpace(state.ChartDigestBackfillCurrentPath) ? string.Empty : Path.GetFileName(state.ChartDigestBackfillCurrentPath);
            return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info, state.ChartDigestBackfillProcessedCount, state.ChartDigestBackfillTotalCount, fileName);
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
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.InstallableMaintenanceDeferredDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_installable_maintenance;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_background;
    }

    private static string GetStartupProgressLibraryLoadSubLabel(StartupProgressState state)
    {
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryDatabaseLoadDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_library_db_load;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryFileEnumerationDone))
        {
            string scanner = state.LibraryInitializationProgressScannerLabel;
            if (!string.IsNullOrWhiteSpace(scanner))
            {
                return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_enumeration + " (" + scanner + ")";
            }
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_enumeration;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryFileDiffDone))
        {
            if (state.LibraryInitializationProgressTotalCount > 0)
            {
                string fileName = string.IsNullOrWhiteSpace(state.LibraryInitializationProgressCurrentPath) ? string.Empty : Path.GetFileName(state.LibraryInitializationProgressCurrentPath);
                return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_diff, state.LibraryInitializationProgressProcessedCount, state.LibraryInitializationProgressTotalCount, fileName);
            }
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_diff;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_library_load;
    }

    private static string FormatStartupProgressCountLabel(string phaseLabel, int processedCount, int totalCount, string fileName)
    {
        string prefix = "[" + Math.Max(0, processedCount) + "/" + Math.Max(0, totalCount) + "] " + (phaseLabel ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return prefix;
        }
        return prefix + " " + fileName;
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
        return IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistReferenceApplied)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ExternalPlaylistSyncDone)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.MaintenanceDeferredDone)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.InstallableMaintenanceDeferredDone);
    }

    private static int CountExpectedStartupProgressPhases(StartupProgressState state)
    {
        int count = 0;
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryDatabaseLoadDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileEnumerationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileDiffDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyData, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyUi, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyOperable, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.InstallableMaintenanceDeferredDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.RankingRefreshDone, ref count);
        return count;
    }

    private static StartupProgressPhase GetInitialExpectedStartupProgressPhases(StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryDatabaseLoadDone
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyData
                                | StartupProgressPhase.StartupReadyUi
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ExternalPlaylistSyncDone
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.RankingRefreshDone
                                | StartupProgressPhase.MaintenanceDeferredDone
                                | StartupProgressPhase.InstallableMaintenanceDeferredDone
                                | StartupProgressPhase.ChartDigestBackfillDone
                                | StartupProgressPhase.ChartInfoBackfillDone
                                | StartupProgressPhase.ChartInfoHydrationDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressOperationKind.FullReinitialize => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryDatabaseLoadDone
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.RankingRefreshDone
                                | StartupProgressPhase.MaintenanceDeferredDone
                                | StartupProgressPhase.InstallableMaintenanceDeferredDone
                                | StartupProgressPhase.ChartDigestBackfillDone
                                | StartupProgressPhase.ChartInfoBackfillDone
                                | StartupProgressPhase.ChartInfoHydrationDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressOperationKind.ReloadFileDiff => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressOperationKind.ScoreOnly => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.RankingRefreshDone,
            StartupProgressOperationKind.ReloadTables => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ExternalPlaylistSyncDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            _ => StartupProgressPhase.CoreInitializeStarted | StartupProgressPhase.StartupReadyOperable,
        };
    }

    private static int CountCompletedExpectedStartupProgressPhases(StartupProgressState state)
    {
        int count = 0;
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryDatabaseLoadDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileEnumerationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileDiffDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyData, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyUi, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyOperable, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.InstallableMaintenanceDeferredDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.RankingRefreshDone, ref count);
        return count;
    }

    internal static StartupProgressTestResult ReduceStartupProgressForTest(string operationKindName, params string[] actions)
    {
        StartupProgressOperationKind operationKind = ParseStartupProgressOperationKindForTest(operationKindName);
        var state = new StartupProgressState
        {
            OperationKind = operationKind,
            IsActive = true,
            ExpectedPhases = GetInitialExpectedStartupProgressPhases(operationKind),
            CompletedPhases = StartupProgressPhase.CoreInitializeStarted
        };
        int ignoredRequests = 0;
        int ignoredCompletes = 0;
        foreach (string rawAction in actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawAction))
            {
                continue;
            }
            string[] parts = rawAction.Split([':'], 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException("Action must be formatted as verb:PhaseName.", nameof(actions));
            }
            string verb = parts[0].Trim();
            if (string.Equals(verb, "request", StringComparison.OrdinalIgnoreCase))
            {
                StartupProgressPhase phase = ParseStartupProgressPhaseForTest(parts[1].Trim());
                if ((state.ExpectedPhases & phase) == 0)
                {
                    ignoredRequests++;
                    continue;
                }
                state.RequestedPhases |= phase;
            }
            else if (string.Equals(verb, "complete", StringComparison.OrdinalIgnoreCase))
            {
                StartupProgressPhase phase = ParseStartupProgressPhaseForTest(parts[1].Trim());
                if (CanCompleteStartupProgressPhase(state, phase))
                {
                    state.CompletedPhases |= phase;
                }
                else
                {
                    ignoredCompletes++;
                }
            }
            else if (string.Equals(verb, "skip", StringComparison.OrdinalIgnoreCase))
            {
                StartupProgressPhase phase = ParseStartupProgressPhaseForTest(parts[1].Trim());
                if ((state.ExpectedPhases & phase) != 0 && (state.RequestedPhases & phase) == 0)
                {
                    state.CompletedPhases |= phase;
                    state.SkippedPhases |= phase;
                }
            }
            else if (string.Equals(verb, "library", StringComparison.OrdinalIgnoreCase))
            {
                string[] statusParts = parts[1].Split('|');
                state.LibraryInitializationProgressStage = (BMSLibrary.LibraryInitializationProgressStage)Enum.Parse(typeof(BMSLibrary.LibraryInitializationProgressStage), statusParts[0], ignoreCase: true);
                if (statusParts.Length > 1)
                {
                    state.LibraryInitializationProgressTotalCount = int.Parse(statusParts[1], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 2)
                {
                    state.LibraryInitializationProgressProcessedCount = int.Parse(statusParts[2], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 3)
                {
                    state.LibraryInitializationProgressCurrentPath = statusParts[3];
                }
                if (statusParts.Length > 4)
                {
                    state.LibraryInitializationProgressScannerLabel = statusParts[4];
                }
            }
            else if (string.Equals(verb, "chartinfo", StringComparison.OrdinalIgnoreCase))
            {
                string[] statusParts = parts[1].Split('|');
                if (statusParts.Length > 0)
                {
                    state.ChartInfoBackfillTotalCount = int.Parse(statusParts[0], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 1)
                {
                    state.ChartInfoBackfillProcessedCount = int.Parse(statusParts[1], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 2)
                {
                    state.ChartInfoBackfillCurrentPath = statusParts[2];
                }
            }
            else if (string.Equals(verb, "hydrate", StringComparison.OrdinalIgnoreCase))
            {
                string[] statusParts = parts[1].Split('|');
                if (statusParts.Length > 0)
                {
                    state.ChartInfoHydrationTotalCount = int.Parse(statusParts[0], CultureInfo.InvariantCulture);
                }
                if (statusParts.Length > 1)
                {
                    state.ChartInfoHydrationAppliedCount = int.Parse(statusParts[1], CultureInfo.InvariantCulture);
                }
            }
            else
            {
                throw new ArgumentException("Unsupported action verb: " + verb, nameof(actions));
            }
        }
        bool completed = AreExpectedStartupProgressPhasesCompleted(state);
        bool operableCompleted = (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
        string label = completed
            ? GetStartupProgressCompletedLabel(operationKind)
            : (operableCompleted ? BeMusicSeeker.Properties.Resources.Statusbar_progress_operable_background : GetStartupProgressRunningLabel(operationKind));
        return new StartupProgressTestResult
        {
            ExpectedCount = CountExpectedStartupProgressPhases(state),
            CompletedCount = CountCompletedExpectedStartupProgressPhases(state),
            RequestedCount = CountStartupProgressPhases(state.RequestedPhases & state.ExpectedPhases),
            SkippedCount = CountStartupProgressPhases(state.SkippedPhases & state.ExpectedPhases),
            IgnoredRequestCount = ignoredRequests,
            IgnoredCompleteCount = ignoredCompletes,
            Label = label,
            SubLabel = completed ? string.Empty : GetStartupProgressSubLabel(state),
            IsCompleted = completed
        };
    }

    internal static int GetInitialStartupProgressExpectedCountForTest(string operationKindName)
    {
        var state = new StartupProgressState
        {
            OperationKind = ParseStartupProgressOperationKindForTest(operationKindName),
            ExpectedPhases = GetInitialExpectedStartupProgressPhases(ParseStartupProgressOperationKindForTest(operationKindName))
        };
        return CountExpectedStartupProgressPhases(state);
    }

    internal static bool ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest(string operationKindName, bool operableReached)
    {
        return ShouldStartStartupBackgroundTaskSchedulerAfterReset(ParseStartupProgressOperationKindForTest(operationKindName), operableReached);
    }

    private static int CountStartupProgressPhases(StartupProgressPhase phases)
    {
        int count = 0;
        CountStartupProgressPhase(phases, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.LibraryDatabaseLoadDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.LibraryFileEnumerationDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.LibraryFileDiffDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.StartupReadyData, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.StartupReadyUi, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.StartupReadyOperable, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.InstallableMaintenanceDeferredDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountStartupProgressPhase(phases, StartupProgressPhase.RankingRefreshDone, ref count);
        return count;
    }

    private static void CountStartupProgressPhase(StartupProgressPhase phases, StartupProgressPhase phase, ref int count)
    {
        if ((phases & phase) != 0)
        {
            count++;
        }
    }

    private static StartupProgressOperationKind ParseStartupProgressOperationKindForTest(string operationKindName)
    {
        return (StartupProgressOperationKind)Enum.Parse(typeof(StartupProgressOperationKind), operationKindName, ignoreCase: true);
    }

    private static StartupProgressPhase ParseStartupProgressPhaseForTest(string phaseName)
    {
        return (StartupProgressPhase)Enum.Parse(typeof(StartupProgressPhase), phaseName, ignoreCase: true);
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
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => string.Equals(reason, "Initialize", StringComparison.Ordinal) || string.Equals(reason, "DeferredExternalSync:Initialize", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadFileDiff => string.Equals(reason, "ReloadFileDiff", StringComparison.Ordinal),
            StartupProgressOperationKind.FullReinitialize => string.Equals(reason, "FullReinitialize", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadTables => string.Equals(reason, "DeferredExternalSync:ReloadTables", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// deferred 外部プレイリスト同期要求を現在の operation 進捗へ関連付けるかどうかを返します。
    /// </summary>
    private static bool ShouldTrackStartupProgressExternalSync(string reason, StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => string.Equals(reason, "Initialize", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadTables => string.Equals(reason, "ReloadTables", StringComparison.Ordinal),
            _ => false,
        };
    }

    private List<ChartPackage> ExtractChartPackagesFromChartEntries(ref List<PackageChartEntry> entries, bool isInstalled = false)
    {
        DispatcherCollection<ChartPackage> source = (isInstalled ? ChartPackagesInstalled : ChartPackagesPending);
        List<PackageChartEntry> remainingEntries = [];
        List<ChartPackage> packages = [];
        foreach (PackageChartEntry entry in entries)
        {
            ChartPackage chartPackage = source?.FirstOrDefault(p => ContainsChartTarget(p, entry));
            if (chartPackage == null)
            {
                remainingEntries.Add(entry);
            }
            else
            {
                packages.Add(chartPackage);
            }
        }
        entries = remainingEntries;
        return [.. packages.Distinct()];
    }

    private List<ChartPackage> ExtractChartPackagesFromChartTargets(ref List<ChartOperationTarget> targets, bool isInstalled = false)
    {
        DispatcherCollection<ChartPackage> source = (isInstalled ? ChartPackagesInstalled : ChartPackagesPending);
        List<ChartOperationTarget> remainingTargets = [];
        List<ChartPackage> packages = [];
        foreach (ChartOperationTarget target in targets)
        {
            ChartPackage chartPackage = source?.FirstOrDefault(p => ContainsChartTarget(p, target));
            if (chartPackage == null)
            {
                remainingTargets.Add(target);
            }
            else
            {
                packages.Add(chartPackage);
            }
        }
        targets = remainingTargets;
        return [.. packages.Distinct()];
    }

    private static bool ContainsChartTarget(ChartPackage chartPackage, ChartFile chart)
    {
        if (chartPackage == null || chart == null)
        {
            return false;
        }
        return (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(chart) == true);
    }

    private static bool ContainsChartTarget(ChartPackage chartPackage, PackageChartEntry targetEntry)
    {
        ChartFile chart = targetEntry?.Chart;
        if (chartPackage == null || chart == null)
        {
            return false;
        }
        return (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(targetEntry) == true);
    }

    private static bool ContainsChartTarget(ChartPackage chartPackage, ChartOperationTarget target)
    {
        ChartFile chart = target?.Chart;
        if (chartPackage == null || chart == null)
        {
            return false;
        }
        if (target.PackageEntry != null)
        {
            return (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(target.PackageEntry) == true);
        }
        return (chartPackage.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(chart) == true);
    }

    public void ForceInstallPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<ChartPackage> list = [.. packages.Where(pkg => pkg != null)];
        RunPendingInstallMutation(delegate
        {
            files.ForceInstallPendingPackages(list);
        }, CreatePackagePlaybackTargetSnapshot(list), UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree);
    }

    internal void ForceInstallPendingCharts(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException("targets");
        }
        List<ChartOperationTarget> remainingTargets = [.. targets.Where(target => target?.Chart != null)];
        lock (lockCopyFile)
        {
            List<ChartPackage> chartPackages = ExtractChartPackagesFromChartTargets(ref remainingTargets);
            ForceInstallPendingPackages(chartPackages);
        }
    }

    public void ManualInstallPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<ChartPackage> list = [.. packages.Where(pkg => pkg != null)];
        RunPendingInstallMutation(delegate
        {
            files.InstallPendingPackagesToEstimatedDestinations(list);
        }, CreatePackagePlaybackTargetSnapshot(list), UiRefreshChannel.LibraryFolderTree | UiRefreshChannel.DuplicateTree);
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

    private void RefreshPlaylistSummaryIfVisible(string reason = "playlist_summary_refresh", bool invalidateTableCountCache = false)
    {
        RefreshPlaylistSummaryDataIfVisible(reason, invalidateTableCountCache);
    }

    private void RefreshPlaylistSummaryDataIfVisible(string reason, bool invalidateTableCountCache)
    {
        // 表示へ戻るだけなら raw rows cache は必ず捨てるが、table count cache は必要な場合だけ落とす。
        // count cache key には playlist entry revision と owned snapshot version が含まれるため、
        // playlist 内容や所持状態が変わった場合は自然に miss する。
        PlaylistSummaryDataRefreshDecision decision = BuildPlaylistSummaryDataRefreshDecision(IsPlaylistSummaryMode, IsUiUpdateSuppressed(), invalidateTableCountCache);
        if (decision.InvalidateRowsCache)
        {
            InvalidatePlaylistSummaryData(reason, decision.InvalidateTableCountCache);
        }
        if (decision.RequestDeferredRefresh)
        {
            RequestDeferredPlaylistSummaryRefresh();
            return;
        }
        if (!decision.RebuildImmediately)
        {
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
        void action()
        {
            var stopwatch = Stopwatch.StartNew();
            List<PlaylistSummaryRow> rows = BuildPlaylistSummaryRows(out BMSLibrary.PlaylistSummaryOwnedHashSnapshot playlistSummaryOwnedHashSnapshot, out int tableCount, out int entryScanCount, out int unloadedTableCount, out int summaryCacheHitCount, out int summaryCacheMissCount);
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
        }
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
        List<PlaylistSummaryRow> rows = [];
        entryScanCount = 0;
        summaryCacheHitCount = 0;
        summaryCacheMissCount = 0;
        Dictionary<string, PlaylistSyncRuntimeStatus> playlistSyncStatusSnapshot = GetPlaylistSyncStatusSnapshot();
        playlistSummaryOwnedHashSnapshot = files?.GetPlaylistSummaryOwnedHashSnapshot();
        HashSet<string> ownedMd5Hashes = playlistSummaryOwnedHashSnapshot?.Md5Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ownedSha256Hashes = playlistSummaryOwnedHashSnapshot?.Sha256Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int ownedSnapshotVersion = playlistSummaryOwnedHashSnapshot?.Version ?? 0;
        List<BMSTable> tablesSnapshot = [];
        unloadedTableCount = 0;
        if (tables != null)
        {
            tables.AcquireReaderLockBMSTables();
            try
            {
                tablesSnapshot = [.. BMSTables.Where(t => t != null).OrderBy(t => t.name ?? string.Empty)];
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
            var countResult = new PlaylistSummaryCountResult();
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
        countResult = default;
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
        List<PlaylistSummaryRow> safeRawRows = rawRows ?? [];
        PlaylistSummaryPresentationResult presentationResult = BuildPlaylistSummaryPresentationRows(safeRawRows, PlaylistSummaryKeywordFilter, PlaylistSummaryOwnedFilter, PlaylistSummarySortParameters, false);
        Action reflect = delegate
        {
            if (!IsPlaylistSummaryMode)
            {
                return;
            }
            PlaylistSummaryView = new ObservableCollection<PlaylistSummaryRow>(presentationResult.Rows);
            GridSummaryText = string.Format(BeMusicSeeker.Properties.Resources.Playlist_summary_format, presentationResult.Rows.Sum(r => r.TotalCharts), presentationResult.Rows.Count);
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
        var stopwatch = Stopwatch.StartNew();
        List<PlaylistSummaryRow> filteredRows = [.. ApplyPlaylistSummaryFilters(rows, keywordFilter, ownedFilter)];
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
        PlaylistSummaryCountResult result = default;
        HashSet<string> safeOwnedMd5Hashes = ownedMd5Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> safeOwnedSha256Hashes = ownedSha256Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSTableEntry entry in entries ?? [])
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
        IEnumerable<PlaylistSummaryRow> source = rows ?? [];
        string text = (keywordFilter ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var query = GridKeywordSearchQuery.Parse(text);
            source = source.Where(row => query.MatchesPlaylistSummary(row));
        }
        return source.Where(row => IsPlaylistSummaryRowMatchedOwnedFilter(row, ownedFilter));
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
        return ownedFilter switch
        {
            PlaylistSummaryOwnedFilterType.OwnedComplete => row.TotalCharts > 0 && row.OwnedCharts == row.TotalCharts,
            PlaylistSummaryOwnedFilterType.OwnedIncomplete => row.TotalCharts == 0 || row.OwnedCharts < row.TotalCharts,
            _ => true,
        };
    }

    public void ApplyPlaylistSummaryFlags(IEnumerable<PlaylistSummaryRow> rows, bool? isExternalSync = null, bool? isRootFolder = null)
    {
        if (rows == null || tables == null)
        {
            return;
        }
        List<PlaylistSummaryRow> list = [.. rows.Where(r => r?.TableRef != null).GroupBy(r => r.TableRef).Select(g => g.First())];
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
                    lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Union([customFolderOutputDirectory]).Distinct(StringComparer.OrdinalIgnoreCase));
                }
                else
                {
                    lr2config.SetBMSSearchDirectories(bMSSearchDirectories.Except([customFolderOutputDirectory], StringComparer.OrdinalIgnoreCase));
                }
                lr2config.Save();
            }
            flag = true;
        }
        if (flag)
        {
            RefreshPlaylistSummaryIfVisible("playlist_properties_bulk_changed", invalidateTableCountCache: true);
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
        List<BMSTable> tablesToResync = [.. rows.Where(r => r?.TableRef != null).Select(r => r.TableRef).Distinct()];
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
        List<BMSTable> list = [.. tablesToResync.Where(t => t != null).Distinct().Where(delegate (BMSTable item)
        {
            Uri uri2 = item.Page_url ?? item.Header_url;
            return uri2 != null && uri2.IsAbsoluteUri;
        })];
        if (list.Count == 0)
        {
            return;
        }
        PlaylistReloadOperationKind playlistReloadOperationKind = (list.Count > 1) ? PlaylistReloadOperationKind.ManualFullReload : PlaylistReloadOperationKind.SinglePlaylistReload;
        var playlistReloadStopwatch = Stopwatch.StartNew();
        BeginPlaylistSyncProgressOperation();
        try
        {
            LogPlaylistReload("playlist_reload_operation started operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=manual_resync tableCount=" + list.Count);
            List<BMSPlaylist.PlaylistReloadTargetResult> results = await tables.ReloadPlaylistTargetsAsync(
                list,
                [CreatePlaylistReferenceReplaceUpdateCallback()],
                delegate (PlaylistSyncAttemptResult result)
                {
                    if (result == null)
                    {
                        return;
                    }
                    if (!result.Succeeded)
                    {
                        NLogWrapper.FileLogger?.Warn(result.Exception, "playlist_manual_resync_failed table=" + (result.SourceTable?.name ?? string.Empty) + " uri=" + (result.PageUri?.ToString() ?? string.Empty));
                    }
                    UpdatePlaylistSyncRuntimeStatus(result);
                },
                UpdatePlaylistSyncProgressStatus,
                "manual_resync");
            tables.QueueBeatorajaBmtExportAll("manual_resync");
            RefreshPlaylistSummaryIfVisible("manual_playlist_resync", invalidateTableCountCache: true);
            RefreshPlaylistDetailAfterReloadIfVisible();
            bool cleanupQueued = QueuePlaylistReloadCleanup(playlistReloadOperationKind, list.Count);
            LogPlaylistReload("playlist_reload_operation completed operationKind=" + GetPlaylistReloadOperationKindText(playlistReloadOperationKind) + " reason=manual_resync tableCount=" + list.Count + " processedCount=" + (results?.Count ?? 0) + " summaryRebuildMs=" + Interlocked.Read(ref lastPlaylistSummaryBuildElapsedMs) + " detailRefreshMs=" + Interlocked.Read(ref lastPlaylistDetailBuildElapsedMs) + " cleanupQueued=" + cleanupQueued.ToString().ToLowerInvariant() + " elapsedMs=" + playlistReloadStopwatch.ElapsedMilliseconds);
        }
        finally
        {
            EndPlaylistSyncProgressOperation();
        }
    }

    internal void ManualInstallPendingCharts(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException("targets");
        }
        List<ChartOperationTarget> remainingTargets = [.. targets.Where(target => target?.Chart != null)];
        lock (lockCopyFile)
        {
            List<ChartPackage> chartPackages = ExtractChartPackagesFromChartTargets(ref remainingTargets);
            ManualInstallPendingPackages(chartPackages);
        }
    }

    public void RemovePendingPackagesAll()
    {
        RunPendingInstallMutation(delegate
        {
            files.RemovePendingPackagesAll();
        });
    }

    public void RemovePendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        RunPendingInstallMutation(delegate
        {
            files.RemovePendingPackages(packages);
        });
    }

    internal void RemovePendingPackages(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException("targets");
        }
        List<ChartOperationTarget> remainingTargets = [.. targets.Where(target => target?.Chart != null)];
        List<ChartPackage> chartPackages = ExtractChartPackagesFromChartTargets(ref remainingTargets);
        RemovePendingPackages(chartPackages);
    }

    public List<ChartPackage> GetPendingPackagesContainingOnlyInstalledCharts()
    {
        if (files == null)
        {
            return [];
        }
        lock (lockCopyFile)
        {
            return files.GetPendingPackagesContainingOnlyInstalledCharts();
        }
    }

    internal List<ChartFile> GetPendingBmsFormatChartFilesSnapshot()
    {
        if (files == null)
        {
            return [];
        }
        lock (lockCopyFile)
        {
            return files.GetPendingBmsFormatChartFilesSnapshot();
        }
    }

    public void DeletePendingPackageSources(IEnumerable<ChartPackage> packages, bool sendToRecycleBin = true, CancellationToken token = default, Action onEachProcessed = null)
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

    internal void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(IEnumerable<ChartFile> targetCharts, CancellationToken token = default, Action onEachProcessed = null)
    {
        List<ChartFile> list = GetBmsFormatCharts((targetCharts != null) ? [.. targetCharts.Where(chart => chart != null)] : GetPendingBmsFormatChartFilesSnapshot());
        RunPendingInstallMutation(delegate
        {
            files.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(list, token, onEachProcessed);
        }, list);
    }

    public PendingInstalledOnlyResourceOverwriteResult OverwritePendingInstalledOnlyPackagesResources(IEnumerable<ChartPackage> packages, CancellationToken token = default, Action onEachProcessed = null)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<ChartPackage> list = [.. packages.Where(pkg => pkg != null)];
        List<ChartFile> playbackTargetCharts = CreatePackagePlaybackTargetSnapshot(list);
        return RunPendingInstallMutation(() => files.OverwritePendingInstalledOnlyPackagesResources(list, token, onEachProcessed), playbackTargetCharts);
    }

    public void RemoveInstalledPackageRecordsAll()
    {
        files?.RemoveInstalledPackageRecordsAll();
    }

    public void RemoveInstalledPackageRecords(IEnumerable<ChartPackage> packages)
    {
        if (files != null)
        {
            if (packages == null)
            {
                throw new ArgumentNullException("packages");
            }
            files.RemoveInstalledPackageRecords(packages);
        }
    }

    internal void RemoveInstalledPackageRecords(IEnumerable<ChartOperationTarget> targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException("targets");
        }
        List<ChartOperationTarget> remainingTargets = [.. targets.Where(target => target?.Chart != null)];
        List<ChartPackage> chartPackages = ExtractChartPackagesFromChartTargets(ref remainingTargets, isInstalled: true);
        RemoveInstalledPackageRecords(chartPackages);
    }

    private void SearchCorrectInstallationDirectoryCharts(IEnumerable<PackageChartEntry> chartEntries)
    {
        if (files != null)
        {
            if (chartEntries == null)
            {
                throw new ArgumentNullException(nameof(chartEntries));
            }
            files.SearchCorrectInstallationDirectoryCharts(chartEntries);
            UpdateSharedChartTransientStates(chartEntries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
            InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
        }
    }

    internal void SearchCorrectInstallationDirectoryCharts(IRepairInstalledLocationTargetSnapshot targets)
    {
        RepairInstalledLocationTargetSnapshot snapshot = AsRepairInstalledLocationTargetSnapshot(targets);
        if (snapshot?.HasTargets != true)
        {
            return;
        }
        SearchCorrectInstallationDirectoryCharts(snapshot.RepairEntries);
    }

    public void ClearInstallDestinationForPendingPackages(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException("packages");
        }
        List<ChartPackage> list = [.. packages.Where(f => f != null)];
        for (int num = 0; num < list.Count; num++)
        {
            ClearChartPackageInstallDestinations(list[num]);
        }
        InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
    }

    internal void ClearInstallDestinationForPendingCharts(PendingInstallDestinationTargetSnapshot targets)
    {
        if (targets == null)
        {
            throw new ArgumentNullException("targets");
        }
        if (files == null)
        {
            return;
        }
        if (!targets.HasTargets)
        {
            return;
        }
        List<ChartOperationTarget> packageTargets = targets.PackageTargets.ToList();
        List<ChartPackage> packages = ExtractChartPackagesFromChartTargets(ref packageTargets);
        for (int num = 0; num < packages.Count; num++)
        {
            ClearChartPackageInstallDestinations(packages[num]);
        }
        if (targets.LooseEntries.Count > 0)
        {
            files.RemoveInstallDestination(targets.LooseEntries);
            UpdateSharedChartTransientStates(targets.LooseEntries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
        }
        InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
    }

    internal void ClearInstallDestinationForCharts(IRepairInstalledLocationTargetSnapshot targets)
    {
        RepairInstalledLocationTargetSnapshot snapshot = AsRepairInstalledLocationTargetSnapshot(targets);
        if (snapshot?.HasTargets != true)
        {
            return;
        }
        ClearInstallDestinationForCharts(snapshot.RepairEntries);
    }

    private void ClearInstallDestinationForCharts(IEnumerable<PackageChartEntry> chartEntries)
    {
        if (files != null)
        {
            if (chartEntries == null)
            {
                throw new ArgumentNullException(nameof(chartEntries));
            }
            List<PackageChartEntry> entries = [.. chartEntries.Where(entry => entry?.Chart != null)];
            List<ChartPackage> chartPackages = ExtractChartPackagesFromChartEntries(ref entries);
            for (int num = 0; num < chartPackages.Count; num++)
            {
                ClearChartPackageInstallDestinations(chartPackages[num]);
            }
            files.RemoveInstallDestination(entries);
            UpdateSharedChartTransientStates(entries.Select(entry => entry?.Chart), forceInstallDestinationProjection: true);
            InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
        }
    }

    private static void ClearChartPackageInstallDestinations(ChartPackage chartPackage)
    {
        foreach (PackageChartEntry entry in chartPackage?.ChartEntries ?? [])
        {
            entry?.ClearInstallDestination();
        }
    }

    internal bool SetPendingInstallDestination(PendingInstallDestinationEditTargetSnapshot target, string destinationDirectory)
    {
        if (files == null)
        {
            return false;
        }
        if (target == null)
        {
            throw new ArgumentNullException("target");
        }
        lock (lockCopyFile)
        {
            bool changed;
            PackageChartEntry changedEntry;
            if (target.PackageEntry != null)
            {
                changedEntry = target.PackageEntry;
                changed = files.SetPendingInstallDestination(changedEntry, destinationDirectory);
            }
            else
            {
                PackageChartEntry chartEntry = target.GetOrCreateChartEntry();
                if (chartEntry == null)
                {
                    return false;
                }
                changedEntry = chartEntry;
                changed = files.SetPendingInstallDestination(changedEntry, destinationDirectory);
            }
            if (changed)
            {
                UpdateSharedChartTransientStates([changedEntry.Chart], forceInstallDestinationProjection: true);
                InvalidateNormalLibrarySortKeys(NormalLibraryInstallDestinationChangedReason);
            }
            return changed;
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

    internal void EnqueueExternalPlaylistBMSTableImport(Uri uri)
    {
        EnqueueExternalPlaylistBMSTableImports([uri]);
    }

    internal void EnqueueExternalPlaylistBMSTableImports(IEnumerable<Uri> uris)
    {
        if (externalPlaylistImportQueue.EnqueueRange(uris))
        {
            _ = DrainExternalPlaylistImportQueueAsync().Logging("DrainExternalPlaylistImportQueueAsync");
        }
    }

    private async Task DrainExternalPlaylistImportQueueAsync()
    {
        List<ExternalPlaylistImportOutcome> outcomes = [];
        int completedCount = 0;
        BeginPlaylistSyncProgressOperation();
        try
        {
            while (externalPlaylistImportQueue.TryDequeue(out Uri uri))
            {
                UpdateExternalPlaylistImportQueueProgress(completedCount, uri, string.Empty, hasActiveImport: true);
                ExternalPlaylistImportOutcome outcome = await ImportExternalPlaylistBMSTableCoreAsync(uri, showFailureDialog: false, skipDuplicateName: true).Logging("ImportExternalPlaylistBMSTableCoreAsync");
                outcomes.Add(outcome);
                completedCount++;
                UpdateExternalPlaylistImportQueueProgress(completedCount, uri, outcome?.TableName ?? string.Empty, hasActiveImport: false);
            }
        }
        finally
        {
            EndPlaylistSyncProgressOperation();
        }
        ShowExternalPlaylistImportQueueSummary(new ExternalPlaylistImportQueueSummary(outcomes));
    }

    private void UpdateExternalPlaylistImportQueueProgress(int completedCount, Uri currentUri, string currentTableName, bool hasActiveImport)
    {
        UpdatePlaylistSyncProgressStatus(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = ResolveExternalPlaylistImportQueueProgressTotal(completedCount, hasActiveImport, externalPlaylistImportQueue.PendingCount),
            CompletedTableCount = completedCount,
            CurrentTableName = currentTableName ?? string.Empty,
            CurrentUri = currentUri,
            LabelFormat = BeMusicSeeker.Properties.Resources.Playlist_import_progress_label_format,
            SingleLabel = BeMusicSeeker.Properties.Resources.Playlist_import_progress_single_label
        });
    }

    internal static int ResolveExternalPlaylistImportQueueProgressTotal(int completedCount, bool hasActiveImport, int pendingCount)
    {
        int normalizedCompletedCount = Math.Max(0, completedCount);
        int normalizedPendingCount = Math.Max(0, pendingCount);
        return Math.Max(normalizedCompletedCount + (hasActiveImport ? 1 : 0) + normalizedPendingCount, normalizedCompletedCount);
    }

    private async Task<ExternalPlaylistImportOutcome> ImportExternalPlaylistBMSTableCoreAsync(Uri uri, bool showFailureDialog, bool skipDuplicateName)
    {
        try
        {
            BMSTable table = await tables.RegistrateExternalTableAsync(uri);
            if (table == null)
            {
                return ExternalPlaylistImportOutcome.Failed(uri, new InvalidOperationException("Playlist registration returned no table."));
            }
            tables.AcquireReaderLockBMSTables();
            try
            {
                files.AddReferenceBMSTables(table);
                InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
            }
            finally
            {
                tables.FreeReaderLockBMSTables();
            }
            UpdatePlaylistSyncRuntimeStatus(PlaylistSyncAttemptResult.CreateSuccess(table, table, uri, updated: false));
            RefreshPlaylistSummaryIfVisible("playlist_registered", invalidateTableCountCache: true);
            return ExternalPlaylistImportOutcome.Imported(uri, table.name);
        }
        catch (PlaylistAlreadyExistsException ex) when (skipDuplicateName)
        {
            NLogWrapper.FileLogger?.Info("playlist_register_skipped_duplicate_name uri=" + (uri?.ToString() ?? string.Empty) + " table=" + (ex.PlaylistName ?? string.Empty));
            return ExternalPlaylistImportOutcome.SkippedDuplicateName(uri, ex.PlaylistName, ex);
        }
        catch (InvalidOperationException ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "playlist_register_failed uri=" + (uri?.ToString() ?? string.Empty));
            if (showFailureDialog)
            {
                ShowPlaylistLoadFailure(ex);
            }
            return ExternalPlaylistImportOutcome.Failed(uri, ex);
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "playlist_register_failed uri=" + (uri?.ToString() ?? string.Empty));
            if (showFailureDialog)
            {
                ShowPlaylistLoadFailure(ex);
            }
            return ExternalPlaylistImportOutcome.Failed(uri, ex);
        }
    }

    private void ShowExternalPlaylistImportQueueSummary(ExternalPlaylistImportQueueSummary summary)
    {
        if (summary == null || !summary.HasNotifiableItems)
        {
            return;
        }
        var message = new StringBuilder();
        message.AppendFormat(
            BeMusicSeeker.Properties.Resources.Playlist_import_result_summary_format,
            summary.ImportedCount,
            summary.SkippedDuplicateNameCount,
            summary.FailedCount);
        AppendImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Playlist_import_result_skipped_header, summary.SkippedDuplicateNameOutcomes);
        AppendImportOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Playlist_import_result_failed_header, summary.FailedOutcomes);
        base.Messenger.Raise(new ConfirmationMessage(
            message.ToString(),
            BeMusicSeeker.Properties.Resources.Playlist_import_result_title,
            summary.FailedCount > 0 ? MessageBoxImage.Exclamation : MessageBoxImage.Information,
            MessageBoxButton.OK,
            "ConfirmationDialog"));
    }

    private static void AppendImportOutcomeSamples(StringBuilder message, string header, IReadOnlyList<ExternalPlaylistImportOutcome> outcomes)
    {
        const int maxSamples = 5;
        if (message == null || outcomes == null || outcomes.Count == 0)
        {
            return;
        }
        message.AppendLine();
        message.AppendLine();
        message.AppendLine(header);
        foreach (ExternalPlaylistImportOutcome outcome in outcomes.Take(maxSamples))
        {
            string nameOrUri = !string.IsNullOrWhiteSpace(outcome.TableName) ? outcome.TableName : (outcome.Uri?.ToString() ?? string.Empty);
            if (outcome.Kind == ExternalPlaylistImportOutcomeKind.Failed && outcome.Exception != null && !string.IsNullOrWhiteSpace(outcome.Exception.Message))
            {
                message.AppendLine("- " + nameOrUri + " (" + outcome.Exception.Message + ")");
            }
            else
            {
                message.AppendLine("- " + nameOrUri);
            }
        }
        if (outcomes.Count > maxSamples)
        {
            message.AppendLine("- ...");
        }
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
        if (snapshot.TryGetValue(playlistSyncStatusKey, out PlaylistSyncRuntimeStatus value) && value != null)
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

    internal void AddChartRowsToFolderBMSTable(IEnumerable<object> rows, BMSTable bmsTable, string folderName = "")
    {
        folderName ??= string.Empty;
        if (rows == null)
        {
            throw new ArgumentNullException(nameof(rows));
        }
        if (bmsTable.is_external_sync)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_add_playlist_entry, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            return;
        }
        tables.EnsurePlaylistEntriesLoaded(bmsTable, "MainWindowViewModel.AddChartRowsToFolderBMSTable");
        List<object> sourceRows = [.. rows.Where(row => row != null)];
        if (sourceRows.Count == 0)
        {
            return;
        }
        if (sourceRows.All(GridRowResolver.IsPlaylistRow))
        {
            List<BMSTableEntry> list = [.. sourceRows.Select(GridRowResolver.GetPlaylistEntry).Where(entry => entry != null && entry.parent == bmsTable)];
            List<BMSTableEntry> second = [.. list.Where(entry => string.Equals(entry.folder ?? string.Empty, folderName, StringComparison.Ordinal))];
            if (bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder && string.IsNullOrWhiteSpace(folderName))
            {
                tables.RemoveEntriesBMSTable(list, bmsTable, commitFlag: false);
            }
            else
            {
                sourceRows = [.. sourceRows.Where(row => !second.Contains(GridRowResolver.GetPlaylistEntry(row)))];
                if (sourceRows.Count == 0)
                {
                    return;
                }
                tables.RemoveEntriesBMSTable(list.Except(second), bmsTable, commitFlag: false);
            }
        }
        List<ChartFile> resolvedCharts = [.. sourceRows.Select(ResolvePlaylistDropChart).Where(chart => chart != null)];
        if (bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder && string.IsNullOrWhiteSpace(folderName))
        {
            List<object> playlistEntryRows = [.. sourceRows.Where(ShouldPreservePlaylistEntryForRootFolderDrop)];
            List<ChartFile> source = [.. sourceRows.Except(playlistEntryRows).Select(ResolvePlaylistDropChart).Where(chart => chart != null)];
            tables.AddPlaylistEntriesToFolderBMSTable(playlistEntryRows.Select(row => GridRowResolver.GetPlaylistEntry(row)?.Duplicate()).Where(entry => entry != null), bmsTable, folderName, commitFlag: false);
            if (BMSFiles == null)
            {
                return;
            }
            foreach (IGrouping<string, ChartFile> item in source.GroupBy(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path), StringComparer.OrdinalIgnoreCase).ToList())
            {
                List<string> md5sInTheSameDir = files.GetPlaylistFolderOrgMd5sForCharts(item);
                string text = null;
                if (md5sInTheSameDir.Count > 0)
                {
                    text = bmsTable.folder_list.Where(folder => !string.IsNullOrWhiteSpace(folder)).FirstOrDefault(folder => (from e in bmsTable.entries
                                                                                                                              where e.folder == folder
                                                                                                                              select e.md5).ToList().Intersect(md5sInTheSameDir, StringComparer.OrdinalIgnoreCase).Any());
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    text = BMSLibrary.GetLongestCommonChartInfo(item.Select(chart => chart.Title));
                    text = tables.CreateNewFolderBMSTable(bmsTable, text, commitFlag: false);
                }
                tables.AddPlaylistEntriesToFolderBMSTable(item.Select(chart => BMSTableEntry.CreateForPlaylistDrop(chart, md5sInTheSameDir)), bmsTable, text, commitFlag: false);
            }
        }
        else
        {
            ParallelQuery<BMSTableEntry> bmsEntries = from row in sourceRows.AsParallel()
                                                      let entry = GridRowResolver.GetPlaylistEntry(row)
                                                      let chart = ResolvePlaylistDropChart(row)
                                                      where entry != null || chart != null
                                                      select (entry != null) ? entry.Duplicate() : BMSTableEntry.CreateForPlaylistDrop(chart, GetPlaylistDropOrgMd5(chart));
            tables.AddPlaylistEntriesToFolderBMSTable(bmsEntries, bmsTable, folderName, commitFlag: false);
        }
        tables.ReOutputCustomFolderAndCommitToDB(bmsTable);
        tables.AcquireReaderLockBMSTables();
        files.AddReferenceBMSTablesToCharts(bmsTable, resolvedCharts);
        tables.FreeReaderLockBMSTables();
        RefreshChartRowsViewForPlaylist(bmsTable);
        InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
    }

    /// <summary>
    /// root folder drop 時に playlist entry をそのまま複製すべき行かどうかを返します。
    /// 実体 chart adapter が作れる行は BMS / bmson を問わず chart として再追加し、未所持 playlist entry だけを entry 複製として残します。
    /// </summary>
    internal static bool ShouldPreservePlaylistEntryForRootFolderDrop(object row)
    {
        return GridRowResolver.GetPlaylistEntry(row) != null
            && ResolvePlaylistDropChart(row) == null;
    }

    internal static ChartFile ResolvePlaylistDropChart(object row)
    {
        return GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target)
            && !target.IsPlaylistMissing
            ? target.Chart
            : null;
    }

    private List<string> GetPlaylistDropOrgMd5(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }
        return files.GetPlaylistOrgMd5sForChart(chart);
    }

    internal void DeleteBMSTableEntries(IEnumerable<BMSTableEntry> bmsEntries, BMSTable bmsTable)
    {
        if (bmsTable.is_external_sync)
        {
            base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_remove_playlist_entry, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            return;
        }
        tables.RemoveEntriesBMSTable(bmsEntries, bmsTable);
        RefreshChartRowsViewForPlaylist(bmsTable);
        tables.AcquireReaderLockBMSTables();
        files.RemoveReferenceBMSTables(bmsTable, bmsEntries);
        tables.FreeReaderLockBMSTables();
        InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
    }

    private void RefreshChartRowsViewForPlaylist(BMSTable bmsTableUpdated)
    {
        RefreshPlaylistSummaryIfVisible("playlist_entries_updated", invalidateTableCountCache: true);
        BMSTable bMSTable;
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
            RefreshChartRowsView(viewUpdateMode.TreeViewFilterNotChanged);
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
            lr2config.RemoveBMSSearchDirectories([customFolderOutputDirectory]);
            lr2config.Save();
        }
        files.RemoveReferenceBMSTables(bmsTable);
        InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason);
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
        void action()
        {
            try
            {
                if (!LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0)))
                {
                    throw new TimeoutException(BeMusicSeeker.Properties.Resources.Msg_error_timeout_dblock_restore);
                }
                tables.LoadPlaylistDump(lines);
                tables.ReloadTables();
                tables.QueueBeatorajaBmtExportAll("RestoreBMSTables");
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_success_playlist_restore, BeMusicSeeker.Properties.Resources.Success, MessageBoxImage.Asterisk, MessageBoxButton.OK, "ConfirmationDialog"));
            }
            catch (Exception ex)
            {
                base.Messenger.Raise(new ConfirmationMessage(BeMusicSeeker.Properties.Resources.Msg_failed_playlist_restore + Environment.NewLine + Environment.NewLine + ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, MessageBoxButton.OK, "ConfirmationDialog"));
            }
        }
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
        bool unlockAfterOperation = !LR2SongDBExtended.IsProcessLockEnteredByCurrentThread();
        if (!LR2SongDBExtended.Lock(new TimeSpan(0, 1, 0)))
        {
            throw new TimeoutException(BeMusicSeeker.Properties.Resources.Msg_error_timeout_dblock_uninstall);
        }
        try
        {
            using var lR2SongDBExtended = new LR2SongDBExtended(Settings.Default.LR2SongDBPath);
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
        finally
        {
            if (unlockAfterOperation)
            {
                LR2SongDBExtended.Unlock();
            }
        }
    }

    public void MergeChartDirectory(string src, string dst)
    {
        lock (lockCopyFile)
        {
            PlayEndBMSFile(closeProcess: true);
            files.MergeChartDirectory(src, dst);
        }
    }

    internal void FixInstallationDirectoryCharts(IRepairInstalledLocationTargetSnapshot targets)
    {
        RepairInstalledLocationTargetSnapshot snapshot = AsRepairInstalledLocationTargetSnapshot(targets);
        if (snapshot?.HasTargets != true)
        {
            return;
        }
        lock (lockCopyFile)
        {
            IReadOnlyList<ChartFile> repairCharts = snapshot.RepairCharts;
            stopPlayingChartFiles(GetBmsFormatCharts(repairCharts));
            files.FixInstallationDirectoryCharts(repairCharts);
        }
    }

    internal List<string> GetLibraryWholeFolderDeleteConfirmationPaths(IEnumerable<ChartOperationTarget> targets)
    {
        List<LibraryChartRef> charts = [.. ToLibraryChartRefs(targets, ChartOperationCapabilities.RemoveFromLibrary)];
        if (charts.Count == 0)
        {
            return [];
        }
        lock (lockCopyFile)
        {
            return files.GetLibraryWholeFolderDeleteConfirmationPaths(charts);
        }
    }

    internal void RemoveLibraryCharts(IEnumerable<ChartOperationTarget> targets, IEnumerable<string> approvedWholeFolderDeletePaths = null)
    {
        List<LibraryChartRef> charts = [.. ToLibraryChartRefs(targets, ChartOperationCapabilities.RemoveFromLibrary)];
        if (charts.Count == 0)
        {
            return;
        }
        lock (lockCopyFile)
        {
            stopPlayingLibraryCharts(GetBmsLibraryChartRefs(charts));
            stopPlayingChartDirectories(approvedWholeFolderDeletePaths);
            files.RemoveLibraryCharts(charts, approvedWholeFolderDeletePaths: approvedWholeFolderDeletePaths);
        }
    }

    internal void RemoveLibraryCharts(IEnumerable<ChartFile> charts, IEnumerable<string> approvedWholeFolderDeletePaths = null)
    {
        List<ChartFile> chartSnapshot = [.. (charts ?? []).Where(chart => chart != null)];
        List<LibraryChartRef> chartRefs = [.. chartSnapshot
            .Select(chart => LibraryChartRef.FromChartFile(chart))
            .Where(chart => chart != null)];
        if (chartRefs.Count == 0)
        {
            return;
        }
        lock (lockCopyFile)
        {
            stopPlayingChartFiles(GetBmsFormatCharts(chartSnapshot));
            stopPlayingChartDirectories(approvedWholeFolderDeletePaths);
            files.RemoveLibraryCharts(chartRefs, approvedWholeFolderDeletePaths: approvedWholeFolderDeletePaths);
        }
    }

    internal void RemovePendingCharts(IEnumerable<ChartOperationTarget> targets, bool sendToRecycleBin = true, bool deleteContainingPackageFoldersWhenNoBms = false)
    {
        List<ChartFile> charts = [.. (targets ?? [])
            .Where(target => target != null && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
            .Select(target => target.Chart)
            .Where(chart => chart != null)];
        RunPendingInstallMutation(delegate
        {
            files.RemovePendingCharts(charts, sendToRecycleBin, deleteContainingPackageFoldersWhenNoBms);
        }, deleteContainingPackageFoldersWhenNoBms ? charts : GetBmsFormatCharts(charts));
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

    internal void RenameBMSFilesExtensions(IEnumerable<ChartFile> charts, string newExt)
    {
        List<ChartFile> chartList = GetBmsFormatCharts(charts);
        lock (lockCopyFile)
        {
            stopPlayingChartFiles(chartList);
            files.RenameBMSFilesExtensions(chartList, newExt, true);
        }
    }

    internal void RenamePendingBmsFormatChartFileExtensions(IEnumerable<ChartFile> charts, string newExt)
    {
        List<ChartFile> chartList = GetBmsFormatCharts(charts);
        RunPendingInstallMutation(delegate
        {
            files.RenamePendingBmsFormatChartFileExtensions(chartList, newExt);
        }, chartList);
    }

    internal RenameChartFolderTargetSnapshot CreateRenameChartFolderTargetSnapshot(ChartOperationTarget target)
    {
        if (target == null
            || !target.HasCapability(ChartOperationCapabilities.MoveInLibrary)
            || string.IsNullOrWhiteSpace(target.Chart?.Path))
        {
            return RenameChartFolderTargetSnapshot.Empty;
        }
        return new RenameChartFolderTargetSnapshot(target.Chart);
    }

    internal void RenameChartFolder(RenameChartFolderTargetSnapshot target, string newFolder)
    {
        string chartPath = target?.Chart?.Path;
        if (target == null
            || string.IsNullOrWhiteSpace(chartPath)
            || string.IsNullOrWhiteSpace(newFolder))
        {
            return;
        }
        lock (lockCopyFile)
        {
            stopPlayingChartFiles([target.Chart]);
            string directoryNameSimple = DirectoryExt.GetDirectoryNameSimple(chartPath);
            if (!string.IsNullOrWhiteSpace(directoryNameSimple) && Directory.Exists(directoryNameSimple))
            {
                files.RenameChartFolder(directoryNameSimple, newFolder, false);
                InvalidateNormalLibrarySortKeysAfterPathMutation(hasBmsPathMutation: true, hasBmsonPathMutation: true);
            }
        }
    }

    internal sealed class RenameChartFolderTargetSnapshot
    {
        internal static RenameChartFolderTargetSnapshot Empty { get; } = new(null);

        internal RenameChartFolderTargetSnapshot(ChartFile chart)
        {
            Chart = chart;
        }

        internal ChartFile Chart { get; }

        internal bool HasTarget => Chart != null;
    }

    public void AutoRenameAllChartFolders(string parentDir = null)
    {
        List<ChartFile> charts = GetLibraryChartsForFolderOperations();
        if (charts.Count == 0)
        {
            return;
        }
        IEnumerable<ChartFile> enumerable = charts;
        lock (lockCopyFile)
        {
            PlayEndBMSFile(closeProcess: true);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                enumerable = enumerable.Where(chart => chart.Path.StartsWith(parentDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            }
            List<ChartFile> targetCharts = [.. enumerable.Where(chart => chart != null)];
            if (targetCharts.Count == 0)
            {
                return;
            }
            files.AutoRenameChartFolders(targetCharts);
            InvalidateNormalLibrarySortKeysAfterPathMutation(hasBmsPathMutation: true, hasBmsonPathMutation: true);
        }
    }

    private List<ChartFile> GetLibraryChartsForFolderOperations()
    {
        List<ChartFile> charts = [.. (BMSFiles ?? []).Where(file => file != null).Select(file => ChartFileProjection.FromBmsFile(file))];
        charts.AddRange((files?.BmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
            .Select(song => ChartFileProjection.FromBmsonSong(song))
            .Where(chart => chart != null));
        return charts;
    }

    internal void AutoRenameChartFolders(IEnumerable<ChartFile> chartFilesSource)
    {
        List<ChartFile> charts = [.. (chartFilesSource ?? []).Where(chart => chart != null)];
        if (charts.Count == 0)
        {
            return;
        }
        lock (lockCopyFile)
        {
            stopPlayingChartFiles(charts);
            files.AutoRenameChartFolders(charts);
            InvalidateNormalLibrarySortKeysAfterPathMutation(hasBmsPathMutation: true, hasBmsonPathMutation: true);
        }
    }

    internal void AutoRenameChartFolders(ChartOperationTargetSnapshot targets)
    {
        if (targets?.Charts.Count > 0 != true)
        {
            return;
        }
        AutoRenameChartFolders(targets.Charts);
    }

    internal void MoveLibraryCharts(IEnumerable<ChartOperationTarget> targets, string newParentDirectory)
    {
        List<LibraryChartRef> charts = [.. ToLibraryChartRefs(targets, ChartOperationCapabilities.MoveInLibrary).Where(chart => !string.IsNullOrWhiteSpace(chart.Path))];
        if (charts.Count == 0 || string.IsNullOrWhiteSpace(newParentDirectory))
        {
            return;
        }
        lock (lockCopyFile)
        {
            stopPlayingLibraryCharts(charts);
            files.MoveLibraryRootFolder(charts, newParentDirectory, false);
            InvalidateNormalLibrarySortKeysAfterPathMutation(hasBmsPathMutation: true, hasBmsonPathMutation: true);
        }
    }

    private static IEnumerable<LibraryChartRef> ToLibraryChartRefs(IEnumerable<ChartOperationTarget> targets, ChartOperationCapabilities requiredCapability)
    {
        return (targets ?? [])
            .Where(target => target != null && target.HasCapability(requiredCapability))
            .Select(target => target.ToLibraryChartRef())
            .Where(chart => chart != null);
    }

    internal IRepairInstalledLocationTargetSnapshot CreateRepairInstalledLocationTargetSnapshot(IEnumerable<ChartOperationTarget> targets)
    {
        List<ChartOperationTarget> targetList = [.. (targets ?? []).Where(target => target != null && target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation))];
        List<ChartFile> charts = [.. targetList.Select(target => target.Chart).Where(chart => chart != null)];
        return new RepairInstalledLocationTargetSnapshot(
            charts,
            () => [.. targetList
                .Select(target => target.ToPackageChartEntry())
                .Where(entry => entry?.Chart != null)]);
    }

    internal PendingInstallDestinationTargetSnapshot CreatePendingInstallDestinationTargetSnapshot(IEnumerable<ChartOperationTarget> targets)
    {
        List<ChartOperationTarget> remainingTargets = [.. (targets ?? [])
            .Where(target => target?.Chart != null && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))];
        List<ChartOperationTarget> packageTargets = [.. remainingTargets.Where(target => target.PackageEntry != null)];
        remainingTargets = [.. remainingTargets.Where(target => target.PackageEntry == null)];
        List<ChartFile> charts = [.. remainingTargets.Select(target => target.Chart).Where(chart => chart != null)];
        return new PendingInstallDestinationTargetSnapshot(
            packageTargets,
            charts,
            () => [.. remainingTargets
                .Select(target => target.ToPackageChartEntry())
                .Where(entry => entry?.Chart != null)]);
    }

    internal PendingInstallDestinationEditTargetSnapshot CreatePendingInstallDestinationEditTargetSnapshot(ChartOperationTarget target)
    {
        if (target == null || !target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination))
        {
            return PendingInstallDestinationEditTargetSnapshot.Empty;
        }
        if (target.PackageEntry != null)
        {
            return new PendingInstallDestinationEditTargetSnapshot(target.PackageEntry, null, null);
        }
        return new PendingInstallDestinationEditTargetSnapshot(
            null,
            target.Chart,
            target.ToPackageChartEntry);
    }

    internal sealed class PendingInstallDestinationTargetSnapshot
    {
        private readonly Lazy<IReadOnlyList<PackageChartEntry>> looseEntries;

        internal PendingInstallDestinationTargetSnapshot(
            IEnumerable<ChartOperationTarget> packageTargets,
            IEnumerable<ChartFile> charts,
            Func<IReadOnlyList<PackageChartEntry>> looseEntryFactory)
        {
            PackageTargets = [.. (packageTargets ?? []).Where(target => target?.PackageEntry != null && target.Chart != null)];
            Charts = [.. (charts ?? []).Where(chart => chart != null)];
            looseEntries = new Lazy<IReadOnlyList<PackageChartEntry>>(
                () => [.. (looseEntryFactory?.Invoke() ?? []).Where(entry => entry?.Chart != null)]);
        }

        internal IReadOnlyList<ChartOperationTarget> PackageTargets { get; }

        internal IReadOnlyList<ChartFile> Charts { get; }

        internal IReadOnlyList<PackageChartEntry> LooseEntries => looseEntries.Value;

        internal bool HasTargets => PackageTargets.Count > 0 || Charts.Count > 0;

        internal void MaterializeLooseEntries()
        {
            _ = LooseEntries.Count;
        }
    }

    internal sealed class PendingInstallDestinationEditTargetSnapshot
    {
        private readonly Lazy<PackageChartEntry> chartEntry;

        internal static PendingInstallDestinationEditTargetSnapshot Empty { get; } = new(null, null, null);

        internal PendingInstallDestinationEditTargetSnapshot(PackageChartEntry packageEntry, ChartFile chartFile, Func<PackageChartEntry> chartEntryFactory)
        {
            PackageEntry = packageEntry;
            ChartFile = chartFile;
            chartEntry = new Lazy<PackageChartEntry>(() => chartEntryFactory?.Invoke());
        }

        internal PackageChartEntry PackageEntry { get; }

        internal ChartFile ChartFile { get; }

        internal bool HasTarget => PackageEntry != null || ChartFile != null;

        internal PackageChartEntry GetOrCreateChartEntry()
        {
            return PackageEntry ?? chartEntry.Value;
        }
    }

    internal ChartOperationTargetSnapshot CreateChartOperationTargetSnapshot(IEnumerable<ChartOperationTarget> targets, ChartOperationCapabilities requiredCapability)
    {
        List<ChartOperationTarget> targetList = [.. (targets ?? []).Where(target => target != null && target.HasCapability(requiredCapability))];
        List<ChartFile> charts = [.. targetList.Select(target => target.Chart).Where(chart => chart != null)];
        return new ChartOperationTargetSnapshot(charts);
    }

    internal sealed class ChartOperationTargetSnapshot
    {
        internal ChartOperationTargetSnapshot(IEnumerable<ChartFile> charts)
        {
            Charts = [.. (charts ?? []).Where(chart => chart != null)];
        }

        internal IReadOnlyList<ChartFile> Charts { get; }

        internal bool HasTargets => Charts.Count > 0;
    }

    internal interface IRepairInstalledLocationTargetSnapshot
    {
        bool HasTargets { get; }

        bool HasInstallDestination { get; }

        IReadOnlyList<ChartFile> RepairCharts { get; }

        void MaterializeRepairEntries();
    }

    private static RepairInstalledLocationTargetSnapshot AsRepairInstalledLocationTargetSnapshot(IRepairInstalledLocationTargetSnapshot snapshot)
    {
        return snapshot as RepairInstalledLocationTargetSnapshot;
    }

    private sealed class RepairInstalledLocationTargetSnapshot : IRepairInstalledLocationTargetSnapshot
    {
        private readonly Lazy<IReadOnlyList<PackageChartEntry>> repairEntries;

        internal RepairInstalledLocationTargetSnapshot(IEnumerable<ChartFile> charts, Func<IReadOnlyList<PackageChartEntry>> repairEntryFactory)
        {
            Charts = [.. (charts ?? []).Where(chart => chart != null)];
            repairEntries = new Lazy<IReadOnlyList<PackageChartEntry>>(
                () => [.. (repairEntryFactory?.Invoke() ?? []).Where(entry => entry?.Chart != null)]);
            repairCharts = new Lazy<IReadOnlyList<ChartFile>>(CreateRepairCharts);
        }

        private readonly Lazy<IReadOnlyList<ChartFile>> repairCharts;

        internal bool HasTargets => Charts.Count > 0;

        public bool HasInstallDestination => RepairCharts.Any(chart => !string.IsNullOrWhiteSpace(chart.InstallDestination));

        internal IReadOnlyList<ChartFile> Charts { get; }

        public IReadOnlyList<ChartFile> RepairCharts => repairCharts.Value;

        internal IReadOnlyList<PackageChartEntry> RepairEntries => repairEntries.Value;

        private IReadOnlyList<ChartFile> CreateRepairCharts()
        {
            IReadOnlyList<PackageChartEntry> entries = RepairEntries;
            if (entries.Count == 0)
            {
                return Charts;
            }

            var entriesByChartKey = entries
                .Select(entry => new
                {
                    Entry = entry,
                    Key = CreateRepairChartKey(entry.Chart)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            return [.. Charts.Select(chart =>
            {
                string key = CreateRepairChartKey(chart);
                if (string.IsNullOrWhiteSpace(key) || !entriesByChartKey.TryGetValue(key, out var entry))
                {
                    return chart;
                }
                if (!HasInstallDestinationState(entry.Entry.Chart))
                {
                    return chart;
                }

                return ChartFileProjection.WithPackageState(
                    chart,
                    entry.Entry.Chart.InstallDestination,
                    entry.Entry.Chart.InstallDestinationTitle,
                    entry.Entry.Chart.InstallDestinationArtist,
                    entry.Entry.Chart.InstallDestinationSuggestions,
                    chart.Warnings);
            })];
        }

        private static bool HasInstallDestinationState(ChartFile chart)
        {
            return chart != null
                && (!string.IsNullOrWhiteSpace(chart.InstallDestination)
                    || !string.IsNullOrWhiteSpace(chart.InstallDestinationTitle)
                    || !string.IsNullOrWhiteSpace(chart.InstallDestinationArtist)
                    || (chart.InstallDestinationSuggestions?.Count ?? 0) > 0);
        }

        private static string CreateRepairChartKey(ChartFile chart)
        {
            if (chart == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(chart.Path))
            {
                return "path:" + chart.Path;
            }

            string hash = chart.PrimaryLookupHash;
            return string.IsNullOrWhiteSpace(hash) ? null : "hash:" + hash;
        }

        public void MaterializeRepairEntries()
        {
            _ = RepairEntries.Count;
        }

        bool IRepairInstalledLocationTargetSnapshot.HasTargets => HasTargets;
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
                List<string> normalizedHashes = [.. hashes.Where(hash => !string.IsNullOrWhiteSpace(hash)).Distinct(StringComparer.OrdinalIgnoreCase)];
                if (normalizedHashes.Count == 0)
                {
                    return;
                }
                List<BMSLibrary.IRDataCacheInfo> iRDataNeedUpdates = files.GetIRDataNeedUpdates(normalizedHashes);
                if (iRDataNeedUpdates.Count > 0)
                {
                    if (DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_download_ranking_cache + Environment.NewLine + Environment.NewLine + BeMusicSeeker.Properties.Resources.Download + ": " + iRDataNeedUpdates.Count + Environment.NewLine + BeMusicSeeker.Properties.Resources.Skip + ": " + (normalizedHashes.Count - iRDataNeedUpdates.Count) + Environment.NewLine + BeMusicSeeker.Properties.Resources.Size + ": " + FileSizeHelper.GetReadableFileSize(iRDataNeedUpdates.Select(c => c.size).Sum()), BeMusicSeeker.Properties.Resources.Confirm, MessageBoxButton.OKCancel, MessageBoxImage.Asterisk, MessageBoxResult.OK) == MessageBoxResult.OK)
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
        string text = bmsFile.hash;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("MD5 not found: " + bmsFile.path);
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
        GridRowResolver.TryGetBmsPlayerFile(row, out BeMusicSeeker.Models.BMSFile realFile);
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
        List<ScoreViewerTarget> normalizedTargets = [.. targets.Where(target => target != null && !string.IsNullOrWhiteSpace(target.Hash))];
        if (normalizedTargets.Count == 0)
        {
            return null;
        }
        ScoreViewerTarget lastTarget = normalizedTargets.Last();
        bool userConfirmedMultiRegister = false;
        string resultViewUrl = null;
        if (normalizedTargets.Count > 1)
        {
            var confirmationMessage = new ConfirmationMessage(
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
                    var uploadConfirmMessage = new ConfirmationMessage(
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

    public void ConvertBMSToAudioFiles(IEnumerable<BeMusicSeeker.Models.BMSFile> bmsFiles, string saveDir, CancellationToken token = default, Action<bool> onEachCompleted = null)
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
                var bMSFile = new Ribbit.BMS.BMSFile(bmsFile.path);
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
                }.Aggregate(Settings.Default.EncodeFileNameFormat, (i, r) => i.Replace(r.Key, r.Value)).NaturalNormalizationForFileName().ReplaceInvalidFileNameCharsByWide()
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
