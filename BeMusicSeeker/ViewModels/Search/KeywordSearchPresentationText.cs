using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal static class KeywordSearchPresentationText
{
    internal static string BuildWarningText(string keywordFilter, GridKeywordSearchContext context)
        => BuildWarningText(GridKeywordSearchQuery.Parse(keywordFilter), context);

    /// <summary>照合に使う解析済みの式から、現在の言語の警告を組み立てます。</summary>
    internal static string BuildWarningText(GridKeywordSearchQuery query, GridKeywordSearchContext context)
    {
        IReadOnlyList<GridKeywordSearchDiagnostic> diagnostics = query.GetDiagnostics(context);
        if (diagnostics.Count == 0)
        {
            return string.Empty;
        }
        return string.Join(
            Environment.NewLine,
            diagnostics
                .Select(FormatDiagnostic)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal));
    }

    internal static string BuildHelpText(GridKeywordSearchContext context)
    {
        string fields = context switch
        {
            GridKeywordSearchContext.PlaylistDetail => BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_playlist_detail,
            GridKeywordSearchContext.PlaylistSummary => BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_playlist_summary,
            GridKeywordSearchContext.PlayHistory => BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_play_history,
            _ => BeMusicSeeker.Properties.Resources.Keyword_search_help_fields_bmsfile,
        };
        return string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_help_template, fields);
    }

    private static string FormatDiagnostic(GridKeywordSearchDiagnostic diagnostic)
    {
        return diagnostic.Kind switch
        {
            GridKeywordSearchDiagnosticKind.UnknownField => string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_unknown_field, diagnostic.Value),
            GridKeywordSearchDiagnosticKind.EmptyFieldTerm => string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_field_term, diagnostic.Value),
            GridKeywordSearchDiagnosticKind.EmptyNegation => BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_negation,
            GridKeywordSearchDiagnosticKind.EmptyOr => BeMusicSeeker.Properties.Resources.Keyword_search_warning_empty_or,
            GridKeywordSearchDiagnosticKind.InvalidRegex => string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_invalid_regex, diagnostic.Value),
            GridKeywordSearchDiagnosticKind.InvalidDate => string.Format(BeMusicSeeker.Properties.Resources.Keyword_search_warning_invalid_date, diagnostic.Value),
            _ => string.Empty,
        };
    }
}
