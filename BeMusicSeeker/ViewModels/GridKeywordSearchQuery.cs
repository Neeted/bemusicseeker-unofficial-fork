using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal enum GridKeywordSearchContext
{
    ChartList,
    PlaylistDetail,
    PlaylistSummary,
    PlayHistory
}

internal enum GridKeywordSearchDiagnosticKind
{
    None,
    UnknownField,
    EmptyFieldTerm,
    EmptyNegation,
    EmptyOr,
    InvalidRegex,
    InvalidDate
}

internal readonly struct GridKeywordSearchDiagnostic
{
    internal GridKeywordSearchDiagnostic(GridKeywordSearchDiagnosticKind kind, string value)
    {
        Kind = kind;
        Value = value ?? string.Empty;
    }

    internal GridKeywordSearchDiagnosticKind Kind { get; }

    internal string Value { get; }
}

internal sealed class GridKeywordSearchQuery
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly string[] BaseChartFields =
    [
        "level", "difficulty", "mainbpm", "maxbpm", "minbpm", "duration", "length", "judge", "judge%", "judgepct",
        "feature", "notes", "long", "ln", "scratch", "total", "tn", "t/n", "density", "peak", "peakdensity",
        "end", "enddensity", "soflan"
    ];

    private static readonly string[] ScoreFields =
    [
        "clear", "rank", "djlevel", "dj", "rate", "score", "combo", "bp"
    ];

    private static readonly string[] ChartListFields = ["title", "artist", "genre", "tag", "path", "playlist", "ref", "table", "md5", "hash", "sha256", .. BaseChartFields, .. ScoreFields];

    private static readonly string[] PlaylistDetailFields = ["title", "artist", "genre", "tag", "path", "playlist", "ref", "table", "md5", "hash", "sha256", "memo", "comment", .. BaseChartFields, .. ScoreFields];

    private static readonly string[] PlaylistSummaryFields = ["id", "output", "name", "folder", "foldername", "prefix", "symbol", "header", "data"];

    private static readonly string[] PlayHistoryFields =
    [
        "title", "artist", "path", "folder", "playlist", "ref", "table", "md5", "hash", "sha256",
        "date", "year", "month", "type", "kind", "clear", "oldclear", "newclear", "finalized", "source"
    ];

    private readonly SearchCondition[] conditions;

    private GridKeywordSearchQuery(SearchCondition[] conditions)
    {
        this.conditions = conditions ?? [];
    }

    internal bool HasTokens => conditions.Length > 0;

    /// <summary>
    /// 指定 context で利用できる field query 名を返します。
    /// 診断・ヘルプ・補完が同じ field 定義を使うための共有入口です。
    /// </summary>
    /// <param name="context">検索対象の context。</param>
    /// <returns>利用可能な field 名一覧。</returns>
    internal static IReadOnlyList<string> GetKnownFields(GridKeywordSearchContext context)
    {
        return context switch
        {
            GridKeywordSearchContext.PlaylistDetail => PlaylistDetailFields,
            GridKeywordSearchContext.PlaylistSummary => PlaylistSummaryFields,
            GridKeywordSearchContext.PlayHistory => PlayHistoryFields,
            _ => ChartListFields,
        };
    }

    internal IReadOnlyList<GridKeywordSearchDiagnostic> GetDiagnostics(GridKeywordSearchContext context)
    {
        if (!HasTokens)
        {
            return [];
        }
        List<GridKeywordSearchDiagnostic> diagnostics = [];
        foreach (SearchCondition condition in conditions)
        {
            if (!string.IsNullOrEmpty(condition.Field) && !IsKnownField(context, condition.Field))
            {
                diagnostics.Add(new GridKeywordSearchDiagnostic(GridKeywordSearchDiagnosticKind.UnknownField, condition.Field));
            }
            if (condition.DiagnosticKind != GridKeywordSearchDiagnosticKind.None
                && (condition.DiagnosticKind != GridKeywordSearchDiagnosticKind.InvalidDate
                    || context == GridKeywordSearchContext.PlayHistory))
            {
                diagnostics.Add(new GridKeywordSearchDiagnostic(condition.DiagnosticKind, condition.DiagnosticValue));
            }
            foreach (SearchAlternative alternative in condition.Alternatives)
            {
                if (alternative.IsInvalid && condition.IsRegex)
                {
                    diagnostics.Add(new GridKeywordSearchDiagnostic(GridKeywordSearchDiagnosticKind.InvalidRegex, alternative.Term));
                }
                else if (alternative.IsInvalid
                    && !condition.IsRegex
                    && string.Equals(condition.Field, "date", StringComparison.Ordinal)
                    && context == GridKeywordSearchContext.PlayHistory)
                {
                    diagnostics.Add(new GridKeywordSearchDiagnostic(GridKeywordSearchDiagnosticKind.InvalidDate, alternative.Term));
                }
            }
        }
        return diagnostics;
    }

    internal static GridKeywordSearchQuery Parse(string keywordFilter)
    {
        if (string.IsNullOrWhiteSpace(keywordFilter))
        {
            return new GridKeywordSearchQuery([]);
        }
        SearchCondition[] parsedConditions = [.. Tokenize(keywordFilter)
            .Select(ParseCondition)
            .OrderBy(condition => condition.SortWeight)];
        return new GridKeywordSearchQuery(parsedConditions);
    }

    internal bool MatchesLibraryChartRow(LibraryChartRow row)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (row == null)
        {
            return false;
        }
        foreach (SearchCondition condition in conditions)
        {
            if (!MatchesCondition(condition, row))
            {
                return false;
            }
        }
        return true;
    }

    internal bool MatchesChartListSourceRow(ChartListSourceRow row)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (row == null)
        {
            return false;
        }
        foreach (SearchCondition condition in conditions)
        {
            if (!MatchesCondition(condition, row))
            {
                return false;
            }
        }
        return true;
    }

    internal bool MatchesPlaylistDetail(PlaylistDetailSourceRow row)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (row == null)
        {
            return false;
        }
        foreach (SearchCondition condition in conditions)
        {
            if (!MatchesCondition(condition, row))
            {
                return false;
            }
        }
        return true;
    }

    internal bool MatchesPlaylistSummary(PlaylistSummaryRow row)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (row == null)
        {
            return false;
        }
        foreach (SearchCondition condition in conditions)
        {
            if (!MatchesCondition(condition, row))
            {
                return false;
            }
        }
        return true;
    }

    internal bool MatchesPlayHistoryRow(PlayHistoryRow row)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (row == null)
        {
            return false;
        }
        foreach (SearchCondition condition in conditions)
        {
            if (!MatchesCondition(condition, row))
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<string> Tokenize(string keywordFilter)
    {
        var builder = new StringBuilder();
        bool inQuote = false;
        bool escaping = false;
        foreach (char c in keywordFilter ?? string.Empty)
        {
            if (inQuote)
            {
                if (escaping)
                {
                    builder.Append('\\');
                    builder.Append(c);
                    escaping = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaping = true;
                    continue;
                }
                builder.Append(c);
                if (c == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }
                continue;
            }
            builder.Append(c);
            if (c == '"')
            {
                inQuote = true;
            }
        }
        if (escaping)
        {
            builder.Append('\\');
        }
        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static SearchCondition ParseCondition(string rawToken)
    {
        string token = rawToken ?? string.Empty;
        if (token == "-")
        {
            return SearchCondition.Invalid(isNegated: true, field: null, GridKeywordSearchDiagnosticKind.EmptyNegation, "-");
        }
        bool isNegated = token.Length > 1 && token[0] == '-';
        if (isNegated)
        {
            token = token.Substring(1);
        }
        if (string.IsNullOrEmpty(token))
        {
            return SearchCondition.Invalid(isNegated, field: null, GridKeywordSearchDiagnosticKind.EmptyNegation, "-");
        }
        if (IsWindowsDriveToken(token))
        {
            return CreateCondition(isNegated, field: null, isRegex: false, rawTerms: token);
        }
        int colonIndex = FindUnquotedChar(token, ':');
        if (colonIndex <= 0)
        {
            return CreateCondition(isNegated, field: null, isRegex: false, rawTerms: token);
        }
        string field = token.Substring(0, colonIndex).Trim().ToLowerInvariant();
        string rawTerms = token.Substring(colonIndex + 1);
        if (string.Equals(field, "re", StringComparison.OrdinalIgnoreCase))
        {
            return CreateCondition(isNegated, field: null, isRegex: true, rawTerms);
        }
        bool isRegex = false;
        if (rawTerms.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
        {
            isRegex = true;
            rawTerms = rawTerms.Substring(3);
        }
        return CreateCondition(isNegated, field, isRegex, rawTerms);
    }

    private static bool IsWindowsDriveToken(string token)
    {
        return token != null
            && token.Length >= 2
            && token[1] == ':'
            && ((token[0] >= 'A' && token[0] <= 'Z') || (token[0] >= 'a' && token[0] <= 'z'));
    }

    private static SearchCondition CreateCondition(bool isNegated, string field, bool isRegex, string rawTerms)
    {
        if (string.IsNullOrEmpty(rawTerms))
        {
            GridKeywordSearchDiagnosticKind diagnosticKind = field == null
                ? GridKeywordSearchDiagnosticKind.EmptyOr
                : string.Equals(field, "date", StringComparison.Ordinal) && !isRegex
                    ? GridKeywordSearchDiagnosticKind.InvalidDate
                    : GridKeywordSearchDiagnosticKind.EmptyFieldTerm;
            return SearchCondition.Invalid(isNegated, field, diagnosticKind, field ?? string.Empty);
        }
        List<SearchAlternative> alternatives = [.. SplitUnquoted(rawTerms, '|')
            .Select(rawAlternative => CreateAlternative(rawAlternative, isRegex, field))
            .Where(alternative => !alternative.IsEmpty)];
        if (alternatives.Count == 0)
        {
            GridKeywordSearchDiagnosticKind diagnosticKind = string.Equals(field, "date", StringComparison.Ordinal) && !isRegex
                ? GridKeywordSearchDiagnosticKind.InvalidDate
                : GridKeywordSearchDiagnosticKind.EmptyOr;
            return SearchCondition.Invalid(isNegated, field, diagnosticKind, rawTerms);
        }
        if (alternatives.All(alternative => alternative.IsInvalid))
        {
            return SearchCondition.Invalid(isNegated, field, GridKeywordSearchDiagnosticKind.None, string.Empty, isRegex, [.. alternatives]);
        }
        return new SearchCondition(isNegated, field, isRegex, [.. alternatives], isInvalid: false, diagnosticKind: GridKeywordSearchDiagnosticKind.None, diagnosticValue: string.Empty);
    }

    private static SearchAlternative CreateAlternative(string rawAlternative, bool isRegex, string field = null)
    {
        string term = NormalizeTerm(rawAlternative);
        if (string.IsNullOrEmpty(term))
        {
            return SearchAlternative.Empty;
        }
        if (!isRegex)
        {
            bool isInvalidDate = string.Equals(field, "date", StringComparison.Ordinal)
                && (HasUnclosedQuote(rawAlternative)
                    || !PlayHistoryDateSearchTerm.TryParse(term, out _));
            return new SearchAlternative(term, regex: null, isInvalid: isInvalidDate, isEmpty: false);
        }
        try
        {
            var regex = new Regex(term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return new SearchAlternative(term, regex, isInvalid: false, isEmpty: false);
        }
        catch (ArgumentException)
        {
            return new SearchAlternative(term, regex: null, isInvalid: true, isEmpty: false);
        }
    }

    private static bool HasUnclosedQuote(string rawTerm)
    {
        string term = rawTerm?.Trim() ?? string.Empty;
        if (term.Length == 0 || term[0] != '"')
        {
            return false;
        }

        bool escaping = false;
        for (int i = 1; i < term.Length; i++)
        {
            char c = term[i];
            if (escaping)
            {
                escaping = false;
                continue;
            }
            if (c == '\\')
            {
                escaping = true;
                continue;
            }
            if (c == '"')
            {
                return i != term.Length - 1;
            }
        }
        return true;
    }

    private static string NormalizeTerm(string rawTerm)
    {
        string term = rawTerm ?? string.Empty;
        if (term.Length == 0)
        {
            return string.Empty;
        }
        term = term.Trim();
        if (term.Length == 0)
        {
            return string.Empty;
        }
        if (term[0] != '"')
        {
            return term;
        }
        int start = 1;
        int end = term.Length > 1 && term[term.Length - 1] == '"' ? term.Length - 1 : term.Length;
        var builder = new StringBuilder(end - start);
        bool escaping = false;
        for (int i = start; i < end; i++)
        {
            char c = term[i];
            if (escaping)
            {
                if (c == '"' || c == '\\')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('\\');
                    builder.Append(c);
                }
                escaping = false;
                continue;
            }
            if (c == '\\')
            {
                escaping = true;
                continue;
            }
            builder.Append(c);
        }
        if (escaping)
        {
            builder.Append('\\');
        }
        return builder.ToString();
    }

    private static IEnumerable<string> SplitUnquoted(string value, char separator)
    {
        var builder = new StringBuilder();
        bool inQuote = false;
        bool escaping = false;
        foreach (char c in value ?? string.Empty)
        {
            if (inQuote)
            {
                if (escaping)
                {
                    builder.Append('\\');
                    builder.Append(c);
                    escaping = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaping = true;
                    continue;
                }
                builder.Append(c);
                if (c == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (c == separator)
            {
                yield return builder.ToString();
                builder.Clear();
                continue;
            }
            builder.Append(c);
            if (c == '"')
            {
                inQuote = true;
            }
        }
        if (escaping)
        {
            builder.Append('\\');
        }
        yield return builder.ToString();
    }

    private static int FindUnquotedChar(string value, char target)
    {
        bool inQuote = false;
        bool escaping = false;
        for (int i = 0; i < (value?.Length ?? 0); i++)
        {
            char c = value[i];
            if (inQuote)
            {
                if (escaping)
                {
                    escaping = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaping = true;
                    continue;
                }
                if (c == '"')
                {
                    inQuote = false;
                }
                continue;
            }
            if (c == '"')
            {
                inQuote = true;
                continue;
            }
            if (c == target)
            {
                return i;
            }
        }
        return -1;
    }

    private static bool MatchesCondition(SearchCondition condition, LibraryChartRow row)
    {
        if (condition.IsInvalid || !IsKnownChartListField(condition.Field))
        {
            return false;
        }
        bool matched = IsChartInfoField(condition.Field) && !condition.IsRegex
            ? condition.Alternatives.Any(alternative => MatchesChartInfoAlternative(alternative, row.ChartInfo, condition.Field))
            : IsScoreField(condition.Field) && !condition.IsRegex
                ? condition.Alternatives.Any(alternative => MatchesScoreAlternative(alternative, condition.Field, row.clear, row.rank, row.rateDouble, row.score, row.maxcombo, row.minbp))
                : condition.Alternatives.Any(alternative => MatchesAlternative(alternative, GetLibraryChartRowValues(row, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesCondition(SearchCondition condition, ChartListSourceRow row)
    {
        if (condition.IsInvalid || !IsKnownChartListField(condition.Field))
        {
            return false;
        }
        bool matched = IsChartInfoField(condition.Field) && !condition.IsRegex
            ? condition.Alternatives.Any(alternative => MatchesChartInfoAlternative(alternative, row.ChartInfo, condition.Field))
            : IsScoreField(condition.Field) && !condition.IsRegex
                ? condition.Alternatives.Any(alternative => MatchesScoreAlternative(alternative, condition.Field, row.Clear, row.Rank, row.RateDouble, row.Score, row.MaxCombo, row.MinBp))
                : condition.Alternatives.Any(alternative => MatchesAlternative(alternative, GetChartListSourceRowValues(row, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesCondition(SearchCondition condition, PlaylistDetailSourceRow row)
    {
        if (condition.IsInvalid || !IsKnownPlaylistDetailField(condition.Field))
        {
            return false;
        }
        bool matched = IsChartInfoField(condition.Field) && !condition.IsRegex
            ? condition.Alternatives.Any(alternative => MatchesChartInfoAlternative(alternative, row.ChartInfo, condition.Field))
            : IsScoreField(condition.Field) && !condition.IsRegex
                ? condition.Alternatives.Any(alternative => MatchesScoreAlternative(alternative, condition.Field, row.clear, row.rank, row.rateDouble, row.score, row.maxcombo, row.minbp))
                : condition.Alternatives.Any(alternative => MatchesAlternative(alternative, GetPlaylistDetailValues(row, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesCondition(SearchCondition condition, PlaylistSummaryRow row)
    {
        if (condition.IsInvalid || !IsKnownPlaylistSummaryField(condition.Field))
        {
            return false;
        }
        bool matched = condition.Alternatives.Any(alternative => MatchesAlternative(alternative, GetPlaylistSummaryValues(row, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesCondition(SearchCondition condition, PlayHistoryRow row)
    {
        if (condition.IsInvalid || !IsKnownPlayHistoryField(condition.Field))
        {
            return false;
        }
        bool matched = IsPlayHistoryClearField(condition.Field) && !condition.IsRegex
            ? condition.Alternatives.Any(alternative => MatchesPlayHistoryClearAlternative(alternative, row, condition.Field))
            : string.Equals(condition.Field, "finalized", StringComparison.Ordinal) && !condition.IsRegex
                ? condition.Alternatives.Any(alternative => MatchesBooleanAlternative(alternative, row.Finalized))
                : IsPlayHistoryDateField(condition.Field) && !condition.IsRegex
                    ? condition.Alternatives.Any(alternative => MatchesPlayHistoryDateAlternative(alternative, row, condition.Field))
                    : condition.Alternatives.Any(alternative => MatchesAlternative(alternative, GetPlayHistoryValues(row, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesAlternative(SearchAlternative alternative, IEnumerable<string> values)
    {
        if (alternative.IsInvalid || alternative.IsEmpty)
        {
            return false;
        }
        foreach (string value in values ?? [])
        {
            if (alternative.Regex != null)
            {
                try
                {
                    if (!string.IsNullOrEmpty(value) && alternative.Regex.IsMatch(value))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            }
            else if (Contains(value, alternative.Term))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsKnownChartListField(string field)
    {
        return field == null || ChartListFields.Contains(field);
    }

    private static bool IsKnownPlaylistDetailField(string field)
    {
        return field == null || PlaylistDetailFields.Contains(field);
    }

    private static bool IsKnownPlaylistSummaryField(string field)
    {
        return field == null || PlaylistSummaryFields.Contains(field);
    }

    private static bool IsKnownPlayHistoryField(string field)
    {
        return field == null || PlayHistoryFields.Contains(field);
    }

    private static bool IsKnownField(GridKeywordSearchContext context, string field)
    {
        return context switch
        {
            GridKeywordSearchContext.PlaylistDetail => IsKnownPlaylistDetailField(field),
            GridKeywordSearchContext.PlaylistSummary => IsKnownPlaylistSummaryField(field),
            GridKeywordSearchContext.PlayHistory => IsKnownPlayHistoryField(field),
            _ => IsKnownChartListField(field),
        };
    }

    private static bool IsChartInfoField(string field)
    {
        return field != null && BaseChartFields.Contains(field);
    }

    private static bool IsScoreField(string field)
    {
        return field != null && ScoreFields.Contains(field);
    }

    private static IEnumerable<string> GetLibraryChartRowValues(LibraryChartRow row, string field)
    {
        switch (field)
        {
            case null:
                yield return row.Title;
                yield return row.genre;
                yield return row.Artist;
                yield return row.tag;
                yield return row.path;
                yield return row.RefTablesSymbols;
                yield return row.hash;
                yield return row.sha256;
                break;
            case "title":
                yield return row.Title;
                break;
            case "artist":
                yield return row.Artist;
                break;
            case "genre":
                yield return row.genre;
                break;
            case "tag":
                yield return row.tag;
                break;
            case "path":
                yield return row.path;
                break;
            case "playlist":
            case "ref":
            case "table":
                foreach (string name in SplitPlaylistReferenceNames(row.RefTablesNames))
                {
                    yield return name;
                }
                break;
            case "md5":
            case "hash":
                yield return row.hash;
                break;
            case "sha256":
                yield return row.sha256;
                break;
            case "clear":
                yield return ScoreDisplayTextFormatter.FormatClear(row.clear);
                break;
            case "rank":
            case "djlevel":
            case "dj":
                yield return ScoreDisplayTextFormatter.FormatRank(row.rank);
                break;
            case "rate":
                yield return FormatNullableDoubleInvariant(row.rateDouble);
                break;
            case "score":
                yield return FormatNullableIntInvariant(row.score);
                break;
            case "combo":
                yield return FormatNullableIntInvariant(row.maxcombo);
                break;
            case "bp":
                yield return FormatNullableIntInvariant(row.minbp);
                break;
            default:
                foreach (string value in GetChartInfoValues(row.ChartInfo, field))
                {
                    yield return value;
                }
                break;
        }
    }

    private static IEnumerable<string> GetChartListSourceRowValues(ChartListSourceRow row, string field)
    {
        switch (field)
        {
            case null:
                yield return row.Title;
                yield return row.Genre;
                yield return row.Artist;
                yield return row.Tag;
                yield return row.Path;
                yield return row.RefTablesSymbols;
                yield return row.Hash;
                yield return row.Sha256;
                break;
            case "title":
                yield return row.Title;
                break;
            case "artist":
                yield return row.Artist;
                break;
            case "genre":
                yield return row.Genre;
                break;
            case "tag":
                yield return row.Tag;
                break;
            case "path":
                yield return row.Path;
                break;
            case "playlist":
            case "ref":
            case "table":
                foreach (string name in SplitPlaylistReferenceNames(row.RefTablesNames))
                {
                    yield return name;
                }
                break;
            case "md5":
            case "hash":
                yield return row.Hash;
                break;
            case "sha256":
                yield return row.Sha256;
                break;
            case "clear":
                yield return ScoreDisplayTextFormatter.FormatClear(row.Clear);
                break;
            case "rank":
            case "djlevel":
            case "dj":
                yield return ScoreDisplayTextFormatter.FormatRank(row.Rank);
                break;
            case "rate":
                yield return FormatNullableDoubleInvariant(row.RateDouble);
                break;
            case "score":
                yield return FormatNullableIntInvariant(row.Score);
                break;
            case "combo":
                yield return FormatNullableIntInvariant(row.MaxCombo);
                break;
            case "bp":
                yield return FormatNullableIntInvariant(row.MinBp);
                break;
            default:
                foreach (string value in GetChartInfoValues(row.ChartInfo, field))
                {
                    yield return value;
                }
                break;
        }
    }

    private static IEnumerable<string> GetPlaylistDetailValues(PlaylistDetailSourceRow row, string field)
    {
        switch (field)
        {
            case null:
                yield return row.Title;
                yield return row.genre;
                yield return row.Artist;
                yield return row.tag;
                yield return row.path;
                yield return row.RefTablesSymbols;
                yield return row.hash;
                yield return row.sha256;
                yield return row.memo;
                yield return row.comment;
                break;
            case "title":
                yield return row.Title;
                break;
            case "artist":
                yield return row.Artist;
                break;
            case "genre":
                yield return row.genre;
                break;
            case "tag":
                yield return row.tag;
                break;
            case "path":
                yield return row.path;
                break;
            case "playlist":
            case "ref":
            case "table":
                foreach (string name in SplitPlaylistReferenceNames(row.RefTablesNames))
                {
                    yield return name;
                }
                break;
            case "md5":
            case "hash":
                yield return row.hash;
                break;
            case "sha256":
                yield return row.sha256;
                break;
            case "memo":
                yield return row.memo;
                break;
            case "comment":
                yield return row.comment;
                break;
            case "clear":
                yield return ScoreDisplayTextFormatter.FormatClear(row.clear);
                break;
            case "rank":
            case "djlevel":
            case "dj":
                yield return ScoreDisplayTextFormatter.FormatRank(row.rank);
                break;
            case "rate":
                yield return FormatNullableDoubleInvariant(row.rateDouble);
                break;
            case "score":
                yield return FormatNullableIntInvariant(row.score);
                break;
            case "combo":
                yield return FormatNullableIntInvariant(row.maxcombo);
                break;
            case "bp":
                yield return FormatNullableIntInvariant(row.minbp);
                break;
            default:
                foreach (string value in GetChartInfoValues(row.ChartInfo, field))
                {
                    yield return value;
                }
                break;
        }
    }

    private static IEnumerable<string> SplitPlaylistReferenceNames(string names)
    {
        return (names ?? string.Empty)
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0);
    }

    private static bool MatchesScoreAlternative(SearchAlternative alternative, string field, ClearType clear, RankType rank, double? rate, int? score, int? combo, int? bp)
    {
        if (alternative.IsInvalid || alternative.IsEmpty)
        {
            return false;
        }
        string term = alternative.Term?.Trim() ?? string.Empty;
        if (string.Equals(field, "clear", StringComparison.Ordinal))
        {
            if (IsDefinedTerm(term, out bool clearDefined))
            {
                return clearDefined;
            }
            return TryParseClearTerm(term, out ClearType expectedClear) && clear == expectedClear;
        }
        if (IsRankField(field))
        {
            if (IsDefinedTerm(term, out bool rankDefined))
            {
                return (rank != RankType.INVALID) == rankDefined;
            }
            return TryParseRankTerm(term, out RankType expectedRank) && rank == expectedRank;
        }
        double? value = GetScoreNumericValue(field, rate, score, combo, bp);
        if (IsDefinedTerm(term, out bool numericDefined))
        {
            return value.HasValue == numericDefined;
        }
        return MatchesNumericTerm(value, term);
    }

    private static bool IsRankField(string field)
    {
        return string.Equals(field, "rank", StringComparison.Ordinal)
            || string.Equals(field, "djlevel", StringComparison.Ordinal)
            || string.Equals(field, "dj", StringComparison.Ordinal);
    }

    private static double? GetScoreNumericValue(string field, double? rate, int? score, int? combo, int? bp)
    {
        return field switch
        {
            "rate" => rate,
            "score" => score,
            "combo" => combo,
            "bp" => bp,
            _ => null,
        };
    }

    private static bool TryParseClearTerm(string term, out ClearType clear)
    {
        switch (NormalizeEnumTerm(term))
        {
            case "nosong":
                clear = ClearType.NO_SONG;
                return true;
            case "np":
            case "noplay":
                clear = ClearType.NO_PLAY;
                return true;
            case "f":
            case "failed":
                clear = ClearType.FAILED;
                return true;
            case "ae":
            case "assist":
                clear = ClearType.INVALID;
                return true;
            case "lae":
            case "lassist":
                clear = ClearType.L_ASSIST;
                return true;
            case "ec":
            case "easyclear":
                clear = ClearType.EASY;
                return true;
            case "nc":
            case "clear":
                clear = ClearType.CLEAR;
                return true;
            case "hc":
            case "hardclear":
                clear = ClearType.HARD;
                return true;
            case "exh":
            case "exhard":
                clear = ClearType.EX_HARD;
                return true;
            case "fc":
            case "fullcombo":
                clear = ClearType.FC;
                return true;
            case "pf":
            case "perfect":
                clear = ClearType.PA;
                return true;
            case "max":
                clear = ClearType.MAX;
                return true;
            default:
                clear = ClearType.NO_PLAY;
                return false;
        }
    }

    private static bool TryParseRankTerm(string term, out RankType rank)
    {
        switch (NormalizeEnumTerm(term))
        {
            case "f":
                rank = RankType.F;
                return true;
            case "e":
                rank = RankType.E;
                return true;
            case "d":
                rank = RankType.D;
                return true;
            case "c":
                rank = RankType.C;
                return true;
            case "b":
                rank = RankType.B;
                return true;
            case "a":
                rank = RankType.A;
                return true;
            case "aa":
                rank = RankType.AA;
                return true;
            case "aaa":
                rank = RankType.AAA;
                return true;
            case "max":
                rank = RankType.MAX;
                return true;
            default:
                rank = RankType.INVALID;
                return false;
        }
    }

    private static string NormalizeEnumTerm(string term)
    {
        var builder = new StringBuilder();
        foreach (char c in term ?? string.Empty)
        {
            if (!char.IsWhiteSpace(c) && c != '-' && c != '_')
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }
        return builder.ToString();
    }

    private static string FormatNullableIntInvariant(int? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string FormatNullableDoubleInvariant(double? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static IEnumerable<string> GetChartInfoValues(LR2SongDBExtended.chart_info chartInfo, string field)
    {
        switch (field)
        {
            case "level":
                yield return ChartInfoDisplayFormatter.FormatOptionalInt(chartInfo?.level);
                break;
            case "difficulty":
                yield return ChartInfoDisplayFormatter.FormatDifficulty(chartInfo?.difficulty);
                yield return ChartInfoDisplayFormatter.FormatOptionalInt(chartInfo?.difficulty);
                break;
            case "mainbpm":
                yield return ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.mainbpm);
                break;
            case "maxbpm":
                yield return ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.maxbpm);
                break;
            case "minbpm":
                yield return ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.minbpm);
                break;
            case "duration":
            case "length":
                yield return ChartInfoDisplayFormatter.FormatDuration(chartInfo?.length);
                break;
            case "judge":
                yield return ChartInfoDisplayFormatter.FormatJudge(chartInfo?.judge);
                yield return ChartInfoDisplayFormatter.FormatOptionalInt(chartInfo?.judge);
                break;
            case "judge%":
            case "judgepct":
                yield return ChartInfoDisplayFormatter.FormatOptionalInt(chartInfo?.judge);
                break;
            case "feature":
                yield return chartInfo == null ? string.Empty : ChartInfoDisplayFormatter.FormatFeature(chartInfo.feature);
                break;
            case "notes":
                yield return chartInfo == null ? string.Empty : chartInfo.notes.ToString(CultureInfo.InvariantCulture);
                break;
            case "long":
            case "ln":
                yield return chartInfo == null ? string.Empty : chartInfo.ln.ToString(CultureInfo.InvariantCulture);
                break;
            case "scratch":
                yield return ChartInfoDisplayFormatter.FormatOptionalInt(ChartInfoDisplayFormatter.GetScratchNotes(chartInfo));
                break;
            case "total":
                yield return ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.total);
                break;
            case "tn":
            case "t/n":
                yield return ChartInfoDisplayFormatter.FormatFixedTwo(ChartInfoDisplayFormatter.GetTotalPerNote(chartInfo));
                break;
            case "density":
                yield return ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.density);
                break;
            case "peak":
            case "peakdensity":
                yield return ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.peakdensity);
                break;
            case "end":
            case "enddensity":
                yield return ChartInfoDisplayFormatter.FormatOptionalDouble(chartInfo?.enddensity);
                break;
            case "soflan":
                yield return chartInfo == null ? string.Empty : chartInfo.speedchange_count.ToString(CultureInfo.InvariantCulture);
                break;
        }
    }

    private static bool MatchesChartInfoAlternative(SearchAlternative alternative, LR2SongDBExtended.chart_info chartInfo, string field)
    {
        if (alternative.IsInvalid || alternative.IsEmpty)
        {
            return false;
        }
        string term = alternative.Term?.Trim() ?? string.Empty;
        if (IsDefinedTerm(term, out bool defined))
        {
            return IsChartFieldDefined(chartInfo, field) == defined;
        }
        if (string.Equals(field, "feature", StringComparison.Ordinal))
        {
            return MatchesFeatureTerm(chartInfo, term);
        }
        if (string.Equals(field, "difficulty", StringComparison.Ordinal) && TryParseDifficultyTerm(term, out int difficulty))
        {
            return chartInfo != null && chartInfo.difficulty == difficulty;
        }
        if (string.Equals(field, "judge", StringComparison.Ordinal) && TryParseJudgeTerm(term, out int judgeLower, out int judgeUpperExclusive))
        {
            return chartInfo != null && chartInfo.judge.HasValue && chartInfo.judge.Value >= judgeLower && chartInfo.judge.Value < judgeUpperExclusive;
        }
        return MatchesNumericTerm(GetChartFieldNumericValue(chartInfo, field), term);
    }

    private static bool IsDefinedTerm(string term, out bool defined)
    {
        if (string.Equals(term, "defined", StringComparison.OrdinalIgnoreCase))
        {
            defined = true;
            return true;
        }
        if (string.Equals(term, "undefined", StringComparison.OrdinalIgnoreCase)
            || string.Equals(term, "undef", StringComparison.OrdinalIgnoreCase)
            || string.Equals(term, "null", StringComparison.OrdinalIgnoreCase))
        {
            defined = false;
            return true;
        }
        defined = false;
        return false;
    }

    private static bool IsChartFieldDefined(LR2SongDBExtended.chart_info chartInfo, string field)
    {
        if (chartInfo == null)
        {
            return false;
        }
        return field switch
        {
            "level" => chartInfo.level.HasValue,
            "mainbpm" => chartInfo.mainbpm.HasValue,
            "maxbpm" => chartInfo.maxbpm.HasValue,
            "minbpm" => chartInfo.minbpm.HasValue,
            "judge" or "judge%" or "judgepct" => chartInfo.judge.HasValue,
            "difficulty" => chartInfo.difficulty_defined,
            "total" or "tn" or "t/n" => chartInfo.total_defined && chartInfo.total.HasValue,
            _ => true,
        };
    }

    private static double? GetChartFieldNumericValue(LR2SongDBExtended.chart_info chartInfo, string field)
    {
        if (chartInfo == null)
        {
            return null;
        }
        return field switch
        {
            "level" => chartInfo.level,
            "difficulty" => chartInfo.difficulty,
            "mainbpm" => chartInfo.mainbpm,
            "maxbpm" => chartInfo.maxbpm,
            "minbpm" => chartInfo.minbpm,
            "duration" or "length" => chartInfo.length / 1000.0,
            "judge" or "judge%" or "judgepct" => chartInfo.judge,
            "notes" => chartInfo.notes,
            "long" or "ln" => chartInfo.ln,
            "scratch" => chartInfo.s + chartInfo.ls,
            "total" => chartInfo.total,
            "tn" or "t/n" => ChartInfoDisplayFormatter.GetTotalPerNote(chartInfo),
            "density" => chartInfo.density,
            "peak" or "peakdensity" => chartInfo.peakdensity,
            "end" or "enddensity" => chartInfo.enddensity,
            "soflan" => chartInfo.speedchange_count,
            _ => null,
        };
    }

    private static bool MatchesNumericTerm(double? value, string term)
    {
        if (!value.HasValue || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }
        string trimmed = term.Trim();
        if (trimmed.Contains(".."))
        {
            string[] parts = trimmed.Split([".."], StringSplitOptions.None);
            if (parts.Length != 2)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(parts[0]) && (!TryParseDouble(parts[0], out double min) || value.Value < min))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(parts[1]) && (!TryParseDouble(parts[1], out double max) || value.Value > max))
            {
                return false;
            }
            return true;
        }
        string[] operators = [">=", "<=", ">", "<"];
        foreach (string op in operators)
        {
            if (!trimmed.StartsWith(op, StringComparison.Ordinal))
            {
                continue;
            }
            if (!TryParseDouble(trimmed.Substring(op.Length), out double threshold))
            {
                return false;
            }
            switch (op)
            {
                case ">=":
                    return value.Value >= threshold;
                case "<=":
                    return value.Value <= threshold;
                case ">":
                    return value.Value > threshold;
                case "<":
                    return value.Value < threshold;
            }
        }
        return TryParseDouble(trimmed, out double expected) && Math.Abs(value.Value - expected) < 0.000000001;
    }

    private static bool TryParseDouble(string value, out double parsed)
    {
        return double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
    }

    private static bool TryParseDifficultyTerm(string term, out int difficulty)
    {
        switch ((term ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "beginner":
                difficulty = 1;
                return true;
            case "normal":
                difficulty = 2;
                return true;
            case "hyper":
                difficulty = 3;
                return true;
            case "another":
                difficulty = 4;
                return true;
            case "insane":
                difficulty = 5;
                return true;
            default:
                return int.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out difficulty);
        }
    }

    private static bool TryParseJudgeTerm(string term, out int lower, out int upperExclusive)
    {
        switch ((term ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "veryhard":
                lower = int.MinValue;
                upperExclusive = 35;
                return true;
            case "hard":
                lower = 35;
                upperExclusive = 60;
                return true;
            case "normal":
                lower = 60;
                upperExclusive = 85;
                return true;
            case "easy":
                lower = 85;
                upperExclusive = 110;
                return true;
            case "veryeasy":
                lower = 110;
                upperExclusive = int.MaxValue;
                return true;
            default:
                lower = 0;
                upperExclusive = 0;
                return false;
        }
    }

    private static bool MatchesFeatureTerm(LR2SongDBExtended.chart_info chartInfo, string term)
    {
        if (chartInfo == null)
        {
            return false;
        }
        int? bit = GetFeatureBit(term);
        return bit.HasValue && ChartInfoDisplayFormatter.HasFeature(chartInfo.feature, bit.Value);
    }

    private static int? GetFeatureBit(string term)
    {
        return (term ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "ln" => ChartInfoDisplayFormatter.FeatureUndefinedLongNote,
            "mine" => ChartInfoDisplayFormatter.FeatureMineNote,
            "random" => ChartInfoDisplayFormatter.FeatureRandom,
            "lnmode" or "ln(#lnmode)" => ChartInfoDisplayFormatter.FeatureLongNote,
            "cn" => ChartInfoDisplayFormatter.FeatureChargeNote,
            "hcn" => ChartInfoDisplayFormatter.FeatureHellChargeNote,
            "stop" => ChartInfoDisplayFormatter.FeatureStopSequence,
            "scroll" => ChartInfoDisplayFormatter.FeatureScroll,
            _ => null,
        };
    }

    private static IEnumerable<string> GetPlaylistSummaryValues(PlaylistSummaryRow row, string field)
    {
        switch (field)
        {
            case null:
                yield return row.PlaylistId?.ToString() ?? string.Empty;
                yield return row.OutputBaseDisplayName;
                yield return row.Name;
                yield return row.FolderName;
                yield return row.CompatPrefix;
                yield return row.Symbol;
                yield return row.HeaderUriText;
                yield return row.DataUriText;
                break;
            case "id":
                yield return row.PlaylistId?.ToString() ?? string.Empty;
                break;
            case "name":
                yield return row.Name;
                break;
            case "folder":
            case "foldername":
                yield return row.FolderName;
                break;
            case "output":
                yield return row.OutputBaseDisplayName;
                break;
            case "prefix":
                yield return row.CompatPrefix;
                break;
            case "symbol":
                yield return row.Symbol;
                break;
            case "header":
                yield return row.HeaderUriText;
                break;
            case "data":
                yield return row.DataUriText;
                break;
        }
    }

    private static IEnumerable<string> GetPlayHistoryValues(PlayHistoryRow row, string field)
    {
        switch (field)
        {
            case null:
                yield return row.Title;
                yield return row.Artist;
                yield return row.Path;
                yield return row.FolderLabels;
                yield return row.PlaylistNames;
                yield return row.RawHash;
                yield return row.Md5;
                yield return row.Sha256;
                yield return row.Kind;
                yield return row.ScoreWriteType;
                yield return row.BestClear;
                yield return row.Source;
                yield return row.SourcePath;
                yield return FormatPlayHistoryDate(row.PlayedAt);
                break;
            case "title":
                yield return row.Title;
                break;
            case "artist":
                yield return row.Artist;
                break;
            case "path":
                yield return row.Path;
                break;
            case "folder":
                yield return row.FolderLabels;
                break;
            case "playlist":
            case "ref":
            case "table":
                foreach (string name in SplitPlaylistReferenceNames(row.PlaylistNames))
                {
                    yield return name;
                }
                break;
            case "md5":
                yield return row.Md5;
                break;
            case "hash":
                yield return row.RawHash;
                break;
            case "sha256":
                yield return row.Sha256;
                break;
            case "date":
                yield return FormatPlayHistoryDate(row.PlayedAt);
                yield return row.PlayedAt.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
                break;
            case "year":
                yield return row.PlayedAt.ToString("yyyy", CultureInfo.InvariantCulture);
                break;
            case "month":
                yield return row.PlayedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                yield return row.PlayedAt.ToString("yyyy/MM", CultureInfo.InvariantCulture);
                yield return row.PlayedAt.ToString("MM", CultureInfo.InvariantCulture);
                break;
            case "type":
            case "kind":
                yield return row.Kind;
                yield return row.ScoreWriteType;
                break;
            case "clear":
                yield return row.BestClear;
                yield return row.OldBestClear.HasValue ? ScoreDisplayTextFormatter.FormatClear(row.OldBestClear.Value) : string.Empty;
                yield return row.NewBestClear.HasValue ? ScoreDisplayTextFormatter.FormatClear(row.NewBestClear.Value) : string.Empty;
                break;
            case "oldclear":
                yield return row.OldBestClear.HasValue ? ScoreDisplayTextFormatter.FormatClear(row.OldBestClear.Value) : string.Empty;
                break;
            case "newclear":
                yield return row.NewBestClear.HasValue ? ScoreDisplayTextFormatter.FormatClear(row.NewBestClear.Value) : string.Empty;
                break;
            case "finalized":
                yield return row.Finalized ? "true" : "false";
                yield return row.Finalized ? "1" : "0";
                break;
            case "source":
                yield return row.Source;
                yield return row.SourcePath;
                break;
        }
    }

    private static bool IsPlayHistoryClearField(string field)
    {
        return string.Equals(field, "clear", StringComparison.Ordinal)
            || string.Equals(field, "oldclear", StringComparison.Ordinal)
            || string.Equals(field, "newclear", StringComparison.Ordinal);
    }

    private static bool MatchesPlayHistoryClearAlternative(SearchAlternative alternative, PlayHistoryRow row, string field)
    {
        if (alternative.IsInvalid || alternative.IsEmpty || row == null)
        {
            return false;
        }
        string term = alternative.Term?.Trim() ?? string.Empty;
        if (IsDefinedTerm(term, out bool clearDefined))
        {
            return GetPlayHistoryClearDefined(row, field) == clearDefined;
        }
        if (TryParseClearTerm(term, out ClearType expectedClear))
        {
            return MatchesPlayHistoryClearValue(row, field, expectedClear);
        }
        return MatchesAlternative(alternative, GetPlayHistoryValues(row, field));
    }

    private static bool GetPlayHistoryClearDefined(PlayHistoryRow row, string field)
    {
        return field switch
        {
            "oldclear" => row.OldBestClear.HasValue,
            "newclear" => row.NewBestClear.HasValue,
            _ => row.NewBestClear.HasValue,
        };
    }

    private static bool MatchesPlayHistoryClearValue(PlayHistoryRow row, string field, ClearType expectedClear)
    {
        return field switch
        {
            "oldclear" => row.OldBestClear == expectedClear,
            "newclear" => row.NewBestClear == expectedClear,
            _ => row.NewBestClear == expectedClear || row.OldBestClear == expectedClear,
        };
    }

    private static bool MatchesBooleanAlternative(SearchAlternative alternative, bool value)
    {
        if (alternative.IsInvalid || alternative.IsEmpty)
        {
            return false;
        }
        string term = NormalizeEnumTerm(alternative.Term);
        bool? expected = term switch
        {
            "1" or "true" or "yes" or "y" or "finalized" => true,
            "0" or "false" or "no" or "n" or "unfinalized" or "pending" => false,
            _ => null,
        };
        return expected.HasValue && expected.Value == value;
    }

    private static bool IsPlayHistoryDateField(string field)
    {
        return string.Equals(field, "date", StringComparison.Ordinal)
            || string.Equals(field, "year", StringComparison.Ordinal)
            || string.Equals(field, "month", StringComparison.Ordinal);
    }

    private static bool MatchesPlayHistoryDateAlternative(SearchAlternative alternative, PlayHistoryRow row, string field)
    {
        if (alternative.IsInvalid || alternative.IsEmpty || row == null)
        {
            return false;
        }
        string term = alternative.Term?.Trim() ?? string.Empty;
        if (string.Equals(field, "date", StringComparison.Ordinal))
        {
            return PlayHistoryDateSearchTerm.TryParse(term, out PlayHistoryDateSearchTerm dateTerm)
                && dateTerm.Matches(row.PlayedAtWallClockSecond);
        }
        if (string.Equals(field, "year", StringComparison.Ordinal))
        {
            return int.TryParse(term, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year)
                && row.PlayedAt.Year == year;
        }
        return TryParsePlayHistoryMonthTerm(term, out int? yearPart, out int month)
            && row.PlayedAt.Month == month
            && (!yearPart.HasValue || row.PlayedAt.Year == yearPart.Value);
    }

    private static bool TryParsePlayHistoryMonthTerm(string term, out int? year, out int month)
    {
        year = null;
        month = 0;
        string text = term?.Trim() ?? string.Empty;
        if (text.Contains("-") || text.Contains("/"))
        {
            string[] parts = text.Split(['-', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedYear)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMonth)
                || parsedMonth < 1
                || parsedMonth > 12)
            {
                return false;
            }
            year = parsedYear;
            month = parsedMonth;
            return true;
        }
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int monthOnly)
            || monthOnly < 1
            || monthOnly > 12)
        {
            return false;
        }
        month = monthOnly;
        return true;
    }

    private static string FormatPlayHistoryDate(DateTime playedAt)
    {
        return playedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static bool Contains(string value, string term)
    {
        return !string.IsNullOrEmpty(term)
            && !string.IsNullOrEmpty(value)
            && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private readonly struct SearchCondition
    {
        internal SearchCondition(bool isNegated, string field, bool isRegex, SearchAlternative[] alternatives, bool isInvalid, GridKeywordSearchDiagnosticKind diagnosticKind, string diagnosticValue)
        {
            IsNegated = isNegated;
            Field = field;
            IsRegex = isRegex;
            Alternatives = alternatives ?? [];
            IsInvalid = isInvalid;
            DiagnosticKind = diagnosticKind;
            DiagnosticValue = diagnosticValue ?? string.Empty;
        }

        internal bool IsNegated { get; }

        internal string Field { get; }

        internal bool IsRegex { get; }

        internal SearchAlternative[] Alternatives { get; }

        internal bool IsInvalid { get; }

        internal GridKeywordSearchDiagnosticKind DiagnosticKind { get; }

        internal string DiagnosticValue { get; }

        internal int SortWeight => IsInvalid ? 0 : (IsRegex ? 100000 : Alternatives.Where(alternative => !alternative.IsInvalid && !alternative.IsEmpty).Select(alternative => alternative.Term.Length).DefaultIfEmpty(0).Min());

        internal static SearchCondition Invalid(bool isNegated, string field, GridKeywordSearchDiagnosticKind diagnosticKind, string diagnosticValue, bool isRegex = false, SearchAlternative[] alternatives = null)
        {
            return new SearchCondition(isNegated, field, isRegex, alternatives ?? [], isInvalid: true, diagnosticKind, diagnosticValue);
        }
    }

    private readonly struct SearchAlternative
    {
        internal static readonly SearchAlternative Empty = new(string.Empty, regex: null, isInvalid: false, isEmpty: true);

        internal SearchAlternative(string term, Regex regex, bool isInvalid, bool isEmpty)
        {
            Term = term ?? string.Empty;
            Regex = regex;
            IsInvalid = isInvalid;
            IsEmpty = isEmpty;
        }

        internal string Term { get; }

        internal Regex Regex { get; }

        internal bool IsInvalid { get; }

        internal bool IsEmpty { get; }
    }
}
