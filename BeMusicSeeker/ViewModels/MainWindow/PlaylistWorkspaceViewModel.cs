using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns playlist detail and summary presentation state while shell workflows remain on MainWindowViewModel.
/// </summary>
public sealed class PlaylistWorkspaceViewModel : ViewModel
{
    private MainWindowViewModel.cSortParameters playlistSummarySortParameters;

    private PlaylistSummaryColumnSettings playlistSummaryColumnsSettings;

    private Visibility columnSettingsVisibilityForPlaylist = Visibility.Collapsed;

    private ObservableCollection<PlaylistSummaryRow> playlistSummaryView = [];

    private string playlistSummaryText = string.Empty;

    private WeakReference<ObservableCollection<PlaylistSummaryRow>> previousPlaylistSummaryViewWeakReference;

    private bool isPlaylistSummaryMode;

    private bool isPlaylistDetailViewActive;

    private bool useAsyncChartRowsViewBinding = true;

    private string gridHeaderText = string.Empty;

    private string playlistSummaryKeywordFilter = string.Empty;

    private string playlistSummaryKeywordSearchWarningText = string.Empty;

    private bool isPlaylistSummaryKeywordSearchHelpOpen;

    private readonly ObservableCollection<KeywordSearchSuggestionItem> playlistSummaryKeywordSearchSuggestions = [];

    private bool isPlaylistSummaryKeywordSearchSuggestionPopupOpen;

    private string playlistSummaryKeywordSearchSuggestionHeaderText = string.Empty;

    private MainWindowViewModel.PlaylistSummaryOwnedFilterType playlistSummaryOwnedFilter = MainWindowViewModel.PlaylistSummaryOwnedFilterType.All;

    private long lastPlaylistSummaryBuildCompletedTimestamp;

    private readonly object playlistSummaryTransitionLock = new();

    private long playlistSummaryPresentationGeneration;

    private long playlistSummaryDataRebuildGeneration;

    private long playlistSummaryRowsCacheGeneration;

    /// <summary>
    /// Raised after the summary view is replaced and code-behind selection restoration can run.
    /// </summary>
    internal event EventHandler<PlaylistSummaryViewAppliedEventArgs> PlaylistSummaryViewApplied;

    /// <summary>
    /// Gets or sets the current playlist summary sort parameters.
    /// </summary>
    public MainWindowViewModel.cSortParameters PlaylistSummarySortParameters
    {
        get => playlistSummarySortParameters;
        internal set
        {
            if (value == null
                || playlistSummarySortParameters == null
                || playlistSummarySortParameters.ColumnsName != value.ColumnsName
                || playlistSummarySortParameters.Direction != value.Direction)
            {
                playlistSummarySortParameters = value;
                RaisePropertyChanged(nameof(PlaylistSummarySortParameters));
            }
        }
    }

    /// <summary>
    /// Gets or sets the playlist detail/summary column menu visibility state.
    /// </summary>
    public Visibility ColumnSettingsVisibilityForPlaylist
    {
        get => columnSettingsVisibilityForPlaylist;
        internal set
        {
            if (columnSettingsVisibilityForPlaylist != value)
            {
                columnSettingsVisibilityForPlaylist = value;
                RaisePropertyChanged(nameof(ColumnSettingsVisibilityForPlaylist));
            }
        }
    }

    internal PlaylistColumnPresentationCommit CommitColumnPresentationWithoutNotification(
        Visibility visibility,
        PlaylistSummaryColumnSettings summaryColumnsSettings)
    {
        bool visibilityChanged = columnSettingsVisibilityForPlaylist != visibility;
        bool summaryColumnsChanged = !ReferenceEquals(playlistSummaryColumnsSettings, summaryColumnsSettings);
        columnSettingsVisibilityForPlaylist = visibility;
        playlistSummaryColumnsSettings = summaryColumnsSettings;
        return new PlaylistColumnPresentationCommit(visibilityChanged, summaryColumnsChanged);
    }

    internal void PublishColumnPresentation(PlaylistColumnPresentationCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        if (commit.VisibilityChanged)
        {
            RaisePropertyChanged(nameof(ColumnSettingsVisibilityForPlaylist));
        }
        if (commit.SummaryColumnsChanged)
        {
            RaisePropertyChanged(nameof(PlaylistSummaryColumnsSettings));
        }
    }

