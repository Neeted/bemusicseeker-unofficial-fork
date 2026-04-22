using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallEstimationMetadataProfile
{
    public static InstallEstimationMetadataProfile Empty { get; } = new InstallEstimationMetadataProfile();

    public string DominantNormalizedTitle { get; set; } = string.Empty;

    public string DominantNormalizedArtist { get; set; } = string.Empty;

    public string DominantNormalizedTitleArtistPair { get; set; } = string.Empty;

    public int TitleSupportCount { get; set; }

    public int ArtistSupportCount { get; set; }

    public int PairSupportCount { get; set; }

    public int SourceChartCount { get; set; }

    public bool HasAnySignal => !string.IsNullOrWhiteSpace(DominantNormalizedTitle)
        || !string.IsNullOrWhiteSpace(DominantNormalizedArtist)
        || !string.IsNullOrWhiteSpace(DominantNormalizedTitleArtistPair);
}

internal enum InstallEstimationMetadataTitleMatchKind
{
    None,
    Weak,
    FuzzyStrong,
    Exact
}

internal static class InstallEstimationMetadataNormalizer
{
    private const char PairSeparator = '\u001f';
    internal const double TitleFuzzyStrongThreshold = 0.88;
    internal const double TitleWeakThreshold = 0.72;

    private static readonly Regex whitespaceRegex = new Regex("\\s+", RegexOptions.Compiled);

    private static readonly string[] artistDiffPrefixes = { "notes", "note", "obj" };

    internal static string NormalizeTitleForTieBreak(string value)
    {
        string normalized = NormalizeCommon(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        int delimiterIndex = FindTitleDelimiterIndex(normalized);
        if (delimiterIndex > 0)
        {
            normalized = normalized.Substring(0, delimiterIndex).Trim();
        }

        return NormalizeCommon(normalized);
    }

    internal static string NormalizeArtistForTieBreak(string value)
    {
        string normalized = NormalizeCommon(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        int diffMarkerIndex = FindArtistDiffMarkerIndex(normalized);
        if (diffMarkerIndex >= 0)
        {
            normalized = normalized.Substring(0, diffMarkerIndex).TrimEnd(' ', '.', ';', ':');
        }

        int slashIndex = normalized.IndexOf('/');
        if (slashIndex > 0)
        {
            normalized = normalized.Substring(0, slashIndex).Trim();
        }

        return NormalizeCommon(normalized);
    }

    internal static double ComputeTitleSimilarity(string leftValue, string rightValue)
    {
        string left = NormalizeTitleForTieBreak(leftValue);
        string right = NormalizeTitleForTieBreak(rightValue);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0d;
        }
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        Dictionary<string, int> leftBigrams = BuildBigramCounts(left);
        Dictionary<string, int> rightBigrams = BuildBigramCounts(right);
        int leftCount = leftBigrams.Values.Sum();
        int rightCount = rightBigrams.Values.Sum();
        if (leftCount == 0 || rightCount == 0)
        {
            return 0d;
        }

        int overlapCount = 0;
        foreach (KeyValuePair<string, int> pair in leftBigrams)
        {
            if (rightBigrams.TryGetValue(pair.Key, out int rightOccurrences))
            {
                overlapCount += Math.Min(pair.Value, rightOccurrences);
            }
        }
        return (2d * overlapCount) / (leftCount + rightCount);
    }

    internal static InstallEstimationMetadataTitleMatchKind ClassifyTitleMatch(string leftValue, string rightValue)
    {
        string left = NormalizeTitleForTieBreak(leftValue);
        string right = NormalizeTitleForTieBreak(rightValue);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return InstallEstimationMetadataTitleMatchKind.None;
        }
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return InstallEstimationMetadataTitleMatchKind.Exact;
        }

