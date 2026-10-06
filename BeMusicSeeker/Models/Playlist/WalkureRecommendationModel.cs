using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

/// <summary>実観測と推薦目標に使う、難度順の正規化ランプです。</summary>
internal enum WalkureLamp { Failed = 1, Easy, Normal, Hard, FullCombo }

/// <summary>実観測の採用・補充を指定する推薦方針です。</summary>
internal enum WalkureScorePolicy { Standard, UnplayedAsFailed, FailedAsUnplayed }

/// <summary>四つのクリア境界の変更不能な値です。未定義の★を null のまま保持します。</summary>
internal sealed record WalkureLampValues(double? Easy, double? Normal, double? Hard, double? FullCombo)
{
    /// <summary>目標ランプの境界値を返します。</summary>
    internal double? Get(WalkureLamp lamp) => lamp switch
    {
        WalkureLamp.Easy => Easy,
        WalkureLamp.Normal => Normal,
        WalkureLamp.Hard => Hard,
        WalkureLamp.FullCombo => FullCombo,
        _ => throw new ArgumentOutOfRangeException(nameof(lamp))
    };
}

/// <summary>固定上流モデルの一譜面または一段位です。所属行とは独立した一つのキーを持ちます。</summary>
internal sealed record WalkureModelEntry(
    string Md5, string Name, bool IsCourse, double Discrimination,
    WalkureLampValues Difficulty, WalkureLampValues StarRatings,
    ImmutableDictionary<string, string> TableLevels);

/// <summary>実力と、換算済みの二桁の★を保持します。</summary>
internal sealed record WalkurePlayerRating(double Theta, double StarRating);

/// <summary>通常譜面の一つの目標ランプと二桁の達成確率です。</summary>
internal sealed record WalkureRecommendation(WalkureModelEntry Entry, WalkureLamp Lamp, double Percent);

/// <summary>MIT の固定版 Walkure モデルと純粋な GRM 計算を所有します。</summary>
internal sealed class WalkureRecommendationModel
{
    private static readonly Lazy<WalkureRecommendationModel> bundled = new(LoadBundled);
    private readonly ImmutableArray<double> starMapping;

    /// <summary>配布アセンブリに埋め込まれた変更不能なモデルです。欠落は失敗として伝播します。</summary>
    internal static WalkureRecommendationModel Bundled => bundled.Value;

    /// <summary>段位を含む一意のモデルキーの順序付き集合です。</summary>
    internal ImmutableArray<WalkureModelEntry> Entries { get; }

    /// <summary>モデル対象への射影に使う索引です。</summary>
    internal ImmutableDictionary<string, WalkureModelEntry> EntriesByMd5 { get; }

    /// <summary>変更不能な入力からモデルを構成します。</summary>
    internal WalkureRecommendationModel(IEnumerable<WalkureModelEntry> entries, IEnumerable<double> starMapping)
    {
        Entries = [.. entries];
        EntriesByMd5 = Entries.ToImmutableDictionary(entry => entry.Md5, StringComparer.OrdinalIgnoreCase);
        this.starMapping = [.. starMapping];
    }

