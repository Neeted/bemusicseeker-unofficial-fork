using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryTerminalOwner
{
    private readonly PlayHistoryPresentationState state;
    private readonly MainChartListViewModel mainChartList;
    private readonly PlaylistWorkspaceViewModel playlistWorkspace;
    private readonly RegularChartListOwner regularChartListOwner;
    private readonly PlaylistDetailBuildState playlistDetailBuildState;
    private readonly PlaylistDetailViewState playlistDetailViewState;
    private readonly Action<string> publishPropertyChanged;
    private readonly Action<PlaylistSourceClearCommitResult> logPlaylistSourceClear;

    internal PlayHistoryTerminalOwner(
        PlayHistoryPresentationState state,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        RegularChartListOwner regularChartListOwner,
        PlaylistDetailBuildState playlistDetailBuildState,
        PlaylistDetailViewState playlistDetailViewState,
        Action<string> publishPropertyChanged,
        Action<PlaylistSourceClearCommitResult> logPlaylistSourceClear)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.mainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        this.regularChartListOwner = regularChartListOwner ?? throw new ArgumentNullException(nameof(regularChartListOwner));
        this.playlistDetailBuildState = playlistDetailBuildState ?? throw new ArgumentNullException(nameof(playlistDetailBuildState));
        this.playlistDetailViewState = playlistDetailViewState ?? throw new ArgumentNullException(nameof(playlistDetailViewState));
        this.publishPropertyChanged = publishPropertyChanged ?? throw new ArgumentNullException(nameof(publishPropertyChanged));
        this.logPlaylistSourceClear = logPlaylistSourceClear ?? throw new ArgumentNullException(nameof(logPlaylistSourceClear));
    }

    internal PlayHistoryTerminalCommitResult TryApply(PlayHistoryTerminalRequest request)
    {
        if (request?.ViewState == null || request.Rows == null || request.MainRowsRequest == null)
        {
            throw new ArgumentException("A complete play-history terminal request is required.", nameof(request));
        }

        var result = new PlayHistoryTerminalCommitResult();
        try
        {
            MainChartListCoordinatedRowsApplyResult coordinated = mainChartList.ApplyCoordinatedRows(
                request.MainRowsRequest,
                commitRows =>
                {
                    lock (state.SyncRoot)
                    {
                        if (!state.IsFresh(request.ViewState))
                        {
                            return false;
                        }
                        commitRows();
                        result.PlaylistSourceClear = playlistDetailBuildState.CommitSourceClear(playlistDetailViewState);
                        result.BindingMode = playlistWorkspace.CommitBindingModeWithoutNotification(playlistDetailActive: false);
                        result.ColumnPresentation = playlistWorkspace.CommitColumnPresentationWithoutNotification(
                            request.ColumnSelection.PlaylistColumnSettingsVisibility,
                            request.ColumnSelection.PlaylistSummaryColumnsSettings);
                        if (request.ColumnSelection.AppliedMode.HasValue)
                        {
                            regularChartListOwner.CommitExternalColumnMode(request.ColumnSelection.AppliedMode);
                        }
                        regularChartListOwner.ResetDerivedCaches();

                        if (request.ArchivePeriodTree != null
                            && !ReferenceEquals(state.ArchivePeriodTree, request.ArchivePeriodTree)
                            && !PlayHistoryPresentationState.AreSameArchiveTree(state.ArchivePeriodTree, request.ArchivePeriodTree))
                        {
                            state.ArchivePeriodTree = request.ArchivePeriodTree;
                            result.ArchivePeriodTreeChanged = true;
                        }

                        IReadOnlyList<PlayHistorySummaryCard> summaryCards = request.SummaryCards ?? [];
                        if (!ReferenceEquals(state.SummaryCards, summaryCards)
                            && !PlayHistoryPresentationState.AreSameSummaryCards(state.SummaryCards, summaryCards))
                        {
                            state.SummaryCards = summaryCards;
                            result.SummaryCardsChanged = true;
                        }

                        string diagnosticText = request.DiagnosticText ?? string.Empty;
                        if (state.DiagnosticText != diagnosticText)
                        {
                            state.DiagnosticText = diagnosticText;
                            result.DiagnosticTextChanged = true;
                        }

                        state.CurrentView = request.ViewState;
                        result.Applied = true;
                        return true;
                    }
                },
                () =>
                {
                    List<Exception> featurePublishExceptions = [];
                    if (result.ColumnPresentation != null)
                    {
                        TryPublish(() => playlistWorkspace.PublishColumnPresentation(result.ColumnPresentation), featurePublishExceptions);
                    }
                    if (result.BindingMode != null)
                    {
                        TryPublish(() => playlistWorkspace.PublishBindingMode(result.BindingMode), featurePublishExceptions);
                    }
                    if (result.ArchivePeriodTreeChanged)
                    {
                        TryPublish(() => publishPropertyChanged("PlayHistoryArchivePeriodTree"), featurePublishExceptions);
                    }
                    if (result.SummaryCardsChanged)
                    {
                        TryPublish(() => publishPropertyChanged("PlayHistorySummaryCards"), featurePublishExceptions);
                    }
                    if (result.DiagnosticTextChanged)
                    {
                        TryPublish(() => publishPropertyChanged("PlayHistorySummaryDiagnosticText"), featurePublishExceptions);
                    }
                    if (result.PlaylistSourceClear != null)
                    {
                        TryPublish(
                            () =>
                            {
                                playlistDetailBuildState.PublishSourceClear(result.PlaylistSourceClear);
                                logPlaylistSourceClear(result.PlaylistSourceClear);
                            },
                            featurePublishExceptions);
                    }
                    if (featurePublishExceptions.Count > 0)
                    {
                        throw new AggregateException(featurePublishExceptions);
                    }
                });
            result.MainRowsApply = coordinated.RowsApply;
            return result;
        }
        catch (MainChartListCoordinatedPublishException ex)
        {
            throw new PlayHistoryTerminalPublishException(ex, ownershipTransferred: true);
        }
    }

    private static void TryPublish(Action publish, ICollection<Exception> exceptions)
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
}