        double similarity = ComputeTitleSimilarity(left, right);
        if (similarity >= TitleFuzzyStrongThreshold)
        {
            return InstallEstimationMetadataTitleMatchKind.FuzzyStrong;
        }
        if (similarity >= TitleWeakThreshold)
        {
            return InstallEstimationMetadataTitleMatchKind.Weak;
        }
        return InstallEstimationMetadataTitleMatchKind.None;
    }

    internal static string ComposePair(string normalizedTitle, string normalizedArtist)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) && string.IsNullOrWhiteSpace(normalizedArtist))
        {
            return string.Empty;
        }
        return (normalizedTitle ?? string.Empty) + PairSeparator + (normalizedArtist ?? string.Empty);
    }

    internal static InstallEstimationMetadataProfile BuildProfile(IEnumerable<(string Title, string Artist, string Path)> records)
    {
        IEnumerable<(string Title, string Artist, string Path)> effectiveRecords = records ?? Enumerable.Empty<(string Title, string Artist, string Path)>();
        List<MetadataRecord> normalizedRecords = effectiveRecords
            .Select((ValueTuple<string, string, string> record) =>
            {
                string normalizedTitle = NormalizeTitleForTieBreak(record.Item1);
                string normalizedArtist = NormalizeArtistForTieBreak(record.Item2);
                return new MetadataRecord
                {
                    Title = normalizedTitle,
                    Artist = normalizedArtist,
                    Pair = ComposePair(normalizedTitle, normalizedArtist),
                    Path = record.Item3 ?? string.Empty
                };
            })
            .ToList();

        return new InstallEstimationMetadataProfile
        {
            DominantNormalizedTitle = SelectDominantValue(normalizedRecords, (MetadataRecord record) => record.Title, out int titleSupportCount),
            TitleSupportCount = titleSupportCount,
            DominantNormalizedArtist = SelectDominantValue(normalizedRecords, (MetadataRecord record) => record.Artist, out int artistSupportCount),
            ArtistSupportCount = artistSupportCount,
            DominantNormalizedTitleArtistPair = SelectDominantValue(normalizedRecords, (MetadataRecord record) => record.Pair, out int pairSupportCount),
            PairSupportCount = pairSupportCount,
            SourceChartCount = normalizedRecords.Count
        };
    }

    private static string NormalizeCommon(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Normalize(NormalizationForm.FormKC)
            .Replace('\u3000', ' ')
            .Trim()
            .ToLowerInvariant();
        return whitespaceRegex.Replace(normalized, " ").Trim();
    }

    private static int FindTitleDelimiterIndex(string normalized)
    {
        for (int i = 1; i < normalized.Length; i++)
        {
            char current = normalized[i];
            if (current == '(' || current == '[' || current == '~')
            {
                return i;
            }
            if (current == '-' && normalized[i - 1] == ' ')
            {
                return i - 1;
            }
        }
        return -1;
    }

    private static int FindArtistDiffMarkerIndex(string normalized)
    {
        for (int i = 0; i < normalized.Length; i++)
        {
            foreach (string marker in artistDiffPrefixes)
            {
                if (i + marker.Length > normalized.Length || !normalized.AsSpan(i, marker.Length).SequenceEqual(marker.AsSpan()))
                {
                    continue;
                }
                if (i > 0 && char.IsLetterOrDigit(normalized[i - 1]))
                {
                    continue;
                }

                int separatorIndex = i + marker.Length;
                while (separatorIndex < normalized.Length && normalized[separatorIndex] == ' ')
                {
                    separatorIndex++;
                }
                if (separatorIndex >= normalized.Length)
                {
                    continue;
                }
                if (!IsArtistDiffSeparator(normalized[separatorIndex]))
                {
                    continue;
                }

                return i;
            }
        }
        return -1;
    }

    private static bool IsArtistDiffSeparator(char value)
    {
        return value == '.' || value == ';' || value == ':';
    }

    private static Dictionary<string, int> BuildBigramCounts(string value)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return counts;
        }

        if (value.Length < 2)
        {
            counts[value] = 1;
            return counts;
        }

        for (int i = 0; i < value.Length - 1; i++)
        {
            string bigram = value.Substring(i, 2);
            counts[bigram] = counts.TryGetValue(bigram, out int currentCount) ? currentCount + 1 : 1;
        }
        return counts;
    }

    private static string SelectDominantValue(IReadOnlyList<MetadataRecord> records, Func<MetadataRecord, string> selector, out int supportCount)
    {
        supportCount = 0;
        if (records == null || records.Count == 0)
        {
            return string.Empty;
        }

        DominantGroup bestGroup = records
            .Select((MetadataRecord record, int index) => new DominantGroupItem
            {
                Value = selector(record) ?? string.Empty,
                Path = record.Path ?? string.Empty,
                Index = index
            })
            .GroupBy((DominantGroupItem item) => item.Value, StringComparer.Ordinal)
            .Select((IGrouping<string, DominantGroupItem> group) => new DominantGroup
            {
                Value = group.Key ?? string.Empty,
                Count = group.Count(),
                HasNonEmptyValue = !string.IsNullOrWhiteSpace(group.Key),
                FirstPath = group.Select((DominantGroupItem item) => item.Path ?? string.Empty).OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty,
                FirstIndex = group.Min((DominantGroupItem item) => item.Index)
            })
            .OrderByDescending((DominantGroup group) => group.Count)
            .ThenByDescending((DominantGroup group) => group.HasNonEmptyValue)
            .ThenBy((DominantGroup group) => group.FirstPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy((DominantGroup group) => group.FirstIndex)
            .FirstOrDefault();

        if (bestGroup == null)
        {
            return string.Empty;
        }

        supportCount = bestGroup.Count;
        return bestGroup.Value ?? string.Empty;
    }

    private sealed class MetadataRecord
    {
        public string Title { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        public string Pair { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }

    private sealed class DominantGroupItem
    {
        public string Value { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public int Index { get; set; }
    }

    private sealed class DominantGroup
    {
        public string Value { get; set; } = string.Empty;

        public int Count { get; set; }

        public bool HasNonEmptyValue { get; set; }

        public string FirstPath { get; set; } = string.Empty;

        public int FirstIndex { get; set; }
    }
}
