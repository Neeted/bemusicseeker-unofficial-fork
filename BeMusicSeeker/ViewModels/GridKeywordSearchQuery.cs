using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal enum GridKeywordSearchContext
{
    BmsFile,
    PlaylistDetail,
    PlaylistSummary
}

internal enum GridKeywordSearchDiagnosticKind
{
    None,
    UnknownField,
    EmptyFieldTerm,
    EmptyNegation,
    EmptyOr,
    InvalidRegex
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

    private readonly SearchCondition[] conditions;

    private GridKeywordSearchQuery(SearchCondition[] conditions)
    {
        this.conditions = conditions ?? Array.Empty<SearchCondition>();
    }

    internal bool HasTokens => conditions.Length > 0;

    internal IReadOnlyList<GridKeywordSearchDiagnostic> GetDiagnostics(GridKeywordSearchContext context)
    {
        if (!HasTokens)
        {
            return Array.Empty<GridKeywordSearchDiagnostic>();
        }
        List<GridKeywordSearchDiagnostic> diagnostics = new List<GridKeywordSearchDiagnostic>();
        foreach (SearchCondition condition in conditions)
        {
            if (!string.IsNullOrEmpty(condition.Field) && !IsKnownField(context, condition.Field))
            {
                diagnostics.Add(new GridKeywordSearchDiagnostic(GridKeywordSearchDiagnosticKind.UnknownField, condition.Field));
            }
            if (condition.DiagnosticKind != GridKeywordSearchDiagnosticKind.None)
            {
                diagnostics.Add(new GridKeywordSearchDiagnostic(condition.DiagnosticKind, condition.DiagnosticValue));
            }
            foreach (SearchAlternative alternative in condition.Alternatives)
            {
                if (alternative.IsInvalid && condition.IsRegex)
                {
                    diagnostics.Add(new GridKeywordSearchDiagnostic(GridKeywordSearchDiagnosticKind.InvalidRegex, alternative.Term));
                }
            }
        }
        return diagnostics;
    }

    internal static GridKeywordSearchQuery Parse(string keywordFilter)
    {
        if (string.IsNullOrWhiteSpace(keywordFilter))
        {
            return new GridKeywordSearchQuery(Array.Empty<SearchCondition>());
        }
        SearchCondition[] parsedConditions = Tokenize(keywordFilter)
            .Select(ParseCondition)
            .OrderBy((SearchCondition condition) => condition.SortWeight)
            .ToArray();
        return new GridKeywordSearchQuery(parsedConditions);
    }

