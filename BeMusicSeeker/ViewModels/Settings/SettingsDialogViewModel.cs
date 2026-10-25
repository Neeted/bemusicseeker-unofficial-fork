using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Livet.Commands;
using Livet.EventListeners;
using Ribbit.Logging;
using Ribbit.Media.Audio;
using Ribbit.Util.Extensions;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 通常項目の確定値と、実操作に必要な各セクションの局所候補を設定画面へ接続します。
/// </summary>
public partial class SettingsDialogViewModel : ViewModel
{
    /// <summary>
    /// 既存の型付き初期化と局所操作の結果を接続します。
    /// </summary>
    private readonly ISettingsDialogStatePort statePort;

    private readonly ISettingsDialogWorkspacePort workspacePort;

    private readonly ISettingsDialogCustomFolderOutputPort customFolderOutputPort;

    private readonly Action publishOutputPlacement;

    private readonly ISettingsDialogPlayHistoryPort playHistoryPort;

    private readonly ISettingsDialogSearchRootRuntimePort searchRootRuntimePort;

    private readonly ISettingsDialogPlayerFactoryPort playerFactoryPort;

    private readonly ISettingsDialogPlaybackRuntimePort playbackRuntimePort;

    private readonly Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow;

    private ISettingDialogPresentationPort presentationPort;

    private ViewModelCommand openCommand;

    private ViewModelCommand closeCommand;

    private bool isEditCompletionInProgress;

    private readonly IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore;

    private readonly ISettingsEditSession settingsEditSession;

    private readonly RightClickActionSettingsEditor rightClickActionSettingsEditor;

    private readonly Action<Exception> reportApplyFailure;

    private readonly IUiDialogService schemaDialogs;

    private readonly ILr2PlayHistorySchemaUninstallDialogPort schemaWindowDialogs;

    private readonly ApplicationDataUninstallWorkflowOwner applicationDataUninstallWorkflow;

    private readonly AudioDeviceTestWorkflowOwner audioDeviceTestWorkflow;

    private readonly IAudioDeviceCatalog audioDeviceCatalog;

    private readonly IAudioSettingsGateway audioSettingsGateway;

    private readonly ChartFileOperationSynchronizer operationAdmission;

    private readonly IApplicationLifetimePort applicationLifetime;

    private readonly Func<OperationModeRestartRequest, Task<bool>> requestOperationModeRestart;

    private readonly ICultureCatalog cultureCatalog;

    private readonly IExternalShellGateway externalShellGateway;

    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    /// <summary>
    /// Gets the command used by views to request the settings dialog.
    /// </summary>
    public ViewModelCommand OpenCommand => openCommand ??= new ViewModelCommand(() => RequestOpen());

    /// <summary>
    /// 保存済み値を保ち、残る局所候補を破棄して画面を閉じる要求です。
    /// </summary>
    public ViewModelCommand CloseCommand => closeCommand ??= new ViewModelCommand(ExecuteCloseCommand);

    internal IExternalShellGateway ExternalShellGateway => externalShellGateway;

