using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly List<string> playlistSummaryKeywordSearchHistory = [];

    private readonly IKeywordSearchHistorySettingsStore playlistSummaryKeywordSearchHistorySettingsStore;

    internal IReadOnlyList<string> GetPlaylistKeywordValueCandidates()
    {
        if (getPlaylistStore == null)
        {
            throw new InvalidOperationException("Playlist persistence provider is not configured.");
        }
        BMSPlaylist playlistStore = getPlaylistStore();
        if (playlistStore == null)
        {
            return [];
        }
        bool lockAcquired = false;
        try
        {
            playlistStore.AcquireReaderLockBMSTables();
            lockAcquired = true;
            return [.. (playlistStore.BMSTables ?? Enumerable.Empty<BMSTable>())
                .Where(table => !string.IsNullOrWhiteSpace(table?.name))
                .Select(table => table.name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        }
        finally
        {
            if (lockAcquired)
            {
                playlistStore.FreeReaderLockBMSTables();
            }
        }
    }

    internal void RefreshPlaylistSummaryKeywordSearchSuggestions(
        string keywordFilter,
        int caretIndex,
        bool forceHistory)
    {
        GridKeywordSearchCompletionResult fieldCompletion = GridKeywordSearchCompletion.CreateFieldCompletion(
            keywordFilter,
            caretIndex,
            GridKeywordSearchContext.PlaylistSummary);
        if (fieldCompletion.Items.Count > 0)
        {
            SetPlaylistSummaryKeywordSearchSuggestions(
                fieldCompletion.Items,
                KeywordSearchSuggestionKind.Field);
            return;
        }

        if (GridKeywordSearchCompletion.IsPlaylistValueCompletionContext(
            keywordFilter,
            caretIndex,
            GridKeywordSearchContext.PlaylistSummary))
        {
            GridKeywordSearchCompletionResult playlistValueCompletion =
                GridKeywordSearchCompletion.CreatePlaylistValueCompletion(
                    keywordFilter,
                    caretIndex,
                    GridKeywordSearchContext.PlaylistSummary,
                    []);
            if (playlistValueCompletion.Items.Count > 0)
            {
                SetPlaylistSummaryKeywordSearchSuggestions(
                    playlistValueCompletion.Items,
                    KeywordSearchSuggestionKind.Value);
                return;
            }
        }

        if (forceHistory)
        {
            SetPlaylistSummaryKeywordSearchSuggestions(
                KeywordSearchPresentationText.BuildHistorySuggestions(
                    playlistSummaryKeywordSearchHistory,
                    keywordFilter),
                KeywordSearchSuggestionKind.History);
            return;
        }

        SetPlaylistSummaryKeywordSearchSuggestions([], KeywordSearchSuggestionKind.Field);
    }

    internal void ClosePlaylistSummaryKeywordSearchSuggestions()
    {
        IsPlaylistSummaryKeywordSearchSuggestionPopupOpen = false;
    }

    internal void CommitPlaylistSummaryKeywordSearchHistory(string keywordFilter)
    {
        IReadOnlyList<string> nextHistory = KeywordSearchHistoryStore.AddEntry(
            playlistSummaryKeywordSearchHistory,
            keywordFilter);
        playlistSummaryKeywordSearchHistory.Clear();
        playlistSummaryKeywordSearchHistory.AddRange(nextHistory);
        playlistSummaryKeywordSearchHistorySettingsStore.PlaylistSummaryKeywordSearchHistory = KeywordSearchHistoryStore.Serialize(
            playlistSummaryKeywordSearchHistory);
    }

    private void SetPlaylistSummaryKeywordSearchSuggestions(
        IReadOnlyList<KeywordSearchSuggestionItem> suggestions,
        KeywordSearchSuggestionKind kind)
    {
        playlistSummaryKeywordSearchSuggestions.Clear();
        foreach (KeywordSearchSuggestionItem suggestion in suggestions ?? [])
        {
            playlistSummaryKeywordSearchSuggestions.Add(suggestion);
        }

        SetPlaylistSummaryKeywordSearchSuggestionHeaderText(
            playlistSummaryKeywordSearchSuggestions.Count == 0
                ? string.Empty
                : KeywordSearchPresentationText.BuildSuggestionHeaderText(kind));
        IsPlaylistSummaryKeywordSearchSuggestionPopupOpen =
            playlistSummaryKeywordSearchSuggestions.Count > 0;
    }

    private void UpdatePlaylistSummaryKeywordSearchPresentation()
    {
        SetPlaylistSummaryKeywordSearchWarningText(
            KeywordSearchPresentationText.BuildWarningText(
                PlaylistSummaryKeywordFilter,
                GridKeywordSearchContext.PlaylistSummary));
    }

    private void SetPlaylistSummaryKeywordSearchWarningText(string value)
    {
        string next = value ?? string.Empty;
        if (playlistSummaryKeywordSearchWarningText == next)
        {
            return;
        }

        playlistSummaryKeywordSearchWarningText = next;
        RaisePropertyChanged(nameof(PlaylistSummaryKeywordSearchWarningText));
        RaisePropertyChanged(nameof(HasPlaylistSummaryKeywordSearchWarning));
    }

    private void SetPlaylistSummaryKeywordSearchSuggestionHeaderText(string value)
    {
        string next = value ?? string.Empty;
        if (playlistSummaryKeywordSearchSuggestionHeaderText == next)
        {
            return;
        }

        playlistSummaryKeywordSearchSuggestionHeaderText = next;
        RaisePropertyChanged(nameof(PlaylistSummaryKeywordSearchSuggestionHeaderText));
    }
}