    private static WalkureRecommendationModel LoadBundled()
    {
        static string Read(string name)
        {
            using Stream stream = typeof(WalkureRecommendationModel).Assembly.GetManifestResourceStream(
                "BeMusicSeeker.Assets.Walkure." + name)
                ?? throw new FileNotFoundException("同梱 Walkure モデルがありません。", name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        static WalkureLampValues Values(JToken token) => new(
            token.Value<double?>("easy"), token.Value<double?>("normal"),
            token.Value<double?>("hard"), token.Value<double?>("fullCombo"));
        return new WalkureRecommendationModel(
            JArray.Parse(Read("recommendation-model-entries.json")).Select(token => new WalkureModelEntry(
                token.Value<string>("md5"), token.Value<string>("name"), token.Value<string>("entryType") == "course",
                token.Value<double>("chartDiscrimination"), Values(token["clearDifficulty"]),
                Values(token["clearDifficultyStarRatings"]),
                ((JObject)token["difficultyTableLevels"]).Properties().ToImmutableDictionary(
                    property => property.Name, property => property.Value.ToString()))),
            JArray.Parse(Read("recommendation-star-rating-mapping.json")).Values<double>());
    }

    /// <summary>原観測に方針を適用します。原入力を変更せず、モデルキーは一度だけ採用します。</summary>
    internal ImmutableDictionary<string, WalkureLamp> ApplyPolicy(
        IReadOnlyDictionary<string, WalkureLamp> scores, WalkureScorePolicy policy)
    {
        ImmutableDictionary<string, WalkureLamp>.Builder result = ImmutableDictionary.CreateBuilder<string, WalkureLamp>(StringComparer.OrdinalIgnoreCase);
        foreach (WalkureModelEntry entry in Entries)
        {
            if (scores.TryGetValue(entry.Md5, out WalkureLamp lamp))
            {
                if (policy != WalkureScorePolicy.FailedAsUnplayed || lamp != WalkureLamp.Failed)
                {
                    result[entry.Md5] = lamp;
                }
            }
            else if (policy == WalkureScorePolicy.UnplayedAsFailed)
            {
                result[entry.Md5] = WalkureLamp.Failed;
            }
        }
        return result.ToImmutable();
    }

    /// <summary>GRM の対数尤度微分を [-20,20] の二分法で解きます。観測なし・推定不能は失敗します。</summary>
    internal WalkurePlayerRating Estimate(
        IReadOnlyDictionary<string, WalkureLamp> scores, CancellationToken cancellationToken = default)
    {
        (WalkureModelEntry Entry, WalkureLamp Lamp)[] observations = Entries.Where(entry => scores.ContainsKey(entry.Md5))
            .Select(entry => (Entry: entry, Lamp: scores[entry.Md5])).ToArray();
        if (observations.Length == 0)
        {
            throw new InvalidOperationException(Properties.Resources.Error_RecommendationNoObservations);
        }

        double Derivative(double theta)
        {
            double total = 0;
            foreach ((WalkureModelEntry entry, WalkureLamp lamp) in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double lower = lamp == WalkureLamp.Failed ? double.NegativeInfinity : entry.Difficulty.Get(lamp).Value;
                double upper = lamp == WalkureLamp.FullCombo ? double.PositiveInfinity : entry.Difficulty.Get(lamp + 1).Value;
                double lowerProbability = Sigmoid(entry.Discrimination * (theta - lower));
                double upperProbability = Sigmoid(entry.Discrimination * (theta - upper));
                total += (entry.Discrimination * lowerProbability * (1 - lowerProbability)
                    - entry.Discrimination * upperProbability * (1 - upperProbability)) / (lowerProbability - upperProbability);
            }
            return total;
        }
        double lowerBound = -20, upperBound = 20;
        if (Derivative(upperBound) > 0 || Derivative(lowerBound) < 0)
        {
            throw new InvalidOperationException(Properties.Resources.Error_RecommendationRatingFailed);
        }

        while (upperBound - lowerBound > 1e-6)
        {
            double midpoint = (lowerBound + upperBound) / 2;
            double derivative = Derivative(midpoint);
            if (derivative > 0)
            {
                lowerBound = midpoint;
            }
            else
            {
                upperBound = midpoint;
            }
        }
        double starRating = RoundTwo(ConvertStarRating(upperBound));
        if (!double.IsFinite(upperBound) || upperBound < -20 || upperBound > 20 || !double.IsFinite(starRating))
        {
            throw new InvalidOperationException(Properties.Resources.Error_RecommendationRatingFailed);
        }
        return new WalkurePlayerRating(upperBound, starRating);
    }

    /// <summary>固定換算点で区分線形補間し、端の区間で外挿します。</summary>
    internal double ConvertStarRating(double theta)
    {
        int left = 0;
        if (theta >= starMapping[^1])
        {
            left = starMapping.Length - 2;
        }
        else if (theta > starMapping[0])
        {
            for (int index = 0; index < starMapping.Length - 1; index++)
            {
                if (theta <= starMapping[index + 1]) { left = index; break; }
            }
        }

        return left + 1 + (theta - starMapping[left]) / (starMapping[left + 1] - starMapping[left]);
    }

    /// <summary>保存★が未定義なら 0、それ以外は原難度から達成確率を計算します。</summary>
    internal static double ClearProbability(WalkureModelEntry entry, WalkureLamp lamp, double theta)
        => entry.StarRatings.Get(lamp) == null ? 0 : Sigmoid(entry.Discrimination * (theta - entry.Difficulty.Get(lamp).Value));

    /// <summary>現在より上位で未丸め確率 20%以上の通常譜面を、確率降順で生成します。</summary>
    internal ImmutableArray<WalkureRecommendation> Recommend(
        IReadOnlyDictionary<string, WalkureLamp> scores, double theta, CancellationToken cancellationToken = default)
    {
        var result = new List<WalkureRecommendation>();
        foreach (WalkureModelEntry entry in Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsCourse)
            {
                continue;
            }

            scores.TryGetValue(entry.Md5, out WalkureLamp current);
            for (WalkureLamp lamp = WalkureLamp.Easy; lamp <= WalkureLamp.FullCombo; lamp++)
            {
                if (lamp <= current)
                {
                    continue;
                }

                double probability = ClearProbability(entry, lamp, theta);
                if (probability >= .2)
                {
                    result.Add(new WalkureRecommendation(entry, lamp, RoundTwo(probability * 100)));
                }
            }
        }
        return [.. result.OrderByDescending(item => item.Percent)];
    }

    private static double Sigmoid(double value) => 1 / (1 + Math.Exp(-value));

    // F2 は二進浮動小数の実値を二桁へ丸める。二桁の正確な中点は奇数/8だけなので、
    // その場合だけ上流 toFixed(2) の絶対値が大きい側へ揃える。2.675等を中点扱いしない。
    private static double RoundTwo(double value)
    {
        double eighths = value * 8;
        return double.IsFinite(eighths) && eighths == Math.Truncate(eighths) && Math.Abs(eighths % 2) == 1
            ? Math.Round(value, 2, MidpointRounding.AwayFromZero)
            : double.Parse(value.ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}
