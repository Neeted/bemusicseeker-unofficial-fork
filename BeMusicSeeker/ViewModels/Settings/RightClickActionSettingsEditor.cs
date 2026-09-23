using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 右クリックの Web / program action を設定ダイアログ内の draft として保持します。
/// 編集中は <see cref="Settings.RightClickActionsJson"/> を変更せず、検証成功後だけ親ダイアログが保存します。
/// </summary>
public sealed class RightClickActionSettingsEditor : ViewModel
{
    private readonly ObservableCollection<RightClickWebActionEditorRow> webActions = [];

    private readonly ObservableCollection<RightClickProgramActionEditorRow> programActions = [];

    private readonly ObservableCollection<RightClickChartKindOption> chartKindOptions = [];

    private string savedRawJson;

    private bool isInvalidPersistedSettings;

    private string persistedSettingsError = string.Empty;

    private string validationMessage = string.Empty;

    private bool isDirty;

    private bool isLoading;

    private RightClickWebActionEditorRow selectedWebAction;

    private RightClickProgramActionEditorRow selectedProgramAction;

    /// <summary>
    /// 保存済み JSON snapshot から editor を初期化します。不正値には fallback draft を作りません。
    /// </summary>
    /// <param name="rawJson">The raw settings value from the current edit-session settings.</param>
    public RightClickActionSettingsEditor(string rawJson)
    {
        webActions.CollectionChanged += WebActionsCollectionChanged;
        programActions.CollectionChanged += ProgramActionsCollectionChanged;
        chartKindOptions.Add(new RightClickChartKindOption(
            nameof(ExternalChartKind.BmsOnly),
            Resources.RightClick_chart_kind_bms));
        chartKindOptions.Add(new RightClickChartKindOption(
            nameof(ExternalChartKind.BmsonOnly),
            Resources.RightClick_chart_kind_bmson));
        chartKindOptions.Add(new RightClickChartKindOption(
            nameof(ExternalChartKind.All),
            Resources.RightClick_chart_kind_both));
        ChartKindOptions = new ReadOnlyObservableCollection<RightClickChartKindOption>(chartKindOptions);
        LoadRawSnapshot(rawJson);
    }

    /// <summary>Gets the editable web actions in persisted array order.</summary>
    public ObservableCollection<RightClickWebActionEditorRow> WebActions => webActions;

    /// <summary>Gets the editable program actions in persisted array order.</summary>
    public ObservableCollection<RightClickProgramActionEditorRow> ProgramActions => programActions;

    /// <summary>Gets the localized chart-kind options shown by the page.</summary>
    public ReadOnlyObservableCollection<RightClickChartKindOption> ChartKindOptions { get; private set; }

    /// <summary>Gets or sets the selected web action.</summary>
    public RightClickWebActionEditorRow SelectedWebAction
    {
        get => selectedWebAction;
        set
        {
            if (ReferenceEquals(selectedWebAction, value))
            {
                return;
            }

            selectedWebAction = value;
            RaisePropertyChanged(nameof(SelectedWebAction));
            RaiseSelectionStateChanged();
        }
    }

    /// <summary>Gets or sets the selected program action.</summary>
    public RightClickProgramActionEditorRow SelectedProgramAction
    {
        get => selectedProgramAction;
        set
        {
            if (ReferenceEquals(selectedProgramAction, value))
            {
                return;
            }

            selectedProgramAction = value;
            RaisePropertyChanged(nameof(SelectedProgramAction));
            RaiseSelectionStateChanged();
        }
    }

    /// <summary>Gets whether the saved JSON could not be parsed and must be explicitly reset.</summary>
    public bool IsInvalidPersistedSettings => isInvalidPersistedSettings;

    /// <summary>Gets the localized diagnostic for an invalid saved JSON value.</summary>
    public string PersistedSettingsError => persistedSettingsError;

    /// <summary>Gets the current draft validation message, if any.</summary>
    public string ValidationMessage => validationMessage;

    /// <summary>Gets a value indicating whether the draft differs from its saved snapshot.</summary>
    public bool IsDirty => isDirty;

