using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Describes the immutable dynamic portion of a keyword-search catalog.
/// </summary>
internal sealed class KeywordSearchCatalogSnapshot
{
    private readonly IReadOnlyList<string> playlistNames;

    /// <summary>
    /// Captures one immutable dynamic catalog snapshot at a known revision.
    /// </summary>
    /// <param name="revision">The revision assigned by the owning feature.</param>
    /// <param name="playlistNames">The installed playlist names captured for completion.</param>
    internal KeywordSearchCatalogSnapshot(long revision, IEnumerable<string> playlistNames)
    {
        Revision = revision;
        this.playlistNames = new ReadOnlyCollection<string>(
            (playlistNames ?? [])
                .Select(name => (name ?? string.Empty).Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    /// <summary>
    /// Gets the revision associated with the captured catalog.
    /// </summary>
    internal long Revision { get; }

    /// <summary>
    /// Gets the immutable installed-playlist names captured for this catalog.
    /// </summary>
    internal IReadOnlyList<string> PlaylistNames => playlistNames;

    /// <summary>
    /// Creates an empty catalog snapshot.
    /// </summary>
    internal static KeywordSearchCatalogSnapshot Empty { get; } = new(0L, []);

    /// <summary>
    /// Gets values for a field in the captured context.
    /// </summary>
    internal IReadOnlyList<string> GetValues(GridKeywordSearchContext context, string field)
    {
        return KeywordSearchCatalog.GetValues(context, field, playlistNames);
    }

    /// <summary>
    /// Returns whether a field has a finite or injected dynamic value vocabulary.
    /// </summary>
    internal bool IsValueField(GridKeywordSearchContext context, string field)
    {
        return KeywordSearchCatalog.IsValueField(context, field);
    }
}

/// <summary>
/// Owns the explicit keyword-search field value vocabulary.
/// </summary>
internal static class KeywordSearchCatalog
{
    private static readonly IReadOnlyList<string> MainClearValues = ReadOnly(
        "nosong", "NP", "F", "AE", "LAE", "EC", "NC", "HC", "EXH", "FC", "PF", "MAX");

    private static readonly IReadOnlyList<string> PlayHistoryClearValues = ReadOnly(
        "nosong", "NP", "F", "AE", "LAE", "EC", "NC", "HC", "EXH", "FC", "PF", "MAX", "defined", "undefined");

    private static readonly IReadOnlyList<string> RankValues = ReadOnly(
        "F", "E", "D", "C", "B", "A", "AA", "AAA", "MAX", "defined", "undefined");

    private static readonly IReadOnlyList<string> DifficultyValues = ReadOnly(
        "beginner", "normal", "hyper", "another", "insane", "defined", "undefined");

    private static readonly IReadOnlyList<string> JudgeValues = ReadOnly(
        "veryhard", "hard", "normal", "easy", "veryeasy", "defined", "undefined");

    private static readonly IReadOnlyList<string> JudgePercentValues = ReadOnly("defined", "undefined");

    private static readonly IReadOnlyList<string> FeatureValues = ReadOnly(
        "ln", "mine", "random", "lnmode", "cn", "hcn", "stop", "scroll", "defined", "undefined");

    private static readonly IReadOnlyList<string> DefinedValues = ReadOnly("defined", "undefined");

    private static readonly IReadOnlyList<string> BooleanValues = ReadOnly("true", "false");

    private static readonly IReadOnlyList<string> MonthValues = new ReadOnlyCollection<string>(
        Enumerable.Range(1, 12).Select(month => month.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());

    private static readonly IReadOnlyList<string> PlayHistoryTypeValues = ReadOnly("score", "bp", "clear", "combo", "play");

    /// <summary>
    /// Returns the canonical values applicable to one field and context.
    /// </summary>
    internal static IReadOnlyList<string> GetValues(
        GridKeywordSearchContext context,
        string field,
        IEnumerable<string> playlistNames)
    {
        string canonicalField = (field ?? string.Empty).Trim().ToLowerInvariant();
        if (canonicalField.Length == 0)
        {
            return [];
        }

        return canonicalField switch
        {
            "clear" => context == GridKeywordSearchContext.PlayHistory
                ? PlayHistoryClearValues
                : context == GridKeywordSearchContext.PlaylistSummary ? [] : MainClearValues,
            "oldclear" or "newclear" => context == GridKeywordSearchContext.PlayHistory ? PlayHistoryClearValues : [],
            "rank" or "djlevel" or "dj" => context == GridKeywordSearchContext.PlaylistSummary || context == GridKeywordSearchContext.PlayHistory ? [] : RankValues,
            "difficulty" => context == GridKeywordSearchContext.PlaylistSummary || context == GridKeywordSearchContext.PlayHistory ? [] : DifficultyValues,
            "judge" => context == GridKeywordSearchContext.PlaylistSummary || context == GridKeywordSearchContext.PlayHistory ? [] : JudgeValues,
            "judge%" or "judgepct" => context == GridKeywordSearchContext.PlaylistSummary || context == GridKeywordSearchContext.PlayHistory ? [] : JudgePercentValues,
            "feature" => context == GridKeywordSearchContext.PlaylistSummary || context == GridKeywordSearchContext.PlayHistory ? [] : FeatureValues,
            "level" or "mainbpm" or "maxbpm" or "minbpm" or "duration" or "length" or "notes"
                or "long" or "ln" or "scratch" or "total" or "tn" or "t/n" or "density"
                or "peak" or "peakdensity" or "end" or "enddensity" or "soflan"
                => context == GridKeywordSearchContext.PlaylistSummary || context == GridKeywordSearchContext.PlayHistory ? [] : DefinedValues,
            "rate" or "score" or "combo" or "bp"
                => context == GridKeywordSearchContext.PlaylistSummary || context == GridKeywordSearchContext.PlayHistory ? [] : DefinedValues,
            "finalized" => context == GridKeywordSearchContext.PlayHistory ? BooleanValues : [],
            "month" => context == GridKeywordSearchContext.PlayHistory ? MonthValues : [],
            "type" or "kind" => context == GridKeywordSearchContext.PlayHistory ? PlayHistoryTypeValues : [],
            "playlist" or "ref" or "table" => context == GridKeywordSearchContext.PlaylistSummary
                ? []
                : DistinctSorted(playlistNames),
            _ => [],
        };
    }

    /// <summary>
    /// Returns whether a field is eligible for value completion even when a dynamic snapshot is empty.
    /// </summary>
    internal static bool IsValueField(GridKeywordSearchContext context, string field)
    {
        string canonicalField = (field ?? string.Empty).Trim().ToLowerInvariant();
        if (context == GridKeywordSearchContext.PlaylistSummary)
        {
            return false;
        }

        return canonicalField is
            "clear" or "oldclear" or "newclear" or "rank" or "djlevel" or "dj" or "difficulty"
            or "judge" or "judge%" or "judgepct" or "feature" or "level" or "mainbpm" or "maxbpm"
            or "minbpm" or "duration" or "length" or "notes" or "long" or "ln" or "scratch" or "total"
            or "tn" or "t/n" or "density" or "peak" or "peakdensity" or "end" or "enddensity" or "soflan"
            or "rate" or "score" or "combo" or "bp" or "finalized" or "month" or "type" or "kind"
            or "playlist" or "ref" or "table";
    }

    private static IReadOnlyList<string> ReadOnly(params string[] values)
    {
        return new ReadOnlyCollection<string>(values ?? []);
    }

    private static IReadOnlyList<string> DistinctSorted(IEnumerable<string> values)
    {
        return new ReadOnlyCollection<string>(
            (values ?? [])
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }
}
