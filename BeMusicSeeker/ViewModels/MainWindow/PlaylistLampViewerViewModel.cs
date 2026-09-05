using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet;
using Livet.Commands;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Identifies which playlist surface a lamp viewer navigation intent targets.
/// </summary>
internal enum PlaylistLampViewerNavigationScope
{
    /// <summary>Navigate to one normal playlist folder.</summary>
    Folder,

    /// <summary>Navigate to the playlist table root across its normal folders.</summary>
    Overall
}

/// <summary>
/// Carries a lamp viewer category intent while preserving whether it targets one folder
/// or the complete normal-folder playlist table.
/// </summary>
internal sealed class PlaylistLampViewerNavigationRequest
{
    private PlaylistLampViewerNavigationRequest(
        string playlistId,
        PlaylistLampViewerNavigationScope scope,
        string folderName,
        PlaylistLampSegmentKind kind,
        PlaylistLampClearCategory? clearCategory,
        PlaylistLampRankCategory? rankCategory)
    {
        PlaylistId = playlistId ?? string.Empty;
        Scope = scope;
        FolderName = folderName;
        Kind = kind;
        ClearCategory = clearCategory;
        RankCategory = rankCategory;
    }

    /// <summary>Stable playlist identity used to re-resolve the active table.</summary>
    internal string PlaylistId { get; }

    /// <summary>Whether the request targets a folder or the complete table root.</summary>
    internal PlaylistLampViewerNavigationScope Scope { get; }

    /// <summary>
    /// Normal folder identity for a folder request. Overall requests intentionally leave
    /// this value unused; <see cref="Scope"/> is the route discriminator.
    /// </summary>
    internal string FolderName { get; }

    /// <summary>Semantic graph kind.</summary>
    internal PlaylistLampSegmentKind Kind { get; }

    /// <summary>Clear category when <see cref="Kind"/> is clear.</summary>
    internal PlaylistLampClearCategory? ClearCategory { get; }

    /// <summary>DJ-rank category when <see cref="Kind"/> is rank.</summary>
    internal PlaylistLampRankCategory? RankCategory { get; }

    /// <summary>Creates a folder-scoped viewer request from the core segment intent.</summary>
    /// <param name="request">Core segment intent carrying a normal folder.</param>
    /// <returns>The folder-scoped presentation intent.</returns>
    internal static PlaylistLampViewerNavigationRequest ForFolder(
        PlaylistLampSegmentInvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new PlaylistLampViewerNavigationRequest(
            request.PlaylistId,
            PlaylistLampViewerNavigationScope.Folder,
            request.FolderName,
            request.Kind,
            request.ClearCategory,
            request.RankCategory);
    }

    /// <summary>Creates an overall table-scoped viewer request.</summary>
    /// <param name="playlistId">Stable playlist identity.</param>
    /// <param name="kind">Semantic graph kind.</param>
    /// <param name="clearCategory">Clear category for a clear request.</param>
    /// <param name="rankCategory">DJ-rank category for a rank request.</param>
    /// <returns>The overall presentation intent.</returns>
    internal static PlaylistLampViewerNavigationRequest ForOverall(
        string playlistId,
        PlaylistLampSegmentKind kind,
        PlaylistLampClearCategory? clearCategory,
        PlaylistLampRankCategory? rankCategory)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("playlistId is required.", nameof(playlistId));
        }
        if (kind == PlaylistLampSegmentKind.Clear
            ? !clearCategory.HasValue || rankCategory.HasValue
            : !rankCategory.HasValue || clearCategory.HasValue)
        {
            throw new ArgumentException("The category must match the graph kind.", nameof(kind));
        }
        return new PlaylistLampViewerNavigationRequest(
            playlistId,
            PlaylistLampViewerNavigationScope.Overall,
            folderName: null,
            kind,
            clearCategory,
            rankCategory);
    }
}

/// <summary>
/// Owns the dispatcher-bound projection of one playlist lamp aggregation session.
/// </summary>
internal sealed class PlaylistLampViewerViewModel : ViewModel, IDisposable
{
    private readonly Dispatcher dispatcher;

    private readonly PlaylistLampViewerSession session;

