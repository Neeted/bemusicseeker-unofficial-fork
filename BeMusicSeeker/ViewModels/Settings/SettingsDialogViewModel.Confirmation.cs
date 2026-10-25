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
        RefreshBmsRootCandidatesFromSettings();
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

    /// <summary>確定rootを使うスコア候補だけに未保存差分があるかを示します。</summary>
    public bool HasScoreSelectionChanges => scoreUseBeatorajaScoreDb != ApplicationSettings.UseBeatorajaScoreDb
        || scoreBeatorajaPlayerId != ApplicationSettings.BeatorajaPlayerId
        || scoreBeatorajaScoreDbPath != ApplicationSettings.BeatorajaScoreDbPath;

    /// <summary>確定rootに対するスコア候補が有効かを示します。BMT候補は検査しません。</summary>
    public bool IsScoreSelectionValid => !scoreUseBeatorajaScoreDb
        || BeatorajaConfigService.IsBeatorajaRootPathValid(ApplicationSettings.BeatorajaRootPath)
            && IsBeatorajaScoreDbPathValid(scoreBeatorajaScoreDbPath);

    /// <summary>スコア候補の保存、又は保存後のスコア読込みだけを明示再試行できます。</summary>
    public bool CanRetryScoreSettings => IsEditCompletionEnabled && !statePort.IsLibraryOperationInProgress && IsScoreSelectionValid
        && (HasScoreSelectionChanges || scoreReloadPending);

    /// <summary>未了の旧新BMT又はスコア入力をroot変更で取り替えないようにします。</summary>
    public bool IsBeatorajaSelectionEnabled => IsSettingsOperationAvailable && IsBeatorajaSelectionCoreEnabled;

    private bool IsBeatorajaSelectionCoreEnabled => IsEnvironmentSelectionCoreEnabled && pendingBmtChange == null && !scoreReloadPending;

    /// <summary>スコア入力はBMTの未了修復から独立し、保存済みスコアの反映中だけ制限します。</summary>
    public bool IsScoreSelectionEnabled => IsEnvironmentSelectionEnabled && !scoreReloadPending;

    /// <summary>root変更に伴う旧新BMT反映を、rootの明示操作から修復できます。</summary>
    public bool CanRetryBeatorajaRootSettings => IsEditCompletionEnabled && !statePort.IsLibraryOperationInProgress
        && pendingBmtChange != null && pendingBmtChange.IsRootChange;

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
    internal Task StartConfirmedEnvironmentAsync() => RunSettingsOperationAsync(() => StartConfirmedEnvironmentCoreAsync(), requiresPresentation: true);

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task StartConfirmedEnvironmentCoreAsync()
    {
        if (statePort.HasActiveLibraryProfile || !IsEnvironmentSelectionCoreEnabled) { return; }
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

    // この設定担当の寿命内でだけ、保存済みBMT変更の旧新入力と成功段階を保持します。
    private sealed record BmtLocalChange(BeatorajaBmtOptionsSnapshot Previous, BeatorajaBmtOptionsSnapshot Target)
    {
        public bool PreviousUrlsCompleted { get; set; }
        public bool IsRootChange => !string.Equals(Previous.BeatorajaRootPath, Target.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase);
    }
    private BmtLocalChange pendingBmtChange;

    /// <summary>確定rootで解決したスコア項目だけを保存し、スコア読込みと必須表示の実終端を待ちます。</summary>
    internal Task CommitScoreSettingsAsync() => RunSettingsOperationAsync(() => CommitScoreSettingsCoreAsync());

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task CommitScoreSettingsCoreAsync()
    {
        if (!IsSectionCompletionEnabled || statePort.IsLibraryOperationInProgress) { return; }
        if (!IsScoreSelectionValid) { ShowSettingValidationError(Resources.Error_InvalidBeatorajaScoreDbPath); return; }
        if (!HasScoreSelectionChanges && !scoreReloadPending) { return; }
        bool useScore = scoreUseBeatorajaScoreDb;
        string player = scoreBeatorajaPlayerId;
        string scorePath = scoreBeatorajaScoreDbPath;
        IsEditCompletionInProgress = true;
        try
        {
            async Task<StartupInitializationResult> SaveAndReload(LibraryFileMutationCapability capability)
            {
                if (useScore != ApplicationSettings.UseBeatorajaScoreDb || player != ApplicationSettings.BeatorajaPlayerId || scorePath != ApplicationSettings.BeatorajaScoreDbPath)
                {
                    bool requiresReload = ApplicationSettings.UseBeatorajaScoreDb || useScore;
                    ApplicationSettings.Commit((nameof(Settings.UseBeatorajaScoreDb), useScore),
                        (nameof(Settings.BeatorajaPlayerId), player),
                        (nameof(Settings.BeatorajaScoreDbPath), scorePath));
                    if (statePort.HasActiveLibraryProfile && requiresReload) { SetScoreReloadPending(true); }
                    DiscardConflictingCandidates(ConflictingCandidates.BeatorajaScore);
                }
                return statePort.HasActiveLibraryProfile && scoreReloadPending ? await ReloadScoresOnlyAsync(capability) : null;
            }
            if (statePort.HasActiveLibraryProfile) { await RunLibrarySettingsOperationAsync(SaveAndReload); }
            else if (operationAdmission.CanEnter && customFolderOutputPort.CanBeginOutputOperation) { await SaveAndReload(null); }
        }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
        finally { IsEditCompletionInProgress = false; RaiseEnvironmentAvailabilityChanged(); }
    }

    /// <summary>新rootを独立確定し、確定済みスコア使用方針とBMT方針で両取得先を反映します。旧root前提の候補は採用点でだけ破棄します。</summary>
    internal Task CommitBeatorajaRootSettingsAsync(string root = null) => RunSettingsOperationAsync(() => CommitBeatorajaRootSettingsCoreAsync(root));

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task CommitBeatorajaRootSettingsCoreAsync(string root = null)
    {
        if (!IsSectionCompletionEnabled || statePort.IsLibraryOperationInProgress) { return; }
        bool retry = pendingBmtChange?.IsRootChange == true;
        if (!retry && !IsBeatorajaSelectionCoreEnabled) { return; }
        root ??= environmentBeatorajaRootPath;
        if (!retry && string.Equals(root, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)) { return; }
        string player = retry ? ApplicationSettings.BeatorajaPlayerId : ResolveRootPlayer(root);
        string scorePath = retry ? ApplicationSettings.BeatorajaScoreDbPath : BeatorajaConfigService.GetScoreDbPath(root, player);
        string tablePath = retry ? pendingBmtChange.Target.BeatorajaBmtTablePath : BeatorajaConfigService.GetTablePath(root);
        if (!retry)
        {
            if (!BeatorajaConfigService.IsBeatorajaRootPathValid(root)
                || ApplicationSettings.EnableBeatorajaBmtOutput && string.IsNullOrWhiteSpace(tablePath))
            { ShowSettingValidationError(Resources.Error_InvalidBeatorajaRootPath); return; }
            if (ApplicationSettings.UseBeatorajaScoreDb && !IsBeatorajaScoreDbPathValid(scorePath))
            { ShowSettingValidationError(Resources.Error_InvalidBeatorajaScoreDbPath); return; }
            if (!await ConfirmCandidateSwitchAsync(ConflictingCandidates.BeatorajaScore | ConflictingCandidates.Bmt)) { return; }
            if (!IsBeatorajaSelectionCoreEnabled) { return; }
        }
        IsEditCompletionInProgress = true;
        try
        {
            async Task<StartupInitializationResult> SaveAndReflect(LibraryFileMutationCapability capability)
            {
                if (!retry)
                {
                    if (!BeatorajaConfigService.IsBeatorajaRootPathValid(root)
                        || ApplicationSettings.EnableBeatorajaBmtOutput && string.IsNullOrWhiteSpace(tablePath))
                    { throw new InvalidOperationException(Resources.Error_InvalidBeatorajaRootPath); }
                    if (ApplicationSettings.UseBeatorajaScoreDb && !IsBeatorajaScoreDbPathValid(scorePath))
                    { throw new InvalidOperationException(Resources.Error_InvalidBeatorajaScoreDbPath); }
                    var previous = BeatorajaBmtOptionsSnapshot.CreateCurrent(ApplicationSettings);
                    ApplicationSettings.Commit((nameof(Settings.BeatorajaRootPath), root),
                        (nameof(Settings.BeatorajaPlayerId), player), (nameof(Settings.BeatorajaScoreDbPath), scorePath),
                        (nameof(Settings.BeatorajaBmtTablePath), tablePath));
                    // BMTが先に失敗しても必要なスコア反映の事実を失いません。
                    if (statePort.HasActiveLibraryProfile && ApplicationSettings.UseBeatorajaScoreDb) { SetScoreReloadPending(true); }
                    pendingBmtChange = new(previous, BeatorajaBmtOptionsSnapshot.CreateCurrent(ApplicationSettings));
                    DiscardConflictingCandidates(ConflictingCandidates.BeatorajaScore | ConflictingCandidates.Bmt);
                    environmentBeatorajaRootPath = root;
                    RefreshEnvironmentBeatorajaCandidate();
                    RaisePropertyChanged(nameof(BeatorajaRootPath));
                }
                await CompletePendingBmtChangeAsync(capability?.PlaylistCapability);
                return statePort.HasActiveLibraryProfile && scoreReloadPending ? await ReloadScoresOnlyAsync(capability) : null;
            }
            if (statePort.HasActiveLibraryProfile) { await RunLibrarySettingsOperationAsync(SaveAndReflect); }
            else if (operationAdmission.CanEnter && customFolderOutputPort.CanBeginOutputOperation) { await SaveAndReflect(null); }
        }
        catch (Exception exception) { if (!operationAdmission.IsAdmissionClosed) { reportApplyFailure(exception); } }
        finally { IsEditCompletionInProgress = false; RaiseBmtProcessingStateChanged(); }
    }

    private static string ResolveRootPlayer(string root)
    {
        List<string> players = BeatorajaConfigService.GetPlayerIds(root);
        string configured = BeatorajaConfigService.GetConfiguredPlayerId(root);
        return players.Contains(configured, StringComparer.OrdinalIgnoreCase) ? configured : players.FirstOrDefault() ?? string.Empty;
    }

    private async Task CompletePendingBmtChangeAsync(LibraryFileMutationCapability capability)
    {
        BmtLocalChange change = pendingBmtChange;
        if (change == null) { return; }
        if (statePort.HasActiveLibraryProfile)
        {
            bool keepDisabledOutput = !change.Target.EnableBeatorajaBmtOutput && change.Target.KeepBeatorajaBmtFilesWhenOutputDisabled;
            if (!change.PreviousUrlsCompleted)
            {
                if (!keepDisabledOutput && change.Previous.RegisterBeatorajaBmtUrls
                    && !string.IsNullOrWhiteSpace(change.Previous.BeatorajaBmtTablePath)
                    && (change.IsRootChange || !change.Target.EnableBeatorajaBmtOutput
                        || !string.Equals(change.Previous.BeatorajaBmtTablePath, change.Target.BeatorajaBmtTablePath, StringComparison.OrdinalIgnoreCase)))
                {
                    await Task.Run(() => BeatorajaConfigService.SyncTableUrls(change.Previous.BeatorajaRootPath, [],
                        BmtTableExportService.ReadManagedTableUrls(change.Previous.BeatorajaBmtTablePath).Select(entry => entry.Url)));
                }
                change.PreviousUrlsCompleted = true;
            }
            await workspacePort.ExportBeatorajaBmtAsync("settings BMT confirmation", change.Previous.BeatorajaBmtTablePath,
                capability, forceProjection: true, options: change.Target);
        }
        pendingBmtChange = null;
    }

    /// <summary>BMT候補の組だけを保存し、旧新出力先を保持して全表投影・旧先整理・URL反映を待ちます。保存後の再試行は同じ要求だけを修復します。</summary>
    internal Task CommitBmtSettingsAsync() => RunSettingsOperationAsync(() => CommitBmtSettingsCoreAsync());

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task CommitBmtSettingsCoreAsync()
    {
        if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress || statePort.IsLibraryOperationInProgress) { return; }
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
                    CancelBmtSettings();
                    BmtLocalChange change = pendingBmtChange;
                    if (string.Equals(environmentBeatorajaRootPath, change.Target.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase))
                    { beatorajaBmtTablePath = change.Target.BeatorajaBmtTablePath; }
                    RaisePropertyChanged(nameof(BeatorajaBmtTablePath));
                    RaisePropertyChanged(nameof(EnvironmentBeatorajaBmtTablePath));
                    await CompletePendingBmtChangeAsync(capability);
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
    private string replacementBmsInstallDir;

    /// <summary>他の操作が採用されたときだけ破棄する、未確定の設定範囲です。受理済みの修復状態は含めません。</summary>
    [Flags]
    internal enum ConflictingCandidates { None = 0, Bms = 1, Lr2Details = 2, BeatorajaScore = 4, Bmt = 8, Lr2Mode = 16, BeatorajaRoot = 32 }

    private ConflictingCandidates GetConflictingCandidates(ConflictingCandidates sections)
    {
        ConflictingCandidates result = ConflictingCandidates.None;
        if (sections.HasFlag(ConflictingCandidates.Bms) && (!GetConfirmedBmsRoots().SequenceEqual(AvailableBMSDirectories, StringComparer.OrdinalIgnoreCase) || !string.IsNullOrEmpty(replacementBmsInstallDir))) { result |= ConflictingCandidates.Bms; }
        if (sections.HasFlag(ConflictingCandidates.Lr2Details) && (CaptureEnvironmentCandidate() with { OperationModeLR2DB = ApplicationSettings.OperationModeLR2DB }) != EnvironmentSettingsChange.Capture(ApplicationSettings)) { result |= ConflictingCandidates.Lr2Details; }
        if (sections.HasFlag(ConflictingCandidates.BeatorajaScore) && HasScoreSelectionChanges) { result |= ConflictingCandidates.BeatorajaScore; }
        if (sections.HasFlag(ConflictingCandidates.Bmt) && HasBmtSelectionChanges) { result |= ConflictingCandidates.Bmt; }
        if (sections.HasFlag(ConflictingCandidates.Lr2Mode) && operationModeLR2DB != ApplicationSettings.OperationModeLR2DB) { result |= ConflictingCandidates.Lr2Mode; }
        if (sections.HasFlag(ConflictingCandidates.BeatorajaRoot) && !string.Equals(environmentBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)) { result |= ConflictingCandidates.BeatorajaRoot; }
        return result;
    }

    /// <summary>独立導入先の変更に、設定担当が所有するBMS候補破棄確認が必要かを示します。実処理のBusyは含めません。</summary>
    internal bool RequiresBmsInstallDirectoryConfirmation => GetConflictingCandidates(ConflictingCandidates.Bms) != ConflictingCandidates.None;

    /// <summary>競合する未確定範囲の名称を含む確認を作ります。受理済みの修復状態は案内の対象に含めません。</summary>
    private string BuildCandidateDiscardConfirmation(ConflictingCandidates sections)
    {
        ConflictingCandidates conflicts = GetConflictingCandidates(sections);
        if (conflicts == ConflictingCandidates.None) { return null; }
        var names = new List<string>();
        if (conflicts.HasFlag(ConflictingCandidates.Bms)) { names.Add(Resources.Standalone_BMSDirectories.Trim().TrimEnd(':', '：')); }
        if (conflicts.HasFlag(ConflictingCandidates.Lr2Details)) { names.Add(Resources.Settings_lr2_detailed_paths); }
        if (conflicts.HasFlag(ConflictingCandidates.BeatorajaScore)) { names.Add(Resources.Use_beatoraja_scoreDB); }
        if (conflicts.HasFlag(ConflictingCandidates.Bmt)) { names.Add(Resources.Use_beatoraja_bmt_output); }
        if (conflicts.HasFlag(ConflictingCandidates.Lr2Mode)) { names.Add(Resources.Operation_Mode_LR2); }
        if (conflicts.HasFlag(ConflictingCandidates.BeatorajaRoot)) { names.Add(Resources.FilePath_beatoraja_root); }
        return FormatResource(Resources.Settings_confirm_discard_conflicting_candidates, string.Join(" / ", names));
    }

    /// <summary>非同期操作の確認をUIを塞がずに待ちます。実際に破棄する範囲名を示し、候補破棄は採用点へ残します。</summary>
    internal async Task<bool> ConfirmCandidateSwitchAsync(ConflictingCandidates sections)
    {
        string message = BuildCandidateDiscardConfirmation(sections);
        return message == null || await ShowUiConfirmationAsync(message, "Settings candidate switch");
    }

    private async Task<bool> ShowUiConfirmationAsync(string message, string routeName)
        => ToUiConfirmationDecision(await schemaDialogs.ConfirmAsync(new BeMusicSeeker.Views.Dialogs.UiConfirmationRequest(
            message, Resources.Confirm, UiDialogButton.OKCancel, UiDialogIcon.Question)), routeName);

    /// <summary>実受付・最終検証後の採用点で、関係する未確定範囲だけを最新確定値へ戻します。</summary>
    internal void DiscardConflictingCandidates(ConflictingCandidates sections)
    {
        ConflictingCandidates conflicts = GetConflictingCandidates(sections);
        if (conflicts.HasFlag(ConflictingCandidates.Bms)) { CancelBmsRoots(); }
        if ((conflicts & (ConflictingCandidates.Lr2Details | ConflictingCandidates.Lr2Mode)) != 0) { CancelEnvironment(); }
        if (conflicts.HasFlag(ConflictingCandidates.BeatorajaRoot))
        {
            environmentBeatorajaRootPath = ApplicationSettings.BeatorajaRootPath;
            RefreshEnvironmentBeatorajaCandidate();
            RaisePropertyChanged(nameof(BeatorajaRootPath));
        }
        if (sections.HasFlag(ConflictingCandidates.BeatorajaScore))
        {
            scoreUseBeatorajaScoreDb = ApplicationSettings.UseBeatorajaScoreDb;
            scoreBeatorajaPlayerId = ApplicationSettings.BeatorajaPlayerId;
            scoreBeatorajaScoreDbPath = ApplicationSettings.BeatorajaScoreDbPath;
            RaisePropertyChanged(nameof(UseBeatorajaScoreDb));
            RaiseEnvironmentBeatorajaCandidateChanged();
        }
        if (conflicts.HasFlag(ConflictingCandidates.Bmt)) { CancelBmtSettings(); }
    }

    /// <summary>補助操作の既存確認へ、実行対象を変える未確定入力の破棄案内だけを併記します。</summary>
    internal string BuildAuxiliaryOperationConfirmation(string message, ConflictingCandidates sections)
    {
        string discard = BuildCandidateDiscardConfirmation(sections);
        return discard == null ? message : message + Environment.NewLine + Environment.NewLine + discard;
    }

    /// <summary>終了を伴う確認へ、未確定候補を次回へ引き継がない案内を統合します。</summary>
    internal string BuildExitOperationConfirmation(string message)
        => message + Environment.NewLine + Environment.NewLine + Resources.Settings_candidates_discarded_on_exit;

    /// <summary>復元後の終了で失われるBMT・スコアの保存後未了入力があれば復元を拒否します。出力とBMSの過去失敗は対象外です。</summary>
    internal async Task<bool> CanStartRestoreBeforeExitAsync()
    {
        if (pendingBmtChange == null && !scoreReloadPending) { return true; }
        await ShowUiMessageAsync(Resources.Settings_repair_related_save, Resources.Error, UiDialogIcon.Hand, "Restore pending settings repair");
        return false;
    }

    private async Task<bool> PrepareBmsCandidateEditAsync()
    {
        if (!IsBmsSearchRootEditorCoreEnabled) { return false; }
        if (!await ConfirmCandidateSwitchAsync(ConflictingCandidates.Lr2Details) || !IsBmsSearchRootEditorCoreEnabled) { return false; }
        return true;
    }

    private async Task<bool> PrepareLr2CandidateEditAsync()
    {
        if (!IsEnvironmentSelectionCoreEnabled) { return false; }
        if (!await ConfirmCandidateSwitchAsync(ConflictingCandidates.Bms) || !IsEnvironmentSelectionCoreEnabled) { return false; }
        return true;
    }

    /// <summary>保存済みスコアの読込み又は必須表示が未完了であることを示します。</summary>
    public bool IsScoreProcessingRetryRequired => scoreReloadPending && !IsEditCompletionInProgress;

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

    /// <summary>有効な今回入力を、公開値と同値でも関連媒体へ明示保存できます。</summary>
    public bool CanConfirmBmsRoots => IsEditCompletionEnabled && !statePort.IsLibraryOperationInProgress
        && CheckBmsRootsBeforeConfirmation(out _)
        && (!RequiresReplacementBmsInstallDir || AvailableBMSDirectories.Contains(replacementBmsInstallDir, StringComparer.OrdinalIgnoreCase));

    /// <summary>有効なBMT組の確定・全表再更新、または保存済み要求の明示再試行を受け付けます。未了要求は現在configの新規検証に依存させません。</summary>
    public bool CanConfirmBmtSettings => IsEnvironmentSelectionEnabled
        && (pendingBmtChange != null || !bmtEnableBeatorajaBmtOutput || BeatorajaConfigService.IsBeatorajaRootPathValid(ApplicationSettings.BeatorajaRootPath));

    /// <summary>BMTの未確定オプションだけに取消可能な差分があるかを示します。</summary>
    public bool CanCancelBmtSettings => IsSectionCompletionEnabled && HasBmtSelectionChanges;

    private bool HasBmtSelectionChanges => (bmtEnableBeatorajaBmtOutput != ApplicationSettings.EnableBeatorajaBmtOutput
        || bmtKeepBeatorajaBmtFilesWhenOutputDisabled != ApplicationSettings.KeepBeatorajaBmtFilesWhenOutputDisabled
        || bmtRegisterBeatorajaBmtUrls != ApplicationSettings.RegisterBeatorajaBmtUrls
        || bmtBeatorajaBmtHashOutputMode != ApplicationSettings.BeatorajaBmtHashOutputMode);


    /// <summary>導入先が最終候補一覧から外れるときだけ代替選択を表示します。</summary>
    public bool RequiresReplacementBmsInstallDir => !AvailableBMSDirectories.Contains(ApplicationSettings.BMSInstallDir, StringComparer.OrdinalIgnoreCase);

    /// <summary>実操作は編集中の環境候補ではなく保存済みのrootを使います。</summary>
    internal string ConfirmedBeatorajaRootPath => ApplicationSettings.BeatorajaRootPath;

    private void RaiseSectionRepairStateChanged()
    {
        RaiseEnvironmentAvailabilityChanged();
    }

    private IReadOnlyList<string> GetConfirmedBmsRoots()
    {
        if (!ApplicationSettings.OperationModeLR2DB)
        { return StandaloneBmsRootPathSettings.Deserialize(ApplicationSettings.StandaloneBmsRootPaths, ApplicationSettings.BMSRootPath); }
        Lr2ConfigSnapshot snapshot = GetConfirmedLr2Owner()?.Capture();
        if (snapshot == null) { return []; }
        CustomFolderOutputSettingsSnapshot placement = snapshot.Placement;
        IReadOnlyList<string> excluded = placement == null ? GetUserVisibleBmsSearchRootExcludedDirectories()
            : CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(new[] { placement.LR2CustomFolderOutputBaseDir,
                placement.LR2CustomFolderOutputBaseDirRootType }.Concat(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(placement.LR2CustomFolderAdditionalOutputBaseDirs)));
        return snapshot.RegisteredRoots.Where(path => !ContainsSameOrChildDirectory(excluded, path)).ToArray();
    }

    private IReadOnlyList<string> CaptureConfirmedManagedOutputRoots()
    {
        var managed = new List<string> { ApplicationSettings.LR2CustomFolderOutputBaseDir, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType };
        managed.AddRange(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(ApplicationSettings.LR2CustomFolderAdditionalOutputBaseDirs));
        return CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(managed);
    }

    /// <summary>今回のBMS一覧と明示代替導入先を捕捉し、同値でも関連媒体を再保存して公開後に必要な差分再読込みを実行します。</summary>
    internal Task CommitBmsRootsAsync() => RunSettingsOperationAsync(() => CommitBmsRootsCoreAsync());

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task CommitBmsRootsCoreAsync()
    {
        if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress || statePort.IsLibraryOperationInProgress) { return; }
        IReadOnlyList<string> roots = AvailableBMSDirectories.ToArray();
        string replacementInstall = replacementBmsInstallDir;
        if (!CheckBmsRootsBeforeConfirmation(out string rootError, roots))
        {
            ShowSettingValidationError(rootError);
            return;
        }
        string install = ApplicationSettings.BMSInstallDir;
        if (!roots.Contains(install, StringComparer.OrdinalIgnoreCase))
        {
            if (!roots.Contains(replacementInstall, StringComparer.OrdinalIgnoreCase))
            {
                ShowSettingValidationError(Resources.Error_InvalidBmsInstallDir);
                return;
            }
            install = replacementInstall;
        }
        if (!await ConfirmCandidateSwitchAsync(ConflictingCandidates.Lr2Details) || !IsSectionCompletionEnabled || statePort.IsLibraryOperationInProgress) { return; }
        IsEditCompletionInProgress = true;
        try
        {
            async Task<StartupInitializationResult> SaveAndReload(LibraryFileMutationCapability capability)
            {
                if (!CheckBmsRootsBeforeConfirmation(out string latestError, roots)) { ShowSettingValidationError(latestError); return null; }
                IReadOnlyList<string> previousUserRoots = GetConfirmedBmsRoots();
                if (!roots.Contains(install, StringComparer.OrdinalIgnoreCase))
                {
                    ShowSettingValidationError(Resources.Error_InvalidBmsInstallDir); return null;
                }
                DiscardConflictingCandidates(ConflictingCandidates.Lr2Details);
                if (ApplicationSettings.OperationModeLR2DB)
                {
                    Lr2ConfigOwner owner = GetConfirmedLr2Owner() ?? throw new InvalidOperationException(Resources.Error_InvalidLR2SongDbOrConfigPath);
                    owner.SaveUserRoots(roots, CaptureConfirmedManagedOutputRoots());
                    (string, object)[] values = new[] { (nameof(Settings.BMSInstallDir), (object)install) };
                    ApplicationSettings.Persist(values);
                    ApplicationSettings.Publish(values);
                    owner.PublishUserRoots(previousUserRoots, roots);
                }
                else
                {
                    (string, object)[] values = new[] { (nameof(Settings.StandaloneBmsRootPaths), (object)SerializeStandaloneBmsRootPaths(roots)),
                        (nameof(Settings.BMSRootPath), (object)roots[0]), (nameof(Settings.BMSInstallDir), (object)install) };
                    ApplicationSettings.Persist(values);
                    ApplicationSettings.Publish(values);
                }
                RaisePropertyChanged(nameof(ConfirmedBMSDirectories));
                RefreshBmsRootCandidatesFromSettings();
                ReplacementBmsInstallDir = null;
                if (statePort.HasActiveLibraryProfile)
                {
                    if (!ApplicationSettings.OperationModeLR2DB) { searchRootRuntimePort.ApplyStandaloneSearchTargets(roots); }
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

    /// <summary>BMS候補と局所代替導入先を最新公開値へ戻します。媒体の読取り・保存は行いません。</summary>
    internal void CancelBmsRoots()
    {
        replacementBmsInstallDir = null;
        IReadOnlyList<string> roots = GetConfirmedBmsRoots();
        BmsRootPathList.Clear();
        foreach (string root in roots) { BmsRootPathList.Add(root); }
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
            if (!IsLR2SongDBPathValid(settings.LR2SongDBPath) || !lr2ConfigSession.TryResolve(settings.LR2ConfigXmlPath, out _))
            { failures.Add(Resources.Error_InvalidLR2SongDbOrConfigPath); }
            if (string.IsNullOrWhiteSpace(settings.LR2CustomFolderOutputBaseDir)) { failures.Add(Resources.Error_CustomFolderOutputPathNotSet); }
            if (string.IsNullOrWhiteSpace(settings.LR2CustomFolderOutputBaseDirRootType)) { failures.Add(Resources.Error_CustomFolderRootOutputPathNotSet); }
            if (lr2ConfigSession.TryResolve(settings.LR2ConfigXmlPath, out Lr2ConfigOwner confirmedConfig))
            {
                IReadOnlyList<NestedBmsSearchRootConflict> conflicts = CollectNestedBmsSearchRootConflicts(confirmedConfig.Capture().RegisteredRoots);
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
                            confirmedConfig.Capture().RegisteredRoots, output.Label, allowRegisteredChildren: output.Root);
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
            if (!IsLR2PlayerRootPathValid(settings.LR2RootPath) || !lr2ConfigSession.TryResolve(settings.LR2ConfigXmlPath, out _)) { failures.Add(Resources.Error_InvalidLR2RootPath); }
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

    /// <summary>起動の準備・必須UI公開が終わり、LR2環境を編集できるかを示します。</summary>
    public bool IsEnvironmentSelectionEnabled => IsSettingsOperationAvailable && IsEnvironmentSelectionCoreEnabled;

    private bool IsEnvironmentSelectionCoreEnabled => IsSectionCompletionEnabled && !environmentTransitionInProgress
        && !statePort.IsLibraryOperationInProgress;

    /// <summary>確定済みの設定から初回開始を受け付けられるかを示します。</summary>
    public bool CanStartEnvironment => IsInitialEnvironment && IsEnvironmentSelectionEnabled
        && operationAdmission.CanEnter && customFolderOutputPort.CanBeginOutputOperation
        && CheckValidationForInitialization(out _);

    // 再起動はこの担当が保持する旧新BMT・スコア待ちの寿命を終えるため、LR2環境の確定だけを制限します。
    private bool IsEnvironmentRestartBlockedByRepair => statePort.HasActiveLibraryProfile && (pendingBmtChange != null || scoreReloadPending);

    /// <summary>LR2詳細の適用可能差分だけを確定できます。rootが空でも有効な個別パスを受理し、再起動で失われる未了修復がある間は確定しません。</summary>
    public bool CanConfirmEnvironment => HasEnvironmentChanges && IsEnvironmentSelectionEnabled
        && !IsEnvironmentRestartBlockedByRepair
        && operationAdmission.CanEnter && customFolderOutputPort.CanBeginOutputOperation
        && (!operationModeLR2DB || IsLR2SongDBPathValid(environmentLR2SongDBPath) && lr2ConfigSession.TryResolve(environmentLR2ConfigXmlPath, out _, adoptInitial: false));

    /// <summary>無効な候補も含め、LR2詳細だけを最新確定値へ戻せます。</summary>
    public bool CanCancelEnvironment => HasEnvironmentChanges && IsEditCompletionEnabled && !environmentTransitionInProgress;

    private void RaiseEnvironmentAvailabilityChanged()
    {
        RaisePropertyChanged(nameof(HasEnvironmentChanges));
        RaisePropertyChanged(nameof(CanConfirmEnvironment));
        RaisePropertyChanged(nameof(CanCancelEnvironment));
        RaisePropertyChanged(nameof(IsEnvironmentSelectionEnabled));
        RaisePropertyChanged(nameof(IsBeatorajaSelectionEnabled));
        RaisePropertyChanged(nameof(IsScoreSelectionEnabled));
        RaisePropertyChanged(nameof(CanRetryBeatorajaRootSettings));
        RaisePropertyChanged(nameof(CanStartEnvironment));
        RaisePropertyChanged(nameof(CanRetryScoreSettings));
        RaisePropertyChanged(nameof(IsInitialEnvironment));
        RaisePropertyChanged(nameof(CanConfirmBmsRoots));
        RaisePropertyChanged(nameof(CanCancelBmsRoots));
        RaisePropertyChanged(nameof(IsBmsInstallDirectorySelectionEnabled));
        RaisePropertyChanged(nameof(CanCancelBmtSettings));
        RaisePropertyChanged(nameof(CanConfirmBmtSettings));
    }

    private void SettingsOperationAvailabilityChanged(object sender, EventArgs args) => RaiseEnvironmentAvailabilityChanged();

    /// <summary>モードの実選択は保存済みLR2組で反映します。保存前の取消・拒否は確定値へ戻し、実保存の失敗では今回入力を残して明示再操作を可能にします。</summary>
    internal Task SelectOperationModeAsync(bool value)
    {
        if (!IsEnvironmentSelectionEnabled) { RestoreOperationModeSelection(); return Task.CompletedTask; }
        return RunSettingsOperationAsync(() => SelectOperationModeCoreAsync(value), requiresPresentation: true);
    }

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task SelectOperationModeCoreAsync(bool value)
    {
        if (!IsEnvironmentSelectionCoreEnabled) { return; }
        SetOperationModeSelection(value);
        bool saveStarted = false;
        try
        {
            await CommitEnvironmentChangeAsync(EnvironmentSettingsChange.Capture(ApplicationSettings) with { OperationModeLR2DB = value },
                replaceDetails: true, modeOnly: true, onSaveStarted: () => saveStarted = true);
        }
        finally
        {
            if (!environmentTransitionInProgress) { SetOperationModeSelection(saveStarted ? value : ApplicationSettings.OperationModeLR2DB); }
        }
    }

    /// <summary>標準パスを解決できた今回のrootだけを反映します。不正入力から前の候補を確定しません。</summary>
    internal Task SelectLr2RootAsync(string root) => RunSettingsOperationAsync(() => SelectLr2RootCoreAsync(root), requiresPresentation: true);

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal Task SelectLr2RootCoreAsync(string root)
    {
        if (!IsEnvironmentSelectionCoreEnabled || root == null) { return Task.CompletedTask; }
        if (!root.IsSjisSchemeString()) { ShowSettingValidationError(Resources.Error_LR2UnicodePathUnsupported); return Task.CompletedTask; }
        if (!TryBuildStandardLr2PathTuple(root, out string song, out string config, out _))
        {
            SetLr2PathSelectionError(Resources.Error_InvalidLR2RootPath);
            return Task.CompletedTask;
        }
        return CommitEnvironmentChangeAsync(new(ApplicationSettings.OperationModeLR2DB, root, song, config), replaceDetails: true);
    }

    /// <summary>個別song.db/XMLの候補を一回の選択保存・再起動で確定します。</summary>
    internal Task CommitEnvironmentAsync() => RunSettingsOperationAsync(() => CommitEnvironmentCoreAsync(), requiresPresentation: true);

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal Task CommitEnvironmentCoreAsync() => CommitEnvironmentChangeAsync(CaptureEnvironmentCandidate());

    /// <summary>環境の今回入力を確認・最終検証し、実受付後に保存します。再起動で失われる未了修復は確認前後と採用点で保護し、関係候補を先に破棄しません。</summary>
    /// <param name="onSaveStarted">保存前の拒否と実保存の失敗を区別するため、保存を開始する採用点だけで呼びます。</param>
    private async Task CommitEnvironmentChangeAsync(EnvironmentSettingsChange change, bool replaceDetails = false, bool modeOnly = false, Action onSaveStarted = null)
    {
        if (!IsEnvironmentSelectionCoreEnabled || !operationAdmission.CanEnter || !customFolderOutputPort.CanBeginOutputOperation) { return; }
        if (IsEnvironmentRestartBlockedByRepair)
        {
            ShowSettingValidationError(Resources.Settings_repair_related_save);
            return;
        }
        bool validConfig = lr2ConfigSession.TryResolve(change.LR2ConfigXmlPath, out Lr2ConfigOwner candidateOwner, adoptInitial: false);
        if (change.OperationModeLR2DB && (!IsLR2SongDBPathValid(change.LR2SongDBPath) || !validConfig))
        {
            ShowSettingValidationError(Resources.Error_InvalidLR2SongDbOrConfigPath);
            return;
        }
        if (change == EnvironmentSettingsChange.Capture(ApplicationSettings)) { return; }
        ConflictingCandidates conflicts = ConflictingCandidates.Bms | (replaceDetails ? ConflictingCandidates.Lr2Details : ConflictingCandidates.None);
        string confirmation = statePort.HasActiveLibraryProfile ? Resources.Confirm_RestartForOperationModeChange : null;
        string discardConfirmation = BuildCandidateDiscardConfirmation(conflicts);
        if (discardConfirmation != null)
        { confirmation = string.Join(Environment.NewLine + Environment.NewLine, new[] { confirmation, discardConfirmation }.Where(text => text != null)); }
        if (confirmation != null && !await ShowUiConfirmationAsync(confirmation, "Environment confirmation"))
        {
            SetOperationModeSelection(ApplicationSettings.OperationModeLR2DB);
            return;
        }
        if (!IsEnvironmentSelectionCoreEnabled || !operationAdmission.CanEnter || !customFolderOutputPort.CanBeginOutputOperation) { return; }
        if (IsEnvironmentRestartBlockedByRepair) { ShowSettingValidationError(Resources.Settings_repair_related_save); return; }
        change = modeOnly ? EnvironmentSettingsChange.Capture(ApplicationSettings) with { OperationModeLR2DB = change.OperationModeLR2DB }
            : change with { OperationModeLR2DB = ApplicationSettings.OperationModeLR2DB };
        if (change == EnvironmentSettingsChange.Capture(ApplicationSettings)) { SetOperationModeSelection(ApplicationSettings.OperationModeLR2DB); return; }
        if (change.OperationModeLR2DB && (!IsLR2SongDBPathValid(change.LR2SongDBPath)
            || !lr2ConfigSession.TryResolve(change.LR2ConfigXmlPath, out candidateOwner, adoptInitial: false))) { return; }
        if (!statePort.HasActiveLibraryProfile)
        {
            try
            {
                DiscardConflictingCandidates(conflicts);
                onSaveStarted?.Invoke();
                change.Commit(ApplicationSettings);
                if (candidateOwner != null) { lr2ConfigSession.Adopt(candidateOwner); }
                // 初回のモード・LR2 root保存も、次の初期化が捕捉する既存の確定配置へ同じ組を公開します。
                publishOutputPlacement();

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
                if (IsEnvironmentRestartBlockedByRepair) { throw new InvalidOperationException(Resources.Settings_repair_related_save); }
                // 確認中に先着したL/Pを再検査し、保存の先着判断だけをshellへ渡します。
                // 再起動の終端は待たず、受理Taskが戻った時点で解放して終了joinの自己待機を避けます。
                accepted = await requestOperationModeRestart(new OperationModeRestartRequest(change,
                    playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity, () =>
                    {
                        // UIへの配送後も、候補破棄・保存に先立ち修復情報の寿命を再検査します。
                        if (IsEnvironmentRestartBlockedByRepair) { throw new InvalidOperationException(Resources.Settings_repair_related_save); }
                        DiscardConflictingCandidates(conflicts);
                        onSaveStarted?.Invoke();
                    }));
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
        isLr2ConfigPathParsed = lr2ConfigSession.TryResolve(environmentLR2ConfigXmlPath, out _);
        SetLr2PathSelectionErrorWithoutNotification(string.Empty);
        RaisePropertyChanged(nameof(OperationModeLR2DB));
        RaisePropertyChanged(nameof(IsStandaloneOperationMode));
        RaisePropertyChanged(nameof(LR2RootPath));
        RaisePropertyChanged(nameof(LR2SongDBPath));
        RaisePropertyChanged(nameof(LR2ConfigXmlPath));
        RaiseLr2PathPresentationChanged();
        RaiseEnvironmentAvailabilityChanged();
    }

    /// <summary>root候補を採用した場合のplayerを表示し、独立したスコア候補を変更しません。</summary>
    public string EnvironmentBeatorajaPlayerId => string.Equals(environmentBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
        ? ApplicationSettings.BeatorajaPlayerId : ResolveRootPlayer(environmentBeatorajaRootPath);

    /// <summary>root候補から解決したscore.dbを詳細欄へ表示します。</summary>
    public string EnvironmentBeatorajaScoreDbPath => BeatorajaConfigService.GetScoreDbPath(environmentBeatorajaRootPath, EnvironmentBeatorajaPlayerId);

    /// <summary>root候補を選択した時点のBMT出力先を表示します。</summary>
    public string EnvironmentBeatorajaBmtTablePath => beatorajaBmtTablePath;

    private void RefreshEnvironmentBeatorajaCandidate()
    {
        beatorajaBmtTablePath = pendingBmtChange?.Target.BeatorajaBmtTablePath
            ?? (string.Equals(environmentBeatorajaRootPath, ApplicationSettings.BeatorajaRootPath, StringComparison.OrdinalIgnoreCase)
                ? ApplicationSettings.BeatorajaBmtTablePath : BeatorajaConfigService.GetTablePath(environmentBeatorajaRootPath));
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
                || !lr2ConfigSession.TryResolve(ApplicationSettings.LR2ConfigXmlPath, out _)
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

    /// <summary>今回入力と他区分の最新確定値を保存し、必要header DBの成功後に公開して現在正本から生成します。過去要求は保持しません。</summary>
    /// <param name="section">normal・root・additionalは当該区分だけを扱い、他区分は最新確定値を使います。</param>
    internal Task CommitOutputSettingsAsync(string section = null) => RunSettingsOperationAsync(() => CommitOutputSettingsCoreAsync(section));

    /// <summary>受理済み設定操作として実行し、設定の単一受付を再取得しません。</summary>
    internal async Task CommitOutputSettingsCoreAsync(string section = null)
    {
        if (section is not (null or "normal" or "root" or "additional")) { throw new ArgumentOutOfRangeException(nameof(section)); }
        bool accepted = false;
        try
        {
            if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress || statePort.IsLibraryOperationInProgress) { return; }
            var previous = CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings);
            CustomFolderOutputSettingsSnapshot baseline = previous;
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
            var requestedRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (customFolderAdditionalOutputBaseRename is { } rename) { requestedRenames.Add(rename.Key, rename.Value); }
            bool ValidateTarget()
            {
                try
                {
                    ValidateOutputBasePreservesInstallDirectory(target.LR2CustomFolderOutputBaseDir);
                    ValidateOutputBasePreservesInstallDirectory(target.LR2CustomFolderOutputBaseDirRootType);
                    foreach (string path in GetOutputAdditionalPaths(target)) { ValidateOutputBasePreservesInstallDirectory(path); }
                }
                catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.PathTooLongException)
                { ShowSettingValidationError(failure.Message); return false; }
                // 初回は未設定の別区分を残して順に保存できます。選択対象と設定済みの配置は検証し、開始時は両必須値を別途検証します。
                bool requireNormal = statePort.HasActiveLibraryProfile || section is null or "normal" || !string.IsNullOrWhiteSpace(target.LR2CustomFolderOutputBaseDir);
                bool requireRoot = statePort.HasActiveLibraryProfile || section is null or "root" || !string.IsNullOrWhiteSpace(target.LR2CustomFolderOutputBaseDirRootType);
                string error;
                if ((requireNormal && !ValidateCustomFolderOutputBaseDir(target.LR2CustomFolderOutputBaseDir, out error, target: target))
                    || (requireRoot && !ValidateCustomFolderAsRootOutputBaseDir(target.LR2CustomFolderOutputBaseDirRootType, out error, target: target))
                    || !ValidateCustomFolderAdditionalOutputBaseDirs(out error, target)) { ShowSettingValidationError(error); return false; }
                return true;
            }
            if (!ValidateTarget()) { return; }
            if (section == "additional" && SameOutputPlacement(previous, target) && requestedRenames.Count == 0) { return; }
            if (!await ConfirmCustomFolderOutputChangesBeforeSaveAsync(target)) { return; }
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
                        if (!ValidateTarget()) { return; }
                        IReadOnlyDictionary<string, string> renames = section is null or "additional"
                            ? requestedRenames : new Dictionary<string, string>();
                        customFolderOutputPort.ValidateCustomFolderOutputRegistration(target, renames);
                        DiscardConflictingCandidates(ConflictingCandidates.Bms | ConflictingCandidates.Lr2Details);
                        accepted = true;
                        (string Name, object Value)[] values = [
                            (nameof(Settings.LR2CustomFolderOutputBaseDir), target.LR2CustomFolderOutputBaseDir),
                            (nameof(Settings.LR2CustomFolderOutputBaseDirRootType), target.LR2CustomFolderOutputBaseDirRootType),
                            (nameof(Settings.LR2CustomFolderAdditionalOutputBaseDirs), target.LR2CustomFolderAdditionalOutputBaseDirs)];
                        ApplicationSettings.Persist(values);
                        Lr2RootChange rootChange = null;
                        if (ApplicationSettings.OperationModeLR2DB)
                        {
                            try
                            {
                                Lr2ConfigOwner owner = GetConfirmedLr2Owner() ?? throw new InvalidOperationException(Resources.Error_InvalidLR2SongDbOrConfigPath);
                                IReadOnlyList<string> before = owner.Capture().RegisteredRoots;
                                var latest = LR2Config.CreateRootPlanningDocument(owner.ConfigFilePath, before);
                                if (statePort.HasActiveLibraryProfile)
                                {
                                    latest.SetBMSSearchDirectories(customFolderOutputPort.PrepareCustomFolderOutputSearchRoots(
                                        previous.LR2CustomFolderOutputBaseDirRootType, target, before, renames));
                                }
                                // 初回ROOT選択は未修復の通常先に触れず、関係する登録だけを準備してXML自体は必ず再保存します。
                                if (statePort.HasActiveLibraryProfile || section is null or "normal" or "additional")
                                {
                                    CustomFolderOutputBaseSearchRootSyncPlan plan = CustomFolderOutputBaseSearchRootSyncService.PrepareNormalOutputBaseRoots(latest,
                                        previous.LR2CustomFolderOutputBaseDir, target.LR2CustomFolderOutputBaseDir,
                                        previous.LR2CustomFolderAdditionalOutputBaseDirs, target.LR2CustomFolderAdditionalOutputBaseDirs, []);
                                    CustomFolderOutputBaseSearchRootSyncService.CompleteAdditionalOutputBaseRootSync(latest, plan);
                                }
                                rootChange = owner.SaveOutputRoots(before, latest.GetBMSSearchDirectoriesForChangeTracking());
                            }
                            catch (Exception failure) { throw new PartialSettingsSaveException(ApplicationSettings.LR2ConfigXmlPath, failure); }
                        }
                        IReadOnlyList<PlaylistCustomFolderOutputMaintenanceOwner.PreviousOutputLocation> previousLocations = [];
                        if (statePort.HasActiveLibraryProfile)
                        {
                            previousLocations = await Task.Run(() => customFolderOutputPort.CommitCustomFolderOutputSettings(previous, target, renames, capability));
                        }
                        ApplicationSettings.Publish(values);
                        AdoptPublishedOutputCandidates(section, target);
                        publishOutputPlacement();
                        if (rootChange != null) { GetConfirmedLr2Owner().Publish(rootChange, target); }
                        CancelBmsRoots();
                        RaisePropertyChanged(nameof(ConfirmedBMSDirectories));
                        if (statePort.HasActiveLibraryProfile)
                        {
                            await Task.Run(() => customFolderOutputPort.RegenerateCustomFolderOutputs(previousLocations, target, capability));
                            using LibraryFileMutationCapability primary = operationAdmission.CreateMutationCapability(lease);
                            await lr2SongDbSyncWorkflow.SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChangeAsync(
                                "output settings confirmation", primary, capability);
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
        finally
        {
            if (!accepted) { RestoreUnacceptedOutputCandidates(); }
            else if (section is null or "additional")
            {
                // 即時確定した追加操作の対応は終端で捨て、保存失敗後も次の操作を公開済みの名前から始めます。
                AdoptPublishedOutputCandidates("additional", CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings));
            }
        }
    }

    // 保存前に再表示した欄も採用済み配置へ揃えます。別区分の未確定候補は触りません。
    private void AdoptPublishedOutputCandidates(string section, CustomFolderOutputSettingsSnapshot target)
    {
        if (section is null or "normal")
        {
            outputLR2CustomFolderOutputBaseDir = target.LR2CustomFolderOutputBaseDir;
            RaisePropertyChanged(nameof(LR2CustomFolderOutputDir));
        }
        if (section is null or "root")
        {
            outputLR2CustomFolderOutputBaseDirRootType = target.LR2CustomFolderOutputBaseDirRootType;
            RaisePropertyChanged(nameof(LR2CustomFolderAsRootOutputDir));
        }
        if (section is null or "additional")
        {
            outputLR2CustomFolderAdditionalOutputBaseDirs = target.LR2CustomFolderAdditionalOutputBaseDirs;
            RefreshCustomFolderAdditionalOutputBaseDirsFromSettings();
        }
        RaiseValidationStateChanged();
    }

    /// <summary>未受理の出力候補だけを現在の公開済み配置へ戻します。</summary>
    internal void RestoreUnacceptedOutputCandidates()
    {
        var target = CustomFolderOutputSettingsSnapshot.CreateCurrent(ApplicationSettings);
        outputLR2CustomFolderOutputBaseDir = target.LR2CustomFolderOutputBaseDir;
        outputLR2CustomFolderOutputBaseDirRootType = target.LR2CustomFolderOutputBaseDirRootType;
        outputLR2CustomFolderAdditionalOutputBaseDirs = target.LR2CustomFolderAdditionalOutputBaseDirs;
        RefreshCustomFolderAdditionalOutputBaseDirsFromSettings();
        RaisePropertyChanged(nameof(LR2CustomFolderOutputDir));
        RaisePropertyChanged(nameof(LR2CustomFolderAsRootOutputDir));
        RaiseValidationStateChanged();
        RaiseSectionRepairStateChanged();
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
    /// <param name="error">既存のBMS入力検証で見つかった理由。</param>
    /// <param name="roots">操作が捕捉した一覧。省略時は表示候補を使い、採用時は他の候補へ読み替えません。</param>
    internal bool CheckBmsRootsBeforeConfirmation(out string error, IReadOnlyList<string> roots = null)
    {
        roots ??= AvailableBMSDirectories;
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
