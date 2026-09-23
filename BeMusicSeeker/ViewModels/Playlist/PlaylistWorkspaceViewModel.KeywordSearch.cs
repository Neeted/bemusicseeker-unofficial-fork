using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly KeywordSearchSavedQueryOwner playlistSummaryKeywordSearchSavedQueryOwner;

    private readonly KeywordSearchAssistanceOwner playlistSummaryKeywordSearchAssistanceOwner;

    /// <summary>
    /// Gets the immutable assistance owner used by the playlist-summary keyword editor.
    /// </summary>
    internal KeywordSearchAssistanceOwner PlaylistSummaryKeywordSearchAssistanceOwner
        => playlistSummaryKeywordSearchAssistanceOwner;

    /// <summary>
    /// Marks playlist-summary keyword assistance focused and returns its immutable presentation.
    /// </summary>
    internal KeywordSearchPresentationState FocusPlaylistSummaryKeywordSearch(string text, int caretIndex)
        => playlistSummaryKeywordSearchAssistanceOwner.Focus(text, caretIndex);

    /// <summary>
    /// Closes playlist-summary keyword assistance on focus loss.
    /// </summary>
    internal KeywordSearchPresentationState BlurPlaylistSummaryKeywordSearch()
        => playlistSummaryKeywordSearchAssistanceOwner.Blur();

    /// <summary>
    /// Refreshes playlist-summary keyword assistance for the current editor snapshot.
    /// </summary>
    internal KeywordSearchPresentationState RefreshPlaylistSummaryKeywordSearchAssistance(string text, int caretIndex)
        => playlistSummaryKeywordSearchAssistanceOwner.Refresh(text, caretIndex);

    /// <summary>
    /// Applies a playlist-summary keyword presentation item after revision validation.
    /// </summary>
    internal KeywordSearchApplyResult TryApplyPlaylistSummaryKeywordSearchPresentationItem(
        KeywordSearchPresentationItem item,
        string text,
        int caretIndex,
        long catalogRevision)
        => playlistSummaryKeywordSearchAssistanceOwner.TryApply(
            item,
            text,
            caretIndex,
            GridKeywordSearchContext.PlaylistSummary,
            catalogRevision);

    /// <summary>
    /// Adds a playlist-summary keyword query to favorites.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryAddPlaylistSummaryKeywordSearchFavorite(string query)
        => playlistSummaryKeywordSearchAssistanceOwner.TryAddFavorite(query);

    /// <summary>
    /// Removes a playlist-summary keyword query from favorites.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryRemovePlaylistSummaryKeywordSearchFavorite(string query)
        => playlistSummaryKeywordSearchAssistanceOwner.TryRemoveFavorite(query);

    /// <summary>
    /// Deletes a playlist-summary keyword history query.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryDeletePlaylistSummaryKeywordSearchHistory(string query)
        => playlistSummaryKeywordSearchAssistanceOwner.TryDeleteHistory(query);

    internal IReadOnlyList<string> GetPlaylistKeywordValueCandidates()
    {
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

    internal void CommitPlaylistSummaryKeywordSearchHistory(string keywordFilter)
    {
        KeywordSearchSavedQueryMutationResult result = playlistSummaryKeywordSearchSavedQueryOwner.TryCommitHistory(keywordFilter);
        if (!result.Succeeded)
        {
            throw result.Exception ?? new InvalidOperationException("Playlist summary keyword search history persistence failed.");
        }
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

}
