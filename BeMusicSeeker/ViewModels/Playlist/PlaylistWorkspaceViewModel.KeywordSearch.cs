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

    /// <summary>サマリーの表示寿命へ言語通知を接続し、現在の資源で警告とヘルプを同期し、採用済みの概要なら見出しと自動件数も現在語へ再整形します。再接続で購読を重複させません。</summary>
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

    /// <summary>
    /// 画面へ反映済みの概要だけを対象に、見出しと自動件数を現在語で再整形します。
    /// 行・source・要求世代は触らず、概要要求のみで未採用の状態や概要を抜けた後は何も通知しません。
    /// 明示的な <c>SummaryText</c> は保持し、見出しだけを更新します。
    /// </summary>
    private void RefreshPlaylistSummaryLocalizedHeaderAndCount()
    {
        bool headerChanged;
        bool textChanged;
        lock (playlistSummaryTransitionLock)
        {
            if (!isPlaylistSummaryMode)
            {
                return;
            }
            string nextHeader = BeMusicSeeker.Properties.Resources.Playlist_summary_header;
            headerChanged = gridHeaderText != nextHeader;
            gridHeaderText = nextHeader;
            textChanged = false;
            // 概要を抜けた後は件数が空のため、残った古いviewから件数を出さない。
            if (playlistSummaryTextIsAutomatic && appliedPlaylistSummaryIdentity != null && playlistSummaryText.Length != 0)
            {
                string nextText = FormatPlaylistSummaryText(playlistSummaryView.TotalCharts, playlistSummaryView.Count);
                textChanged = playlistSummaryText != nextText;
                playlistSummaryText = nextText;
            }
        }
        var publishExceptions = new List<Exception>();
        if (headerChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(GridHeaderText)), publishExceptions);
        }
        if (textChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryText)), publishExceptions);
        }
        if (publishExceptions.Count > 0)
        {
            throw new AggregateException(publishExceptions);
        }
    }

    private void RefreshPlaylistSummaryKeywordSearchLocalizedPresentation()
    {
        UpdatePlaylistSummaryKeywordSearchPresentation();
        RaisePropertyChanged(nameof(PlaylistSummaryKeywordSearchHelpText));
        RefreshPlaylistSummaryLocalizedHeaderAndCount();
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