    /// <summary>Gets whether the editor has a selected web action that can be deleted.</summary>
    public bool CanDeleteWebAction => selectedWebAction != null;

    /// <summary>Gets whether the selected web action can move toward the beginning of the list.</summary>
    public bool CanMoveWebActionUp => selectedWebAction != null && webActions.IndexOf(selectedWebAction) > 0;

    /// <summary>Gets whether the selected web action can move toward the end of the list.</summary>
    public bool CanMoveWebActionDown => selectedWebAction != null
        && webActions.IndexOf(selectedWebAction) >= 0
        && webActions.IndexOf(selectedWebAction) < webActions.Count - 1;

    /// <summary>Gets whether the editor has a selected program action that can be deleted.</summary>
    public bool CanDeleteProgramAction => selectedProgramAction != null;

    /// <summary>Gets whether the selected program action can move toward the beginning of the list.</summary>
    public bool CanMoveProgramActionUp => selectedProgramAction != null && programActions.IndexOf(selectedProgramAction) > 0;

    /// <summary>Gets whether the selected program action can move toward the end of the list.</summary>
    public bool CanMoveProgramActionDown => selectedProgramAction != null
        && programActions.IndexOf(selectedProgramAction) >= 0
        && programActions.IndexOf(selectedProgramAction) < programActions.Count - 1;

    /// <summary>名前と URL が未入力の Web action を draft に追加し、編集対象として選択します。</summary>
    public void AddWebAction()
    {
        var action = new RightClickWebActionEditorRow(
            CreateUniqueId("web"),
            string.Empty,
            string.Empty,
            enabled: true,
            ExternalChartKind.All,
            MarkDirty);
        webActions.Add(action);
        SelectedWebAction = action;
    }

    /// <summary>既定値由来の項目を含め、選択中の Web action を削除します。</summary>
    public void DeleteSelectedWebAction()
    {
        if (selectedWebAction == null)
        {
            return;
        }

        RightClickWebActionEditorRow action = selectedWebAction;
        int index = webActions.IndexOf(action);
        if (index < 0)
        {
            return;
        }

        webActions.RemoveAt(index);
        SelectedWebAction = webActions.Count == 0
            ? null
            : webActions[Math.Min(index, webActions.Count - 1)];
    }

    /// <summary>Moves the selected web action one position toward the beginning.</summary>
    public void MoveSelectedWebActionUp()
    {
        Move(webActions, selectedWebAction, -1);
    }

    /// <summary>Moves the selected web action one position toward the end.</summary>
    public void MoveSelectedWebActionDown()
    {
        Move(webActions, selectedWebAction, 1);
    }

    /// <summary>Adds a custom program action with the required default argument placeholder.</summary>
    public void AddProgramAction()
    {
        var action = new RightClickProgramActionEditorRow(
            CreateUniqueId("program"),
            string.Empty,
            string.Empty,
            "{filePath}",
            enabled: true,
            MarkDirty);
        programActions.Add(action);
        SelectedProgramAction = action;
    }

    /// <summary>Deletes the selected program action.</summary>
    public void DeleteSelectedProgramAction()
    {
        if (selectedProgramAction == null)
        {
            return;
        }

        RightClickProgramActionEditorRow action = selectedProgramAction;
        int index = programActions.IndexOf(action);
        if (index < 0)
        {
            return;
        }

        programActions.RemoveAt(index);
        SelectedProgramAction = programActions.Count == 0
            ? null
            : programActions[Math.Min(index, programActions.Count - 1)];
    }

    /// <summary>Moves the selected program action one position toward the beginning.</summary>
    public void MoveSelectedProgramActionUp()
    {
        Move(programActions, selectedProgramAction, -1);
    }

    /// <summary>Moves the selected program action one position toward the end.</summary>
    public void MoveSelectedProgramActionDown()
    {
        Move(programActions, selectedProgramAction, 1);
    }

