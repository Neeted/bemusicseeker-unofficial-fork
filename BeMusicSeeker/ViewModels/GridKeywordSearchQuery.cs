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

    private static readonly string[] BaseChartFields =
    {
        "level", "difficulty", "mainbpm", "maxbpm", "minbpm", "duration", "length", "judge", "judge%", "judgepct",
        "feature", "notes", "long", "ln", "scratch", "total", "tn", "t/n", "density", "peak", "peakdensity",
        "end", "enddensity", "soflan"
    };

    private static readonly string[] BmsFileFields = new[] { "title", "artist", "genre", "tag", "path", "playlist", "ref", "md5", "hash", "sha256" }
        .Concat(BaseChartFields)
        .ToArray();

    private static readonly string[] PlaylistDetailFields = new[] { "title", "artist", "genre", "tag", "path", "playlist", "ref", "md5", "hash", "sha256", "memo", "comment" }
        .Concat(BaseChartFields)
        .ToArray();

    private static readonly string[] PlaylistSummaryFields = { "id", "name", "symbol" };

    private readonly SearchCondition[] conditions;

    private GridKeywordSearchQuery(SearchCondition[] conditions)
    {
        this.conditions = conditions ?? Array.Empty<SearchCondition>();
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
        switch (context)
        {
            case GridKeywordSearchContext.PlaylistDetail:
                return PlaylistDetailFields;
            case GridKeywordSearchContext.PlaylistSummary:
                return PlaylistSummaryFields;
            default:
                return BmsFileFields;
        }
    }

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
        bool matched = IsChartInfoField(condition.Field) && !condition.IsRegex
            ? condition.Alternatives.Any((SearchAlternative alternative) => MatchesChartInfoAlternative(alternative, file.ChartInfo, condition.Field))
            : condition.Alternatives.Any((SearchAlternative alternative) => MatchesAlternative(alternative, GetBmsFileValues(file, condition.Field)));
        return condition.IsNegated ? !matched : matched;
    }

    private static bool MatchesCondition(SearchCondition condition, PlaylistDetailSourceRow row)
    {
        if (condition.IsInvalid || !IsKnownPlaylistDetailField(condition.Field))
        {
            return false;
        }
        bool matched = IsChartInfoField(condition.Field) && !condition.IsRegex
            ? condition.Alternatives.Any((SearchAlternative alternative) => MatchesChartInfoAlternative(alternative, row.ChartInfo, condition.Field))
            : condition.Alternatives.Any((SearchAlternative alternative) => MatchesAlternative(alternative, GetPlaylistDetailValues(row, condition.Field)));
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
        return field == null || BmsFileFields.Contains(field);
    }

    private static bool IsKnownPlaylistDetailField(string field)
    {
        return field == null || PlaylistDetailFields.Contains(field);
    }

    private static bool IsKnownPlaylistSummaryField(string field)
    {
        return field == null || PlaylistSummaryFields.Contains(field);
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

    private static bool IsChartInfoField(string field)
    {
        return field != null && BaseChartFields.Contains(field);
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
            default:
                foreach (string value in GetChartInfoValues(file.ChartInfo, field))
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
            default:
                foreach (string value in GetChartInfoValues(row.ChartInfo, field))
                {
                    yield return value;
                }
                break;
        }
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
        if (string.Equals(term, "undefined", StringComparison.OrdinalIgnoreCase))
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
        switch (field)
        {
            case "level":
                return chartInfo.level.HasValue;
            case "mainbpm":
                return chartInfo.mainbpm.HasValue;
            case "maxbpm":
                return chartInfo.maxbpm.HasValue;
            case "minbpm":
                return chartInfo.minbpm.HasValue;
            case "judge":
            case "judge%":
            case "judgepct":
                return chartInfo.judge.HasValue;
            case "difficulty":
                return chartInfo.difficulty_defined;
            case "total":
            case "tn":
            case "t/n":
                return chartInfo.total_defined && chartInfo.total.HasValue;
            default:
                return true;
        }
    }

    private static double? GetChartFieldNumericValue(LR2SongDBExtended.chart_info chartInfo, string field)
    {
        if (chartInfo == null)
        {
            return null;
        }
        switch (field)
        {
            case "level":
                return chartInfo.level;
            case "difficulty":
                return chartInfo.difficulty;
            case "mainbpm":
                return chartInfo.mainbpm;
            case "maxbpm":
                return chartInfo.maxbpm;
            case "minbpm":
                return chartInfo.minbpm;
            case "duration":
            case "length":
                return chartInfo.length / 1000.0;
            case "judge":
            case "judge%":
            case "judgepct":
                return chartInfo.judge;
            case "notes":
                return chartInfo.notes;
            case "long":
            case "ln":
                return chartInfo.ln;
            case "scratch":
                return chartInfo.s + chartInfo.ls;
            case "total":
                return chartInfo.total;
            case "tn":
            case "t/n":
                return ChartInfoDisplayFormatter.GetTotalPerNote(chartInfo);
            case "density":
                return chartInfo.density;
            case "peak":
            case "peakdensity":
                return chartInfo.peakdensity;
            case "end":
            case "enddensity":
                return chartInfo.enddensity;
            case "soflan":
                return chartInfo.speedchange_count;
            default:
                return null;
        }
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
            string[] parts = trimmed.Split(new[] { ".." }, StringSplitOptions.None);
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
        string[] operators = { ">=", "<=", ">", "<" };
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
        switch ((term ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "ln":
                return ChartInfoDisplayFormatter.FeatureUndefinedLongNote;
            case "mine":
                return ChartInfoDisplayFormatter.FeatureMineNote;
            case "random":
                return ChartInfoDisplayFormatter.FeatureRandom;
            case "lnmode":
            case "ln(#lnmode)":
                return ChartInfoDisplayFormatter.FeatureLongNote;
            case "cn":
                return ChartInfoDisplayFormatter.FeatureChargeNote;
            case "hcn":
                return ChartInfoDisplayFormatter.FeatureHellChargeNote;
            case "stop":
                return ChartInfoDisplayFormatter.FeatureStopSequence;
            case "scroll":
                return ChartInfoDisplayFormatter.FeatureScroll;
            default:
                return null;
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