    private readonly Action<PlaylistLampViewerNavigationRequest> navigationRequestSink;

    private readonly ObservableCollection<PlaylistLampViewerSegmentViewModel> clearSegments = [];

    private readonly ObservableCollection<PlaylistLampViewerSegmentViewModel> rankSegments = [];

    private readonly ObservableCollection<PlaylistLampViewerFolderRowViewModel> folderRows = [];

    private readonly ObservableCollection<PlaylistLampViewerStatCardViewModel> statisticsCards = [];

    private readonly TaskCompletionSource<PlaylistLampAggregationResult> firstPresentable =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PlaylistLampViewerSegmentViewModel selectedSegment;

    private PlaylistLampAggregationResult result;

    private DateTime? selectedAsOfDate;

    private ViewModelCommand latestCommand;

    private int disposed;

    /// <summary>Creates a dispatcher-bound viewer projection over one session.</summary>
    /// <param name="playlistId">Stable playlist identity.</param>
    /// <param name="playlistName">Current playlist display name.</param>
    /// <param name="session">The immutable aggregation session.</param>
    /// <param name="dispatcher">Dispatcher that owns the viewer window.</param>
    /// <param name="navigationRequestSink">Sink for accepted folder or overall category intents.</param>
    internal PlaylistLampViewerViewModel(
        string playlistId,
        string playlistName,
        PlaylistLampViewerSession session,
        Dispatcher dispatcher,
        Action<PlaylistLampViewerNavigationRequest> navigationRequestSink)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("playlistId is required.", nameof(playlistId));
        }
        PlaylistId = playlistId;
        PlaylistName = playlistName ?? string.Empty;
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.navigationRequestSink = navigationRequestSink;
        result = session.Current;
        selectedAsOfDate = result?.Query?.SelectedLocalDate;
        session.ResultChanged += SessionResultChanged;
        ApplyResult(result);
    }

    /// <summary>Stable playlist identity.</summary>
    public string PlaylistId { get; }

    /// <summary>Current playlist display name.</summary>
    public string PlaylistName { get; }

    /// <summary>
    /// 現在表示している score の local as-of date。null は Latest/current です。
    /// DatePicker のカレンダー選択だけがこの値を変更します。
    /// </summary>
    public DateTime? SelectedAsOfDate
    {
        get => selectedAsOfDate;
        set
        {
            DateTime? normalized = value.HasValue
                ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified)
                : null;
            if (Nullable.Equals(selectedAsOfDate, normalized))
            {
                return;
            }
            selectedAsOfDate = normalized;
            RaisePropertyChanged(nameof(SelectedAsOfDate));
            session.UpdateSelectedAsOfDateAsync(normalized)
                .ObserveFault("PlaylistLampViewerViewModel.SelectedAsOfDate");
        }
    }

    /// <summary>Latest/current score snapshotへ戻す command。</summary>
    public ViewModelCommand LatestCommand => latestCommand ??= new ViewModelCommand(
        () => SelectedAsOfDate = null);

    /// <summary>historical DatePicker の最古選択可能日。</summary>
    public DateTime? AsOfDateStart => result?.HistoricalDateRange?.EarliestLocalDate;

    /// <summary>historical DatePicker の最新選択可能日。</summary>
    public DateTime? AsOfDateEnd => result?.HistoricalDateRange?.LatestLocalDate;

    /// <summary>history row があり DatePicker を選択できるかどうか。</summary>
    public bool IsAsOfDateSelectionEnabled => result?.HistoricalDateRange?.HasHistoricalDates == true;

    /// <summary>as-of header 用 localised status。</summary>
    public string AsOfDateStatusText
    {
        get
        {
            if (!SelectedAsOfDate.HasValue)
            {
                return Resources.PlaylistLampViewer_latest;
            }
            if (result?.HistoricalStatus == PlaylistLampHistoricalSnapshotStatus.Unavailable)
            {
                return Resources.PlaylistLampViewer_historical_unavailable;
            }
            return SelectedAsOfDate.Value.ToString("d", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Localized native-window title.</summary>
    public string WindowTitle => string.Format(
        CultureInfo.CurrentCulture,
        Resources.PlaylistLampViewer_title_format,
        PlaylistName);

    /// <summary>Current aggregation state. The state is consumed by owner lifecycle, not shown as routine status text.</summary>
    public PlaylistLampViewerState State => result?.State ?? PlaylistLampViewerState.Loading;

    /// <summary>Localized score-source value for the source statistic card.</summary>
    public string ScoreSourceText
    {
        get
        {
            PlaylistLampScoreSnapshot snapshot = result?.ScoreSnapshot;
            string source = snapshot?.LampSource switch
            {
                PlaylistLampScoreSource.Lr2 => Resources.PlaylistLampViewer_source_lr2,
                PlaylistLampScoreSource.Beatoraja => Resources.PlaylistLampViewer_source_beatoraja,
                _ => Resources.PlaylistLampViewer_source_none
            };
            if (snapshot?.Health == PlaylistLampScoreHealth.Failed)
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.PlaylistLampViewer_source_status_format,
                    source,
                    Resources.PlaylistLampViewer_source_failed);
            }
            return source;
        }
    }

    /// <summary>Whether score-dependent graph and card values are available.</summary>
    public bool IsScoreDataAvailable => result?.ScoreDataAvailable == true;

    /// <summary>Whether the current ready result is score-degraded.</summary>
    public bool IsDegraded => State == PlaylistLampViewerState.Ready && !IsScoreDataAvailable;

    /// <summary>Whether an aggregation failure is being displayed.</summary>
    public bool IsFailure => State == PlaylistLampViewerState.Failed;

    /// <summary>Whether the source playlist has been deleted.</summary>
    public bool IsDeleted => State == PlaylistLampViewerState.Deleted;

    /// <summary>Whether the result contains no active chart entries.</summary>
    public bool IsEmpty => State == PlaylistLampViewerState.Empty;

    /// <summary>Whether graph controls can be displayed.</summary>
    public bool IsGraphVisible => (State is PlaylistLampViewerState.Ready or PlaylistLampViewerState.Empty)
        && IsScoreDataAvailable;

    /// <summary>Whether folder rows are available to display.</summary>
    public bool HasFolderRows => folderRows.Count > 0;

    /// <summary>Current global clear-lamp segments in semantic order.</summary>
    public ObservableCollection<PlaylistLampViewerSegmentViewModel> ClearSegments => clearSegments;

    /// <summary>Current global DJ-rank segments in semantic order.</summary>
    public ObservableCollection<PlaylistLampViewerSegmentViewModel> RankSegments => rankSegments;

    /// <summary>Current global clear segments with positive display width.</summary>
    public IEnumerable<PlaylistLampViewerSegmentViewModel> PositiveClearSegments
        => clearSegments.Where(segment => segment.HasPositiveWidth);

    /// <summary>Current global DJ-rank segments with positive display width.</summary>
    public IEnumerable<PlaylistLampViewerSegmentViewModel> PositiveRankSegments
        => rankSegments.Where(segment => segment.HasPositiveWidth);

    /// <summary>Current normal-folder rows in source order.</summary>
    public ObservableCollection<PlaylistLampViewerFolderRowViewModel> FolderRows => folderRows;

    /// <summary>
    /// Current statistics in their visual and semantic order. Score-dependent cards are
    /// omitted when the score source is unavailable or failed, while the legacy local
    /// playlist last update remains the final card in either state.
    /// </summary>
    public ObservableCollection<PlaylistLampViewerStatCardViewModel> StatisticsCards => statisticsCards;

    /// <summary>Currently selected folder segment in this window.</summary>
    public PlaylistLampViewerSegmentViewModel SelectedSegment => selectedSegment;

    /// <summary>Latest immutable result accepted by this projection.</summary>
    internal PlaylistLampAggregationResult CurrentResult => result;

    /// <summary>
    /// Raised on the viewer dispatcher when a deleted or failed terminal result arrives.
    /// The owner uses this to close one child window without changing other windows.
    /// </summary>
    internal event EventHandler<PlaylistLampAggregationResultChangedEventArgs> TerminalResultChanged;

    /// <summary>Starts the underlying session's first snapshot build.</summary>
    /// <returns>The session completion task.</returns>
    internal Task StartAsync(CancellationToken cancellationToken = default) => session.StartAsync(cancellationToken);

    /// <summary>
    /// Starts the session and waits until the first non-loading result is presented.
    /// </summary>
    /// <param name="cancellationToken">Cancellation used by owner shutdown.</param>
    /// <returns>The first ready, empty, degraded, deleted, or failed result.</returns>
    internal async Task<PlaylistLampAggregationResult> StartAndWaitForPresentableAsync(
        CancellationToken cancellationToken = default)
    {
        Task startTask = session.StartAsync(cancellationToken);
        try
        {
            PlaylistLampAggregationResult presentable = await firstPresentable.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(true);
            await startTask.ConfigureAwait(true);
            return presentable;
        }
        catch
        {
            await startTask.ConfigureAwait(true);
            throw;
        }
    }

    /// <summary>Dispatches a segment invocation onto the owner workflow.</summary>
    /// <param name="segment">The clicked segment projection.</param>
    internal void InvokeSegment(PlaylistLampViewerSegmentViewModel segment)
    {
        if (segment == null || !segment.IsInvokable || State != PlaylistLampViewerState.Ready || !IsScoreDataAvailable)
        {
            return;
        }

        foreach (PlaylistLampViewerSegmentViewModel candidate in EnumerateSegments())
        {
            candidate.IsSelected = ReferenceEquals(candidate, segment);
        }
        selectedSegment = segment;
        RaisePropertyChanged(nameof(SelectedSegment));
        PlaylistLampViewerNavigationRequest request = segment.CreateInvocationRequest();
        if (request != null)
        {
            navigationRequestSink?.Invoke(request);
        }
    }

    /// <summary>Releases the session event subscription and session lifetime exactly once.</summary>
    public new void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        session.ResultChanged -= SessionResultChanged;
        firstPresentable.TrySetCanceled();
        TerminalResultChanged = null;
        session.Dispose();
    }

    private void SessionResultChanged(object sender, PlaylistLampAggregationResultChangedEventArgs e)
    {
        if (e?.Result == null || Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        void apply() => ApplyResult(e.Result);
        if (dispatcher.CheckAccess())
        {
            apply();
            return;
        }
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
        try
        {
            dispatcher.BeginInvoke(DispatcherPriority.DataBind, apply);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher shutdown races are terminal for a modeless child window.
        }
    }

    private void ApplyResult(PlaylistLampAggregationResult next)
    {
        if (next == null || Volatile.Read(ref disposed) != 0)
        {
            return;
        }
        if (!dispatcher.CheckAccess())
        {
            SessionResultChanged(this, new PlaylistLampAggregationResultChangedEventArgs(0L, next));
            return;
        }
        result = next;
        DateTime? nextSelectedAsOfDate = next.Query?.SelectedLocalDate;
        if (!Nullable.Equals(selectedAsOfDate, nextSelectedAsOfDate))
        {
            selectedAsOfDate = nextSelectedAsOfDate;
        }
        selectedSegment = null;
        clearSegments.Clear();
        rankSegments.Clear();
        folderRows.Clear();
        statisticsCards.Clear();

        if (next.State is PlaylistLampViewerState.Ready or PlaylistLampViewerState.Empty
            && next.ScoreDataAvailable)
        {
            foreach (PlaylistLampSegment segment in FilterClearSegments(next))
            {
                clearSegments.Add(new PlaylistLampViewerSegmentViewModel(segment, PlaylistId, this));
            }
            foreach (PlaylistLampSegment segment in next.RankSegments ?? [])
            {
                rankSegments.Add(new PlaylistLampViewerSegmentViewModel(segment, PlaylistId, this));
            }
            foreach (PlaylistLampFolderRow row in next.FolderRows ?? [])
            {
                if (string.Equals(row.FolderName, "[NO SONG]", StringComparison.Ordinal))
                {
                    continue;
                }
                folderRows.Add(new PlaylistLampViewerFolderRowViewModel(row, PlaylistId, this));
            }
        }

        AddStatisticsCards(next);
        if (next.State is not PlaylistLampViewerState.Loading)
        {
            firstPresentable.TrySetResult(next);
        }
        RaisePropertyChanged(string.Empty);
        if (next.State is PlaylistLampViewerState.Deleted or PlaylistLampViewerState.Failed)
        {
            TerminalResultChanged?.Invoke(
                this,
                new PlaylistLampAggregationResultChangedEventArgs(0L, next));
        }
    }

    private IEnumerable<PlaylistLampSegment> FilterClearSegments(PlaylistLampAggregationResult next)
    {
        return (next.ClearSegments ?? [])
            .Where(segment => next.ScoreSnapshot?.LampSource != PlaylistLampScoreSource.Lr2
                || (segment.ClearCategory is not PlaylistLampClearCategory.MAX
                    and not PlaylistLampClearCategory.EXHARD));
    }

    private void AddStatisticsCards(PlaylistLampAggregationResult next)
    {
        PlaylistLampStatistics statistics = next.Statistics;
        statisticsCards.Add(Card(Resources.PlaylistLampViewer_total, FormatCount(statistics?.TotalCount)));
        statisticsCards.Add(Card(Resources.PlaylistLampViewer_owned, FormatCount(statistics?.OwnedCount)));
        statisticsCards.Add(Card(Resources.PlaylistLampViewer_missing, FormatCount(statistics?.MissingCount)));
        statisticsCards.Add(Card(Resources.PlaylistLampViewer_ownership_rate, FormatPercentage(statistics?.OwnershipPercentage)));
        statisticsCards.Add(Card(Resources.PlaylistLampViewer_score_source, ScoreSourceText));
        if (next.ScoreDataAvailable
            && (next.State is PlaylistLampViewerState.Ready or PlaylistLampViewerState.Empty))
        {
            statisticsCards.Add(Card(Resources.PlaylistLampViewer_played, FormatCount(statistics?.PlayedCount)));
            statisticsCards.Add(Card(Resources.PlaylistLampViewer_no_play, FormatCount(statistics?.UnplayedCount)));
            statisticsCards.Add(Card(Resources.PlaylistLampViewer_play_rate, FormatPercentage(statistics?.PlayPercentage)));
            statisticsCards.Add(Card(Resources.PlaylistLampViewer_average_ex_rate, FormatPercentage(statistics?.AverageExRatePercentage)));
            statisticsCards.Add(Card(Resources.PlaylistLampViewer_clear_rate, FormatPercentage(statistics?.ClearPercentage)));
        }
        statisticsCards.Add(Card(
            Resources.PlaylistLampViewer_playlist_last_update,
            FormatTimestamp(statistics?.PlaylistLastUpdated),
            isWide: true));
    }

    private static PlaylistLampViewerStatCardViewModel Card(
        string label,
        string value,
        bool isWide = false)
    {
        return new PlaylistLampViewerStatCardViewModel(label, value, isWide);
    }

    private static string FormatCount(int? value)
    {
        return value.HasValue
            ? value.Value.ToString("N0", CultureInfo.CurrentCulture)
            : Unavailable;
    }

    private static string FormatPercentage(double? value)
    {
        return value.HasValue
            ? PlaylistLampViewerPercentageFormatter.Format(value.Value, CultureInfo.CurrentCulture)
            : Unavailable;
    }

    private static string FormatTimestamp(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToString("g", CultureInfo.CurrentCulture)
            : Unavailable;
    }

    private static string Unavailable => Resources.PlaylistLampViewer_unavailable;

    private IEnumerable<PlaylistLampViewerSegmentViewModel> EnumerateSegments()
    {
        foreach (PlaylistLampViewerSegmentViewModel segment in clearSegments)
        {
            yield return segment;
        }
        foreach (PlaylistLampViewerSegmentViewModel segment in rankSegments)
        {
            yield return segment;
        }
        foreach (PlaylistLampViewerFolderRowViewModel row in folderRows)
        {
            foreach (PlaylistLampViewerSegmentViewModel segment in row.ClearSegments)
            {
                yield return segment;
            }
            foreach (PlaylistLampViewerSegmentViewModel segment in row.RankSegments)
            {
                yield return segment;
            }
        }
    }
}