    /// <summary>draft を既定 action に戻し、未保存変更として扱います。</summary>
    public void ResetToDefaults()
    {
        Populate(RightClickActionSettingsDefaults.Create());
        isInvalidPersistedSettings = false;
        persistedSettingsError = string.Empty;
        validationMessage = string.Empty;
        RaisePropertyChanged(nameof(IsInvalidPersistedSettings));
        RaisePropertyChanged(nameof(PersistedSettingsError));
        RaisePropertyChanged(nameof(ValidationMessage));
        MarkDirty();
    }

    /// <summary>Alias used by settings-page actions for the explicit recovery operation.</summary>
    public void RestoreDefaults()
    {
        ResetToDefaults();
    }

    /// <summary>
    /// Restores the saved raw snapshot after Cancel. Invalid persisted data is restored as invalid
    /// data rather than being replaced with a default draft.
    /// </summary>
    public void DiscardChanges()
    {
        LoadRawSnapshot(savedRawJson);
    }

    /// <summary>culture 変更後に翻訳対象の表示を更新します。</summary>
    public void RefreshLocalizedDisplayNames()
    {
        foreach (RightClickWebActionEditorRow action in webActions)
        {
            action.RefreshLocalizedValidationMessages();
        }

        foreach (RightClickProgramActionEditorRow action in programActions)
        {
            action.RefreshLocalizedValidationMessages();
        }

        chartKindOptions[0].DisplayName = Resources.RightClick_chart_kind_bms;
        chartKindOptions[1].DisplayName = Resources.RightClick_chart_kind_bmson;
        chartKindOptions[2].DisplayName = Resources.RightClick_chart_kind_both;
        RaisePropertyChanged(nameof(ChartKindOptions));
    }

    /// <summary>
    /// 現在の draft を検証して直列化します。設定は変更せず、不正なプログラム行があれば配列順で最初の行を選択します。
    /// </summary>
    /// <param name="json">成功時の canonical JSON。</param>
    /// <param name="error">失敗時の現在の言語による検証メッセージ。</param>
    /// <returns>保存可能な draft の場合は <see langword="true"/>。</returns>
    public bool TryPrepareSave(out string json, out string error)
    {
        json = null;
        error = string.Empty;
        if (isInvalidPersistedSettings)
        {
            error = persistedSettingsError;
            SetValidationMessage(error);
            return false;
        }

        var webDefinitions = new List<RightClickWebActionDefinition>(webActions.Count);
        foreach (RightClickWebActionEditorRow action in webActions)
        {
            webDefinitions.Add(new RightClickWebActionDefinition(
                action.Id,
                action.Name,
                action.UrlTemplate,
                action.Enabled,
                action.ChartKind));
        }

        var programDefinitions = new List<RightClickProgramActionDefinition>(programActions.Count);
        foreach (RightClickProgramActionEditorRow action in programActions)
        {
            if (action.TryGetValidationError(
                out RightClickProgramActionValidationErrorKind validationErrorKind))
            {
                SelectedProgramAction = action;
                error = RightClickProgramActionEditorRow.GetValidationMessage(validationErrorKind);
                SetValidationMessage(error);
                return false;
            }

            programDefinitions.Add(new RightClickProgramActionDefinition(
                action.Id,
                action.Name,
                action.ExecutablePath,
                action.ArgumentTemplate,
                action.Enabled));
        }

        if (!RightClickActionSettingsSerializer.TrySerialize(
            new RightClickActionSettings(webDefinitions, programDefinitions),
            out json,
            out RightClickActionSettingsParseError parseError))
        {
            error = string.Format(
                CultureInfo.CurrentCulture,
                Resources.RightClick_settings_validation_error_format,
                Resources.RightClick_settings_invalid);
            SetValidationMessage(error);
            return false;
        }

        SetValidationMessage(string.Empty);
        return true;
    }

    /// <summary>Marks a successfully persisted canonical JSON value as the new saved snapshot.</summary>
    /// <param name="rawJson">The canonical JSON written to the settings object.</param>
    internal void AcceptSavedSnapshot(string rawJson)
    {
        savedRawJson = rawJson;
        isDirty = false;
        isInvalidPersistedSettings = false;
        persistedSettingsError = string.Empty;
        validationMessage = string.Empty;
        RaiseEditorStateChanged();
    }

