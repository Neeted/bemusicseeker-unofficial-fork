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
