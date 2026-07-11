using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.Serialization;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlayHistoryPresentationState
{
    internal readonly object SyncRoot = new();
    internal long RequestGeneration;
    internal long SortRevision;
    internal CancellationTokenSource RequestCancellation;
    internal long KeywordRevision;
    internal long DisplayTargetRevision;
    internal PlayHistoryViewState CurrentView;
    internal SortSnapshot CurrentSortSnapshot;
    internal string CurrentKeywordIdentity = string.Empty;
    internal string CurrentDisplayTargetIdentity = PlayHistoryDisplayTargetItem.All.Identity;
    internal IReadOnlyList<PlayHistoryPeriodTreeItem> ArchivePeriodTree = [];
    internal IReadOnlyList<PlayHistorySummaryCard> SummaryCards = [];
    internal string DiagnosticText = string.Empty;

    internal bool IsFresh(PlayHistoryViewState state)
    {
        return state != null
            && state.RequestId > 0
            && state.RequestId == RequestGeneration
            && SortSnapshot.Equals(state.SortSnapshot, CurrentSortSnapshot)
            && state.KeywordFilterRevision == KeywordRevision
            && string.Equals(state.KeywordFilterIdentity, CurrentKeywordIdentity, StringComparison.Ordinal)
            && state.DisplayTargetRevision == DisplayTargetRevision
            && string.Equals(state.DisplayTargetIdentity, CurrentDisplayTargetIdentity, StringComparison.Ordinal);
    }

    internal bool TryCommitTerminal(
        PlayHistoryTerminalRequest request,
        PlayHistoryTerminalCommitResult result,
        Action commitRows,
        Action commitRelatedOwners)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (result == null)
        {
            throw new ArgumentNullException(nameof(result));
        }
        if (commitRows == null)
        {
            throw new ArgumentNullException(nameof(commitRows));
        }
        if (commitRelatedOwners == null)
        {
            throw new ArgumentNullException(nameof(commitRelatedOwners));
        }

        lock (SyncRoot)
        {
            if (!IsFresh(request.ViewState))
            {
                return false;
            }
            commitRows();
            commitRelatedOwners();

            if (request.ArchivePeriodTree != null
                && !ReferenceEquals(ArchivePeriodTree, request.ArchivePeriodTree)
                && !AreSameArchiveTree(ArchivePeriodTree, request.ArchivePeriodTree))
            {
                ArchivePeriodTree = request.ArchivePeriodTree;
                result.ArchivePeriodTreeChanged = true;
            }

            IReadOnlyList<PlayHistorySummaryCard> summaryCards = request.SummaryCards ?? [];
            if (!ReferenceEquals(SummaryCards, summaryCards)
                && !AreSameSummaryCards(SummaryCards, summaryCards))
            {
                SummaryCards = summaryCards;
                result.SummaryCardsChanged = true;
            }

            string diagnosticText = request.DiagnosticText ?? string.Empty;
            if (DiagnosticText != diagnosticText)
            {
                DiagnosticText = diagnosticText;
                result.DiagnosticTextChanged = true;
            }

            CurrentView = request.ViewState;
            result.Applied = true;
            return true;
        }
    }

    internal static bool AreSameArchiveTree(IReadOnlyList<PlayHistoryPeriodTreeItem> left, IReadOnlyList<PlayHistoryPeriodTreeItem> right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            PlayHistoryPeriodTreeItem leftItem = left[index];
            PlayHistoryPeriodTreeItem rightItem = right[index];
            if (ReferenceEquals(leftItem, rightItem))
            {
                continue;
            }
            if (leftItem == null || rightItem == null
                || !string.Equals(leftItem.Label, rightItem.Label, StringComparison.Ordinal)
                || !AreSamePeriodRequest(leftItem.Request, rightItem.Request)
                || !AreSameArchiveTree(leftItem.Children, rightItem.Children))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool AreSameSummaryCards(IReadOnlyList<PlayHistorySummaryCard> left, IReadOnlyList<PlayHistorySummaryCard> right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            PlayHistorySummaryCard leftCard = left[index];
            PlayHistorySummaryCard rightCard = right[index];
            if (leftCard == null || rightCard == null)
            {
                if (leftCard != rightCard)
                {
                    return false;
                }
                continue;
            }
            if (!string.Equals(leftCard.Label, rightCard.Label, StringComparison.Ordinal)
                || !string.Equals(leftCard.Value, rightCard.Value, StringComparison.Ordinal)
                || leftCard.Compact != rightCard.Compact
                || !string.Equals(leftCard.FilterKey, rightCard.FilterKey, StringComparison.Ordinal)
                || !string.Equals(leftCard.FilterText, rightCard.FilterText, StringComparison.Ordinal)
                || leftCard.IsSelected != rightCard.IsSelected)
            {
                return false;
            }
        }
        return true;
    }

    private static bool AreSamePeriodRequest(PlayHistoryPeriodRequest left, PlayHistoryPeriodRequest right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        return left != null
            && right != null
            && left.Kind == right.Kind
            && string.Equals(left.Label, right.Label, StringComparison.Ordinal)
            && left.PlayedAtFromInclusive == right.PlayedAtFromInclusive
            && left.PlayedAtToExclusive == right.PlayedAtToExclusive
            && left.FinalizationFilter == right.FinalizationFilter;
    }
}