    private void LoadRawSnapshot(string rawJson)
    {
        savedRawJson = rawJson;
        RightClickActionSettingsParseResult result = RightClickActionSettingsSerializer.Parse(rawJson);
        isLoading = true;
        try
        {
            if (result.Succeeded)
            {
                Populate(result.Settings);
                isInvalidPersistedSettings = false;
                persistedSettingsError = string.Empty;
            }
            else
            {
                webActions.Clear();
                programActions.Clear();
                SelectedWebAction = null;
                SelectedProgramAction = null;
                isInvalidPersistedSettings = true;
                persistedSettingsError = string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.RightClick_settings_invalid_format,
                    Resources.RightClick_settings_invalid);
            }
        }
        finally
        {
            isLoading = false;
        }

        isDirty = false;
        validationMessage = string.Empty;
        RaiseEditorStateChanged();
    }

    private void Populate(RightClickActionSettings settings)
    {
        isLoading = true;
        try
        {
            webActions.Clear();
            programActions.Clear();
            foreach (RightClickWebActionDefinition action in settings.WebActions)
            {
                webActions.Add(new RightClickWebActionEditorRow(
                    action.Id,
                    action.Name,
                    action.UrlTemplate,
                    action.Enabled,
                    action.ChartKind,
                    MarkDirty));
            }
            foreach (RightClickProgramActionDefinition action in settings.ProgramActions)
            {
                programActions.Add(new RightClickProgramActionEditorRow(
                    action.Id,
                    action.Name,
                    action.ExecutablePath,
                    action.ArgumentTemplate,
                    action.Enabled,
                    MarkDirty));
            }
            SelectedWebAction = webActions.FirstOrDefault();
            SelectedProgramAction = programActions.FirstOrDefault();
        }
        finally
        {
            isLoading = false;
        }
    }

    private void MarkDirty()
    {
        if (isLoading)
        {
            return;
        }

        if (!isDirty)
        {
            isDirty = true;
            RaisePropertyChanged(nameof(IsDirty));
        }

        if (!string.IsNullOrEmpty(validationMessage))
        {
            SetValidationMessage(string.Empty);
        }
    }

    private void SetValidationMessage(string value)
    {
        value ??= string.Empty;
        if (string.Equals(validationMessage, value, StringComparison.Ordinal))
        {
            return;
        }

        validationMessage = value;
        RaisePropertyChanged(nameof(ValidationMessage));
    }

    private void RaiseEditorStateChanged()
    {
        RaisePropertyChanged(nameof(IsDirty));
        RaisePropertyChanged(nameof(IsInvalidPersistedSettings));
        RaisePropertyChanged(nameof(PersistedSettingsError));
        RaisePropertyChanged(nameof(ValidationMessage));
        RaiseSelectionStateChanged();
    }

    private void RaiseSelectionStateChanged()
    {
        RaisePropertyChanged(nameof(CanDeleteWebAction));
        RaisePropertyChanged(nameof(CanMoveWebActionUp));
        RaisePropertyChanged(nameof(CanMoveWebActionDown));
        RaisePropertyChanged(nameof(CanDeleteProgramAction));
        RaisePropertyChanged(nameof(CanMoveProgramActionUp));
        RaisePropertyChanged(nameof(CanMoveProgramActionDown));
    }

    private void WebActionsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (!isLoading)
        {
            MarkDirty();
        }
        RaiseSelectionStateChanged();
    }

    private void ProgramActionsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (!isLoading)
        {
            MarkDirty();
        }
        RaiseSelectionStateChanged();
    }

    private static void Move<T>(ObservableCollection<T> actions, T selected, int offset)
        where T : class
    {
        if (selected == null)
        {
            return;
        }

        int oldIndex = actions.IndexOf(selected);
        int newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= actions.Count)
        {
            return;
        }

        actions.Move(oldIndex, newIndex);
    }

    private string CreateUniqueId(string prefix)
    {
        string id;
        do
        {
            id = "custom-" + prefix + "-" + Guid.NewGuid().ToString("N");
        }
        while (webActions.Any(action => string.Equals(action.Id, id, StringComparison.Ordinal))
            || programActions.Any(action => string.Equals(action.Id, id, StringComparison.Ordinal)));
        return id;
    }
}