    internal PlaylistBindingModeCommit CommitBindingModeWithoutNotification(bool playlistDetailActive)
    {
        bool useAsyncBinding = !playlistDetailActive;
        bool detailActiveChanged = isPlaylistDetailViewActive != playlistDetailActive;
        bool asyncBindingChanged = useAsyncChartRowsViewBinding != useAsyncBinding;
        isPlaylistDetailViewActive = playlistDetailActive;
        useAsyncChartRowsViewBinding = useAsyncBinding;
        return new PlaylistBindingModeCommit(detailActiveChanged, asyncBindingChanged);
    }

    internal void PublishBindingMode(PlaylistBindingModeCommit commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        if (commit.DetailActiveChanged)
        {
            RaisePropertyChanged(nameof(IsPlaylistDetailViewActive));
        }
        if (commit.AsyncBindingChanged)
        {
            RaisePropertyChanged(nameof(UseAsyncChartRowsViewBinding));
        }
    }

    /// <summary>
    /// Gets or sets the rows currently displayed by the playlist summary table.
    /// </summary>
    public ObservableCollection<PlaylistSummaryRow> PlaylistSummaryView
    {
        get => playlistSummaryView;
    }

    public string PlaylistSummaryText
    {
        get => playlistSummaryText;
        internal set
        {
            string next = value ?? string.Empty;
            bool changed;
            lock (playlistSummaryTransitionLock)
            {
                changed = playlistSummaryText != next;
                playlistSummaryText = next;
            }
            if (changed)
            {
                RaisePropertyChanged(nameof(PlaylistSummaryText));
            }
        }
    }

    /// <summary>
    /// Gets whether the playlist summary table is active.
    /// </summary>
    public bool IsPlaylistSummaryMode
    {
        get => isPlaylistSummaryMode;
        internal set
        {
            bool changed;
            lock (playlistSummaryTransitionLock)
            {
                changed = isPlaylistSummaryMode != value;
                isPlaylistSummaryMode = value;
            }
            if (changed)
            {
                RaisePropertyChanged(nameof(IsPlaylistSummaryMode));
            }
        }
    }

    /// <summary>
    /// Gets whether the shared main table is showing playlist detail rows.
    /// </summary>
    public bool IsPlaylistDetailViewActive
    {
        get => isPlaylistDetailViewActive;
        internal set
        {
            if (isPlaylistDetailViewActive != value)
            {
                isPlaylistDetailViewActive = value;
                RaisePropertyChanged(nameof(IsPlaylistDetailViewActive));
            }
        }
    }

    /// <summary>
    /// Gets whether the main table should use async ItemsSource binding outside playlist detail view.
    /// </summary>
    public bool UseAsyncChartRowsViewBinding
    {
        get => useAsyncChartRowsViewBinding;
        internal set
        {
            if (useAsyncChartRowsViewBinding != value)
            {
                useAsyncChartRowsViewBinding = value;
                RaisePropertyChanged(nameof(UseAsyncChartRowsViewBinding));
            }
        }
    }

    /// <summary>
    /// Gets or sets the playlist summary header text displayed above the table.
    /// </summary>
    public string GridHeaderText
    {
        get => gridHeaderText;
        internal set
        {
            string next = value ?? string.Empty;
            if (gridHeaderText != next)
            {
                gridHeaderText = next;
                RaisePropertyChanged(nameof(GridHeaderText));
            }
        }
    }

    /// <summary>
    /// Gets or sets playlist summary column settings.
    /// </summary>
    public PlaylistSummaryColumnSettings PlaylistSummaryColumnsSettings
    {
        get => playlistSummaryColumnsSettings;
        internal set
        {
            if (ReferenceEquals(playlistSummaryColumnsSettings, value))
            {
                return;
            }

            playlistSummaryColumnsSettings = value;
            RaisePropertyChanged(nameof(PlaylistSummaryColumnsSettings));
        }
    }

    /// <summary>
    /// Gets or sets the playlist summary keyword filter text.
    /// </summary>
    public string PlaylistSummaryKeywordFilter
    {
        get => playlistSummaryKeywordFilter;
        internal set
        {
            string next = value ?? string.Empty;
            if (playlistSummaryKeywordFilter != next)
            {
                playlistSummaryKeywordFilter = next;
                RaisePropertyChanged(nameof(PlaylistSummaryKeywordFilter));
            }
        }
    }

    /// <summary>
    /// Gets playlist summary keyword warning text.
    /// </summary>
    public string PlaylistSummaryKeywordSearchWarningText => playlistSummaryKeywordSearchWarningText;

    /// <summary>
    /// Gets whether playlist summary keyword warning text is visible.
    /// </summary>
    public bool HasPlaylistSummaryKeywordSearchWarning => !string.IsNullOrWhiteSpace(playlistSummaryKeywordSearchWarningText);