/// <summary>Dispatcher-bound presentation projection for one graph segment.</summary>
internal sealed class PlaylistLampViewerSegmentViewModel : ViewModel
{
    private readonly PlaylistLampSegment segment;

    private readonly string playlistId;

    private readonly PlaylistLampViewerViewModel owner;

    private bool isSelected;

    /// <summary>Creates a segment projection.</summary>
    /// <param name="segment">Immutable aggregation segment.</param>
    /// <param name="playlistId">Stable playlist identity.</param>
    /// <param name="owner">Owning viewer projection.</param>
    internal PlaylistLampViewerSegmentViewModel(
        PlaylistLampSegment segment,
        string playlistId,
        PlaylistLampViewerViewModel owner)
    {
        this.segment = segment ?? throw new ArgumentNullException(nameof(segment));
        this.playlistId = playlistId ?? string.Empty;
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Label = PlaylistLampViewerLabels.GetLabel(segment);
        CategoryKey = segment.ClearCategory?.ToString() ?? segment.RankCategory?.ToString() ?? string.Empty;
        PaletteKey = segment.ClearCategory is not null
            ? "Clear." + CategoryKey
            : "Rank." + CategoryKey;
    }

    /// <summary>Semantic segment kind.</summary>
    public PlaylistLampSegmentKind Kind => segment.Kind;

