using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>推定の単独比較キーと実在する最頻ペア、および同じペアの表示表記を保持します。</summary>
internal sealed class InstallEstimationMetadataProfile
{
    public static InstallEstimationMetadataProfile Empty { get; } = new InstallEstimationMetadataProfile();

    /// <summary>非空の原題キーの最頻値です。有効値がなければ空です。</summary>
    public string DominantNormalizedTitle { get; set; } = string.Empty;

    /// <summary>非空の作者キーの最頻値です。原題の欠如とは独立に集計します。</summary>
    public string DominantNormalizedArtist { get; set; } = string.Empty;

    /// <summary>両項目が非空の同一レコードから得る実在最頻ペアです。有効ペアがなければ空です。</summary>
    public string DominantNormalizedTitleArtistPair { get; set; } = string.Empty;

    /// <summary>最頻ペア内でパス順・入力順に選んだ同一レコードの、小文字化前の主題です。有効ペアがなければ空です。</summary>
    public string DominantPairDisplayTitle { get; set; } = string.Empty;

    /// <summary>最頻ペアの表示主題と同じレコードから得た、小文字化前の作者です。有効ペアがなければ空です。</summary>
    public string DominantPairDisplayArtist { get; set; } = string.Empty;

    /// <summary>採用した非空原題グループの件数です。有効値がなければ0です。</summary>
    public int TitleSupportCount { get; set; }

    /// <summary>採用した非空作者グループの件数です。有効値がなければ0です。</summary>
    public int ArtistSupportCount { get; set; }

    /// <summary>採用した実在ペアの件数です。有効ペアがなければ0です。</summary>
    public int PairSupportCount { get; set; }

    /// <summary>空値を含む全入力譜面の件数です。</summary>
    public int SourceChartCount { get; set; }

    /// <summary>項目単独またはペアの比較材料の有無です。高確信度に必要な両側の実在ペアを意味しません。</summary>
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

    private static readonly Regex whitespaceRegex = new("\\s+", RegexOptions.Compiled);

    private static readonly string[] artistDiffPrefixes = ["notes", "note", "obj"];

    /// <summary>表示と共通の主題整理を行い、比較用の小文字キーを返します。</summary>
    internal static string NormalizeTitleForTieBreak(string value) => NormalizeTitleDisplay(value).ToLowerInvariant();

    private static string NormalizeTitleDisplay(string value)
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

    /// <summary>表示と共通の作者整理を行い、比較用の小文字キーを返します。</summary>
    internal static string NormalizeArtistForTieBreak(string value) => NormalizeArtistDisplay(value).ToLowerInvariant();

    private static string NormalizeArtistDisplay(string value)
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

    /// <summary>両方が非空の比較キーから実在ペアのキーを作ります。片側でも空ならペアはありません。</summary>
    internal static string ComposePair(string normalizedTitle, string normalizedArtist)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(normalizedArtist))
        {
            return string.Empty;
        }
        return (normalizedTitle ?? string.Empty) + PairSeparator + (normalizedArtist ?? string.Empty);
    }

    /// <summary>原題・作者を独立に非空集計し、両項目が揃う実在ペアと同じレコードの両表示表記を選びます。</summary>
    internal static InstallEstimationMetadataProfile BuildProfile(IEnumerable<(string Title, string Artist, string Path)> records)
    {
        IEnumerable<(string Title, string Artist, string Path)> effectiveRecords = records ?? [];
        List<MetadataRecord> normalizedRecords = [.. effectiveRecords
            .Select((ValueTuple<string, string, string> record) =>
            {
                string displayTitle = NormalizeTitleDisplay(record.Item1);
                string displayArtist = NormalizeArtistDisplay(record.Item2);
                string normalizedTitle = displayTitle.ToLowerInvariant();
                string normalizedArtist = displayArtist.ToLowerInvariant();
                return new MetadataRecord
                {
                    Title = normalizedTitle,
                    Artist = normalizedArtist,
                    Pair = ComposePair(normalizedTitle, normalizedArtist),
                    DisplayTitle = displayTitle,
                    DisplayArtist = displayArtist,
                    Path = record.Item3 ?? string.Empty
                };
            })];

        string dominantPair = SelectDominantValue(normalizedRecords, record => record.Pair, out int pairSupportCount);
        MetadataRecord displayRecord = string.IsNullOrEmpty(dominantPair) ? null : normalizedRecords
            .Where(record => record.Pair == dominantPair)
            .OrderBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
            .First();

        return new InstallEstimationMetadataProfile
        {
            DominantNormalizedTitle = SelectDominantValue(normalizedRecords, record => record.Title, out int titleSupportCount),
            TitleSupportCount = titleSupportCount,
            DominantNormalizedArtist = SelectDominantValue(normalizedRecords, record => record.Artist, out int artistSupportCount),
            ArtistSupportCount = artistSupportCount,
            DominantNormalizedTitleArtistPair = dominantPair,
            DominantPairDisplayTitle = displayRecord?.DisplayTitle ?? string.Empty,
            DominantPairDisplayArtist = displayRecord?.DisplayArtist ?? string.Empty,
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
            .Trim();
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
                if (i + marker.Length > normalized.Length || !normalized.AsSpan(i, marker.Length).Equals(marker.AsSpan(), StringComparison.OrdinalIgnoreCase))
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
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
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

        var bestGroup = records
            .Select((record, index) => (Value: selector(record), record.Path, Index: index))
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .GroupBy(item => item.Value, StringComparer.Ordinal)
            .Select(group => new
            {
                Value = group.Key ?? string.Empty,
                Count = group.Count(),
                FirstPath = group.Select(item => item.Path ?? string.Empty).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty,
                FirstIndex = group.Min(item => item.Index)
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.FirstPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.FirstIndex)
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

        public string DisplayTitle { get; set; } = string.Empty;

        public string DisplayArtist { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }

}