    internal bool MatchesBmsFile(BMSFile file)
    {
        if (!HasTokens)
        {
            return true;
        }
        if (file == null)
        {
            return false;
        }
        foreach (SearchCondition condition in conditions)
        {
            if (!MatchesCondition(condition, file))
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

    private static IEnumerable<string> Tokenize(string keywordFilter)
    {
        StringBuilder builder = new StringBuilder();
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

    private static SearchCondition CreateCondition(bool isNegated, string field, bool isRegex, string rawTerms)
    {
        if (string.IsNullOrEmpty(rawTerms))
        {
            return SearchCondition.Invalid(isNegated, field, field == null ? GridKeywordSearchDiagnosticKind.EmptyOr : GridKeywordSearchDiagnosticKind.EmptyFieldTerm, field ?? string.Empty);
        }
        List<SearchAlternative> alternatives = SplitUnquoted(rawTerms, '|')
            .Select((string rawAlternative) => CreateAlternative(rawAlternative, isRegex))
            .Where((SearchAlternative alternative) => !alternative.IsEmpty)
            .ToList();
        if (alternatives.Count == 0)
        {
            return SearchCondition.Invalid(isNegated, field, GridKeywordSearchDiagnosticKind.EmptyOr, rawTerms);
        }
        if (alternatives.All((SearchAlternative alternative) => alternative.IsInvalid))
        {
            return SearchCondition.Invalid(isNegated, field, GridKeywordSearchDiagnosticKind.None, string.Empty, isRegex, alternatives.ToArray());
        }
        return new SearchCondition(isNegated, field, isRegex, alternatives.ToArray(), isInvalid: false, diagnosticKind: GridKeywordSearchDiagnosticKind.None, diagnosticValue: string.Empty);
    }

    private static SearchAlternative CreateAlternative(string rawAlternative, bool isRegex)
    {
        string term = NormalizeTerm(rawAlternative);
        if (string.IsNullOrEmpty(term))
        {
            return SearchAlternative.Empty;
        }
        if (!isRegex)
        {
            return new SearchAlternative(term, regex: null, isInvalid: false, isEmpty: false);
        }
        try
        {
            Regex regex = new Regex(term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return new SearchAlternative(term, regex, isInvalid: false, isEmpty: false);
        }
        catch (ArgumentException)
        {
            return new SearchAlternative(term, regex: null, isInvalid: true, isEmpty: false);
        }
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
        StringBuilder builder = new StringBuilder(end - start);
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
        StringBuilder builder = new StringBuilder();
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

    private static bool MatchesCondition(SearchCondition condition, BMSFile file)
    {
        if (condition.IsInvalid || !IsKnownBmsFileField(condition.Field))
        {
            return false;
        }
        bool matched = condition.Alternatives.Any((SearchAlternative alternative) => MatchesAlternative(alternative, GetBmsFileValues(file, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesCondition(SearchCondition condition, PlaylistDetailSourceRow row)
    {
        if (condition.IsInvalid || !IsKnownPlaylistDetailField(condition.Field))
        {
            return false;
        }
        bool matched = condition.Alternatives.Any((SearchAlternative alternative) => MatchesAlternative(alternative, GetPlaylistDetailValues(row, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesCondition(SearchCondition condition, PlaylistSummaryRow row)
    {
        if (condition.IsInvalid || !IsKnownPlaylistSummaryField(condition.Field))
        {
            return false;
        }
        bool matched = condition.Alternatives.Any((SearchAlternative alternative) => MatchesAlternative(alternative, GetPlaylistSummaryValues(row, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesAlternative(SearchAlternative alternative, IEnumerable<string> values)
    {
        if (alternative.IsInvalid || alternative.IsEmpty)
        {
            return false;
        }
        foreach (string value in values ?? Enumerable.Empty<string>())
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

    private static bool IsKnownBmsFileField(string field)
    {
        switch (field)
        {
            case null:
            case "title":
            case "artist":
            case "genre":
            case "tag":
            case "path":
            case "playlist":
            case "ref":
            case "md5":
            case "hash":
            case "sha256":
                return true;
            default:
                return false;
        }
    }

    private static bool IsKnownPlaylistDetailField(string field)
    {
        switch (field)
        {
            case null:
            case "title":
            case "artist":
            case "genre":
            case "tag":
            case "path":
            case "playlist":
            case "ref":
            case "md5":
            case "hash":
            case "sha256":
            case "memo":
            case "comment":
                return true;
            default:
                return false;
        }
    }

    private static bool IsKnownPlaylistSummaryField(string field)
    {
        switch (field)
        {
            case null:
            case "id":
            case "name":
            case "symbol":
                return true;
            default:
                return false;
        }
    }

    private static bool IsKnownField(GridKeywordSearchContext context, string field)
    {
        switch (context)
        {
            case GridKeywordSearchContext.PlaylistDetail:
                return IsKnownPlaylistDetailField(field);
            case GridKeywordSearchContext.PlaylistSummary:
                return IsKnownPlaylistSummaryField(field);
            default:
                return IsKnownBmsFileField(field);
        }
    }

    private static IEnumerable<string> GetBmsFileValues(BMSFile file, string field)
    {
        switch (field)
        {
            case null:
                yield return file.Title;
                yield return file.genre;
                yield return file.Artist;
                yield return file.tag;
                yield return file.path;
                yield return file.RefTablesSymbols;
                yield return file.hash;
                yield return file.sha256;
                break;
            case "title":
                yield return file.Title;
                break;
            case "artist":
                yield return file.Artist;
                break;
            case "genre":
                yield return file.genre;
                break;
            case "tag":
                yield return file.tag;
                break;
            case "path":
                yield return file.path;
                break;
            case "playlist":
            case "ref":
                yield return file.RefTablesSymbols;
                break;
            case "md5":
            case "hash":
                yield return file.hash;
                break;
            case "sha256":
                yield return file.sha256;
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
                yield return row.RefTablesSymbols;
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
        }
    }

    private static IEnumerable<string> GetPlaylistSummaryValues(PlaylistSummaryRow row, string field)
    {
        switch (field)
        {
            case null:
                yield return row.PlaylistId?.ToString() ?? string.Empty;
                yield return row.Name;
                yield return row.Symbol;
                break;
            case "id":
                yield return row.PlaylistId?.ToString() ?? string.Empty;
                break;
            case "name":
                yield return row.Name;
                break;
            case "symbol":
                yield return row.Symbol;
                break;
        }
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
            Alternatives = alternatives ?? Array.Empty<SearchAlternative>();
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

        internal int SortWeight => IsInvalid ? 0 : (IsRegex ? 100000 : Alternatives.Where((SearchAlternative alternative) => !alternative.IsInvalid && !alternative.IsEmpty).Select((SearchAlternative alternative) => alternative.Term.Length).DefaultIfEmpty(0).Min());

        internal static SearchCondition Invalid(bool isNegated, string field, GridKeywordSearchDiagnosticKind diagnosticKind, string diagnosticValue, bool isRegex = false, SearchAlternative[] alternatives = null)
        {
            return new SearchCondition(isNegated, field, isRegex, alternatives ?? Array.Empty<SearchAlternative>(), isInvalid: true, diagnosticKind, diagnosticValue);
        }
    }

    private readonly struct SearchAlternative
    {
        internal static readonly SearchAlternative Empty = new SearchAlternative(string.Empty, regex: null, isInvalid: false, isEmpty: true);

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