    /// <summary>Semantic category label.</summary>
    public string Label { get; }

    /// <summary>Non-localized category identity for styling.</summary>
    public string CategoryKey { get; }

    /// <summary>Category identity scoped to the clear or rank graph palette.</summary>
    public string PaletteKey { get; }

    /// <summary>Chart count in this segment.</summary>
    public int Count => segment.Count;

    /// <summary>Folder denominator for this segment.</summary>
    public int Denominator => segment.Denominator;

    /// <summary>Percentage in the folder/global denominator.</summary>
    public double? Percentage => segment.Percentage;

    /// <summary>Positive segment width in percentage points.</summary>
    public double WidthPercentage => segment.PositiveWidthPercentage ?? 0d;

    /// <summary>Whether this segment has positive display width.</summary>
    public bool HasPositiveWidth => segment.PositiveWidthPercentage.HasValue;

    /// <summary>Whether this segment can publish a typed navigation request.</summary>
    public bool IsInvokable => segment.IsInvokable
        || (segment.FolderName == null
            && segment.Count > 0
            && segment.ScoreDataAvailable);

    /// <summary>Whether this segment should be materialized as a positive bar control.</summary>
    public Visibility BarVisibility => HasPositiveWidth ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Localized adaptive percentage text for this segment.</summary>
    public string PercentageText => Percentage.HasValue
        ? PlaylistLampViewerPercentageFormatter.Format(Percentage.Value, CultureInfo.CurrentCulture)
        : Resources.PlaylistLampViewer_unavailable;

