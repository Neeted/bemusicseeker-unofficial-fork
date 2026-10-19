using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>同一譜面由来の実在最頻ペアと、その小文字化前の表示表記を保持します。</summary>
internal sealed class InstallEstimationMetadataProfile
{
    /// <summary>非空TITLEの実在ペアを持たないプロファイルです。</summary>
    public static InstallEstimationMetadataProfile Empty { get; } = new InstallEstimationMetadataProfile();

    /// <summary>採用した実在最頻ペアのTITLEキーです。有効ペアがなければ空です。</summary>
    public string DominantNormalizedTitle { get; init; } = string.Empty;

    /// <summary>採用ペアのARTISTキーです。空文字も有効であり、空文字同士だけ一致します。</summary>
    public string DominantNormalizedArtist { get; init; } = string.Empty;

    /// <summary>採用ペア内でパス順・入力順に選んだ同一レコードの、小文字化前の照合TITLEです。</summary>
    public string DominantPairDisplayTitle { get; init; } = string.Empty;

    /// <summary>表示TITLEと同じレコードから得た、小文字化前の照合ARTISTです。</summary>
    public string DominantPairDisplayArtist { get; init; } = string.Empty;

    /// <summary>正規化キーが一意な実在ペアです。表示は同じレコードの表記を保持します。</summary>
    public IReadOnlyList<InstallEstimationMetadataPair> Pairs { get; init; } = [];

    /// <summary>比較に使える実在ペアの有無です。ARTISTの非空は要求しません。</summary>
    public bool HasPair => DominantNormalizedTitle.Length > 0;
}

/// <summary>候補と対象の関連性です。Unavailableは有効な実在ペアがない場合です。</summary>
internal enum InstallEstimationMetadataRelation
{
    /// <summary>対象または候補に有効な非空TITLEの実在ペアがありません。</summary>
    Unavailable,
    /// <summary>比較材料はありますが、関連性の条件を満たしません。</summary>
    Unrelated,
    /// <summary>候補の実在ペアでTITLEまたはARTISTのDiceが0.50以上です。</summary>
    Related,
    /// <summary>両最頻ペアのARTISTが同値で、非空TITLEのDiceが0.72以上です。</summary>
    Strong
}

/// <summary>比較キーと同一レコードの表示根拠を保持する不変の実在ペアです。</summary>
internal sealed record InstallEstimationMetadataPair(string Title, string Artist, string DisplayTitle, string DisplayArtist, string Path, int InputOrder);

/// <summary>分類と表示根拠は同じ比較結果から取得します。</summary>
internal sealed record InstallEstimationMetadataMatch(InstallEstimationMetadataRelation Relation, string DisplayTitle, string DisplayArtist, double TitleSimilarity, double ArtistSimilarity);

/// <summary>対象の2-gramと文字列ごとの比較結果を一操作で再利用します。対象依存の結果は永続化しません。</summary>
internal sealed class InstallEstimationMetadataMatcher
{
    private readonly InstallEstimationMetadataProfile target;
    private readonly Dictionary<string, double> titleSimilarities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> artistSimilarities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> titleGrams;
    private readonly Dictionary<string, int> artistGrams;

    internal InstallEstimationMetadataMatcher(InstallEstimationMetadataProfile target)
    {
        this.target = target ?? InstallEstimationMetadataProfile.Empty;
        titleGrams = InstallEstimationMetadataNormalizer.BuildBigramCounts(this.target.DominantNormalizedTitle);
        artistGrams = InstallEstimationMetadataNormalizer.BuildBigramCounts(this.target.DominantNormalizedArtist);
    }

    /// <summary>実際に評価したペア数です。操作内の診断にだけ使います。</summary>
    internal int PairComparisonCount { get; private set; }