/// <summary><see cref="RightClickActionSettingsEditor"/> が保持する編集可能な Web action 行です。</summary>
public sealed class RightClickWebActionEditorRow : ViewModel
{
    private readonly Action changed;
    private string name;
    private string urlTemplate;
    private bool enabled;
    private ExternalChartKind chartKind;

    internal RightClickWebActionEditorRow(
        string id,
        string name,
        string urlTemplate,
        bool enabled,
        ExternalChartKind chartKind,
        Action changed)
    {
        Id = id;
        this.name = name;
        this.urlTemplate = urlTemplate;
        this.enabled = enabled;
        this.chartKind = chartKind;
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    /// <summary>保存形式で使う安定 ID を取得します。</summary>
    public string Id { get; }

    /// <summary>表示名を取得または設定します。</summary>
    public string Name
    {
        get => name ?? string.Empty;
        set
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                return;
            }
            name = value;
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(DisplayName));
            RaisePropertyChanged(nameof(NameValidationMessage));
            RaisePropertyChanged(nameof(NameValidationStatus));
            changed();
        }
    }

    /// <summary>action 一覧に表示する名前を取得します。</summary>
    public string DisplayName => Name;

    /// <summary>名前入力欄の検証メッセージを取得します。</summary>
    public string NameValidationMessage => string.IsNullOrWhiteSpace(Name)
        ? Resources.RightClick_name_required
        : string.Empty;

    /// <summary>名前入力欄の検証状態を取得します。</summary>
    public string NameValidationStatus => string.IsNullOrWhiteSpace(NameValidationMessage) ? string.Empty : "Error";

    /// <summary>URL template を取得または設定します。</summary>
    public string UrlTemplate
    {
        get => urlTemplate ?? string.Empty;
        set
        {
            if (string.Equals(urlTemplate, value, StringComparison.Ordinal))
            {
                return;
            }
            urlTemplate = value;
            RaisePropertyChanged(nameof(UrlTemplate));
            RaisePropertyChanged(nameof(UrlTemplateValidationMessage));
            RaisePropertyChanged(nameof(UrlTemplateValidationStatus));
            changed();
        }
    }

    /// <summary>URL template 入力欄の検証メッセージを取得します。</summary>
    public string UrlTemplateValidationMessage => string.IsNullOrWhiteSpace(UrlTemplate)
        ? Resources.RightClick_url_required
        : string.Empty;

    /// <summary>URL template 入力欄の検証状態を取得します。</summary>
    public string UrlTemplateValidationStatus => string.IsNullOrWhiteSpace(UrlTemplateValidationMessage) ? string.Empty : "Error";

    /// <summary>Gets or sets whether this action is enabled.</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }
            enabled = value;
            RaisePropertyChanged(nameof(Enabled));
            changed();
        }
    }

    /// <summary>Gets or sets the internal target chart kind used for serialization.</summary>
    internal ExternalChartKind ChartKind
    {
        get => chartKind;
        set
        {
            if (chartKind == value)
            {
                return;
            }
            chartKind = value;
            RaisePropertyChanged(nameof(ChartKind));
            RaisePropertyChanged(nameof(ChartKindKey));
            changed();
        }
    }

    /// <summary>Gets or sets the serialized chart-kind enum name for WPF binding.</summary>
    public string ChartKindKey
    {
        get => chartKind.ToString();
        set
        {
            if (!Enum.TryParse(value, ignoreCase: false, out ExternalChartKind parsed))
            {
                return;
            }
            ChartKind = parsed;
        }
    }

    /// <summary>culture 変更後に翻訳済み検証メッセージを再評価します。</summary>
    internal void RefreshLocalizedValidationMessages()
    {
        RaisePropertyChanged(nameof(NameValidationMessage));
        RaisePropertyChanged(nameof(NameValidationStatus));
        RaisePropertyChanged(nameof(UrlTemplateValidationMessage));
        RaisePropertyChanged(nameof(UrlTemplateValidationStatus));
    }
}

