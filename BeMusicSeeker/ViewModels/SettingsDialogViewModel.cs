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
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Livet.Commands;
using Livet.EventListeners;
using Microsoft.VisualBasic.FileIO;
using NLog;
using Ribbit.Logging;
using Ribbit.Media.Audio;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Holds editable settings state for the settings dialog before values are applied to the application settings store.
/// </summary>
public partial class SettingsDialogViewModel : ViewModel
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

    private readonly ISettingsDialogStatePort statePort;

    private readonly ISettingsDialogWorkspacePort workspacePort;

    private readonly ISettingsDialogCustomFolderOutputPort customFolderOutputPort;

    private readonly ISettingsDialogPlayHistoryPort playHistoryPort;

    private readonly ISettingsDialogSearchRootRuntimePort searchRootRuntimePort;

    private readonly ISettingsDialogPlayerFactoryPort playerFactoryPort;

    private readonly ISettingsDialogPlaybackRuntimePort playbackRuntimePort;

    private readonly Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow;

    private ISettingDialogPresentationPort presentationPort;

    private ViewModelCommand openCommand;

    private ViewModelCommand cancelCommand;

    private bool isEditCompletionInProgress;

    private readonly IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore;

    private readonly ISettingsEditSession settingsEditSession;

    private readonly Action<Exception> reportApplyFailure;

    private readonly IUiDialogService schemaDialogs;

    private readonly ILr2PlayHistorySchemaUninstallDialogPort schemaWindowDialogs;

    private readonly ApplicationDataUninstallWorkflowOwner applicationDataUninstallWorkflow;

    private readonly AudioDeviceTestWorkflowOwner audioDeviceTestWorkflow;

    private string audioDeviceTestStatusMessage;

    private readonly IAudioDeviceCatalog audioDeviceCatalog;

    private readonly IAudioSettingsGateway audioSettingsGateway;

    private readonly IApplicationLifetimePort applicationLifetime;

    private readonly ICultureCatalog cultureCatalog;

    private readonly IExternalShellGateway externalShellGateway;

    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    /// <summary>
    /// Gets the command used by views to request the settings dialog.
    /// </summary>
    public ViewModelCommand OpenCommand => openCommand ??= new ViewModelCommand(RequestOpen);

    /// <summary>
    /// Gets the command that restores the saved settings snapshot and closes the dialog.
    /// </summary>
    public ViewModelCommand CancelCommand => cancelCommand ??= new ViewModelCommand(ExecuteCancelCommand);

    internal IExternalShellGateway ExternalShellGateway => externalShellGateway;

    /// <summary>
    /// Gets a value indicating whether an apply operation is currently completing.
    /// </summary>
    public bool IsEditCompletionInProgress
    {
        get => isEditCompletionInProgress;
        private set
        {
            if (isEditCompletionInProgress == value)
            {
                return;
            }

            isEditCompletionInProgress = value;
            RaisePropertyChanged(nameof(IsEditCompletionInProgress));
            RaisePropertyChanged(nameof(IsEditCompletionEnabled));
            RaisePropertyChanged(nameof(IsEditCancellationEnabled));
        }
    }

    /// <summary>
    /// Gets a value indicating whether the settings editor can accept another completion command.
    /// </summary>
    public bool IsEditCompletionEnabled => !IsEditCompletionInProgress && !IsAudioDeviceTestInProgress;

    /// <summary>
    /// Gets a value indicating whether the settings editor can be cancelled without leaving a failed score or file-diff reload pending.
    /// </summary>
    public bool IsEditCancellationEnabled => !IsEditCompletionInProgress
        && !IsAudioDeviceTestInProgress
        && !scoreReloadPending
        && !fileDiffReloadPending;

    /// <summary>
    /// Gets a value indicating whether the audio-device test is currently running.
    /// </summary>
    public bool IsAudioDeviceTestInProgress => audioDeviceTestWorkflow?.IsRunning == true;

    /// <summary>
    /// Gets a value indicating whether the audio-device test command can start.
    /// </summary>
    public bool IsAudioDeviceTestAvailable => !IsAudioDeviceTestInProgress;

    /// <summary>Gets the localized outcome of the most recent audio-device test.</summary>
    public string AudioDeviceTestStatusMessage
    {
        get => audioDeviceTestStatusMessage;
        private set
        {
            if (string.Equals(audioDeviceTestStatusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            audioDeviceTestStatusMessage = value;
            RaisePropertyChanged(nameof(AudioDeviceTestStatusMessage));
        }
    }

    internal bool IsScoreReloadPending => scoreReloadPending;

    internal bool IsFileDiffReloadPending => fileDiffReloadPending;

    /// <summary>
    /// Publishes a settings-dialog open request to the shell.
    /// </summary>
    internal void RequestOpen()
    {
        AudioDeviceTestStatusMessage = null;
        audioDeviceCatalog.Refresh();
        playerDeviceNames = BuildPlayerDeviceNames(audioOutputSelectionDraft.Backend);
        RaisePropertyChanged(nameof(PlayerDriverNames));
        RaisePropertyChanged(nameof(PlayerDriverIndex));
        RaisePropertyChanged(nameof(UnavailablePlayerDriverDescription));
        RaisePropertyChanged(nameof(PlayerDeviceNames));
        RaisePropertyChanged(nameof(PlayerDevice));
        RaisePropertyChanged(nameof(SelectedPlayerDevice));
        presentationPort?.OpenSettingsDialog();
    }

    /// <summary>
    /// 初回設定の言語選択 overlay を shell に要求します。
    /// </summary>
    internal void RequestInitialSetupLanguageDialog()
    {
        presentationPort?.OpenInitialSetupLanguageDialog();
    }

    internal void AttachPresentationPort(ISettingDialogPresentationPort port)
    {
        presentationPort = port ?? throw new ArgumentNullException(nameof(port));
    }

    internal void DetachPresentationPort(ISettingDialogPresentationPort port)
    {
        if (ReferenceEquals(presentationPort, port))
        {
            presentationPort = null;
        }
    }

    private void ClosePresentation()
    {
        presentationPort?.CloseSettingsDialog();
    }

    private void RefreshPresentationAppearance()
    {
        presentationPort?.RefreshAppearanceSelection();
    }

    private void ExecuteCancelCommand()
    {
        if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress || scoreReloadPending || fileDiffReloadPending)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        bool reset = false;
        if (HasPendingSettingChanges())
        {
            reset = true;
            ResetSettings();
            RefreshPresentationAppearance();
        }

        ClosePresentation();
        LogSettingsPerformance(
            "settings_cancel",
            stopwatch,
            "reset=" + reset.ToString().ToLowerInvariant());
    }

    /// <summary>
    /// Completes the settings edit through validation, durable persistence, and the existing reload handoff.
    /// </summary>
    internal async Task ApplySettingsAsync()
    {
        if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress)
        {
            return;
        }

        IsEditCompletionInProgress = true;
        var totalStopwatch = Stopwatch.StartNew();
        long validationMs = 0L;
        long saveMs = 0L;
        string outcome = "unknown";
        RestartMode needRestart = RestartMode.None;
        bool shouldInitializeAfterSave = false;
        try
        {
            if (statePort.IsLibraryOperationInProgress)
            {
                outcome = "blocked_operation";
                totalStopwatch.Stop();
                ShowUiMessage(
                    BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization,
                    BeMusicSeeker.Properties.Resources.Warning,
                    MessageBoxImage.Exclamation,
                    "Settings apply blocked notification");
                ResetSettings();
                RefreshPresentationAppearance();
                return;
            }

            if (statePort.HasActiveLibraryProfile && !HasPendingSettingChanges())
            {
                outcome = "no_changes";
                ClosePresentation();
                return;
            }

            shouldInitializeAfterSave = !statePort.HasActiveLibraryProfile;
            string errMsg;
            var validationStopwatch = Stopwatch.StartNew();
            bool isValid = shouldInitializeAfterSave
                ? CheckValidation(out errMsg)
                : CheckValidationBeforeSave(out errMsg);
            validationMs = validationStopwatch.ElapsedMilliseconds;
            LogSettingsPerformance(
                "settings_validation",
                validationStopwatch,
                "valid=" + isValid.ToString().ToLowerInvariant()
                + " initial=" + shouldInitializeAfterSave.ToString().ToLowerInvariant());
            if (!isValid)
            {
                outcome = "invalid";
                totalStopwatch.Stop();
                ShowUiMessage(
                    BeMusicSeeker.Properties.Resources.Msg_invalid_setting + Environment.NewLine + Environment.NewLine + errMsg,
                    BeMusicSeeker.Properties.Resources.Error,
                    MessageBoxImage.Hand,
                    "Settings validation notification");
                return;
            }

            totalStopwatch.Stop();
            bool customFolderOutputBaseJukeboxAdoptionConfirmed = ConfirmCustomFolderOutputBaseJukeboxAdoptionBeforeSave(out _);
            totalStopwatch.Start();
            if (!customFolderOutputBaseJukeboxAdoptionConfirmed)
            {
                outcome = "custom_folder_jukebox_adoption_cancelled";
                return;
            }

            needRestart = shouldInitializeAfterSave ? RestartMode.None : IsNeedRestartForSaved();
            var saveStopwatch = Stopwatch.StartNew();
            if (shouldInitializeAfterSave)
            {
                await SaveSettingsForInitialInitialize();
                saveMs = saveStopwatch.ElapsedMilliseconds;
                if (applicationLifetime.IsFirstStartup)
                {
                    totalStopwatch.Stop();
                    ShowUiMessage(
                        BeMusicSeeker.Properties.Resources.Msg_initsetting_completed,
                        BeMusicSeeker.Properties.Resources.Information,
                        MessageBoxImage.Asterisk,
                        "Initial settings completion notification");
                    totalStopwatch.Start();
                }
                bool initializationSucceeded = await statePort.InitializeLibraryAsync();
                if (initializationSucceeded)
                {
                    SetScoreReloadPending(false);
                    SetFileDiffReloadPending(false);
                    ClosePresentation();
                    outcome = "saved_initial";
                }
                else
                {
                    outcome = "saved_initialization_failed";
                }
            }
            else
            {
                await SaveSettings();
                saveMs = saveStopwatch.ElapsedMilliseconds;
                bool initializationSucceeded = true;
                if (needRestart.HasFlag(RestartMode.All)
                    || (needRestart.HasFlag(RestartMode.ScoreOnly) && needRestart.HasFlag(RestartMode.FolderOnly)))
                {
                    initializationSucceeded = await statePort.InitializeLibraryAsync();
                }
                else if (needRestart.HasFlag(RestartMode.ScoreOnly))
                {
                    await ReloadScoresOnlyAsync();
                }
                else if (needRestart.HasFlag(RestartMode.FolderOnly))
                {
                    await ReloadFileDiffAsync();
                }
                if (initializationSucceeded)
                {
                    SetScoreReloadPending(false);
                    SetFileDiffReloadPending(false);
                    ClosePresentation();
                    outcome = "saved";
                }
                else
                {
                    outcome = "saved_initialization_failed";
                }
            }
        }
        catch (Exception ex)
        {
            outcome = "failed";
            totalStopwatch.Stop();
            reportApplyFailure(ex);
        }
        finally
        {
            IsEditCompletionInProgress = false;
            LogSettingsPerformance(
                "settings_apply",
                totalStopwatch,
                "outcome=" + outcome
                + " initial=" + shouldInitializeAfterSave.ToString().ToLowerInvariant()
                + " restartMode=" + needRestart
                + " validationMs=" + validationMs
                + " saveMs=" + saveMs);
        }
    }

    private Task ReloadScoresOnlyAsync()
    {
        return ReloadScoresOnlyCoreAsync();
    }

    internal Task ReloadFileDiffAsync()
    {
        return ReloadFileDiffCoreAsync();
    }

    internal async Task RequestLr2SongDbSyncAsync()
    {
        await lr2SongDbSyncWorkflow.RequestManualResyncAsync().ConfigureAwait(false);
    }

    private async Task ReloadFileDiffCoreAsync()
    {
        try
        {
            await statePort.ReloadFileDiffAsync();
            SetFileDiffReloadPending(false);
        }
        catch
        {
            SetFileDiffReloadPending(true);
            throw;
        }
    }

    private async Task ReloadScoresOnlyCoreAsync()
    {
        try
        {
            await statePort.ReloadScoresOnlyAsync();
            SetScoreReloadPending(false);
        }
        catch
        {
            SetScoreReloadPending(true);
            throw;
        }
    }

    private void SetScoreReloadPending(bool value)
    {
        if (scoreReloadPending == value)
        {
            return;
        }

        scoreReloadPending = value;
        RaisePropertyChanged(nameof(IsEditCancellationEnabled));
        RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
    }

    private void SetFileDiffReloadPending(bool value)
    {
        if (fileDiffReloadPending == value)
        {
            return;
        }

        fileDiffReloadPending = value;
        RaisePropertyChanged(nameof(IsEditCancellationEnabled));
        RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
    }

    private Settings ApplicationSettings => settingsEditSession.Values;

    private readonly EventHandler libraryOperationAvailabilityChangedHandler;

    private readonly Action<Lr2PlayHistorySchemaStatusSnapshot> lr2PlayHistorySchemaStatusChangedHandler;

    private readonly EventHandler<PlaylistCatalogChangedEventArgs> playlistCatalogChangedHandler;

    private long latestPlaylistCatalogVersion;

    private long publishedPlaylistCatalogVersion;

    private bool isPresentationActive;

    private readonly PropertyChangedEventListener resourceServiceEventListener;

    private bool tempOperationModeLR2DB;

    private bool scoreReloadPending;

    private bool fileDiffReloadPending;

    private string tempLR2RootPath;

    private readonly Dictionary<string, PlayerResolution> lr2bodyResolutions = new()
        {
            {
                "  320x180 (16:9)",
                new PlayerResolution(320.0, 180.0)
            },
            {
                "  480x270 (16:9)",
                new PlayerResolution(480.0, 270.0)
            },
            {
                "  640x360 (16:9)",
                new PlayerResolution(640.0, 360.0)
            },
            {
                "  960x540 (16:9)",
                new PlayerResolution(960.0, 540.0)
            },
            {
                " 1280x720 (16:9)",
                new PlayerResolution(1280.0, 720.0)
            },
            {
                "1920x1080 (16:9)",
                new PlayerResolution(1920.0, 1080.0)
            },
            {
                "  320x240 (4:3)",
                new PlayerResolution(320.0, 240.0)
            },
            {
                "  480x360 (4:3)",
                new PlayerResolution(480.0, 360.0)
            },
            {
                "  640x480 (4:3)",
                new PlayerResolution(640.0, 480.0)
            },
            {
                "  960x720 (4:3)",
                new PlayerResolution(960.0, 720.0)
            },
            {
                " 1280x960 (4:3)",
                new PlayerResolution(1280.0, 960.0)
            },
            {
                "1600x1200 (4:3)",
                new PlayerResolution(1600.0, 1200.0)
            }
        };

    private PlayerResolution tempLR2bodyResolution;

    private string tempBMSRootPath;

    private string tempLR2SongDBPath;

    private string tempLR2ConfigXmlPath;

    private Lr2PlayHistorySchemaStatusSnapshot lr2PlayHistorySchemaStatusSnapshot;

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

    private AudioNormalization tempEncoderNormalization;

    private float tempEncoderQuality;

    private float tempEncoderAmplifier;

    private string tempEncoderExeDir;

    private static readonly string defaultEncodeFileNameFormat = "[%ARTIST%] %TITLE%";

    private string tempEncodeFileNameFormat;

    private AudioOutputSelection savedAudioOutputSelection;

    private AudioOutputSelection audioOutputSelectionDraft;

    private List<AudioDeviceInfo> playerDeviceNames;

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
                if (!statePort.HasActiveLibraryProfile)
                {
                    SetOperationModeSelection(value);
                    return;
                }
                ConfirmAndRestartForOperationModeChange(value);
            }
        }
    }

    public bool CanUseLr2Features => OperationModeLR2DB;

    /// <summary>
    /// 設定画面から LR2 の派生データ再同期を要求できる状態かどうかを返します。
    /// </summary>
    public bool CanRequestLr2SongDbSyncDataResync => statePort.HasActiveLibraryProfile
        && OperationModeLR2DB
        && !scoreReloadPending
        && !fileDiffReloadPending
        && !statePort.IsLibraryOperationInProgress;

    /// <summary>
    /// 再同期要求がライブラリ処理中で拒否される状態かどうかを返します。
    /// </summary>
    public bool IsLr2SongDbSyncDataResyncBlockedByLibraryOperation =>
        !CanRequestLr2SongDbSyncDataResync
        && statePort.IsLibraryOperationInProgress;

    private void RaiseLr2SongDbSyncDataResyncAvailabilityChanged()
    {
        RaisePropertyChanged(nameof(CanRequestLr2SongDbSyncDataResync));
        RaisePropertyChanged(nameof(IsLr2SongDbSyncDataResyncBlockedByLibraryOperation));
    }

    public bool IsOperationModeChanged => tempOperationModeLR2DB != OperationModeLR2DB;

    public bool CanSaveSettings => CheckValidationForSave();

    private void RaiseValidationStateChanged()
    {
        RaisePropertyChanged(nameof(CanSaveSettings));
    }

    private void SetOperationModeSelection(bool value)
    {
        operationModeLR2DB = value;
        RaisePropertyChanged("OperationModeLR2DB");
        RaisePropertyChanged(nameof(IsOperationModeChanged));
        RaisePropertyChanged(nameof(CanUseLr2Features));
        RaisePropertyChanged(nameof(AvailableBMSDirectories));
        RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        RaisePropertyChanged(nameof(IsBmsSearchRootEditorEnabled));
        RaisePropertyChanged(nameof(BMSInstallDir));
        RaiseValidationStateChanged();
        RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
        ResetLr2PlayHistorySchemaStatus();
    }

    private void ConfirmAndRestartForOperationModeChange(bool value)
    {
        if (!ShowUiConfirmation(
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
            SetOperationModeSelection(value);
            _ = RestartForOperationModeChangeAsync();
        }
        catch (Exception ex)
        {
            HandleRestartFailure(ex);
        }
    }

    private async Task RestartForOperationModeChangeAsync()
    {
        try
        {
            await applicationLifetime.RestartApplicationAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            HandleRestartFailure(exception);
        }
    }

    private void HandleRestartFailure(Exception exception)
    {
        ShowUiMessage(
            BeMusicSeeker.Properties.Resources.Error_RestartApplicationFailed + Environment.NewLine + Environment.NewLine + exception.Message,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxImage.Hand,
            "Restart failure notification");
        applicationLifetime.RequestShutdown();
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
            return ApplicationSettings.LR2RootPath;
        }
        set
        {
            if (ApplicationSettings.LR2RootPath == value)
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
                ApplicationSettings.LR2RootPath = value;
                if (IsLR2SongDBPathValid(lR2SongDBPath))
                {
                    LR2SongDBPath = lR2SongDBPath;
                }
                LR2ConfigXmlPath = empty;
            }
            else
            {
                ApplicationSettings.LR2RootPath = null;
            }
            RaisePropertyChanged("LR2RootPath");
            RaisePropertyChanged(nameof(LR2bodyPath));
            RaiseValidationStateChanged();
            ResetLr2PlayHistorySchemaStatus();
        }
    }

    public Dictionary<string, PlayerResolution> LR2bodyResolutions => lr2bodyResolutions;

    public PlayerResolution LR2bodyResolution
    {
        get
        {
            return PlayerResolutionSettingsAdapter.FromSettings(ApplicationSettings);
        }
        set
        {
            PlayerResolutionSettingsAdapter.SaveToSettings(ApplicationSettings, value);
        }
    }

    public string BMSRootPath
    {
        get
        {
            return ApplicationSettings.BMSRootPath;
        }
        set
        {
            if (!(ApplicationSettings.BMSRootPath == value))
            {
                if (IsBMSRootPathValid(value))
                {
                    ApplicationSettings.BMSRootPath = value;
                }
                else
                {
                    ApplicationSettings.BMSRootPath = null;
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
    /// プレイリスト table source の再読み込みに追従するため、選択中プリセットから都度再構築します。
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
                RaisePropertyChanged(nameof(CanEditPlayHistoryFolderDisplayPreset));
                RaisePropertyChanged(nameof(CanRemovePlayHistoryFolderDisplayPreset));
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
                RaisePropertyChanged(nameof(SelectedStandaloneBmsRootPath));
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
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
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
                return;
            }
            if (!string.Equals(selectedLR2ConfigBmsDirectory, value, StringComparison.Ordinal))
            {
                selectedLR2ConfigBmsDirectory = value;
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
            }
        }
    }

    public bool IsBmsSearchRootEditorEnabled => !OperationModeLR2DB || lr2config != null;

    internal Lr2PlayHistorySchemaStatusSnapshot Lr2PlayHistorySchemaStatusSnapshot => lr2PlayHistorySchemaStatusSnapshot;

    public string Lr2PlayHistoryScoreDbPath => lr2PlayHistoryScoreDbPath ?? string.Empty;

    public string Lr2PlayHistorySchemaStatusText => Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaStatusSnapshot).StatusText;

    public string Lr2PlayHistorySchemaMessage => Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaStatusSnapshot).Message;

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

    public bool CanInstallLr2PlayHistorySchema => Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaStatusSnapshot).CanInstall;

    public bool CanRepairLr2PlayHistorySchema => Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaStatusSnapshot).CanRepair;

    /// <summary>
    /// LR2 play history schema に対して enable/repair のどちらかを実行できるかを返します。
    /// UI では状態別にボタンを分けず、現在の schema 状態に応じて同じ操作境界へ集約します。
    /// </summary>
    public bool CanInstallOrRepairLr2PlayHistorySchema =>
        OperationModeLR2DB
        && !string.IsNullOrWhiteSpace(Lr2PlayHistoryScoreDbPath)
        && (lr2PlayHistorySchemaStatusSnapshot == null || CanInstallLr2PlayHistorySchema || CanRepairLr2PlayHistorySchema);

    /// <summary>
    /// LR2 play history schema の enable/repair 統合ボタンに表示する文言を返します。
    /// </summary>
    public string Lr2PlayHistorySchemaInstallOrRepairButtonText
    {
        get
        {
            Lr2PlayHistorySchemaStatusPresentation presentation = Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaStatusSnapshot);
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

    internal async Task InstallOrRepairLr2PlayHistorySchemaAsync()
    {
        if (statePort.IsLibraryOperationInProgress)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema install blocked notification");
            return;
        }

        await RefreshLr2PlayHistorySchemaStatusAsync(force: true);
        if (!CanInstallOrRepairLr2PlayHistorySchema)
        {
            return;
        }

        string scoreDbPath = Lr2PlayHistoryScoreDbPath;
        bool isLr2LinkedProfile = OperationModeLR2DB;
        UiDialogResult confirmation = await schemaDialogs.ConfirmAsync(new UiConfirmationRequest(
            BeMusicSeeker.Properties.Resources.Msg_confirm_lr2_play_history_schema_install_or_repair
                + Environment.NewLine
                + Environment.NewLine
                + "score DB: "
                + scoreDbPath,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Exclamation,
            MessageBoxResult.Cancel));
        UiDialogRoute.ThrowIfNotShown(confirmation, "LR2 play history schema install confirmation");
        if (!confirmation.IsAccepted)
        {
            return;
        }

        try
        {
            Lr2PlayHistorySchemaCheckResult result = await Task.Run(() =>
                InstallOrRepairLr2PlayHistorySchemaCore(scoreDbPath, isLr2LinkedProfile));
            if (isLr2LinkedProfile != OperationModeLR2DB
                || !string.Equals(scoreDbPath, Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyLr2PlayHistorySchemaCheckResult(result);
            if (result.Status == Lr2PlayHistorySchemaStatus.Installed)
            {
                playHistoryPort.InvalidateReadCache("lr2_play_history_schema_install_or_repair");
                await ShowLr2PlayHistorySchemaMessageAsync(
                    BeMusicSeeker.Properties.Resources.Msg_success_lr2_play_history_schema_install_or_repair,
                    BeMusicSeeker.Properties.Resources.Success,
                    MessageBoxImage.Asterisk,
                    "LR2 play history schema install success notification");
                if (statePort.HasActiveLibraryProfile)
                {
                    await ReloadScoresOnlyAsync();
                }
                return;
            }

            await ShowLr2PlayHistorySchemaMessageAsync(
                result.Message,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema install result notification");
        }
        catch (Exception ex)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "LR2 play history schema install failure notification");
        }
    }

    internal Task<ApplicationDataUninstallResult> UninstallApplicationDataAsync()
    {
        return applicationDataUninstallWorkflow.RunAsync(new ApplicationDataUninstallRequest(
            workspacePort.HasPlaylistTables,
            statePort.IsLibraryOperationInProgress,
            ApplicationSettings.LR2SongDBPath));
    }

    internal async Task UninstallLr2PlayHistorySchemaAsync()
    {
        if (statePort.IsLibraryOperationInProgress)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema uninstall blocked notification");
            return;
        }

        await RefreshLr2PlayHistorySchemaStatusAsync(force: true);
        Lr2PlayHistorySchemaStatusSnapshot before = Lr2PlayHistorySchemaStatusSnapshot;
        if (before == null || !CanUninstallLr2PlayHistorySchema)
        {
            return;
        }
        if (before.Status == Lr2PlayHistorySchemaStatus.NotInstalled)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_lr2_play_history_schema_uninstall_not_installed,
                BeMusicSeeker.Properties.Resources.Information,
                MessageBoxImage.Asterisk,
                "LR2 play history schema uninstall not installed notification");
            return;
        }
        if (before.Status is Lr2PlayHistorySchemaStatus.SkippedProfile or Lr2PlayHistorySchemaStatus.Unreadable)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                before.Message,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema uninstall preflight notification");
            return;
        }

        string scoreDbPath = Lr2PlayHistoryScoreDbPath;
        bool isLr2LinkedProfile = OperationModeLR2DB;
        if (schemaWindowDialogs == null)
        {
            throw new InvalidOperationException("LR2 play history schema dialog port is not configured.");
        }

        UiInteractionResult<Lr2PlayHistorySchemaUninstallMode> dialogResult = await schemaWindowDialogs.ShowAsync(scoreDbPath);
        ThrowIfWindowDialogNotShown(dialogResult, "LR2 play history schema uninstall dialog");
        if (!dialogResult.IsAccepted)
        {
            return;
        }

        Lr2PlayHistorySchemaUninstallMode uninstallMode = dialogResult.Value;
        try
        {
            Lr2PlayHistorySchemaCheckResult result = await Task.Run(() =>
                UninstallLr2PlayHistorySchemaCore(scoreDbPath, isLr2LinkedProfile, uninstallMode));
            if (isLr2LinkedProfile != OperationModeLR2DB
                || !string.Equals(scoreDbPath, Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ApplyLr2PlayHistorySchemaCheckResult(result);
            if (IsExpectedLr2PlayHistorySchemaUninstallResult(uninstallMode, result.Status))
            {
                playHistoryPort.InvalidateReadCache("lr2_play_history_schema_uninstall");
                await ShowLr2PlayHistorySchemaMessageAsync(
                    BeMusicSeeker.Properties.Resources.Msg_success_lr2_play_history_schema_uninstall,
                    BeMusicSeeker.Properties.Resources.Success,
                    MessageBoxImage.Asterisk,
                    "LR2 play history schema uninstall success notification");
                if (statePort.HasActiveLibraryProfile)
                {
                    await ReloadScoresOnlyAsync();
                }
                return;
            }

            await ShowLr2PlayHistorySchemaMessageAsync(
                result.Message,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema uninstall result notification");
        }
        catch (Exception ex)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "LR2 play history schema uninstall failure notification");
        }
    }

    private async Task RefreshLr2PlayHistorySchemaStatusAsync(bool force)
    {
        string expectedScoreDbPath = Lr2PlayHistoryScoreDbPath;
        bool expectedOperationMode = OperationModeLR2DB;
        if (!force && HasFreshLr2PlayHistorySchemaCheckResult(expectedScoreDbPath, expectedOperationMode))
        {
            return;
        }

        Lr2PlayHistorySchemaCheckResult result = await Task.Run(() =>
            CheckLr2PlayHistorySchemaCore(expectedScoreDbPath, expectedOperationMode));
        if (expectedOperationMode == OperationModeLR2DB
            && string.Equals(expectedScoreDbPath, Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
        {
            ApplyLr2PlayHistorySchemaCheckResult(result);
        }
    }

    private async Task ShowLr2PlayHistorySchemaMessageAsync(
        string message,
        string caption,
        MessageBoxImage icon,
        string routeName)
    {
        UiDialogResult result = await schemaDialogs.ShowMessageAsync(new UiMessageRequest(
            message,
            caption,
            MessageBoxButton.OK,
            icon,
            MessageBoxResult.OK));
        UiDialogRoute.ThrowIfNotShown(result, routeName);
    }

    private void ResetLr2PlayHistorySchemaStatus()
    {
        string nextScoreDbPath = ResolveLr2PlayHistoryScoreDbPath();
        bool nextOperationMode = OperationModeLR2DB;
        if (lr2PlayHistorySchemaStatusSnapshot != null
            && lr2PlayHistorySchemaCheckOperationMode == nextOperationMode
            && string.Equals(lr2PlayHistoryScoreDbPath ?? string.Empty, nextScoreDbPath ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            RaiseLr2PlayHistorySchemaStatusChanged();
            return;
        }
        lr2PlayHistoryScoreDbPath = nextScoreDbPath;
        lr2PlayHistorySchemaStatusSnapshot = null;
        lr2PlayHistorySchemaCheckOperationMode = null;
        RaiseLr2PlayHistorySchemaStatusChanged();
    }

    internal void RefreshLr2PlayHistorySchemaStatusPresentation()
    {
        ResetLr2PlayHistorySchemaStatus();
    }

    private Lr2PlayHistorySchemaCheckResult InstallOrRepairLr2PlayHistorySchemaCore(
        string scoreDbPath,
        bool isLr2LinkedProfile)
    {
        LogLr2PlayHistorySchema("install_or_repair_start", scoreDbPath, isLr2LinkedProfile);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile);
        LogLr2PlayHistorySchema("install_or_repair_done", result, isLr2LinkedProfile);
        return result;
    }

    /// <summary>
    /// LR2 score DB に入れた play history schema を指定モードで削除し、削除後の状態を返します。
    /// </summary>
    /// <param name="uninstallMode">trigger のみ削除するか、履歴 table も削除するか。</param>
    /// <returns>削除後に再確認した schema 状態。</returns>
    private Lr2PlayHistorySchemaCheckResult UninstallLr2PlayHistorySchemaCore(
        string scoreDbPath,
        bool isLr2LinkedProfile,
        Lr2PlayHistorySchemaUninstallMode uninstallMode)
    {
        LogLr2PlayHistorySchema("uninstall_start_" + uninstallMode, scoreDbPath, isLr2LinkedProfile);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().Uninstall(scoreDbPath, isLr2LinkedProfile, uninstallMode);
        LogLr2PlayHistorySchema("uninstall_done", result, isLr2LinkedProfile);
        return result;
    }

    private void ApplyLr2PlayHistorySchemaStatusSnapshot(Lr2PlayHistorySchemaStatusSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        if (snapshot.IsReset)
        {
            lr2PlayHistoryScoreDbPath = ResolveLr2PlayHistoryScoreDbPath();
            lr2PlayHistorySchemaStatusSnapshot = null;
            lr2PlayHistorySchemaCheckOperationMode = null;
            RaiseLr2PlayHistorySchemaStatusChanged();
            return;
        }

        if (!OperationModeLR2DB
            || !string.Equals(snapshot.ScoreDbPath, Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lr2PlayHistoryScoreDbPath = snapshot.ScoreDbPath;
        lr2PlayHistorySchemaStatusSnapshot = snapshot;
        lr2PlayHistorySchemaCheckOperationMode = OperationModeLR2DB;
        RaiseLr2PlayHistorySchemaStatusChanged();
    }

    private void ApplyLr2PlayHistorySchemaCheckResult(Lr2PlayHistorySchemaCheckResult result)
    {
        Lr2PlayHistorySchemaStatusSnapshot snapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(result);
        lr2PlayHistoryScoreDbPath = snapshot.IsReset
            ? ResolveLr2PlayHistoryScoreDbPath()
            : snapshot.ScoreDbPath;
        lr2PlayHistorySchemaStatusSnapshot = snapshot.IsReset ? null : snapshot;
        lr2PlayHistorySchemaCheckOperationMode = OperationModeLR2DB;
        RaiseLr2PlayHistorySchemaStatusChanged();
    }

    private bool HasFreshLr2PlayHistorySchemaCheckResult(string scoreDbPath, bool isLr2LinkedProfile)
    {
        return lr2PlayHistorySchemaStatusSnapshot != null
            && lr2PlayHistorySchemaCheckOperationMode == isLr2LinkedProfile
            && string.Equals(lr2PlayHistoryScoreDbPath ?? string.Empty, scoreDbPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private Lr2PlayHistorySchemaCheckResult CheckLr2PlayHistorySchemaCore(string scoreDbPath, bool isLr2LinkedProfile)
    {
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().Check(scoreDbPath, isLr2LinkedProfile);
        LogLr2PlayHistorySchema("check", result, isLr2LinkedProfile);
        return result;
    }

    private static bool IsExpectedLr2PlayHistorySchemaUninstallResult(
        Lr2PlayHistorySchemaUninstallMode uninstallMode,
        Lr2PlayHistorySchemaStatus status)
    {
        return (uninstallMode == Lr2PlayHistorySchemaUninstallMode.TriggersOnly && status == Lr2PlayHistorySchemaStatus.Repairable)
            || (uninstallMode == Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers && status == Lr2PlayHistorySchemaStatus.NotInstalled);
    }

    private static void ThrowIfWindowDialogNotShown<TResult>(UiInteractionResult<TResult> result, string routeName)
    {
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " failed: no result");
        }
        if (result.Status is UiInteractionStatus.Accepted or UiInteractionStatus.CancelledByUser or UiInteractionStatus.ClosedByUser)
        {
            return;
        }

        throw new InvalidOperationException(routeName + " failed: " + result.Status, result.Error);
    }

    private string ResolveLr2PlayHistoryScoreDbPath()
    {
        if (!OperationModeLR2DB)
        {
            return null;
        }
        return Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(ApplicationSettings.LR2RootPath, () => lr2config?.GetPlayerId());
    }

    private void RaiseLr2PlayHistorySchemaStatusChanged()
    {
        RaisePropertyChanged(nameof(Lr2PlayHistorySchemaStatusSnapshot));
        RaisePropertyChanged(nameof(Lr2PlayHistoryScoreDbPath));
        RaisePropertyChanged(nameof(Lr2PlayHistorySchemaStatusText));
        RaisePropertyChanged(nameof(Lr2PlayHistorySchemaMessage));
        RaisePropertyChanged(nameof(Lr2PlayHistorySchemaDetailText));
        RaisePropertyChanged(nameof(CanInstallLr2PlayHistorySchema));
        RaisePropertyChanged(nameof(CanRepairLr2PlayHistorySchema));
        RaisePropertyChanged(nameof(CanInstallOrRepairLr2PlayHistorySchema));
        RaisePropertyChanged(nameof(Lr2PlayHistorySchemaInstallOrRepairButtonText));
        RaisePropertyChanged(nameof(CanUninstallLr2PlayHistorySchema));
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
                RaisePropertyChanged(nameof(SelectedCustomFolderAdditionalOutputBaseDir));
                RaisePropertyChanged(nameof(SelectedCustomFolderAdditionalOutputBaseName));
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
                RaisePropertyChanged(nameof(SelectedCustomFolderAdditionalOutputBaseName));
            }
        }
    }

    public string LR2SongDBPath
    {
        get
        {
            return ApplicationSettings.LR2SongDBPath;
        }
        set
        {
            if (!(ApplicationSettings.LR2SongDBPath == value))
            {
                if (IsLR2SongDBPathValid(value))
                {
                    ApplicationSettings.LR2SongDBPath = value;
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
            return ApplicationSettings.LR2ConfigXmlPath;
        }
        set
        {
            if (ApplicationSettings.LR2ConfigXmlPath == value)
            {
                return;
            }
            if (IsLR2ConfigXmlPathValid(value))
            {
                ApplicationSettings.LR2ConfigXmlPath = value;
                try
                {
                    lr2config = new LR2Config(ApplicationSettings.LR2ConfigXmlPath);
                }
                catch
                {
                    ApplicationSettings.LR2ConfigXmlPath = null;
                    lr2config = null;
                }
            }
            RaisePropertyChanged("LR2ConfigXmlPath");
            RaisePropertyChanged(nameof(LR2bodyPath));
            RaisePropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
            RaisePropertyChanged(nameof(IsBmsSearchRootEditorEnabled));
            RaisePropertyChanged(nameof(BMSInstallDir));
            RaiseValidationStateChanged();
            ResetLr2PlayHistorySchemaStatus();
        }
    }

    public bool UseBeatorajaScoreDb
    {
        get
        {
            return ApplicationSettings.UseBeatorajaScoreDb;
        }
        set
        {
            if (ApplicationSettings.UseBeatorajaScoreDb != value)
            {
                ApplicationSettings.UseBeatorajaScoreDb = value;
                RaisePropertyChanged("UseBeatorajaScoreDb");
                RaiseValidationStateChanged();
            }
        }
    }

    public string BeatorajaRootPath
    {
        get
        {
            return ApplicationSettings.BeatorajaRootPath;
        }
        set
        {
            string path = value ?? string.Empty;
            if (ApplicationSettings.BeatorajaRootPath == path)
            {
                return;
            }
            ApplicationSettings.BeatorajaRootPath = path;
            RefreshBeatorajaDerivedSettings();
            RaisePropertyChanged("BeatorajaRootPath");
            RaisePropertyChanged(nameof(AvailableBeatorajaPlayers));
            RaisePropertyChanged(nameof(BeatorajaPlayerId));
            RaisePropertyChanged(nameof(BeatorajaScoreDbPath));
            RaisePropertyChanged(nameof(BeatorajaBmtTablePath));
            RaiseValidationStateChanged();
        }
    }

    public List<string> AvailableBeatorajaPlayers
    {
        get
        {
            return BeatorajaConfigService.GetPlayerIds(ApplicationSettings.BeatorajaRootPath);
        }
    }

    public string BeatorajaPlayerId
    {
        get
        {
            return ApplicationSettings.BeatorajaPlayerId;
        }
        set
        {
            string playerId = value ?? string.Empty;
            if (ApplicationSettings.BeatorajaPlayerId == playerId)
            {
                return;
            }
            ApplicationSettings.BeatorajaPlayerId = playerId;
            RefreshBeatorajaDerivedSettings();
            RaisePropertyChanged("BeatorajaPlayerId");
            RaisePropertyChanged(nameof(BeatorajaScoreDbPath));
            RaiseValidationStateChanged();
        }
    }

    public string BeatorajaScoreDbPath
    {
        get
        {
            return ApplicationSettings.BeatorajaScoreDbPath;
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
            return ApplicationSettings.EnableBeatorajaBmtOutput;
        }
        set
        {
            if (ApplicationSettings.EnableBeatorajaBmtOutput != value)
            {
                if (value
                    && !ApplicationSettings.EnableBeatorajaBmtOutput
                    && workspacePort.HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(BeatorajaRootPath)
                    && !ShowUiConfirmation(
                        BeMusicSeeker.Properties.Resources.Confirm_enable_beatoraja_bmt_output_before_table_url_import,
                        BeMusicSeeker.Properties.Resources.Confirm,
                        MessageBoxImage.Exclamation,
                        MessageBoxButton.OKCancel,
                        "beatoraja BMT output enable before Table URL import confirmation"))
                {
                    RaisePropertyChanged("EnableBeatorajaBmtOutput");
                    return;
                }
                ApplicationSettings.EnableBeatorajaBmtOutput = value;
                RaisePropertyChanged("EnableBeatorajaBmtOutput");
                RaiseValidationStateChanged();
            }
        }
    }

    public bool KeepBeatorajaBmtFilesWhenOutputDisabled
    {
        get
        {
            return ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled;
        }
        set
        {
            if (ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled != value)
            {
                ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled = value;
                RaisePropertyChanged("KeepBeatorajaBmtFilesWhenOutputDisabled");
                RaiseValidationStateChanged();
            }
        }
    }

    public string BeatorajaBmtTablePath
    {
        get
        {
            return ApplicationSettings.BeatorajaBmtTablePath;
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
            return ApplicationSettings.RegisterBeatorajaBmtUrls;
        }
        set
        {
            if (ApplicationSettings.RegisterBeatorajaBmtUrls != value)
            {
                ApplicationSettings.RegisterBeatorajaBmtUrls = value;
                RaisePropertyChanged("RegisterBeatorajaBmtUrls");
                RaiseValidationStateChanged();
            }
        }
    }

    private LR2Config lr2ConfigValue;

    private LR2Config lr2config
    {
        get
        {
            return lr2ConfigValue;
        }
        set
        {
            if (lr2ConfigValue != value)
            {
                lr2ConfigValue = value;
                RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
                RaisePropertyChanged(nameof(AvailableBMSDirectories));
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
                RaisePropertyChanged(nameof(LR2CustomFolderOutputDir));
                RaisePropertyChanged(nameof(LR2CustomFolderAsRootOutputDir));
                RaisePropertyChanged(nameof(BMSInstallDir));
            }
        }
    }

    public string uBMplayPath
    {
        get
        {
            return ApplicationSettings.uBMplayPath;
        }
        set
        {
            if (!(ApplicationSettings.uBMplayPath == value))
            {
                if (IsuBMplayPathValid(value))
                {
                    ApplicationSettings.uBMplayPath = value;
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
            return ApplicationSettings.BMIIDXViewPath;
        }
        set
        {
            if (!(ApplicationSettings.BMIIDXViewPath == value))
            {
                if (IsBMIIDXViewPathValid(value))
                {
                    ApplicationSettings.BMIIDXViewPath = value;
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
            return ApplicationSettings.UsePlayeruBMplay;
        }
        set
        {
            if (value)
            {
                SetPlayerSelection(usePlayeruBMplay: true, usePlayerLR2body: false, usePlayerBMIIDXView: false);
            }
            else if (ApplicationSettings.UsePlayeruBMplay)
            {
                SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: ApplicationSettings.UsePlayerLR2body, usePlayerBMIIDXView: ApplicationSettings.UsePlayerBMIIDXView);
            }
        }
    }

    public bool UsePlayerLR2body
    {
        get
        {
            return ApplicationSettings.UsePlayerLR2body;
        }
        set
        {
            if (value)
            {
                SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: true, usePlayerBMIIDXView: false);
            }
            else if (ApplicationSettings.UsePlayerLR2body)
            {
                SetPlayerSelection(usePlayeruBMplay: ApplicationSettings.UsePlayeruBMplay, usePlayerLR2body: false, usePlayerBMIIDXView: ApplicationSettings.UsePlayerBMIIDXView);
            }
        }
    }

    public bool UsePlayerBMIIDXView
    {
        get
        {
            return ApplicationSettings.UsePlayerBMIIDXView;
        }
        set
        {
            if (value)
            {
                SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: false, usePlayerBMIIDXView: true);
            }
            else if (ApplicationSettings.UsePlayerBMIIDXView)
            {
                SetPlayerSelection(usePlayeruBMplay: ApplicationSettings.UsePlayeruBMplay, usePlayerLR2body: ApplicationSettings.UsePlayerLR2body, usePlayerBMIIDXView: false);
            }
        }
    }

    public bool UseInternalPlayer
    {
        get
        {
            return !ApplicationSettings.UsePlayeruBMplay
                && !ApplicationSettings.UsePlayerLR2body
                && !ApplicationSettings.UsePlayerBMIIDXView;
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
        if (ApplicationSettings.UsePlayeruBMplay == usePlayeruBMplay
            && ApplicationSettings.UsePlayerLR2body == usePlayerLR2body
            && ApplicationSettings.UsePlayerBMIIDXView == usePlayerBMIIDXView)
        {
            return;
        }
        ApplicationSettings.UsePlayeruBMplay = usePlayeruBMplay;
        ApplicationSettings.UsePlayerLR2body = usePlayerLR2body;
        ApplicationSettings.UsePlayerBMIIDXView = usePlayerBMIIDXView;
        RaisePropertyChanged(nameof(UseInternalPlayer));
        RaisePropertyChanged(nameof(UsePlayeruBMplay));
        RaisePropertyChanged(nameof(UsePlayerLR2body));
        RaisePropertyChanged(nameof(UsePlayerBMIIDXView));
        RaiseValidationStateChanged();
    }

    public bool IsSaveLR2bodyWindowPosition
    {
        get
        {
            return ApplicationSettings.IsSaveLR2bodyWindowPosition;
        }
        set
        {
            if (ApplicationSettings.IsSaveLR2bodyWindowPosition != value)
            {
                ApplicationSettings.IsSaveLR2bodyWindowPosition = value;
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
        if (string.IsNullOrWhiteSpace(rootOutputBaseDirectory) || !workspacePort.HasPlaylistTables)
        {
            return [];
        }

        return [.. workspacePort.CapturePlaylistPresentationSnapshots()
                .Where(table => table != null && table.IsRootFolder && !string.IsNullOrWhiteSpace(table.OutputDirectory))
                .Select(table => Path.Combine(rootOutputBaseDirectory, table.OutputDirectory))];
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
            return ApplicationSettings.LR2CustomFolderOutputBaseDir;
        }
        set
        {
            if (value != null && !(ApplicationSettings.LR2CustomFolderOutputBaseDir == value))
            {
                string previousPath = ApplicationSettings.LR2CustomFolderOutputBaseDir;
                if (ValidateCustomFolderOutputBaseDir(value, out string errMsg))
                {
                    ApplicationSettings.LR2CustomFolderOutputBaseDir = value;
                    NotifyNormalOutputBaseChanged(previousPath, value);
                }
                else
                {
                    ShowSettingValidationError(errMsg);
                }
                RaisePropertyChanged("LR2CustomFolderOutputDir");
                RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
                RaisePropertyChanged(nameof(AvailableBMSDirectories));
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
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

        ShowUiMessage(
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
            return ApplicationSettings.BMSInstallDir;
        }
        set
        {
            if (value != null && !(ApplicationSettings.BMSInstallDir == value))
            {
                if (IsBMSInstallDirValid(value))
                {
                    ApplicationSettings.BMSInstallDir = value;
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
            return ApplicationSettings.LR2CustomFolderOutputBaseDirRootType;
        }
        set
        {
            if (value != null && !(ApplicationSettings.LR2CustomFolderOutputBaseDirRootType == value))
            {
                if (ValidateCustomFolderAsRootOutputBaseDir(value, out string errMsg))
                {
                    ApplicationSettings.LR2CustomFolderOutputBaseDirRootType = value;
                }
                else
                {
                    ShowSettingValidationError(errMsg);
                }
                RaisePropertyChanged("LR2CustomFolderAsRootOutputDir");
                RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
                RaisePropertyChanged(nameof(AvailableBMSDirectories));
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
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
            BMSPlaylist.NormalizeNewPlaylistIgnoreFolderOutputDefault(ApplicationSettings.PlaylistDefaultIgnoreFolderOutput),
            type);
    }

    private void SetDefaultCustomFolderOutputEnabled(LR2SongDBExtended.playlist.CustomFolderType type, bool enabled, string propertyName)
    {
        LR2SongDBExtended.playlist.CustomFolderType mask =
            BMSPlaylist.NormalizeNewPlaylistIgnoreFolderOutputDefault(ApplicationSettings.PlaylistDefaultIgnoreFolderOutput);
        mask = enabled ? mask & ~type : mask | type;
        int nextValue = (int)BMSPlaylist.NormalizeNewPlaylistIgnoreFolderOutputDefault((int)mask);
        if (ApplicationSettings.PlaylistDefaultIgnoreFolderOutput != nextValue)
        {
            ApplicationSettings.PlaylistDefaultIgnoreFolderOutput = nextValue;
            RaisePropertyChanged(propertyName);
        }
    }

    private void RaiseDefaultCustomFolderOutputPropertiesChanged()
    {
        RaisePropertyChanged(nameof(DefaultOutputAllSongsFolder));
        RaisePropertyChanged(nameof(DefaultOutputUserFolder));
        RaisePropertyChanged(nameof(DefaultOutputLevelFolder));
        RaisePropertyChanged(nameof(DefaultOutputAlphabetFolder));
        RaisePropertyChanged(nameof(DefaultOutputClearFolder));
        RaisePropertyChanged(nameof(DefaultOutputDJLevelFolder));
        RaisePropertyChanged(nameof(DefaultOutputCategoryAllFolder));
        RaisePropertyChanged(nameof(DefaultOutputOtherFolder));
        RaisePropertyChanged(nameof(DefaultOutputRandomFolder));
        RaisePropertyChanged(nameof(DefaultOutputBpmSortFolder));
        RaisePropertyChanged(nameof(DefaultOutputBpSortFolder));
        RaisePropertyChanged(nameof(DefaultOutputPlayCountSortFolder));
        RaisePropertyChanged(nameof(DefaultOutputLastPlaySortFolder));
    }

    private void ShowSettingValidationError(string errMsg)
    {
        if (string.IsNullOrWhiteSpace(errMsg))
        {
            return;
        }
        ShowUiMessage(errMsg, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
    }

    internal bool ConfirmCustomFolderOutputBaseJukeboxAdoptionBeforeSave(out bool hasConflicts)
    {
        IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> conflicts = CollectCustomFolderOutputBaseJukeboxAdoptionConflicts();
        hasConflicts = conflicts.Count > 0;
        if (conflicts.Count == 0)
        {
            return true;
        }

        return ShowUiConfirmation(
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
            config = lr2config ?? new LR2Config(ApplicationSettings.LR2ConfigXmlPath);
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
            config = lr2config ?? new LR2Config(ApplicationSettings.LR2ConfigXmlPath);
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
            return ApplicationSettings.TableListURL;
        }
        set
        {
            if (!(ApplicationSettings.TableListURL == value))
            {
                if (IsTableListURLValid(value))
                {
                    ApplicationSettings.TableListURL = value;
                }
                else
                {
                    ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidTableListUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
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
            return ApplicationSettings.EnablePlaylistUrlCompletion;
        }
        set
        {
            if (ApplicationSettings.EnablePlaylistUrlCompletion != value)
            {
                ApplicationSettings.EnablePlaylistUrlCompletion = value;
                RaisePropertyChanged("EnablePlaylistUrlCompletion");
                RaiseValidationStateChanged();
            }
        }
    }

    public bool OverwritePlaylistUrlsWithCompletion
    {
        get
        {
            return ApplicationSettings.OverwritePlaylistUrlsWithCompletion;
        }
        set
        {
            if (ApplicationSettings.OverwritePlaylistUrlsWithCompletion != value)
            {
                ApplicationSettings.OverwritePlaylistUrlsWithCompletion = value;
                RaisePropertyChanged("OverwritePlaylistUrlsWithCompletion");
            }
        }
    }

    public bool EnableStellaFullPlaylistUrlCompletion
    {
        get
        {
            return ApplicationSettings.EnableStellaFullPlaylistUrlCompletion;
        }
        set
        {
            if (ApplicationSettings.EnableStellaFullPlaylistUrlCompletion != value)
            {
                ApplicationSettings.EnableStellaFullPlaylistUrlCompletion = value;
                RaisePropertyChanged("EnableStellaFullPlaylistUrlCompletion");
            }
        }
    }

    public string PlaylistMd5UrlMappingTsvUri
    {
        get
        {
            if (ApplicationSettings.PlaylistMd5UrlMappingTsvUri == null)
            {
                return PlaylistUrlCompletionSupport.DefaultMd5UrlMappingTsvUri;
            }
            return ApplicationSettings.PlaylistMd5UrlMappingTsvUri;
        }
        set
        {
            string normalizedValue = value ?? string.Empty;
            if (string.Equals(ApplicationSettings.PlaylistMd5UrlMappingTsvUri, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }
            if (IsPlaylistMd5UrlMappingTsvUriValid(normalizedValue))
            {
                ApplicationSettings.PlaylistMd5UrlMappingTsvUri = normalizedValue;
            }
            else
            {
                ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidPlaylistMd5UrlMappingTsvUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
            RaisePropertyChanged("PlaylistMd5UrlMappingTsvUri");
            RaiseValidationStateChanged();
        }
    }

    public bool IsLR2BackupEnabled
    {
        get
        {
            return ApplicationSettings.IsLR2BackupEnabled;
        }
        set
        {
            if (ApplicationSettings.IsLR2BackupEnabled != value)
            {
                ApplicationSettings.IsLR2BackupEnabled = value;
                RaisePropertyChanged("IsLR2BackupEnabled");
                RaiseValidationStateChanged();
            }
        }
    }

    public string LR2BackupPath
    {
        get
        {
            return ApplicationSettings.LR2BackupPath;
        }
        set
        {
            if (!(ApplicationSettings.LR2BackupPath == value))
            {
                if (IsLR2BackupPathValid(value))
                {
                    ApplicationSettings.LR2BackupPath = value;
                }
                else
                {
                    ApplicationSettings.LR2BackupPath = null;
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
            return ApplicationSettings.LR2BackupTarget;
        }
        set
        {
            if (ApplicationSettings.LR2BackupTarget != value)
            {
                ApplicationSettings.LR2BackupTarget = value;
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
                ApplicationSettings.LR2BackupSpan = 7;
            }
            return ApplicationSettings.LR2BackupSpan;
        }
        set
        {
            if (ApplicationSettings.LR2BackupSpan != value)
            {
                if (IsLR2BackupSpanValid(value))
                {
                    ApplicationSettings.LR2BackupSpan = value;
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
                ApplicationSettings.LR2BackupNum = 1;
            }
            return ApplicationSettings.LR2BackupNum;
        }
        set
        {
            if (ApplicationSettings.LR2BackupNum != value)
            {
                if (IsLR2BackupNumValid(value))
                {
                    ApplicationSettings.LR2BackupNum = value;
                }
                RaisePropertyChanged("LR2BackupNum");
            }
        }
    }

    public bool UseExternalWebBrowser
    {
        get
        {
            return ApplicationSettings.UseExternalWebBrowser;
        }
        set
        {
            if (ApplicationSettings.UseExternalWebBrowser != value)
            {
                ApplicationSettings.UseExternalWebBrowser = value;
                RaisePropertyChanged("UseExternalWebBrowser");
            }
        }
    }

    public bool UseExternalPanelImage
    {
        get
        {
            return ApplicationSettings.UseExternalPanelImage;
        }
        set
        {
            if (ApplicationSettings.UseExternalPanelImage != value)
            {
                ApplicationSettings.UseExternalPanelImage = value;
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
            RaisePropertyChanged(nameof(DisplayName));
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
            RaisePropertyChanged(nameof(DisplayName));
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
            return BmtTableExportService.NormalizeHashOutputMode(ApplicationSettings.BeatorajaBmtHashOutputMode).ToString();
        }
        set
        {
            string normalizedValue = BmtTableExportService.NormalizeHashOutputMode(value).ToString();
            if (ApplicationSettings.BeatorajaBmtHashOutputMode != normalizedValue)
            {
                ApplicationSettings.BeatorajaBmtHashOutputMode = normalizedValue;
                RaisePropertyChanged("BeatorajaBmtHashOutputMode");
                RaiseValidationStateChanged();
            }
        }
    }

    public string AppearanceTheme
    {
        get
        {
            return ApplicationSettings.AppearanceTheme;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                RaisePropertyChanged("AppearanceTheme");
                return;
            }
            string normalizedTheme = AppThemeService.NormalizeTheme(value);
            if (ApplicationSettings.AppearanceTheme != normalizedTheme)
            {
                ApplicationSettings.AppearanceTheme = normalizedTheme;
                AppThemeService.ApplyTheme(normalizedTheme);
                RaisePropertyChanged("AppearanceTheme");
            }
        }
    }

    public double CustomTableFontSize
    {
        get
        {
            return ApplicationSettings.CustomTableFontSize;
        }
        set
        {
            double normalizedValue = Settings.NormalizeRange(value, Settings.MinCustomTableFontSize, Settings.MaxCustomTableFontSize, Settings.DefaultCustomTableFontSize);
            if (!ApplicationSettings.CustomTableFontSize.Equals(normalizedValue))
            {
                ApplicationSettings.CustomTableFontSize = normalizedValue;
                RaisePropertyChanged("CustomTableFontSize");
            }
        }
    }

    public double CustomTableRowHeight
    {
        get
        {
            return ApplicationSettings.CustomTableRowHeight;
        }
        set
        {
            double normalizedValue = Settings.NormalizeRange(value, Settings.MinCustomTableRowHeight, Settings.MaxCustomTableRowHeight, Settings.DefaultCustomTableRowHeight);
            if (!ApplicationSettings.CustomTableRowHeight.Equals(normalizedValue))
            {
                ApplicationSettings.CustomTableRowHeight = normalizedValue;
                RaisePropertyChanged("CustomTableRowHeight");
            }
        }
    }

    public double CustomTableHeaderHeight
    {
        get
        {
            return ApplicationSettings.CustomTableHeaderHeight;
        }
        set
        {
            double normalizedValue = Settings.NormalizeRange(value, Settings.MinCustomTableHeaderHeight, Settings.MaxCustomTableHeaderHeight, Settings.DefaultCustomTableHeaderHeight);
            if (!ApplicationSettings.CustomTableHeaderHeight.Equals(normalizedValue))
            {
                ApplicationSettings.CustomTableHeaderHeight = normalizedValue;
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
            return ApplicationSettings.ShowScoreViewerRegisterConfirmMsg;
        }
        set
        {
            if (ApplicationSettings.ShowScoreViewerRegisterConfirmMsg != value)
            {
                ApplicationSettings.ShowScoreViewerRegisterConfirmMsg = value;
                RaisePropertyChanged("ShowScoreViewerRegisterConfirmMsg");
            }
        }
    }

    public bool ShowDiffBMSInstallConfirmMsg
    {
        get
        {
            return ApplicationSettings.ShowDiffBMSInstallConfirmMsg;
        }
        set
        {
            if (ApplicationSettings.ShowDiffBMSInstallConfirmMsg != value)
            {
                ApplicationSettings.ShowDiffBMSInstallConfirmMsg = value;
                RaisePropertyChanged("ShowDiffBMSInstallConfirmMsg");
            }
        }
    }

    public bool ShowDuplicateFileCheckConfirmMsg
    {
        get
        {
            return ApplicationSettings.ShowDuplicateFileCheckConfirmMsg;
        }
        set
        {
            if (ApplicationSettings.ShowDuplicateFileCheckConfirmMsg != value)
            {
                ApplicationSettings.ShowDuplicateFileCheckConfirmMsg = value;
                RaisePropertyChanged("ShowDuplicateFileCheckConfirmMsg");
            }
        }
    }

    public bool ShowRecommUpdatedMsg
    {
        get
        {
            return ApplicationSettings.ShowRecommUpdatedMsg;
        }
        set
        {
            if (ApplicationSettings.ShowRecommUpdatedMsg != value)
            {
                ApplicationSettings.ShowRecommUpdatedMsg = value;
                RaisePropertyChanged("ShowRecommUpdatedMsg");
            }
        }
    }

    public bool ScanBmsFilesOnStartup
    {
        get
        {
            return ApplicationSettings.ScanBmsFilesOnStartup;
        }
        set
        {
            if (ApplicationSettings.ScanBmsFilesOnStartup == value)
            {
                return;
            }
            if (!value)
            {
                if (!ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_disable_startup_file_scan, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "Startup file scan disable confirmation"))
                {
                    RaisePropertyChanged("ScanBmsFilesOnStartup");
                    return;
                }
            }
            ApplicationSettings.ScanBmsFilesOnStartup = value;
            RaisePropertyChanged("ScanBmsFilesOnStartup");
        }
    }

    public bool SkipInitPlaylistLoad
    {
        get
        {
            return ApplicationSettings.SkipInitPlaylistLoad;
        }
        set
        {
            if (ApplicationSettings.SkipInitPlaylistLoad == value)
            {
                return;
            }
            if (value)
            {
                if (!ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_skip_init_playlist_load, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "Initial playlist load skip confirmation"))
                {
                    RaisePropertyChanged("UpdateLr2IrRankingCacheOnStartup");
                    return;
                }
            }
            ApplicationSettings.SkipInitPlaylistLoad = value;
            RaisePropertyChanged("SkipInitPlaylistLoad");
        }
    }

    public bool StartupSelectInstallPending
    {
        get
        {
            return ApplicationSettings.StartupSelectInstallPending;
        }
        set
        {
            if (ApplicationSettings.StartupSelectInstallPending != value)
            {
                ApplicationSettings.StartupSelectInstallPending = value;
                RaisePropertyChanged("StartupSelectInstallPending");
            }
        }
    }

    public bool EnableReadOptimizedPragmas
    {
        get
        {
            return ApplicationSettings.EnableReadOptimizedPragmas;
        }
        set
        {
            if (ApplicationSettings.EnableReadOptimizedPragmas != value)
            {
                ApplicationSettings.EnableReadOptimizedPragmas = value;
                RaisePropertyChanged("EnableReadOptimizedPragmas");
            }
        }
    }

    public bool EstimateOfflineScoreRanking
    {
        get
        {
            return ApplicationSettings.EstimateOfflineScoreRanking;
        }
        set
        {
            if (ApplicationSettings.EstimateOfflineScoreRanking == value)
            {
                return;
            }
            if (value)
            {
                if (!ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_enable_offline_score_ranking_estimation, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "Offline score ranking estimation confirmation"))
                {
                    return;
                }
            }
            ApplicationSettings.EstimateOfflineScoreRanking = value;
            RaisePropertyChanged("EstimateOfflineScoreRanking");
        }
    }

    public bool EnableDownloadLr2IrScoreAndDetectUnsent
    {
        get
        {
            return ApplicationSettings.EnableDownloadLr2IrScoreAndDetectUnsent;
        }
        set
        {
            if (ApplicationSettings.EnableDownloadLr2IrScoreAndDetectUnsent != value)
            {
                ApplicationSettings.EnableDownloadLr2IrScoreAndDetectUnsent = value;
                RaisePropertyChanged("EnableDownloadLr2IrScoreAndDetectUnsent");
            }
        }
    }

    public bool UpdateLr2IrRankingCacheOnStartup
    {
        get
        {
            return ApplicationSettings.UpdateLr2IrRankingCacheOnStartup;
        }
        set
        {
            if (ApplicationSettings.UpdateLr2IrRankingCacheOnStartup == value)
            {
                return;
            }
            if (value)
            {
                if (!ShowUiConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_enable_lr2ir_ranking_cache_startup_update, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, "LR2IR ranking cache startup update confirmation"))
                {
                    return;
                }
            }
            ApplicationSettings.UpdateLr2IrRankingCacheOnStartup = value;
            RaisePropertyChanged("UpdateLr2IrRankingCacheOnStartup");
        }
    }

    public bool EnableAutoInstall
    {
        get
        {
            return ApplicationSettings.AutoInstall;
        }
        set
        {
            if (ApplicationSettings.AutoInstall != value)
            {
                ApplicationSettings.AutoInstall = value;
                RaisePropertyChanged("EnableAutoInstall");
            }
        }
    }

    public bool KeepInstallablePackagesPending
    {
        get
        {
            return ApplicationSettings.KeepInstallablePackagesPending;
        }
        set
        {
            if (ApplicationSettings.KeepInstallablePackagesPending != value)
            {
                ApplicationSettings.KeepInstallablePackagesPending = value;
                RaisePropertyChanged("KeepInstallablePackagesPending");
            }
        }
    }

    public bool AutoApplyAmbiguousInstallDestination
    {
        get
        {
            return ApplicationSettings.AutoApplyAmbiguousInstallDestination;
        }
        set
        {
            if (ApplicationSettings.AutoApplyAmbiguousInstallDestination != value)
            {
                ApplicationSettings.AutoApplyAmbiguousInstallDestination = value;
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
            return ApplicationSettings.DeletePendingPackageSourceAfterInstall;
        }
        set
        {
            if (ApplicationSettings.DeletePendingPackageSourceAfterInstall != value)
            {
                ApplicationSettings.DeletePendingPackageSourceAfterInstall = value;
                RaisePropertyChanged("DeletePendingPackageSourceAfterInstall");
            }
        }
    }

    public bool EnableSmartComponentOverwrite
    {
        get
        {
            return ApplicationSettings.EnableSmartComponentOverwrite;
        }
        set
        {
            if (ApplicationSettings.EnableSmartComponentOverwrite != value)
            {
                ApplicationSettings.EnableSmartComponentOverwrite = value;
                RaisePropertyChanged("EnableSmartComponentOverwrite");
            }
        }
    }

    public bool KeepSmartOverwriteProtectedFilesByRenaming
    {
        get
        {
            return ApplicationSettings.KeepSmartOverwriteProtectedFilesByRenaming;
        }
        set
        {
            if (ApplicationSettings.KeepSmartOverwriteProtectedFilesByRenaming != value)
            {
                ApplicationSettings.KeepSmartOverwriteProtectedFilesByRenaming = value;
                RaisePropertyChanged("KeepSmartOverwriteProtectedFilesByRenaming");
            }
        }
    }

    public string StagefilePath
    {
        get
        {
            return ApplicationSettings.StagefilePath;
        }
        set
        {
            if (!(ApplicationSettings.StagefilePath == value))
            {
                if (IsStagefilePathValid(value))
                {
                    ApplicationSettings.StagefilePath = value;
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
            return ApplicationSettings.FolderNameFormat;
        }
        set
        {
            if (!(ApplicationSettings.FolderNameFormat == value))
            {
                value = value.RemoveInvalidFileNameChars();
                if (IsFolderNameFormatValid(value))
                {
                    ApplicationSettings.FolderNameFormat = value;
                }
                else
                {
                    ApplicationSettings.FolderNameFormat = defaultFolderNameFormat;
                    ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidFolderNameFormat, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation);
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
            return ApplicationSettings.UseOnlyShiftJISChars;
        }
        set
        {
            if (ApplicationSettings.UseOnlyShiftJISChars != value)
            {
                if (!value)
                {
                    ShowUiMessage(BeMusicSeeker.Properties.Resources.Warn_DisableShiftJisFolderNames, BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation);
                }
                ApplicationSettings.UseOnlyShiftJISChars = value;
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
            return ApplicationSettings.EncoderSampleRate;
        }
        set
        {
            if (ApplicationSettings.EncoderSampleRate != value)
            {
                ApplicationSettings.EncoderSampleRate = value;
                RaisePropertyChanged("EncoderSampleRate");
            }
        }
    }

    public ReadOnlyObservableCollection<string> EncoderNames => new(["WAVE", "MP3 LAME", "AAC Nero", "Opus", "FLAC", "Ogg Vorbis"]);

    public int EncoderIndex
    {
        get
        {
            if (!EncoderChecker(ApplicationSettings.Encoder))
            {
                ApplicationSettings.Encoder = EncoderType.WAVE;
            }
            return (int)ApplicationSettings.Encoder;
        }
        set
        {
            if (ApplicationSettings.Encoder != (EncoderType)value)
            {
                if (EncoderChecker((EncoderType)value))
                {
                    ApplicationSettings.Encoder = (EncoderType)value;
                }
                else
                {
                    ShowUiMessage(
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
            return ApplicationSettings.EncoderFormat;
        }
        set
        {
            if (ApplicationSettings.EncoderFormat != value)
            {
                ApplicationSettings.EncoderFormat = value;
                RaisePropertyChanged("EncoderFormat");
            }
        }
    }

    public ReadOnlyDictionary<AudioNormalization, string> EncoderNormalizationNames => new(new Dictionary<AudioNormalization, string>
        {
            {
                AudioNormalization.None,
                BeMusicSeeker.Properties.Resources.Record_setting_normalize_none
            },
            {
                AudioNormalization.PeakLevel,
                BeMusicSeeker.Properties.Resources.Record_setting_normalize_peak
            },
            {
                AudioNormalization.RmsValue,
                BeMusicSeeker.Properties.Resources.Record_setting_normalize_average
            }
        });

    public AudioNormalization EncoderNormalization
    {
        get
        {
            return audioSettingsGateway.EncoderNormalization;
        }
        set
        {
            if (audioSettingsGateway.EncoderNormalization != value)
            {
                audioSettingsGateway.EncoderNormalization = value;
                RaisePropertyChanged("EncoderNormalization");
            }
        }
    }

    public float EncoderQuality
    {
        get
        {
            return ApplicationSettings.EncoderQuality;
        }
        set
        {
            if (ApplicationSettings.EncoderQuality != value)
            {
                ApplicationSettings.EncoderQuality = value;
                RaisePropertyChanged("EncoderQuality");
            }
        }
    }

    public float EncoderAmplifier
    {
        get
        {
            return ApplicationSettings.EncoderAmplifier;
        }
        set
        {
            if (ApplicationSettings.EncoderAmplifier != value)
            {
                ApplicationSettings.EncoderAmplifier = value;
                RaisePropertyChanged("EncoderAmplifier");
            }
        }
    }

    public string EncoderExeDir
    {
        get
        {
            return ApplicationSettings.EncoderExeDir;
        }
        set
        {
            if (!(ApplicationSettings.EncoderExeDir == value))
            {
                ApplicationSettings.EncoderExeDir = value;
                RaisePropertyChanged("EncoderExeDir");
                RaisePropertyChanged(nameof(EncoderIndex));
            }
        }
    }

    public string EncodeFileNameFormat
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ApplicationSettings.EncodeFileNameFormat))
            {
                return defaultEncodeFileNameFormat;
            }
            return ApplicationSettings.EncodeFileNameFormat;
        }
        set
        {
            if (!(ApplicationSettings.EncodeFileNameFormat == value))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    value = defaultEncodeFileNameFormat;
                }
                value = value.RemoveInvalidFileNameChars();
                ApplicationSettings.EncodeFileNameFormat = value;
                RaisePropertyChanged("EncodeFileNameFormat");
            }
        }
    }

    public ReadOnlyObservableCollection<string> PlayerDriverNames
        => new(
        [
            "DirectSound",
            "WASAPI (" + BeMusicSeeker.Properties.Resources.Shared + ")",
            "WASAPI (" + BeMusicSeeker.Properties.Resources.Exclusive + ")",
            "ASIO"
        ]);

    /// <summary>Gets a localized description for a saved backend that is not audible.</summary>
    public string UnavailablePlayerDriverDescription => IsAudiblePlayerDriver(audioOutputSelectionDraft.Backend)
        ? null
        : string.Format(
            BeMusicSeeker.Properties.Resources.AudioDeviceUnavailableFormat,
            audioOutputSelectionDraft.Backend);

    public int PlayerDriverIndex
    {
        get
        {
            AudioDriver draftDriver = audioOutputSelectionDraft.Backend;
            return IsAudiblePlayerDriver(draftDriver) ? (int)draftDriver : -1;
        }
        set
        {
            AudioDriver driver = (AudioDriver)value;
            if (!IsAudiblePlayerDriver(driver))
            {
                return;
            }
            if (audioOutputSelectionDraft.Backend != driver)
            {
                audioOutputSelectionDraft = new AudioOutputSelection(driver, null, null);
                playerDeviceNames = BuildPlayerDeviceNames(driver);
                RaisePropertyChanged(nameof(PlayerDriverNames));
                RaisePropertyChanged(nameof(UnavailablePlayerDriverDescription));
                RaisePropertyChanged(nameof(PlayerDriverIndex));
                RaisePropertyChanged(nameof(PlayerDeviceNames));
                RaisePropertyChanged(nameof(PlayerDevice));
                RaisePropertyChanged(nameof(SelectedPlayerDevice));
            }
        }
    }

    private static bool IsAudiblePlayerDriver(AudioDriver driver)
        => driver >= AudioDriver.DirectSound && driver <= AudioDriver.Asio;

    public List<AudioDeviceInfo> PlayerDeviceNames
    {
        get
        {
            return playerDeviceNames ??= BuildPlayerDeviceNames(audioOutputSelectionDraft.Backend);
        }
        private set
        {
            playerDeviceNames = value;
        }
    }

    /// <summary>
    /// Gets or sets the persisted device identity represented by the selected device option.
    /// The Default option is represented by a null identity.
    /// </summary>
    public string PlayerDevice
    {
        get
        {
            return ResolvePlayerDeviceDescriptor().Driver ?? audioOutputSelectionDraft.DeviceIdentity;
        }
        set
        {
            int selectedIndex = FindPlayerDeviceIndex(value);
            if (selectedIndex < 0)
            {
                return;
            }

            SelectedPlayerDevice = PlayerDeviceNames[selectedIndex];
        }
    }

    /// <summary>
    /// Gets or sets the device option selected in the settings dialog.
    /// A transient null published while WPF replaces the catalog is ignored.
    /// </summary>
    public AudioDeviceInfo? SelectedPlayerDevice
    {
        get
        {
            int selectedIndex = FindPlayerDeviceIndex(audioOutputSelectionDraft.DeviceIdentity);
            return selectedIndex < 0 ? null : PlayerDeviceNames[selectedIndex];
        }
        set
        {
            if (!value.HasValue)
            {
                return;
            }

            AudioDeviceInfo deviceDescriptor = value.Value;
            if (!deviceDescriptor.IsDefaultPlaceholder
                && string.IsNullOrWhiteSpace(deviceDescriptor.Driver))
            {
                return;
            }

            string nextDevice = deviceDescriptor.IsDefaultPlaceholder
                ? null
                : deviceDescriptor.Driver;
            string nextDeviceName = deviceDescriptor.IsDefaultPlaceholder
                ? null
                : deviceDescriptor.Name;
            var nextSelection = new AudioOutputSelection(
                audioOutputSelectionDraft.Backend,
                nextDevice,
                nextDeviceName);
            if (audioOutputSelectionDraft == nextSelection)
            {
                return;
            }

            audioOutputSelectionDraft = nextSelection;
            RaisePropertyChanged(nameof(PlayerDevice));
            RaisePropertyChanged(nameof(SelectedPlayerDevice));
        }
    }

    private int FindPlayerDeviceIndex(string deviceIdentity)
    {
        if (string.IsNullOrWhiteSpace(deviceIdentity))
        {
            return PlayerDeviceNames.FindIndex(device => device.IsDefaultPlaceholder);
        }

        return PlayerDeviceNames.FindIndex(device =>
            !device.IsDefaultPlaceholder
            && string.Equals(device.Driver, deviceIdentity, StringComparison.Ordinal));
    }

    private AudioDeviceInfo ResolvePlayerDeviceDescriptor()
    {
        return PlayerDeviceNames.FirstOrDefault(d =>
            string.Equals(d.Driver, audioOutputSelectionDraft.DeviceIdentity, StringComparison.Ordinal));
    }

    private List<AudioDeviceInfo> BuildPlayerDeviceNames(AudioDriver driver)
    {
        var devices = IsAudiblePlayerDriver(driver)
            ? new List<AudioDeviceInfo>(audioDeviceCatalog.GetDevices(driver))
            : [];
        string savedIdentity = audioOutputSelectionDraft.DeviceIdentity;
        if (!string.IsNullOrWhiteSpace(savedIdentity)
            && !devices.Any(device => string.Equals(device.Driver, savedIdentity, StringComparison.Ordinal)))
        {
            devices.Add(new AudioDeviceInfo(
                string.IsNullOrWhiteSpace(audioOutputSelectionDraft.DeviceName)
                    ? savedIdentity
                    : audioOutputSelectionDraft.DeviceName,
                savedIdentity,
                -1,
                isDefaultPlaceholder: false,
                isNativeDefault: false,
                isAvailable: false));
        }
        return devices;
    }

    public SampleRate PlayerSampleRate
    {
        get
        {
            return ApplicationSettings.PlayerSampleRate;
        }
        set
        {
            if (ApplicationSettings.PlayerSampleRate != value)
            {
                ApplicationSettings.PlayerSampleRate = value;
                RaisePropertyChanged("PlayerSampleRate");
            }
        }
    }

    public SampleFormat PlayerFormat
    {
        get
        {
            return ApplicationSettings.PlayerFormat;
        }
        set
        {
            if (ApplicationSettings.PlayerFormat != value)
            {
                ApplicationSettings.PlayerFormat = value;
                RaisePropertyChanged("PlayerFormat");
            }
        }
    }

    public float PlayerBufferSize
    {
        get
        {
            return ApplicationSettings.PlayerBufferSize;
        }
        set
        {
            if (ApplicationSettings.PlayerBufferSize != value)
            {
                ApplicationSettings.PlayerBufferSize = value;
                RaisePropertyChanged("PlayerBufferSize");
            }
        }
    }

    public double PlayerLatency { get; private set; }

    public bool PlayerWASAPIParam
    {
        get
        {
            return ApplicationSettings.PlayerWASAPIParam;
        }
        set
        {
            if (ApplicationSettings.PlayerWASAPIParam != value)
            {
                ApplicationSettings.PlayerWASAPIParam = value;
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

    public List<string> Languages => [.. cultureCatalog.Cultures.Keys];

    public string Language
    {
        get
        {
            // 表示名が保存されている場合はそれを優先（同一カルチャ名の重複対策）
            string savedDisplayName = ApplicationSettings.LangDisplayName;
            if (!string.IsNullOrEmpty(savedDisplayName) && cultureCatalog.Cultures.ContainsKey(savedDisplayName))
                return savedDisplayName;
            return cultureCatalog.Cultures.FirstOrDefault(kv => kv.Value == ApplicationSettings.Lang).Key;
        }
        set
        {
            if (!cultureCatalog.Cultures.TryGetValue(value ?? string.Empty, out string text))
            {
                return;
            }
            if (string.Equals(ApplicationSettings.Lang, text, StringComparison.Ordinal)
                && string.Equals(ApplicationSettings.LangDisplayName, value, StringComparison.Ordinal))
            {
                return;
            }
            ApplicationSettings.Lang = text;
            ApplicationSettings.LangDisplayName = value;
            ResourceService.Current.ChangeCulture(text);
            RaisePropertyChanged("Language");
        }
    }

    internal SettingsDialogViewModel(
        ISettingsDialogStatePort statePort,
        ISettingsDialogWorkspacePort workspacePort,
        ISettingsDialogCustomFolderOutputPort customFolderOutputPort,
        ISettingsDialogPlayHistoryPort playHistoryPort,
        ISettingsDialogSearchRootRuntimePort searchRootRuntimePort,
        ISettingsDialogPlayerFactoryPort playerFactoryPort,
        ISettingsDialogPlaybackRuntimePort playbackRuntimePort,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow,
        ISettingsEditSession settingsEditSession,
        IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore = null,
        Action<Exception> reportApplyFailure = null,
        IUiDialogService schemaDialogs = null,
        ILr2PlayHistorySchemaUninstallDialogPort schemaWindowDialogs = null,
        ApplicationDataUninstallWorkflowOwner applicationDataUninstallWorkflow = null,
        AudioDeviceTestWorkflowOwner audioDeviceTestWorkflow = null,
        IApplicationLifetimePort applicationLifetime = null,
        ICultureCatalog cultureCatalog = null,
        IExternalShellGateway externalShellGateway = null,
        ApplicationPathSnapshot applicationPathSnapshot = null,
        IAudioDeviceCatalog audioDeviceCatalog = null,
        IAudioSettingsGateway audioSettingsGateway = null)
    {
        SettingsDialogViewModel settingDialogViewModel = this;
        this.statePort = statePort ?? throw new ArgumentNullException(nameof(statePort));
        this.workspacePort = workspacePort ?? throw new ArgumentNullException(nameof(workspacePort));
        this.customFolderOutputPort = customFolderOutputPort
            ?? throw new ArgumentNullException(nameof(customFolderOutputPort));
        this.playHistoryPort = playHistoryPort ?? throw new ArgumentNullException(nameof(playHistoryPort));
        this.searchRootRuntimePort = searchRootRuntimePort ?? throw new ArgumentNullException(nameof(searchRootRuntimePort));
        this.playerFactoryPort = playerFactoryPort ?? throw new ArgumentNullException(nameof(playerFactoryPort));
        this.playbackRuntimePort = playbackRuntimePort ?? throw new ArgumentNullException(nameof(playbackRuntimePort));
        this.lr2SongDbSyncWorkflow = lr2SongDbSyncWorkflow ?? throw new ArgumentNullException(nameof(lr2SongDbSyncWorkflow));
        this.settingsEditSession = settingsEditSession ?? throw new ArgumentNullException(nameof(settingsEditSession));
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        this.cultureCatalog = cultureCatalog
            ?? throw new ArgumentNullException(nameof(cultureCatalog));
        this.externalShellGateway = externalShellGateway
            ?? throw new ArgumentNullException(nameof(externalShellGateway));
        this.applicationPathSnapshot = applicationPathSnapshot
            ?? throw new ArgumentNullException(nameof(applicationPathSnapshot));
        this.audioDeviceCatalog = audioDeviceCatalog
            ?? throw new ArgumentNullException(nameof(audioDeviceCatalog));
        this.audioSettingsGateway = audioSettingsGateway
            ?? throw new ArgumentNullException(nameof(audioSettingsGateway));
        this.playHistoryDisplaySettingsStore = playHistoryDisplaySettingsStore
            ?? new SettingsPlayHistoryDisplaySettingsStore(() => this.settingsEditSession.Values);
        this.reportApplyFailure = reportApplyFailure
            ?? (ex => ShowUiMessage(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "Settings apply failure notification"));
        this.schemaDialogs = schemaDialogs ?? new UiDialogCoordinator();
        this.schemaWindowDialogs = schemaWindowDialogs;
        this.applicationDataUninstallWorkflow = applicationDataUninstallWorkflow
            ?? new ApplicationDataUninstallWorkflowOwner(this.schemaDialogs, new Lr2ApplicationDataUninstallStore());
        this.audioDeviceTestWorkflow = audioDeviceTestWorkflow
            ?? throw new ArgumentNullException(nameof(audioDeviceTestWorkflow));
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
        playlistCatalogChangedHandler = (_, eventArgs) =>
        {
            long version = eventArgs?.Version ?? workspacePort.PlaylistCatalogVersion;
            settingDialogViewModel.ObservePlaylistCatalogVersion(version);
            settingDialogViewModel.PublishPlaylistCatalogPresentationIfNeeded();
        };
        workspacePort.PlaylistCatalogChanged += playlistCatalogChangedHandler;
        ObservePlaylistCatalogVersion(workspacePort.PlaylistCatalogVersion);
        Interlocked.Exchange(
            ref publishedPlaylistCatalogVersion,
            Interlocked.Read(ref latestPlaylistCatalogVersion));
        libraryOperationAvailabilityChangedHandler = (_, _) =>
            settingDialogViewModel.RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
        statePort.LibraryOperationAvailabilityChanged += libraryOperationAvailabilityChangedHandler;
        lr2PlayHistorySchemaStatusChangedHandler = snapshot =>
            settingDialogViewModel.ApplyLr2PlayHistorySchemaStatusSnapshot(snapshot);
        statePort.Lr2PlayHistorySchemaStatusChanged += lr2PlayHistorySchemaStatusChangedHandler;
        resourceServiceEventListener = new PropertyChangedEventListener(ResourceService.Current);
        resourceServiceEventListener.RegisterHandler(() => ResourceService.Current.Resources, delegate
        {
            settingDialogViewModel.RaisePropertyChanged(nameof(settingDialogViewModel.LR2ConfigBMSDirectories));
            settingDialogViewModel.RaisePropertyChanged(nameof(settingDialogViewModel.AvailableBMSDirectories));
            settingDialogViewModel.MarkPlayHistoryFolderDisplayPresetPlaylistOptionsDirty();
        });
        resourceServiceEventListener.RegisterHandler(() => ResourceService.Current.Resources, delegate
        {
            settingDialogViewModel.RaisePropertyChanged(nameof(settingDialogViewModel.EncoderNormalizationNames));
        });
        resourceServiceEventListener.RegisterHandler(() => ResourceService.Current.Resources, delegate
        {
            settingDialogViewModel.RaisePropertyChanged(nameof(settingDialogViewModel.EncoderNormalization));
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
            playHistoryPort.RefreshDisplayTargetCatalog(queueRefreshWhenSelectionChanges: false);
        });
        this.settingsEditSession.Reload();
        if (ApplicationSettings.OperationModeLR2DB)
        {
            try
            {
                lr2config = new LR2Config(ApplicationSettings.LR2ConfigXmlPath);
            }
            catch
            {
                ApplicationSettings.LR2ConfigXmlPath = null;
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
        return IsLR2RootPathValid(ApplicationSettings.LR2RootPath);
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
        return IsLR2PlayerRootPathValid(ApplicationSettings.LR2RootPath);
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
        return IsBMSRootPathValid(ApplicationSettings.BMSRootPath);
    }

    private bool IsBMSRootPathValid(string value)
    {
        return LongPathFileSystem.DirectoryExists(value)
            && Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(value);
    }

    private IReadOnlyList<string> GetStandaloneBmsRootPathsForCurrentSession()
    {
        return StandaloneBmsRootPathSettings.Deserialize(
            ApplicationSettings.StandaloneBmsRootPaths,
            ApplicationSettings.BMSRootPath);
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
        IReadOnlyList<string> paths = GetStandaloneBmsRootPathsForCurrentSession();
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
            RaisePropertyChanged(nameof(StandaloneBmsRootPathList));
            RaisePropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        }
        if (selectedChanged)
        {
            SelectedStandaloneBmsRootPath = selectedPath;
        }
        RaiseValidationStateChanged();
    }

    private void PersistStandaloneBmsRootPathsToSettings()
    {
        ApplicationSettings.StandaloneBmsRootPaths = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);
        string firstRoot = DeserializeStandaloneBmsRootPaths(ApplicationSettings.StandaloneBmsRootPaths).FirstOrDefault();
        ApplicationSettings.BMSRootPath = string.IsNullOrWhiteSpace(firstRoot) ? null : firstRoot;
    }

    private void RefreshCustomFolderAdditionalOutputBaseDirsFromSettings()
    {
        IReadOnlyList<string> paths = CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(
            ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs);
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
    /// playlist tree の再読み込み後でも draft の選択状態を維持するため、保存済み参照と現在の playlist snapshot を照合します。
    /// </summary>
    internal void RefreshPlayHistoryFolderDisplayPresetPlaylistOptions()
    {
        isPlayHistoryFolderDisplayPresetPlaylistOptionsDirty = false;
        PlayHistoryFolderDisplayPresetPlaylistOptions.Clear();
        PlayHistoryFolderDisplayPresetEditor preset = SelectedPlayHistoryFolderDisplayPreset;
        if (preset != null)
        {
            PlayHistoryFolderDisplayPresetSelectionIndex selectionIndex = PlayHistoryFolderDisplayPresetSelectionIndex.Create(preset.Targets);
            foreach (PlaylistTablePresentationSnapshot table in GetPlayHistoryFolderDisplayPresetTables())
            {
                PlayHistoryFolderDisplayPresetPlaylistOptions.Add(new PlayHistoryFolderPresetPlaylistOption(
                    table,
                    selectionIndex.Matches(table),
                    ApplyPlayHistoryFolderDisplayPresetPlaylistSelection));
            }
        }
        RaisePropertyChanged(nameof(PlayHistoryFolderDisplayPresetPlaylistOptions));
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

    internal void SetPresentationActive(bool active)
    {
        isPresentationActive = active;
        if (active)
        {
            ObservePlaylistCatalogVersion(workspacePort.PlaylistCatalogVersion);
            PublishPlaylistCatalogPresentationIfNeeded();
            RefreshPlayHistoryFolderDisplayPresetPlaylistOptionsIfDirty();
        }
    }

    private void ObservePlaylistCatalogVersion(long version)
    {
        long observedVersion = Interlocked.Read(ref latestPlaylistCatalogVersion);
        while (version > observedVersion)
        {
            long previousVersion = Interlocked.CompareExchange(
                ref latestPlaylistCatalogVersion,
                version,
                observedVersion);
            if (previousVersion == observedVersion)
            {
                return;
            }

            observedVersion = previousVersion;
        }
    }

    private void PublishPlaylistCatalogPresentationIfNeeded()
    {
        long latestVersion = Interlocked.Read(ref latestPlaylistCatalogVersion);
        if (!isPresentationActive
            || latestVersion <= Interlocked.Read(ref publishedPlaylistCatalogVersion))
        {
            return;
        }

        RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
        RaisePropertyChanged(nameof(AvailableBMSDirectories));
        MarkPlayHistoryFolderDisplayPresetPlaylistOptionsDirty();
        RefreshPlayHistoryFolderDisplayPresetPlaylistOptionsIfDirty();
        Interlocked.Exchange(ref publishedPlaylistCatalogVersion, latestVersion);
    }

    private IReadOnlyList<PlaylistTablePresentationSnapshot> GetPlayHistoryFolderDisplayPresetTables()
    {
        return [.. workspacePort.CapturePlaylistPresentationSnapshots()
                .Where(table => table?.PlaylistId != null)
                .OrderBy(table => table.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(table => table.Symbol ?? string.Empty, StringComparer.OrdinalIgnoreCase)];
    }

    private void RefreshPlayHistoryFolderDisplayPresetsFromSettings()
    {
        PlayHistoryFolderDisplayPresets.Clear();
        foreach (PlayHistoryDisplayTargetSet targetSet in PlayHistoryDisplayTargetSetStore.Deserialize(playHistoryDisplaySettingsStore.DisplayTargetSetsJson))
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
                .Select(table => table.PlaylistId.Value)];
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
        playHistoryDisplaySettingsStore.DisplayTargetSetsJson = serializedDisplayTargetSets;
        if (playHistoryDisplayTargetSetsChanged)
        {
            playHistoryPort.RefreshDisplayTargetSetsFromSettings(
                serializedDisplayTargetSets,
                queueRefreshWhenSelectionChanges: true);
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
        RaisePropertyChanged(nameof(PlayHistoryFolderDisplayPresets));
        RaisePropertyChanged(nameof(SelectedPlayHistoryFolderDisplayPreset));
        RaisePropertyChanged(nameof(CanEditPlayHistoryFolderDisplayPreset));
        RaisePropertyChanged(nameof(CanRemovePlayHistoryFolderDisplayPreset));
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
        public bool Matches(PlaylistTablePresentationSnapshot table)
        {
            return table?.PlaylistId is int playlistId && playlistIds.Contains(playlistId);
        }
    }

    private void PersistCustomFolderAdditionalOutputBaseDirsToSettings()
    {
        ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs =
            CustomFolderOutputBaseRegistry.SerializeBaseDirectories(CustomFolderAdditionalOutputBaseDirList);
    }

    private void RaiseCustomFolderAdditionalOutputBasePropertiesChanged()
    {
        RaisePropertyChanged(nameof(CustomFolderAdditionalOutputBaseDirList));
        RaisePropertyChanged(nameof(SelectedCustomFolderAdditionalOutputBaseDir));
        RaisePropertyChanged(nameof(SelectedCustomFolderAdditionalOutputBaseName));
        RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
        RaisePropertyChanged(nameof(AvailableBMSDirectories));
        RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        RaisePropertyChanged(nameof(PlaylistPropertyOutputBaseOptions));
        RaiseValidationStateChanged();
    }

    public IReadOnlyList<PlaylistCustomFolderOutputBaseOption> PlaylistPropertyOutputBaseOptions =>
        PlaylistCustomFolderOutputBaseOptions.Create(
            LR2CustomFolderOutputDir,
            CustomFolderAdditionalOutputBaseDirList);

    private void ApplyRuntimeSearchRootsForCurrentMode()
    {
        if (!searchRootRuntimePort.IsLibraryAttached)
        {
            return;
        }
        if (ApplicationSettings.OperationModeLR2DB && lr2config != null)
        {
            searchRootRuntimePort.ApplySearchTargets(lr2config.GetBMSSearchDirectories());
            return;
        }
        searchRootRuntimePort.ApplySearchTargets(GetStandaloneBmsRootPathsForCurrentSession());
    }

    private bool IsLR2SongDBPathValid()
    {
        return IsLR2SongDBPathValid(ApplicationSettings.LR2SongDBPath);
    }

    private bool IsLR2SongDBPathValid(string value)
    {
        return File.Exists(value);
    }

    private bool IsLR2ConfigXmlPathValid()
    {
        return IsLR2ConfigXmlPathValid(ApplicationSettings.LR2ConfigXmlPath);
    }

    private bool IsLR2ConfigXmlPathValid(string value)
    {
        return File.Exists(value);
    }

    private bool IsBeatorajaScoreDbPathValid()
    {
        return BeatorajaConfigService.IsPlayerScoreDbPathValid(ApplicationSettings.BeatorajaRootPath, ApplicationSettings.BeatorajaPlayerId);
    }

    private bool IsBeatorajaScoreDbPathValid(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(Path.GetFileName(value), "score.db", StringComparison.OrdinalIgnoreCase)
            && File.Exists(value);
    }

    private bool IsBeatorajaBmtTablePathValid()
    {
        return IsBeatorajaBmtTablePathValid(ApplicationSettings.BeatorajaBmtTablePath);
    }

    private bool IsBeatorajaBmtTablePathValid(string value)
    {
        return string.IsNullOrWhiteSpace(value) || Directory.Exists(value);
    }

    private bool IsBeatorajaRootPathValid()
    {
        return BeatorajaConfigService.IsBeatorajaRootPathValid(ApplicationSettings.BeatorajaRootPath);
    }

    private void RefreshBeatorajaDerivedSettings()
    {
        if (!BeatorajaConfigService.IsBeatorajaRootPathValid(ApplicationSettings.BeatorajaRootPath))
        {
            ApplicationSettings.BeatorajaScoreDbPath = string.Empty;
            ApplicationSettings.BeatorajaBmtTablePath = string.Empty;
            return;
        }
        List<string> playerIds = BeatorajaConfigService.GetPlayerIds(ApplicationSettings.BeatorajaRootPath);
        if (string.IsNullOrWhiteSpace(ApplicationSettings.BeatorajaPlayerId) || !playerIds.Contains(ApplicationSettings.BeatorajaPlayerId, StringComparer.OrdinalIgnoreCase))
        {
            string configuredPlayerId = BeatorajaConfigService.GetConfiguredPlayerId(ApplicationSettings.BeatorajaRootPath);
            ApplicationSettings.BeatorajaPlayerId = playerIds.Contains(configuredPlayerId, StringComparer.OrdinalIgnoreCase)
                ? configuredPlayerId
                : (playerIds.FirstOrDefault() ?? string.Empty);
        }
        ApplicationSettings.BeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(ApplicationSettings.BeatorajaRootPath, ApplicationSettings.BeatorajaPlayerId);
        ApplicationSettings.BeatorajaBmtTablePath = BeatorajaConfigService.GetTablePath(ApplicationSettings.BeatorajaRootPath);
    }

    private bool IsuBMplayPathValid()
    {
        return IsuBMplayPathValid(ApplicationSettings.uBMplayPath);
    }

    private bool IsuBMplayPathValid(string value)
    {
        return File.Exists(value);
    }

    private bool IsBMIIDXViewPathValid()
    {
        return IsuBMplayPathValid(ApplicationSettings.BMIIDXViewPath);
    }

    private bool IsBMIIDXViewPathValid(string value)
    {
        return File.Exists(value);
    }

    private bool IsLR2CustomFolderOutputDirValid()
    {
        return IsLR2CustomFolderOutputDirValid(ApplicationSettings.LR2CustomFolderOutputBaseDir);
    }

    private bool IsLR2CustomFolderOutputDirValid(string value)
    {
        return ValidateCustomFolderOutputBaseDir(value, out _);
    }

    private bool IsBMSInstallDirValid()
    {
        return IsBMSInstallDirValid(ApplicationSettings.BMSInstallDir);
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
        return IsLR2CustomFolderAsRootOutputDirValid(ApplicationSettings.LR2CustomFolderOutputBaseDirRootType);
    }

    private bool IsLR2CustomFolderAsRootOutputDirValid(string value)
    {
        return ValidateCustomFolderAsRootOutputBaseDir(value, out _);
    }

    private bool IsTableListURLValid()
    {
        return IsTableListURLValid(ApplicationSettings.TableListURL);
    }

    private bool IsTableListURLValid(Uri value)
    {
        return value != null && Uri.TryCreate(value.OriginalString, UriKind.RelativeOrAbsolute, out value);
    }

    private bool IsPlaylistMd5UrlMappingTsvUriValid()
    {
        return IsPlaylistMd5UrlMappingTsvUriValid(ApplicationSettings.PlaylistMd5UrlMappingTsvUri);
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
        return IsLR2BackupPathValid(ApplicationSettings.LR2BackupPath);
    }

    private bool IsLR2BackupPathValid(string value)
    {
        return Directory.Exists(value);
    }

    private bool IsLR2BackupSpanValid()
    {
        return IsLR2BackupSpanValid(ApplicationSettings.LR2BackupSpan);
    }

    private bool IsLR2BackupSpanValid(int value)
    {
        return value > 0;
    }

    private bool IsLR2BackupNumValid()
    {
        return IsLR2BackupNumValid(ApplicationSettings.LR2BackupNum);
    }

    private bool IsLR2BackupNumValid(int value)
    {
        return value > 0;
    }

    private bool IsStagefilePathValid()
    {
        return IsStagefilePathValid(ApplicationSettings.StagefilePath);
    }

    private bool IsStagefilePathValid(string value)
    {
        return File.Exists(value);
    }

    private bool IsFolderNameFormatValid()
    {
        return IsFolderNameFormatValid(ApplicationSettings.FolderNameFormat);
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
        return audioDeviceCatalog.IsEncoderAvailable(encoder, EncoderExeDir);
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
            ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
        }
        catch (Exception ex)
        {
            ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
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

    public async Task AddBmsSearchRootPathFromMainWindowPicker(string path)
    {
        if (path == null)
        {
            return;
        }
        AddBmsSearchRootPaths([path], null, saveImmediately: true);
        if (isSearchRootsChanged)
        {
            if (!ApplicationSettings.OperationModeLR2DB)
            {
                PersistStandaloneBmsRootPathsToSettings();
                settingsEditSession.Save();
            }
            ApplyRuntimeSearchRootsForCurrentMode();
            if (isBMSDirectoryAdded)
            {
                await ReloadFileDiffAsync();
            }
            else
            {
                searchRootRuntimePort.InvalidateLibraryFolderCache();
            }
            isSearchRootsChanged = false;
            isBMSDirectoryAdded = false;
        }
        else
        {
            searchRootRuntimePort.InvalidateLibraryFolderCache();
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
        return string.IsNullOrWhiteSpace(propertyName) ? null : propertyName;
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
            ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
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
            if (!ShowUiConfirmation(
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
            ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
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
            config = lr2config ?? new LR2Config(ApplicationSettings.LR2ConfigXmlPath);
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
        if (string.IsNullOrWhiteSpace(baseName) || !workspacePort.HasPlaylistTables)
        {
            return 0;
        }
        return workspacePort.CapturePlaylistPresentationSnapshots().Count(table =>
            table != null
            && string.Equals(table.CustomFolderOutputBaseName, baseName, StringComparison.OrdinalIgnoreCase));
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
                GetType().GetProperty(settingPropertyPath).GetSetMethod().Invoke(this, [requestedPath]);
            }
            string after = SerializeBmsRootPathsForChangeTracking(StandaloneBmsRootPathList);
            isSearchRootsChanged = !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
            isBMSDirectoryAdded = isSearchRootsChanged;
            RaisePropertyChanged(nameof(StandaloneBmsRootPathList));
            RaisePropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
            RaiseValidationStateChanged();
        }
        catch (Exception ex)
        {
            ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
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
            RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
            RaisePropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
            RaiseValidationStateChanged();
            if (!string.IsNullOrWhiteSpace(settingPropertyPath))
            {
                GetType().GetProperty(settingPropertyPath).GetSetMethod().Invoke(this, [requestedPath]);
            }
            if (saveImmediately)
            {
                lr2config.Save();
            }
        }
        catch (ArgumentException ex)
        {
            ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
        }
        catch (Exception ex)
        {
            ShowUiMessage(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
        }
    }

    internal async Task RequestRemoveBmsSearchRootAsync(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !LongPathFileSystem.DirectoryExists(dir))
        {
            return;
        }

        UiDialogResult confirmation = await schemaDialogs.ConfirmAsync(new UiConfirmationRequest(
            BeMusicSeeker.Properties.Resources.Msg_unregister_root_folder,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel));
        if (!ToUiConfirmationDecision(
            confirmation,
            "treeViewLibraryFolderContextMenuItemUnregisterRootFolder"))
        {
            return;
        }

        await RemoveBmsSearchRootAndSaveAsync(dir);
    }

    private async Task RemoveBmsSearchRootAndSaveAsync(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return;
        }
        if (ApplicationSettings.OperationModeLR2DB)
        {
            Exception removalFailure = RemoveBMSDirectoryFromLR2Config(dir, saveImmediately: true);
            if (removalFailure != null)
            {
                await PresentBmsSearchRootRemovalFailureAsync(removalFailure);
                return;
            }
            if (isSearchRootsChanged)
            {
                ApplyRuntimeSearchRootsForCurrentMode();
                if (isBMSDirectoryRemoved)
                {
                    await ReloadFileDiffAsync();
                }
                else
                {
                    searchRootRuntimePort.InvalidateLibraryFolderCache();
                }
                isSearchRootsChanged = false;
                isBMSDirectoryRemoved = false;
            }
            else
            {
                searchRootRuntimePort.InvalidateLibraryFolderCache();
            }
            return;
        }
        List<string> previousStandaloneRoots = [.. StandaloneBmsRootPathList];
        string previousStandaloneSelection = SelectedStandaloneBmsRootPath;
        string previousStandaloneSerializedPaths = ApplicationSettings.StandaloneBmsRootPaths;
        string previousLegacyBmsRootPath = ApplicationSettings.BMSRootPath;
        Exception standaloneRemovalFailure = RemoveStandaloneBmsRootPath(dir);
        if (standaloneRemovalFailure != null)
        {
            await PresentBmsSearchRootRemovalFailureAsync(standaloneRemovalFailure);
            return;
        }
        if (isSearchRootsChanged)
        {
            try
            {
                PersistStandaloneBmsRootPathsToSettings();
                settingsEditSession.Save();
            }
            catch
            {
                RestoreStandaloneBmsRootRemovalState(
                    previousStandaloneRoots,
                    previousStandaloneSelection,
                    previousStandaloneSerializedPaths,
                    previousLegacyBmsRootPath);
                throw;
            }
            ApplyRuntimeSearchRootsForCurrentMode();
            if (isBMSDirectoryRemoved)
            {
                await ReloadFileDiffAsync();
            }
            else
            {
                searchRootRuntimePort.InvalidateLibraryFolderCache();
            }
            isSearchRootsChanged = false;
            isBMSDirectoryRemoved = false;
        }
        else
        {
            searchRootRuntimePort.InvalidateLibraryFolderCache();
        }
    }

    private void RemoveBMSDirectoryFromSearchRoots(string dir)
    {
        if (OperationModeLR2DB)
        {
            Exception removalFailure = RemoveBMSDirectoryFromLR2Config(dir, saveImmediately: false);
            if (removalFailure != null)
            {
                ShowUiMessage(removalFailure.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
            return;
        }
        Exception standaloneRemovalFailure = RemoveStandaloneBmsRootPath(dir);
        if (standaloneRemovalFailure != null)
        {
            ShowUiMessage(standaloneRemovalFailure.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
        }
    }

    private Exception RemoveStandaloneBmsRootPath(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }
        try
        {
            string normalizedDir = TrimDirectorySeparatorUnlessRoot(Path.GetFullPath(dir.Trim()));
            if (!string.IsNullOrWhiteSpace(ApplicationSettings.BMSInstallDir) && IsSameOrChildPath(ApplicationSettings.BMSInstallDir, normalizedDir))
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
            RaisePropertyChanged(nameof(StandaloneBmsRootPathList));
            RaisePropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
            RaisePropertyChanged(nameof(BMSInstallDir));
            RaiseValidationStateChanged();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private Exception RemoveBMSDirectoryFromLR2Config(string dir, bool saveImmediately)
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
            bool removed = saveImmediately
                ? lr2config.RemoveBMSSearchDirectoriesAndSave([dir])
                : lr2config.RemoveBMSSearchDirectories([dir]);
            if (removed)
            {
                isSearchRootsChanged = true;
                if (string.Equals(selectedLR2ConfigBmsDirectory, dir, StringComparison.OrdinalIgnoreCase))
                {
                    selectedLR2ConfigBmsDirectory = LR2ConfigBMSDirectories.FirstOrDefault();
                }
                RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
                RaisePropertyChanged(nameof(AvailableBMSDirectories));
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
                RaisePropertyChanged(nameof(BMSInstallDir));
                RaiseValidationStateChanged();
                if (searchRootRuntimePort.HasOwnedChartUnderRealPath(dir))
                {
                    isBMSDirectoryRemoved = true;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private void RestoreStandaloneBmsRootRemovalState(
        IReadOnlyList<string> previousRoots,
        string previousSelection,
        string previousSerializedPaths,
        string previousLegacyBmsRootPath)
    {
        StandaloneBmsRootPathList.Clear();
        foreach (string path in previousRoots ?? [])
        {
            StandaloneBmsRootPathList.Add(path);
        }
        ApplicationSettings.StandaloneBmsRootPaths = previousSerializedPaths;
        ApplicationSettings.BMSRootPath = previousLegacyBmsRootPath;
        SelectedStandaloneBmsRootPath = previousSelection;
        RaisePropertyChanged(nameof(StandaloneBmsRootPathList));
        RaisePropertyChanged(nameof(AvailableBMSDirectories));
        RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        RaisePropertyChanged(nameof(BMSInstallDir));
        RaiseValidationStateChanged();
        isSearchRootsChanged = false;
        isBMSDirectoryRemoved = false;
    }

    private async Task PresentBmsSearchRootRemovalFailureAsync(Exception exception)
    {
        UiDialogResult result = await schemaDialogs.ShowMessageAsync(new UiMessageRequest(
            exception.Message,
            BeMusicSeeker.Properties.Resources.Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK));
        UiDialogRoute.ThrowIfNotShown(result, "treeViewLibraryFolderContextMenuItemUnregisterRootFolder failure notification");
    }

    internal async Task RunAudioDeviceTestAsync()
    {
        if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress)
        {
            return;
        }

        AudioDeviceTestRequest request = new(
            audioOutputSelectionDraft.Backend,
            audioOutputSelectionDraft.DeviceIdentity,
            audioOutputSelectionDraft.DeviceName,
            ApplicationSettings.PlayerSampleRate,
            ApplicationSettings.PlayerFormat,
            ApplicationSettings.PlayerBufferSize,
            ApplicationSettings.PlayerWASAPIParam,
            ApplicationSettings.uBMplayVolume,
            playSound: true);
        AudioDeviceTestStatusMessage = null;
        Task<AudioDeviceTestResult> testTask = audioDeviceTestWorkflow.TryRunAsync(request);
        RaisePropertyChanged(nameof(IsAudioDeviceTestInProgress));
        RaisePropertyChanged(nameof(IsAudioDeviceTestAvailable));
        RaisePropertyChanged(nameof(IsEditCompletionEnabled));
        RaisePropertyChanged(nameof(IsEditCancellationEnabled));
        try
        {
            AudioDeviceTestResult result = await testTask;
            if (result == null)
            {
                return;
            }

            AudioDeviceTestStatusMessage = FormatAudioDeviceTestResult(result);

            if (!CanApplyAudioDeviceTestResult(request, result))
            {
                return;
            }

            audioOutputSelectionDraft = new AudioOutputSelection(
                result.ActualBackend,
                request.PlayerDevice == null ? null : result.ActualDevice,
                request.PlayerDevice == null ? null : result.ActualDeviceName);
            if (!string.IsNullOrWhiteSpace(request.PlayerDevice))
            {
                playerDeviceNames = BuildPlayerDeviceNames(audioOutputSelectionDraft.Backend);
            }
            if (request.PlayerSampleRate != SampleRate.AUTO)
            {
                ApplicationSettings.PlayerSampleRate = result.ActualRate;
            }
            if (request.PlayerFormat != SampleFormat.AUTO)
            {
                ApplicationSettings.PlayerFormat = result.EngineFormat;
            }
            PlayerLatency = result.Latency;
            RaisePropertyChanged(nameof(PlayerDriverIndex));
            RaisePropertyChanged(nameof(PlayerDeviceNames));
            RaisePropertyChanged(nameof(PlayerDevice));
            RaisePropertyChanged(nameof(SelectedPlayerDevice));
            RaisePropertyChanged(nameof(PlayerSampleRate));
            RaisePropertyChanged(nameof(PlayerFormat));
            RaisePropertyChanged(nameof(PlayerLatency));
        }
        catch (AudioInitializationException exception)
        {
            string message = FormatAudioInitializationFailure(exception);
            AudioDeviceTestStatusMessage = message;
            UiDialogResult dialogResult = await schemaDialogs.ShowMessageAsync(new UiMessageRequest(
                message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK));
            UiDialogRoute.ThrowIfNotShown(dialogResult, "Audio device initialization failure notification");
        }
        finally
        {
            RaisePropertyChanged(nameof(IsAudioDeviceTestInProgress));
            RaisePropertyChanged(nameof(IsAudioDeviceTestAvailable));
            RaisePropertyChanged(nameof(IsEditCompletionEnabled));
            RaisePropertyChanged(nameof(IsEditCancellationEnabled));
        }
    }

    private static string FormatAudioDeviceTestResult(AudioDeviceTestResult result)
    {
        string requestedDevice = DescribeAudioDevice(result.RequestedDevice, result.RequestedDeviceName);
        string actualDevice = DescribeAudioDevice(result.ActualDevice, result.ActualDeviceName);
        if (!result.Succeeded)
        {
            return string.Format(
                BeMusicSeeker.Properties.Resources.AudioDeviceTestStreamFailureFormat,
                result.RequestedBackend,
                result.ActualBackend,
                BeMusicSeeker.Properties.Resources.AudioDeviceTestStreamProgressFailureReason);
        }
        if (result.FallbackOccurred)
        {
            return string.Format(
                BeMusicSeeker.Properties.Resources.AudioDeviceTestFallbackFormat,
                result.RequestedBackend,
                requestedDevice,
                result.ActualBackend,
                actualDevice,
                BeMusicSeeker.Properties.Resources.AudioDeviceTestFallbackReason);
        }
        return string.Format(
            BeMusicSeeker.Properties.Resources.AudioDeviceTestSuccessFormat,
            result.ActualBackend,
            actualDevice,
            result.ActualRate,
            result.EngineFormat,
            result.EndpointFormat,
            result.ActualChannels,
            result.Latency);
    }

    private static string FormatAudioInitializationFailure(AudioInitializationException exception)
    {
        return string.Format(
            BeMusicSeeker.Properties.Resources.AudioDeviceTestInitializationErrorFormat,
            exception.RequestedBackend,
            exception.ActualBackend,
            exception.Stage,
            exception.NativeErrorSource,
            exception.NativeErrorCode?.ToString() ?? "-",
            DescribeAudioDevice(exception.RequestedDevice.Driver, exception.RequestedDevice.Name),
            DescribeAudioDevice(exception.ActualDevice.Driver, exception.ActualDevice.Name));
    }

    private static string DescribeAudioDevice(string identity, string name)
    {
        if (string.IsNullOrWhiteSpace(identity) && string.IsNullOrWhiteSpace(name))
        {
            return BeMusicSeeker.Properties.Resources.AudioDeviceDefault;
        }
        return string.IsNullOrWhiteSpace(name) ? identity : name;
    }

    private bool CanApplyAudioDeviceTestResult(
        AudioDeviceTestRequest request,
        AudioDeviceTestResult result)
    {
        if (!result.Succeeded
            || result.FallbackOccurred
            || result.IsSilentFallback
            || result.RequestedBackend != request.PlayerDriver
            || result.ActualBackend != request.PlayerDriver
            || !IsCurrentAudioDeviceTestRequest(request))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.PlayerDevice)
            && !string.Equals(result.ActualDevice, request.PlayerDevice, StringComparison.Ordinal))
        {
            return false;
        }
        if (request.PlayerSampleRate != SampleRate.AUTO
            && result.ActualRate != request.PlayerSampleRate)
        {
            return false;
        }
        return request.PlayerFormat == SampleFormat.AUTO
            || result.EngineFormat == request.PlayerFormat;
    }

    private bool IsCurrentAudioDeviceTestRequest(AudioDeviceTestRequest request)
    {
        return audioOutputSelectionDraft.Backend == request.PlayerDriver
            && string.Equals(audioOutputSelectionDraft.DeviceIdentity, request.PlayerDevice, StringComparison.Ordinal)
            && string.Equals(audioOutputSelectionDraft.DeviceName, request.PlayerDeviceName, StringComparison.Ordinal)
            && ApplicationSettings.PlayerSampleRate == request.PlayerSampleRate
            && ApplicationSettings.PlayerFormat == request.PlayerFormat
            && ApplicationSettings.PlayerBufferSize.Equals(request.PlayerBufferSize)
            && ApplicationSettings.PlayerWASAPIParam == request.PlayerWASAPIParam
            && ApplicationSettings.uBMplayVolume == request.PlayerVolume;
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
        operationModeLR2DB = ApplicationSettings.OperationModeLR2DB;
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
        tempOperationModeLR2DB = ApplicationSettings.OperationModeLR2DB;
        tempLR2RootPath = ApplicationSettings.LR2RootPath;
        tempLR2SongDBPath = ApplicationSettings.LR2SongDBPath;
        tempLR2ConfigXmlPath = ApplicationSettings.LR2ConfigXmlPath;
        tempUseBeatorajaScoreDb = ApplicationSettings.UseBeatorajaScoreDb;
        tempBeatorajaRootPath = ApplicationSettings.BeatorajaRootPath;
        tempBeatorajaPlayerId = ApplicationSettings.BeatorajaPlayerId;
        tempBeatorajaScoreDbPath = ApplicationSettings.BeatorajaScoreDbPath;
        tempEnableBeatorajaBmtOutput = ApplicationSettings.EnableBeatorajaBmtOutput;
        tempKeepBeatorajaBmtFilesWhenOutputDisabled = ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled;
        tempBeatorajaBmtHashOutputMode = BeatorajaBmtHashOutputMode;
        tempBeatorajaBmtTablePath = ApplicationSettings.BeatorajaBmtTablePath;
        tempRegisterBeatorajaBmtUrls = ApplicationSettings.RegisterBeatorajaBmtUrls;
        tempBMSRootPath = ApplicationSettings.BMSRootPath;
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
        tempuBMplayPath = ApplicationSettings.uBMplayPath;
        tempBMIIDXViewPath = ApplicationSettings.BMIIDXViewPath;
        tempUsePlayeruBMplay = ApplicationSettings.UsePlayeruBMplay;
        tempUsePlayerLR2body = ApplicationSettings.UsePlayerLR2body;
        tempUsePlayerBMIIDXView = ApplicationSettings.UsePlayerBMIIDXView;
        tempLR2bodyResolution = LR2bodyResolution;
        tempIsSaveLR2bodyWindowPosition = ApplicationSettings.IsSaveLR2bodyWindowPosition;
        tempLR2CustomFolderOutputDir = ApplicationSettings.LR2CustomFolderOutputBaseDir;
        tempLR2CustomFolderAdditionalOutputBaseDirs = ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        tempLR2CustomFolderAsRootOutputDir = ApplicationSettings.LR2CustomFolderOutputBaseDirRootType;
        tempPlaylistDefaultIgnoreFolderOutput = ApplicationSettings.PlaylistDefaultIgnoreFolderOutput;
        tempBMSInstallDir = ApplicationSettings.BMSInstallDir;
        tempTableListURL = ApplicationSettings.TableListURL;
        tempEnablePlaylistUrlCompletion = ApplicationSettings.EnablePlaylistUrlCompletion;
        tempOverwritePlaylistUrlsWithCompletion = ApplicationSettings.OverwritePlaylistUrlsWithCompletion;
        tempEnableStellaFullPlaylistUrlCompletion = ApplicationSettings.EnableStellaFullPlaylistUrlCompletion;
        tempPlaylistMd5UrlMappingTsvUri = ApplicationSettings.PlaylistMd5UrlMappingTsvUri;
        tempPlayHistoryDisplayTargetSetsJson = playHistoryDisplaySettingsStore.DisplayTargetSetsJson;
        tempIsLR2BackupEnabled = ApplicationSettings.IsLR2BackupEnabled;
        tempLR2BackupPath = ApplicationSettings.LR2BackupPath;
        tempLR2BackupTarget = ApplicationSettings.LR2BackupTarget;
        tempLR2BackupSpan = ApplicationSettings.LR2BackupSpan;
        tempLR2BackupNum = ApplicationSettings.LR2BackupNum;
        tempUseExternalWebBrowser = ApplicationSettings.UseExternalWebBrowser;
        tempUseExternalPanelImage = ApplicationSettings.UseExternalPanelImage;
        tempAppearanceTheme = AppThemeService.NormalizeTheme(ApplicationSettings.AppearanceTheme);
        tempCustomTableFontSize = ApplicationSettings.CustomTableFontSize;
        tempCustomTableRowHeight = ApplicationSettings.CustomTableRowHeight;
        tempCustomTableHeaderHeight = ApplicationSettings.CustomTableHeaderHeight;
        tempStagefilePath = ApplicationSettings.StagefilePath;
        tempFolderNameFormat = ApplicationSettings.FolderNameFormat;
        tempUseOnlyShiftJISChars = ApplicationSettings.UseOnlyShiftJISChars;
        tempShowScoreViewerRegisterConfirmMsg = ApplicationSettings.ShowScoreViewerRegisterConfirmMsg;
        tempShowDiffBMSInstallConfirmMsg = ApplicationSettings.ShowDiffBMSInstallConfirmMsg;
        tempShowDuplicateFileCheckConfirmMsg = ApplicationSettings.ShowDuplicateFileCheckConfirmMsg;
        tempShowRecommUpdatedMsg = ApplicationSettings.ShowRecommUpdatedMsg;
        tempScanBmsFilesOnStartup = ApplicationSettings.ScanBmsFilesOnStartup;
        tempSkipInitPlaylistLoad = ApplicationSettings.SkipInitPlaylistLoad;
        tempStartupSelectInstallPending = ApplicationSettings.StartupSelectInstallPending;
        tempEnableReadOptimizedPragmas = ApplicationSettings.EnableReadOptimizedPragmas;
        tempEstimateOfflineScoreRanking = ApplicationSettings.EstimateOfflineScoreRanking;
        tempUpdateLr2IrRankingCacheOnStartup = ApplicationSettings.UpdateLr2IrRankingCacheOnStartup;
        tempEnableDownloadLr2IrScoreAndDetectUnsent = ApplicationSettings.EnableDownloadLr2IrScoreAndDetectUnsent;
        tempEnableAutoInstall = ApplicationSettings.AutoInstall;
        tempKeepInstallablePackagesPending = ApplicationSettings.KeepInstallablePackagesPending;
        tempAutoApplyAmbiguousInstallDestination = ApplicationSettings.AutoApplyAmbiguousInstallDestination;
        tempDeletePendingPackageSourceAfterInstall = ApplicationSettings.DeletePendingPackageSourceAfterInstall;
        tempEnableSmartComponentOverwrite = ApplicationSettings.EnableSmartComponentOverwrite;
        tempKeepSmartOverwriteProtectedFilesByRenaming = ApplicationSettings.KeepSmartOverwriteProtectedFilesByRenaming;
        tempEncoderSampleRate = ApplicationSettings.EncoderSampleRate;
        tempEncoderIndex = (int)ApplicationSettings.Encoder;
        tempEncoderFormat = ApplicationSettings.EncoderFormat;
        tempEncoderNormalization = audioSettingsGateway.EncoderNormalization;
        tempEncoderExeDir = ApplicationSettings.EncoderExeDir;
        tempEncoderAmplifier = ApplicationSettings.EncoderAmplifier;
        tempEncoderQuality = ApplicationSettings.EncoderQuality;
        tempEncodeFileNameFormat = ApplicationSettings.EncodeFileNameFormat;
        savedAudioOutputSelection = audioSettingsGateway.CaptureOutputSelection();
        audioOutputSelectionDraft = savedAudioOutputSelection;
        tempPlayerSampleRate = ApplicationSettings.PlayerSampleRate;
        tempPlayerFormat = ApplicationSettings.PlayerFormat;
        tempPlayerBufferSize = ApplicationSettings.PlayerBufferSize;
        tempPlayerWASAPIParam = ApplicationSettings.PlayerWASAPIParam;
        tempLanguage = ApplicationSettings.Lang;
        tempLanguageDisplayName = ApplicationSettings.LangDisplayName;
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
            RaisePropertyChanged(nameof(IsOperationModeChanged));
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
            || HasPlayHistoryFolderDisplayPresetDraftsChanged()
            || scoreReloadPending
            || fileDiffReloadPending;
    }

    private bool HasSettingValueChanges()
    {
        return HasPathSettingValueChanged(tempLR2RootPath, ApplicationSettings.LR2RootPath, value => IsLR2RootPathValid(value) || IsLR2PlayerRootPathValid(value))
            || HasPathSettingValueChanged(tempLR2SongDBPath, ApplicationSettings.LR2SongDBPath, IsLR2SongDBPathValid)
            || HasPathSettingValueChanged(tempLR2ConfigXmlPath, ApplicationSettings.LR2ConfigXmlPath, IsLR2ConfigXmlPathValid)
            || tempUseBeatorajaScoreDb != ApplicationSettings.UseBeatorajaScoreDb
            || !string.Equals(tempBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempBeatorajaPlayerId, ApplicationSettings.BeatorajaPlayerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempBeatorajaScoreDbPath, ApplicationSettings.BeatorajaScoreDbPath, StringComparison.OrdinalIgnoreCase)
            || tempEnableBeatorajaBmtOutput != ApplicationSettings.EnableBeatorajaBmtOutput
            || tempKeepBeatorajaBmtFilesWhenOutputDisabled != ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled
            || !string.Equals(tempBeatorajaBmtHashOutputMode, BeatorajaBmtHashOutputMode, StringComparison.Ordinal)
            || !string.Equals(tempBeatorajaBmtTablePath, ApplicationSettings.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase)
            || tempRegisterBeatorajaBmtUrls != ApplicationSettings.RegisterBeatorajaBmtUrls
            || HasPathSettingValueChanged(tempBMSRootPath, ApplicationSettings.BMSRootPath, IsBMSRootPathValid)
            || HasPathSettingValueChanged(tempuBMplayPath, ApplicationSettings.uBMplayPath, IsuBMplayPathValid)
            || HasPathSettingValueChanged(tempBMIIDXViewPath, ApplicationSettings.BMIIDXViewPath, IsBMIIDXViewPathValid)
            || tempUsePlayeruBMplay != ApplicationSettings.UsePlayeruBMplay
            || tempUsePlayerLR2body != ApplicationSettings.UsePlayerLR2body
            || tempUsePlayerBMIIDXView != ApplicationSettings.UsePlayerBMIIDXView
            || tempLR2bodyResolution != LR2bodyResolution
            || tempIsSaveLR2bodyWindowPosition != ApplicationSettings.IsSaveLR2bodyWindowPosition
            || HasCustomFolderOutputBaseSettingsChanged()
            || tempPlaylistDefaultIgnoreFolderOutput != ApplicationSettings.PlaylistDefaultIgnoreFolderOutput
            || !string.Equals(tempBMSInstallDir, ApplicationSettings.BMSInstallDir, StringComparison.OrdinalIgnoreCase)
            || !IsSameUri(tempTableListURL, ApplicationSettings.TableListURL)
            || tempEnablePlaylistUrlCompletion != ApplicationSettings.EnablePlaylistUrlCompletion
            || tempOverwritePlaylistUrlsWithCompletion != ApplicationSettings.OverwritePlaylistUrlsWithCompletion
            || tempEnableStellaFullPlaylistUrlCompletion != ApplicationSettings.EnableStellaFullPlaylistUrlCompletion
            || !string.Equals(tempPlaylistMd5UrlMappingTsvUri, ApplicationSettings.PlaylistMd5UrlMappingTsvUri, StringComparison.Ordinal)
            || !string.Equals(tempPlayHistoryDisplayTargetSetsJson, playHistoryDisplaySettingsStore.DisplayTargetSetsJson, StringComparison.Ordinal)
            || tempIsLR2BackupEnabled != ApplicationSettings.IsLR2BackupEnabled
            || HasPathSettingValueChanged(tempLR2BackupPath, ApplicationSettings.LR2BackupPath, IsLR2BackupPathValid)
            || tempLR2BackupTarget != ApplicationSettings.LR2BackupTarget
            || tempLR2BackupSpan != ApplicationSettings.LR2BackupSpan
            || tempLR2BackupNum != ApplicationSettings.LR2BackupNum
            || tempUseExternalWebBrowser != ApplicationSettings.UseExternalWebBrowser
            || tempUseExternalPanelImage != ApplicationSettings.UseExternalPanelImage
            || !string.Equals(AppThemeService.NormalizeTheme(tempAppearanceTheme), AppThemeService.NormalizeTheme(ApplicationSettings.AppearanceTheme), StringComparison.Ordinal)
            || !tempCustomTableFontSize.Equals(ApplicationSettings.CustomTableFontSize)
            || !tempCustomTableRowHeight.Equals(ApplicationSettings.CustomTableRowHeight)
            || !tempCustomTableHeaderHeight.Equals(ApplicationSettings.CustomTableHeaderHeight)
            || HasPathSettingValueChanged(tempStagefilePath, ApplicationSettings.StagefilePath, IsStagefilePathValid)
            || !string.Equals(tempFolderNameFormat, ApplicationSettings.FolderNameFormat, StringComparison.Ordinal)
            || tempUseOnlyShiftJISChars != ApplicationSettings.UseOnlyShiftJISChars
            || tempShowScoreViewerRegisterConfirmMsg != ApplicationSettings.ShowScoreViewerRegisterConfirmMsg
            || tempShowDiffBMSInstallConfirmMsg != ApplicationSettings.ShowDiffBMSInstallConfirmMsg
            || tempShowDuplicateFileCheckConfirmMsg != ApplicationSettings.ShowDuplicateFileCheckConfirmMsg
            || tempShowRecommUpdatedMsg != ApplicationSettings.ShowRecommUpdatedMsg
            || tempScanBmsFilesOnStartup != ApplicationSettings.ScanBmsFilesOnStartup
            || tempSkipInitPlaylistLoad != ApplicationSettings.SkipInitPlaylistLoad
            || tempStartupSelectInstallPending != ApplicationSettings.StartupSelectInstallPending
            || tempEnableReadOptimizedPragmas != ApplicationSettings.EnableReadOptimizedPragmas
            || tempEstimateOfflineScoreRanking != ApplicationSettings.EstimateOfflineScoreRanking
            || tempUpdateLr2IrRankingCacheOnStartup != ApplicationSettings.UpdateLr2IrRankingCacheOnStartup
            || tempEnableDownloadLr2IrScoreAndDetectUnsent != ApplicationSettings.EnableDownloadLr2IrScoreAndDetectUnsent
            || tempEnableAutoInstall != ApplicationSettings.AutoInstall
            || tempKeepInstallablePackagesPending != ApplicationSettings.KeepInstallablePackagesPending
            || tempAutoApplyAmbiguousInstallDestination != ApplicationSettings.AutoApplyAmbiguousInstallDestination
            || tempDeletePendingPackageSourceAfterInstall != ApplicationSettings.DeletePendingPackageSourceAfterInstall
            || tempEnableSmartComponentOverwrite != ApplicationSettings.EnableSmartComponentOverwrite
            || tempKeepSmartOverwriteProtectedFilesByRenaming != ApplicationSettings.KeepSmartOverwriteProtectedFilesByRenaming
            || tempEncoderSampleRate != ApplicationSettings.EncoderSampleRate
            || tempEncoderIndex != (int)ApplicationSettings.Encoder
            || tempEncoderFormat != ApplicationSettings.EncoderFormat
            || tempEncoderNormalization != audioSettingsGateway.EncoderNormalization
            || !string.Equals(tempEncoderExeDir, ApplicationSettings.EncoderExeDir, StringComparison.OrdinalIgnoreCase)
            || tempEncoderAmplifier != ApplicationSettings.EncoderAmplifier
            || tempEncoderQuality != ApplicationSettings.EncoderQuality
            || !string.Equals(tempEncodeFileNameFormat, ApplicationSettings.EncodeFileNameFormat, StringComparison.Ordinal)
            || savedAudioOutputSelection != audioOutputSelectionDraft
            || tempPlayerSampleRate != ApplicationSettings.PlayerSampleRate
            || tempPlayerFormat != ApplicationSettings.PlayerFormat
            || tempPlayerBufferSize != ApplicationSettings.PlayerBufferSize
            || tempPlayerWASAPIParam != ApplicationSettings.PlayerWASAPIParam
            || !string.Equals(tempLanguage, ApplicationSettings.Lang, StringComparison.Ordinal)
            || !string.Equals(tempLanguageDisplayName, ApplicationSettings.LangDisplayName, StringComparison.Ordinal);
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
        return !string.Equals(tempLR2CustomFolderOutputDir, ApplicationSettings.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempLR2CustomFolderAsRootOutputDir, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase)
            || HasCustomFolderAdditionalOutputBaseDirsChanged();
    }

    private bool HasCustomFolderAdditionalOutputBaseDirsChanged()
    {
        return !string.Equals(tempLR2CustomFolderAdditionalOutputBaseDirs, ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal);
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

        bool playerSelectionChanged = tempUsePlayeruBMplay != ApplicationSettings.UsePlayeruBMplay
            || tempUsePlayerLR2body != ApplicationSettings.UsePlayerLR2body
            || tempUsePlayerBMIIDXView != ApplicationSettings.UsePlayerBMIIDXView;
        bool playerRuntimePathChanged =
            (ApplicationSettings.UsePlayeruBMplay
                && HasPathSettingValueChanged(tempuBMplayPath, ApplicationSettings.uBMplayPath, value => true))
            || (ApplicationSettings.UsePlayerBMIIDXView
                && HasPathSettingValueChanged(tempBMIIDXViewPath, ApplicationSettings.BMIIDXViewPath, value => true))
            || (ApplicationSettings.UsePlayerLR2body
                && (HasPathSettingValueChanged(tempLR2RootPath, ApplicationSettings.LR2RootPath, value => true)
                    || HasPathSettingValueChanged(tempLR2ConfigXmlPath, ApplicationSettings.LR2ConfigXmlPath, value => true)));
        bool forceInternalPlayerForStandaloneModeChange =
            !ApplicationSettings.OperationModeLR2DB
            && tempOperationModeLR2DB != ApplicationSettings.OperationModeLR2DB;
        if (playerSelectionChanged || playerRuntimePathChanged || forceInternalPlayerForStandaloneModeChange)
        {
            impact |= SettingsPostSaveImpact.PlayerRuntime;
        }

        if (ApplicationSettings.OperationModeLR2DB && ApplicationSettings.IsLR2BackupEnabled && tempIsLR2BackupEnabled != ApplicationSettings.IsLR2BackupEnabled)
        {
            impact |= SettingsPostSaveImpact.Lr2BackupEnabledNotice;
        }
        if (tempEnablePlaylistUrlCompletion != ApplicationSettings.EnablePlaylistUrlCompletion
            || tempOverwritePlaylistUrlsWithCompletion != ApplicationSettings.OverwritePlaylistUrlsWithCompletion
            || tempEnableStellaFullPlaylistUrlCompletion != ApplicationSettings.EnableStellaFullPlaylistUrlCompletion
            || !string.Equals(tempPlaylistMd5UrlMappingTsvUri, ApplicationSettings.PlaylistMd5UrlMappingTsvUri, StringComparison.Ordinal))
        {
            impact |= SettingsPostSaveImpact.PlaylistUrlCompletion;
        }

        bool lr2CoreSyncInputChanged =
            tempOperationModeLR2DB != ApplicationSettings.OperationModeLR2DB
            || !string.Equals(tempLR2RootPath, ApplicationSettings.LR2RootPath, StringComparison.OrdinalIgnoreCase);
        bool normalOutputBaseDirChanged =
            !string.Equals(tempLR2CustomFolderOutputDir, ApplicationSettings.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase);
        bool rootOutputBaseDirChanged =
            !string.Equals(tempLR2CustomFolderAsRootOutputDir, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase);
        bool additionalOutputBaseDirsChanged =
            !string.Equals(tempLR2CustomFolderAdditionalOutputBaseDirs, ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal);
        if (ApplicationSettings.OperationModeLR2DB && lr2CoreSyncInputChanged)
        {
            impact |= SettingsPostSaveImpact.Lr2CoreSync;
        }
        else if (ApplicationSettings.OperationModeLR2DB
            && (normalOutputBaseDirChanged || rootOutputBaseDirChanged || additionalOutputBaseDirsChanged))
        {
            impact |= SettingsPostSaveImpact.ExternalLr2FolderRowsSync;
        }

        if (tempEnableBeatorajaBmtOutput != ApplicationSettings.EnableBeatorajaBmtOutput
            || tempKeepBeatorajaBmtFilesWhenOutputDisabled != ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled
            || tempRegisterBeatorajaBmtUrls != ApplicationSettings.RegisterBeatorajaBmtUrls
            || !string.Equals(tempBeatorajaBmtHashOutputMode, BeatorajaBmtHashOutputMode, StringComparison.Ordinal)
            || !string.Equals(tempBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempBeatorajaBmtTablePath, ApplicationSettings.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase))
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
        bool tablesAvailable = workspacePort.HasPlaylistTables;
        try
        {
            if (!tablesAvailable)
            {
                return;
            }
            CustomFolderOutputSettingsSnapshot customFolderOutputSettingsAfterSave = null;
            if (impact.HasFlag(SettingsPostSaveImpact.CustomFolderSearchRootSync))
            {
                customFolderOutputSettingsAfterSave = customFolderOutputPort.CustomFolderOutputSettings
                    ?? throw new InvalidOperationException("Custom-folder output settings provider returned null after settings save.");
            }
            if (customFolderOutputSettingsAfterSave?.OperationModeLR2DB == true)
            {
                var stepStopwatch = Stopwatch.StartNew();
                while (!workspacePort.HasPlaylistTables)
                {
                    await Task.Delay(100);
                }
                CustomFolderOutputBaseSearchRootSyncPlan normalOutputBaseRootSyncPlan = default;
                CustomFolderOutputBaseSearchRootSyncResult normalOutputBaseRootSyncResult = default;
                bool rootOutputBaseRootSyncChanged = false;
                await workspacePort.RunWithPlaylistOperationNotificationsAsync(
                    async () =>
                    {
                        try
                        {
                            await Task.Run(delegate
                            {
                                normalOutputBaseRootSyncPlan = PrepareCustomFolderNormalOutputBaseSearchRootSyncWithSettings(customFolderOutputSettingsAfterSave);
                                if (!string.IsNullOrWhiteSpace(tempLR2CustomFolderOutputDir) && !string.IsNullOrWhiteSpace(customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDir) && tempLR2CustomFolderOutputDir != customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDir)
                                {
                                    customFolderOutputPort.ChangeCustomFolderBaseDirectoryWithSettings(
                                        tempLR2CustomFolderOutputDir,
                                        customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDir,
                                        tempLR2CustomFolderAdditionalOutputBaseDirs,
                                        customFolderOutputSettingsAfterSave.LR2CustomFolderAdditionalOutputBaseDirs,
                                        customFolderOutputSettingsAfterSave);
                                }
                                customFolderOutputPort.ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
                                    tempLR2CustomFolderAdditionalOutputBaseDirs,
                                    pendingCustomFolderAdditionalOutputBaseRenames,
                                    customFolderOutputSettingsAfterSave);
                                normalOutputBaseRootSyncResult = CompleteCustomFolderNormalOutputBaseSearchRootSyncWithSettings(
                                    normalOutputBaseRootSyncPlan,
                                    customFolderOutputSettingsAfterSave);
                                if (!string.IsNullOrWhiteSpace(tempLR2CustomFolderAsRootOutputDir) && !string.IsNullOrWhiteSpace(customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDirRootType) && tempLR2CustomFolderAsRootOutputDir != customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDirRootType)
                                {
                                    customFolderOutputPort.ChangeCustomFolderBaseDirectoryRootWithSettings(
                                        tempLR2CustomFolderAsRootOutputDir,
                                        customFolderOutputSettingsAfterSave.LR2CustomFolderOutputBaseDirRootType,
                                        customFolderOutputSettingsAfterSave);
                                }
                                rootOutputBaseRootSyncChanged = SyncCustomFolderOutputRootAfterSettingsChange(
                                    customFolderOutputSettingsAfterSave);
                            }).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            NLogWrapper.FileLogger?.Error(ex, "necessaryStepsAfterSaved failed");
                            throw;
                        }
                    },
                    "custom folder output base sync notification");
                ApplyCustomFolderNormalOutputBaseSearchRootSyncResult(normalOutputBaseRootSyncResult);
                if (rootOutputBaseRootSyncChanged)
                {
                    isSearchRootsChanged = true;
                    isBMSDirectoryAdded = true;
                    isBMSDirectoryRemoved = true;
                    ApplyRuntimeSearchRootsForCurrentMode();
                    RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
                    RaisePropertyChanged(nameof(AvailableBMSDirectories));
                    RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
                    RaiseValidationStateChanged();
                }
                customFolderSearchRootSyncMs = stepStopwatch.ElapsedMilliseconds;
            }
            var playerRuntimeStopwatch = Stopwatch.StartNew();
            bool forceInternalPlayerForStandaloneModeChange =
                !ApplicationSettings.OperationModeLR2DB
                && tempOperationModeLR2DB != ApplicationSettings.OperationModeLR2DB;
            if (impact.HasFlag(SettingsPostSaveImpact.PlayerRuntime))
            {
                IBMSPlayer replacementPlayer = forceInternalPlayerForStandaloneModeChange
                    ? playerFactoryPort.CreateDefaultBmsPlayer()
                    : playerFactoryPort.CreateBmsPlayerForSettings(StartupSettingsSnapshot.CreateCurrent(ApplicationSettings));
                await playbackRuntimePort.ApplyPlayerSettingsAsync(replacementPlayer);
            }
            playbackRuntimePort.NotifySettingsChanged();
            playerRuntimeMs = playerRuntimeStopwatch.ElapsedMilliseconds;
            var lr2BackupNoticeStopwatch = Stopwatch.StartNew();
            if (impact.HasFlag(SettingsPostSaveImpact.Lr2BackupEnabledNotice))
            {
                ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_LR2ConfigBackupEnabledNextStartup, BeMusicSeeker.Properties.Resources.Confirm, MessageBoxImage.Asterisk);
            }
            lr2BackupNoticeMs = lr2BackupNoticeStopwatch.ElapsedMilliseconds;
            var playlistUrlCompletionStopwatch = Stopwatch.StartNew();
            if (impact.HasFlag(SettingsPostSaveImpact.PlaylistUrlCompletion))
            {
                workspacePort.SchedulePlaylistUrlCompletionRefresh("SettingDialog.SaveSettings");
            }
            playlistUrlCompletionMs = playlistUrlCompletionStopwatch.ElapsedMilliseconds;
            var lr2GeneratedDataSyncStopwatch = Stopwatch.StartNew();
            if (impact.HasFlag(SettingsPostSaveImpact.Lr2CoreSync))
            {
                lr2SongDbSyncWorkflow.SyncFolderDataAfterSettingsChange("SettingDialog.SaveSettings");
            }
            else if (impact.HasFlag(SettingsPostSaveImpact.ExternalLr2FolderRowsSync))
            {
                lr2SongDbSyncWorkflow.SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChange("SettingDialog.SaveSettings");
            }
            lr2GeneratedDataSyncMs = lr2GeneratedDataSyncStopwatch.ElapsedMilliseconds;
            var beatorajaBmtExportStopwatch = Stopwatch.StartNew();
            if (impact.HasFlag(SettingsPostSaveImpact.BeatorajaBmtExport))
            {
                bool preserveDisabledBmtOutput = !ApplicationSettings.EnableBeatorajaBmtOutput && ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled;
                if (BeatorajaConfigService.IsBeatorajaRootPathValid(tempBeatorajaRootPath)
                    && !string.IsNullOrWhiteSpace(tempBeatorajaBmtTablePath)
                    && !preserveDisabledBmtOutput
                    && (!string.Equals(tempBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                        || !ApplicationSettings.EnableBeatorajaBmtOutput
                        || !ApplicationSettings.RegisterBeatorajaBmtUrls))
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
                workspacePort.QueueBeatorajaBmtExportAll("SettingDialog.SaveSettings", tempBeatorajaBmtTablePath);
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
            customFolderOutputPort.CustomFolderOutputSettings
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
            customFolderOutputPort.CustomFolderOutputSettings
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
        RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
        RaisePropertyChanged(nameof(AvailableBMSDirectories));
        RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        RaiseValidationStateChanged();
    }

    private bool SyncCustomFolderOutputRootAfterSettingsChange(
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        if (!settings.OperationModeLR2DB || lr2config == null || !workspacePort.HasPlaylistTables)
        {
            return false;
        }
        return customFolderOutputPort.SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
            tempLR2CustomFolderAsRootOutputDir,
            settings);
    }

    private int ApplyCustomFolderAdditionalOutputBaseRegistrationChanges()
    {
        return customFolderOutputPort.ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
            tempLR2CustomFolderAdditionalOutputBaseDirs,
            pendingCustomFolderAdditionalOutputBaseRenames,
            customFolderOutputPort.CustomFolderOutputSettings
                ?? throw new InvalidOperationException("Custom-folder output settings provider returned null."));
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
        if (EnableBeatorajaBmtOutput && IsBeatorajaRootPathValid() && string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(ApplicationSettings.BeatorajaRootPath)))
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
            if ((int)LR2bodyResolution.Width <= 0 || (int)LR2bodyResolution.Height <= 0)
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
            || HasPathSettingValueChanged(tempLR2RootPath, ApplicationSettings.LR2RootPath, value => true)
            || HasPathSettingValueChanged(tempLR2SongDBPath, ApplicationSettings.LR2SongDBPath, value => true)
            || HasPathSettingValueChanged(tempLR2ConfigXmlPath, ApplicationSettings.LR2ConfigXmlPath, value => true)
            || tempUseBeatorajaScoreDb != ApplicationSettings.UseBeatorajaScoreDb
            || !string.Equals(tempBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempBeatorajaPlayerId, ApplicationSettings.BeatorajaPlayerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempBeatorajaScoreDbPath, ApplicationSettings.BeatorajaScoreDbPath, StringComparison.OrdinalIgnoreCase)
            || tempEnableBeatorajaBmtOutput != ApplicationSettings.EnableBeatorajaBmtOutput
            || !string.Equals(tempBeatorajaBmtTablePath, ApplicationSettings.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase)
            || HasPathSettingValueChanged(tempBMSRootPath, ApplicationSettings.BMSRootPath, value => true)
            || HasPathSettingValueChanged(tempuBMplayPath, ApplicationSettings.uBMplayPath, value => true)
            || HasPathSettingValueChanged(tempBMIIDXViewPath, ApplicationSettings.BMIIDXViewPath, value => true)
            || tempUsePlayeruBMplay != ApplicationSettings.UsePlayeruBMplay
            || tempUsePlayerLR2body != ApplicationSettings.UsePlayerLR2body
            || tempUsePlayerBMIIDXView != ApplicationSettings.UsePlayerBMIIDXView
            || !string.Equals(tempBMSInstallDir, ApplicationSettings.BMSInstallDir, StringComparison.OrdinalIgnoreCase)
            || !IsSameUri(tempTableListURL, ApplicationSettings.TableListURL)
            || tempEnablePlaylistUrlCompletion != ApplicationSettings.EnablePlaylistUrlCompletion
            || !string.Equals(tempPlaylistMd5UrlMappingTsvUri, ApplicationSettings.PlaylistMd5UrlMappingTsvUri, StringComparison.Ordinal)
            || tempIsLR2BackupEnabled != ApplicationSettings.IsLR2BackupEnabled
            || HasPathSettingValueChanged(tempLR2BackupPath, ApplicationSettings.LR2BackupPath, value => true)
            || tempUseExternalPanelImage != ApplicationSettings.UseExternalPanelImage
            || HasPathSettingValueChanged(tempStagefilePath, ApplicationSettings.StagefilePath, value => true)
            || !string.Equals(tempFolderNameFormat, ApplicationSettings.FolderNameFormat, StringComparison.Ordinal);
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
        if (EnableBeatorajaBmtOutput && IsBeatorajaRootPathValid() && string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(ApplicationSettings.BeatorajaRootPath)))
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
            if ((int)LR2bodyResolution.Width <= 0 || (int)LR2bodyResolution.Height <= 0)
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
    /// 検証済みの設定ダイアログ入力を `ApplicationSettings` メモリ領域から
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
                && (!string.Equals(tempLR2RootPath, ApplicationSettings.LR2RootPath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(tempLR2ConfigXmlPath, ApplicationSettings.LR2ConfigXmlPath, StringComparison.OrdinalIgnoreCase)
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
                ApplicationSettings.OperationModeLR2DB = operationModeLR2DB;
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
                AudioOutputSelection previousOutputSelection = audioSettingsGateway.CaptureOutputSelection();
                bool outputSelectionChanged = savedAudioOutputSelection != audioOutputSelectionDraft;
                if (outputSelectionChanged)
                {
                    audioSettingsGateway.ApplyOutputSelection(audioOutputSelectionDraft);
                }
                try
                {
                    settingsEditSession.Save();
                }
                catch
                {
                    if (outputSelectionChanged)
                    {
                        audioSettingsGateway.ApplyOutputSelection(previousOutputSelection);
                    }
                    throw;
                }
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
        return tempUseBeatorajaScoreDb != ApplicationSettings.UseBeatorajaScoreDb
            || tempEnableBeatorajaBmtOutput != ApplicationSettings.EnableBeatorajaBmtOutput
            || !string.Equals(tempBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempBeatorajaPlayerId, ApplicationSettings.BeatorajaPlayerId, StringComparison.OrdinalIgnoreCase);
    }

    public void SaveOperationModeForRestart(bool operationMode)
    {
        string playHistorySelectedDisplayTargetIdentity = playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity;
        settingsEditSession.Reload();
        ApplicationSettings.OperationModeLR2DB = operationMode;
        playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity = playHistorySelectedDisplayTargetIdentity;
        settingsEditSession.Save();
    }

    public void ResetSettings()
    {
        operationModeLR2DB = tempOperationModeLR2DB;
        ApplicationSettings.LR2ConfigXmlPath = tempLR2ConfigXmlPath;
        ApplicationSettings.OperationModeLR2DB = tempOperationModeLR2DB;
        ApplicationSettings.LR2RootPath = tempLR2RootPath;
        ApplicationSettings.LR2SongDBPath = tempLR2SongDBPath;
        ApplicationSettings.UseBeatorajaScoreDb = tempUseBeatorajaScoreDb;
        ApplicationSettings.BeatorajaRootPath = tempBeatorajaRootPath;
        ApplicationSettings.BeatorajaPlayerId = tempBeatorajaPlayerId;
        ApplicationSettings.BeatorajaScoreDbPath = tempBeatorajaScoreDbPath;
        ApplicationSettings.EnableBeatorajaBmtOutput = tempEnableBeatorajaBmtOutput;
        ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled = tempKeepBeatorajaBmtFilesWhenOutputDisabled;
        ApplicationSettings.BeatorajaBmtHashOutputMode = tempBeatorajaBmtHashOutputMode;
        ApplicationSettings.BeatorajaBmtTablePath = tempBeatorajaBmtTablePath;
        ApplicationSettings.RegisterBeatorajaBmtUrls = tempRegisterBeatorajaBmtUrls;
        ApplicationSettings.BMSRootPath = tempBMSRootPath;
        ApplicationSettings.StandaloneBmsRootPaths = tempStandaloneBmsRootPaths;
        ApplicationSettings.uBMplayPath = tempuBMplayPath;
        ApplicationSettings.BMIIDXViewPath = tempBMIIDXViewPath;
        ApplicationSettings.UsePlayeruBMplay = tempUsePlayeruBMplay;
        ApplicationSettings.UsePlayerLR2body = tempUsePlayerLR2body;
        ApplicationSettings.UsePlayerBMIIDXView = tempUsePlayerBMIIDXView;
        ApplicationSettings.LR2CustomFolderOutputBaseDir = tempLR2CustomFolderOutputDir;
        ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs = tempLR2CustomFolderAdditionalOutputBaseDirs;
        ApplicationSettings.LR2CustomFolderOutputBaseDirRootType = tempLR2CustomFolderAsRootOutputDir;
        ApplicationSettings.PlaylistDefaultIgnoreFolderOutput = tempPlaylistDefaultIgnoreFolderOutput;
        ApplicationSettings.BMSInstallDir = tempBMSInstallDir;
        ApplicationSettings.TableListURL = tempTableListURL;
        ApplicationSettings.EnablePlaylistUrlCompletion = tempEnablePlaylistUrlCompletion;
        ApplicationSettings.OverwritePlaylistUrlsWithCompletion = tempOverwritePlaylistUrlsWithCompletion;
        ApplicationSettings.EnableStellaFullPlaylistUrlCompletion = tempEnableStellaFullPlaylistUrlCompletion;
        ApplicationSettings.PlaylistMd5UrlMappingTsvUri = tempPlaylistMd5UrlMappingTsvUri;
        playHistoryDisplaySettingsStore.DisplayTargetSetsJson = tempPlayHistoryDisplayTargetSetsJson;
        PlayerResolutionSettingsAdapter.SaveToSettings(ApplicationSettings, tempLR2bodyResolution);
        ApplicationSettings.IsSaveLR2bodyWindowPosition = tempIsSaveLR2bodyWindowPosition;
        ApplicationSettings.IsLR2BackupEnabled = tempIsLR2BackupEnabled;
        ApplicationSettings.LR2BackupPath = tempLR2BackupPath;
        ApplicationSettings.LR2BackupTarget = tempLR2BackupTarget;
        ApplicationSettings.LR2BackupSpan = tempLR2BackupSpan;
        ApplicationSettings.LR2BackupNum = tempLR2BackupNum;
        ApplicationSettings.UseExternalWebBrowser = tempUseExternalWebBrowser;
        ApplicationSettings.UseExternalPanelImage = tempUseExternalPanelImage;
        string restoredAppearanceTheme = AppThemeService.NormalizeTheme(tempAppearanceTheme);
        ApplicationSettings.AppearanceTheme = restoredAppearanceTheme;
        AppThemeService.ApplyTheme(restoredAppearanceTheme);
        ApplicationSettings.CustomTableFontSize = tempCustomTableFontSize;
        ApplicationSettings.CustomTableRowHeight = tempCustomTableRowHeight;
        ApplicationSettings.CustomTableHeaderHeight = tempCustomTableHeaderHeight;
        ApplicationSettings.StagefilePath = tempStagefilePath;
        ApplicationSettings.FolderNameFormat = tempFolderNameFormat;
        ApplicationSettings.UseOnlyShiftJISChars = tempUseOnlyShiftJISChars;
        ApplicationSettings.ShowScoreViewerRegisterConfirmMsg = tempShowScoreViewerRegisterConfirmMsg;
        ApplicationSettings.ShowDiffBMSInstallConfirmMsg = tempShowDiffBMSInstallConfirmMsg;
        ApplicationSettings.ShowDuplicateFileCheckConfirmMsg = tempShowDuplicateFileCheckConfirmMsg;
        ApplicationSettings.ShowRecommUpdatedMsg = tempShowRecommUpdatedMsg;
        ApplicationSettings.ScanBmsFilesOnStartup = tempScanBmsFilesOnStartup;
        ApplicationSettings.SkipInitPlaylistLoad = tempSkipInitPlaylistLoad;
        ApplicationSettings.StartupSelectInstallPending = tempStartupSelectInstallPending;
        ApplicationSettings.EnableReadOptimizedPragmas = tempEnableReadOptimizedPragmas;
        ApplicationSettings.EstimateOfflineScoreRanking = tempEstimateOfflineScoreRanking;
        ApplicationSettings.UpdateLr2IrRankingCacheOnStartup = tempUpdateLr2IrRankingCacheOnStartup;
        ApplicationSettings.EnableDownloadLr2IrScoreAndDetectUnsent = tempEnableDownloadLr2IrScoreAndDetectUnsent;
        ApplicationSettings.AutoInstall = tempEnableAutoInstall;
        ApplicationSettings.KeepInstallablePackagesPending = tempKeepInstallablePackagesPending;
        ApplicationSettings.AutoApplyAmbiguousInstallDestination = tempAutoApplyAmbiguousInstallDestination;
        ApplicationSettings.DeletePendingPackageSourceAfterInstall = tempDeletePendingPackageSourceAfterInstall;
        ApplicationSettings.EnableSmartComponentOverwrite = tempEnableSmartComponentOverwrite;
        ApplicationSettings.KeepSmartOverwriteProtectedFilesByRenaming = tempKeepSmartOverwriteProtectedFilesByRenaming;
        ApplicationSettings.ScanBmsFilesOnStartup = tempScanBmsFilesOnStartup;
        ApplicationSettings.EncoderSampleRate = tempEncoderSampleRate;
        ApplicationSettings.Encoder = (EncoderType)tempEncoderIndex;
        ApplicationSettings.EncoderFormat = tempEncoderFormat;
        audioSettingsGateway.EncoderNormalization = tempEncoderNormalization;
        ApplicationSettings.EncoderExeDir = tempEncoderExeDir;
        ApplicationSettings.EncoderAmplifier = tempEncoderAmplifier;
        ApplicationSettings.EncoderQuality = tempEncoderQuality;
        ApplicationSettings.EncodeFileNameFormat = tempEncodeFileNameFormat;
        audioOutputSelectionDraft = savedAudioOutputSelection;
        if (playerDeviceNames != null)
        {
            playerDeviceNames = BuildPlayerDeviceNames(audioOutputSelectionDraft.Backend);
        }
        ApplicationSettings.PlayerSampleRate = tempPlayerSampleRate;
        ApplicationSettings.PlayerFormat = tempPlayerFormat;
        ApplicationSettings.PlayerBufferSize = tempPlayerBufferSize;
        ApplicationSettings.PlayerWASAPIParam = tempPlayerWASAPIParam;
        ApplicationSettings.Lang = tempLanguage;
        ApplicationSettings.LangDisplayName = tempLanguageDisplayName;
        ResourceService.Current.ChangeCulture(tempLanguage);
        if (tempOperationModeLR2DB)
        {
            try
            {
                lr2config = new LR2Config(ApplicationSettings.LR2ConfigXmlPath);
            }
            catch
            {
                ApplicationSettings.LR2ConfigXmlPath = null;
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
        RaisePropertyChanged(nameof(OperationModeLR2DB));
        RaisePropertyChanged(nameof(CanUseLr2Features));
        RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
        RaisePropertyChanged(nameof(LR2RootPath));
        RaisePropertyChanged(nameof(BMSRootPath));
        RaisePropertyChanged(nameof(StandaloneBmsRootPathList));
        RaisePropertyChanged(nameof(SelectedStandaloneBmsRootPath));
        RaisePropertyChanged(nameof(AvailableBMSDirectories));
        RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        RaisePropertyChanged(nameof(IsBmsSearchRootEditorEnabled));
        RaisePropertyChanged(nameof(LR2SongDBPath));
        RaisePropertyChanged(nameof(LR2ConfigXmlPath));
        RaisePropertyChanged(nameof(UseBeatorajaScoreDb));
        RaisePropertyChanged(nameof(BeatorajaRootPath));
        RaisePropertyChanged(nameof(AvailableBeatorajaPlayers));
        RaisePropertyChanged(nameof(BeatorajaPlayerId));
        RaisePropertyChanged(nameof(BeatorajaScoreDbPath));
        RaisePropertyChanged(nameof(EnableBeatorajaBmtOutput));
        RaisePropertyChanged(nameof(KeepBeatorajaBmtFilesWhenOutputDisabled));
        RaisePropertyChanged(nameof(BeatorajaBmtHashOutputMode));
        RaisePropertyChanged(nameof(BeatorajaBmtTablePath));
        RaisePropertyChanged(nameof(RegisterBeatorajaBmtUrls));
        RaisePropertyChanged(nameof(uBMplayPath));
        RaisePropertyChanged(nameof(BMIIDXViewPath));
        RaisePropertyChanged(nameof(UsePlayeruBMplay));
        RaisePropertyChanged(nameof(UsePlayerLR2body));
        RaisePropertyChanged(nameof(UsePlayerBMIIDXView));
        RaisePropertyChanged(nameof(UseInternalPlayer));
        RaisePropertyChanged(nameof(LR2bodyResolution));
        RaisePropertyChanged(nameof(IsSaveLR2bodyWindowPosition));
        RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
        RaisePropertyChanged(nameof(LR2CustomFolderOutputDir));
        RaisePropertyChanged(nameof(BMSInstallDir));
        RaisePropertyChanged(nameof(LR2CustomFolderAsRootOutputDir));
        RaiseDefaultCustomFolderOutputPropertiesChanged();
        RaisePropertyChanged(nameof(TableListURL));
        RaisePropertyChanged(nameof(EnablePlaylistUrlCompletion));
        RaisePropertyChanged(nameof(OverwritePlaylistUrlsWithCompletion));
        RaisePropertyChanged(nameof(EnableStellaFullPlaylistUrlCompletion));
        RaisePropertyChanged(nameof(PlaylistMd5UrlMappingTsvUri));
        RaisePropertyChanged(nameof(PlayHistoryFolderDisplayPresets));
        RaisePropertyChanged(nameof(SelectedPlayHistoryFolderDisplayPreset));
        RaisePropertyChanged(nameof(PlayHistoryFolderDisplayPresetPlaylistOptions));
        RaisePropertyChanged(nameof(IsLR2BackupEnabled));
        RaisePropertyChanged(nameof(LR2BackupPath));
        RaisePropertyChanged(nameof(LR2BackupTarget));
        RaisePropertyChanged(nameof(LR2BackupSpan));
        RaisePropertyChanged(nameof(LR2BackupNum));
        RaisePropertyChanged(nameof(UseExternalWebBrowser));
        RaisePropertyChanged(nameof(UseExternalPanelImage));
        RaisePropertyChanged(nameof(AppearanceTheme));
        RaisePropertyChanged(nameof(CustomTableFontSize));
        RaisePropertyChanged(nameof(CustomTableRowHeight));
        RaisePropertyChanged(nameof(CustomTableHeaderHeight));
        RaisePropertyChanged(nameof(StagefilePath));
        RaisePropertyChanged(nameof(FolderNameFormat));
        RaisePropertyChanged(nameof(UseOnlyShiftJISChars));
        RaisePropertyChanged(nameof(ShowScoreViewerRegisterConfirmMsg));
        RaisePropertyChanged(nameof(ShowDiffBMSInstallConfirmMsg));
        RaisePropertyChanged(nameof(ShowDuplicateFileCheckConfirmMsg));
        RaisePropertyChanged(nameof(ShowRecommUpdatedMsg));
        RaisePropertyChanged(nameof(ScanBmsFilesOnStartup));
        RaisePropertyChanged(nameof(SkipInitPlaylistLoad));
        RaisePropertyChanged(nameof(StartupSelectInstallPending));
        RaisePropertyChanged(nameof(EnableReadOptimizedPragmas));
        RaisePropertyChanged(nameof(EstimateOfflineScoreRanking));
        RaisePropertyChanged(nameof(UpdateLr2IrRankingCacheOnStartup));
        RaisePropertyChanged(nameof(EnableDownloadLr2IrScoreAndDetectUnsent));
        RaisePropertyChanged(nameof(EnableAutoInstall));
        RaisePropertyChanged(nameof(KeepInstallablePackagesPending));
        RaisePropertyChanged(nameof(AutoApplyAmbiguousInstallDestination));
        RaisePropertyChanged(nameof(DeletePendingPackageSourceAfterInstall));
        RaisePropertyChanged(nameof(EnableSmartComponentOverwrite));
        RaisePropertyChanged(nameof(KeepSmartOverwriteProtectedFilesByRenaming));
        RaisePropertyChanged(nameof(EncoderSampleRate));
        RaisePropertyChanged(nameof(EncoderIndex));
        RaisePropertyChanged(nameof(EncoderNormalization));
        RaisePropertyChanged(nameof(EncoderFormat));
        RaisePropertyChanged(nameof(EncoderExeDir));
        RaisePropertyChanged(nameof(EncoderAmplifier));
        RaisePropertyChanged(nameof(EncoderQuality));
        RaisePropertyChanged(nameof(EncodeFileNameFormat));
        RaisePropertyChanged(nameof(PlayerDriverIndex));
        RaisePropertyChanged(nameof(UnavailablePlayerDriverDescription));
        RaisePropertyChanged(nameof(PlayerDevice));
        RaisePropertyChanged(nameof(PlayerDeviceNames));
        RaisePropertyChanged(nameof(SelectedPlayerDevice));
        RaisePropertyChanged(nameof(PlayerSampleRate));
        RaisePropertyChanged(nameof(PlayerFormat));
        RaisePropertyChanged(nameof(PlayerBufferSize));
        RaisePropertyChanged(nameof(PlayerWASAPIParam));
        RaisePropertyChanged(nameof(Languages));
        RaisePropertyChanged(nameof(IsOperationModeChanged));
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
        playHistoryDisplaySettingsStore.DisplayTargetSetsJson = tempPlayHistoryDisplayTargetSetsJson;
        RefreshPlayHistoryFolderDisplayPresetsFromSettings();
        tempPlayHistoryDisplayTargetSetDraftsJson = SerializePlayHistoryFolderDisplayPresetDraftsForChangeTracking();
    }

    public RestartMode IsNeedRestartForSaved()
    {
        RestartMode restartMode = scoreReloadPending ? RestartMode.ScoreOnly : RestartMode.None;
        if (fileDiffReloadPending)
        {
            restartMode |= RestartMode.FolderOnly;
        }
        bool scoreSourceChanged = tempUseBeatorajaScoreDb != ApplicationSettings.UseBeatorajaScoreDb
            || !string.Equals(tempBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempBeatorajaPlayerId, ApplicationSettings.BeatorajaPlayerId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tempBeatorajaScoreDbPath, ApplicationSettings.BeatorajaScoreDbPath, StringComparison.OrdinalIgnoreCase);
        if (tempOperationModeLR2DB != OperationModeLR2DB)
        {
            return RestartMode.All;
        }
        if (OperationModeLR2DB)
        {
            if (tempLR2SongDBPath != ApplicationSettings.LR2SongDBPath)
            {
                return RestartMode.All;
            }
            if (tempLR2ConfigXmlPath != ApplicationSettings.LR2ConfigXmlPath)
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

    private static bool ShowUiConfirmation(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        MessageBoxButton button,
        string routeName = "UI confirmation dialog",
        MessageBoxResult defaultResult = MessageBoxResult.None,
        string warningMessageBoxText = null)
    {
        UiDialogResult result = new UiDialogCoordinator()
            .ConfirmAsync(new UiConfirmationRequest(
                messageBoxText,
                caption,
                button,
                icon,
                defaultResult,
                warningMessageBoxText: warningMessageBoxText))
            .GetAwaiter()
            .GetResult();
        return ToUiConfirmationDecision(result, routeName);
    }

    private static void ShowUiMessage(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        string routeName = "UI message dialog",
        MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        UiDialogResult result = new UiDialogCoordinator()
            .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, MessageBoxButton.OK, icon, defaultResult))
            .GetAwaiter()
            .GetResult();
        ThrowIfUiDialogNotShown(result, routeName);
    }

    private static bool ToUiConfirmationDecision(UiDialogResult result, string routeName)
    {
        return result.Status switch
        {
            UiDialogStatus.Accepted => true,
            UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => false,
            UiDialogStatus.ClosedByUser => result.IsPositive,
            UiDialogStatus.Failed => throw CreateUiDialogDisplayException(routeName, result),
            _ => throw CreateUiDialogDisplayException(routeName, result),
        };
    }

    private static void ThrowIfUiDialogNotShown(UiDialogResult result, string routeName)
    {
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }

        throw CreateUiDialogDisplayException(routeName, result);
    }

    private static SettingsDialogUiDialogDisplayException CreateUiDialogDisplayException(
        string routeName,
        UiDialogResult result)
    {
        string message = result.Status == UiDialogStatus.Failed
            ? routeName + " failed."
            : routeName + " was not shown: " + result.Status;
        return new SettingsDialogUiDialogDisplayException(message, result.Exception);
    }

    private static void LogSettingsPerformance(string action, Stopwatch stopwatch, string detail = null)
    {
        try
        {
            stopwatch?.Stop();
            Ribbit.Logging.NLogWrapper.FileLogger?.Info(
                (action ?? "settings_dialog")
                + " elapsedMs=" + (stopwatch?.ElapsedMilliseconds ?? 0L)
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
        }
        catch
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            workspacePort.PlaylistCatalogChanged -= playlistCatalogChangedHandler;
            statePort.LibraryOperationAvailabilityChanged -= libraryOperationAvailabilityChangedHandler;
            statePort.Lr2PlayHistorySchemaStatusChanged -= lr2PlayHistorySchemaStatusChangedHandler;
        }
    }

    private sealed class SettingsDialogUiDialogDisplayException : InvalidOperationException
    {
        internal SettingsDialogUiDialogDisplayException()
        {
        }

        internal SettingsDialogUiDialogDisplayException(string message)
            : base(message)
        {
        }

        internal SettingsDialogUiDialogDisplayException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

}