    /// <summary>
    /// 受理した局所操作の実終端を待っているかを示します。
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
            RaisePropertyChanged(nameof(CanUninstallApplicationData));
            RaisePropertyChanged(nameof(IsEditCancellationEnabled));
            RaisePropertyChanged(nameof(IsAudioOutputSelectionEnabled));
            RaisePropertyChanged(nameof(IsBmsSearchRootEditorEnabled));
            RaisePropertyChanged(nameof(IsScoreProcessingRetryRequired));
            RaisePropertyChanged(nameof(CanRetryScoreSettings));
            RaisePropertyChanged(nameof(IsAudioDeviceTestAvailable));
            RaiseEnvironmentAvailabilityChanged();
        }
    }

    /// <summary>
    /// ライブラリ・出力の実処理を伴う次の局所確定を受け付けられるかを示します。独立同期確定の可否は分けて公開します。
    /// </summary>
    public bool IsEditCompletionEnabled => IsSettingsOperationAvailable && IsSectionCompletionEnabled;

    private bool IsSectionCompletionEnabled => !IsEditCompletionInProgress && !IsAudioDeviceTestInProgress;

    private bool environmentTransitionInProgress;

    /// <summary>音声テストと環境の開始・再起動以外では、実資源を共有しない同期の局所編集・確定を受け付けます。</summary>
    public bool IsIndependentSectionEnabled => !environmentTransitionInProgress && !IsAudioDeviceTestInProgress;

    private void SetEnvironmentTransitionInProgress(bool value)
    {
        environmentTransitionInProgress = value;
        audioDeviceTestWorkflow.SetSaving(value);
        RaisePropertyChanged(nameof(IsIndependentSectionEnabled));
    }

    /// <summary>
    /// 受理済みの通常操作を継続したまま閉鎖できます。初回開始・再起動・復元・撤去は既存の必須終端を待ちます。
    /// </summary>
    public bool IsEditCancellationEnabled => !settingsOperationRequiresPresentation && !environmentTransitionInProgress;

    /// <summary>音声設定担当が所有するIsAudioDeviceTestInProgressを画面へ接続します。</summary>
    public bool IsAudioDeviceTestInProgress => audioDeviceTestWorkflow.IsAudioDeviceTestInProgress;

    /// <summary>音声設定担当が所有するIsAudioDeviceTestAvailableを画面へ接続します。</summary>
    public bool IsAudioDeviceTestAvailable => IsSettingsOperationAvailable && !IsEditCompletionInProgress && audioDeviceTestWorkflow.IsAudioDeviceTestAvailable;

    /// <summary>音声設定担当が所有するAudioDeviceTestButtonContentを画面へ接続します。</summary>
    public string AudioDeviceTestButtonContent => audioDeviceTestWorkflow.AudioDeviceTestButtonContent;

    /// <summary>音声設定担当が所有するIsAudioOutputSelectionEnabledを画面へ接続します。</summary>
    public bool IsAudioOutputSelectionEnabled => audioDeviceTestWorkflow.IsAudioOutputSelectionEnabled;

    /// <summary>音声設定担当が所有するAudioOutputSelectionResetMessageを画面へ接続します。</summary>
    public string AudioOutputSelectionResetMessage => audioDeviceTestWorkflow.AudioOutputSelectionResetMessage;

    /// <summary>音声設定担当が所有するAudioDeviceTestStatusMessageを画面へ接続します。</summary>
    public string AudioDeviceTestStatusMessage => audioDeviceTestWorkflow.AudioDeviceTestStatusMessage;

    /// <summary>音声設定担当が所有するHasAudioDeviceTestDiagnosticsを画面へ接続します。</summary>
    public bool HasAudioDeviceTestDiagnostics => audioDeviceTestWorkflow.HasAudioDeviceTestDiagnostics;

    /// <summary>音声設定担当が所有するAudioDeviceTestDiagnosticMessageを画面へ接続します。</summary>
    public string AudioDeviceTestDiagnosticMessage => audioDeviceTestWorkflow.AudioDeviceTestDiagnosticMessage;

    /// <summary>音声設定担当が所有するAudioDeviceCapabilityStatusMessageを画面へ接続します。</summary>
    public string AudioDeviceCapabilityStatusMessage => audioDeviceTestWorkflow.AudioDeviceCapabilityStatusMessage;

    /// <summary>音声設定担当が所有するHasAudioDeviceCapabilityDiagnosticsを画面へ接続します。</summary>
    public bool HasAudioDeviceCapabilityDiagnostics => audioDeviceTestWorkflow.HasAudioDeviceCapabilityDiagnostics;

    /// <summary>音声設定担当が所有するAudioDeviceCapabilityDiagnosticMessageを画面へ接続します。</summary>
    public string AudioDeviceCapabilityDiagnosticMessage => audioDeviceTestWorkflow.AudioDeviceCapabilityDiagnosticMessage;

    /// <summary>音声設定担当が所有するAudioDeviceCapabilityFormatDescriptionを画面へ接続します。</summary>
    public string AudioDeviceCapabilityFormatDescription => audioDeviceTestWorkflow.AudioDeviceCapabilityFormatDescription;

    /// <summary>音声設定担当が所有するAudioDeviceCapabilityRateDescriptionを画面へ接続します。</summary>
    public string AudioDeviceCapabilityRateDescription => audioDeviceTestWorkflow.AudioDeviceCapabilityRateDescription;

    /// <summary>音声一覧の取得中・取得失敗の状態を画面へ接続します。</summary>
    public string AudioDeviceListStatusMessage => audioDeviceTestWorkflow.AudioDeviceListStatusMessage;

    internal bool IsScoreReloadPending => scoreReloadPending;

    /// <summary>
    /// 設定画面の表示を要求します。通常操作中も同じTaskと制限を再接続し、必須終端・Shell終了中は再表示しません。音声一覧は音声ページの初回表示時に取得します。
    /// </summary>
    /// <param name="deferPresentation">失敗後の再表示など、呼出元をモーダル表示で待たせない場合は true。</param>
    internal void RequestOpen(bool deferPresentation = false)
    {
        if (operationAdmission.IsAdmissionClosed || !IsEditCancellationEnabled) { return; }
        presentationPort?.OpenSettingsDialog(deferPresentation);
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

    private void ExecuteCloseCommand()
    {
        if (!IsEditCancellationEnabled)
        {
            return;
        }

        SetAudioSettingsPageVisible(false);
        InitializeSectionEditors();
        ClosePresentation();
    }

    /// <summary>受理済み設定変更の実処理とcleanupまでL/Pを所有し、専用ディレクトリ失敗は両受付の解放後に通知して再伝播します。</summary>
    /// <param name="lease">この操作が所有する共通受付。</param>
    /// <param name="operation">保存・反映・内部再読込みと後片付けを行う処理。</param>
    /// <param name="playlistCapability">同じ操作の生存P権限。内部継続へ明示的に借用します。</param>
    /// <param name="playlistLease">同じ操作のP。両受付の実終端解放後に専用preflight警告を提示します。</param>
    /// <returns>実処理終端、受付解放と失敗通知までを表すTask。</returns>
    private async Task CompleteAcceptedSettingsOperationAsync(IDisposable lease, Func<LibraryFileMutationCapability, Task> operation, LibraryFileMutationCapability playlistCapability = null, IDisposable playlistLease = null, Func<StartupInitializationResult> completionResult = null)
    {
        try
        {
            using (playlistLease)
            using (lease)
            using (LibraryFileMutationCapability primaryCapability = operationAdmission.CreateMutationCapability(lease))
            using (LibraryFileMutationCapability capability = primaryCapability.WithPlaylistCapability(playlistCapability)) { await operation(capability); }
            StartupInitializationResult result = completionResult?.Invoke();
            if (result != null)
            {
                result = await CompleteRequiredSettingsInitializationAsync(result);
                if (result.Failure != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Failure).Throw(); }
            }
        }
        catch (LibraryDirectoryPreflightException failure)
        {
            await statePort.PresentLibraryDirectoryWarningAsync(failure);
            throw;
        }
    }

    /// <summary>受付外の実必須UIと成功公開まで待ち、その操作で成立したスコア読込みだけ既存pendingから解除します。</summary>
    private async Task<StartupInitializationResult> CompleteRequiredSettingsInitializationAsync(StartupInitializationResult result)
    {
        result = await statePort.CompleteRequiredInitializationAfterAdmissionAsync(result);
        if (result?.CompletionPublished == true && statePort.IsInitializationCompletionCurrent(result.OperationToken))
        {
            if (result.OperationKind is StartupProgressOperationKind.Startup or StartupProgressOperationKind.FullReinitialize or StartupProgressOperationKind.ScoreOnly) { SetScoreReloadPending(false); }
        }
        return result;
    }

    /// <summary>取得済み受付内で検証・保存、実Closeと必要な再初期化の終端まで処理します。</summary>
    private Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability)
    {
        return ReloadScoresOnlyCoreAsync(capability);
    }

    /// <summary>受理済みの共通権限で差分とLR2必須継続の実終端を待ち、失敗を呼出元の解放後通知へ伝播します。</summary>
    internal Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability capability = null)
    {
        return ReloadFileDiffCoreAsync(capability);
    }

    /// <summary>手動同期の実受付時だけ関係環境候補を戻し、閉鎖を通知して実処理の終端を待ちます。</summary>
    internal Task RequestLr2SongDbSyncAsync(Action accepted = null) => RunSettingsOperationAsync(() => RequestLr2SongDbSyncCoreAsync(accepted));

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task RequestLr2SongDbSyncCoreAsync(Action accepted = null)
    {
        if (!CanRequestLr2SongDbSyncCore) { return; }
        await lr2SongDbSyncWorkflow.RequestManualResyncAsync(admitted =>
        {
            if (!admitted) { return; }
            DiscardConflictingCandidates(ConflictingCandidates.Lr2Details | ConflictingCandidates.Lr2Mode);
            accepted?.Invoke();
        });
    }

    private async Task<StartupInitializationResult> ReloadFileDiffCoreAsync(LibraryFileMutationCapability capability = null)
    {
        return await statePort.ReloadFileDiffAsync(capability);
    }

    private async Task<StartupInitializationResult> ReloadScoresOnlyCoreAsync(LibraryFileMutationCapability capability)
    {
        SetScoreReloadPending(true);
        return await statePort.ReloadScoresOnlyAsync(capability);
    }

    private void SetScoreReloadPending(bool value)
    {
        if (scoreReloadPending == value)
        {
            return;
        }

        scoreReloadPending = value;
        RaisePropertyChanged(nameof(IsScoreProcessingRetryRequired));
        RaiseEnvironmentAvailabilityChanged();
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


    private bool scoreReloadPending;



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





    private Lr2PlayHistorySchemaStatusSnapshot lr2PlayHistorySchemaStatusSnapshot;

    private string lr2PlayHistoryScoreDbPath;

    private bool? lr2PlayHistorySchemaCheckOperationMode;






















    private string tableListUriValidationMessage = string.Empty;





    private string playlistMd5UrlMappingTsvUriValidationMessage = string.Empty;



    private PlayHistoryFolderDisplayPresetEditor selectedPlayHistoryFolderDisplayPreset;













    private readonly IReadOnlyList<AppearanceThemeOption> appearanceThemeOptions;

    private readonly IReadOnlyList<BeatorajaBmtHashOutputModeOption> beatorajaBmtHashOutputModeOptions;






















    private static readonly string defaultFolderNameFormat = "[%ARTIST%] %TITLE%";










    private static readonly string defaultEncodeFileNameFormat = "[%ARTIST%] %TITLE%";









    private bool operationModeLR2DB;







    private string selectedStandaloneBmsRootPath;

    private string selectedLR2ConfigBmsDirectory;

    private string selectedCustomFolderAdditionalOutputBaseDir;

    private string selectedCustomFolderAdditionalOutputBaseName;

    private readonly Dictionary<string, string> pendingCustomFolderAdditionalOutputBaseRenames = new(StringComparer.OrdinalIgnoreCase);



    /// <summary>
    /// LR2詳細編集の候補modeを保持します。実radio入力は保存済みLR2組で自動反映します。
    /// </summary>
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
                if (IsEditCompletionInProgress)
                {
                    return;
                }
                SetOperationModeSelection(value);
            }
        }
    }

    /// <summary>実操作の可否は確定済み環境だけから判定します。</summary>
    public bool CanUseLr2Features => ApplicationSettings.OperationModeLR2DB;

    /// <summary>LR2連携が有効で別の設定実操作がない場合に、管理データ撤去を表示上許可します。</summary>
    public bool CanUninstallApplicationData => CanUseLr2Features && IsEditCompletionEnabled;

    /// <summary>Gets whether the standalone library composition is selected.</summary>
    public bool IsStandaloneOperationMode => !OperationModeLR2DB;

    /// <summary>
    /// 設定画面から LR2 の派生データ再同期を要求できる状態かどうかを返します。
    /// </summary>
    public bool CanRequestLr2SongDbSyncDataResync => IsSettingsOperationAvailable && CanRequestLr2SongDbSyncCore;

    private bool CanRequestLr2SongDbSyncCore => statePort.HasActiveLibraryProfile
        && ApplicationSettings.OperationModeLR2DB && !scoreReloadPending
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

    public bool IsOperationModeChanged => ApplicationSettings.OperationModeLR2DB != OperationModeLR2DB;

    /// <summary>Gets the dialog-local right-click action editor.</summary>
    public RightClickActionSettingsEditor RightClickActionSettingsEditor => rightClickActionSettingsEditor;


    /// <summary>LR2 ディレクトリ未設定時に表示する、保存を阻止しない警告です。</summary>
    public string Lr2RootPathValidationMessage => OperationModeLR2DB && string.IsNullOrWhiteSpace(LR2RootPath)
        ? BeMusicSeeker.Properties.Resources.Warning_LR2RootPathNotSet
        : string.Empty;

    /// <summary>LR2 ディレクトリ欄の検証表示種別です。</summary>
    public string Lr2RootPathValidationStatus => string.IsNullOrWhiteSpace(Lr2RootPathValidationMessage) ? string.Empty : "Warning";

    /// <summary>BMS インストール先欄の保存時検証メッセージです。</summary>
    public string BmsInstallDirValidationMessage => IsBMSInstallDirValid()
        ? string.Empty
        : BeMusicSeeker.Properties.Resources.Error_InvalidBmsInstallDir;

    /// <summary>BMS インストール先欄の検証表示種別です。</summary>
    public string BmsInstallDirValidationStatus => string.IsNullOrWhiteSpace(BmsInstallDirValidationMessage) ? string.Empty : "Error";

    /// <summary>通常カスタムフォルダ出力先欄の保存時検証メッセージです。</summary>
    public string CustomFolderOutputDirValidationMessage => GetCustomFolderOutputDirValidationMessage();

    /// <summary>通常カスタムフォルダ出力先欄の検証表示種別です。</summary>
    public string CustomFolderOutputDirValidationStatus => string.IsNullOrWhiteSpace(CustomFolderOutputDirValidationMessage) ? string.Empty : "Error";

    /// <summary>ROOT 形式カスタムフォルダ出力先欄の保存時検証メッセージです。</summary>
    public string CustomFolderRootOutputDirValidationMessage => GetCustomFolderRootOutputDirValidationMessage();

    /// <summary>ROOT 形式カスタムフォルダ出力先欄の検証表示種別です。</summary>
    public string CustomFolderRootOutputDirValidationStatus => string.IsNullOrWhiteSpace(CustomFolderRootOutputDirValidationMessage) ? string.Empty : "Error";

    /// <summary>表一覧URL欄の検証表示種別です。</summary>
    public string TableListUriValidationStatus => string.IsNullOrWhiteSpace(TableListUriValidationMessage) ? string.Empty : "Error";

    /// <summary>プレイリストURL対応表欄の検証表示種別です。</summary>
    public string PlaylistMd5UrlMappingTsvUriValidationStatus => string.IsNullOrWhiteSpace(PlaylistMd5UrlMappingTsvUriValidationMessage) ? string.Empty : "Error";

    private void RaiseValidationStateChanged()
    {
        RaiseEnvironmentAvailabilityChanged();
        RaisePropertyChanged(nameof(CanRetryScoreSettings));
        RaisePropertyChanged(nameof(CanRetryImageSettings));
        RaisePropertyChanged(nameof(CanRetryExternalPlayerSettings));
        RaisePropertyChanged(nameof(Lr2RootPathValidationMessage));
        RaisePropertyChanged(nameof(Lr2RootPathValidationStatus));
        RaisePropertyChanged(nameof(RequiresReplacementBmsInstallDir));
        RaisePropertyChanged(nameof(BmsInstallDirValidationMessage));
        RaisePropertyChanged(nameof(BmsInstallDirValidationStatus));
        RaisePropertyChanged(nameof(CustomFolderOutputDirValidationMessage));
        RaisePropertyChanged(nameof(CustomFolderOutputDirValidationStatus));
        RaisePropertyChanged(nameof(CustomFolderRootOutputDirValidationMessage));
        RaisePropertyChanged(nameof(CustomFolderRootOutputDirValidationStatus));
        RaisePropertyChanged(nameof(TableListUriValidationMessage));
        RaisePropertyChanged(nameof(TableListUriValidationStatus));
        RaisePropertyChanged(nameof(PlaylistMd5UrlMappingTsvUriValidationMessage));
        RaisePropertyChanged(nameof(PlaylistMd5UrlMappingTsvUriValidationStatus));
    }

    private void RightClickActionSettingsEditorPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        RaiseValidationStateChanged();
    }

    /// <summary>受付前に拒否したモード入力の両Radioを、保存済みモードへ再通知します。</summary>
    internal void RestoreOperationModeSelection() => SetOperationModeSelection(ApplicationSettings.OperationModeLR2DB);

    private void SetOperationModeSelection(bool value)
    {
        operationModeLR2DB = value;
        RaisePropertyChanged("OperationModeLR2DB");
        RaisePropertyChanged(nameof(IsStandaloneOperationMode));
        RaisePropertyChanged(nameof(IsOperationModeChanged));
        RaisePropertyChanged(nameof(CanUseLr2Features));
        RaisePropertyChanged(nameof(CanUninstallApplicationData));
        RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
        RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        RaisePropertyChanged(nameof(IsBmsSearchRootEditorEnabled));
        RaisePropertyChanged(nameof(BMSInstallDir));
        RaiseValidationStateChanged();
        RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
        ResetLr2PlayHistorySchemaStatus();
    }

    /// <summary>再生に使う実行ファイルを確定環境から導出します。</summary>
    public string LR2bodyPath => StartupSettingsSnapshot.CreateCurrent(ApplicationSettings).LR2bodyPath;

    public string LR2RootPath
    {
        get
        {
            return environmentLR2RootPath;
        }
        set
        {
            if (TryBuildStandardLr2PathTuple(value, out string songDbPath, out string configPath, out Lr2ConfigOwner parsedConfig))
            {
                // 同じルートの再選択では採用済みXMLを再利用し、未採用の別環境だけを検証します。
                environmentLR2RootPath = CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(value, ApplicationSettings.LR2RootPath)
                    ? ApplicationSettings.LR2RootPath : value;
                environmentLR2SongDBPath = PathsEqual(songDbPath, ApplicationSettings.LR2SongDBPath) ? ApplicationSettings.LR2SongDBPath : songDbPath;
                environmentLR2ConfigXmlPath = PathsEqual(configPath, ApplicationSettings.LR2ConfigXmlPath) ? ApplicationSettings.LR2ConfigXmlPath : configPath;
                isLr2ConfigPathParsed = true;
                SetLr2PathSelectionErrorWithoutNotification(string.Empty);
            }
            else
            {
                SetLr2PathSelectionError(BeMusicSeeker.Properties.Resources.Error_InvalidLR2RootPath);
            }
            RaisePropertyChanged("LR2RootPath");
            RaisePropertyChanged(nameof(LR2SongDBPath));
            RaisePropertyChanged(nameof(LR2ConfigXmlPath));
            RaisePropertyChanged(nameof(LR2bodyPath));
            RaiseLr2PathPresentationChanged();
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
            TryCommitSetting(nameof(LR2bodyResolution), (nameof(Settings.LR2bodyResolution), new System.Windows.Point(value.Width, value.Height)));
        }
    }

    public ObservableCollection<string> BmsRootPathList { get; } = [];

    public ObservableCollection<string> CustomFolderAdditionalOutputBaseDirList { get; } = [];

    /// <summary>
    /// 履歴FOLDER表示プリセットのrecord候補一覧です。選択recordの明示確定で保存します。
    /// </summary>
    public ObservableCollection<PlayHistoryFolderDisplayPresetEditor> PlayHistoryFolderDisplayPresets { get; } = [];

    /// <summary>
    /// 選択中プリセットへ追加できる playlist 候補です。
    /// プレイリスト table source の再読み込みに追従するため、選択中プリセットから都度再構築します。
    /// </summary>

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
            if (!ApplicationSettings.OperationModeLR2DB)
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
            if (!ApplicationSettings.OperationModeLR2DB)
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

    /// <summary>通常の導入先選択に使う最新の確定BMS一覧です。</summary>
    public IReadOnlyList<string> ConfirmedBMSDirectories => GetConfirmedBmsRoots();

    public bool IsBmsSearchRootEditorEnabled => IsSettingsOperationAvailable && IsBmsSearchRootEditorCoreEnabled;

    private bool IsBmsSearchRootEditorCoreEnabled => IsSectionCompletionEnabled && (!ApplicationSettings.OperationModeLR2DB || lr2config != null);

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
    public bool CanInstallOrRepairLr2PlayHistorySchema => IsSettingsOperationAvailable && CanInstallOrRepairLr2PlayHistorySchemaCore;

    private bool CanInstallOrRepairLr2PlayHistorySchemaCore =>
        ApplicationSettings.OperationModeLR2DB
        && !string.IsNullOrWhiteSpace(Lr2PlayHistoryScoreDbPath)
        && (lr2PlayHistorySchemaStatusSnapshot == null || CanInstallLr2PlayHistorySchema || CanRepairLr2PlayHistorySchema);

    /// <summary>
    /// LR2 play history schema の enable/repair 統合ボタンに表示する文言を返します。
    /// </summary>
    public string Lr2PlayHistorySchemaInstallOrRepairButtonText
    {
        get
        {
            var presentation = Lr2PlayHistorySchemaStatusPresentation.Create(lr2PlayHistorySchemaStatusSnapshot);
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
    /// Advanced の Danger zone から LR2 play history schema のアンインストール操作を開始できるかを返します。
    /// destructive な選択は押下後の確認ダイアログへ閉じ込めます。
    /// </summary>
    public bool CanUninstallLr2PlayHistorySchema => IsSettingsOperationAvailable && CanUninstallLr2PlayHistorySchemaCore;

    private bool CanUninstallLr2PlayHistorySchemaCore => ApplicationSettings.OperationModeLR2DB && !string.IsNullOrWhiteSpace(Lr2PlayHistoryScoreDbPath);

    /// <summary>履歴schema変更をL/Pで副作用前に受理し、必要スコア再読込み・通知の実終端まで保持します。</summary>
    internal Task InstallOrRepairLr2PlayHistorySchemaAsync() => RunSettingsOperationAsync(() => InstallOrRepairLr2PlayHistorySchemaCoreAsync());

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task InstallOrRepairLr2PlayHistorySchemaCoreAsync()
    {
        if (!TryBeginBothSettingsAdmissions(out IDisposable admission, out LibraryFileMutationLease playlistLease))
        {
            await ShowUiMessageAsync(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy,
                BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, "Play history schema admission");
            return;
        }
        using LibraryFileMutationLease playlistOperation = playlistLease;
        using LibraryFileMutationCapability playlistCapability = playlistLease.CreateMutationCapability();
        StartupInitializationResult result = null;
        await CompleteAcceptedSettingsOperationAsync(admission,
            async capability => result = await InstallOrRepairLr2PlayHistorySchemaUnderAdmissionAsync(capability), playlistCapability, playlistLease, () => result);
    }

    /// <summary>受理済みschema操作のDB・cache・スコア反映と通知を同じ権限で終えます。</summary>
    private async Task<StartupInitializationResult> InstallOrRepairLr2PlayHistorySchemaUnderAdmissionAsync(LibraryFileMutationCapability capability)
    {
        if (statePort.IsLibraryOperationInProgress)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema install blocked notification");
            return null;
        }

        await RefreshLr2PlayHistorySchemaStatusAsync(force: true);
        if (!CanInstallOrRepairLr2PlayHistorySchemaCore)
        {
            return null;
        }

        string scoreDbPath = Lr2PlayHistoryScoreDbPath;
        bool isLr2LinkedProfile = ApplicationSettings.OperationModeLR2DB;
        UiDialogResult confirmation = await schemaDialogs.ConfirmAsync(new UiConfirmationRequest(
            BuildAuxiliaryOperationConfirmation(BeMusicSeeker.Properties.Resources.Msg_confirm_lr2_play_history_schema_install_or_repair, ConflictingCandidates.Lr2Details | ConflictingCandidates.Lr2Mode)
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
            return null;
        }

        if (isLr2LinkedProfile != ApplicationSettings.OperationModeLR2DB
            || !string.Equals(scoreDbPath, ResolveLr2PlayHistoryScoreDbPath(), StringComparison.OrdinalIgnoreCase)) { return null; }
        DiscardConflictingCandidates(ConflictingCandidates.Lr2Details | ConflictingCandidates.Lr2Mode);
        try
        {
            Lr2PlayHistorySchemaCheckResult result = await Task.Run(() =>
                InstallOrRepairLr2PlayHistorySchemaCore(scoreDbPath, isLr2LinkedProfile));
            if (isLr2LinkedProfile != ApplicationSettings.OperationModeLR2DB
                || !string.Equals(scoreDbPath, Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
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
                    return await ReloadScoresOnlyAsync(capability);
                }
                return null;
            }

            await ShowLr2PlayHistorySchemaMessageAsync(
                result.Message,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema install result notification");
        }
        catch (Exception ex)
        {
            TryLogSettingsLocalFailure(ex, "LR2 play history schema install path=" + scoreDbPath);
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "LR2 play history schema install failure notification");
        }
        return null;
    }

    /// <summary>管理データ撤去の確認をL/Pで受理し、通常終了への要求受理だけを待って受付を解放します。削除の完了は待ちません。</summary>
    internal async Task<ApplicationDataUninstallResult> UninstallApplicationDataAsync(Func<ApplicationDataUninstallWorkflowOwner, string, Task<bool>> requestShutdown)
    {
        ApplicationDataUninstallResult result = ApplicationDataUninstallResult.Blocked;
        await RunSettingsOperationAsync(async () => result = await UninstallApplicationDataCoreAsync(requestShutdown), requiresPresentation: true);
        return result;
    }

    /// <summary>受理済み撤去を通常終了へ渡し、要求元自身の終了完了は待ちません。</summary>
    internal async Task<ApplicationDataUninstallResult> UninstallApplicationDataCoreAsync(Func<ApplicationDataUninstallWorkflowOwner, string, Task<bool>> requestShutdown)
    {
        if (!workspacePort.HasPlaylistTables) { return ApplicationDataUninstallResult.NotStarted; }
        if (!TryBeginBothSettingsAdmissions(out IDisposable admission, out LibraryFileMutationLease playlistLease))
        {
            await ShowUiMessageAsync(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy,
                BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, "Application data uninstall admission");
            return ApplicationDataUninstallResult.Blocked;
        }
        using LibraryFileMutationLease playlistOperation = playlistLease;
        using LibraryFileMutationCapability playlistCapability = playlistLease.CreateMutationCapability();
        ApplicationDataUninstallResult result = ApplicationDataUninstallResult.NotStarted;
        await CompleteAcceptedSettingsOperationAsync(admission, async _ =>
        {
            string songDbPath = ApplicationSettings.LR2SongDBPath;
            result = await applicationDataUninstallWorkflow.ConfirmAsync(new ApplicationDataUninstallRequest(
                workspacePort.HasPlaylistTables, statePort.IsLibraryOperationInProgress, BuildExitOperationConfirmation(BeMusicSeeker.Properties.Resources.Confirm_UninstallApplicationData)));
            if (result.Outcome == ApplicationDataUninstallOutcome.Approved)
            {
                await requestShutdown(applicationDataUninstallWorkflow, songDbPath);
            }
        }, playlistCapability, playlistLease);
        return result;
    }

    /// <summary>履歴schema変更をL/Pで副作用前に受理し、必要スコア再読込み・通知の実終端まで保持します。</summary>
    internal Task UninstallLr2PlayHistorySchemaAsync() => RunSettingsOperationAsync(() => UninstallLr2PlayHistorySchemaCoreAsync());

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task UninstallLr2PlayHistorySchemaCoreAsync()
    {
        if (!TryBeginBothSettingsAdmissions(out IDisposable admission, out LibraryFileMutationLease playlistLease))
        {
            await ShowUiMessageAsync(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy,
                BeMusicSeeker.Properties.Resources.Warning, MessageBoxImage.Exclamation, "Play history schema admission");
            return;
        }
        using LibraryFileMutationLease playlistOperation = playlistLease;
        using LibraryFileMutationCapability playlistCapability = playlistLease.CreateMutationCapability();
        StartupInitializationResult result = null;
        await CompleteAcceptedSettingsOperationAsync(admission,
            async capability => result = await UninstallLr2PlayHistorySchemaUnderAdmissionAsync(capability), playlistCapability, playlistLease, () => result);
    }

    /// <summary>受理済みschema操作のDB・cache・スコア反映と通知を同じ権限で終えます。</summary>
    private async Task<StartupInitializationResult> UninstallLr2PlayHistorySchemaUnderAdmissionAsync(LibraryFileMutationCapability capability)
    {
        if (statePort.IsLibraryOperationInProgress)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema uninstall blocked notification");
            return null;
        }

        await RefreshLr2PlayHistorySchemaStatusAsync(force: true);
        Lr2PlayHistorySchemaStatusSnapshot before = Lr2PlayHistorySchemaStatusSnapshot;
        if (before == null || !CanUninstallLr2PlayHistorySchemaCore)
        {
            return null;
        }
        if (before.Status == Lr2PlayHistorySchemaStatus.NotInstalled)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_lr2_play_history_schema_uninstall_not_installed,
                BeMusicSeeker.Properties.Resources.Information,
                MessageBoxImage.Asterisk,
                "LR2 play history schema uninstall not installed notification");
            return null;
        }
        if (before.Status is Lr2PlayHistorySchemaStatus.SkippedProfile or Lr2PlayHistorySchemaStatus.Unreadable)
        {
            await ShowLr2PlayHistorySchemaMessageAsync(
                before.Message,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema uninstall preflight notification");
            return null;
        }

        string scoreDbPath = Lr2PlayHistoryScoreDbPath;
        bool isLr2LinkedProfile = ApplicationSettings.OperationModeLR2DB;
        if (schemaWindowDialogs == null)
        {
            throw new InvalidOperationException("LR2 play history schema dialog port is not configured.");
        }

        UiInteractionResult<Lr2PlayHistorySchemaUninstallMode> dialogResult = await schemaWindowDialogs.ShowAsync(scoreDbPath, candidateDiscardNotice: BuildCandidateDiscardConfirmation(ConflictingCandidates.Lr2Details | ConflictingCandidates.Lr2Mode));
        ThrowIfWindowDialogNotShown(dialogResult, "LR2 play history schema uninstall dialog");
        if (!dialogResult.IsAccepted)
        {
            return null;
        }

        if (isLr2LinkedProfile != ApplicationSettings.OperationModeLR2DB
            || !string.Equals(scoreDbPath, ResolveLr2PlayHistoryScoreDbPath(), StringComparison.OrdinalIgnoreCase)) { return null; }
        DiscardConflictingCandidates(ConflictingCandidates.Lr2Details | ConflictingCandidates.Lr2Mode);
        Lr2PlayHistorySchemaUninstallMode uninstallMode = dialogResult.Value;
        try
        {
            Lr2PlayHistorySchemaCheckResult result = await Task.Run(() =>
                UninstallLr2PlayHistorySchemaCore(scoreDbPath, isLr2LinkedProfile, uninstallMode));
            if (isLr2LinkedProfile != ApplicationSettings.OperationModeLR2DB
                || !string.Equals(scoreDbPath, Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
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
                    return await ReloadScoresOnlyAsync(capability);
                }
                return null;
            }

            await ShowLr2PlayHistorySchemaMessageAsync(
                result.Message,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "LR2 play history schema uninstall result notification");
        }
        catch (Exception ex)
        {
            TryLogSettingsLocalFailure(ex, "LR2 play history schema uninstall path=" + scoreDbPath);
            await ShowLr2PlayHistorySchemaMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "LR2 play history schema uninstall failure notification");
        }
        return null;
    }

    private async Task RefreshLr2PlayHistorySchemaStatusAsync(bool force)
    {
        string expectedScoreDbPath = Lr2PlayHistoryScoreDbPath;
        bool expectedOperationMode = ApplicationSettings.OperationModeLR2DB;
        if (!force && HasFreshLr2PlayHistorySchemaCheckResult(expectedScoreDbPath, expectedOperationMode))
        {
            return;
        }

        Lr2PlayHistorySchemaCheckResult result = await Task.Run(() =>
            CheckLr2PlayHistorySchemaCore(expectedScoreDbPath, expectedOperationMode));
        if (expectedOperationMode == ApplicationSettings.OperationModeLR2DB
            && string.Equals(expectedScoreDbPath, Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
        {
            ApplyLr2PlayHistorySchemaCheckResult(result);
        }
    }

    private Task ShowLr2PlayHistorySchemaMessageAsync(
        string message,
        string caption,
        MessageBoxImage icon,
        string routeName)
        => ShowUiMessageAsync(message, caption, icon, routeName);

    private void ResetLr2PlayHistorySchemaStatus()
    {
        string nextScoreDbPath = ResolveLr2PlayHistoryScoreDbPath();
        bool nextOperationMode = ApplicationSettings.OperationModeLR2DB;
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

        if (!ApplicationSettings.OperationModeLR2DB
            || !string.Equals(snapshot.ScoreDbPath, Lr2PlayHistoryScoreDbPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lr2PlayHistoryScoreDbPath = snapshot.ScoreDbPath;
        lr2PlayHistorySchemaStatusSnapshot = snapshot;
        lr2PlayHistorySchemaCheckOperationMode = ApplicationSettings.OperationModeLR2DB;
        RaiseLr2PlayHistorySchemaStatusChanged();
    }

    private void ApplyLr2PlayHistorySchemaCheckResult(Lr2PlayHistorySchemaCheckResult result)
    {
        var snapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(result);
        lr2PlayHistoryScoreDbPath = snapshot.IsReset
            ? ResolveLr2PlayHistoryScoreDbPath()
            : snapshot.ScoreDbPath;
        lr2PlayHistorySchemaStatusSnapshot = snapshot.IsReset ? null : snapshot;
        lr2PlayHistorySchemaCheckOperationMode = ApplicationSettings.OperationModeLR2DB;
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
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return null;
        }
        return Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(ApplicationSettings.LR2RootPath, () => GetConfirmedLr2Owner()?.Capture().PlayerId);
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

    /// <summary>
    /// LR2 楽曲DBの編集パスです。無効な候補では現在のパスを保持し、
    /// 同じパスの再選択でもファイルの検出状態を通知します。
    /// </summary>
    public string LR2SongDBPath
    {
        get
        {
            return environmentLR2SongDBPath;
        }
        private set
        {
            string previousPath = environmentLR2SongDBPath;
            if (IsLR2SongDBPathValid(value))
            {
                if (!string.Equals(previousPath, value, StringComparison.Ordinal))
                {
                    environmentLR2SongDBPath = value;
                }
                SetLr2PathSelectionError(string.Empty);
            }
            else
            {
                SetLr2PathSelectionError(BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath);
            }
            RaisePropertyChanged("LR2SongDBPath");
            RaiseLr2PathPresentationChanged();
            RaiseValidationStateChanged();
        }
    }

    /// <summary>
    /// 保存されたLR2設定XMLのパスです。選択候補は読込みと必要構造を検証し、
    /// 無効な候補では元のパスと解析結果を保持したまま入力エラーを通知します。
    /// 採用済みの同じ正規化済みパスは所有者を再利用し、未採用の別パスだけを入力として検証します。
    /// </summary>
    public string LR2ConfigXmlPath
    {
        get
        {
            return environmentLR2ConfigXmlPath;
        }
        private set
        {
            string previousPath = environmentLR2ConfigXmlPath;
            bool previousParsed = isLr2ConfigPathParsed;
            Lr2ConfigOwner previousConfig = lr2config;
            string previousError = Lr2PathSelectionError;
            if (lr2ConfigSession.TryResolve(value, out Lr2ConfigOwner parsedConfig, adoptInitial: false))
            {
                if (!PathsEqual(previousPath, value))
                {
                    environmentLR2ConfigXmlPath = value;
                }
                isLr2ConfigPathParsed = true;
                SetLr2PathSelectionError(string.Empty);
            }
            else
            {
                // A rejected picker candidate is not the draft. Keep the previous raw persisted
                // value and its parsed object intact while exposing an explicit validation failure.
                SetLr2PathSelectionError(BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath);
            }
            if (string.Equals(previousPath, environmentLR2ConfigXmlPath, StringComparison.Ordinal)
                && previousParsed == isLr2ConfigPathParsed
                && ReferenceEquals(previousConfig, lr2config)
                && string.Equals(previousError, Lr2PathSelectionError, StringComparison.Ordinal))
            {
                return;
            }
            RaisePropertyChanged("LR2ConfigXmlPath");
            RaiseLr2PathPresentationChanged();
            RaisePropertyChanged(nameof(LR2bodyPath));
            RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
            RaisePropertyChanged(nameof(BMSInstallDir));
            RaiseValidationStateChanged();
            ResetLr2PlayHistorySchemaStatus();
        }
    }

    /// <summary>Gets the icon paired with the LR2 song database status.</summary>
    public string Lr2SongDbPathStatusIcon => File.Exists(LR2SongDBPath) ? "✓" : "!";
    /// <summary>Gets the semantic LR2 song database status category.</summary>
    public string Lr2SongDbPathStatusKind => File.Exists(LR2SongDBPath) ? "Success" : "Warning";
    /// <summary>Gets localized LR2 song database status text.</summary>
    public string Lr2SongDbPathStatusText => File.Exists(LR2SongDBPath)
        ? GetLr2DetectedPathStatusText(IsStandardLr2SongDbPath())
        : BeMusicSeeker.Properties.Resources.Settings_path_missing;
    /// <summary>Gets the icon paired with the LR2 configuration status.</summary>
    public string Lr2ConfigPathStatusIcon => isLr2ConfigPathParsed ? "✓" : "!";
    /// <summary>Gets the semantic LR2 configuration status category.</summary>
    public string Lr2ConfigPathStatusKind => isLr2ConfigPathParsed ? "Success" : File.Exists(LR2ConfigXmlPath) ? "Error" : "Warning";
    /// <summary>Gets localized LR2 configuration status text.</summary>
    public string Lr2ConfigPathStatusText => isLr2ConfigPathParsed
        ? GetLr2DetectedPathStatusText(IsStandardLr2ConfigPath())
        : File.Exists(LR2ConfigXmlPath)
            ? BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath
            : BeMusicSeeker.Properties.Resources.Settings_path_missing;

    private string GetLr2DetectedPathStatusText(bool isStandardPath) => isStandardPath
        ? BeMusicSeeker.Properties.Resources.Settings_path_detected_from_lr2_root
        : BeMusicSeeker.Properties.Resources.Settings_path_detected_from_individual_setting;

    private bool IsStandardLr2SongDbPath()
    {
        return !string.IsNullOrWhiteSpace(LR2RootPath)
            && PathsEqual(LR2SongDBPath, Path.Combine(LR2RootPath, "LR2files", "Database", "song.db"));
    }

    private bool IsStandardLr2ConfigPath()
    {
        if (string.IsNullOrWhiteSpace(LR2RootPath))
        {
            return false;
        }

        string standardConfigDirectory = Path.Combine(LR2RootPath, "LR2files", "Config");
        return PathsEqual(LR2ConfigXmlPath, Path.Combine(standardConfigDirectory, "config.xml"))
            || PathsEqual(LR2ConfigXmlPath, Path.Combine(standardConfigDirectory, "config.xmh"));
    }

    /// <summary>Gets an explicit failure raised by the most recent rejected LR2 path selection.</summary>
    public string Lr2PathSelectionError => lr2PathSelectionError;

    /// <summary>Gets whether the most recent LR2 path selection was rejected.</summary>
    public bool HasLr2PathSelectionError => !string.IsNullOrWhiteSpace(lr2PathSelectionError);

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RaiseLr2PathPresentationChanged()
    {
        RaisePropertyChanged(nameof(Lr2SongDbPathStatusIcon));
        RaisePropertyChanged(nameof(Lr2SongDbPathStatusKind));
        RaisePropertyChanged(nameof(Lr2SongDbPathStatusText));
        RaisePropertyChanged(nameof(Lr2ConfigPathStatusIcon));
        RaisePropertyChanged(nameof(Lr2ConfigPathStatusKind));
        RaisePropertyChanged(nameof(Lr2ConfigPathStatusText));
        RaisePropertyChanged(nameof(Lr2PathSelectionError));
        RaisePropertyChanged(nameof(HasLr2PathSelectionError));
    }

    private bool TryBuildStandardLr2PathTuple(string rootPath, out string songDbPath, out string configPath, out Lr2ConfigOwner parsedConfig)
    {
        songDbPath = string.Empty;
        configPath = string.Empty;
        parsedConfig = null;
        if (!IsLR2PlayerRootPathValid(rootPath))
        {
            return false;
        }

        songDbPath = Path.Combine(rootPath, "LR2files", "Database", "song.db");
        string configXmlPath = Path.Combine(rootPath, "LR2files", "Config", "config.xml");
        string configXmhPath = Path.Combine(rootPath, "LR2files", "Config", "config.xmh");
        configPath = CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(rootPath, ApplicationSettings.LR2RootPath) ? GetConfirmedLr2Owner()?.ConfigFilePath : null;
        if (configPath != null) { return lr2ConfigSession.TryResolve(configPath, out parsedConfig, adoptInitial: false); }
        configPath = File.Exists(configXmlPath) && File.Exists(configXmhPath)
            ? File.GetLastWriteTime(configXmhPath) >= File.GetLastWriteTime(configXmlPath) ? configXmhPath : configXmlPath
            : File.Exists(configXmlPath) ? configXmlPath : configXmhPath;
        return lr2ConfigSession.TryResolve(configPath, out parsedConfig, adoptInitial: false);
    }

    /// <summary>確定rootに対するスコア使用候補です。有効な実入力だけがスコア項目の自動確定を要求します。</summary>
    public bool UseBeatorajaScoreDb
    {
        get
        {
            return scoreUseBeatorajaScoreDb;
        }
        set
        {
            if (scoreReloadPending) { return; }
            if (scoreUseBeatorajaScoreDb != value)
            {
                scoreUseBeatorajaScoreDb = value;
                RefreshBeatorajaDerivedSettings();
                RaisePropertyChanged("UseBeatorajaScoreDb");
                RaiseValidationStateChanged();
                OnScoreSelectionChanged();
            }
        }
    }

    /// <summary>独立したroot候補です。同値入力は表記を保ち、選択しても確定rootを使うスコア候補を変更しません。</summary>
    public string BeatorajaRootPath
    {
        get
        {
            return environmentBeatorajaRootPath;
        }
        set
        {
            string path = value ?? string.Empty;
            if (scoreReloadPending || pendingBmtChange != null) { return; }
            if (string.Equals(environmentBeatorajaRootPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            environmentBeatorajaRootPath = path;
            RefreshEnvironmentBeatorajaCandidate();
            RaisePropertyChanged("BeatorajaRootPath");
            RaiseValidationStateChanged();
        }
    }

    /// <summary>確定rootのplayer一覧です。</summary>
    public List<string> AvailableBeatorajaPlayers
    {
        get
        {
            List<string> players = BeatorajaConfigService.GetPlayerIds(ApplicationSettings.BeatorajaRootPath);
            if (!string.IsNullOrWhiteSpace(scoreBeatorajaPlayerId) && !players.Contains(scoreBeatorajaPlayerId, StringComparer.OrdinalIgnoreCase))
            { players.Add(scoreBeatorajaPlayerId); }
            return players;
        }
    }

    /// <summary>確定rootで使うplayer候補です。一覧再構成の一時nullでは破棄しません。</summary>
    public string BeatorajaPlayerId
    {
        get
        {
            return scoreBeatorajaPlayerId;
        }
        set
        {
            if (value == null || scoreReloadPending) { return; }
            string playerId = value;
            if (scoreBeatorajaPlayerId == playerId)
            {
                return;
            }
            scoreBeatorajaPlayerId = playerId;
            RefreshBeatorajaDerivedSettings();
            RaisePropertyChanged(nameof(AvailableBeatorajaPlayers));
            RaisePropertyChanged("BeatorajaPlayerId");
            RaisePropertyChanged(nameof(BeatorajaScoreDbPath));
            RaiseEnvironmentBeatorajaCandidateChanged();
            RaiseValidationStateChanged();
            OnScoreSelectionChanged();
        }
    }

    /// <summary>確定rootとplayer候補から解決したscore.dbを表示します。値は派生情報として扱い、直接入力で変更しません。</summary>
    public string BeatorajaScoreDbPath
    {
        get
        {
            return scoreBeatorajaScoreDbPath;
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
            return bmtEnableBeatorajaBmtOutput;
        }
        set
        {
            if (bmtEnableBeatorajaBmtOutput != value)
            {
                bmtEnableBeatorajaBmtOutput = value;
                RaisePropertyChanged("EnableBeatorajaBmtOutput");
                RaiseValidationStateChanged();
            }
        }
    }

    public bool KeepBeatorajaBmtFilesWhenOutputDisabled
    {
        get
        {
            return bmtKeepBeatorajaBmtFilesWhenOutputDisabled;
        }
        set
        {
            if (bmtKeepBeatorajaBmtFilesWhenOutputDisabled != value)
            {
                bmtKeepBeatorajaBmtFilesWhenOutputDisabled = value;
                RaisePropertyChanged("KeepBeatorajaBmtFilesWhenOutputDisabled");
                RaiseValidationStateChanged();
            }
        }
    }

    /// <summary>root選択又はBMT確定で捕捉したBMT出力先を表示します。保存後の修復中は捕捉した対象を維持します。</summary>
    public string BeatorajaBmtTablePath
    {
        get
        {
            return beatorajaBmtTablePath;
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
            return bmtRegisterBeatorajaBmtUrls;
        }
        set
        {
            if (bmtRegisterBeatorajaBmtUrls != value)
            {
                bmtRegisterBeatorajaBmtUrls = value;
                RaisePropertyChanged("RegisterBeatorajaBmtUrls");
                RaiseValidationStateChanged();
            }
        }
    }

    private Lr2ConfigOwner lr2ConfigValue;

    private readonly Lr2ConfigSession lr2ConfigSession;

    private bool isLr2ConfigPathParsed;

    private string lr2PathSelectionError = string.Empty;

    private Lr2ConfigOwner lr2config
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
                RaiseBmsListPropertyChanged(nameof(LR2ConfigBMSDirectories));
                RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
                RaisePropertyChanged(nameof(IsBmsSearchRootEditorEnabled));
                RaisePropertyChanged(nameof(LR2CustomFolderOutputDir));
                RaisePropertyChanged(nameof(LR2CustomFolderAsRootOutputDir));
                RaisePropertyChanged(nameof(BMSInstallDir));
            }
        }
    }

    private Lr2ConfigOwner GetConfirmedLr2Owner()
    {
        if (!lr2ConfigSession.TryResolve(ApplicationSettings.LR2ConfigXmlPath, out Lr2ConfigOwner owner)) { return null; }
        owner.SetPlacement(CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings));
        return owner;
    }

    private void RefreshParsedLr2ConfigState(bool showSelectionError, bool? useLr2Mode = null)
    {
        isLr2ConfigPathParsed = lr2ConfigSession.TryResolve(environmentLR2ConfigXmlPath, out Lr2ConfigOwner parsedConfig);
        lr2config = isLr2ConfigPathParsed && (useLr2Mode ?? OperationModeLR2DB) ? parsedConfig : null;
        SetLr2PathSelectionError(showSelectionError && !string.IsNullOrWhiteSpace(environmentLR2ConfigXmlPath) && !isLr2ConfigPathParsed
            ? BeMusicSeeker.Properties.Resources.Error_InvalidLR2SongDbOrConfigPath
            : string.Empty);
    }

    private void SetLr2PathSelectionError(string value)
    {
        value ??= string.Empty;
        if (string.Equals(lr2PathSelectionError, value, StringComparison.Ordinal))
        {
            return;
        }
        SetLr2PathSelectionErrorWithoutNotification(value);
        RaisePropertyChanged(nameof(Lr2PathSelectionError));
        RaisePropertyChanged(nameof(HasLr2PathSelectionError));
    }

    private void SetLr2PathSelectionErrorWithoutNotification(string value)
    {
        lr2PathSelectionError = value ?? string.Empty;
    }

    public string uBMplayPath
    {
        get => externalPlayeruBMplayPath;
        set
        {
            if (externalPlayeruBMplayPath == value) { return; }
            if (IsuBMplayPathValid(value))
            {
                externalPlayeruBMplayPath = value;
                CommitExternalPlayerSettings();
            }
            RaisePropertyChanged(nameof(uBMplayPath));
            RaiseValidationStateChanged();
        }
    }

    public string BMIIDXViewPath
    {
        get => externalPlayerBMIIDXViewPath;
        set
        {
            if (externalPlayerBMIIDXViewPath == value) { return; }
            if (IsBMIIDXViewPathValid(value))
            {
                externalPlayerBMIIDXViewPath = value;
                CommitExternalPlayerSettings();
            }
            RaisePropertyChanged(nameof(BMIIDXViewPath));
            RaiseValidationStateChanged();
        }
    }

    public bool UsePlayeruBMplay
    {
        get
        {
            return externalPlayerUsePlayeruBMplay;
        }
        set
        {
            if (value == externalPlayerUsePlayeruBMplay) { return; }
            if (value)
            {
                SetPlayerSelection(usePlayeruBMplay: true, usePlayerLR2body: false, usePlayerBMIIDXView: false);
                CommitExternalPlayerSettings();
            }
            else if (externalPlayerUsePlayeruBMplay)
            {
                SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: externalPlayerUsePlayerLR2body, usePlayerBMIIDXView: externalPlayerUsePlayerBMIIDXView);
            }
        }
    }

    public bool UsePlayerLR2body
    {
        get
        {
            return externalPlayerUsePlayerLR2body;
        }
        set
        {
            if (value == externalPlayerUsePlayerLR2body) { return; }
            if (value)
            {
                SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: true, usePlayerBMIIDXView: false);
                CommitExternalPlayerSettings();
            }
            else if (externalPlayerUsePlayerLR2body)
            {
                SetPlayerSelection(usePlayeruBMplay: externalPlayerUsePlayeruBMplay, usePlayerLR2body: false, usePlayerBMIIDXView: externalPlayerUsePlayerBMIIDXView);
            }
        }
    }

    public bool UsePlayerBMIIDXView
    {
        get
        {
            return externalPlayerUsePlayerBMIIDXView;
        }
        set
        {
            if (value == externalPlayerUsePlayerBMIIDXView) { return; }
            if (value)
            {
                SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: false, usePlayerBMIIDXView: true);
                CommitExternalPlayerSettings();
            }
            else if (externalPlayerUsePlayerBMIIDXView)
            {
                SetPlayerSelection(usePlayeruBMplay: externalPlayerUsePlayeruBMplay, usePlayerLR2body: externalPlayerUsePlayerLR2body, usePlayerBMIIDXView: false);
            }
        }
    }

    public bool UseInternalPlayer
    {
        get
        {
            return !externalPlayerUsePlayeruBMplay
                && !externalPlayerUsePlayerLR2body
                && !externalPlayerUsePlayerBMIIDXView;
        }
        set
        {
            if (value && !UseInternalPlayer)
            {
                SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: false, usePlayerBMIIDXView: false);
                CommitExternalPlayerSettings();
            }
        }
    }

    private void SetPlayerSelection(bool usePlayeruBMplay, bool usePlayerLR2body, bool usePlayerBMIIDXView)
    {
        if (externalPlayerUsePlayeruBMplay == usePlayeruBMplay
            && externalPlayerUsePlayerLR2body == usePlayerLR2body
            && externalPlayerUsePlayerBMIIDXView == usePlayerBMIIDXView)
        {
            return;
        }
        externalPlayerUsePlayeruBMplay = usePlayeruBMplay;
        externalPlayerUsePlayerLR2body = usePlayerLR2body;
        externalPlayerUsePlayerBMIIDXView = usePlayerBMIIDXView;
        RaisePropertyChanged(nameof(UseInternalPlayer));
        RaisePropertyChanged(nameof(UsePlayeruBMplay));
        RaisePropertyChanged(nameof(UsePlayerLR2body));
        RaisePropertyChanged(nameof(UsePlayerBMIIDXView));
        RaiseValidationStateChanged();
        RaisePropertyChanged(nameof(CanRetryExternalPlayerSettings));
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
                if (!TryCommitSetting(nameof(IsSaveLR2bodyWindowPosition), (nameof(Settings.IsSaveLR2bodyWindowPosition), value))) { return; }
                RaisePropertyChanged("IsSaveLR2bodyWindowPosition");
            }
        }
    }

    /// <summary>両モードで共有する、管理登録を含めないユーザー編集候補です。</summary>
    public List<string> LR2ConfigBMSDirectories => [.. BmsRootPathList];

    internal bool IsManagedCustomFolderSearchRootPath(string path)
    {
        return IsUserVisibleBmsSearchRootExcludedPath(path);
    }

    private bool IsUserVisibleBmsSearchRootExcludedPath(string path)
    {
        return ContainsSameOrChildDirectory(GetUserVisibleBmsSearchRootExcludedDirectories(), path);
    }

    private IReadOnlyList<string> GetUserVisibleBmsSearchRootExcludedDirectories()
    {
        var paths = new List<string> { ApplicationSettings.LR2CustomFolderOutputBaseDir, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType };
        paths.AddRange(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs));
        if (!string.IsNullOrWhiteSpace(ApplicationSettings.LR2CustomFolderOutputBaseDirRootType) && workspacePort.HasPlaylistTables)
        {
            paths.AddRange(CreateRootFolderOutputDirectories(ApplicationSettings.LR2CustomFolderOutputBaseDirRootType,
                workspacePort.CapturePlaylistPresentationSnapshots()));
        }
        return CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(paths);
    }

    private IEnumerable<string> CreateRootFolderOutputDirectories(string rootOutputBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootOutputBaseDirectory) || !workspacePort.HasPlaylistTables)
        {
            return [];
        }
        return CreateRootFolderOutputDirectories(rootOutputBaseDirectory, workspacePort.CapturePlaylistPresentationSnapshots());
    }

    private static IEnumerable<string> CreateRootFolderOutputDirectories(
        string rootOutputBaseDirectory,
        IReadOnlyList<PlaylistTablePresentationSnapshot> playlists)
    {
        if (string.IsNullOrWhiteSpace(rootOutputBaseDirectory))
        {
            return [];
        }
        return [.. playlists
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

    /// <summary>未確定のユーザーBMS検索先一覧です。保存済みXMLや公開入力とは独立しています。</summary>
    public List<string> AvailableBMSDirectories => [.. BmsRootPathList];

    /// <summary>通常出力の候補です。有効な確定導入先を取り込む選択は拒否し、受理済み配置へ戻します。</summary>
    public string LR2CustomFolderOutputDir
    {
        get
        {
            return outputLR2CustomFolderOutputBaseDir;
        }
        set
        {
            if (value != null && !(outputLR2CustomFolderOutputBaseDir == value))
            {
                if (ValidateCustomFolderOutputBaseDir(value, out string errMsg, target: CopyOutputPlacement(CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings), normal: value)))
                {
                    outputLR2CustomFolderOutputBaseDir = value;
                }
                else
                {
                    RestoreUnacceptedOutputCandidates();
                    ShowSettingValidationError(errMsg);
                }
                RaisePropertyChanged("LR2CustomFolderOutputDir");
                RaiseBmsListPropertyChanged(nameof(LR2ConfigBMSDirectories));
                RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
                RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
                RaiseValidationStateChanged();
            }
        }
    }

    /// <summary>独立導入先として保存済みの値です。選択肢は確定BMS一覧だけから表示します。</summary>
    public string BMSInstallDir => ApplicationSettings.BMSInstallDir;

    /// <summary>確認不要の独立導入先選択は実操作中も有効に保ち、BMS候補の破棄確認が必要な選択だけ設定の単一受付に従って無効化します。</summary>
    public bool IsBmsInstallDirectorySelectionEnabled => IsSettingsOperationAvailable || !RequiresBmsInstallDirectoryConfirmation;

    /// <summary>独立導入先の今回入力だけを検証・確認し、採用時に競合BMS候補を破棄して保存します。BMS一覧は保存しません。</summary>
    /// <param name="value">確定BMS一覧から今回選んだ導入先。</param>
    /// <param name="allowCandidateConfirmation">Windowが破棄確認を所有できる場合だけtrue。確認不要の項目保存はこの可否に依存しません。</param>
    internal Task SelectBmsInstallDirectoryAsync(string value, bool allowCandidateConfirmation = true)
    {
        if (!RequiresBmsInstallDirectoryConfirmation) { return SelectBmsInstallDirectoryCoreAsync(value, allowCandidateConfirmation: false); }
        if (!allowCandidateConfirmation || !IsSettingsOperationAvailable)
        { RaisePropertyChanged(nameof(BMSInstallDir)); return Task.CompletedTask; }
        return RunSettingsOperationAsync(() => SelectBmsInstallDirectoryCoreAsync(value));
    }

    /// <summary>確認不要の同期保存、又は受理済みの候補確認を実行します。</summary>
    internal async Task SelectBmsInstallDirectoryCoreAsync(string value, bool allowCandidateConfirmation = true)
    {
        try
        {
            if (value == null || string.Equals(ApplicationSettings.BMSInstallDir, value, StringComparison.OrdinalIgnoreCase) || !IsBMSInstallDirValid(value)) { return; }
            if (pendingOutputChange != null) { ShowSettingValidationError(Resources.Settings_repair_related_save); return; }
            if (RequiresBmsInstallDirectoryConfirmation && !allowCandidateConfirmation) { return; }
            if (!await ConfirmCandidateSwitchAsync(ConflictingCandidates.Bms) || pendingOutputChange != null || !IsBMSInstallDirValid(value)) { return; }
            if (string.Equals(ApplicationSettings.BMSInstallDir, value, StringComparison.OrdinalIgnoreCase)) { return; }
            DiscardConflictingCandidates(ConflictingCandidates.Bms);
            TryCommitSetting(nameof(BMSInstallDir), (nameof(Settings.BMSInstallDir), value));
        }
        finally { RaisePropertyChanged(nameof(BMSInstallDir)); RaiseValidationStateChanged(); }
    }

    /// <summary>個別LR2パスの今回入力を検証し、非同期の候補確認と採用時再検査後に詳細組へ採用します。</summary>
    internal Task SelectLr2DetailPathAsync(string propertyName, string value) => RunSettingsOperationAsync(() => SelectLr2DetailPathCoreAsync(propertyName, value));

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task SelectLr2DetailPathCoreAsync(string propertyName, string value)
    {
        bool IsValid() => propertyName switch
        {
            nameof(LR2SongDBPath) => IsLR2SongDBPathValid(value),
            nameof(LR2ConfigXmlPath) => lr2ConfigSession.TryResolve(value, out _, adoptInitial: false),
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName))
        };
        string previous = propertyName == nameof(LR2SongDBPath) ? environmentLR2SongDBPath : environmentLR2ConfigXmlPath;
        void ApplyInput() { if (propertyName == nameof(LR2SongDBPath)) { LR2SongDBPath = value; } else { LR2ConfigXmlPath = value; } }
        if (!IsValid() || PathsEqual(previous, value)) { ApplyInput(); return; }
        if (!await PrepareLr2CandidateEditAsync() || !IsValid()) { return; }
        DiscardConflictingCandidates(ConflictingCandidates.Bms);
        ApplyInput();
    }

    /// <summary>ROOT出力の候補です。有効な確定導入先を含む親も拒否し、受理済み配置へ戻します。</summary>
    public string LR2CustomFolderAsRootOutputDir
    {
        get
        {
            return outputLR2CustomFolderOutputBaseDirRootType;
        }
        set
        {
            if (value != null && !(outputLR2CustomFolderOutputBaseDirRootType == value))
            {
                if (ValidateCustomFolderAsRootOutputBaseDir(value, out string errMsg, target: CopyOutputPlacement(CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings), root: value)))
                {
                    outputLR2CustomFolderOutputBaseDirRootType = value;
                }
                else
                {
                    RestoreUnacceptedOutputCandidates();
                    ShowSettingValidationError(errMsg);
                }
                RaisePropertyChanged("LR2CustomFolderAsRootOutputDir");
                RaiseBmsListPropertyChanged(nameof(LR2ConfigBMSDirectories));
                RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
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
            if (!TryCommitSetting(propertyName, (nameof(Settings.PlaylistDefaultIgnoreFolderOutput), nextValue))) { return; }
            RaisePropertyChanged(propertyName);
        }
    }

    private void ShowSettingValidationError(string errMsg)
    {
        if (string.IsNullOrWhiteSpace(errMsg))
        {
            return;
        }
        ShowUiMessage(errMsg, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
    }

    private async Task<bool> ConfirmCustomFolderOutputChangesBeforeSaveAsync(CustomFolderOutputSettingsSnapshot target = null)
    {
        IReadOnlyList<string> warningSearchRoots = GetCustomFolderOutputWarningSearchRoots();
        IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> conflicts =
            CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(CreateCurrentCustomFolderOutputBaseCandidates(target), warningSearchRoots,
                GetPreviousNonNormalManagedCustomFolderSearchRootDirectories(), ApplicationSettings.LR2CustomFolderOutputBaseDir);
        var removedSearchRoots = conflicts
            .Where(conflict => !conflict.IsNormalOutput)
            .Select(conflict => conflict.JukeboxRootPath)
            .ToList();
        if (!CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(ApplicationSettings.LR2CustomFolderOutputBaseDir, target?.LR2CustomFolderOutputBaseDir ?? LR2CustomFolderOutputDir)
            && warningSearchRoots.Any(root =>
                CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(root, ApplicationSettings.LR2CustomFolderOutputBaseDir)))
        {
            removedSearchRoots.Add(ApplicationSettings.LR2CustomFolderOutputBaseDir);
        }

        var messages = new List<string>();
        string discardConfirmation = BuildCandidateDiscardConfirmation(ConflictingCandidates.Bms | ConflictingCandidates.Lr2Details);
        if (discardConfirmation != null) { messages.Add(discardConfirmation); }
        if (conflicts.Count > 0)
        {
            messages.Add(BuildCustomFolderOutputBaseJukeboxAdoptionMessage(conflicts));
        }
        if (removedSearchRoots.Count > 0)
        {
            messages.Add(FormatResource(
                BeMusicSeeker.Properties.Resources.Confirm_CustomFolderOutputSearchRootsRemovedFormat,
                string.Join(Environment.NewLine, CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(removedSearchRoots))));
        }
        if (messages.Count == 0)
        {
            return true;
        }

        return await ShowUiConfirmationAsync(
            FormatResource(BeMusicSeeker.Properties.Resources.Confirm_CustomFolderOutputChangesFormat,
                string.Join(Environment.NewLine + Environment.NewLine, messages)),
            "Custom folder output settings confirmation");
    }

    // XML自体の不備は当該セクションの検証で通知します。表示用のBMS一覧は管理領域を除外するため使いません。
    private Lr2ConfigSnapshot GetCustomFolderValidationConfig()
        => GetConfirmedLr2Owner()?.Capture();

    private IReadOnlyList<string> GetCustomFolderOutputWarningSearchRoots()
    {
        Lr2ConfigSnapshot config = GetCustomFolderValidationConfig();
        if (config == null)
        {
            return [];
        }

        // 未確定のBMS一覧は使わず、公開済み入力から管理領域への採用を確認します。
        string[] savedSearchRoots = GetConfirmedBmsRoots().ToArray();
        return CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(
            savedSearchRoots.Concat(config.RegisteredRoots));
    }

    private IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(
        IEnumerable<string> jukeboxRoots)
    {
        return CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(
            CreateCurrentCustomFolderOutputBaseCandidates(),
            jukeboxRoots,
            GetPreviousNonNormalManagedCustomFolderSearchRootDirectories(),
            ApplicationSettings.LR2CustomFolderOutputBaseDir);
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

    private IReadOnlyList<CustomFolderOutputBaseCandidate> CreateCurrentCustomFolderOutputBaseCandidates(CustomFolderOutputSettingsSnapshot target = null)
    {
        var candidates = new List<CustomFolderOutputBaseCandidate>
            {
                new(BeMusicSeeker.Properties.Resources.Label_NormalOutputBase, target?.LR2CustomFolderOutputBaseDir ?? LR2CustomFolderOutputDir, isNormalOutput: true),
                new(BeMusicSeeker.Properties.Resources.Label_RootOutputBase, target?.LR2CustomFolderOutputBaseDirRootType ?? LR2CustomFolderAsRootOutputDir)
            };
        foreach (string path in GetOutputAdditionalPaths(target))
        {
            candidates.Add(new CustomFolderOutputBaseCandidate(BeMusicSeeker.Properties.Resources.Label_AdditionalOutputBaseFolder, path));
        }
        return candidates;
    }

    private IReadOnlyList<string> GetPreviousNonNormalManagedCustomFolderSearchRootDirectories()
    {
        var paths = new List<string>
            {
                ApplicationSettings.LR2CustomFolderOutputBaseDirRootType
            };
        paths.AddRange(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs));
        paths.AddRange(CreateRootFolderOutputDirectories(ApplicationSettings.LR2CustomFolderOutputBaseDirRootType));
        return CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(paths);
    }

    /// <summary>
    /// 登録済みパスを新たに管理領域へ採用する候補を列挙します。通常出力先は同じ役割を継続する
    /// 場合だけ確認を省き、追加・ルート出力先への転用による楽曲検索の除外を見落としません。
    /// </summary>
    internal static IReadOnlyList<CustomFolderOutputBaseJukeboxAdoptionConflict> CollectCustomFolderOutputBaseJukeboxAdoptionConflicts(
        IEnumerable<CustomFolderOutputBaseCandidate> candidates,
        IEnumerable<string> jukeboxRoots,
        IEnumerable<string> previousManagedRoots,
        string previousNormalOutputBase = null)
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
                    || (candidate.IsNormalOutput
                        && CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(candidatePath, previousNormalOutputBase)
                        && CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(jukeboxRoot, previousNormalOutputBase))
                    || !CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(candidatePath, jukeboxRoot))
                {
                    continue;
                }

                string key = (candidate?.Label ?? string.Empty) + "\n" + candidatePath + "\n" + jukeboxRoot;
                if (seen.Add(key))
                {
                    conflicts.Add(new CustomFolderOutputBaseJukeboxAdoptionConflict(candidate.Label, candidatePath, jukeboxRoot, candidate.IsNormalOutput));
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
        /// <summary>表示名、出力先、通常出力先の互換検索を維持する役割を捕捉します。</summary>
        internal CustomFolderOutputBaseCandidate(string label, string path, bool isNormalOutput = false)
        {
            Label = label ?? string.Empty;
            Path = path ?? string.Empty;
            IsNormalOutput = isNormalOutput;
        }

        /// <summary>通常出力先として互換検索を維持する候補かを表します。</summary>
        internal bool IsNormalOutput { get; }

        internal string Label { get; }

        internal string Path { get; }
    }

    internal sealed class CustomFolderOutputBaseJukeboxAdoptionConflict
    {
        /// <summary>採用する出力先と影響する登録、その役割を捕捉します。</summary>
        internal CustomFolderOutputBaseJukeboxAdoptionConflict(string outputBaseLabel, string outputBasePath, string jukeboxRootPath, bool isNormalOutput)
        {
            OutputBaseLabel = outputBaseLabel ?? string.Empty;
            OutputBasePath = outputBasePath ?? string.Empty;
            JukeboxRootPath = jukeboxRootPath ?? string.Empty;
            IsNormalOutput = isNormalOutput;
        }

        /// <summary>通常出力先として互換検索を維持する候補かを表します。</summary>
        internal bool IsNormalOutput { get; }

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
            if (!IsTableListURLValid(ApplicationSettings.TableListURL))
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
                    if (!TryCommitSetting(nameof(TableListURL), (nameof(Settings.TableListURL), value))) { return; }
                    tableListUriValidationMessage = string.Empty;
                }
                else
                {
                    tableListUriValidationMessage = BeMusicSeeker.Properties.Resources.Error_InvalidTableListUri;
                    ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidTableListUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
                }
                RaisePropertyChanged("TableListURL");
                RaiseValidationStateChanged();
            }
        }
    }

    /// <summary>Gets the inline validation message for the external table-list URI editor.</summary>
    public string TableListUriValidationMessage => tableListUriValidationMessage;

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
                if (!TryCommitSetting(nameof(EnablePlaylistUrlCompletion), (nameof(Settings.EnablePlaylistUrlCompletion), value))) { return; }
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
                if (!TryCommitSetting(nameof(OverwritePlaylistUrlsWithCompletion), (nameof(Settings.OverwritePlaylistUrlsWithCompletion), value))) { return; }
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
                if (!TryCommitSetting(nameof(EnableStellaFullPlaylistUrlCompletion), (nameof(Settings.EnableStellaFullPlaylistUrlCompletion), value))) { return; }
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
                if (!TryCommitSetting(nameof(PlaylistMd5UrlMappingTsvUri), (nameof(Settings.PlaylistMd5UrlMappingTsvUri), normalizedValue))) { return; }
                playlistMd5UrlMappingTsvUriValidationMessage = string.Empty;
            }
            else
            {
                playlistMd5UrlMappingTsvUriValidationMessage = BeMusicSeeker.Properties.Resources.Error_InvalidPlaylistMd5UrlMappingTsvUri;
                ShowUiMessage(BeMusicSeeker.Properties.Resources.Error_InvalidPlaylistMd5UrlMappingTsvUri, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand);
            }
            RaisePropertyChanged("PlaylistMd5UrlMappingTsvUri");
            RaiseValidationStateChanged();
        }
    }

    /// <summary>Gets the inline validation message for the playlist metadata mapping URI editor.</summary>
    public string PlaylistMd5UrlMappingTsvUriValidationMessage => playlistMd5UrlMappingTsvUriValidationMessage;

    /// <summary>バックアップ方針だけを保存し、LR2連携での有効化成功後に次回起動からの適用を案内します。</summary>
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
                if (!TryCommitSetting(nameof(IsLR2BackupEnabled), (nameof(Settings.IsLR2BackupEnabled), value))) { return; }
                RaisePropertyChanged("IsLR2BackupEnabled");
                RaiseValidationStateChanged();
                if (value && ApplicationSettings.OperationModeLR2DB && !operationAdmission.IsAdmissionClosed)
                {
                    ShowUiMessage(Resources.Msg_LR2ConfigBackupEnabledNextStartup, Resources.Confirm, MessageBoxImage.Asterisk,
                        "LR2 backup enabled notification");
                }
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
                    if (!TryCommitSetting(nameof(LR2BackupPath), (nameof(Settings.LR2BackupPath), value))) { return; }
                }
                else
                {
                    if (!TryCommitSetting(nameof(LR2BackupPath), (nameof(Settings.LR2BackupPath), null))) { return; }
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
                if (!TryCommitSetting(nameof(LR2BackupTarget), (nameof(Settings.LR2BackupTarget), value))) { return; }
                RaisePropertyChanged("LR2BackupTarget");
            }
        }
    }

    public int LR2BackupSpan
    {
        get
        {
            if (!IsLR2BackupSpanValid(ApplicationSettings.LR2BackupSpan))
            {
                return 7;
            }
            return ApplicationSettings.LR2BackupSpan;
        }
        set
        {
            if (ApplicationSettings.LR2BackupSpan != value)
            {
                if (IsLR2BackupSpanValid(value))
                {
                    if (!TryCommitSetting(nameof(LR2BackupSpan), (nameof(Settings.LR2BackupSpan), value))) { return; }
                }
                RaisePropertyChanged("LR2BackupSpan");
            }
        }
    }

    public int LR2BackupNum
    {
        get
        {
            if (!IsLR2BackupNumValid(ApplicationSettings.LR2BackupNum))
            {
                return 1;
            }
            return ApplicationSettings.LR2BackupNum;
        }
        set
        {
            if (ApplicationSettings.LR2BackupNum != value)
            {
                if (IsLR2BackupNumValid(value))
                {
                    if (!TryCommitSetting(nameof(LR2BackupNum), (nameof(Settings.LR2BackupNum), value))) { return; }
                }
                RaisePropertyChanged("LR2BackupNum");
            }
        }
    }

    public bool UseExternalPanelImage
    {
        get
        {
            return imageUseExternalPanelImage;
        }
        set
        {
            if (imageUseExternalPanelImage != value)
            {
                imageUseExternalPanelImage = value;
                RaisePropertyChanged("UseExternalPanelImage");
                RaiseValidationStateChanged();
                CommitImageSettings();
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
                return Key switch
                {
                    AppThemeService.Light => Resources.Appearance_theme_light,
                    AppThemeService.Dark => Resources.Appearance_theme_dark,
                    AppThemeService.Sepia => Resources.Appearance_theme_sepia,
                    AppThemeService.Frost => Resources.Appearance_theme_frost,
                    AppThemeService.NeonViolet => Resources.Appearance_theme_neon_violet,
                    AppThemeService.GreenTerminal => Resources.Appearance_theme_green_terminal,
                    AppThemeService.AmberTerminal => Resources.Appearance_theme_amber_terminal,
                    AppThemeService.OledBlack => Resources.Appearance_theme_oled_black,
                    AppThemeService.DeepSea => Resources.Appearance_theme_deep_sea,
                    AppThemeService.DosBlue => Resources.Appearance_theme_dos_blue,
                    _ => throw new InvalidOperationException($"Unknown appearance theme: {Key}")
                };
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
            return BmtTableExportService.NormalizeHashOutputMode(bmtBeatorajaBmtHashOutputMode).ToString();
        }
        set
        {
            string normalizedValue = BmtTableExportService.NormalizeHashOutputMode(value).ToString();
            if (bmtBeatorajaBmtHashOutputMode != normalizedValue)
            {
                bmtBeatorajaBmtHashOutputMode = normalizedValue;
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
                if (!TryCommitSetting(nameof(AppearanceTheme), (nameof(Settings.AppearanceTheme), normalizedTheme))) { return; }
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
                if (!TryCommitSetting(nameof(CustomTableFontSize), (nameof(Settings.CustomTableFontSize), normalizedValue))) { return; }
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
                if (!TryCommitSetting(nameof(CustomTableRowHeight), (nameof(Settings.CustomTableRowHeight), normalizedValue))) { return; }
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
                if (!TryCommitSetting(nameof(CustomTableHeaderHeight), (nameof(Settings.CustomTableHeaderHeight), normalizedValue))) { return; }
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
                if (!TryCommitSetting(nameof(ShowScoreViewerRegisterConfirmMsg), (nameof(Settings.ShowScoreViewerRegisterConfirmMsg), value))) { return; }
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
                if (!TryCommitSetting(nameof(ShowDiffBMSInstallConfirmMsg), (nameof(Settings.ShowDiffBMSInstallConfirmMsg), value))) { return; }
                RaisePropertyChanged("ShowDiffBMSInstallConfirmMsg");
            }
        }
    }

    /// <summary>保留パッケージを新規として導入するときの確認表示を編集します。</summary>
    public bool ShowNewPackageInstallConfirmMsg
    {
        get
        {
            return ApplicationSettings.ShowNewPackageInstallConfirmMsg;
        }
        set
        {
            if (ApplicationSettings.ShowNewPackageInstallConfirmMsg != value)
            {
                if (!TryCommitSetting(nameof(ShowNewPackageInstallConfirmMsg), (nameof(Settings.ShowNewPackageInstallConfirmMsg), value))) { return; }
                RaisePropertyChanged("ShowNewPackageInstallConfirmMsg");
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
                if (!TryCommitSetting(nameof(ShowDuplicateFileCheckConfirmMsg), (nameof(Settings.ShowDuplicateFileCheckConfirmMsg), value))) { return; }
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
                if (!TryCommitSetting(nameof(ShowRecommUpdatedMsg), (nameof(Settings.ShowRecommUpdatedMsg), value))) { return; }
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
            if (!TryCommitSetting(nameof(ScanBmsFilesOnStartup), (nameof(Settings.ScanBmsFilesOnStartup), value))) { return; }
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
            if (!TryCommitSetting(nameof(SkipInitPlaylistLoad), (nameof(Settings.SkipInitPlaylistLoad), value))) { return; }
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
                if (!TryCommitSetting(nameof(StartupSelectInstallPending), (nameof(Settings.StartupSelectInstallPending), value))) { return; }
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
                if (!TryCommitSetting(nameof(EnableReadOptimizedPragmas), (nameof(Settings.EnableReadOptimizedPragmas), value))) { return; }
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
            if (!TryCommitSetting(nameof(EstimateOfflineScoreRanking), (nameof(Settings.EstimateOfflineScoreRanking), value))) { return; }
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
                if (!TryCommitSetting(nameof(EnableDownloadLr2IrScoreAndDetectUnsent), (nameof(Settings.EnableDownloadLr2IrScoreAndDetectUnsent), value))) { return; }
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
            if (!TryCommitSetting(nameof(UpdateLr2IrRankingCacheOnStartup), (nameof(Settings.UpdateLr2IrRankingCacheOnStartup), value))) { return; }
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
                if (!TryCommitSetting(nameof(EnableAutoInstall), (nameof(Settings.AutoInstall), value))) { return; }
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
                if (!TryCommitSetting(nameof(KeepInstallablePackagesPending), (nameof(Settings.KeepInstallablePackagesPending), value))) { return; }
                RaisePropertyChanged("KeepInstallablePackagesPending");
            }
        }
    }

    /// <summary>自動新規導入で音声・画像・動画・補助画像をすべて要求するかを編集します。</summary>
    public bool RequireCompleteUsedResourcesForAutoInstall
    {
        get
        {
            return ApplicationSettings.RequireCompleteUsedResourcesForAutoInstall;
        }
        set
        {
            if (ApplicationSettings.RequireCompleteUsedResourcesForAutoInstall != value)
            {
                if (!TryCommitSetting(nameof(RequireCompleteUsedResourcesForAutoInstall), (nameof(Settings.RequireCompleteUsedResourcesForAutoInstall), value))) { return; }
                RaisePropertyChanged("RequireCompleteUsedResourcesForAutoInstall");
            }
        }
    }

    /// <summary>列定義の優先度に対応する、次回起動時の準備範囲の選択肢です。</summary>
    public IReadOnlyList<KeyValuePair<int, string>> StartupSortCachePrewarmOptions =>
    [
        new(0, Resources.Startup_sort_cache_none),
        new(1, Resources.Startup_sort_cache_basic),
        new(2, Resources.Startup_sort_cache_standard),
        new(3, Resources.Startup_sort_cache_all)
    ];

    /// <summary>次回起動時の準備範囲を編集します。不正な保存値は無選択とし、nullや範囲外の入力では共有値を変更しません。</summary>
    public int? StartupSortCachePrewarmPriority
    {
        get => IsStartupSortCachePrewarmPriorityValid ? ApplicationSettings.StartupSortCachePrewarmPriority : null;
        set
        {
            if (value is >= 0 and <= 3 && ApplicationSettings.StartupSortCachePrewarmPriority != value.Value)
            {
                if (!TryCommitSetting(nameof(StartupSortCachePrewarmPriority), (nameof(Settings.StartupSortCachePrewarmPriority), value.Value))) { return; }
                RaisePropertyChanged(nameof(StartupSortCachePrewarmPriority));
                RaisePropertyChanged(nameof(StartupSortCachePrewarmWarning));
                RaiseValidationStateChanged();
            }
        }
    }

    /// <summary>不正な保存値の明示修正が必要なことを表示します。</summary>
    public string StartupSortCachePrewarmWarning => IsStartupSortCachePrewarmPriorityValid
        ? string.Empty
        : string.Format(Resources.Startup_sort_cache_invalid, ApplicationSettings.StartupSortCachePrewarmPriority);

    private bool IsStartupSortCachePrewarmPriorityValid => ApplicationSettings.StartupSortCachePrewarmPriority is >= 0 and <= 3;

    /// <summary>利用者が明示選択できる90～100%を1%刻みで提供します。</summary>
    public IReadOnlyList<KeyValuePair<int, string>> InstallDestinationAutoApplyMinAudioHealthOptions { get; } =
        Array.AsReadOnly(Enumerable.Range(90, 11).Select(value => new KeyValuePair<int, string>(value, value + "%")).ToArray());

    /// <summary>自動設定時の各譜面の最低音源充足率を編集します。範囲外の保存値やComboBoxの選択解除は自動補正しません。</summary>
    public int? InstallDestinationAutoApplyMinAudioHealth
    {
        get => ApplicationSettings.InstallDestinationAutoApplyMinAudioHealth;
        set
        {
            if (value is >= 90 and <= 100 && ApplicationSettings.InstallDestinationAutoApplyMinAudioHealth != value.Value)
            {
                if (!TryCommitSetting(nameof(InstallDestinationAutoApplyMinAudioHealth), (nameof(Settings.InstallDestinationAutoApplyMinAudioHealth), value.Value))) { return; }
                RaisePropertyChanged(nameof(InstallDestinationAutoApplyMinAudioHealth));
                RaisePropertyChanged(nameof(InstallDestinationAutoApplyMinAudioHealthWarning));
            }
        }
    }

    /// <summary>保存済みの不正値が未選択になる理由と、明示修正が必要なことを表示します。</summary>
    public string InstallDestinationAutoApplyMinAudioHealthWarning =>
        ApplicationSettings.InstallDestinationAutoApplyMinAudioHealth is >= 90 and <= 100
            ? string.Empty
            : string.Format(Resources.Details_install_destination_min_audio_health_invalid, ApplicationSettings.InstallDestinationAutoApplyMinAudioHealth);

    /// <summary>取り込み後の自動推定で導入先が確定した保留を、続けて確認なしで導入するかを編集します。</summary>
    public bool AutoInstallAfterAutomaticEstimation
    {
        get => ApplicationSettings.AutoInstallAfterAutomaticEstimation;
        set
        {
            if (ApplicationSettings.AutoInstallAfterAutomaticEstimation != value)
            {
                if (!TryCommitSetting(nameof(AutoInstallAfterAutomaticEstimation), (nameof(Settings.AutoInstallAfterAutomaticEstimation), value))) { return; }
                RaisePropertyChanged(nameof(AutoInstallAfterAutomaticEstimation));
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
                if (!TryCommitSetting(nameof(AutoApplyAmbiguousInstallDestination), (nameof(Settings.AutoApplyAmbiguousInstallDestination), value))) { return; }
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
                if (!TryCommitSetting(nameof(DeletePendingPackageSourceAfterInstall), (nameof(Settings.DeletePendingPackageSourceAfterInstall), value))) { return; }
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
                if (!TryCommitSetting(nameof(EnableSmartComponentOverwrite), (nameof(Settings.EnableSmartComponentOverwrite), value))) { return; }
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
                if (!TryCommitSetting(nameof(KeepSmartOverwriteProtectedFilesByRenaming), (nameof(Settings.KeepSmartOverwriteProtectedFilesByRenaming), value))) { return; }
                RaisePropertyChanged("KeepSmartOverwriteProtectedFilesByRenaming");
            }
        }
    }

    public string StagefilePath
    {
        get => imageStagefilePath;
        set
        {
            if (imageStagefilePath == value) { return; }
            if (IsStagefilePathValid(value))
            {
                imageStagefilePath = value;
                CommitImageSettings();
            }
            RaisePropertyChanged(nameof(StagefilePath));
            RaiseValidationStateChanged();
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
                    if (!TryCommitSetting(nameof(FolderNameFormat), (nameof(Settings.FolderNameFormat), value))) { return; }
                }
                else
                {
                    if (!TryCommitSetting(nameof(FolderNameFormat), (nameof(Settings.FolderNameFormat), defaultFolderNameFormat))) { return; }
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
                if (!TryCommitSetting(nameof(UseOnlyShiftJISChars), (nameof(Settings.UseOnlyShiftJISChars), value))) { return; }
                RaisePropertyChanged("UseOnlyShiftJISChars");
            }
        }
    }

    /// <summary>音声ファイル書出しで選択できるサンプルレート名を取得します。</summary>
    public ReadOnlyDictionary<SampleRate, string> EncoderSampleRateNames { get; } = new ReadOnlyDictionary<SampleRate, string>(new Dictionary<SampleRate, string>
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

    /// <summary>音声設定担当が所有するPlayerSampleRateNamesを画面へ接続します。</summary>
    public ReadOnlyDictionary<SampleRate, string> PlayerSampleRateNames => audioDeviceTestWorkflow.PlayerSampleRateNames;

    /// <summary>音声設定担当が所有するIsPlayerSampleRateReadOnlyを画面へ接続します。</summary>
    public bool IsPlayerSampleRateReadOnly => audioDeviceTestWorkflow.IsPlayerSampleRateReadOnly;

    /// <summary>音声設定担当が所有するIsPlayerFormatReadOnlyを画面へ接続します。</summary>
    public bool IsPlayerFormatReadOnly => audioDeviceTestWorkflow.IsPlayerFormatReadOnly;

    /// <summary>音声設定担当が所有するPlayerFormatNamesを画面へ接続します。</summary>
    public ReadOnlyDictionary<SampleFormat, string> PlayerFormatNames => audioDeviceTestWorkflow.PlayerFormatNames;

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
                if (!TryCommitSetting(nameof(EncoderSampleRate), (nameof(Settings.EncoderSampleRate), value))) { return; }
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
                return (int)EncoderType.WAVE;
            }
            return (int)ApplicationSettings.Encoder;
        }
        set
        {
            if (ApplicationSettings.Encoder != (EncoderType)value)
            {
                if (EncoderChecker((EncoderType)value))
                {
                    if (!TryCommitSetting(nameof(EncoderIndex), (nameof(Settings.Encoder), (EncoderType)value))) { return; }
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

    /// <summary>音声ファイル書出しで選択できるサンプル形式名を取得します。</summary>
    public ReadOnlyDictionary<SampleFormat, string> EncoderFormatNames { get; } = new ReadOnlyDictionary<SampleFormat, string>(new Dictionary<SampleFormat, string>
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
                if (!TryCommitSetting(nameof(EncoderFormat), (nameof(Settings.EncoderFormat), value))) { return; }
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
                if (!TryCommitSetting(nameof(EncoderNormalization), (nameof(Settings.EncoderNormalization), BassAudioMapping.ToBassNormalization(value)))) { return; }
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
                if (!TryCommitSetting(nameof(EncoderQuality), (nameof(Settings.EncoderQuality), value))) { return; }
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
                if (!TryCommitSetting(nameof(EncoderAmplifier), (nameof(Settings.EncoderAmplifier), value))) { return; }
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
                if (!TryCommitSetting(nameof(EncoderExeDir), (nameof(Settings.EncoderExeDir), value))) { return; }
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
                if (!TryCommitSetting(nameof(EncodeFileNameFormat), (nameof(Settings.EncodeFileNameFormat), value))) { return; }
                RaisePropertyChanged("EncodeFileNameFormat");
            }
        }
    }

    /// <summary>音声設定担当が所有するPlayerDriverNamesを画面へ接続します。</summary>
    public ReadOnlyObservableCollection<string> PlayerDriverNames => audioDeviceTestWorkflow.PlayerDriverNames;

    /// <summary>音声設定担当が所有するIsPlayerSampleRateSelectionEnabledを画面へ接続します。</summary>
    public bool IsPlayerSampleRateSelectionEnabled => audioDeviceTestWorkflow.IsPlayerSampleRateSelectionEnabled;

    /// <summary>音声設定担当が所有するIsPlayerFormatSelectionEnabledを画面へ接続します。</summary>
    public bool IsPlayerFormatSelectionEnabled => audioDeviceTestWorkflow.IsPlayerFormatSelectionEnabled;

    /// <summary>音声設定担当が所有するIsPlayerWasapiDriverを画面へ接続します。</summary>
    public bool IsPlayerWasapiDriver => audioDeviceTestWorkflow.IsPlayerWasapiDriver;

    /// <summary>音声設定担当が所有するIsPlayerBufferControlEnabledを画面へ接続します。</summary>
    public bool IsPlayerBufferControlEnabled => audioDeviceTestWorkflow.IsPlayerBufferControlEnabled;

    /// <summary>音声設定担当が所有するUnavailablePlayerDriverDescriptionを画面へ接続します。</summary>
    public string UnavailablePlayerDriverDescription => audioDeviceTestWorkflow.UnavailablePlayerDriverDescription;

    /// <summary>音声設定担当が所有するPlayerDriverIndexを画面へ接続します。</summary>
    public int PlayerDriverIndex { get => audioDeviceTestWorkflow.PlayerDriverIndex; set { int previous = audioDeviceTestWorkflow.PlayerDriverIndex; audioDeviceTestWorkflow.PlayerDriverIndex = value; if (!Equals(previous, audioDeviceTestWorkflow.PlayerDriverIndex)) { CommitAudioSettings(); } } }

    /// <summary>音声設定担当が所有するPlayerDeviceNamesを画面へ接続します。</summary>
    public List<AudioDeviceInfo> PlayerDeviceNames => audioDeviceTestWorkflow.PlayerDeviceNames;

    /// <summary>音声設定担当が所有するPlayerDeviceを画面へ接続します。</summary>
    public string PlayerDevice { get => audioDeviceTestWorkflow.PlayerDevice; set { string previous = audioDeviceTestWorkflow.PlayerDevice; audioDeviceTestWorkflow.PlayerDevice = value; if (!Equals(previous, audioDeviceTestWorkflow.PlayerDevice)) { CommitAudioSettings(); } } }

    /// <summary>音声設定担当が所有するSelectedPlayerDeviceを画面へ接続します。</summary>
    public AudioDeviceInfo? SelectedPlayerDevice { get => audioDeviceTestWorkflow.SelectedPlayerDevice; set { AudioDeviceInfo? previous = audioDeviceTestWorkflow.SelectedPlayerDevice; audioDeviceTestWorkflow.SelectedPlayerDevice = value; if (!Equals(previous, audioDeviceTestWorkflow.SelectedPlayerDevice)) { CommitAudioSettings(); } } }

    /// <summary>音声設定担当が所有するPlayerSampleRateを画面へ接続します。</summary>
    public SampleRate PlayerSampleRate { get => audioDeviceTestWorkflow.PlayerSampleRate; set { SampleRate previous = audioDeviceTestWorkflow.PlayerSampleRate; audioDeviceTestWorkflow.PlayerSampleRate = value; if (!Equals(previous, audioDeviceTestWorkflow.PlayerSampleRate)) { CommitAudioSettings(); } } }

    /// <summary>音声設定担当が所有するSelectedPlayerSampleRateを画面へ接続します。</summary>
    public SampleRate? SelectedPlayerSampleRate { get => audioDeviceTestWorkflow.SelectedPlayerSampleRate; set { SampleRate? previous = audioDeviceTestWorkflow.SelectedPlayerSampleRate; audioDeviceTestWorkflow.SelectedPlayerSampleRate = value; if (!Equals(previous, audioDeviceTestWorkflow.SelectedPlayerSampleRate)) { CommitAudioSettings(); } } }

    /// <summary>音声設定担当が所有するPlayerFormatを画面へ接続します。</summary>
    public SampleFormat PlayerFormat { get => audioDeviceTestWorkflow.PlayerFormat; set { SampleFormat previous = audioDeviceTestWorkflow.PlayerFormat; audioDeviceTestWorkflow.PlayerFormat = value; if (!Equals(previous, audioDeviceTestWorkflow.PlayerFormat)) { CommitAudioSettings(); } } }

    /// <summary>音声設定担当が所有するSelectedPlayerFormatを画面へ接続します。</summary>
    public SampleFormat? SelectedPlayerFormat { get => audioDeviceTestWorkflow.SelectedPlayerFormat; set { SampleFormat? previous = audioDeviceTestWorkflow.SelectedPlayerFormat; audioDeviceTestWorkflow.SelectedPlayerFormat = value; if (!Equals(previous, audioDeviceTestWorkflow.SelectedPlayerFormat)) { CommitAudioSettings(); } } }

    /// <summary>音声設定担当が所有するPlayerBufferSizeを画面へ接続します。</summary>
    public float PlayerBufferSize { get => audioDeviceTestWorkflow.PlayerBufferSize; set { float previous = audioDeviceTestWorkflow.PlayerBufferSize; audioDeviceTestWorkflow.PlayerBufferSize = value; if (!Equals(previous, audioDeviceTestWorkflow.PlayerBufferSize)) { CommitAudioSettings(); } } }

    /// <summary>音声設定担当が所有するPlayerResamplingQualityを画面へ接続します。</summary>
    public int PlayerResamplingQuality { get => audioDeviceTestWorkflow.PlayerResamplingQuality; set => audioDeviceTestWorkflow.PlayerResamplingQuality = value; }

    /// <summary>音声設定担当が所有するPlayerResamplingQualityNamesを画面へ接続します。</summary>
    public ReadOnlyDictionary<int, string> PlayerResamplingQualityNames => audioDeviceTestWorkflow.PlayerResamplingQualityNames;

    /// <summary>音声設定担当が所有するPlayerMixerThreadCountを画面へ接続します。</summary>
    public int PlayerMixerThreadCount { get => audioDeviceTestWorkflow.PlayerMixerThreadCount; set => audioDeviceTestWorkflow.PlayerMixerThreadCount = value; }

    /// <summary>音声設定担当が所有するPlayerMixerThreadCountNamesを画面へ接続します。</summary>
    public ReadOnlyDictionary<int, string> PlayerMixerThreadCountNames => audioDeviceTestWorkflow.PlayerMixerThreadCountNames;

    /// <summary>音声設定担当が所有するPlayerWASAPIParamを画面へ接続します。</summary>
    public bool PlayerWASAPIParam { get => audioDeviceTestWorkflow.PlayerWASAPIParam; set { bool previous = audioDeviceTestWorkflow.PlayerWASAPIParam; audioDeviceTestWorkflow.PlayerWASAPIParam = value; if (!Equals(previous, audioDeviceTestWorkflow.PlayerWASAPIParam)) { CommitAudioSettings(); } } }

    public List<string> Languages => [.. cultureCatalog.Cultures.Keys];

    public string Language
    {
        get
        {
            // 表示名が保存されている場合はそれを優先（同一カルチャ名の重複対策）
            string savedDisplayName = ApplicationSettings.LangDisplayName;
            if (!string.IsNullOrEmpty(savedDisplayName) && cultureCatalog.Cultures.ContainsKey(savedDisplayName))
            {
                return savedDisplayName;
            }

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
            if (!TryCommitSetting(nameof(Language), (nameof(Settings.Lang), text), (nameof(Settings.LangDisplayName), value))) { return; }
            ResourceService.Current.ChangeCulture(text);
            RaisePropertyChanged("Language");
        }
    }

    /// <summary>確定値と各セクションの候補を分け、必要な局所操作だけを既存の処理担当へ接続します。</summary>
    /// <param name="operationAdmission">構成で推定・変更と共有する論理受付。</param>
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
        IAudioSettingsGateway audioSettingsGateway = null,
        Func<OperationModeRestartRequest, Task<bool>> requestOperationModeRestart = null,
        ChartFileOperationSynchronizer operationAdmission = null,
        Action publishOutputPlacement = null, Lr2ConfigSession lr2ConfigSession = null)
    {
        SettingsDialogViewModel settingDialogViewModel = this;
        this.lr2ConfigSession = lr2ConfigSession ?? new Lr2ConfigSession();
        this.operationAdmission = operationAdmission ?? new ChartFileOperationSynchronizer();
        this.publishOutputPlacement = publishOutputPlacement ?? (() => { });
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
        this.requestOperationModeRestart = requestOperationModeRestart
            ?? (_ => Task.FromException<bool>(
                new InvalidOperationException("Operation mode restart request is not configured.")));
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
                SettingsFailureMessage.Format(ex),
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "Settings apply failure notification"));
        this.schemaDialogs = schemaDialogs ?? new UiDialogCoordinator();
        this.schemaWindowDialogs = schemaWindowDialogs;
        this.applicationDataUninstallWorkflow = applicationDataUninstallWorkflow
            ?? new ApplicationDataUninstallWorkflowOwner(this.schemaDialogs, new Lr2ApplicationDataUninstallStore());
        this.audioDeviceTestWorkflow = audioDeviceTestWorkflow
            ?? throw new ArgumentNullException(nameof(audioDeviceTestWorkflow));
        this.audioDeviceTestWorkflow.ConfigurePresentation(() => ApplicationSettings, this.audioDeviceCatalog, this.reportApplyFailure);
        this.audioDeviceTestWorkflow.PropertyChanged += AudioSettingsPropertyChanged;
        appearanceThemeOptions = AppThemeService.AvailableThemes.Select(theme => new AppearanceThemeOption(theme)).ToArray();
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
        {
            settingDialogViewModel.RaiseLr2SongDbSyncDataResyncAvailabilityChanged();
            settingDialogViewModel.RaiseEnvironmentAvailabilityChanged();
        };
        statePort.LibraryOperationAvailabilityChanged += libraryOperationAvailabilityChangedHandler;
        lr2PlayHistorySchemaStatusChangedHandler = snapshot =>
            settingDialogViewModel.ApplyLr2PlayHistorySchemaStatusSnapshot(snapshot);
        statePort.Lr2PlayHistorySchemaStatusChanged += lr2PlayHistorySchemaStatusChangedHandler;
        resourceServiceEventListener = new PropertyChangedEventListener(ResourceService.Current);
        resourceServiceEventListener.RegisterHandler(() => ResourceService.Current.Resources, delegate
        {
            settingDialogViewModel.RaisePropertyChanged(nameof(settingDialogViewModel.LR2ConfigBMSDirectories));
            settingDialogViewModel.RaisePropertyChanged(nameof(settingDialogViewModel.AvailableBMSDirectories));
            settingDialogViewModel.RaiseLr2PathPresentationChanged();
            settingDialogViewModel.RaisePropertyChanged(nameof(AudioDeviceListStatusMessage));
            settingDialogViewModel.RaiseValidationStateChanged();
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
            settingDialogViewModel.RaisePropertyChanged(nameof(InstallDestinationAutoApplyMinAudioHealthWarning));
            settingDialogViewModel.RaisePropertyChanged(nameof(StartupSortCachePrewarmOptions));
            settingDialogViewModel.RaisePropertyChanged(nameof(StartupSortCachePrewarmPriority));
            settingDialogViewModel.RaisePropertyChanged(nameof(StartupSortCachePrewarmWarning));
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
        // 起動構成が捕捉する値は保存済み設定の読込み後に揃えます。画面の再表示では再読込みしません。
        this.settingsEditSession.Reload();
        RefreshSectionCandidates();
        rightClickActionSettingsEditor = new RightClickActionSettingsEditor(ApplicationSettings.RightClickActionsJson);
        rightClickActionSettingsEditor.PropertyChanged += RightClickActionSettingsEditorPropertyChanged;
        resourceServiceEventListener.RegisterHandler(() => ResourceService.Current.Resources, delegate
        {
            rightClickActionSettingsEditor.RefreshLocalizedDisplayNames();
        });
        RefreshParsedLr2ConfigState(showSelectionError: false, useLr2Mode: ApplicationSettings.OperationModeLR2DB);
        RefreshBmsRootCandidatesFromSettings();
        RefreshBeatorajaDerivedSettings();
        InitializeSectionEditors();
        ResetLr2PlayHistorySchemaStatus();
        this.operationAdmission.AvailabilityChanged += SettingsOperationAvailabilityChanged;
        this.customFolderOutputPort.OutputOperationAvailabilityChanged += SettingsOperationAvailabilityChanged;
    }

    private bool IsLR2RootPathValid()
    {
        return IsLR2RootPathValid(environmentLR2RootPath);
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
        return IsLR2PlayerRootPathValid(environmentLR2RootPath);
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
            && (CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(value, ApplicationSettings.LR2RootPath) && GetConfirmedLr2Owner() != null
                || IsLR2ConfigXmlPathValid(value2) || IsLR2ConfigXmlPathValid(value3));
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

    private void RefreshBmsRootCandidatesFromSettings()
    {
        IReadOnlyList<string> paths = GetConfirmedBmsRoots();
        bool pathsChanged = !BmsRootPathList.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase);
        string selectedPath = paths.FirstOrDefault();
        bool selectedChanged = !string.Equals(SelectedStandaloneBmsRootPath, selectedPath, StringComparison.OrdinalIgnoreCase);
        if (!pathsChanged && !selectedChanged)
        {
            return;
        }
        if (pathsChanged)
        {
            BmsRootPathList.Clear();
            foreach (string path in paths)
            {
                BmsRootPathList.Add(path);
            }
            RaisePropertyChanged(nameof(BmsRootPathList));
            RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        }
        if (selectedChanged)
        {
            SelectedStandaloneBmsRootPath = selectedPath;
        }
        RaiseValidationStateChanged();
    }


    private void RefreshCustomFolderAdditionalOutputBaseDirsFromSettings()
    {
        IReadOnlyList<string> paths = CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(
            outputLR2CustomFolderAdditionalOutputBaseDirs);
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

    /// <summary>表示だけを有効・失効にし、再表示の候補を最新確定値から作ります。受理済みTaskと保存後の修復要求は保持します。</summary>
    internal void SetPresentationActive(bool active)
    {
        bool presentationOpening = active && !isPresentationActive;
        if (presentationOpening)
        {
            long openingCatalogVersion = workspacePort.PlaylistCatalogVersion;
            InitializeSectionEditors();
            // 候補の再構築で通知した版を記録します。その後のcatalog変更は通常の差分通知へ残します。
            Interlocked.Exchange(ref publishedPlaylistCatalogVersion, openingCatalogVersion);
        }
        isPresentationActive = active;
        audioDeviceTestWorkflow.SetPresentationActive(active);
        if (presentationOpening)
        {
            ClearTransientPlaylistUriValidationState();
            ObservePlaylistCatalogVersion(workspacePort.PlaylistCatalogVersion);
            PublishPlaylistCatalogPresentationIfNeeded();
        }
    }

    /// <summary>ページ表示と明示テストを音声担当へ接続します。</summary>
    internal void SetAudioSettingsPageVisible(bool visible) => audioDeviceTestWorkflow.SetAudioSettingsPageVisible(visible);

    /// <summary>固定した下書きでテストし、音声担当が解放完了まで追跡します。</summary>
    internal Task RunAudioDeviceTestAsync() => RunSettingsOperationAsync(() => RunAudioDeviceTestCoreAsync());

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal Task RunAudioDeviceTestCoreAsync() => IsEditCompletionInProgress ? Task.CompletedTask : audioDeviceTestWorkflow.RunAudioDeviceTestAsync();

    /// <summary>音声一覧・能力照会・テストが実処理と表示終端を終えるTaskを取得します。終了時は先に表示を失効させます。</summary>
    internal Task AudioSettingsCompletionTask => audioDeviceTestWorkflow.AudioSettingsCompletionTask;

    private void ClearTransientPlaylistUriValidationState()
    {
        tableListUriValidationMessage = string.Empty;
        playlistMd5UrlMappingTsvUriValidationMessage = string.Empty;
        RaiseValidationStateChanged();
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

        RaiseBmsListPropertyChanged(nameof(LR2ConfigBMSDirectories));
        RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
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
        RaisePlayHistoryFolderDisplayPresetPropertiesChanged();
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
        IEnumerable<PlayHistoryDisplayTargetSet> confirmed = PlayHistoryDisplayTargetSetStore.Deserialize(ApplicationSettings.PlayHistoryDisplayTargetSetsJson)
            .Where(item => !string.Equals(item.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        string json = PlayHistoryDisplayTargetSetStore.Serialize(confirmed);
        if (!TryCommitSetting(nameof(PlayHistoryFolderDisplayPresets), (nameof(Settings.PlayHistoryDisplayTargetSetsJson), json))) { return; }
        playHistoryPort.RefreshDisplayTargetSetsFromSettings(json, queueRefreshWhenSelectionChanges: true);
        int index = PlayHistoryFolderDisplayPresets.IndexOf(preset);
        PlayHistoryFolderDisplayPresets.Remove(preset);
        SelectedPlayHistoryFolderDisplayPreset = PlayHistoryFolderDisplayPresets.Count == 0
            ? null
            : PlayHistoryFolderDisplayPresets[Math.Max(0, Math.Min(index, PlayHistoryFolderDisplayPresets.Count - 1))];
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
        var selectionIndex = PlayHistoryFolderDisplayPresetSelectionIndex.Create(targets);
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
    /// 別ウィンドウの一record候補だけを保存し、成功後に一覧と実表示へ反映します。
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
        var confirmed = PlayHistoryDisplayTargetSetStore.Deserialize(ApplicationSettings.PlayHistoryDisplayTargetSetsJson).ToList();
        int confirmedIndex = preset == null ? -1 : confirmed.FindIndex(item => string.Equals(item.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        var existingIds = GetPlayHistoryFolderDisplayPresetTables().Select(item => item.PlaylistId.Value).ToHashSet();
        PlayHistoryDisplayTargetSet saved = FilterPlayHistoryFolderDisplayPresetTargets(targetSet, existingIds);
        if (confirmedIndex < 0) { confirmed.Add(saved); } else { confirmed[confirmedIndex] = saved; }
        string json = PlayHistoryDisplayTargetSetStore.Serialize(confirmed);
        if (!TryCommitSetting(nameof(PlayHistoryFolderDisplayPresets), (nameof(Settings.PlayHistoryDisplayTargetSetsJson), json)))
        { errMsg = Resources.Msg_error_unexpected; return false; }
        playHistoryPort.RefreshDisplayTargetSetsFromSettings(json, queueRefreshWhenSelectionChanges: true);

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
        RaisePlayHistoryFolderDisplayPresetPropertiesChanged();
        return true;
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

    private void UpdateAdditionalOutputCandidate()
    {
        outputLR2CustomFolderAdditionalOutputBaseDirs =
            CustomFolderOutputBaseRegistry.SerializeBaseDirectories(CustomFolderAdditionalOutputBaseDirList);
    }

    private void RaiseCustomFolderAdditionalOutputBasePropertiesChanged()
    {
        RaisePropertyChanged(nameof(CustomFolderAdditionalOutputBaseDirList));
        RaisePropertyChanged(nameof(SelectedCustomFolderAdditionalOutputBaseDir));
        RaisePropertyChanged(nameof(SelectedCustomFolderAdditionalOutputBaseName));
        RaiseBmsListPropertyChanged(nameof(LR2ConfigBMSDirectories));
        RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
        RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
        RaisePropertyChanged(nameof(PlaylistPropertyOutputBaseOptions));
        RaiseValidationStateChanged();
    }

    public IReadOnlyList<PlaylistCustomFolderOutputBaseOption> PlaylistPropertyOutputBaseOptions =>
        PlaylistCustomFolderOutputBaseOptions.Create(
            LR2CustomFolderOutputDir,
            CustomFolderAdditionalOutputBaseDirList);

    private bool IsLR2SongDBPathValid(string value)
    {
        return File.Exists(value);
    }

    private bool IsLR2ConfigXmlPathValid(string value)
    {
        return lr2ConfigSession.TryResolve(value, out _, adoptInitial: false);
    }

    private bool IsBeatorajaScoreDbPathValid(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && string.Equals(Path.GetFileName(value), "score.db", StringComparison.OrdinalIgnoreCase)
            && File.Exists(value);
    }

    private void RefreshBeatorajaDerivedSettings()
    {
        // スコア入力では確定rootのscoreだけを解決し、BMTの外部config変更を取り込みません。
        scoreBeatorajaScoreDbPath = BeatorajaConfigService.GetScoreDbPath(ApplicationSettings.BeatorajaRootPath, scoreBeatorajaPlayerId);
    }

    private bool IsuBMplayPathValid(string value)
    {
        return File.Exists(value);
    }

    private bool IsBMIIDXViewPathValid(string value)
    {
        return File.Exists(value);
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
        return NormalizeStandaloneBmsRootPaths(GetConfirmedBmsRoots()).Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private bool IsLR2CustomFolderAsRootOutputDirValid()
    {
        return IsLR2CustomFolderAsRootOutputDirValid(outputLR2CustomFolderOutputBaseDirRootType);
    }

    private bool IsLR2CustomFolderAsRootOutputDirValid(string value)
    {
        return ValidateCustomFolderAsRootOutputBaseDir(value, out _);
    }

    private bool IsTableListURLValid(Uri value)
    {
        return value != null && Uri.TryCreate(value.OriginalString, UriKind.RelativeOrAbsolute, out value);
    }

    private bool IsPlaylistMd5UrlMappingTsvUriValid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        return PlaylistUrlCompletionSupport.TryResolveSourceUri(value, out Uri _);
    }

    private bool IsLR2BackupPathValid(string value)
    {
        return Directory.Exists(value);
    }

    private bool IsLR2BackupSpanValid(int value)
    {
        return value > 0;
    }

    private bool IsLR2BackupNumValid(int value)
    {
        return value > 0;
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
        {
            return;
        }

        SetSettingProperty(propertyName, path);
    }

    public void SetDirectoryPathFromPicker(string propertyName, string path)
    {
        if (path == null)
        {
            return;
        }

        SetSettingProperty(propertyName, path);
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

    /// <summary>追加出力候補を検証して加えます。導入先保護を含む拒否時は未受理候補を復元します。</summary>
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
            UpdateAdditionalOutputCandidate();
            RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
        {
            RestoreUnacceptedOutputCandidates();
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
        UpdateAdditionalOutputCandidate();
        RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
    }

    /// <summary>追加出力候補の末尾名を変更します。拒否時は受理済み配置と改名対応を維持します。</summary>
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
            UpdateAdditionalOutputCandidate();
            RaiseCustomFolderAdditionalOutputBasePropertiesChanged();
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
        {
            RestoreUnacceptedOutputCandidates();
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
        string defaultName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(ApplicationSettings.LR2CustomFolderOutputBaseDir);
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
        ValidateCustomFolderAdditionalOutputBaseSearchRootPath(path, oldPath,
            CopyOutputPlacement(CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings), additional: outputLR2CustomFolderAdditionalOutputBaseDirs));
    }

    private bool ValidateCustomFolderAdditionalOutputBaseDirs(out string errMsg, CustomFolderOutputSettingsSnapshot target = null)
    {
        errMsg = string.Empty;
        try
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            string defaultName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(target?.LR2CustomFolderOutputBaseDir ?? LR2CustomFolderOutputDir);
            if (!string.IsNullOrWhiteSpace(defaultName))
            {
                names.Add(defaultName);
            }

            List<string> normalizedPaths = [.. GetOutputAdditionalPaths(target)
                    .Select(CustomFolderOutputBaseRegistry.NormalizeDirectoryPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))];
            IReadOnlyList<string> previousAdditionalOutputBasePaths =
                CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs);
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
                ValidateCustomFolderAdditionalOutputBaseSearchRootPath(path, oldPath, target);
            }

            for (int leftIndex = 0; leftIndex < normalizedPaths.Count; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < normalizedPaths.Count; rightIndex++)
                {
                    if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(normalizedPaths[leftIndex], normalizedPaths[rightIndex]))
                    {
                        errMsg = FormatPlaylistValidationMessage(FormatResource(BeMusicSeeker.Properties.Resources.Validation_OutputBasesOverlapFormat,
                            BeMusicSeeker.Properties.Resources.Label_AdditionalOutputBaseFolder,
                            BeMusicSeeker.Properties.Resources.Label_AdditionalOutputBaseFolder));
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

    private bool ValidateCustomFolderOutputBaseDir(out string errMsg, bool includeSection = true)
    {
        return ValidateCustomFolderOutputBaseDir(LR2CustomFolderOutputDir, out errMsg, includeSection);
    }

    private bool ValidateCustomFolderOutputBaseDir(string path, out string errMsg, bool includeSection = true, CustomFolderOutputSettingsSnapshot target = null)
    {
        errMsg = string.Empty;
        try
        {
            ValidateOutputBasePreservesInstallDirectory(path);
            string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
            string label = BeMusicSeeker.Properties.Resources.Label_NormalOutputBase;
            CustomFolderOutputBaseSearchRootSyncService.ValidateSjisDirectoryPath(normalizedPath, label);
            if (string.IsNullOrWhiteSpace(CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(normalizedPath)))
            {
                throw new ArgumentException(BeMusicSeeker.Properties.Resources.Validation_NormalOutputBaseNameEmpty);
            }
            ValidateOutputBasesDoNotOverlap(normalizedPath, label,
                target?.LR2CustomFolderOutputBaseDirRootType ?? LR2CustomFolderAsRootOutputDir, BeMusicSeeker.Properties.Resources.Label_RootOutputBase);
            foreach (string additionalPath in GetOutputAdditionalPaths(target))
            {
                ValidateOutputBasesDoNotOverlap(normalizedPath, label,
                    additionalPath, BeMusicSeeker.Properties.Resources.Label_AdditionalOutputBaseFolder);
            }

            ValidatePreviousOutputBase(normalizedPath, label, ApplicationSettings.LR2CustomFolderOutputBaseDir,
                BeMusicSeeker.Properties.Resources.Label_PreviousNormalOutputBase);
            ValidatePreviousOutputBase(normalizedPath, label, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType,
                BeMusicSeeker.Properties.Resources.Label_PreviousRootOutputBase, allowSameDirectory: false);
            foreach (string previousAdditionalPath in CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs))
            {
                ValidatePreviousOutputBase(normalizedPath, label, previousAdditionalPath,
                    BeMusicSeeker.Properties.Resources.Label_PreviousAdditionalOutputBase, allowSameDirectory: false);
            }
            ValidateOutputBaseAgainstBmsRoots(normalizedPath, label);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
        {
            errMsg = includeSection ? FormatPlaylistValidationMessage(ex.Message) : ex.Message;
            return false;
        }
    }

    private string GetCustomFolderOutputDirValidationMessage()
    {
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(LR2CustomFolderOutputDir))
        {
            return BeMusicSeeker.Properties.Resources.Error_CustomFolderOutputPathNotSet;
        }
        return ValidateCustomFolderOutputBaseDir(out string errMsg, includeSection: false)
            ? string.Empty
            : errMsg;
    }

    private static void ValidateOutputBasesDoNotOverlap(string path, string label, string otherPath, string otherLabel)
    {
        if (CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(path, otherPath))
        {
            throw new InvalidOperationException(FormatResource(
                BeMusicSeeker.Properties.Resources.Validation_OutputBasesOverlapFormat, label, otherLabel));
        }
    }

    private static void ValidatePreviousOutputBase(
        string path, string label, string previousPath, string previousLabel, bool allowSameDirectory = true)
    {
        if (allowSameDirectory && CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(path, previousPath))
        {
            return;
        }
        ValidateOutputBasesDoNotOverlap(path, label, previousPath, previousLabel);
    }

    /// <summary>LR2の有効な確定導入先を出力管理領域へ取り込む遷移だけを拒否し、初回や既存不正値の段階保存は妨げません。</summary>
    private void ValidateOutputBasePreservesInstallDirectory(string path)
    {
        string installDirectory = ApplicationSettings.BMSInstallDir;
        if (ApplicationSettings.OperationModeLR2DB
            && NormalizeStandaloneBmsRootPaths(GetConfirmedBmsRoots()).Any(root => CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(root, installDirectory))
            && CustomFolderOutputBaseSearchRootSyncService.IsSameOrChildDirectory(installDirectory, path))
        {
            throw new InvalidOperationException(FormatResource(Resources.Validation_OutputBaseContainsInstallDirectoryFormat, installDirectory));
        }
    }

    private void ValidateOutputBaseAgainstBmsRoots(string path, string label, bool allowRegisteredChildren = false)
    {
        Lr2ConfigSnapshot config = GetCustomFolderValidationConfig();
        if (config != null)
        {
            CustomFolderOutputBaseSearchRootSyncService.ValidateOutputBaseAgainstSearchRoots(
                path, config.RegisteredRoots, label, allowRegisteredChildren);
        }
    }

    private bool ValidateCustomFolderAsRootOutputBaseDir(out string errMsg, bool includeSection = true)
    {
        return ValidateCustomFolderAsRootOutputBaseDir(LR2CustomFolderAsRootOutputDir, out errMsg, includeSection);
    }

    private bool ValidateCustomFolderAsRootOutputBaseDir(string path, out string errMsg, bool includeSection = true, CustomFolderOutputSettingsSnapshot target = null)
    {
        errMsg = string.Empty;
        try
        {
            ValidateOutputBasePreservesInstallDirectory(path);
            string normalizedPath = CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(path);
            string label = BeMusicSeeker.Properties.Resources.Label_RootOutputBase;
            CustomFolderOutputBaseSearchRootSyncService.ValidateSjisDirectoryPath(normalizedPath, label);
            if (string.IsNullOrWhiteSpace(CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(normalizedPath)))
            {
                throw new ArgumentException(BeMusicSeeker.Properties.Resources.Validation_RootOutputBaseNameEmpty);
            }
            ValidateOutputBasesDoNotOverlap(normalizedPath, label,
                target?.LR2CustomFolderOutputBaseDir ?? LR2CustomFolderOutputDir, BeMusicSeeker.Properties.Resources.Label_NormalOutputBase);
            foreach (string additionalPath in GetOutputAdditionalPaths(target))
            {
                ValidateOutputBasesDoNotOverlap(normalizedPath, label,
                    additionalPath, BeMusicSeeker.Properties.Resources.Label_AdditionalOutputBaseFolder);
            }
            ValidatePreviousOutputBase(normalizedPath, label, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType,
                BeMusicSeeker.Properties.Resources.Label_PreviousRootOutputBase);
            ValidateOutputBaseAgainstBmsRoots(normalizedPath, label, allowRegisteredChildren: true);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
        {
            errMsg = includeSection ? FormatPlaylistValidationMessage(ex.Message) : ex.Message;
            return false;
        }
    }

    private string GetCustomFolderRootOutputDirValidationMessage()
    {
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return string.Empty;
        }
        if (string.IsNullOrWhiteSpace(LR2CustomFolderAsRootOutputDir))
        {
            return BeMusicSeeker.Properties.Resources.Error_CustomFolderRootOutputPathNotSet;
        }
        return ValidateCustomFolderAsRootOutputBaseDir(out string errMsg, includeSection: false)
            ? string.Empty
            : errMsg;
    }

    private void ValidateCustomFolderAdditionalOutputBaseSearchRootPath(string path, string oldPath, CustomFolderOutputSettingsSnapshot target = null)
    {
        ValidateOutputBasePreservesInstallDirectory(path);
        string label = BeMusicSeeker.Properties.Resources.Label_AdditionalOutputBaseFolder;
        CustomFolderOutputBaseSearchRootSyncService.ValidateSjisDirectoryPath(path, label);
        ValidateOutputBasesDoNotOverlap(path, label, target?.LR2CustomFolderOutputBaseDir ?? LR2CustomFolderOutputDir, BeMusicSeeker.Properties.Resources.Label_NormalOutputBase);
        ValidateOutputBasesDoNotOverlap(path, label, target?.LR2CustomFolderOutputBaseDirRootType ?? LR2CustomFolderAsRootOutputDir, BeMusicSeeker.Properties.Resources.Label_RootOutputBase);
        foreach (string otherPath in GetOutputAdditionalPaths(target))
        {
            if (!CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(otherPath, path)
                && !CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(otherPath, oldPath))
            {
                ValidateOutputBasesDoNotOverlap(path, label, otherPath, label);
            }
        }
        ValidatePreviousOutputBase(path, label, oldPath, BeMusicSeeker.Properties.Resources.Label_PreviousAdditionalOutputBase);
        ValidateOutputBaseAgainstBmsRoots(path, label);
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

    /// <summary>複数BMS入力を検証し、競合詳細候補の確認後に一覧候補へまとめて追加します。保存は明示確定まで行いません。</summary>
    public Task AddBmsSearchRootPathsAsync(IEnumerable<string> paths) => RunSettingsOperationAsync(() => AddBmsSearchRootPathsCoreAsync(paths));

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal Task AddBmsSearchRootPathsCoreAsync(IEnumerable<string> paths)
        => ApplicationSettings.OperationModeLR2DB ? AddBMSDirectoriesToLR2ConfigAsync(paths) : AddStandaloneBmsRootPathsCoreAsync(paths);

    /// <summary>単独モードのBMS候補を検証・確認後に追加し、確定値は変更しません。</summary>
    public Task AddStandaloneBmsRootPathsAsync(IEnumerable<string> paths) => RunSettingsOperationAsync(() => AddStandaloneBmsRootPathsCoreAsync(paths));

    /// <summary>受理済みの単独BMS候補編集を、設定受付を取り直さず実行します。</summary>
    private async Task AddStandaloneBmsRootPathsCoreAsync(IEnumerable<string> paths)
    {
        try
        {
            string before = SerializeBmsRootPathsForChangeTracking(BmsRootPathList);
            List<string> requestedPaths = [.. NormalizeExistingStandaloneBmsRootPathsWithoutLr2Compatibility(paths ?? [])];
            ThrowIfLr2IncompatibleStandaloneBmsRoots(requestedPaths);
            string requestedPath = requestedPaths.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return;
            }
            if (!requestedPaths.Any(path => !BmsRootPathList.Contains(path, StringComparer.OrdinalIgnoreCase)) || !await PrepareBmsCandidateEditAsync()) { return; }
            requestedPaths = [.. NormalizeExistingStandaloneBmsRootPathsWithoutLr2Compatibility(requestedPaths)];
            if (requestedPaths.Count == 0) { return; }
            requestedPath = requestedPaths[0];
            if (!requestedPaths.Any(path => !BmsRootPathList.Contains(path, StringComparer.OrdinalIgnoreCase))) { return; }
            DiscardConflictingCandidates(ConflictingCandidates.Lr2Details);
            foreach (string path in requestedPaths)
            {
                if (!BmsRootPathList.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    BmsRootPathList.Add(path);
                }
            }
            SelectedStandaloneBmsRootPath = BmsRootPathList.FirstOrDefault(path => string.Equals(path, requestedPath, StringComparison.OrdinalIgnoreCase))
                ?? BmsRootPathList.FirstOrDefault(path => IsSameOrChildPath(requestedPath, path))
                ?? BmsRootPathList.FirstOrDefault();
            string after = SerializeBmsRootPathsForChangeTracking(BmsRootPathList);
            RaisePropertyChanged(nameof(BmsRootPathList));
            RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
            RaiseValidationStateChanged();
        }
        catch (Exception ex)
        {
            await ShowUiMessageAsync(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "BMS candidate edit");
        }
    }

    private async Task AddBMSDirectoriesToLR2ConfigAsync(IEnumerable<string> paths)
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
            void ValidateAddition()
            {
                List<string> nonSjisPaths = [.. requestedPaths.Where(path => !path.IsSjisSchemeString())];
                if (nonSjisPaths.Count > 0)
                {
                    throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_LR2UnicodePathUnsupported + Environment.NewLine + string.Join(Environment.NewLine, nonSjisPaths));
                }
                ThrowIfLr2IncompatibleStandaloneBmsRoots(requestedPaths);
                IReadOnlyList<string> managedCustomFolderOutputRoots = GetUserVisibleBmsSearchRootExcludedDirectories();
                string conflictingPath = requestedPaths.FirstOrDefault(path =>
                    managedCustomFolderOutputRoots.Any(root => CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(path, root)));
                if (!string.IsNullOrWhiteSpace(conflictingPath))
                {
                    throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_ManagedCustomFolderOutputCannotBeAddedAsBmsRoot + Environment.NewLine + conflictingPath);
                }
                if (requestedPaths.Any(path => BmsRootPathList.Any(existing => CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(path, existing)))
                    || requestedPaths.Any(path => requestedPaths.Any(other => !string.Equals(path, other, StringComparison.OrdinalIgnoreCase)
                        && CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(path, other))))
                { throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_BmsSearchDirectoriesOverlap); }
            }
            ValidateAddition();
            if (!await PrepareBmsCandidateEditAsync()) { return; }
            requestedPaths = [.. NormalizeExistingStandaloneBmsRootPathsWithoutLr2Compatibility(requestedPaths)];
            if (requestedPaths.Count == 0) { return; }
            requestedPath = requestedPaths[0];
            ValidateAddition();
            DiscardConflictingCandidates(ConflictingCandidates.Lr2Details);
            foreach (string path in requestedPaths) { BmsRootPathList.Add(path); }
            selectedLR2ConfigBmsDirectory = requestedPath;
            RaiseBmsListPropertyChanged(nameof(LR2ConfigBMSDirectories));
            RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
            RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
            RaiseValidationStateChanged();
        }
        catch (ArgumentException ex)
        {
            await ShowUiMessageAsync(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "BMS candidate edit");
        }
        catch (Exception ex)
        {
            await ShowUiMessageAsync(ex.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "BMS candidate edit");
        }
    }

    /// <summary>選択したBMS登録の候補だけを削除します。競合詳細候補を非同期で確認し、保存は一覧の明示確定まで行いません。</summary>
    internal Task RemoveBmsSearchRootPathAsync(string dir) => RunSettingsOperationAsync(() => RemoveBmsSearchRootPathCoreAsync(dir));

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task RemoveBmsSearchRootPathCoreAsync(string dir)
    {
        if (ApplicationSettings.OperationModeLR2DB)
        {
            Exception removalFailure = await RemoveBMSDirectoryFromLR2ConfigAsync(dir);
            if (removalFailure != null)
            {
                await ShowUiMessageAsync(removalFailure.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "BMS candidate edit");
            }
            return;
        }
        Exception standaloneRemovalFailure = await RemoveStandaloneBmsRootPathAsync(dir);
        if (standaloneRemovalFailure != null)
        {
            await ShowUiMessageAsync(standaloneRemovalFailure.Message, BeMusicSeeker.Properties.Resources.Error, MessageBoxImage.Hand, "BMS candidate edit");
        }
    }

    private async Task<Exception> RemoveStandaloneBmsRootPathAsync(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }
        try
        {
            string normalizedDir = TrimDirectorySeparatorUnlessRoot(Path.GetFullPath(dir.Trim()));
            string before = SerializeBmsRootPathsForChangeTracking(BmsRootPathList);
            List<string> remaining = [.. BmsRootPathList.Where(path => !string.Equals(path, normalizedDir, StringComparison.OrdinalIgnoreCase))];
            if (remaining.Count == BmsRootPathList.Count || !await PrepareBmsCandidateEditAsync()) { return null; }
            remaining = [.. BmsRootPathList.Where(path => !string.Equals(path, normalizedDir, StringComparison.OrdinalIgnoreCase))];
            if (remaining.Count == BmsRootPathList.Count) { return null; }
            DiscardConflictingCandidates(ConflictingCandidates.Lr2Details);
            BmsRootPathList.Clear();
            foreach (string path in remaining)
            {
                BmsRootPathList.Add(path);
            }
            SelectedStandaloneBmsRootPath = BmsRootPathList.FirstOrDefault();
            string after = SerializeBmsRootPathsForChangeTracking(BmsRootPathList);
            RaisePropertyChanged(nameof(BmsRootPathList));
            RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
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

    private async Task<Exception> RemoveBMSDirectoryFromLR2ConfigAsync(string dir)
    {
        if (dir == null)
        {
            throw new ArgumentNullException();
        }
        try
        {
            void ValidateRemoval()
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
            }
            ValidateRemoval();
            string registeredPath = BmsRootPathList.FirstOrDefault(path => CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(path, dir));
            if (registeredPath == null || !await PrepareBmsCandidateEditAsync()) { return null; }
            ValidateRemoval();
            registeredPath = BmsRootPathList.FirstOrDefault(path => CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(path, dir));
            if (registeredPath == null) { return null; }
            DiscardConflictingCandidates(ConflictingCandidates.Lr2Details);
            bool removed = BmsRootPathList.Remove(registeredPath);
            if (removed)
            {
                if (string.Equals(selectedLR2ConfigBmsDirectory, dir, StringComparison.OrdinalIgnoreCase))
                {
                    selectedLR2ConfigBmsDirectory = LR2ConfigBMSDirectories.FirstOrDefault();
                }
                RaiseBmsListPropertyChanged(nameof(LR2ConfigBMSDirectories));
                RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
                RaisePropertyChanged(nameof(SelectedBmsSearchRootPath));
                RaisePropertyChanged(nameof(BMSInstallDir));
                RaiseValidationStateChanged();
                if (searchRootRuntimePort.HasOwnedChartUnderRealPath(dir))
                {
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }






    private bool TryBeginBothSettingsAdmissions(out IDisposable libraryLease, out LibraryFileMutationLease playlistLease)
    {
        playlistLease = null;
        if (!operationAdmission.TryEnter(out libraryLease)) { return false; }
        try
        {
            if (TryBeginRequiredPlaylistOutput(out playlistLease)) { return true; }
        }
        catch
        {
            libraryLease.Dispose();
            libraryLease = null;
            throw;
        }
        libraryLease.Dispose();
        libraryLease = null;
        return false;
    }

    private bool TryBeginRequiredPlaylistOutput(out LibraryFileMutationLease lease)
    {
        lease = customFolderOutputPort.TryBeginOutputOperation();
        return lease != null;
    }

    /// <summary>設定とXMLを永続化して適用配置を公開し、指定した実表示終端を待ってから必要な保存後処理を行います。</summary>
    private bool ShowUiConfirmation(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        MessageBoxButton button,
        string routeName = "UI confirmation dialog",
        MessageBoxResult defaultResult = MessageBoxResult.None,
        string warningMessageBoxText = null)
    {
        UiDialogResult result = schemaDialogs
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

    private async Task ShowUiMessageAsync(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        string routeName,
        MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        try
        {
            UiDialogResult result = await schemaDialogs.ShowMessageAsync(new UiMessageRequest(
                messageBoxText, caption, MessageBoxButton.OK, icon, defaultResult));
            DiagnoseSettingsNotificationResult(result, routeName);
        }
        catch (Exception exception)
        {
            TryLogSettingsLocalFailure(exception, routeName);
        }
    }

    private void ShowUiMessage(
        string messageBoxText,
        string caption,
        MessageBoxImage icon,
        string routeName = "UI message dialog",
        MessageBoxResult defaultResult = MessageBoxResult.OK)
    {
        try
        {
            UiDialogResult result = schemaDialogs
                .ShowMessageAsync(new UiMessageRequest(messageBoxText, caption, MessageBoxButton.OK, icon, defaultResult))
                .GetAwaiter().GetResult();
            DiagnoseSettingsNotificationResult(result, routeName);
        }
        catch (Exception exception)
        {
            TryLogSettingsLocalFailure(exception, routeName);
        }
    }

    private static void DiagnoseSettingsNotificationResult(UiDialogResult result, string routeName)
    {
        if (result == null || result.Status is not (UiDialogStatus.Accepted
            or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser))
        {
            TryLogSettingsLocalFailure(result?.Exception,
                routeName + " status=" + (result?.Status.ToString() ?? "no result"));
        }
    }

    private static void TryLogSettingsLocalFailure(Exception exception, string routeName)
    {
        try
        {
            NLogWrapper.FileLogger?.Error(exception, routeName + " failed");
        }
        catch
        {
            // 任意通知と診断先の失敗は局所で終え、確定結果・確認の判断へ混ぜません。
        }
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

    private static SettingsDialogUiDialogDisplayException CreateUiDialogDisplayException(
        string routeName,
        UiDialogResult result)
    {
        string message = result.Status == UiDialogStatus.Failed
            ? routeName + " failed."
            : routeName + " was not shown: " + result.Status;
        return new SettingsDialogUiDialogDisplayException(message, result.Exception);
    }

    private void AudioSettingsPropertyChanged(object sender, PropertyChangedEventArgs args)
    {
        RaisePropertyChanged(args.PropertyName);
        RaisePropertyChanged(nameof(CanRetryAudioSettings));
        if (args.PropertyName == nameof(IsAudioDeviceTestInProgress))
        {
            RaisePropertyChanged(nameof(IsEditCompletionEnabled));
            RaisePropertyChanged(nameof(CanUninstallApplicationData));
            RaisePropertyChanged(nameof(IsEditCancellationEnabled));
            RaisePropertyChanged(nameof(IsIndependentSectionEnabled));
            RaisePropertyChanged(nameof(IsBmsSearchRootEditorEnabled));
            RaiseEnvironmentAvailabilityChanged();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            operationAdmission.AvailabilityChanged -= SettingsOperationAvailabilityChanged;
            customFolderOutputPort.OutputOperationAvailabilityChanged -= SettingsOperationAvailabilityChanged;
            audioDeviceTestWorkflow.PropertyChanged -= AudioSettingsPropertyChanged;
            audioDeviceTestWorkflow.Dispose();
            workspacePort.PlaylistCatalogChanged -= playlistCatalogChangedHandler;
            statePort.LibraryOperationAvailabilityChanged -= libraryOperationAvailabilityChangedHandler;
            statePort.Lr2PlayHistorySchemaStatusChanged -= lr2PlayHistorySchemaStatusChangedHandler;
            rightClickActionSettingsEditor.PropertyChanged -= RightClickActionSettingsEditorPropertyChanged;
            resourceServiceEventListener.Dispose();
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
