using System;
using System.Collections.Generic;
using System.Linq;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the chart-list filter values entered by the main window.
/// </summary>
public sealed class ChartListFilterViewModel : ViewModel
{
    private readonly object syncRoot = new();

    private readonly KeywordSearchSavedQueryOwner keywordSearchSavedQueryOwner;

    private readonly KeywordSearchAssistanceOwner keywordSearchAssistanceOwner;

    private ChartModeFilter modeFilter = ChartModeFilter.All;

    private string keywordFilter = string.Empty;

    private GridKeywordSearchContext keywordSearchContext = GridKeywordSearchContext.ChartList;

    private IReadOnlyList<string> playlistNameCandidates = [];

    private long keywordSearchCatalogRevision;

    private string keywordSearchWarningText = string.Empty;

    private bool isKeywordSearchHelpOpen;

    /// <summary>
    /// Creates the normal chart-search feature state with explicit saved-query boundaries.
    /// </summary>
    /// <param name="keywordSearchHistorySettingsStore">The normal/summary History settings boundary.</param>
    /// <param name="keywordSearchFavoritesSettingsStore">The normal/summary Favorites settings boundary.</param>
    internal ChartListFilterViewModel(
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
        IKeywordSearchFavoritesSettingsStore keywordSearchFavoritesSettingsStore)
    {
        IKeywordSearchHistorySettingsStore historySettingsStore = keywordSearchHistorySettingsStore
            ?? throw new ArgumentNullException(nameof(keywordSearchHistorySettingsStore));
        keywordSearchSavedQueryOwner = new KeywordSearchSavedQueryOwner(
            KeywordSearchSavedQueryScope.Normal,
            historySettingsStore,
            keywordSearchFavoritesSettingsStore);
        keywordSearchAssistanceOwner = new KeywordSearchAssistanceOwner(
            keywordSearchSavedQueryOwner,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(keywordSearchCatalogRevision, []));
        UpdateKeywordSearchPresentation(raiseHelpText: false);
    }

    /// <summary>
    /// Raised after the mode filter has changed so the shell can coordinate feature workflows.
    /// </summary>
    internal event EventHandler ModeFilterChanged;

    /// <summary>
    /// Raised after the keyword filter has changed so the shell can coordinate feature workflows.
    /// </summary>
    internal event EventHandler KeywordFilterChanged;