    /// <summary>Localized count and percentage tooltip/accessibility text.</summary>
    public string DetailText => Percentage.HasValue
        ? string.Format(
            CultureInfo.CurrentCulture,
            Resources.PlaylistLampViewer_segment_detail_adaptive_format,
            Label,
            Count,
            PercentageText)
        : string.Format(
            CultureInfo.CurrentCulture,
            Resources.PlaylistLampViewer_segment_unavailable_detail_format,
            Label,
            Count,
            Resources.PlaylistLampViewer_unavailable);

    /// <summary>Localized automation name.</summary>
    public string AutomationName => DetailText;

    /// <summary>Localized selected-state automation status.</summary>
    public string SelectionStatus => IsSelected
        ? Resources.PlaylistLampViewer_selected
        : Resources.PlaylistLampViewer_not_selected;

    /// <summary>Whether this segment is selected in the current viewer window.</summary>
    public bool IsSelected
    {
        get => isSelected;
        internal set
        {
            if (isSelected == value)
            {
                return;
            }
            isSelected = value;
            RaisePropertyChanged(nameof(IsSelected));
            RaisePropertyChanged(nameof(SelectionStatus));
        }
    }

    /// <summary>Creates the typed intent represented by this folder segment.</summary>
    /// <returns>The typed intent, or <see langword="null"/> for non-invokable segments.</returns>
    internal PlaylistLampViewerNavigationRequest CreateInvocationRequest()
    {
        if (segment.FolderName == null)
        {
            return IsInvokable
                ? PlaylistLampViewerNavigationRequest.ForOverall(
                    playlistId,
                    segment.Kind,
                    segment.ClearCategory,
                    segment.RankCategory)
                : null;
        }
        PlaylistLampSegmentInvocationRequest request = segment.CreateInvocationRequest(playlistId);
        return request == null
            ? null
            : PlaylistLampViewerNavigationRequest.ForFolder(request);
    }

