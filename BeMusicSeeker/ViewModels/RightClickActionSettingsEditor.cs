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

        chartKindOptions[0].DisplayName = Resources.RightClick_chart_kind_bms;
        chartKindOptions[1].DisplayName = Resources.RightClick_chart_kind_bmson;
        chartKindOptions[2].DisplayName = Resources.RightClick_chart_kind_both;
        RaisePropertyChanged(nameof(ChartKindOptions));
    }

    /// <summary>
    /// Validates and serializes the current draft without mutating application settings.
    /// </summary>
    /// <param name="json">The canonical JSON on success.</param>
    /// <param name="error">A localized validation message on failure.</param>
    /// <returns><c>true</c> when the draft can be committed.</returns>
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

/// <summary>Editable external-program action row owned by the right-click settings editor.</summary>
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

    /// <summary>Gets the stable persisted ID.</summary>
    public string Id { get; }

    /// <summary>Gets or sets the action display name.</summary>
    public string Name
    {
        get => name;
        set
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                return;
            }
            name = value;
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(DisplayName));
            changed();
        }
    }

    /// <summary>Gets the list display name, falling back to the executable filename when named later.</summary>
    public string DisplayName => name ?? string.Empty;

    /// <summary>Gets or sets the absolute executable path.</summary>
    public string ExecutablePath
    {
        get => executablePath;
        set
        {
            if (string.Equals(executablePath, value, StringComparison.Ordinal))
            {
                return;
            }
            executablePath = value;
            RaisePropertyChanged(nameof(ExecutablePath));
            changed();
        }
    }

    /// <summary>Gets or sets the Windows argument template.</summary>
    public string ArgumentTemplate
    {
        get => argumentTemplate;
        set
        {
            if (string.Equals(argumentTemplate, value, StringComparison.Ordinal))
            {
                return;
            }
            argumentTemplate = value;
            RaisePropertyChanged(nameof(ArgumentTemplate));
            changed();
        }
    }

    /// <summary>Gets or sets whether this program action is enabled.</summary>
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
    /// Applies an accepted executable picker result and derives the name only when the name is blank.
    /// </summary>
    /// <param name="path">The accepted absolute executable path.</param>
    public void SetExecutablePathFromPicker(string path)
    {
        ExecutablePath = path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(path))
        {
            Name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        }
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
