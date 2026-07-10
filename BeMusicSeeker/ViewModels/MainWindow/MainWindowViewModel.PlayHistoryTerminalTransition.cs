using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel
{
    internal bool TryCommitPlayHistoryRowsForTest(long requestId, IList rows, string summaryText)
    {
        CaptureSortParameters(out SortSnapshot sortSnapshot);
        var state = new PlayHistoryViewState(
            requestId,
            PlayHistoryPeriodRequest.All(),
            [],
            [],
            [],
            [],
            PlayHistoryProvider.Lr2,
            default,
            sourceCount: 0,
            sortSnapshot,
            KeywordFilter,
            Interlocked.Read(ref playHistoryKeywordFilterRevision),
            SelectedPlayHistoryDisplayTarget,
            Interlocked.Read(ref playHistoryDisplayTargetRevision));
        MainChartListColumnSelection columnSelection = ResolveMainColumnSettingForViewUpdate(MainViewUpdateMode.PlayHistorySelected);
        var stopwatch = Stopwatch.StartNew();
        bool ownsCandidateRows = !ReferenceEquals(MainChartList.Rows, rows);
        PlayHistoryTerminalCommitResult result = PlayHistoryTerminalTransition.TryCommit(
            this,
            new PlayHistoryTerminalRequest
            {
                State = state,
                Rows = rows,
                ColumnSelection = columnSelection,
                SummaryCards = [],
                DiagnosticText = string.Empty,
                MainRowsRequest = new MainChartListRowsApplyRequest
                {
                    Rows = rows,
                    ColumnsSettings = columnSelection.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Reset,
                    Summary = MainChartListSummaryUpdate.Explicit(summaryText),
                    ColumnSettingReuse = columnSelection.Reused,
                    ColumnPreparationMs = columnSelection.ElapsedMs,
                    TerminalStageStartMs = stopwatch.ElapsedMilliseconds,
                    Stopwatch = stopwatch
                }
            });
        if (!result.Applied && ownsCandidateRows)
        {
            MainChartListViewModel.DisposeRows(rows);
        }
        return result.Applied;
    }

    private sealed class PlayHistoryTerminalRequest
    {
        internal PlayHistoryViewState State { get; set; }

        internal IList Rows { get; set; }

        internal MainChartListRowsApplyRequest MainRowsRequest { get; set; }

        internal MainChartListColumnSelection ColumnSelection { get; set; }

        internal IReadOnlyList<PlayHistoryPeriodTreeItem> ArchivePeriodTree { get; set; }

        internal IReadOnlyList<PlayHistorySummaryCard> SummaryCards { get; set; }

        internal string DiagnosticText { get; set; }
    }

    private sealed class PlayHistoryTerminalCommitResult
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

    private static class PlayHistoryTerminalTransition
    {
        internal static PlayHistoryTerminalCommitResult TryCommit(
            MainWindowViewModel owner,
            PlayHistoryTerminalRequest request)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            if (request?.State == null || request.Rows == null || request.MainRowsRequest == null)
            {
                throw new ArgumentException("A complete play-history terminal request is required.", nameof(request));
            }

            MainChartListPreparedRowsApply prepared = owner.MainChartList.PrepareRowsApply(request.MainRowsRequest);
            MainChartListRowsCommit mainRowsCommit = null;
            var result = new PlayHistoryTerminalCommitResult();
            bool stale = false;
            Exception commitException = null;
            try
            {
                lock (owner.playHistoryViewRequestLock)
                {
                    if (!IsFresh(owner, request.State))
                    {
                        stale = true;
                    }
                    else
                    {
                        mainRowsCommit = owner.MainChartList.CommitPreparedRowsWithoutDisposal(prepared);
                        result.PlaylistSourceClear = owner.CommitPlaylistSourceClearWithoutCallbacks();
                        result.BindingMode = owner.PlaylistWorkspace.CommitBindingModeWithoutNotification(playlistDetailActive: false);
                        result.ColumnPresentation = owner.PlaylistWorkspace.CommitColumnPresentationWithoutNotification(
                            request.ColumnSelection.PlaylistColumnSettingsVisibility,
                            request.ColumnSelection.PlaylistSummaryColumnsSettings);
                        if (request.ColumnSelection.AppliedMode.HasValue)
                        {
                            owner.regularChartListOwner.CommitExternalColumnMode(request.ColumnSelection.AppliedMode);
                        }

                        owner.regularChartListOwner.ResetDerivedCaches();

                        if (request.ArchivePeriodTree != null
                            && !ReferenceEquals(owner._PlayHistoryArchivePeriodTree, request.ArchivePeriodTree)
                            && !AreSamePlayHistoryArchivePeriodTree(owner._PlayHistoryArchivePeriodTree, request.ArchivePeriodTree))
                        {
                            owner._PlayHistoryArchivePeriodTree = request.ArchivePeriodTree;
                            result.ArchivePeriodTreeChanged = true;
                        }

                        IReadOnlyList<PlayHistorySummaryCard> summaryCards = request.SummaryCards ?? [];
                        if (!ReferenceEquals(owner._PlayHistorySummaryCards, summaryCards)
                            && !AreSamePlayHistorySummaryCards(owner._PlayHistorySummaryCards, summaryCards))
                        {
                            owner._PlayHistorySummaryCards = summaryCards;
                            result.SummaryCardsChanged = true;
                        }

                        string diagnosticText = request.DiagnosticText ?? string.Empty;
                        if (owner._PlayHistorySummaryDiagnosticText != diagnosticText)
                        {
                            owner._PlayHistorySummaryDiagnosticText = diagnosticText;
                            result.DiagnosticTextChanged = true;
                        }

                        Volatile.Write(ref owner.playHistoryViewState, request.State);
                        result.Applied = true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (mainRowsCommit == null)
                {
                    owner.MainChartList.CancelPreparedRowsApply(prepared);
                    throw;
                }
                commitException = ex;
            }

            if (stale)
            {
                owner.MainChartList.CancelPreparedRowsApply(prepared);
                return result;
            }

            var publishExceptions = new List<Exception>();
            if (commitException != null)
            {
                publishExceptions.Add(commitException);
            }
            TryPublish(() => owner.MainChartList.DisposeCommittedRows(mainRowsCommit), publishExceptions);
            TryPublish(() => result.MainRowsApply = owner.MainChartList.PublishRowsCommit(mainRowsCommit), publishExceptions);
            if (result.ColumnPresentation != null)
            {
                TryPublish(() => owner.PlaylistWorkspace.PublishColumnPresentation(result.ColumnPresentation), publishExceptions);
            }
            if (result.BindingMode != null)
            {
                TryPublish(() => owner.PlaylistWorkspace.PublishBindingMode(result.BindingMode), publishExceptions);
            }
            if (result.ArchivePeriodTreeChanged)
            {
                TryPublish(() => owner.RaisePropertyChanged(nameof(PlayHistoryArchivePeriodTree)), publishExceptions);
            }
            if (result.SummaryCardsChanged)
            {
                TryPublish(() => owner.RaisePropertyChanged(nameof(PlayHistorySummaryCards)), publishExceptions);
            }
            if (result.DiagnosticTextChanged)
            {
                TryPublish(() => owner.RaisePropertyChanged(nameof(PlayHistorySummaryDiagnosticText)), publishExceptions);
            }
            if (result.PlaylistSourceClear != null)
            {
                TryPublish(() => owner.PublishPlaylistSourceClear(result.PlaylistSourceClear), publishExceptions);
            }
            if (publishExceptions.Count > 0)
            {
                throw new PlayHistoryTerminalPublishException(new AggregateException(publishExceptions));
            }
            return result;
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

        private static bool IsFresh(MainWindowViewModel owner, PlayHistoryViewState state)
        {
            if (!owner.IsCurrentPlayHistoryViewRequestUnsafe(state.RequestId)
                || !owner.IsCurrentSortSnapshot(state.SortSnapshot))
            {
                return false;
            }

            long keywordRevision = Interlocked.Read(ref owner.playHistoryKeywordFilterRevision);
            if (keywordRevision != state.KeywordFilterRevision
                || !string.Equals(NormalizePlaylistKeywordFilter(owner.KeywordFilter), state.KeywordFilterIdentity, StringComparison.Ordinal))
            {
                return false;
            }

            long displayTargetRevision = Interlocked.Read(ref owner.playHistoryDisplayTargetRevision);
            return displayTargetRevision == state.DisplayTargetRevision
                && string.Equals(owner.SelectedPlayHistoryDisplayTarget?.Identity ?? string.Empty, state.DisplayTargetIdentity, StringComparison.Ordinal);
        }
    }
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

    private PlayHistoryTerminalPublishException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}