    /// <summary>Invokes this segment through its owning viewer.</summary>
    internal void Invoke() => owner.InvokeSegment(this);
}

/// <summary>
/// Formats viewer percentages without turning a small positive value into a displayed
/// zero. The regular percentage format remains the source for ordinary values, while
/// values below its two-decimal positive threshold use a localized lower-bound phrase.
/// </summary>
internal static class PlaylistLampViewerPercentageFormatter
{
    private const double MinimumTwoDecimalPositivePercentage = 0.005d;

    /// <summary>Formats a percentage for a viewer label, preserving positive semantics.</summary>
    /// <param name="percentage">Percentage value in percentage points.</param>
    /// <param name="culture">Culture used for numeric formatting.</param>
    /// <returns>A localized percentage that cannot misrepresent a small positive value as zero.</returns>
    internal static string Format(double percentage, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (!double.IsFinite(percentage) || percentage < 0d)
        {
            return Resources.PlaylistLampViewer_unavailable;
        }

        if (percentage > 0d && percentage < MinimumTwoDecimalPositivePercentage)
        {
            string minimumPositive = string.Format(
                culture,
                Resources.PlaylistLampViewer_percentage_format,
                MinimumTwoDecimalPositivePercentage * 2d);
            return string.Format(
                culture,
                Resources.PlaylistLampViewer_percentage_less_than_format,
                minimumPositive);
        }

        return string.Format(
            culture,
            Resources.PlaylistLampViewer_percentage_format,
            percentage);
    }
}