internal sealed class PlayHistoryTerminalRequest
{
    internal PlayHistoryViewState ViewState { get; set; }
    internal IList Rows { get; set; }
    internal MainChartListRowsApplyRequest MainRowsRequest { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal IReadOnlyList<PlayHistoryPeriodTreeItem> ArchivePeriodTree { get; set; }
    internal IReadOnlyList<PlayHistorySummaryCard> SummaryCards { get; set; }
    internal string DiagnosticText { get; set; }
}

internal sealed class PlayHistoryTerminalCommitResult
{
    internal bool Applied { get; set; }
    internal MainChartListRowsApplyResult MainRowsApply { get; set; }
    internal PlaylistSourceClearCommitResult PlaylistSourceClear { get; set; }
    internal PlaylistColumnPresentationCommit ColumnPresentation { get; set; }
    internal PlaylistBindingModeCommit BindingMode { get; set; }
    internal bool ArchivePeriodTreeChanged { get; set; }
    internal bool SummaryCardsChanged { get; set; }
    internal bool DiagnosticTextChanged { get; set; }
}

[Serializable]
internal sealed class PlayHistoryTerminalPublishException : Exception
{
    internal PlayHistoryTerminalPublishException()
    {
    }

    internal PlayHistoryTerminalPublishException(string message)
        : base(message)
    {
    }

    internal PlayHistoryTerminalPublishException(Exception innerException)
        : base("Play-history terminal state was committed but publishing notifications failed.", innerException)
    {
    }

    internal PlayHistoryTerminalPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal PlayHistoryTerminalPublishException(Exception innerException, bool ownershipTransferred)
        : base("Play-history terminal state was committed but publishing notifications failed.", innerException)
    {
        OwnershipTransferred = ownershipTransferred;
    }

    private PlayHistoryTerminalPublishException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        OwnershipTransferred = info.GetBoolean(nameof(OwnershipTransferred));
    }

    internal bool OwnershipTransferred { get; }

    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(OwnershipTransferred), OwnershipTransferred);
    }
}