    /// <summary>
    /// Gets or sets whether playlist summary keyword help is open.
    /// </summary>
    public bool IsPlaylistSummaryKeywordSearchHelpOpen
    {
        get => isPlaylistSummaryKeywordSearchHelpOpen;
        internal set
        {
            if (isPlaylistSummaryKeywordSearchHelpOpen != value)
            {
                isPlaylistSummaryKeywordSearchHelpOpen = value;
                RaisePropertyChanged(nameof(IsPlaylistSummaryKeywordSearchHelpOpen));
            }
        }
    }

    /// <summary>
    /// Gets playlist summary keyword suggestions.
    /// </summary>
    public ObservableCollection<KeywordSearchSuggestionItem> PlaylistSummaryKeywordSearchSuggestions => playlistSummaryKeywordSearchSuggestions;

    /// <summary>
    /// Gets or sets whether playlist summary keyword suggestion popup is open.
    /// </summary>
    public bool IsPlaylistSummaryKeywordSearchSuggestionPopupOpen
    {
        get => isPlaylistSummaryKeywordSearchSuggestionPopupOpen;
        internal set
        {
            if (isPlaylistSummaryKeywordSearchSuggestionPopupOpen != value)
            {
                isPlaylistSummaryKeywordSearchSuggestionPopupOpen = value;
                RaisePropertyChanged(nameof(IsPlaylistSummaryKeywordSearchSuggestionPopupOpen));
            }
        }
    }

    /// <summary>
    /// Gets the playlist summary keyword suggestion popup header text.
    /// </summary>
    public string PlaylistSummaryKeywordSearchSuggestionHeaderText => playlistSummaryKeywordSearchSuggestionHeaderText;

    /// <summary>
    /// Gets or sets the playlist summary owned filter.
    /// </summary>
    public MainWindowViewModel.PlaylistSummaryOwnedFilterType PlaylistSummaryOwnedFilter
    {
        get => playlistSummaryOwnedFilter;
        internal set
        {
            if (playlistSummaryOwnedFilter != value)
            {
                playlistSummaryOwnedFilter = value;
                RaisePropertyChanged(nameof(PlaylistSummaryOwnedFilter));
            }
        }
    }

    internal long LastPlaylistSummaryBuildCompletedTimestamp => Interlocked.Read(ref lastPlaylistSummaryBuildCompletedTimestamp);

    internal long BeginPlaylistSummaryPresentationGeneration()
    {
        lock (playlistSummaryTransitionLock)
        {
            return ++playlistSummaryPresentationGeneration;
        }
    }

    internal long BeginPlaylistSummaryDataRebuildGeneration()
    {
        lock (playlistSummaryTransitionLock)
        {
            return ++playlistSummaryDataRebuildGeneration;
        }
    }

    internal long IncrementPlaylistSummaryRowsCacheGeneration()
    {
        lock (playlistSummaryTransitionLock)
        {
            return ++playlistSummaryRowsCacheGeneration;
        }
    }

    internal long CurrentPlaylistSummaryPresentationGeneration
    {
        get
        {
            lock (playlistSummaryTransitionLock)
            {
                return playlistSummaryPresentationGeneration;
            }
        }
    }

    internal long CurrentPlaylistSummaryDataRebuildGeneration
    {
        get
        {
            lock (playlistSummaryTransitionLock)
            {
                return playlistSummaryDataRebuildGeneration;
            }
        }
    }

    internal long CurrentPlaylistSummaryRowsCacheGeneration
    {
        get
        {
            lock (playlistSummaryTransitionLock)
            {
                return playlistSummaryRowsCacheGeneration;
            }
        }
    }

    internal bool IsCurrentPlaylistSummaryDataRebuildGeneration(long generation)
    {
        lock (playlistSummaryTransitionLock)
        {
            return generation == playlistSummaryDataRebuildGeneration;
        }
    }