/// <summary>Dispatcher-bound presentation projection for one normal folder row.</summary>
internal sealed class PlaylistLampViewerFolderRowViewModel : ViewModel
{
    /// <summary>Creates a folder-row projection.</summary>
    /// <param name="row">Immutable aggregation row.</param>
    /// <param name="playlistId">Stable playlist identity.</param>
    /// <param name="owner">Owning viewer projection.</param>
    internal PlaylistLampViewerFolderRowViewModel(
        PlaylistLampFolderRow row,
        string playlistId,
        PlaylistLampViewerViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(row);
        FolderName = row.FolderName;
        Count = row.Count;
        OwnedCount = row.OwnedCount;
        MissingCount = row.MissingCount;
        ClearSegments = new ObservableCollection<PlaylistLampViewerSegmentViewModel>(
            (row.ClearSegments ?? [])
                .Where(segment => segment != null)
                .Where(segment => segment.ClearCategory is not PlaylistLampClearCategory.MAX
                    and not PlaylistLampClearCategory.EXHARD
                    || owner.CurrentResult?.ScoreSnapshot?.LampSource != PlaylistLampScoreSource.Lr2)
                .Select(segment => new PlaylistLampViewerSegmentViewModel(segment, playlistId, owner)));
        RankSegments = new ObservableCollection<PlaylistLampViewerSegmentViewModel>(
            (row.RankSegments ?? [])
                .Where(segment => segment != null)
                .Select(segment => new PlaylistLampViewerSegmentViewModel(segment, playlistId, owner)));
    }

    /// <summary>Normal folder display name.</summary>
    public string FolderName { get; }

    /// <summary>Folder chart count.</summary>
    public int Count { get; }

    /// <summary>Folder owned count.</summary>
    public int OwnedCount { get; }

    /// <summary>Folder missing count.</summary>
    public int MissingCount { get; }

    /// <summary>Clear segments in semantic order.</summary>
    public ObservableCollection<PlaylistLampViewerSegmentViewModel> ClearSegments { get; }

    /// <summary>DJ-rank segments in semantic order.</summary>
    public ObservableCollection<PlaylistLampViewerSegmentViewModel> RankSegments { get; }

