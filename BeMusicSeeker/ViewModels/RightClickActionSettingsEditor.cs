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
/// Owns the settings-dialog draft for configured right-click web and program actions.
/// The editor never writes <see cref="Settings.RightClickActionsJson"/> while a field is edited;
/// the parent settings dialog commits the serialized snapshot only after validation succeeds.
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
    /// Initializes an editor from the saved JSON snapshot. A missing value creates the five
    /// built-in web actions; an invalid value remains invalid and creates no fallback draft.
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

    /// <summary>Adds a deliberately incomplete custom web action to the draft.</summary>
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

    /// <summary>Deletes the selected web action, including built-in actions.</summary>
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

    /// <summary>Restores the built-in defaults in the draft and marks the editor dirty.</summary>
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

    /// <summary>Refreshes localized built-in labels and chart-kind option labels after a culture change.</summary>
    public void RefreshLocalizedDisplayNames()
    {
        foreach (RightClickWebActionEditorRow action in webActions)
        {
            action.RefreshDisplayName();
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
                RightClickActionSettingsDefaults.IsBuiltInId(action.Id)
                    && string.IsNullOrWhiteSpace(action.NameOverride)
                    ? null
                    : action.NameOverride,
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

/// <summary>Editable web-action row owned by <see cref="RightClickActionSettingsEditor"/>.</summary>
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

    /// <summary>Gets the stable persisted ID.</summary>
    public string Id { get; }

    /// <summary>
    /// Gets or sets the editable name. Built-in actions expose their localized default when
    /// the raw name override is empty, while serialization retains that override as null.
    /// </summary>
    public string Name
    {
        get => string.IsNullOrWhiteSpace(name) && IsBuiltIn
            ? GetBuiltInDisplayName(Id)
            : name ?? string.Empty;
        set
        {
            string normalizedValue = NormalizeName(value);
            if (string.Equals(name, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }
            name = normalizedValue;
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(DisplayName));
            changed();
        }
    }

    /// <summary>Gets the raw optional built-in name override used for serialization.</summary>
    internal string NameOverride => name;

    /// <summary>Gets the localized display name used by the action list.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(name) && RightClickActionSettingsDefaults.IsBuiltInId(Id)
        ? GetBuiltInDisplayName(Id)
        : name ?? string.Empty;

    /// <summary>Gets or sets the URL template.</summary>
    public string UrlTemplate
    {
        get => urlTemplate;
        set
        {
            if (string.Equals(urlTemplate, value, StringComparison.Ordinal))
            {
                return;
            }
            urlTemplate = value;
            RaisePropertyChanged(nameof(UrlTemplate));
            changed();
        }
    }

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

    /// <summary>Gets whether this row uses one of the five built-in stable IDs.</summary>
    public bool IsBuiltIn => RightClickActionSettingsDefaults.IsBuiltInId(Id);

    /// <summary>Raises display-name notifications after a culture change.</summary>
    internal void RefreshDisplayName()
    {
        if (IsBuiltIn && string.IsNullOrWhiteSpace(name))
        {
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(DisplayName));
        }
    }

    private string NormalizeName(string value)
    {
        if (IsBuiltIn
            && (string.IsNullOrWhiteSpace(value)
                || string.Equals(value, GetBuiltInDisplayName(Id), StringComparison.Ordinal)))
        {
            return null;
        }

        return value;
    }

    private static string GetBuiltInDisplayName(string id)
    {
        return id switch
        {
            RightClickActionSettingsDefaults.BmsIrId => Resources.RightClick_builtin_bms_ir,
            RightClickActionSettingsDefaults.MochaId => Resources.RightClick_builtin_mocha,
            RightClickActionSettingsDefaults.MinIrId => Resources.RightClick_builtin_minir,
            RightClickActionSettingsDefaults.RianIrId => Resources.RightClick_builtin_rianir,
            RightClickActionSettingsDefaults.StellaverseIrId => Resources.RightClick_builtin_stellaverse,
            _ => id
        };
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