    /// <summary>最頻ペアだけでStrongを判定し、その後に全ユニーク実在ペアのRelatedと表示根拠を求めます。</summary>
    internal InstallEstimationMetadataMatch Evaluate(InstallEstimationMetadataProfile candidate)
    {
        candidate ??= InstallEstimationMetadataProfile.Empty;
        var dominant = new InstallEstimationMetadataPair(candidate.DominantNormalizedTitle, candidate.DominantNormalizedArtist,
            candidate.DominantPairDisplayTitle, candidate.DominantPairDisplayArtist, string.Empty, 0);
        if (!target.HasPair || !candidate.HasPair)
        {
            return Match(InstallEstimationMetadataRelation.Unavailable, dominant, 0, 0);
        }
        PairComparisonCount++;
        double title = Similarity(dominant.Title, true);
        double artist = Similarity(dominant.Artist, false);
        if (title >= 0.72 && string.Equals(target.DominantNormalizedArtist, dominant.Artist, StringComparison.Ordinal))
        {
            return Match(InstallEstimationMetadataRelation.Strong, dominant, title, artist);
        }
        InstallEstimationMetadataPair best = null;
        double bestTitle = 0, bestArtist = 0;
        foreach (InstallEstimationMetadataPair pair in candidate.Pairs.Count > 0 ? candidate.Pairs : new[] { dominant })
        {
            PairComparisonCount++;
            double t = Similarity(pair.Title, true), a = Similarity(pair.Artist, false);
            if (t < 0.50 && a < 0.50) { continue; }
            int compare = Math.Max(t, a).CompareTo(Math.Max(bestTitle, bestArtist));
            if (compare == 0) { compare = t.CompareTo(bestTitle); }
            if (compare == 0) { compare = a.CompareTo(bestArtist); }
            if (compare == 0 && best != null) { compare = -StringComparer.OrdinalIgnoreCase.Compare(pair.Path, best.Path); }
            if (compare == 0 && best != null) { compare = best.InputOrder.CompareTo(pair.InputOrder); }
            if (best == null || compare > 0) { best = pair; bestTitle = t; bestArtist = a; }
        }
        return best == null ? Match(InstallEstimationMetadataRelation.Unrelated, dominant, title, artist)
            : Match(InstallEstimationMetadataRelation.Related, best, bestTitle, bestArtist);
    }

    private static InstallEstimationMetadataMatch Match(InstallEstimationMetadataRelation relation, InstallEstimationMetadataPair pair, double title, double artist)
        => new(relation, pair.DisplayTitle, pair.DisplayArtist, title, artist);

    private double Similarity(string value, bool isTitle)
    {
        Dictionary<string, double> cache = isTitle ? titleSimilarities : artistSimilarities;
        if (!cache.TryGetValue(value, out double similarity))
        {
            similarity = InstallEstimationMetadataNormalizer.ComputeSimilarity(isTitle ? target.DominantNormalizedTitle : target.DominantNormalizedArtist,
                value, isTitle ? titleGrams : artistGrams);
            cache.Add(value, similarity);
        }
        return similarity;
    }
}

/// <summary>導入先照合の入力を一度だけ正規化し、実在ペアの集計と唯一の強一致判定を提供します。</summary>
internal static class InstallEstimationMetadataNormalizer
{
    private const string ArtistGeneralCreditNames = "featuring|feat|pmsobj|reobj|obj|odj|bga|bgi|movie|mov|illustration|illust|vocals?|差分|譜面";
    private const string ArtistShortCreditNames = "ft|vo";
    private const string ArtistCommonCreditNames = "notes?|object(?:ed)?|bg|bms|charts?|graphics?|sliced?|guitars?|pms|patterns?|sequence|lyrics?|image|artwork|visual";
    private const string ArtistMusicCreditNames = "(?:re)?mix(?:ed)?|rmx|arr(?:ange(?:d|ment)?)?|cover(?:ed)?|compose(?:d|r)?";
    private const string ArtistExplicitMusicCreditNames = "music|midi|作曲|編曲";
    private const string ArtistRoleName = $"(?:(?<music>{ArtistMusicCreditNames}|{ArtistExplicitMusicCreditNames})|{ArtistGeneralCreditNames}|{ArtistShortCreditNames}|{ArtistCommonCreditNames})";
    private const string ArtistRoleLeftBoundary = @"(?<![a-z0-9_-])-?";