    /// <summary>Clear segments that occupy a positive-width bar.</summary>
    public IEnumerable<PlaylistLampViewerSegmentViewModel> PositiveClearSegments
        => ClearSegments.Where(segment => segment.HasPositiveWidth);

    /// <summary>DJ-rank segments that occupy a positive-width bar.</summary>
    public IEnumerable<PlaylistLampViewerSegmentViewModel> PositiveRankSegments
        => RankSegments.Where(segment => segment.HasPositiveWidth);

    /// <summary>Localized numeric folder chart count using the current culture's N0 format.</summary>
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>One visible statistic card in the lamp viewer.</summary>
internal sealed class PlaylistLampViewerStatCardViewModel
{
    private const double RegularCardWidth = 100d;

    private const double WideCardWidth = 180d;

    /// <summary>Creates a statistic card.</summary>
    /// <param name="label">Localized card label.</param>
    /// <param name="value">Localized card value.</param>
    /// <param name="isWide">Whether the card has the wider last-update layout role.</param>
    internal PlaylistLampViewerStatCardViewModel(string label, string value, bool isWide = false)
    {
        Label = label ?? string.Empty;
        Value = value ?? string.Empty;
        IsWideLayout = isWide;
    }

    /// <summary>Localized label.</summary>
    public string Label { get; }

    /// <summary>Localized value.</summary>
    public string Value { get; }

    /// <summary>Whether this card uses the wider semantic layout role.</summary>
    public bool IsWideLayout { get; }

    /// <summary>Width assigned to this card's rendered layout role.</summary>
    public double CardWidth => IsWideLayout ? WideCardWidth : RegularCardWidth;
}

/// <summary>Localized labels for semantic lamp categories.</summary>
internal static class PlaylistLampViewerLabels
{
    /// <summary>Gets a localized label for one immutable segment.</summary>
    /// <param name="segment">Immutable segment.</param>
    /// <returns>Localized category label.</returns>
    internal static string GetLabel(PlaylistLampSegment segment)
    {
        if (segment?.ClearCategory is { } clear)
        {
            return clear switch
            {
                PlaylistLampClearCategory.MAX => Resources.PlaylistLampViewer_clear_max,
                PlaylistLampClearCategory.PERFECT => Resources.PlaylistLampViewer_clear_perfect,
                PlaylistLampClearCategory.FC => Resources.PlaylistLampViewer_clear_fc,
                PlaylistLampClearCategory.EXHARD => Resources.PlaylistLampViewer_clear_exhard,
                PlaylistLampClearCategory.HARD => Resources.PlaylistLampViewer_clear_hard,
                PlaylistLampClearCategory.NORMAL => Resources.PlaylistLampViewer_clear_normal,
                PlaylistLampClearCategory.EASY => Resources.PlaylistLampViewer_clear_easy,
                PlaylistLampClearCategory.ASSIST => Resources.PlaylistLampViewer_clear_assist,
                PlaylistLampClearCategory.FAILED => Resources.PlaylistLampViewer_clear_failed,
                PlaylistLampClearCategory.NP => Resources.PlaylistLampViewer_clear_np,
                _ => string.Empty
            };
        }
        if (segment?.RankCategory is { } rank)
        {
            return rank switch
            {
                PlaylistLampRankCategory.AAA => Resources.PlaylistLampViewer_rank_aaa,
                PlaylistLampRankCategory.AA => Resources.PlaylistLampViewer_rank_aa,
                PlaylistLampRankCategory.A => Resources.PlaylistLampViewer_rank_a,
                PlaylistLampRankCategory.B => Resources.PlaylistLampViewer_rank_b,
                PlaylistLampRankCategory.C => Resources.PlaylistLampViewer_rank_c,
                PlaylistLampRankCategory.D => Resources.PlaylistLampViewer_rank_d,
                PlaylistLampRankCategory.E => Resources.PlaylistLampViewer_rank_e,
                PlaylistLampRankCategory.F => Resources.PlaylistLampViewer_rank_f,
                PlaylistLampRankCategory.NP => Resources.PlaylistLampViewer_rank_np,
                _ => string.Empty
            };
        }
        return string.Empty;
    }
}