internal sealed class PlayHistoryTerminalRequest
{
    internal PlayHistoryViewState ViewState { get; set; }
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

internal sealed class PlayHistoryViewState
{
    internal PlayHistoryViewState(
        long requestId,
        PlayHistoryPeriodRequest periodRequest,
        IReadOnlyList<PlayHistoryRow> allProjectedRows,
        IReadOnlyList<PlayHistoryRow> filterSourceRows,
        IReadOnlyList<PlayHistoryRow> projectedRows,
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics,
        PlayHistoryProvider provider,
        Lr2PlayHistorySchemaStatus schemaStatus,
        int sourceCount,
        SortSnapshot sortSnapshot,
        string keywordFilter,
        long keywordFilterRevision,
        PlayHistoryDisplayTargetItem displayTarget,
        long displayTargetRevision,
        PlayHistoryPeriodSummaryOverride summaryOverride = null)
    {
        RequestId = requestId;
        PeriodRequest = periodRequest ?? PlayHistoryPeriodRequest.All();
        AllProjectedRows = allProjectedRows ?? [];
        FilterSourceRows = filterSourceRows ?? [];
        ProjectedRows = projectedRows ?? [];
        Diagnostics = diagnostics ?? [];
        Provider = provider;
        SchemaStatus = schemaStatus;
        SourceCount = sourceCount;
        SortSnapshot = sortSnapshot;
        KeywordFilter = keywordFilter ?? string.Empty;
        KeywordFilterIdentity = PlaylistRequestFactory.NormalizeKeywordFilter(KeywordFilter);
        KeywordFilterRevision = keywordFilterRevision;
        DisplayTarget = displayTarget ?? PlayHistoryDisplayTargetItem.All;
        DisplayTargetIdentity = DisplayTarget.Identity;
        DisplayTargetRevision = displayTargetRevision;
        SummaryOverride = summaryOverride;
    }

    internal long RequestId { get; }
    internal PlayHistoryPeriodRequest PeriodRequest { get; }
    internal IReadOnlyList<PlayHistoryRow> AllProjectedRows { get; }
    internal IReadOnlyList<PlayHistoryRow> FilterSourceRows { get; }
    internal IReadOnlyList<PlayHistoryRow> ProjectedRows { get; }
    internal IReadOnlyList<PlayHistoryDiagnostic> Diagnostics { get; }
    internal PlayHistoryProvider Provider { get; }
    internal Lr2PlayHistorySchemaStatus SchemaStatus { get; }
    internal int SourceCount { get; }
    internal SortSnapshot SortSnapshot { get; }
    internal string KeywordFilter { get; }
    internal string KeywordFilterIdentity { get; }
    internal long KeywordFilterRevision { get; }
    internal PlayHistoryDisplayTargetItem DisplayTarget { get; }
    internal string DisplayTargetIdentity { get; }
    internal long DisplayTargetRevision { get; }
    internal PlayHistoryPeriodSummaryOverride SummaryOverride { get; }
}

internal readonly struct SortSnapshot
{
    internal SortSnapshot(string columnName, ListSortDirection? direction, long revision)
    {
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        Revision = revision;
    }

    internal string ColumnName { get; }
    internal ListSortDirection? Direction { get; }
    internal long Revision { get; }

    internal static bool Equals(SortSnapshot left, SortSnapshot right)
    {
        return string.Equals(left.ColumnName, right.ColumnName, StringComparison.Ordinal)
            && left.Direction == right.Direction
            && left.Revision == right.Revision;
    }

    public override string ToString()
    {
        return (string.IsNullOrWhiteSpace(ColumnName) ? "(default)" : ColumnName)
            + ":" + (Direction?.ToString() ?? "(default)")
            + "#" + Revision.ToString(CultureInfo.InvariantCulture);
    }
}
