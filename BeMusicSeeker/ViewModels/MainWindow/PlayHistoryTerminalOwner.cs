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
    private readonly PlaylistDetailTerminalOwner playlistDetailTerminalOwner;
    private readonly Action<string> publishPropertyChanged;
    private readonly Action<PlaylistSourceClearCommitResult> logPlaylistSourceClear;

    internal PlayHistoryTerminalOwner(
        PlayHistoryPresentationState state,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        RegularChartListOwner regularChartListOwner,
        PlaylistDetailTerminalOwner playlistDetailTerminalOwner,
        Action<string> publishPropertyChanged,
        Action<PlaylistSourceClearCommitResult> logPlaylistSourceClear)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.mainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        this.regularChartListOwner = regularChartListOwner ?? throw new ArgumentNullException(nameof(regularChartListOwner));
        this.playlistDetailTerminalOwner = playlistDetailTerminalOwner ?? throw new ArgumentNullException(nameof(playlistDetailTerminalOwner));
        this.publishPropertyChanged = publishPropertyChanged ?? throw new ArgumentNullException(nameof(publishPropertyChanged));
        this.logPlaylistSourceClear = logPlaylistSourceClear ?? throw new ArgumentNullException(nameof(logPlaylistSourceClear));
    }

    internal PlayHistoryTerminalCommitResult TryApply(PlayHistoryTerminalRequest request)
    {
        if (request?.ViewState == null || request.Rows == null || request.MainRowsRequest == null)
        {
            throw new ArgumentException("A complete play-history terminal request is required.", nameof(request));
        }

        MainChartListPreparedRowsApply prepared = mainChartList.PrepareRowsApply(request.MainRowsRequest);
        MainChartListRowsCommit mainRowsCommit = null;
        var result = new PlayHistoryTerminalCommitResult();
        bool stale = false;
        Exception commitException = null;
        try
        {
            lock (state.SyncRoot)
            {
                if (!state.IsFresh(request.ViewState))
                {
                    stale = true;
                }
                else
                {
                    mainRowsCommit = mainChartList.CommitPreparedRowsWithoutDisposal(prepared);
                    result.PlaylistSourceClear = playlistDetailTerminalOwner.CommitSourceClearWithoutCallbacks();
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
                }
            }
        }
        catch (Exception ex)
        {
            if (mainRowsCommit == null)
            {
                mainChartList.CancelPreparedRowsApply(prepared);
                throw;
            }
            commitException = ex;
        }

        if (stale)
        {
            mainChartList.CancelPreparedRowsApply(prepared);
            return result;
        }

        List<Exception> publishExceptions = [];
        if (commitException != null)
        {
            publishExceptions.Add(commitException);
        }
        TryPublish(() => mainChartList.DisposeCommittedRows(mainRowsCommit), publishExceptions);
        TryPublish(() => result.MainRowsApply = mainChartList.PublishRowsCommit(mainRowsCommit), publishExceptions);
        if (result.ColumnPresentation != null)
        {
            TryPublish(() => playlistWorkspace.PublishColumnPresentation(result.ColumnPresentation), publishExceptions);
        }
        if (result.BindingMode != null)
        {
            TryPublish(() => playlistWorkspace.PublishBindingMode(result.BindingMode), publishExceptions);
        }
        if (result.ArchivePeriodTreeChanged)
        {
            TryPublish(() => publishPropertyChanged("PlayHistoryArchivePeriodTree"), publishExceptions);
        }
        if (result.SummaryCardsChanged)
        {
            TryPublish(() => publishPropertyChanged("PlayHistorySummaryCards"), publishExceptions);
        }
        if (result.DiagnosticTextChanged)
        {
            TryPublish(() => publishPropertyChanged("PlayHistorySummaryDiagnosticText"), publishExceptions);
        }
        if (result.PlaylistSourceClear != null)
        {
            TryPublish(
                () =>
                {
                    playlistDetailTerminalOwner.PublishSourceClear(result.PlaylistSourceClear);
                    logPlaylistSourceClear(result.PlaylistSourceClear);
                },
                publishExceptions);
        }
        if (publishExceptions.Count > 0)
        {
            throw new PlayHistoryTerminalPublishException(new AggregateException(publishExceptions), ownershipTransferred: true);
        }
        return result;
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
