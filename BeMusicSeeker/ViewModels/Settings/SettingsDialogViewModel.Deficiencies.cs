using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.ViewModels;

public partial class SettingsDialogViewModel
{
    private IReadOnlyList<SettingsDeficiency> deficiencies = [];
    private SettingsKeyedText deficiencyCategoryMarks;
    private SettingsKeyedText fieldReasons;
    private SettingsKeyedText fieldReasonStatuses;

    /// <summary>
    /// 現在公開している不備の一覧です。確定値だけから作り、局所候補を読みません。
    /// 開始ボタンの可否、案内欄、ページの印、欄の理由はこの一覧を使い、判定を重複させません。
    /// </summary>
    public IReadOnlyList<SettingsDeficiency> Deficiencies => deficiencies;

    /// <summary>公開中の一覧に不備があり、案内欄を表示するかを示します。</summary>
    public bool HasDeficiencies => deficiencies.Count > 0;

    /// <summary>
    /// ページ一覧の印です。鍵は <see cref="SettingsCategory"/> の列挙名で、不備のあるページだけ支援技術が読める状態の文言を返します。
    /// </summary>
    public SettingsKeyedText DeficiencyCategoryMarks => deficiencyCategoryMarks ??= new SettingsKeyedText(key =>
        Enum.TryParse(key, out SettingsCategory category) && deficiencies.Any(item => item.Category == category)
            ? Resources.Settings_deficiency_page_mark : string.Empty);

    /// <summary>
    /// 欄の直下に出す理由です。鍵は <see cref="SettingsDeficiencyField"/> の列挙名です。
    /// 同じ欄の既存の検証表示、未成立の局所候補の理由、一覧の理由を同じ文言は一度だけにして改行で並べます。
    /// </summary>
    public SettingsKeyedText FieldReasons => fieldReasons ??= new SettingsKeyedText(key =>
        Enum.TryParse(key, out SettingsDeficiencyField target) ? GetFieldReason(target) : string.Empty);

    /// <summary>欄の理由の検証表示状態です。一覧または未成立の候補の理由があれば <c>Error</c>、なければ既存の検証表示の状態を返します。</summary>
    public SettingsKeyedText FieldReasonStatuses => fieldReasonStatuses ??= new SettingsKeyedText(key =>
        Enum.TryParse(key, out SettingsDeficiencyField target) ? GetFieldReasonStatus(target) : string.Empty);

    /// <summary>確定値から一覧を作り、設定画面へ公開します。作り直しの時機は表示開始、確定値を変えた操作の終端、表示言語の変更、起動時検査の終端です。</summary>
    /// <returns>公開した一覧。</returns>
    internal IReadOnlyList<SettingsDeficiency> RefreshDeficiencies()
    {
        return PublishDeficiencies(BuildDeficiencies());
    }

    /// <summary>
    /// 起動時検査の終端で一覧を作り直して公開します。<c>PrepareDirectoriesAsync</c> が設定XMLを解決できなかった場合は、
    /// 一覧の生成がその失敗を検出できなくても一般／LR2環境の項目を先頭に補い、警告の行と開始ボタンの可否を失敗と揃えます。
    /// </summary>
    /// <param name="lr2ConfigResolved">起動時の検査が設定XMLを解決できた場合はtrue。</param>
    /// <returns>公開した一覧。</returns>
    internal IReadOnlyList<SettingsDeficiency> RefreshStartupDeficiencies(bool lr2ConfigResolved)
    {
        IReadOnlyList<SettingsDeficiency> current = BuildDeficiencies();
        if (!lr2ConfigResolved && !current.Any(item => item.Field == SettingsDeficiencyField.Lr2Environment))
        {
            // 一般はページ一覧の先頭、LR2環境は一般の検査順の先頭のため、先頭へ補えば並びを保ちます。
            current = [new SettingsDeficiency(SettingsCategory.General, SettingsDeficiencyField.Lr2Environment, Resources.Error_InvalidLR2SongDbOrConfigPath), .. current];
        }
        return PublishDeficiencies(current);
    }

