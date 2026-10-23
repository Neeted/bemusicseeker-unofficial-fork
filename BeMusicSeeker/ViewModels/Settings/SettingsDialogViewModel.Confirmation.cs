using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.ViewModels;

public partial class SettingsDialogViewModel
{
    private string environmentLR2RootPath;
    private string environmentLR2SongDBPath;
    private string environmentLR2ConfigXmlPath;
    private string environmentBeatorajaRootPath;
    private bool scoreUseBeatorajaScoreDb;
    private string scoreBeatorajaPlayerId;
    private string scoreBeatorajaScoreDbPath;
    private string beatorajaBmtTablePath;
    private bool bmtEnableBeatorajaBmtOutput;
    private bool bmtKeepBeatorajaBmtFilesWhenOutputDisabled;
    private string bmtBeatorajaBmtHashOutputMode;
    private bool bmtRegisterBeatorajaBmtUrls;
    private string externalPlayeruBMplayPath;
    private string externalPlayerBMIIDXViewPath;
    private bool externalPlayerUsePlayeruBMplay;
    private bool externalPlayerUsePlayerLR2body;
    private bool externalPlayerUsePlayerBMIIDXView;
    private bool imageUseExternalPanelImage;
    private string imageStagefilePath;
    private string outputLR2CustomFolderOutputBaseDir;
    private string outputLR2CustomFolderOutputBaseDirRootType;
    private string outputLR2CustomFolderAdditionalOutputBaseDirs;

    private void RefreshSectionCandidates()
    {
        environmentLR2RootPath = ApplicationSettings.LR2RootPath;
        environmentLR2SongDBPath = ApplicationSettings.LR2SongDBPath;
        environmentLR2ConfigXmlPath = ApplicationSettings.LR2ConfigXmlPath;
        environmentBeatorajaRootPath = ApplicationSettings.BeatorajaRootPath;
        scoreUseBeatorajaScoreDb = ApplicationSettings.UseBeatorajaScoreDb;
        scoreBeatorajaPlayerId = ApplicationSettings.BeatorajaPlayerId;
        scoreBeatorajaScoreDbPath = ApplicationSettings.BeatorajaScoreDbPath;
        bmtEnableBeatorajaBmtOutput = ApplicationSettings.EnableBeatorajaBmtOutput;
        bmtKeepBeatorajaBmtFilesWhenOutputDisabled = ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled;
        bmtBeatorajaBmtHashOutputMode = ApplicationSettings.BeatorajaBmtHashOutputMode;
        bmtRegisterBeatorajaBmtUrls = ApplicationSettings.RegisterBeatorajaBmtUrls;
        externalPlayeruBMplayPath = ApplicationSettings.uBMplayPath;
        externalPlayerBMIIDXViewPath = ApplicationSettings.BMIIDXViewPath;
        externalPlayerUsePlayeruBMplay = ApplicationSettings.UsePlayeruBMplay;
        externalPlayerUsePlayerLR2body = ApplicationSettings.UsePlayerLR2body;
        externalPlayerUsePlayerBMIIDXView = ApplicationSettings.UsePlayerBMIIDXView;
        imageUseExternalPanelImage = ApplicationSettings.UseExternalPanelImage;
        imageStagefilePath = ApplicationSettings.StagefilePath;
        outputLR2CustomFolderOutputBaseDir = ApplicationSettings.LR2CustomFolderOutputBaseDir;
        outputLR2CustomFolderOutputBaseDirRootType = ApplicationSettings.LR2CustomFolderOutputBaseDirRootType;
        outputLR2CustomFolderAdditionalOutputBaseDirs = ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs;
        RefreshEnvironmentBeatorajaCandidate();
    }

    private bool TryCommitSetting(string propertyName, params (string Name, object Value)[] changes)
    {
        if (changes.Any(item => item.Name == nameof(Settings.BMSInstallDir)) && (bmsSaveIncomplete || pendingOutputChange != null))
        { ShowSettingValidationError(Resources.Settings_repair_related_save); RaisePropertyChanged(propertyName); return false; }
        try
        {
            bool changed = changes.Any(item => !Equals(ApplicationSettings[item.Name], item.Value));
            if (!changed) { return true; }
            ApplicationSettings.Commit(changes);
            if (changed && changes.Any(item => item.Name is nameof(Settings.EnablePlaylistUrlCompletion) or nameof(Settings.OverwritePlaylistUrlsWithCompletion)
                or nameof(Settings.EnableStellaFullPlaylistUrlCompletion) or nameof(Settings.PlaylistMd5UrlMappingTsvUri)))
            { workspacePort.SchedulePlaylistUrlCompletionRefresh("settings item confirmation"); }
            return true;
        }
        catch (Exception exception)
        {
            RaisePropertyChanged(propertyName);
            reportApplyFailure(exception);
            return false;
        }
    }

    private bool refreshingSectionEditors;

    private void RaiseBmsListPropertyChanged(string propertyName)
    {
        if (!refreshingSectionEditors) { RaisePropertyChanged(propertyName); RaiseEnvironmentAvailabilityChanged(); }
    }

    private void InitializeSectionEditors()
    {
        refreshingSectionEditors = true;
        try { InitializeSectionEditorsCore(); }
        finally { refreshingSectionEditors = false; }
        RaisePropertyChanged(nameof(LR2ConfigBMSDirectories));
        RaisePropertyChanged(nameof(AvailableBMSDirectories));
    }

    private void InitializeSectionEditorsCore()
    {
        operationModeLR2DB = ApplicationSettings.OperationModeLR2DB;
        RefreshSectionCandidates();
        RefreshParsedLr2ConfigState(showSelectionError: false, useLr2Mode: ApplicationSettings.OperationModeLR2DB);
        RefreshStandaloneBmsRootPathsFromSettings();
        CancelBmsRoots();
        RefreshCustomFolderAdditionalOutputBaseDirsFromSettings();
        RefreshPlayHistoryFolderDisplayPresetsFromSettings();
        audioDeviceTestWorkflow.OutputSelection = audioSettingsGateway.CaptureOutputSelection();
        audioDeviceTestWorkflow.ResetOutputCandidatesToConfirmedSettings();
        audioDeviceTestWorkflow.RestoreSelection();
        rightClickActionSettingsEditor.AcceptSavedSnapshot(ApplicationSettings.RightClickActionsJson);
        rightClickActionSettingsEditor.DiscardChanges();
        RaiseValidationStateChanged();
        RaiseSectionRepairStateChanged();
    }

    /// <summary>実入力でスコア候補が変わったことを画面へ渡し、画面が非同期確定のTaskを所有します。</summary>
    internal event EventHandler ScoreSelectionChanged;

    /// <summary>同じ局所root・player・score候補に未保存差分があるかを示します。</summary>
    public bool HasScoreSelectionChanges => scoreUseBeatorajaScoreDb != ApplicationSettings.UseBeatorajaScoreDb
        || environmentBeatorajaRootPath != ApplicationSettings.BeatorajaRootPath
        || scoreBeatorajaPlayerId != ApplicationSettings.BeatorajaPlayerId
        || scoreBeatorajaScoreDbPath != ApplicationSettings.BeatorajaScoreDbPath;

    /// <summary>同じ局所root・player候補に対してスコアの組が成立しているかを示します。</summary>
    public bool IsScoreSelectionValid => !scoreUseBeatorajaScoreDb
        || BeatorajaConfigService.IsBeatorajaRootPathValid(environmentBeatorajaRootPath)
            && IsBeatorajaScoreDbPathValid(scoreBeatorajaScoreDbPath);

    /// <summary>局所組の保存、又は保存後のscore・旧新BMT反映を明示再試行できます。</summary>
    public bool CanRetryScoreSettings => IsEditCompletionEnabled && !statePort.IsLibraryOperationInProgress && pendingBmtChange == null && IsScoreSelectionValid
        && (HasScoreSelectionChanges || scoreReloadPending || pendingBeatorajaChange != null);

    /// <summary>保存後の旧新出力修復が必要な間は、同じ局所組を保持して変更を制限します。</summary>
    public bool IsBeatorajaSelectionEnabled => IsEnvironmentSelectionEnabled && pendingBmtChange == null;