    /// <summary>
    /// Gets or sets the selected chart-key flags.
    /// </summary>
    public ChartModeFilter ModeFilter
    {
        get
        {
            lock (syncRoot)
            {
                return modeFilter;
            }
        }
        set
        {
            bool rejectedNone = false;
            lock (syncRoot)
            {
                if (value == ChartModeFilter.None)
                {
                    rejectedNone = true;
                }
                else if (modeFilter == value)
                {
                    return;
                }
                else
                {
                    modeFilter = value;
                }
            }
            if (rejectedNone)
            {
                RaisePropertyChanged(nameof(ModeFilter));
                return;
            }
            RaisePropertyChanged(nameof(ModeFilter));
            ModeFilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets or sets the keyword expression applied to the active chart workflow.
    /// </summary>
    public string KeywordFilter
    {
        get
        {
            lock (syncRoot)
            {
                return keywordFilter;
            }
        }
        set
        {
            lock (syncRoot)
            {
                if (keywordFilter == value)
                {
                    return;
                }
                keywordFilter = value;
            }
            RaisePropertyChanged(nameof(KeywordFilter));
            UpdateKeywordSearchPresentation(raiseHelpText: false);
            KeywordFilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Gets the warning text for the active normal chart search context.
    /// </summary>
    public string KeywordSearchWarningText => keywordSearchWarningText;

    /// <summary>
    /// Gets whether the active normal chart search has a warning.
    /// </summary>
    public bool HasKeywordSearchWarning => !string.IsNullOrWhiteSpace(keywordSearchWarningText);

    /// <summary>
    /// Gets the help text for the active normal chart search context.
    /// </summary>
    public string KeywordSearchHelpText => KeywordSearchPresentationText.BuildHelpText(keywordSearchContext);

    /// <summary>
    /// Gets or sets whether the normal chart search help popup is open.
    /// </summary>
    public bool IsKeywordSearchHelpOpen
    {
        get => isKeywordSearchHelpOpen;
        set
        {
            if (isKeywordSearchHelpOpen == value)
            {
                return;
            }
            isKeywordSearchHelpOpen = value;
            RaisePropertyChanged(nameof(IsKeywordSearchHelpOpen));
        }
    }

    internal GridKeywordSearchContext CurrentKeywordSearchContext => keywordSearchContext;

    /// <summary>
    /// Gets the immutable assistance owner consumed by the reusable keyword editor.
    /// </summary>
    internal KeywordSearchAssistanceOwner KeywordSearchAssistanceOwner => keywordSearchAssistanceOwner;

    /// <summary>
    /// Updates the context and immutable playlist-name candidate snapshot used by normal search presentation.
    /// </summary>
    internal void UpdateKeywordSearchContext(
        GridKeywordSearchContext context,
        IEnumerable<string> playlistNameCandidates)
    {
        keywordSearchContext = context;
        this.playlistNameCandidates = [.. (playlistNameCandidates ?? []).Where(name => !string.IsNullOrWhiteSpace(name))];
        KeywordSearchCatalogSnapshot catalogSnapshot = new(
            ++keywordSearchCatalogRevision,
            this.playlistNameCandidates);
        keywordSearchAssistanceOwner.UpdateContext(context, catalogSnapshot);
        UpdateKeywordSearchPresentation(raiseHelpText: true);
    }

    /// <summary>
    /// Marks the normal keyword editor focused and returns its immutable presentation.
    /// </summary>
    internal KeywordSearchPresentationState FocusKeywordSearch(string text, int caretIndex)
        => keywordSearchAssistanceOwner.Focus(text, caretIndex);

    /// <summary>
    /// Closes the normal keyword editor on focus loss.
    /// </summary>
    internal KeywordSearchPresentationState BlurKeywordSearch()
        => keywordSearchAssistanceOwner.Blur();

    /// <summary>
    /// Refreshes normal keyword assistance for the current editor snapshot.
    /// </summary>
    internal KeywordSearchPresentationState RefreshKeywordSearchAssistance(string text, int caretIndex)
        => keywordSearchAssistanceOwner.Refresh(text, caretIndex);

    /// <summary>
    /// Suppresses the current normal editor snapshot until the input changes.
    /// </summary>
    internal KeywordSearchPresentationState SuppressKeywordSearchAssistance()
        => keywordSearchAssistanceOwner.SuppressCurrentSnapshot();

    /// <summary>
    /// Forces normal keyword assistance to recalculate for the current input.
    /// </summary>
    internal KeywordSearchPresentationState ForceRefreshKeywordSearchAssistance()
        => keywordSearchAssistanceOwner.ForceRefresh();

    /// <summary>
    /// Applies a normal keyword presentation item after revision validation.
    /// </summary>
    internal KeywordSearchApplyResult TryApplyKeywordSearchPresentationItem(
        KeywordSearchPresentationItem item,
        string text,
        int caretIndex,
        GridKeywordSearchContext itemContext,
        long catalogRevision)
        => keywordSearchAssistanceOwner.TryApply(item, text, caretIndex, itemContext, catalogRevision);

    /// <summary>
    /// Adds a normal keyword query to favorites.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryAddKeywordSearchFavorite(string query)
        => keywordSearchAssistanceOwner.TryAddFavorite(query);

    /// <summary>
    /// Removes a normal keyword query from favorites.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryRemoveKeywordSearchFavorite(string query)
        => keywordSearchAssistanceOwner.TryRemoveFavorite(query);

    /// <summary>
    /// Deletes a normal keyword history query.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryDeleteKeywordSearchHistory(string query)
        => keywordSearchAssistanceOwner.TryDeleteHistory(query);

    /// <summary>
    /// Commits the normal chart search value to the persisted serialized history.
    /// </summary>
    internal void CommitKeywordSearchHistory(string keywordFilter)
    {
        KeywordSearchSavedQueryMutationResult result = keywordSearchSavedQueryOwner.TryCommitHistory(keywordFilter);
        if (!result.Succeeded)
        {
            throw result.Exception ?? new InvalidOperationException("Keyword search history persistence failed.");
        }
    }

    private void UpdateKeywordSearchPresentation(bool raiseHelpText)
    {
        string nextWarningText = KeywordSearchPresentationText.BuildWarningText(keywordFilter, keywordSearchContext);
        if (!string.Equals(keywordSearchWarningText, nextWarningText, StringComparison.Ordinal))
        {
            keywordSearchWarningText = nextWarningText;
            RaisePropertyChanged(nameof(KeywordSearchWarningText));
            RaisePropertyChanged(nameof(HasKeywordSearchWarning));
        }
        if (raiseHelpText)
        {
            RaisePropertyChanged(nameof(KeywordSearchHelpText));
        }
    }

    /// <summary>
    /// Captures the raw values used to construct one chart workflow request.
    /// </summary>
    internal ChartListFilterSnapshot CaptureSnapshot()
    {
        lock (syncRoot)
        {
            return new ChartListFilterSnapshot(keywordFilter, modeFilter);
        }
    }

    /// <summary>
    /// Applies a complete filter snapshot to the controls without raising per-field workflow
    /// events. A compound owner operation uses this to publish one final refresh after both
    /// selection and filter state have been committed.
    /// </summary>
    /// <param name="snapshot">The immutable filter values to display.</param>
    internal void ApplySnapshotSilently(ChartListFilterSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        bool keywordChanged;
        bool modeChanged;
        lock (syncRoot)
        {
            string nextKeyword = snapshot.KeywordFilter ?? string.Empty;
            ChartModeFilter nextMode = snapshot.ModeFilter == ChartModeFilter.None
                ? ChartModeFilter.All
                : snapshot.ModeFilter;
            keywordChanged = !string.Equals(keywordFilter, nextKeyword, StringComparison.Ordinal);
            modeChanged = modeFilter != nextMode;
            keywordFilter = nextKeyword;
            modeFilter = nextMode;
        }

        if (keywordChanged)
        {
            RaisePropertyChanged(nameof(KeywordFilter));
        }
        if (modeChanged)
        {
            RaisePropertyChanged(nameof(ModeFilter));
        }
        if (keywordChanged)
        {
            UpdateKeywordSearchPresentation(raiseHelpText: false);
        }
    }
}

/// <summary>
/// Immutable chart filter values carried across a workflow boundary.
/// </summary>
internal sealed class ChartListFilterSnapshot
{
    internal ChartListFilterSnapshot(string keywordFilter, ChartModeFilter modeFilter)
    {
        KeywordFilter = keywordFilter;
        ModeFilter = modeFilter;
    }

    internal static ChartListFilterSnapshot Default { get; } = new(null, ChartModeFilter.All);

    internal string KeywordFilter { get; }

    internal ChartModeFilter ModeFilter { get; }
}