    /// <summary>
    /// 作った一覧を公開し、案内欄・ページの印・欄の理由・開始ボタンの可否を同じ一覧へ揃えます。
    /// 内容（ページ・欄・理由・行の文言）が公開中の一覧と同じなら差し替えず、案内欄の行の作り直しでスクロール位置とフォーカスを失わないようにします。
    /// </summary>
    /// <param name="current">作った一覧。</param>
    /// <returns>公開中の一覧。内容が同じなら従来の一覧のままです。</returns>
    private IReadOnlyList<SettingsDeficiency> PublishDeficiencies(IReadOnlyList<SettingsDeficiency> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!HaveSameContent(deficiencies, current))
        {
            deficiencies = current;
            RaisePropertyChanged(nameof(Deficiencies));
            RaisePropertyChanged(nameof(HasDeficiencies));
            deficiencyCategoryMarks?.Refresh();
        }
        // 開始ボタンと欄の理由は一覧以外の状態にも依存するため、内容が同じでも作り直しの時機に通知します。
        RaisePropertyChanged(nameof(CanStartEnvironment));
        RaiseFieldReasonsChanged();
        return deficiencies;
    }

    private static bool HaveSameContent(IReadOnlyList<SettingsDeficiency> published, IReadOnlyList<SettingsDeficiency> current)
    {
        if (published.Count != current.Count) { return false; }
        for (int index = 0; index < current.Count; index++)
        {
            SettingsDeficiency left = published[index];
            SettingsDeficiency right = current[index];
            if (left.Category != right.Category || left.Field != right.Field
                || !string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
                || !string.Equals(left.DisplayText, right.DisplayText, StringComparison.Ordinal)) { return false; }
        }
        return true;
    }

    private void RaiseFieldReasonsChanged()
    {
        fieldReasons?.Refresh();
        fieldReasonStatuses?.Refresh();
    }

    /// <summary>
    /// 確定値（<c>Settings</c> と採用済みのLR2設定XML）だけから、利用開始前の検査と同じ成立条件で不備の一覧を作ります。公開はしません。
    /// 並びはページ一覧の順、同じページ内は検査順です。
    /// </summary>
    /// <returns>不備がなければ空の一覧。</returns>
    internal IReadOnlyList<SettingsDeficiency> BuildDeficiencies()
    {
        var items = new List<SettingsDeficiency>();
        void Add(SettingsCategory category, SettingsDeficiencyField field, string reason) => items.Add(new SettingsDeficiency(category, field, reason));
        Settings settings = ApplicationSettings;
        if (settings.OperationModeLR2DB)
        {
            // 起動時の PrepareDirectoriesAsync と同じ設定XMLの解決を検査し、表示開始時の作り直しでもこの項目を保ちます。
            bool configResolved = lr2ConfigSession.TryResolve(settings.LR2ConfigXmlPath, ConfirmedEffectiveLr2RootPath, out Lr2ConfigOwner confirmedConfig);
            if (!IsLR2SongDBPathValid(settings.LR2SongDBPath) || !configResolved)
            { Add(SettingsCategory.General, SettingsDeficiencyField.Lr2Environment, Resources.Error_InvalidLR2SongDbOrConfigPath); }
            if (string.IsNullOrWhiteSpace(settings.LR2CustomFolderOutputBaseDir))
            { Add(SettingsCategory.Playlist, SettingsDeficiencyField.NormalOutput, Resources.Error_CustomFolderOutputPathNotSet); }
            if (string.IsNullOrWhiteSpace(settings.LR2CustomFolderOutputBaseDirRootType))
            { Add(SettingsCategory.Playlist, SettingsDeficiencyField.RootOutput, Resources.Error_CustomFolderRootOutputPathNotSet); }
            if (configResolved)
            {
                IReadOnlyList<string> registeredRoots = confirmedConfig.Capture().RegisteredRoots;
                IReadOnlyList<NestedBmsSearchRootConflict> conflicts = CollectNestedBmsSearchRootConflicts(registeredRoots);
                if (conflicts.Count > 0)
                { Add(SettingsCategory.General, SettingsDeficiencyField.BmsDirectories, BuildNestedBmsSearchRootConflictMessage(conflicts)); }
                // 出力先の検査例外は原因の出力先の欄へ割り当てます。重複・重なりは後に検査した側です。
                SettingsDeficiencyField current = SettingsDeficiencyField.AdditionalOutput;
                try
                {
                    var bases = new List<(string Path, string Label, bool Root, SettingsDeficiencyField Field)> {
                        (settings.LR2CustomFolderOutputBaseDir, Resources.Label_NormalOutputBase, false, SettingsDeficiencyField.NormalOutput),
                        (settings.LR2CustomFolderOutputBaseDirRootType, Resources.Label_RootOutputBase, true, SettingsDeficiencyField.RootOutput) };
                    bases.AddRange(CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(settings.LR2CustomFolderAdditionalOutputBaseDirs)
                        .Select(path => (path, Resources.Label_AdditionalOutputBaseFolder, false, SettingsDeficiencyField.AdditionalOutput)));
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int index = 0; index < bases.Count; index++)
                    {
                        (string Path, string Label, bool Root, SettingsDeficiencyField Field) output = bases[index];
                        current = output.Field;
                        CustomFolderOutputBaseSearchRootSyncService.ValidateSjisDirectoryPath(output.Path, output.Label);
                        CustomFolderOutputBaseSearchRootSyncService.ValidateOutputBaseAgainstSearchRoots(output.Path,
                            registeredRoots, output.Label, allowRegisteredChildren: output.Root);
                        if (!output.Root && !names.Add(CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(output.Path)))
                        {
                            Add(SettingsCategory.Playlist, output.Field, FormatResource(Resources.Validation_OutputBaseNameDuplicateFormat,
                                CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(output.Path)));
                        }
                        foreach ((string Path, string Label, bool Root, SettingsDeficiencyField Field) other in bases.Take(index))
                        { ValidateOutputBasesDoNotOverlap(output.Path, output.Label, other.Path, other.Label); }
                    }
                }
                catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or NotSupportedException or System.IO.PathTooLongException)
                { Add(SettingsCategory.Playlist, current, failure.Message); }
            }
        }
        else
        {
            IReadOnlyList<string> roots = NormalizeStandaloneBmsRootPaths(StandaloneBmsRootPathSettings.Deserialize(settings.StandaloneBmsRootPaths, settings.BMSRootPath));
            if (roots.Count == 0) { Add(SettingsCategory.General, SettingsDeficiencyField.BmsDirectories, Resources.Error_InvalidStandaloneBmsRootPaths); }
            List<string> incompatible = GetLr2IncompatibleStandaloneBmsRootPaths(roots);
            if (incompatible.Count > 0)
            {
                Add(SettingsCategory.General, SettingsDeficiencyField.BmsDirectories,
                    Resources.Error_Lr2IncompatibleBmsRootPath + Environment.NewLine + string.Join(Environment.NewLine, incompatible));
            }
        }
        bool beatorajaRootValid = BeatorajaConfigService.IsBeatorajaRootPathValid(settings.BeatorajaRootPath);
        // スコアDBは確定ルートから解決するため、ルートが無効な間はルートの項目だけを示します。
        if (settings.UseBeatorajaScoreDb && beatorajaRootValid && !IsBeatorajaScoreDbPathValid(settings.BeatorajaScoreDbPath))
        { Add(SettingsCategory.General, SettingsDeficiencyField.BeatorajaScoreDb, Resources.Error_InvalidBeatorajaScoreDbPath); }
        if ((settings.UseBeatorajaScoreDb || settings.EnableBeatorajaBmtOutput) && !beatorajaRootValid)
        { Add(SettingsCategory.General, SettingsDeficiencyField.BeatorajaRoot, Resources.Error_InvalidBeatorajaRootPath); }
        if (settings.EnableBeatorajaBmtOutput && beatorajaRootValid && string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(settings.BeatorajaRootPath)))
        { Add(SettingsCategory.General, SettingsDeficiencyField.BeatorajaBmtOutput, Resources.Error_InvalidBeatorajaBmtTablePath); }
        if (settings.UseExternalPanelImage && !IsStagefilePathValid(settings.StagefilePath))
        { Add(SettingsCategory.Appearance, SettingsDeficiencyField.ExternalImage, Resources.Error_InvalidStagefilePath); }
        if (settings.UsePlayeruBMplay && !IsuBMplayPathValid(settings.uBMplayPath))
        { Add(SettingsCategory.Playback, SettingsDeficiencyField.PlayeruBMplay, Resources.Error_InvalidUBMPlayExecutablePath); }
        if (settings.UsePlayerBMIIDXView && !IsBMIIDXViewPathValid(settings.BMIIDXViewPath))
        { Add(SettingsCategory.Playback, SettingsDeficiencyField.PlayerBMIIDXView, Resources.Error_InvalidBMIIDXViewPath); }
        if (settings.UsePlayerLR2body)
        {
            if (!IsLR2PlayerRootPathValid(ConfirmedEffectiveLr2RootPath) || !lr2ConfigSession.TryResolve(settings.LR2ConfigXmlPath, ConfirmedEffectiveLr2RootPath, out _))
            { Add(SettingsCategory.Playback, SettingsDeficiencyField.PlayerLR2body, Resources.Error_InvalidLR2RootPath); }
            string executable = StartupSettingsSnapshot.CreateCurrent(settings).LR2bodyPath;
            if (!System.IO.File.Exists(executable))
            { Add(SettingsCategory.Playback, SettingsDeficiencyField.PlayerLR2body, FormatResource(Resources.Error_LR2ExecutableNotFoundFormat, executable)); }
            if ((int)settings.LR2bodyResolution.X <= 0 || (int)settings.LR2bodyResolution.Y <= 0)
            { Add(SettingsCategory.Playback, SettingsDeficiencyField.PlayerLR2body, Resources.Error_InvalidLR2WindowSize); }
        }
        if (!IsBMSInstallDirValid(settings.BMSInstallDir))
        { Add(SettingsCategory.Install, SettingsDeficiencyField.InstallDirectory, Resources.Error_InvalidBmsInstallDir); }
        if (settings.IsLR2BackupEnabled && settings.OperationModeLR2DB && !IsLR2BackupPathValid(settings.LR2BackupPath))
        { Add(SettingsCategory.Backup, SettingsDeficiencyField.Lr2BackupOutput, Resources.Error_LR2BackupPathNotSet); }
        // 並び替えは安定なため、同じページ内は上の検査順を保ちます。
        return items.OrderBy(item => item.Category).ToArray();
    }

    /// <summary>欄の既存の検証表示・未成立の局所候補・一覧の理由を、同じ文言は一度だけ並べます。</summary>
    private string GetFieldReason(SettingsDeficiencyField field)
    {
        IEnumerable<string> reasons = new[] { GetExistingFieldValidation(field).Message, GetCandidateUnmetReason(field) }
            .Concat(deficiencies.Where(item => item.Field == field).Select(item => item.Reason));
        return string.Join(Environment.NewLine, reasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.Ordinal));
    }

    private string GetFieldReasonStatus(SettingsDeficiencyField field)
        => !string.IsNullOrEmpty(GetCandidateUnmetReason(field)) || deficiencies.Any(item => item.Field == field)
            ? "Error" : GetExistingFieldValidation(field).Status;

    /// <summary>同じ欄に以前からある検証表示です。理由の二重表示を避けるため、欄の理由へ統合します。</summary>
    private (string Message, string Status) GetExistingFieldValidation(SettingsDeficiencyField field) => field switch
    {
        SettingsDeficiencyField.Lr2Environment => (Lr2RootPathValidationMessage, Lr2RootPathValidationStatus),
        SettingsDeficiencyField.NormalOutput => (CustomFolderOutputDirValidationMessage, CustomFolderOutputDirValidationStatus),
        SettingsDeficiencyField.RootOutput => (CustomFolderRootOutputDirValidationMessage, CustomFolderRootOutputDirValidationStatus),
        SettingsDeficiencyField.InstallDirectory => (BmsInstallDirValidationMessage, BmsInstallDirValidationStatus),
        _ => (string.Empty, string.Empty)
    };

    /// <summary>
    /// 前提を要する有効化チェックの局所候補が組として成立していない理由です。成立・チェックを外す・取消・再表示で消えます。
    /// 未成立の候補は保存しないため、一覧には影響しません。
    /// </summary>
    private string GetCandidateUnmetReason(SettingsDeficiencyField field) => field switch
    {
        SettingsDeficiencyField.BeatorajaScoreDb => ScoreCandidateUnmetReason,
        SettingsDeficiencyField.BeatorajaBmtOutput => BmtCandidateUnmetReason,
        SettingsDeficiencyField.ExternalImage => imageUseExternalPanelImage && !IsStagefilePathValid(imageStagefilePath) ? Resources.Error_InvalidStagefilePath : null,
        SettingsDeficiencyField.PlayeruBMplay => externalPlayerUsePlayeruBMplay ? ExternalPlayerValidationError : null,
        SettingsDeficiencyField.PlayerBMIIDXView => externalPlayerUsePlayerBMIIDXView ? ExternalPlayerValidationError : null,
        SettingsDeficiencyField.PlayerLR2body => externalPlayerUsePlayerLR2body ? ExternalPlayerValidationError : null,
        SettingsDeficiencyField.Lr2BackupOutput => lr2BackupEnabledCandidate && ApplicationSettings.OperationModeLR2DB
            && !IsLR2BackupPathValid(ApplicationSettings.LR2BackupPath) ? Resources.Error_LR2BackupPathNotSet : null,
        _ => null
    };

    /// <summary>スコアの候補が未成立の理由です。確定ルートが無効ならルート、それ以外はスコアDBの理由です。</summary>
    private string ScoreCandidateUnmetReason => IsScoreSelectionValid ? null
        : BeatorajaConfigService.IsBeatorajaRootPathValid(ApplicationSettings.BeatorajaRootPath)
            ? Resources.Error_InvalidBeatorajaScoreDbPath : Resources.Error_InvalidBeatorajaRootPath;

    /// <summary>BMTの候補が未成立の理由です。確定ルートが無効ならルート、表の出力先が得られなければ表の出力先の理由です。確定ボタンの可否と確定時の検査も同じ条件を使います。</summary>
    private string BmtCandidateUnmetReason
    {
        get
        {
            if (!bmtEnableBeatorajaBmtOutput) { return null; }
            string root = ApplicationSettings.BeatorajaRootPath;
            if (!BeatorajaConfigService.IsBeatorajaRootPathValid(root)) { return Resources.Error_InvalidBeatorajaRootPath; }
            return string.IsNullOrWhiteSpace(BeatorajaConfigService.GetTablePath(root)) ? Resources.Error_InvalidBeatorajaBmtTablePath : null;
        }
    }

    private bool lr2BackupEnabledCandidate;

    /// <summary>
    /// LR2設定ファイルのバックアップの有効化候補です。出力先と組で保存し、有効な出力先がない間は保存しません。
    /// 画面はチェック時に出力先がなければフォルダ選択を開きます。この入口だけでは選択を開けないため、出力先がなければ確定値へ戻します。
    /// </summary>
    public bool IsLR2BackupEnabled
    {
        get => lr2BackupEnabledCandidate;
        set
        {
            if (!value) { DisableLr2Backup(); return; }
            if (!RequiresLr2BackupOutputSelection) { CommitLr2BackupPair(true, ApplicationSettings.LR2BackupPath); return; }
            CancelLr2BackupCandidate();
        }
    }

    /// <summary>保存済みのバックアップの出力先です。選択した有効な出力先は、確定済みの有効化と組で保存します。</summary>
    public string LR2BackupPath
    {
        get => ApplicationSettings.LR2BackupPath;
        set
        {
            if (value == ApplicationSettings.LR2BackupPath) { return; }
            if (!IsLR2BackupPathValid(value)) { RaisePropertyChanged(nameof(LR2BackupPath)); return; }
            SelectLr2BackupOutput(value, requestEnable: false);
        }
    }

    /// <summary>有効化の前に出力先の選択が必要か、つまり保存済みの出力先が有効でないかを示します。</summary>
    internal bool RequiresLr2BackupOutputSelection => !IsLR2BackupPathValid(ApplicationSettings.LR2BackupPath);

    /// <summary>出力先の選択中だけ有効化の候補をチェック状態で示します。保存はしません。</summary>
    internal void MarkLr2BackupEnabledCandidate()
    {
        lr2BackupEnabledCandidate = true;
        RaiseLr2BackupCandidateChanged();
    }

    /// <summary>有効化の候補を確定値へ戻します。選択の取消・閉鎖・失敗で保存しなかった場合に使います。</summary>
    internal void CancelLr2BackupCandidate()
    {
        lr2BackupEnabledCandidate = ApplicationSettings.IsLR2BackupEnabled;
        RaiseLr2BackupCandidateChanged();
    }

    /// <summary>
    /// 選択した出力先を保存します。有効化を求めた場合と、保存済みの有効化がある場合は有効化と組で保存し、それ以外は出力先だけを保存します。
    /// </summary>
    /// <param name="path">フォルダ選択で得た出力先。</param>
    /// <param name="requestEnable">チェックから続けて選んだ場合はtrue。</param>
    /// <returns>保存した場合はtrue。無効な出力先と保存失敗ではfalse。</returns>
    internal bool SelectLr2BackupOutput(string path, bool requestEnable)
    {
        if (!IsLR2BackupPathValid(path)) { CancelLr2BackupCandidate(); return false; }
        // 従来の出力先の選択と同じく、LR2で扱えない文字を含むパスは保存しません。
        if (!path.IsSjisSchemeString()) { CancelLr2BackupCandidate(); ShowSettingValidationError(Resources.Error_LR2UnicodePathUnsupported); return false; }
        return CommitLr2BackupPair(requestEnable || ApplicationSettings.IsLR2BackupEnabled, path);
    }

    /// <summary>無効化だけを保存し、出力先は残します。</summary>
    private void DisableLr2Backup()
    {
        TryCommitSetting(nameof(IsLR2BackupEnabled), (nameof(Settings.IsLR2BackupEnabled), false));
        CancelLr2BackupCandidate();
    }

    /// <summary>有効化と出力先を一組で保存し、「有効かつ出力先が有効」の組が新たに成立した直後だけ次回起動からの適用を案内します。</summary>
    private bool CommitLr2BackupPair(bool enabled, string path)
    {
        bool wasEstablished = ApplicationSettings.IsLR2BackupEnabled && IsLR2BackupPathValid(ApplicationSettings.LR2BackupPath);
        bool committed = TryCommitSetting(nameof(IsLR2BackupEnabled),
            (nameof(Settings.IsLR2BackupEnabled), enabled), (nameof(Settings.LR2BackupPath), path));
        CancelLr2BackupCandidate();
        RaisePropertyChanged(nameof(LR2BackupPath));
        if (committed && !wasEstablished && ApplicationSettings.IsLR2BackupEnabled && IsLR2BackupPathValid(ApplicationSettings.LR2BackupPath)
            && ApplicationSettings.OperationModeLR2DB && !operationAdmission.IsAdmissionClosed)
        {
            ShowUiMessage(Resources.Msg_LR2ConfigBackupEnabledNextStartup, Resources.Confirm, UiDialogIcon.Asterisk,
                "LR2 backup enabled notification");
        }
        return committed;
    }

    private void RaiseLr2BackupCandidateChanged()
    {
        RaisePropertyChanged(nameof(IsLR2BackupEnabled));
        RaiseFieldReasonsChanged();
    }
}