/// <summary>右クリック設定画面で編集する外部プログラム action の行です。</summary>
public sealed class RightClickProgramActionEditorRow : ViewModel
{
    private readonly Action changed;
    private string name;
    private string executablePath;
    private string argumentTemplate;
    private bool enabled;

    internal RightClickProgramActionEditorRow(
        string id,
        string name,
        string executablePath,
        string argumentTemplate,
        bool enabled,
        Action changed)
    {
        Id = id;
        this.name = name;
        this.executablePath = executablePath;
        this.argumentTemplate = argumentTemplate;
        this.enabled = enabled;
        this.changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    /// <summary>保存形式で使う安定 ID を取得します。</summary>
    public string Id { get; }

    /// <summary>表示名を取得または設定します。</summary>
    public string Name
    {
        get => name ?? string.Empty;
        set
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                return;
            }
            name = value;
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(DisplayName));
            RaisePropertyChanged(nameof(NameValidationMessage));
            RaisePropertyChanged(nameof(NameValidationStatus));
            changed();
        }
    }

    /// <summary>一覧へ表示する名前を取得します。</summary>
    public string DisplayName => Name;

    /// <summary>名前入力欄の検証メッセージを取得します。</summary>
    public string NameValidationMessage => GetValidationMessage(
        RightClickProgramActionValidator.ValidateName(Name));

    /// <summary>名前入力欄の検証状態を取得します。</summary>
    public string NameValidationStatus => string.IsNullOrEmpty(NameValidationMessage) ? string.Empty : "Error";

    /// <summary>絶対パスの実行ファイルを取得または設定します。</summary>
    public string ExecutablePath
    {
        get => executablePath ?? string.Empty;
        set
        {
            if (string.Equals(executablePath, value, StringComparison.Ordinal))
            {
                return;
            }
            executablePath = value;
            RaisePropertyChanged(nameof(ExecutablePath));
            RaisePropertyChanged(nameof(ExecutablePathValidationMessage));
            RaisePropertyChanged(nameof(ExecutablePathValidationStatus));
            changed();
        }
    }

    /// <summary>実行ファイル欄の検証メッセージを取得します。</summary>
    public string ExecutablePathValidationMessage => GetValidationMessage(
        RightClickProgramActionValidator.ValidateExecutablePath(ExecutablePath));

    /// <summary>実行ファイル欄の検証状態を取得します。</summary>
    public string ExecutablePathValidationStatus => string.IsNullOrEmpty(ExecutablePathValidationMessage)
        ? string.Empty
        : "Error";

    /// <summary>Windows の引数テンプレートを取得または設定します。</summary>
    public string ArgumentTemplate
    {
        get => argumentTemplate ?? string.Empty;
        set
        {
            if (string.Equals(argumentTemplate, value, StringComparison.Ordinal))
            {
                return;
            }
            argumentTemplate = value;
            RaisePropertyChanged(nameof(ArgumentTemplate));
            RaisePropertyChanged(nameof(ArgumentTemplateValidationMessage));
            RaisePropertyChanged(nameof(ArgumentTemplateValidationStatus));
            changed();
        }
    }

    /// <summary>引数テンプレート欄の検証メッセージを取得します。</summary>
    public string ArgumentTemplateValidationMessage => GetValidationMessage(
        RightClickProgramActionValidator.ValidateArgumentTemplate(ArgumentTemplate));

    /// <summary>引数テンプレート欄の検証状態を取得します。</summary>
    public string ArgumentTemplateValidationStatus => string.IsNullOrEmpty(ArgumentTemplateValidationMessage)
        ? string.Empty
        : "Error";

    /// <summary>このプログラム action を有効にするかどうかを取得または設定します。</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value)
            {
                return;
            }
            enabled = value;
            RaisePropertyChanged(nameof(Enabled));
            changed();
        }
    }

    /// <summary>
    /// 選択された実行ファイルのパスを適用し、名前が空白の場合だけファイル名から補完します。
    /// </summary>
    /// <param name="path">選択された実行ファイルの絶対パス。</param>
    public void SetExecutablePathFromPicker(string path)
    {
        ExecutablePath = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(path))
        {
            Name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        }
    }

    /// <summary>
    /// 保存時に表示する最初の欄別検証原因を、入力欄と同じ検証規則で取得します。
    /// </summary>
    internal bool TryGetValidationError(out RightClickProgramActionValidationErrorKind errorKind)
    {
        errorKind = RightClickProgramActionValidator.ValidateName(Name);
        if (errorKind != RightClickProgramActionValidationErrorKind.None)
        {
            return true;
        }

        errorKind = RightClickProgramActionValidator.ValidateExecutablePath(ExecutablePath);
        if (errorKind != RightClickProgramActionValidationErrorKind.None)
        {
            return true;
        }

        errorKind = RightClickProgramActionValidator.ValidateArgumentTemplate(ArgumentTemplate);
        return errorKind != RightClickProgramActionValidationErrorKind.None;
    }

    /// <summary>型付き検証原因を現在の言語の欄別文言へ変換します。</summary>
    internal static string GetValidationMessage(RightClickProgramActionValidationErrorKind errorKind)
    {
        return errorKind switch
        {
            RightClickProgramActionValidationErrorKind.NameRequired => Resources.RightClick_name_required,
            RightClickProgramActionValidationErrorKind.ExecutablePathRequired
                => Resources.RightClick_executable_required,
            RightClickProgramActionValidationErrorKind.ExecutablePathNotAbsolute
                => Resources.RightClick_executable_absolute_required,
            RightClickProgramActionValidationErrorKind.ArgumentTemplateRequired
                => Resources.RightClick_arguments_required,
            RightClickProgramActionValidationErrorKind.ArgumentTemplateMissingFilePath
                => string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.RightClick_arguments_file_path_required,
                    "{filePath}"),
            RightClickProgramActionValidationErrorKind.ArgumentTemplateUnknownPlaceholder
                => Resources.RightClick_arguments_unknown_placeholder,
            RightClickProgramActionValidationErrorKind.ArgumentTemplateUnbalancedPlaceholder
                => Resources.RightClick_arguments_unbalanced_placeholder,
            RightClickProgramActionValidationErrorKind.ArgumentTemplateUnbalancedDoubleQuote
                => Resources.RightClick_arguments_unbalanced_quote,
            RightClickProgramActionValidationErrorKind.None => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(errorKind), errorKind, "未知のプログラム action 検証原因です.")
        };
    }

    /// <summary>言語変更後に欄別検証メッセージを再評価します。</summary>
    internal void RefreshLocalizedValidationMessages()
    {
        RaisePropertyChanged(nameof(NameValidationMessage));
        RaisePropertyChanged(nameof(NameValidationStatus));
        RaisePropertyChanged(nameof(ExecutablePathValidationMessage));
        RaisePropertyChanged(nameof(ExecutablePathValidationStatus));
        RaisePropertyChanged(nameof(ArgumentTemplateValidationMessage));
        RaisePropertyChanged(nameof(ArgumentTemplateValidationStatus));
    }
}

/// <summary>Localized chart-kind option used by the right-click settings page.</summary>
public sealed class RightClickChartKindOption : ViewModel
{
    private string displayName;

    internal RightClickChartKindOption(string key, string displayName)
    {
        Key = key;
        this.displayName = displayName;
    }

    /// <summary>Gets the serialized <see cref="ExternalChartKind"/> enum name.</summary>
    public string Key { get; }

    /// <summary>Gets or sets the localized option label.</summary>
    public string DisplayName
    {
        get => displayName;
        internal set
        {
            if (string.Equals(displayName, value, StringComparison.Ordinal))
            {
                return;
            }
            displayName = value;
            RaisePropertyChanged(nameof(DisplayName));
        }
    }
}
