using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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

    internal bool SetArchivePeriodTree(IReadOnlyList<PlayHistoryPeriodTreeItem> value)
    {
        IReadOnlyList<PlayHistoryPeriodTreeItem> next = value ?? [];
        if (ReferenceEquals(ArchivePeriodTree, next) || AreSameArchiveTree(ArchivePeriodTree, next))
        {
            return false;
        }
        ArchivePeriodTree = next;
        return true;
    }

    internal bool SetSummaryCards(IReadOnlyList<PlayHistorySummaryCard> value)
    {
        IReadOnlyList<PlayHistorySummaryCard> next = value ?? [];
        if (ReferenceEquals(SummaryCards, next) || AreSameSummaryCards(SummaryCards, next))
        {
            return false;
        }
        SummaryCards = next;
        return true;
    }

    internal bool SetDiagnosticText(string value)
    {
        string next = value ?? string.Empty;
        if (DiagnosticText == next)
        {
            return false;
        }
        DiagnosticText = next;
        return true;
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
                && SetArchivePeriodTree(request.ArchivePeriodTree))
            {
                result.ArchivePeriodTreeChanged = true;
            }

            if (SetSummaryCards(request.SummaryCards))
            {
                result.SummaryCardsChanged = true;
            }

            if (SetDiagnosticText(request.DiagnosticText))
            {
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

    internal static string FormatGridSummaryText(
        PlayHistoryPeriodRequest request,
        PlayHistoryPeriodSummary summary,
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics)
    {
        return FormatGridSummaryText(request, summary, diagnostics, FormatDiagnosticSummary(diagnostics));
    }

    internal static string FormatGridSummaryText(
        PlayHistoryPeriodRequest request,
        PlayHistoryPeriodSummary summary,
        IReadOnlyList<PlayHistoryDiagnostic> diagnostics,
        string diagnosticSummary)
    {
        summary ??= PlayHistoryPeriodSummary.FromRows(request?.Label ?? string.Empty, []);
        int diagnosticsCount = diagnostics?.Count ?? 0;
        string baseText = string.Format(
            BeMusicSeeker.Properties.Resources.Play_history_summary_format,
            request?.Label ?? string.Empty,
            summary.RowCount,
            summary.ScoreUpdateCount,
            summary.ClearUpdateCount,
            summary.NewFullComboCount,
            FormatDuration(summary),
            diagnosticsCount);
        return string.IsNullOrWhiteSpace(diagnosticSummary)
            ? baseText
            : baseText + " / " + diagnosticSummary;
    }

    internal static IReadOnlyList<PlayHistorySummaryCard> CreateSummaryCards(
        PlayHistoryPeriodSummary summary,
        PlayHistoryProvider provider,
        HashSet<string> selectedFilterKeys = null)
    {
        summary ??= PlayHistoryPeriodSummary.FromRows(string.Empty, []);
        selectedFilterKeys ??= [];
        List<PlayHistorySummaryCard> cards =
        [
            CreateSummaryCard(BeMusicSeeker.Properties.Resources.Play_history_summary_judge_count, FormatCount(summary.JudgeCount, summary.JudgeCountAvailable), selectedFilterKeys),
            CreateSummaryCard(BeMusicSeeker.Properties.Resources.Play_history_summary_play_count, FormatCount(summary.FinalizedCount, summary.PlayCountAvailable), selectedFilterKeys),
            CreateSummaryCard(BeMusicSeeker.Properties.Resources.Play_history_summary_playtime, FormatDuration(summary), selectedFilterKeys),
            CreateSummaryCard(BeMusicSeeker.Properties.Resources.Play_history_summary_score_update, summary.ScoreUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, filterKey: "score"),
            CreateSummaryCard(BeMusicSeeker.Properties.Resources.Play_history_summary_bp_update, summary.BpUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, filterKey: "bp"),
            CreateSummaryCard(BeMusicSeeker.Properties.Resources.Play_history_summary_combo_update, summary.ComboUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, filterKey: "combo"),
            CreateSummaryCard(BeMusicSeeker.Properties.Resources.Play_history_summary_clear_update, summary.ClearUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, filterKey: "clear"),
            CreateSummaryCard("ASSIST", summary.AssistClearUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, compact: true, filterKey: "assist"),
            CreateSummaryCard("EASY", summary.EasyClearUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, compact: true, filterKey: "easy"),
            CreateSummaryCard("NORMAL", summary.NormalClearUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, compact: true, filterKey: "normal"),
            CreateSummaryCard("HARD", summary.HardClearUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, compact: true, filterKey: "hard")
        ];
        if (provider == PlayHistoryProvider.Beatoraja)
        {
            cards.Add(CreateSummaryCard("EXH", summary.ExHardClearUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, compact: true, filterKey: "exhard"));
        }
        cards.Add(CreateSummaryCard("FC", summary.FullComboClearUpdateCount.ToString("N0", CultureInfo.CurrentCulture), selectedFilterKeys, compact: true, filterKey: "fc"));
        return cards;
    }

    internal static IReadOnlyList<string> GetSummaryFilterTexts(HashSet<string> selectedKeys)
    {
        if (selectedKeys == null || selectedKeys.Count == 0)
        {
            return [];
        }
        return [.. CreateSummaryFilterDefinitions()
            .Where(definition => selectedKeys.Contains(definition.Key))
            .Select(definition => definition.FilterText)];
    }

    internal static string FormatDiagnosticSummary(IReadOnlyList<PlayHistoryDiagnostic> diagnostics)
    {
        PlayHistoryDiagnostic diagnostic = (diagnostics ?? [])
            .OrderByDescending(item => item?.Severity == PlayHistoryDiagnosticSeverity.Error ? 2 : (item?.Severity == PlayHistoryDiagnosticSeverity.Warning ? 1 : 0))
            .FirstOrDefault();
        if (diagnostic == null)
        {
            return string.Empty;
        }
        string detail = (diagnostic.Severity.ToString() + " " + diagnostic.Code).Trim();
        if (!string.IsNullOrWhiteSpace(diagnostic.Message))
        {
            detail += ": " + diagnostic.Message.Trim();
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.SourcePath))
        {
            detail += " (" + diagnostic.SourcePath.Trim() + ")";
        }
        return detail;
    }

    private static PlayHistorySummaryCard CreateSummaryCard(
        string label,
        string value,
        HashSet<string> selectedFilterKeys,
        bool compact = false,
        string filterKey = null)
    {
        PlayHistorySummaryFilterDefinition definition = string.IsNullOrWhiteSpace(filterKey)
            ? default
            : GetSummaryFilterDefinition(filterKey);
        return new PlayHistorySummaryCard(
            label,
            value,
            compact,
            definition.Key,
            definition.FilterText,
            !string.IsNullOrWhiteSpace(definition.Key) && (selectedFilterKeys?.Contains(definition.Key) == true));
    }

    private static PlayHistorySummaryFilterDefinition GetSummaryFilterDefinition(string key)
    {
        return CreateSummaryFilterDefinitions()
            .FirstOrDefault(definition => string.Equals(definition.Key, key, StringComparison.Ordinal));
    }

    private static IReadOnlyList<PlayHistorySummaryFilterDefinition> CreateSummaryFilterDefinitions()
    {
        return
        [
            new PlayHistorySummaryFilterDefinition("score", "type:score"),
            new PlayHistorySummaryFilterDefinition("bp", "type:bp"),
            new PlayHistorySummaryFilterDefinition("combo", "type:combo"),
            new PlayHistorySummaryFilterDefinition("clear", "type:clear"),
            new PlayHistorySummaryFilterDefinition("assist", "type:clear newclear:AE|LAE"),
            new PlayHistorySummaryFilterDefinition("easy", "type:clear newclear:EC"),
            new PlayHistorySummaryFilterDefinition("normal", "type:clear newclear:NC"),
            new PlayHistorySummaryFilterDefinition("hard", "type:clear newclear:HC"),
            new PlayHistorySummaryFilterDefinition("exhard", "type:clear newclear:EXH"),
            new PlayHistorySummaryFilterDefinition("fc", "type:clear newclear:FC|PF")
        ];
    }

    private static string FormatDuration(PlayHistoryPeriodSummary summary)
    {
        return summary?.PlaytimeAvailable == false
            ? "-"
            : FormatDuration(summary?.PlaytimeSeconds ?? 0);
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "0:00";
        }
        long hours = seconds / 3600;
        long minutes = (seconds / 60) % 60;
        long remainderSeconds = seconds % 60;
        return hours >= 1L
            ? hours.ToString(CultureInfo.InvariantCulture) + ":" + minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + remainderSeconds.ToString("00", CultureInfo.InvariantCulture)
            : minutes.ToString(CultureInfo.InvariantCulture) + ":" + remainderSeconds.ToString("00", CultureInfo.InvariantCulture);
    }

    private static string FormatCount(long count, bool available)
    {
        return available
            ? count.ToString("N0", CultureInfo.CurrentCulture)
            : "-";
    }

    private readonly struct PlayHistorySummaryFilterDefinition
    {
        internal PlayHistorySummaryFilterDefinition(string key, string filterText)
        {
            Key = key ?? string.Empty;
            FilterText = filterText ?? string.Empty;
        }

        internal string Key { get; }

        internal string FilterText { get; }
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

internal sealed class PlayHistoryPresentationFreshnessResult
{
    private PlayHistoryPresentationFreshnessResult(PlayHistoryPresentationFreshnessStatus status, bool queueRefresh)
    {
        Status = status;
        QueueRefresh = queueRefresh;
    }

    internal PlayHistoryPresentationFreshnessStatus Status { get; }

    internal bool QueueRefresh { get; }

    internal static PlayHistoryPresentationFreshnessResult Fresh()
    {
        return new PlayHistoryPresentationFreshnessResult(PlayHistoryPresentationFreshnessStatus.Fresh, queueRefresh: false);
    }

    internal static PlayHistoryPresentationFreshnessResult StaleRequest()
    {
        return new PlayHistoryPresentationFreshnessResult(PlayHistoryPresentationFreshnessStatus.StaleRequest, queueRefresh: false);
    }

    internal static PlayHistoryPresentationFreshnessResult SortStale()
    {
        return new PlayHistoryPresentationFreshnessResult(PlayHistoryPresentationFreshnessStatus.SortStale, queueRefresh: false);
    }

    internal static PlayHistoryPresentationFreshnessResult DisplayTargetStale(bool queueRefresh)
    {
        return new PlayHistoryPresentationFreshnessResult(PlayHistoryPresentationFreshnessStatus.DisplayTargetStale, queueRefresh);
    }

    internal static PlayHistoryPresentationFreshnessResult KeywordStale(bool queueRefresh)
    {
        return new PlayHistoryPresentationFreshnessResult(PlayHistoryPresentationFreshnessStatus.KeywordStale, queueRefresh);
    }
}

internal enum PlayHistoryPresentationFreshnessStatus
{
    Fresh,
    StaleRequest,
    SortStale,
    DisplayTargetStale,
    KeywordStale
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
