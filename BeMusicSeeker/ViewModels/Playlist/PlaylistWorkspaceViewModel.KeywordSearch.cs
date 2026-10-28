using System;
using System.Collections.Generic;
using System.ComponentModel;
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

    /// <summary>サマリーの表示寿命へ言語通知を接続し、現在の資源で警告とヘルプを同期します。再接続で購読を重複させません。</summary>
    internal void AttachPlaylistSummaryKeywordSearchPresentation()
    {
        ResourceService.Current.PropertyChanged -= PlaylistSummaryKeywordSearchResourcesChanged;
        ResourceService.Current.PropertyChanged += PlaylistSummaryKeywordSearchResourcesChanged;
        RefreshPlaylistSummaryKeywordSearchLocalizedPresentation();
    }

    /// <summary>画面の表示寿命が終わったサマリー検索表示を言語通知から切り離します。</summary>
    internal void DetachPlaylistSummaryKeywordSearchPresentation()
    {
        ResourceService.Current.PropertyChanged -= PlaylistSummaryKeywordSearchResourcesChanged;
    }

    private void PlaylistSummaryKeywordSearchResourcesChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ResourceService.Resources))
        {
            RefreshPlaylistSummaryKeywordSearchLocalizedPresentation();
        }
    }

    private void RefreshPlaylistSummaryKeywordSearchLocalizedPresentation()
    {
        UpdatePlaylistSummaryKeywordSearchPresentation();
        RaisePropertyChanged(nameof(PlaylistSummaryKeywordSearchHelpText));
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