    internal bool TryApplyPlaylistSummary(PlaylistSummaryApplyRequest request)
    {
        if (request?.Rows == null)
        {
            throw new ArgumentException("Playlist summary rows are required.", nameof(request));
        }

        string nextSummaryText = request.SummaryText ?? string.Empty;
        bool rowsChanged;
        bool textChanged;
        lock (playlistSummaryTransitionLock)
        {
            if (!isPlaylistSummaryMode
                || request.PresentationGeneration != playlistSummaryPresentationGeneration
                || (request.DataRebuildGeneration.HasValue && request.DataRebuildGeneration.Value != playlistSummaryDataRebuildGeneration)
                || (request.CacheGeneration.HasValue && request.CacheGeneration.Value != playlistSummaryRowsCacheGeneration))
            {
                return false;
            }

            rowsChanged = !ReferenceEquals(playlistSummaryView, request.Rows);
            textChanged = playlistSummaryText != nextSummaryText;
            if (rowsChanged)
            {
                ObservableCollection<PlaylistSummaryRow> previousView = playlistSummaryView;
                playlistSummaryView = request.Rows;
                if (previousView != null)
                {
                    previousPlaylistSummaryViewWeakReference = new WeakReference<ObservableCollection<PlaylistSummaryRow>>(previousView);
                }
                Interlocked.Exchange(ref lastPlaylistSummaryBuildCompletedTimestamp, Stopwatch.GetTimestamp());
            }
            playlistSummaryText = nextSummaryText;
        }

        var publishExceptions = new List<Exception>();
        if (rowsChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryView)), publishExceptions);
        }
        if (textChanged)
        {
            TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryText)), publishExceptions);
        }
        TryPublish(
            () => PlaylistSummaryViewApplied?.Invoke(this, new PlaylistSummaryViewAppliedEventArgs(request.DataRebuildGeneration ?? 0L)),
            publishExceptions);
        if (publishExceptions.Count > 0)
        {
            throw new PlaylistSummaryPublishException(new AggregateException(publishExceptions));
        }
        return true;
    }

    private static void TryPublish(Action publish, List<Exception> exceptions)
    {
        try
        {
            publish();
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
    }

    internal bool TryGetPreviousPlaylistSummaryViewState(out bool alive, out int rowCount)
    {
        ObservableCollection<PlaylistSummaryRow> previousSummaryRows = null;
        alive = previousPlaylistSummaryViewWeakReference != null && previousPlaylistSummaryViewWeakReference.TryGetTarget(out previousSummaryRows);
        rowCount = alive ? previousSummaryRows.Count : 0;
        return previousPlaylistSummaryViewWeakReference != null;
    }

    internal void SetPlaylistSummaryKeywordSearchWarningText(string value)
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

    internal void SetPlaylistSummaryKeywordSearchSuggestionHeaderText(string value)
    {
        string next = value ?? string.Empty;
        if (playlistSummaryKeywordSearchSuggestionHeaderText != next)
        {
            playlistSummaryKeywordSearchSuggestionHeaderText = next;
            RaisePropertyChanged(nameof(PlaylistSummaryKeywordSearchSuggestionHeaderText));
        }
    }

}

internal sealed class PlaylistSummaryApplyRequest
{
    internal ObservableCollection<PlaylistSummaryRow> Rows { get; set; }

    internal string SummaryText { get; set; } = string.Empty;

    internal long PresentationGeneration { get; set; }

    internal long? DataRebuildGeneration { get; set; }

    internal long? CacheGeneration { get; set; }
}

internal sealed class PlaylistSummaryViewAppliedEventArgs : EventArgs
{
    internal PlaylistSummaryViewAppliedEventArgs(long dataRebuildGeneration)
    {
        DataRebuildGeneration = dataRebuildGeneration;
    }

    internal long DataRebuildGeneration { get; }
}

[Serializable]
internal sealed class PlaylistSummaryPublishException : Exception
{
    internal PlaylistSummaryPublishException()
    {
    }

    internal PlaylistSummaryPublishException(string message)
        : base(message)
    {
    }

    internal PlaylistSummaryPublishException(Exception innerException)
        : base("Playlist summary state was committed but publishing notifications failed.", innerException)
    {
    }

    internal PlaylistSummaryPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private PlaylistSummaryPublishException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}

internal sealed class PlaylistColumnPresentationCommit
{
    internal PlaylistColumnPresentationCommit(bool visibilityChanged, bool summaryColumnsChanged)
    {
        VisibilityChanged = visibilityChanged;
        SummaryColumnsChanged = summaryColumnsChanged;
    }

    internal bool VisibilityChanged { get; }

    internal bool SummaryColumnsChanged { get; }
}

internal sealed class PlaylistBindingModeCommit
{
    internal PlaylistBindingModeCommit(bool detailActiveChanged, bool asyncBindingChanged)
    {
        DetailActiveChanged = detailActiveChanged;
        AsyncBindingChanged = asyncBindingChanged;
    }

    internal bool DetailActiveChanged { get; }

    internal bool AsyncBindingChanged { get; }
}