    private void OnScoreSelectionChanged()
    {
        RaisePropertyChanged(nameof(CanRetryScoreSettings));
        ScoreSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>音声出力の候補だけを確定し、現在の再生と譜面を維持します。</summary>
    internal void CommitAudioSettings()
    {
        if (!IsIndependentSectionEnabled || !audioDeviceTestWorkflow.CanCommitOutputSettings) { return; }
        try { audioDeviceTestWorkflow.CommitOutputSettings(); }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
        finally { RaisePropertyChanged(nameof(CanRetryAudioSettings)); }
    }



    /// <summary>成立した音声候補の保存が未完了であることを示します。</summary>
    public bool CanRetryAudioSettings => audioDeviceTestWorkflow.CanCommitOutputSettings && audioDeviceTestWorkflow.IsOutputSettingsSaveRetryRequired;

    /// <summary>成立した画像候補の保存が未完了であることを示します。</summary>
    public bool CanRetryImageSettings => (!imageUseExternalPanelImage || IsStagefilePathValid(imageStagefilePath))
        && (imageUseExternalPanelImage != ApplicationSettings.UseExternalPanelImage || imageStagefilePath != ApplicationSettings.StagefilePath);

    /// <summary>外部画像の使用方針とパスを一組で保存し現在表示へ反映します。</summary>
    internal void CommitImageSettings()
    {
        if (!IsIndependentSectionEnabled) { return; }
        if (!CanRetryImageSettings) { return; }
        if (TryCommitSetting(nameof(UseExternalPanelImage),
            (nameof(Settings.UseExternalPanelImage), imageUseExternalPanelImage),
            (nameof(Settings.StagefilePath), imageStagefilePath))) { playbackRuntimePort.NotifySettingsChanged(); }
        RaisePropertyChanged(nameof(CanRetryImageSettings));
    }



    /// <summary>保存済みの環境で一回初期化し、必須出力・UI公開の終端で初回設定を閉じます。</summary>
    internal async Task StartConfirmedEnvironmentAsync()
    {
        if (statePort.HasActiveLibraryProfile || !IsEnvironmentSelectionEnabled) { return; }
        if (!operationAdmission.CanEnter || !customFolderOutputPort.CanBeginOutputOperation)
        {
            await ShowUiMessageAsync(Resources.Warn_LibraryOperationBusy, Resources.Warning, UiDialogIcon.Exclamation, "initial settings admission");
            return;
        }
        IsEditCompletionInProgress = true;
        SetEnvironmentTransitionInProgress(true);
        try
        {
            LeapYearFolderRepairApproval approval = await statePort.PrepareLibraryInitializationAsync();
            await RunLibrarySettingsOperationAsync(async capability =>
                await statePort.InitializeLibraryAsync(capability, approval, forceBeatorajaBmtProjection: true));
            if (statePort.HasActiveLibraryProfile && !operationAdmission.IsAdmissionClosed) { ClosePresentation(); }
        }
        catch (OperationCanceledException) when (operationAdmission.IsAdmissionClosed) { }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
        finally { SetEnvironmentTransitionInProgress(false); IsEditCompletionInProgress = false; }
    }

    /// <summary>局所操作のライブラリ受付・処理・cleanup・受付外通知を同じTaskで追跡します。</summary>
    private async Task<bool> RunLibrarySettingsOperationAsync(Func<LibraryFileMutationCapability, Task<StartupInitializationResult>> operation)
    {
        if (!TryBeginBothSettingsAdmissions(out IDisposable lease, out LibraryFileMutationLease playlistLease))
        {
            await ShowUiMessageAsync(Resources.Warn_LibraryOperationBusy, Resources.Warning, UiDialogIcon.Exclamation, "settings section admission");
            return false;
        }
        StartupInitializationResult result = null;
        using LibraryFileMutationCapability playlistCapability = playlistLease.CreateMutationCapability();
        try
        {
            Task CompleteSection() => CompleteAcceptedSettingsOperationAsync(lease, async capability => result = await operation(capability),
                playlistCapability, playlistLease, () => result);
            if (workspacePort.HasPlaylistTables)
            { await workspacePort.RunWithPlaylistOperationNotificationsAsync(CompleteSection, "settings library section terminal"); }
            else { await CompleteSection(); }
            return true;
        }
        catch (LibraryDirectoryPreflightException) { return false; }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } return false; }
    }

    // この設定担当の寿命内でのみ、保存済み変更の旧新BMT入力を固定して修復します。
    private sealed record BeatorajaLocalChange(BeatorajaBmtOptionsSnapshot Previous, BeatorajaBmtOptionsSnapshot Target, bool RequiresScoreReload);
    private BeatorajaLocalChange pendingBeatorajaChange;

    private sealed record BmtLocalChange(BeatorajaBmtOptionsSnapshot Previous, BeatorajaBmtOptionsSnapshot Target);
    private BmtLocalChange pendingBmtChange;

    /// <summary>root・player・派生先を局所保存し、旧新BMT・スコア読込み・必須表示の実終端を同じTaskで待ちます。</summary>
    internal async Task CommitScoreSettingsAsync()
    {
        if (!IsEditCompletionEnabled || statePort.IsLibraryOperationInProgress || pendingBmtChange != null) { return; }
        string root = environmentBeatorajaRootPath;
        string path = scoreBeatorajaScoreDbPath;
        if (!IsScoreSelectionValid)
        {
            ShowSettingValidationError(Resources.Error_InvalidBeatorajaScoreDbPath);
            return;
        }
        if (ApplicationSettings.EnableBeatorajaBmtOutput && !BeatorajaConfigService.IsBeatorajaRootPathValid(root))
        {
            ShowSettingValidationError(Resources.Error_InvalidBeatorajaRootPath);
            return;
        }
        if (!HasScoreSelectionChanges && !scoreReloadPending && pendingBeatorajaChange == null) { return; }
        IsEditCompletionInProgress = true;
        try
        {
            async Task<StartupInitializationResult> SaveAndReload(LibraryFileMutationCapability capability)
            {
                if (pendingBeatorajaChange == null && HasScoreSelectionChanges)
                {
                    var previous = BeatorajaBmtOptionsSnapshot.CreateCurrent(ApplicationSettings);
                    bool requiresScoreReload = ApplicationSettings.UseBeatorajaScoreDb || scoreUseBeatorajaScoreDb;
                    ApplicationSettings.Commit((nameof(Settings.BeatorajaRootPath), root),
                        (nameof(Settings.UseBeatorajaScoreDb), scoreUseBeatorajaScoreDb),
                        (nameof(Settings.BeatorajaPlayerId), scoreBeatorajaPlayerId), (nameof(Settings.BeatorajaScoreDbPath), path),
                        (nameof(Settings.BeatorajaBmtTablePath), beatorajaBmtTablePath));
                    // 保存成功後だけ未了入力を保持します。候補BMTオプションは読みません。
                    pendingBeatorajaChange = new(previous, BeatorajaBmtOptionsSnapshot.CreateCurrent(ApplicationSettings), requiresScoreReload);
                }
                BeatorajaLocalChange change = pendingBeatorajaChange;
                if (statePort.HasActiveLibraryProfile && change != null
                    && !string.Equals(change.Previous.BeatorajaRootPath, change.Target.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (change.Previous.EnableBeatorajaBmtOutput && change.Previous.RegisterBeatorajaBmtUrls
                        && !string.IsNullOrWhiteSpace(change.Previous.BeatorajaBmtTablePath))
                    {
                        // URLの整理に失敗した場合は管理情報を消さず、同じ旧rootで明示再試行します。
                        await Task.Run(() => BeatorajaConfigService.SyncTableUrls(change.Previous.BeatorajaRootPath, [],
                            BmtTableExportService.ReadManagedTableUrls(change.Previous.BeatorajaBmtTablePath).Select(entry => entry.Url)));
                    }
                    await workspacePort.ExportBeatorajaBmtAsync("beatoraja local root confirmation", change.Previous.BeatorajaBmtTablePath,
                        capability?.PlaylistCapability, forceProjection: true, options: change.Target);
                }
                return statePort.HasActiveLibraryProfile && (change?.RequiresScoreReload == true || scoreReloadPending) ? await ReloadScoresOnlyAsync(capability) : null;
            }
            bool complete;
            if (statePort.HasActiveLibraryProfile) { complete = await RunLibrarySettingsOperationAsync(SaveAndReload); }
            else
            {
                if (!operationAdmission.CanEnter || !customFolderOutputPort.CanBeginOutputOperation) { return; }
                await SaveAndReload(null);
                complete = true;
            }
            if (complete && !scoreReloadPending) { pendingBeatorajaChange = null; }
        }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
        finally
        {
            IsEditCompletionInProgress = false;
            RaisePropertyChanged(nameof(CanRetryScoreSettings));
            RaiseEnvironmentAvailabilityChanged();
        }
    }

    /// <summary>BMT候補の組だけを保存し、旧新出力先を保持して全表投影・旧先整理・URL反映を待ちます。保存後の再試行は同じ要求だけを修復します。</summary>
    internal async Task CommitBmtSettingsAsync()
    {
        if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress || statePort.IsLibraryOperationInProgress || pendingBeatorajaChange != null) { return; }
        BeatorajaBmtOptionsSnapshot target = pendingBmtChange?.Target;
        if (target == null)
        {
            string root = ApplicationSettings.BeatorajaRootPath;
            string tablePath = BeatorajaConfigService.GetTablePath(root);
            if (bmtEnableBeatorajaBmtOutput && (!BeatorajaConfigService.IsBeatorajaRootPathValid(root) || string.IsNullOrWhiteSpace(tablePath)))
            {
                ShowSettingValidationError(Resources.Error_InvalidBeatorajaRootPath);
                return;
            }
            if (bmtEnableBeatorajaBmtOutput && !ApplicationSettings.EnableBeatorajaBmtOutput
                && workspacePort.HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(root)
                && !ShowUiConfirmation(Resources.Confirm_enable_beatoraja_bmt_output_before_table_url_import,
                    Resources.Confirm, UiDialogIcon.Exclamation, UiDialogButton.OKCancel, "BMT enable confirmation")) { return; }
            target = new BeatorajaBmtOptionsSnapshot
            {
                BeatorajaRootPath = root,
                BeatorajaBmtTablePath = tablePath,
                EnableBeatorajaBmtOutput = bmtEnableBeatorajaBmtOutput,
                KeepBeatorajaBmtFilesWhenOutputDisabled = bmtKeepBeatorajaBmtFilesWhenOutputDisabled,
                RegisterBeatorajaBmtUrls = bmtRegisterBeatorajaBmtUrls,
                BeatorajaBmtHashOutputMode = bmtBeatorajaBmtHashOutputMode
            };
        }
        IsEditCompletionInProgress = true;
        LibraryFileMutationLease lease = null;
        try
        {
            if (statePort.HasActiveLibraryProfile && !TryBeginRequiredPlaylistOutput(out lease))
            {
                await ShowUiMessageAsync(Resources.Warn_LibraryOperationBusy, Resources.Warning, UiDialogIcon.Exclamation, "BMT section admission");
                return;
            }
            async Task CompleteAcceptedSection()
            {
                using (lease)
                using (LibraryFileMutationCapability capability = lease?.CreateMutationCapability())
                {
                    if (pendingBmtChange == null)
                    {
                        var previous = BeatorajaBmtOptionsSnapshot.CreateCurrent(ApplicationSettings);
                        ApplicationSettings.Commit((nameof(Settings.EnableBeatorajaBmtOutput), target.EnableBeatorajaBmtOutput),
                            (nameof(Settings.KeepBeatorajaBmtFilesWhenOutputDisabled), target.KeepBeatorajaBmtFilesWhenOutputDisabled),
                            (nameof(Settings.RegisterBeatorajaBmtUrls), target.RegisterBeatorajaBmtUrls),
                            (nameof(Settings.BeatorajaBmtHashOutputMode), target.BeatorajaBmtHashOutputMode),
                            (nameof(Settings.BeatorajaBmtTablePath), target.BeatorajaBmtTablePath));
                        // 保存前の旧先は保存済み値から復元できないため、保存成功後に今回の要求と一緒に保持します。
                        pendingBmtChange = new(previous, target);
                    }
                    BmtLocalChange change = pendingBmtChange;
                    if (string.Equals(environmentBeatorajaRootPath, change.Target.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase))
                    { beatorajaBmtTablePath = change.Target.BeatorajaBmtTablePath; }
                    RaisePropertyChanged(nameof(BeatorajaBmtTablePath));
                    RaisePropertyChanged(nameof(EnvironmentBeatorajaBmtTablePath));
                    if (statePort.HasActiveLibraryProfile)
                    {
                        bool keepDisabledOutput = !change.Target.EnableBeatorajaBmtOutput && change.Target.KeepBeatorajaBmtFilesWhenOutputDisabled;
                        if (!keepDisabledOutput && change.Previous.RegisterBeatorajaBmtUrls
                            && !string.IsNullOrWhiteSpace(change.Previous.BeatorajaBmtTablePath)
                            && (!change.Target.EnableBeatorajaBmtOutput
                                || !string.Equals(change.Previous.BeatorajaBmtTablePath, change.Target.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            // 同じ出力先の無効化でも、台帳を消す前にURLを整理して失敗時の再試行入力を残します。
                            await Task.Run(() => BeatorajaConfigService.SyncTableUrls(change.Previous.BeatorajaRootPath, [],
                                BmtTableExportService.ReadManagedTableUrls(change.Previous.BeatorajaBmtTablePath).Select(entry => entry.Url)));
                        }
                        await workspacePort.ExportBeatorajaBmtAsync("settings BMT confirmation", change.Previous.BeatorajaBmtTablePath,
                            capability, forceProjection: true, options: change.Target);
                    }
                }
            }
            if (statePort.HasActiveLibraryProfile)
            { await workspacePort.RunWithPlaylistOperationNotificationsAsync(CompleteAcceptedSection, "settings section terminal"); }
            else { await CompleteAcceptedSection(); }
            pendingBmtChange = null;
        }
        catch (Exception exception)
        {
            if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); }
        }
        finally
        {
            IsEditCompletionInProgress = false;
            RaiseBmtProcessingStateChanged();
        }
    }

    /// <summary>BMTの未確定入力だけを取消します。</summary>
    internal void CancelBmtSettings()
    {
        bmtEnableBeatorajaBmtOutput = ApplicationSettings.EnableBeatorajaBmtOutput;
        bmtKeepBeatorajaBmtFilesWhenOutputDisabled = ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled;
        bmtRegisterBeatorajaBmtUrls = ApplicationSettings.RegisterBeatorajaBmtUrls;
        bmtBeatorajaBmtHashOutputMode = ApplicationSettings.BeatorajaBmtHashOutputMode;
        RaisePropertyChanged(nameof(EnableBeatorajaBmtOutput));
        RaisePropertyChanged(nameof(KeepBeatorajaBmtFilesWhenOutputDisabled));
        RaisePropertyChanged(nameof(RegisterBeatorajaBmtUrls));
        RaisePropertyChanged(nameof(BeatorajaBmtHashOutputMode));
        RaiseEnvironmentAvailabilityChanged();
    }
    private IReadOnlyList<string> confirmedBmsRoots;
    private IReadOnlyList<string> savedBmsXmlRoots;
    private bool bmsSaveIncomplete;
    private string replacementBmsInstallDir;

    /// <summary>保存済み局所組のscore又は旧新BMT反映が未完了で、明示再試行できることを示します。</summary>
    public bool IsScoreProcessingRetryRequired => (scoreReloadPending || pendingBeatorajaChange != null) && !IsEditCompletionInProgress;

    /// <summary>保存済みBMS一覧の差分再読込みが未完了であることを示します。</summary>
    public bool IsBmsProcessingRetryRequired => fileDiffReloadPending && !IsEditCompletionInProgress;

    /// <summary>BMTの保存後実処理の失敗を保持し、同じボタンによる全表再更新を案内します。</summary>
    public bool IsBmtProcessingRetryRequired => pendingBmtChange != null && !IsEditCompletionInProgress;

    private void RaiseBmtProcessingStateChanged()
    {
        RaisePropertyChanged(nameof(IsBmtProcessingRetryRequired));
        RaiseEnvironmentAvailabilityChanged();
        RaisePropertyChanged(nameof(CanRetryScoreSettings));
    }

    /// <summary>導入先が最終BMS一覧から外れる場合にだけ利用する明示的な代替候補です。</summary>
    public string ReplacementBmsInstallDir
    {
        get => replacementBmsInstallDir;
        set { replacementBmsInstallDir = value; RaisePropertyChanged(); RaiseEnvironmentAvailabilityChanged(); }
    }

    /// <summary>BMS一覧または代替導入先に取消可能な局所編集があるかを示します。</summary>
    public bool CanCancelBmsRoots => IsEditCompletionEnabled && (!GetConfirmedBmsRoots().SequenceEqual(AvailableBMSDirectories, StringComparer.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(replacementBmsInstallDir));

    /// <summary>有効なBMS差分または未了保存・再読込みを明示確定できます。</summary>
    public bool CanConfirmBmsRoots => IsEditCompletionEnabled && !statePort.IsLibraryOperationInProgress && pendingOutputChange == null
        && CheckBmsRootsBeforeConfirmation(out _) && (!RequiresReplacementBmsInstallDir || AvailableBMSDirectories.Contains(replacementBmsInstallDir, StringComparer.OrdinalIgnoreCase))
        && (CanCancelBmsRoots || bmsSaveIncomplete || fileDiffReloadPending);

    /// <summary>有効なBMT組の確定・全表再更新、または保存済み要求の明示再試行を受け付けます。未了要求は現在configの新規検証に依存させません。</summary>
    public bool CanConfirmBmtSettings => IsEnvironmentSelectionEnabled
        && (pendingBmtChange != null || !bmtEnableBeatorajaBmtOutput || BeatorajaConfigService.IsBeatorajaRootPathValid(ApplicationSettings.BeatorajaRootPath)
            && !string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(ApplicationSettings.BeatorajaRootPath)));

    /// <summary>BMTの未確定オプションだけに取消可能な差分があるかを示します。</summary>
    public bool CanCancelBmtSettings => IsEditCompletionEnabled && (bmtEnableBeatorajaBmtOutput != ApplicationSettings.EnableBeatorajaBmtOutput
        || bmtKeepBeatorajaBmtFilesWhenOutputDisabled != ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled
        || bmtRegisterBeatorajaBmtUrls != ApplicationSettings.RegisterBeatorajaBmtUrls
        || bmtBeatorajaBmtHashOutputMode != ApplicationSettings.BeatorajaBmtHashOutputMode);

    /// <summary>BMSの二媒体保存に未了段階があることを、画面寿命をまたいで表示します。</summary>
    public bool IsBmsSaveIncomplete => bmsSaveIncomplete;

    /// <summary>出力の保存・実処理・後片付けに未了段階があることを表示します。</summary>
    public bool IsOutputSaveIncomplete => pendingOutputChange != null;

    /// <summary>導入先が最終候補一覧から外れるときだけ代替選択を表示します。</summary>
    public bool RequiresReplacementBmsInstallDir => !AvailableBMSDirectories.Contains(ApplicationSettings.BMSInstallDir, StringComparer.OrdinalIgnoreCase);

    /// <summary>実操作は編集中の環境候補ではなく保存済みのrootを使います。</summary>
    internal string ConfirmedBeatorajaRootPath => ApplicationSettings.BeatorajaRootPath;

    private void RaiseSectionRepairStateChanged()
    {
        RaisePropertyChanged(nameof(IsBmsSaveIncomplete));
        RaisePropertyChanged(nameof(IsOutputSaveIncomplete));
        RaiseEnvironmentAvailabilityChanged();
        RaisePropertyChanged(nameof(HasNormalOutputChanges));
        RaisePropertyChanged(nameof(HasRootOutputChanges));
        RaisePropertyChanged(nameof(HasAdditionalOutputChanges));
    }

    private IReadOnlyList<string> GetConfirmedBmsRoots()
    {
        if (confirmedBmsRoots != null) { return confirmedBmsRoots; }
        if (!ApplicationSettings.OperationModeLR2DB)
        {
            return confirmedBmsRoots = StandaloneBmsRootPathSettings.Deserialize(ApplicationSettings.StandaloneBmsRootPaths, ApplicationSettings.BMSRootPath);
        }
        if (!LR2Config.TryLoad(ApplicationSettings.LR2ConfigXmlPath, out LR2Config config)) { return []; }
        IReadOnlyList<string> excluded = GetUserVisibleBmsSearchRootExcludedDirectories();
        return confirmedBmsRoots = config.GetBMSSearchDirectoriesForChangeTracking()
            .Where(path => !ContainsSameOrChildDirectory(excluded, path)).ToArray();
    }

    private bool IsConfirmedManagedOutputPath(string path)
    {
        var managed = new List<string> { ApplicationSettings.LR2CustomFolderOutputBaseDir, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType };
        managed.AddRange(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs));
        return ContainsSameOrChildDirectory(managed, path);
    }

    /// <summary>最終BMS一覧と必要な代替導入先を一組で確定し、保存成功後に代替候補を解消して差分再読込みを一度だけ実行します。</summary>
    internal async Task CommitBmsRootsAsync()
    {
        if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress || statePort.IsLibraryOperationInProgress) { return; }
        if (pendingOutputChange != null) { ShowSettingValidationError(Resources.Settings_repair_related_save); return; }
        IReadOnlyList<string> roots = AvailableBMSDirectories.ToArray();
        if (!CheckBmsRootsBeforeConfirmation(out string rootError))
        {
            ShowSettingValidationError(rootError);
            return;
        }
        string install = ApplicationSettings.BMSInstallDir;
        if (!roots.Contains(install, StringComparer.OrdinalIgnoreCase))
        {
            if (!roots.Contains(replacementBmsInstallDir, StringComparer.OrdinalIgnoreCase))
            {
                ShowSettingValidationError(Resources.Error_InvalidBmsInstallDir);
                return;
            }
            install = replacementBmsInstallDir;
        }
        bool changed = !GetConfirmedBmsRoots().SequenceEqual(roots, StringComparer.OrdinalIgnoreCase)
            || !string.Equals(install, ApplicationSettings.BMSInstallDir, StringComparison.OrdinalIgnoreCase);
        if (!changed && !bmsSaveIncomplete && !fileDiffReloadPending) { return; }
        IsEditCompletionInProgress = true;
        try
        {
            async Task<StartupInitializationResult> SaveAndReload(LibraryFileMutationCapability capability)
            {
                if (changed || bmsSaveIncomplete)
                {
                    if (ApplicationSettings.OperationModeLR2DB)
                    {
                        if (savedBmsXmlRoots == null || !savedBmsXmlRoots.SequenceEqual(roots, StringComparer.OrdinalIgnoreCase))
                        {
                            if (!LR2Config.TryLoad(ApplicationSettings.LR2ConfigXmlPath, out LR2Config latest))
                            {
                                throw new InvalidOperationException(Resources.Error_InvalidLR2SongDbOrConfigPath);
                            }
                            IEnumerable<string> managed = latest.GetBMSSearchDirectoriesReadOnly().Where(IsConfirmedManagedOutputPath);
                            latest.SetBMSSearchDirectories(managed.Concat(roots));
                            latest.EnsureDatabaseAutoReloadManualOnly();
                            latest.Save();
                            savedBmsXmlRoots = roots.ToArray();
                            bmsSaveIncomplete = true;
                        }
                        ApplicationSettings.Commit((nameof(Settings.BMSInstallDir), install));
                        LR2Config.TryLoad(ApplicationSettings.LR2ConfigXmlPath, out LR2Config committedConfig);
                        lr2config = committedConfig;
                    }
                    else
                    {
                        string serialized = SerializeStandaloneBmsRootPaths(roots);
                        ApplicationSettings.Commit((nameof(Settings.StandaloneBmsRootPaths), serialized),
                            (nameof(Settings.BMSRootPath), roots[0]), (nameof(Settings.BMSInstallDir), install));
                    }
                    confirmedBmsRoots = roots.ToArray();
                    RaisePropertyChanged(nameof(ConfirmedBMSDirectories));
                    savedBmsXmlRoots = null;
                    bmsSaveIncomplete = false;
                    ReplacementBmsInstallDir = null;
                }
                if (statePort.HasActiveLibraryProfile)
                {
                    SetFileDiffReloadPending(true);
                    searchRootRuntimePort.ApplySearchTargets(roots);
                    return await ReloadFileDiffAsync(capability);
                }
                return null;
            }
            if (statePort.HasActiveLibraryProfile) { await RunLibrarySettingsOperationAsync(SaveAndReload); }
            else { await SaveAndReload(null); }
        }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
        finally { IsEditCompletionInProgress = false; RaiseSectionRepairStateChanged(); }
    }

    /// <summary>BMS候補を最新確定一覧へ戻します。二媒体保存の成功事実は保持します。</summary>
    internal void CancelBmsRoots()
    {
        replacementBmsInstallDir = null;
        IReadOnlyList<string> roots = GetConfirmedBmsRoots();
        if (ApplicationSettings.OperationModeLR2DB)
        {
            if (LR2Config.TryLoad(ApplicationSettings.LR2ConfigXmlPath, out LR2Config config))
            {
                IEnumerable<string> managed = config.GetBMSSearchDirectoriesReadOnly().Where(IsConfirmedManagedOutputPath);
                config.SetBMSSearchDirectories(managed.Concat(roots));
                lr2config = config;
            }
        }
        else
        {
            StandaloneBmsRootPathList.Clear();
            foreach (string root in roots) { StandaloneBmsRootPathList.Add(root); }
        }
        RaiseBmsListPropertyChanged(nameof(AvailableBMSDirectories));
        RaisePropertyChanged(nameof(ReplacementBmsInstallDir));
    }

    /// <summary>初期化が読む確定値だけを検証します。別セクションの入力途中の候補は初回開始を妨げません。</summary>
    internal bool CheckValidationForInitialization(out string errMsg)
    {
        var failures = new List<string>();
        Settings settings = ApplicationSettings;
        if (settings.OperationModeLR2DB)
        {
            if (!IsLR2SongDBPathValid(settings.LR2SongDBPath) || !LR2Config.TryLoad(settings.LR2ConfigXmlPath, out _))
            { failures.Add(Resources.Error_InvalidLR2SongDbOrConfigPath); }
            if (string.IsNullOrWhiteSpace(settings.LR2CustomFolderOutputBaseDir)) { failures.Add(Resources.Error_CustomFolderOutputPathNotSet); }
            if (string.IsNullOrWhiteSpace(settings.LR2CustomFolderOutputBaseDirRootType)) { failures.Add(Resources.Error_CustomFolderRootOutputPathNotSet); }
            if (LR2Config.TryLoad(settings.LR2ConfigXmlPath, out LR2Config confirmedConfig))
            {
                IReadOnlyList<NestedBmsSearchRootConflict> conflicts = CollectNestedBmsSearchRootConflicts(confirmedConfig.GetBMSSearchDirectoriesForChangeTracking());
                if (conflicts.Count > 0) { failures.Add(BuildNestedBmsSearchRootConflictMessage(conflicts)); }
                try
                {
                    var bases = new List<(string Path, string Label, bool Root)> {
                        (settings.LR2CustomFolderOutputBaseDir, Resources.Label_NormalOutputBase, false),
                        (settings.LR2CustomFolderOutputBaseDirRootType, Resources.Label_RootOutputBase, true) };
                    bases.AddRange(CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(settings.LR2CustomFolderAdditionalOutputBaseDirs)
                        .Select(path => (path, Resources.Label_AdditionalOutputBaseFolder, false)));
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int index = 0; index < bases.Count; index++)
                    {
                        (string Path, string Label, bool Root) output = bases[index];
                        CustomFolderOutputBaseSearchRootSyncService.ValidateSjisDirectoryPath(output.Path, output.Label);
                        CustomFolderOutputBaseSearchRootSyncService.ValidateOutputBaseAgainstSearchRoots(output.Path,
                            confirmedConfig.GetBMSSearchDirectoriesForChangeTracking(), output.Label, allowRegisteredChildren: output.Root);
                        if (!output.Root && !names.Add(CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(output.Path)))
                        { failures.Add(FormatResource(Resources.Validation_OutputBaseNameDuplicateFormat, CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(output.Path))); }
                        foreach ((string Path, string Label, bool Root) other in bases.Take(index)) { ValidateOutputBasesDoNotOverlap(output.Path, output.Label, other.Path, other.Label); }
                    }
                }
                catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.PathTooLongException)
                { failures.Add(failure.Message); }
            }
        }
        else
        {
            IReadOnlyList<string> roots = StandaloneBmsRootPathSettings.Deserialize(settings.StandaloneBmsRootPaths, settings.BMSRootPath);
            if (NormalizeStandaloneBmsRootPaths(roots).Count == 0) { failures.Add(Resources.Error_InvalidStandaloneBmsRootPaths); }
            List<string> incompatible = GetLr2IncompatibleStandaloneBmsRootPaths(NormalizeStandaloneBmsRootPaths(roots));
            if (incompatible.Count > 0) { failures.Add(Resources.Error_Lr2IncompatibleBmsRootPath + Environment.NewLine + string.Join(Environment.NewLine, incompatible)); }
        }
        if (settings.UseBeatorajaScoreDb && !IsBeatorajaScoreDbPathValid(settings.BeatorajaScoreDbPath)) { failures.Add(Resources.Error_InvalidBeatorajaScoreDbPath); }
        if ((settings.UseBeatorajaScoreDb || settings.EnableBeatorajaBmtOutput) && !BeatorajaConfigService.IsBeatorajaRootPathValid(settings.BeatorajaRootPath))
        { failures.Add(Resources.Error_InvalidBeatorajaRootPath); }
        if (settings.EnableBeatorajaBmtOutput && BeatorajaConfigService.IsBeatorajaRootPathValid(settings.BeatorajaRootPath)
            && string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(settings.BeatorajaRootPath))) { failures.Add(Resources.Error_InvalidBeatorajaBmtTablePath); }
        if (settings.UseExternalPanelImage && !IsStagefilePathValid(settings.StagefilePath)) { failures.Add(Resources.Error_InvalidStagefilePath); }
        if (settings.UsePlayeruBMplay && !IsuBMplayPathValid(settings.uBMplayPath)) { failures.Add(Resources.Error_InvalidUBMPlayExecutablePath); }
        if (settings.UsePlayerBMIIDXView && !IsBMIIDXViewPathValid(settings.BMIIDXViewPath)) { failures.Add(Resources.Error_InvalidBMIIDXViewPath); }
        if (settings.UsePlayerLR2body)
        {
            if (!IsLR2PlayerRootPathValid(settings.LR2RootPath) || !LR2Config.TryLoad(settings.LR2ConfigXmlPath, out _)) { failures.Add(Resources.Error_InvalidLR2RootPath); }
            string executable = StartupSettingsSnapshot.CreateCurrent(settings).LR2bodyPath;
            if (!System.IO.File.Exists(executable)) { failures.Add(FormatResource(Resources.Error_LR2ExecutableNotFoundFormat, executable)); }
            if ((int)settings.LR2bodyResolution.X <= 0 || (int)settings.LR2bodyResolution.Y <= 0) { failures.Add(Resources.Error_InvalidLR2WindowSize); }
        }
        if (!IsBMSInstallDirValid(settings.BMSInstallDir)) { failures.Add(Resources.Error_InvalidBmsInstallDir); }
        if (settings.IsLR2BackupEnabled && settings.OperationModeLR2DB && !IsLR2BackupPathValid(settings.LR2BackupPath)) { failures.Add(Resources.Error_LR2BackupPathNotSet); }
        errMsg = string.Join(Environment.NewLine, failures);
        return failures.Count == 0;
    }

    /// <summary>有効なライブラリ環境がない場合は保存後の明示開始を表示します。</summary>
    public bool IsInitialEnvironment => !statePort.HasActiveLibraryProfile;

    private EnvironmentSettingsChange CaptureEnvironmentCandidate() => new(operationModeLR2DB, environmentLR2RootPath,
        environmentLR2SongDBPath, environmentLR2ConfigXmlPath);

    /// <summary>LR2詳細の候補に、最新確定値からの変更があるかを示します。</summary>
    public bool HasEnvironmentChanges => CaptureEnvironmentCandidate() != EnvironmentSettingsChange.Capture(ApplicationSettings);

    /// <summary>起動の準備・必須UI公開と局所修復が終わり、環境を編集できるかを示します。</summary>
    public bool IsEnvironmentSelectionEnabled => IsEditCompletionEnabled && !environmentTransitionInProgress
        && !statePort.IsLibraryOperationInProgress && pendingBeatorajaChange == null;

    /// <summary>確定済みの設定から初回開始を受け付けられるかを示します。</summary>
    public bool CanStartEnvironment => IsInitialEnvironment && IsEnvironmentSelectionEnabled
        && operationAdmission.CanEnter && customFolderOutputPort.CanBeginOutputOperation
        && CheckValidationForInitialization(out _);

    /// <summary>LR2詳細の適用可能差分だけを確定できます。rootが空でも有効な個別パスを受理します。</summary>
    public bool CanConfirmEnvironment => HasEnvironmentChanges && IsEnvironmentSelectionEnabled
        && !bmsSaveIncomplete && pendingOutputChange == null
        && operationAdmission.CanEnter && customFolderOutputPort.CanBeginOutputOperation
        && (!operationModeLR2DB || IsLR2SongDBPathValid(environmentLR2SongDBPath) && LR2Config.TryLoad(environmentLR2ConfigXmlPath, out _));

    /// <summary>無効な候補も含め、LR2詳細だけを最新確定値へ戻せます。</summary>
    public bool CanCancelEnvironment => HasEnvironmentChanges && IsEditCompletionEnabled && !environmentTransitionInProgress;

    private void RaiseEnvironmentAvailabilityChanged()
    {
        RaisePropertyChanged(nameof(HasEnvironmentChanges));
        RaisePropertyChanged(nameof(CanConfirmEnvironment));
        RaisePropertyChanged(nameof(CanCancelEnvironment));
        RaisePropertyChanged(nameof(IsEnvironmentSelectionEnabled));
        RaisePropertyChanged(nameof(IsBeatorajaSelectionEnabled));
        RaisePropertyChanged(nameof(CanStartEnvironment));
        RaisePropertyChanged(nameof(CanRetryScoreSettings));
        RaisePropertyChanged(nameof(IsInitialEnvironment));
        RaisePropertyChanged(nameof(CanConfirmBmsRoots));
        RaisePropertyChanged(nameof(CanCancelBmsRoots));
        RaisePropertyChanged(nameof(CanCancelBmtSettings));
        RaisePropertyChanged(nameof(CanConfirmBmtSettings));
    }

    private void SettingsOperationAvailabilityChanged(object sender, EventArgs args) => RaiseEnvironmentAvailabilityChanged();

    /// <summary>モードの実選択は保存済みLR2組で反映し、未確定の個別パスを混ぜません。</summary>
    internal Task SelectOperationModeAsync(bool value)
    {
        if (!IsEnvironmentSelectionEnabled) { return Task.CompletedTask; }
        SetOperationModeSelection(value);
        return CommitEnvironmentChangeAsync(EnvironmentSettingsChange.Capture(ApplicationSettings) with { OperationModeLR2DB = value });
    }

    /// <summary>標準パスを解決できた今回のrootだけを反映します。不正入力から前の候補を確定しません。</summary>
    internal Task SelectLr2RootAsync(string root)
    {
        if (!IsEnvironmentSelectionEnabled || root == null) { return Task.CompletedTask; }
        if (!root.IsSjisSchemeString()) { ShowSettingValidationError(Resources.Error_LR2UnicodePathUnsupported); return Task.CompletedTask; }
        if (!TryBuildStandardLr2PathTuple(root, out string song, out string config, out _))
        {
            SetLr2PathSelectionError(Resources.Error_InvalidLR2RootPath);
            return Task.CompletedTask;
        }
        LR2RootPath = root;
        return CommitEnvironmentChangeAsync(new(operationModeLR2DB, root, song, config));
    }

    /// <summary>個別song.db/XMLの候補を一回の選択保存・再起動で確定します。</summary>
    internal Task CommitEnvironmentAsync() => CommitEnvironmentChangeAsync(CaptureEnvironmentCandidate());

    private async Task CommitEnvironmentChangeAsync(EnvironmentSettingsChange change)
    {
        if (!IsEnvironmentSelectionEnabled || !operationAdmission.CanEnter || !customFolderOutputPort.CanBeginOutputOperation) { return; }
        if (bmsSaveIncomplete || pendingOutputChange != null)
        {
            ShowSettingValidationError(Resources.Settings_repair_related_save);
            return;
        }
        if (change.OperationModeLR2DB && (!IsLR2SongDBPathValid(change.LR2SongDBPath)
            || !LR2Config.TryLoad(change.LR2ConfigXmlPath, out _)))
        {
            ShowSettingValidationError(Resources.Error_InvalidLR2SongDbOrConfigPath);
            return;
        }
        if (change == EnvironmentSettingsChange.Capture(ApplicationSettings)) { return; }
        if (!statePort.HasActiveLibraryProfile)
        {
            try
            {
                change.Commit(ApplicationSettings);
                // 初回のモード・LR2 root保存も、次の初期化が捕捉する既存の確定配置へ同じ組を公開します。
                publishOutputPlacement();
                confirmedBmsRoots = null;
                CancelEnvironment();
                RefreshParsedLr2ConfigState(showSelectionError: false, useLr2Mode: ApplicationSettings.OperationModeLR2DB);
                ResetLr2PlayHistorySchemaStatus();
                CancelBmsRoots();
                RaisePropertyChanged(nameof(CanUseLr2Features));
                RaisePropertyChanged(nameof(LR2bodyPath));
                RaiseValidationStateChanged();
            }
            catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
            return;
        }
        if (!operationAdmission.CanEnter || !customFolderOutputPort.CanBeginOutputOperation) { return; }
        if (!ShowUiConfirmation(Resources.Confirm_RestartForOperationModeChange, Resources.Confirm,
            UiDialogIcon.Question, UiDialogButton.OKCancel, "Environment restart confirmation"))
        {
            CancelEnvironment();
            return;
        }
        if (operationAdmission.IsAdmissionClosed || statePort.IsLibraryOperationInProgress) { return; }
        IsEditCompletionInProgress = true;
        SetEnvironmentTransitionInProgress(true);
        bool accepted = false;
        try
        {
            if (!TryBeginBothSettingsAdmissions(out IDisposable lease, out LibraryFileMutationLease playlistLease))
            {
                await ShowUiMessageAsync(Resources.Warn_LibraryOperationBusy, Resources.Warning, UiDialogIcon.Exclamation, "Environment restart admission");
                return;
            }
            using (lease)
            using (playlistLease)
            {
                // 確認中に先着したL/Pを再検査し、保存の先着判断だけをshellへ渡します。
                // 再起動の終端は待たず、受理Taskが戻った時点で解放して終了joinの自己待機を避けます。
                accepted = await requestOperationModeRestart(new OperationModeRestartRequest(change,
                    playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity));
            }
        }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
        finally { if (!accepted) { SetEnvironmentTransitionInProgress(false); IsEditCompletionInProgress = false; } }
    }

    /// <summary>モード・LR2詳細の候補だけを最新確定値へ戻します。</summary>
    internal void CancelEnvironment()
    {
        operationModeLR2DB = ApplicationSettings.OperationModeLR2DB;
        environmentLR2RootPath = ApplicationSettings.LR2RootPath;
        environmentLR2SongDBPath = ApplicationSettings.LR2SongDBPath;
        environmentLR2ConfigXmlPath = ApplicationSettings.LR2ConfigXmlPath;
        isLr2ConfigPathParsed = LR2Config.TryLoad(environmentLR2ConfigXmlPath, out _);
        SetLr2PathSelectionErrorWithoutNotification(string.Empty);
        RaisePropertyChanged(nameof(OperationModeLR2DB));
        RaisePropertyChanged(nameof(IsStandaloneOperationMode));
        RaisePropertyChanged(nameof(LR2RootPath));
        RaisePropertyChanged(nameof(LR2SongDBPath));
        RaisePropertyChanged(nameof(LR2ConfigXmlPath));
        RaiseLr2PathPresentationChanged();
        RaiseEnvironmentAvailabilityChanged();
    }

    /// <summary>局所beatoraja候補のplayerを詳細欄へ表示します。</summary>
    public string EnvironmentBeatorajaPlayerId => scoreBeatorajaPlayerId;

    /// <summary>局所beatoraja候補のscore.dbを詳細欄へ表示します。</summary>
    public string EnvironmentBeatorajaScoreDbPath => scoreBeatorajaScoreDbPath;

    /// <summary>局所beatoraja候補のBMT出力先を詳細欄へ表示します。</summary>
    public string EnvironmentBeatorajaBmtTablePath => beatorajaBmtTablePath;

    private void RefreshEnvironmentBeatorajaCandidate()
    {
        string root = environmentBeatorajaRootPath;
        if (pendingBeatorajaChange != null)
        {
            // 再表示も修復要求の入力を取り替えず、保存済みscoreと捕捉済みtableを表示・検査します。
            scoreBeatorajaPlayerId = ApplicationSettings.BeatorajaPlayerId;
            scoreBeatorajaScoreDbPath = ApplicationSettings.BeatorajaScoreDbPath;
            beatorajaBmtTablePath = pendingBeatorajaChange.Target.BeatorajaBmtTablePath;
        }
        else
        {
            if (string.Equals(root, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase))
            { scoreBeatorajaPlayerId = ApplicationSettings.BeatorajaPlayerId; }
            else
            {
                List<string> players = BeatorajaConfigService.GetPlayerIds(root);
                string configured = BeatorajaConfigService.GetConfiguredPlayerId(root);
                scoreBeatorajaPlayerId = players.Contains(configured, StringComparer.OrdinalIgnoreCase) ? configured : players.FirstOrDefault() ?? string.Empty;
            }
            RefreshBeatorajaDerivedSettings();
        }
        RaiseEnvironmentBeatorajaCandidateChanged();
    }

    private void RaiseEnvironmentBeatorajaCandidateChanged()
    {
        RaisePropertyChanged(nameof(AvailableBeatorajaPlayers));
        RaisePropertyChanged(nameof(BeatorajaPlayerId));
        RaisePropertyChanged(nameof(BeatorajaScoreDbPath));
        RaisePropertyChanged(nameof(BeatorajaBmtTablePath));
        RaisePropertyChanged(nameof(EnvironmentBeatorajaPlayerId));
        RaisePropertyChanged(nameof(EnvironmentBeatorajaScoreDbPath));
        RaisePropertyChanged(nameof(EnvironmentBeatorajaBmtTablePath));
        RaiseEnvironmentAvailabilityChanged();
    }

    /// <summary>再生方式と必要な実行ファイルだけを確定し、次の再生開始へ渡します。</summary>
    internal void CommitExternalPlayerSettings()
    {
        if (!IsIndependentSectionEnabled) { return; }
        if (ExternalPlayerValidationError != null) { return; }
        bool changed = ApplicationSettings.UsePlayeruBMplay != externalPlayerUsePlayeruBMplay
            || ApplicationSettings.UsePlayerLR2body != externalPlayerUsePlayerLR2body
            || ApplicationSettings.UsePlayerBMIIDXView != externalPlayerUsePlayerBMIIDXView
            || ApplicationSettings.uBMplayPath != externalPlayeruBMplayPath || ApplicationSettings.BMIIDXViewPath != externalPlayerBMIIDXViewPath;
        if (!changed) { return; }
        if (!TryCommitSetting(nameof(UseInternalPlayer),
            (nameof(Settings.UsePlayeruBMplay), externalPlayerUsePlayeruBMplay),
            (nameof(Settings.UsePlayerLR2body), externalPlayerUsePlayerLR2body),
            (nameof(Settings.UsePlayerBMIIDXView), externalPlayerUsePlayerBMIIDXView),
            (nameof(Settings.uBMplayPath), externalPlayeruBMplayPath), (nameof(Settings.BMIIDXViewPath), externalPlayerBMIIDXViewPath))) { return; }
        var settings = StartupSettingsSnapshot.CreateCurrent(ApplicationSettings);
        if (changed && statePort.HasActiveLibraryProfile)
        {
            playbackRuntimePort.UsePlayerForNextPlayback(() => playerFactoryPort.CreateBmsPlayerForSettings(settings),
                supportsBmson: !settings.UsePlayeruBMplay && !settings.UsePlayerLR2body && !settings.UsePlayerBMIIDXView);
        }
        RaisePropertyChanged(nameof(CanRetryExternalPlayerSettings));
    }

    private string ExternalPlayerValidationError => externalPlayerUsePlayeruBMplay && !IsuBMplayPathValid(externalPlayeruBMplayPath) ? Resources.Error_InvalidUBMPlayExecutablePath
            : externalPlayerUsePlayerBMIIDXView && !IsBMIIDXViewPathValid(externalPlayerBMIIDXViewPath) ? Resources.Error_InvalidBMIIDXViewPath
            : externalPlayerUsePlayerLR2body && (!IsLR2PlayerRootPathValid(ApplicationSettings.LR2RootPath)
                || !LR2Config.TryLoad(ApplicationSettings.LR2ConfigXmlPath, out _)
                || !System.IO.File.Exists(StartupSettingsSnapshot.CreateCurrent(ApplicationSettings).LR2bodyPath)) ? Resources.Error_InvalidLR2RootPath : null;
    /// <summary>成立した再生方式の保存が未完了であることを示します。</summary>
    public bool CanRetryExternalPlayerSettings => ExternalPlayerValidationError == null
        && (ApplicationSettings.UsePlayeruBMplay != externalPlayerUsePlayeruBMplay
            || ApplicationSettings.UsePlayerLR2body != externalPlayerUsePlayerLR2body
            || ApplicationSettings.UsePlayerBMIIDXView != externalPlayerUsePlayerBMIIDXView
            || ApplicationSettings.uBMplayPath != externalPlayeruBMplayPath || ApplicationSettings.BMIIDXViewPath != externalPlayerBMIIDXViewPath);



    private IEnumerable<string> GetOutputAdditionalPaths(CustomFolderOutputSettingsSnapshot target)
        => target == null ? CustomFolderAdditionalOutputBaseDirList
            : CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(target.LR2CustomFolderAdditionalOutputBaseDirs);

    /// <summary>通常出力の選択がまだ保存されていないことを示します。</summary>
    public bool HasNormalOutputChanges => pendingOutputChange == null && !string.Equals(outputLR2CustomFolderOutputBaseDir, ApplicationSettings.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase);
    /// <summary>ROOT出力の選択がまだ保存されていないことを示します。</summary>
    public bool HasRootOutputChanges => pendingOutputChange == null && !string.Equals(outputLR2CustomFolderOutputBaseDirRootType, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase);
    /// <summary>追加出力の操作がまだ保存されていないことを示します。</summary>
    public bool HasAdditionalOutputChanges => pendingOutputChange == null && outputLR2CustomFolderAdditionalOutputBaseDirs != ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs;

    private OutputConfigurationChange pendingOutputChange;

    private sealed class OutputConfigurationChange(CustomFolderOutputSettingsSnapshot previous, CustomFolderOutputSettingsSnapshot target,
        IReadOnlyDictionary<string, string> renames)
    {
        internal CustomFolderOutputSettingsSnapshot Previous { get; set; } = previous;
        internal CustomFolderOutputSettingsSnapshot Target { get; set; } = target;
        internal CustomFolderOutputSettingsSnapshot SearchRootsPrevious { get; set; } = previous;
        internal IReadOnlyDictionary<string, string> Renames { get; set; } = renames;
        // 確定済みheaderの登録対応を保持し、投影失敗や候補修正後も現在の実登録から移行します。
        internal Dictionary<string, string> CompletedRegistrationRenames { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal CustomFolderOutputSettingsSnapshot SavedSettingsTarget;
        internal bool NormalHeadersCommitted = string.Equals(previous.LR2CustomFolderOutputBaseDir, target.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase);
        internal bool RootHeadersCommitted = string.Equals(previous.LR2CustomFolderOutputBaseDirRootType, target.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase);
        internal bool RegistrationHeadersCommitted = string.Equals(previous.LR2CustomFolderAdditionalOutputBaseDirs, target.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal) && renames.Count == 0;
        internal CustomFolderOutputSettingsSnapshot SavedSearchRootsTarget;
        internal bool Published;
        internal bool ExternalRowsComplete;
        internal List<CustomFolderMigrationReceipt> Migrations { get; } = [];
    }

    /// <summary>出力配置の最小組を保存し、実移行・登録・検索root保存・公開・通知の終端を待ちます。</summary>
    /// <param name="section">normal・root・additionalは当該区分だけ、pendingは受理済み要求だけを扱います。nullは下位の配置候補組を確定します。</param>
    internal async Task CommitOutputSettingsAsync(string section = null)
    {
        if (section is not (null or "normal" or "root" or "additional" or "pending")) { throw new ArgumentOutOfRangeException(nameof(section)); }
        if (section == "pending" && pendingOutputChange == null) { return; }
        if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress || statePort.IsLibraryOperationInProgress) { return; }
        if (bmsSaveIncomplete) { ShowSettingValidationError(Resources.Settings_repair_related_save); return; }
        var previous = CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings);
        CustomFolderOutputSettingsSnapshot baseline = pendingOutputChange?.Target ?? previous;
        var target = new CustomFolderOutputSettingsSnapshot
        {
            OperationModeLR2DB = ApplicationSettings.OperationModeLR2DB,
            LR2RootPath = ApplicationSettings.LR2RootPath,
            LR2CustomFolderOutputBaseDir = section is null or "normal" ? outputLR2CustomFolderOutputBaseDir : baseline.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderOutputBaseDirRootType = section is null or "root" ? outputLR2CustomFolderOutputBaseDirRootType : baseline.LR2CustomFolderOutputBaseDirRootType,
            LR2CustomFolderAdditionalOutputBaseDirs = section is null or "additional" ? outputLR2CustomFolderAdditionalOutputBaseDirs : baseline.LR2CustomFolderAdditionalOutputBaseDirs,
            EnableDownloadLr2IrScoreAndDetectUnsent = ApplicationSettings.EnableDownloadLr2IrScoreAndDetectUnsent,
            PlaylistDefaultIgnoreFolderOutput = ApplicationSettings.PlaylistDefaultIgnoreFolderOutput,
            ShowRecommUpdatedMsg = ApplicationSettings.ShowRecommUpdatedMsg
        };
        if (section == "pending" && pendingOutputChange != null) { target = pendingOutputChange.Target; }
        // 初回は未設定の別区分を残して順に保存できます。選択対象と設定済みの配置は検証し、開始時は両必須値を別途検証します。
        bool requireNormal = statePort.HasActiveLibraryProfile || section is null or "normal" || !string.IsNullOrWhiteSpace(target.LR2CustomFolderOutputBaseDir);
        bool requireRoot = statePort.HasActiveLibraryProfile || section is null or "root" || !string.IsNullOrWhiteSpace(target.LR2CustomFolderOutputBaseDirRootType);
        string error;
        if ((requireNormal && !ValidateCustomFolderOutputBaseDir(target.LR2CustomFolderOutputBaseDir, out error, target: target))
            || (requireRoot && !ValidateCustomFolderAsRootOutputBaseDir(target.LR2CustomFolderOutputBaseDirRootType, out error, target: target))
            || !ValidateCustomFolderAdditionalOutputBaseDirs(out error, target)) { ShowSettingValidationError(error); return; }
        if (!ConfirmCustomFolderOutputChangesBeforeSave(target)) { return; }
        if (pendingOutputChange != null && !SameOutputPlacement(pendingOutputChange.Target, target))
        {
            OutputConfigurationChange repair = pendingOutputChange;
            repair.Target = target;
            repair.Renames = new Dictionary<string, string>(pendingCustomFolderAdditionalOutputBaseRenames, StringComparer.OrdinalIgnoreCase);
            repair.NormalHeadersCommitted = string.Equals(repair.Previous.LR2CustomFolderOutputBaseDir, target.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase);
            repair.RootHeadersCommitted = string.Equals(repair.Previous.LR2CustomFolderOutputBaseDirRootType, target.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase);
            repair.RegistrationHeadersCommitted = string.Equals(repair.Previous.LR2CustomFolderAdditionalOutputBaseDirs, target.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal);
            repair.Published = SameOutputPlacement(CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings), target);
            repair.ExternalRowsComplete = false;
        }
        if (pendingOutputChange == null && SameOutputPlacement(previous, target) && pendingCustomFolderAdditionalOutputBaseRenames.Count == 0) { return; }
        IsEditCompletionInProgress = true;
        try
        {
            if (!TryBeginBothSettingsAdmissions(out IDisposable lease, out LibraryFileMutationLease playlistLease))
            {
                await ShowUiMessageAsync(Resources.Warn_LibraryOperationBusy, Resources.Warning, UiDialogIcon.Exclamation, "output settings admission");
                return;
            }
            async Task CompleteAcceptedSection()
            {
                using (lease)
                using (playlistLease)
                using (LibraryFileMutationCapability capability = playlistLease.CreateMutationCapability())
                {
                    OutputConfigurationChange change = pendingOutputChange ?? new OutputConfigurationChange(previous, target,
                        section is null or "additional" ? new Dictionary<string, string>(pendingCustomFolderAdditionalOutputBaseRenames, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>());
                    // 修復では確定済みヘッダーの現在名から候補を解決し、媒体を一つも保存する前に止めます。
                    IReadOnlyDictionary<string, string> registrationRenames = GetOutputRegistrationRepairRenames(change);
                    customFolderOutputPort.ValidateCustomFolderOutputRegistration(target, registrationRenames);
                    pendingOutputChange = change;
                    (string Name, object Value)[] values = [
                        (nameof(Settings.LR2CustomFolderOutputBaseDir), change.Target.LR2CustomFolderOutputBaseDir),
                    (nameof(Settings.LR2CustomFolderOutputBaseDirRootType), change.Target.LR2CustomFolderOutputBaseDirRootType),
                    (nameof(Settings.LR2CustomFolderAdditionalOutputBaseDirs), change.Target.LR2CustomFolderAdditionalOutputBaseDirs)];
                    if (change.SavedSettingsTarget == null || !SameOutputPlacement(change.SavedSettingsTarget, change.Target))
                    { ApplicationSettings.Persist(values); change.SavedSettingsTarget = change.Target; }
                    // 改名済みヘッダーと旧配置readerを混成させないため、必要媒体を先に保存し、同じL/P内で公開・移行します。
                    if ((change.SavedSearchRootsTarget == null || !SameOutputPlacement(change.SavedSearchRootsTarget, change.Target)) && ApplicationSettings.OperationModeLR2DB)
                    {
                        try
                        {
                            if (!LR2Config.TryLoad(ApplicationSettings.LR2ConfigXmlPath, out LR2Config latest)) { throw new InvalidOperationException(Resources.Error_InvalidLR2SongDbOrConfigPath); }
                            // 初回・修復の表依存ROOT登録は初期化時に揃えます。無関係な未修復先へはアクセスしません。
                            if (statePort.HasActiveLibraryProfile)
                            {
                                latest.SetBMSSearchDirectories(customFolderOutputPort.PrepareCustomFolderOutputSearchRoots(
                                    change.SearchRootsPrevious.LR2CustomFolderOutputBaseDirRootType, change.Target,
                                    latest.GetBMSSearchDirectoriesForChangeTracking(), registrationRenames));
                            }
                            if (statePort.HasActiveLibraryProfile
                                || !string.Equals(change.SearchRootsPrevious.LR2CustomFolderOutputBaseDir, change.Target.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase)
                                || !string.Equals(change.SearchRootsPrevious.LR2CustomFolderAdditionalOutputBaseDirs, change.Target.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal))
                            {
                                CustomFolderOutputBaseSearchRootSyncPlan plan = CustomFolderOutputBaseSearchRootSyncService.PrepareNormalOutputBaseRoots(latest,
                                    change.SearchRootsPrevious.LR2CustomFolderOutputBaseDir, change.Target.LR2CustomFolderOutputBaseDir,
                                    change.SearchRootsPrevious.LR2CustomFolderAdditionalOutputBaseDirs, change.Target.LR2CustomFolderAdditionalOutputBaseDirs, []);
                                CustomFolderOutputBaseSearchRootSyncService.CompleteAdditionalOutputBaseRootSync(latest, plan);
                            }
                            // メモリ上のChangedだけで省略せず、未了のディスク保存を必ず終えます。
                            latest.Save();
                            change.SavedSearchRootsTarget = change.Target;
                            change.SearchRootsPrevious = change.Target;
                        }
                        catch (Exception failure) { throw new PartialSettingsSaveException(ApplicationSettings.LR2ConfigXmlPath, failure); }
                    }
                    if (!change.Published)
                    {
                        ApplicationSettings.Publish(values);
                        publishOutputPlacement();
                        change.Published = true;
                    }
                    if (statePort.HasActiveLibraryProfile)
                    {
                        await Task.Run(() =>
                        {
                            if (!change.NormalHeadersCommitted)
                            {
                                void AcceptNormalHeaders()
                                {
                                    change.Previous = CopyOutputPlacement(change.Previous, normal: change.Target.LR2CustomFolderOutputBaseDir);
                                    change.NormalHeadersCommitted = true;
                                }
                                if (!string.Equals(change.Previous.LR2CustomFolderOutputBaseDir, change.Target.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase))
                                {
                                    customFolderOutputPort.ChangeCustomFolderBaseDirectoryWithSettings(change.Previous.LR2CustomFolderOutputBaseDir,
                                    change.Target.LR2CustomFolderOutputBaseDir, change.Previous.LR2CustomFolderAdditionalOutputBaseDirs,
                                    change.Target.LR2CustomFolderAdditionalOutputBaseDirs, change.Target, capability, receipt =>
                                    {
                                        change.Migrations.Add(receipt);
                                        AcceptNormalHeaders();
                                    }, registrationRenames);
                                }
                                AcceptNormalHeaders();
                            }
                            if (!change.RegistrationHeadersCommitted)
                            {
                                IReadOnlyDictionary<string, string> currentRenames = GetOutputRegistrationRepairRenames(change);
                                void AcceptRegistrationHeaders()
                                {
                                    if (change.RegistrationHeadersCommitted) { return; }
                                    foreach ((string oldName, string newName) in currentRenames)
                                    {
                                        string[] origins = change.CompletedRegistrationRenames.Where(pair => string.Equals(pair.Value, oldName, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
                                        if (origins.Length == 0) { change.CompletedRegistrationRenames[oldName] = newName; }
                                        else { foreach (string origin in origins) { change.CompletedRegistrationRenames[origin] = newName; } }
                                    }
                                    change.Previous = CopyOutputPlacement(change.Previous, additional: change.Target.LR2CustomFolderAdditionalOutputBaseDirs);
                                    change.RegistrationHeadersCommitted = true;
                                }
                                customFolderOutputPort.ApplyCustomFolderAdditionalOutputBaseRegistrationChanges(
                                    change.Previous.LR2CustomFolderAdditionalOutputBaseDirs, currentRenames, change.Target, capability, receipt =>
                                    {
                                        change.Migrations.Add(receipt);
                                        AcceptRegistrationHeaders();
                                    });
                                AcceptRegistrationHeaders();
                            }
                            if (!change.RootHeadersCommitted)
                            {
                                void AcceptRootHeaders()
                                {
                                    change.Previous = CopyOutputPlacement(change.Previous, root: change.Target.LR2CustomFolderOutputBaseDirRootType);
                                    change.RootHeadersCommitted = true;
                                }
                                if (!string.Equals(change.Previous.LR2CustomFolderOutputBaseDirRootType, change.Target.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase))
                                {
                                    customFolderOutputPort.ChangeCustomFolderBaseDirectoryRootWithSettings(change.Previous.LR2CustomFolderOutputBaseDirRootType,
                                    change.Target.LR2CustomFolderOutputBaseDirRootType, change.Target, capability, receipt =>
                                    {
                                        change.Migrations.Add(receipt);
                                        AcceptRootHeaders();
                                    });
                                }
                                AcceptRootHeaders();
                            }
                        });
                    }
                    if (statePort.HasActiveLibraryProfile)
                    {
                        await Task.Run(() => customFolderOutputPort.RetryCustomFolderMigrations(change.Migrations, change.Target, capability));
                    }
                    if (statePort.HasActiveLibraryProfile && !change.ExternalRowsComplete)
                    {
                        using LibraryFileMutationCapability primary = operationAdmission.CreateMutationCapability(lease);
                        await lr2SongDbSyncWorkflow.SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChangeAsync(
                            "output settings confirmation", primary, capability);
                        change.ExternalRowsComplete = true;
                    }
                    change.Migrations.RemoveAll(receipt => receipt.CleanupCompleted);
                    if (change.Migrations.Count == 0) { pendingOutputChange = null; }
                    if (section is null or "additional") { pendingCustomFolderAdditionalOutputBaseRenames.Clear(); }
                    else if (section == "pending")
                    {
                        foreach ((string original, string targetName) in change.Renames)
                        {
                            if (pendingCustomFolderAdditionalOutputBaseRenames.TryGetValue(original, out string candidate)
                                && string.Equals(candidate, targetName, StringComparison.OrdinalIgnoreCase))
                            { pendingCustomFolderAdditionalOutputBaseRenames.Remove(original); }
                        }
                    }
                }
            }
            if (statePort.HasActiveLibraryProfile)
            { await workspacePort.RunWithPlaylistOperationNotificationsAsync(CompleteAcceptedSection, "settings section terminal"); }
            else { await CompleteAcceptedSection(); }
        }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
        finally { IsEditCompletionInProgress = false; RaiseSectionRepairStateChanged(); }
    }

    private static IReadOnlyDictionary<string, string> GetOutputRegistrationRepairRenames(OutputConfigurationChange change)
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string original, string target) in change.Renames)
        {
            string current = change.CompletedRegistrationRenames.TryGetValue(original, out string completed) ? completed : original;
            renames[current] = target;
        }
        var targetNames = CustomFolderOutputBaseRegistry.CreateAdditionalEntries(CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(change.Target.LR2CustomFolderAdditionalOutputBaseDirs))
            .Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string original, string current) in change.CompletedRegistrationRenames)
        {
            // 取消・再表示で元の確定名へ戻した場合も、成立済みの登録対応を明示して戻します。
            if (!renames.ContainsKey(current) && targetNames.Contains(original) && !targetNames.Contains(current)) { renames[current] = original; }
        }
        return renames;
    }

    private static CustomFolderOutputSettingsSnapshot CopyOutputPlacement(CustomFolderOutputSettingsSnapshot source,
        string normal = null, string root = null, string additional = null) => new()
        {
            OperationModeLR2DB = source.OperationModeLR2DB,
            LR2RootPath = source.LR2RootPath,
            LR2CustomFolderOutputBaseDir = normal ?? source.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderOutputBaseDirRootType = root ?? source.LR2CustomFolderOutputBaseDirRootType,
            LR2CustomFolderAdditionalOutputBaseDirs = additional ?? source.LR2CustomFolderAdditionalOutputBaseDirs,
            EnableDownloadLr2IrScoreAndDetectUnsent = source.EnableDownloadLr2IrScoreAndDetectUnsent,
            PlaylistDefaultIgnoreFolderOutput = source.PlaylistDefaultIgnoreFolderOutput,
            ShowRecommUpdatedMsg = source.ShowRecommUpdatedMsg
        };

    private static bool SameOutputPlacement(CustomFolderOutputSettingsSnapshot left, CustomFolderOutputSettingsSnapshot right)
        => string.Equals(left.LR2CustomFolderOutputBaseDir, right.LR2CustomFolderOutputBaseDir, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.LR2CustomFolderOutputBaseDirRootType, right.LR2CustomFolderOutputBaseDirRootType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.LR2CustomFolderAdditionalOutputBaseDirs, right.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal);

    /// <summary>右クリックの選択recordまたは明示的な削除・並替・既定値復帰だけを保存します。</summary>
    internal void CommitRightClickRecord(bool web, string operation = "save")
    {
        if (!IsIndependentSectionEnabled) { return; }
        if (!rightClickActionSettingsEditor.TryPrepareRecordChange(ApplicationSettings.RightClickActionsJson, web, operation,
            out string json, out string error)) { ShowSettingValidationError(error); return; }
        if (!TryCommitSetting(nameof(RightClickActionSettingsEditor), (nameof(Settings.RightClickActionsJson), json))) { return; }
        rightClickActionSettingsEditor.AcceptSavedSnapshot(json);
        switch ((web, operation))
        {
            case (true, "delete"): rightClickActionSettingsEditor.DeleteSelectedWebAction(); break;
            case (false, "delete"): rightClickActionSettingsEditor.DeleteSelectedProgramAction(); break;
            case (true, "up"): rightClickActionSettingsEditor.MoveSelectedWebActionUp(); break;
            case (true, "down"): rightClickActionSettingsEditor.MoveSelectedWebActionDown(); break;
            case (false, "up"): rightClickActionSettingsEditor.MoveSelectedProgramActionUp(); break;
            case (false, "down"): rightClickActionSettingsEditor.MoveSelectedProgramActionDown(); break;
            case (_, "defaults"): rightClickActionSettingsEditor.DiscardChanges(); break;
        }
    }

    /// <summary>右クリックの選択recordの入力だけを取消します。</summary>
    internal void CancelRightClickRecord(bool web) => rightClickActionSettingsEditor.DiscardSelectedRecord(ApplicationSettings.RightClickActionsJson, web);

    /// <summary>BMS局所確定の一覧だけを検証します。他セクションの候補は参照しません。</summary>
    internal bool CheckBmsRootsBeforeConfirmation(out string error)
    {
        IReadOnlyList<string> roots = AvailableBMSDirectories;
        if (NormalizeStandaloneBmsRootPaths(roots).Count == 0) { error = Resources.Error_InvalidStandaloneBmsRootPaths; return false; }
        if (ApplicationSettings.OperationModeLR2DB && CollectNestedBmsSearchRootConflicts(roots).Count > 0)
        { error = BuildNestedBmsSearchRootConflictMessage(CollectNestedBmsSearchRootConflicts(roots)); return false; }
        error = string.Empty;
        return true;
    }

    /// <summary>出力配置の局所確定に必要な関連組だけを検証します。</summary>
    internal bool CheckOutputBeforeConfirmation(out string error) => ValidateCustomFolderOutputBaseDir(out error)
        && ValidateCustomFolderAsRootOutputBaseDir(out error) && ValidateCustomFolderAdditionalOutputBaseDirs(out error);

}