    private static readonly Regex whitespaceRegex = new("\\s+", RegexOptions.Compiled);

    // 直接つながる役割を先に一括認識し、制作担当を含む兼任と純副役割を区別する。
    // スラッシュでの兼任は語への直結だけとし、Frontier Lyric / objの名義を削らない。
    // 短語・一般名詞には記号またはbyを要求し、名義中のmusic等で保持を再開しない。
    // 導入記号とbyも消費し、Remix / 作者のスラッシュを別の区切りとして扱わない。
    private static readonly Regex artistRoleRegex = new(
        $@"{ArtistRoleLeftBoundary}(?:{ArtistRoleName}(?:(?:\s*[&+]\s*|/){ArtistRoleName})+(?=$|[\s.:;,=/])|(?<music>{ArtistMusicCreditNames})(?=$|[\s.:;,=/])|(?<music>{ArtistExplicitMusicCreditNames})(?=\s*[.:;,=/]|\s+by(?=$|[\s.:;,=/]))|(?:{ArtistGeneralCreditNames})(?=[\s.:;,=/])|(?:{ArtistShortCreditNames})(?=\s*[.:;,=]|/(?![\s/]))|(?:{ArtistCommonCreditNames})(?=\s*[.:;,=]|/(?![\s/])|\s+by(?=$|[\s.:;,=/])))[\s.:;,=/]*(?:by(?=$|[\s.:;,=/])[\s.:;,=/]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>表示と共通の主題整理を行い、比較用の小文字キーを返します。</summary>
    internal static string NormalizeTitleForTieBreak(string value) => NormalizeTitleDisplay(value).ToLowerInvariant();

    private static string NormalizeTitleDisplay(string value)
    {
        string normalized = NormalizeCommon(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        int cutIndex = FindTitleSuffixGroupStart(normalized);
        if (cutIndex > 0)
        {
            // 切断対象の最も右のMIXまで残す。正規化済みキーへ再適用すると主題を再切断するため、生TITLEで一度だけ行う。
            int mixIndex = normalized.LastIndexOf("mix", StringComparison.OrdinalIgnoreCase);
            normalized = normalized[..(mixIndex >= cutIndex ? mixIndex + 3 : cutIndex)].TrimEnd();
        }
        return normalized;
    }

    /// <summary>副役割の区間を除き、主名義と明示された音楽制作担当から比較用の小文字キーを作ります。</summary>
    internal static string NormalizeArtistForTieBreak(string value) => NormalizeArtistDisplay(value).ToLowerInvariant();

    private static string NormalizeArtistDisplay(string value)
    {
        string normalized = NormalizeCommon(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return RemoveArtistSecondaryCredits(normalized);
    }

    /// <summary>正規化済みTITLE同士を、UTF-16の重複2-gram付きDice係数で比較します。切断・小文字化は再実行しません。</summary>
    internal static double ComputeTitleSimilarity(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0d;
        }
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1d;
        }

        return ComputeSimilarity(left, right, BuildBigramCounts(left));
    }

    /// <summary>捕捉済みの左辺2-gramを使い、正規化済み文字列のDiceを再利用可能にします。</summary>
    internal static double ComputeSimilarity(string left, string right, Dictionary<string, int> leftBigrams)
    {
        if (left.Length == 0 || right.Length == 0) { return 0d; }
        if (string.Equals(left, right, StringComparison.Ordinal)) { return 1d; }
        Dictionary<string, int> rightBigrams = BuildBigramCounts(right);
        int overlap = 0;
        foreach (KeyValuePair<string, int> pair in leftBigrams)
        {
            if (rightBigrams.TryGetValue(pair.Key, out int count)) { overlap += Math.Min(pair.Value, count); }
        }
        return 2d * overlap / (leftBigrams.Values.Sum() + rightBigrams.Values.Sum());
    }

    /// <summary>両側の非空TITLE、空許容ARTISTの同値、TITLEのDice係数0.72以上を唯一の強一致条件とします。</summary>
    internal static bool IsStrongMatch(InstallEstimationMetadataProfile target, InstallEstimationMetadataProfile candidate)
    {
        return new InstallEstimationMetadataMatcher(target).Evaluate(candidate).Relation == InstallEstimationMetadataRelation.Strong;
    }

    /// <summary>非空TITLEと空許容ARTISTの実在ペアを集計し、最多・パス順・入力順で同一レコードの比較キーと表示を選びます。</summary>
    internal static InstallEstimationMetadataProfile BuildProfile(IEnumerable<(string Title, string Artist, string Path)> records)
    {
        var groups = (records ?? [])
            .Select((record, index) => new
            {
                Title = NormalizeTitleDisplay(record.Title),
                Artist = NormalizeArtistDisplay(record.Artist),
                Path = record.Path ?? string.Empty,
                Index = index
            })
            .Where(record => record.Title.Length > 0)
            .GroupBy(record => (Title: record.Title.ToLowerInvariant(), Artist: record.Artist.ToLowerInvariant()))
            .Select(group => new
            {
                group.Key,
                Count = group.Count(),
                DisplayRecord = group.OrderBy(record => record.Path, StringComparer.OrdinalIgnoreCase).ThenBy(record => record.Index).First(),
                FirstIndex = group.Min(record => record.Index)
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.DisplayRecord.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.FirstIndex)
            .ToArray();
        var dominantGroup = groups.FirstOrDefault();
        return dominantGroup == null ? InstallEstimationMetadataProfile.Empty : new InstallEstimationMetadataProfile
        {
            DominantNormalizedTitle = dominantGroup.Key.Title,
            DominantNormalizedArtist = dominantGroup.Key.Artist,
            DominantPairDisplayTitle = dominantGroup.DisplayRecord.Title,
            DominantPairDisplayArtist = dominantGroup.DisplayRecord.Artist,
            Pairs = Array.AsReadOnly(groups.Select(group => new InstallEstimationMetadataPair(group.Key.Title, group.Key.Artist,
                group.DisplayRecord.Title, group.DisplayRecord.Artist, group.DisplayRecord.Path, group.DisplayRecord.Index)).ToArray())
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

    private static int FindTitleSuffixGroupStart(string title)
    {
        var blocks = new List<(int Start, int End)>();
        for (int index = 0; index < title.Length; index++)
        {
            char current = title[index];
            char close = GetClosingDelimiter(current);
            bool symmetric = current is '~' or '〜' || current == '-' && index > 0 && title[index - 1] == ' ';
            if (close != '\0')
            {
                int start = index;
                var closingDelimiters = new List<char> { close };
                while (++index < title.Length && closingDelimiters.Count > 0)
                {
                    char nestedClose = GetClosingDelimiter(title[index]);
                    if (nestedClose != '\0')
                    {
                        closingDelimiters.Add(nestedClose);
                    }
                    else
                    {
                        // 主題の括弧が交差していても修復せず対応分だけ閉じ、後続差分を主題へ取り込まない。
                        int closingIndex = closingDelimiters.LastIndexOf(title[index]);
                        if (closingIndex >= 0)
                        {
                            closingDelimiters.RemoveAt(closingIndex);
                            if (closingDelimiters.Count == 0)
                            {
                                break;
                            }
                        }
                    }
                }
                blocks.Add((start, Math.Min(index + 1, title.Length)));
            }
            else if (symmetric)
            {
                int start = index;
                int end = title.IndexOf(current, index + 1);
                // ハイフン囲みの途中にあるJ-Trance等を閉じ側にせず、後続の囲みとの隣接関係を保つ。
                while (current == '-' && end >= 0 && end + 1 < title.Length && char.IsLetterOrDigit(title[end + 1]))
                {
                    end = title.IndexOf(current, end + 1);
                }
                index = end < 0 ? title.Length : end;
                blocks.Add((start, Math.Min(index + 1, title.Length)));
            }
            else if (current is ')' or ']' or '>' or '】' or '》')
            {
                // 片側区切りも末尾群の入口になる。左の主題全体の括弧整合性は要求しない。
                blocks.Add((index, index + 1));
            }
        }
        if (blocks.Count == 0)
        {
            return -1;
        }
        int groupIndex = blocks.Count - 1;
        while (groupIndex > 0 && blocks[groupIndex - 1].Start > 0
            && string.IsNullOrWhiteSpace(title[blocks[groupIndex - 1].End..blocks[groupIndex].Start]))
        {
            groupIndex--;
        }
        return blocks[groupIndex].Start;
    }

    private static char GetClosingDelimiter(char value) => value switch
    {
        '(' => ')',
        '[' => ']',
        '<' => '>',
        '【' => '】',
        '《' => '》',
        _ => '\0'
    };

    private static string RemoveArtistSecondaryCredits(string artist)
    {
        var result = new StringBuilder();
        var closingDelimiters = new List<char>();
        Match role = artistRoleRegex.Match(artist);
        int segmentStart = 0;
        bool keepSegment = true;
        for (int index = 0; index < artist.Length; index++)
        {
            if (role.Success && role.Index == index)
            {
                if (keepSegment)
                {
                    AppendArtistSegment(result, artist, segmentStart, index);
                }
                // 制作役割でも区間を分け、先行する副役割や括弧の有無によらず同じキーを作る。
                keepSegment = role.Groups["music"].Success;
                segmentStart = index;
                index += role.Length - 1;
                role = role.NextMatch();
                continue;
            }

            bool isSlash = artist[index] == '/' && index > 0 && closingDelimiters.Count == 0
                && (artist[index - 1] is ' ' or '/' || index + 1 < artist.Length && artist[index + 1] is ' ' or '/');
            if (isSlash)
            {
                if (keepSegment)
                {
                    AppendArtistSegment(result, artist, segmentStart, index);
                }
                keepSegment = false;
            }

            char close = GetClosingDelimiter(artist[index]);
            if (close != '\0')
            {
                closingDelimiters.Add(close);
            }
            else if (artist[index] is ')' or ']' or '>' or '】' or '》')
            {
                int openingIndex = closingDelimiters.LastIndexOf(artist[index]);
                if (openingIndex >= 0)
                {
                    closingDelimiters.RemoveAt(openingIndex);
                }
            }
        }
        if (keepSegment)
        {
            AppendArtistSegment(result, artist, segmentStart, artist.Length);
        }
        return result.ToString();
    }

    private static void AppendArtistSegment(StringBuilder result, string artist, int start, int end)
    {
        end = TrimArtistCreditSeparators(artist, start, end);
        if (end <= start)
        {
            return;
        }
        if (result.Length > 0)
        {
            result.Append(' ');
        }
        result.Append(artist, start, end - start);
    }

    private static int TrimArtistCreditSeparators(string artist, int start, int end)
    {
        while (end > start)
        {
            int previousEnd = end;
            // 全体保持時も同じ末尾整理を行い、役割除去で閉じ括弧が失われた区間と揃える。
            while (end > start && (artist[end - 1] is ' ' or '/' or ')' or ']' or '>' or '】' or '》'
                || GetClosingDelimiter(artist[end - 1]) != '\0'))
            {
                end--;
            }

            int separatorStart = end;
            while (separatorStart > start && artist[separatorStart - 1] is '.' or ':' or ';' or ',' or '=' or '/' or '|' or '&' or '+' or '-')
            {
                separatorStart--;
            }
            // SKY.の末尾など名義につながった句読点を保ち、空白区切りの独立した記号だけを取り除く。
            if (separatorStart < end && (separatorStart == start || artist[separatorStart - 1] == ' '))
            {
                end = separatorStart;
            }
            if (end == previousEnd)
            {
                break;
            }
        }
        return end;
    }

    /// <summary>UTF-16コード単位の重複2-gramを比較用に集計します。</summary>
    internal static Dictionary<string, int> BuildBigramCounts(string value)
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

}
