using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WalkureRecommendationMathTests
{
    [TestMethod]
    public void BundledModel_MatchesFixedUpstreamPlayerReport()
    {
        WalkureRecommendationModel model = WalkureRecommendationModel.Bundled;
        ImmutableDictionary<string, WalkureLamp> scores = ReadScores(JArray.Parse(Read("legacy-lr2-observations.json")));
        WalkurePlayerRating rating = model.Estimate(model.ApplyPolicy(scores, WalkureScorePolicy.Standard));
        var report = JObject.Parse(Read("legacy-player-report.json"));
        Assert.AreEqual(report["rating"]?.Value<double>("theta") ?? throw new FormatException(), rating.Theta, 1e-9);
        Assert.AreEqual(6.60, rating.StarRating);
        ImmutableArray<WalkureRecommendation> actual = model.Recommend(scores, rating.Theta);
        string[] expected = ((JArray)(report["recommendations"] ?? throw new FormatException()))
            .Where(row => row.Value<string>("entryType") == "chart")
            .Select(row => Key(row.Value<string>("hash") ?? throw new FormatException(),
                ParseLamp(row.Value<string>("targetClearLamp") ?? throw new FormatException()), row.Value<double>("recommendationProbabilityPercent"))).ToArray();
        Assert.AreEqual(649, actual.Length);
        CollectionAssert.AreEquivalent(expected, actual.Select(row => Key(row.Entry.Md5, row.Lamp, row.Percent)).ToArray());
        foreach (IGrouping<WalkureLamp, WalkureRecommendation> group in actual.GroupBy(row => row.Lamp))
        {
            CollectionAssert.AreEqual(group.Select(row => row.Percent).OrderDescending().ToArray(), group.Select(row => row.Percent).ToArray());
        }

        Assert.IsTrue(actual.All(row => !row.Entry.IsCourse));
    }

    [TestMethod]
    public void Estimate_MixedLampsAndThreePoliciesMatchIndependentUpstreamCases()
    {
        WalkureRecommendationModel model = WalkureRecommendationModel.Bundled;
        foreach (JToken testCase in JArray.Parse(Read("math-cases.json")))
        {
            ImmutableDictionary<string, WalkureLamp> scores = ReadScores((JArray)(testCase["observations"] ?? throw new FormatException()));
            WalkurePlayerRating actual = model.Estimate(scores);
            Assert.AreEqual(testCase["rating"]?.Value<double>("theta") ?? throw new FormatException(), actual.Theta, 1e-9, testCase.Value<string>("name"));
            Assert.AreEqual(testCase["rating"]?.Value<double>("playerStarRating") ?? throw new FormatException(), actual.StarRating);
        }
        ImmutableDictionary<string, WalkureLamp> raw = ReadScores(JArray.Parse(Read("legacy-lr2-observations.json")));
        var cases = JArray.Parse(Read("math-cases.json"));
        foreach ((WalkureScorePolicy policy, string? name) in new[] { (WalkureScorePolicy.Standard, "standard"),
            (WalkureScorePolicy.UnplayedAsFailed, "baseFailed"), (WalkureScorePolicy.FailedAsUnplayed, "omitFailed") })
        {
            ImmutableDictionary<string, WalkureLamp> observations = model.ApplyPolicy(raw, policy);
            ImmutableDictionary<string, WalkureLamp> independent = ReadScores((JArray)(cases.Single(row => row.Value<string>("name") == name)["observations"] ?? throw new FormatException()));
            independent = independent.Where(row => model.EntriesByMd5.ContainsKey(row.Key)).ToImmutableDictionary();
            CollectionAssert.AreEquivalent(independent.ToArray(), observations.ToArray());
        }
    }

    [TestMethod]
    public void ConvertStarRating_InterpolatesAndExtrapolatesEndIntervals()
    {
        var model = new WalkureRecommendationModel([], [0, 2, 5]);
        foreach ((double theta, double expected) in new[] { (-2d, 0d), (0d, 1d), (1d, 1.5d), (2d, 2d), (3.5d, 2.5d), (8d, 4d) })
        {
            Assert.AreEqual(expected, model.ConvertStarRating(theta), 1e-12);
        }
    }

    [DataTestMethod]
    [DataRow(-.125, .875, 1.13)]
    [DataRow(-1.675, -.675, 2.67)]
    public void EstimatedStars_MatchJavascriptRoundingOfExactBinaryValues(double firstPoint, double lastPoint, double expected)
    {
        WalkureLampValues thresholds = new(0, 0, 0, 0);
        WalkureModelEntry failed = new("failed", "failed", false, 1, thresholds, thresholds, ImmutableDictionary<string, string>.Empty);
        WalkureModelEntry fullCombo = failed with { Md5 = "fc" };
        var model = new WalkureRecommendationModel([failed, fullCombo], [firstPoint, lastPoint]);
        ImmutableDictionary<string, WalkureLamp> scores = ImmutableDictionary<string, WalkureLamp>.Empty.Add("failed", WalkureLamp.Failed).Add("fc", WalkureLamp.FullCombo);
        WalkurePlayerRating rating = model.Estimate(scores);
        Assert.AreEqual(0, rating.Theta);
        Assert.AreEqual(expected, rating.StarRating);
    }

    [TestMethod]
    public void Recommendations_UseRawThresholdHigherLampAndUndefinedStar()
    {
        var entry = new WalkureModelEntry("test", "test", false, 1,
            new WalkureLampValues(0, 0, 0, 0), new WalkureLampValues(1, 1, 1, null), ImmutableDictionary<string, string>.Empty);
        var model = new WalkureRecommendationModel([entry], [0, 1]);
        Assert.AreEqual(.5, WalkureRecommendationModel.ClearProbability(entry, WalkureLamp.Easy, 0));
        Assert.AreEqual(0, WalkureRecommendationModel.ClearProbability(entry, WalkureLamp.FullCombo, 100));
        double boundary = Math.Log(.2 / .8);
        Assert.AreEqual(0, model.Recommend(ImmutableDictionary<string, WalkureLamp>.Empty, boundary - 1e-6).Length);
        ImmutableDictionary<string, WalkureLamp> scores = ImmutableDictionary<string, WalkureLamp>.Empty.Add("test", WalkureLamp.Easy);
        ImmutableArray<WalkureRecommendation> candidates = model.Recommend(scores, boundary);
        CollectionAssert.AreEquivalent(new[] { WalkureLamp.Normal, WalkureLamp.Hard }, candidates.Select(row => row.Lamp).ToArray());
        Assert.IsTrue(candidates.All(row => row.Percent == 20));
        Assert.AreEqual(2, model.Recommend(scores, boundary + 1e-6).Length);
    }

    [TestMethod]
    public void Policy_NewFailedDoesNotChangeOptionalPolicyRatingOrRawInput()
    {
        WalkureRecommendationModel model = WalkureRecommendationModel.Bundled;
        ImmutableDictionary<string, WalkureLamp> raw = ReadScores(JArray.Parse(Read("legacy-lr2-observations.json")));
        string unplayed = model.Entries.First(row => !raw.ContainsKey(row.Md5)).Md5;
        ImmutableDictionary<string, WalkureLamp> changed = raw.Add(unplayed, WalkureLamp.Failed);
        foreach (WalkureScorePolicy policy in new[] { WalkureScorePolicy.UnplayedAsFailed, WalkureScorePolicy.FailedAsUnplayed })
        {
            Assert.AreEqual(model.Estimate(model.ApplyPolicy(raw, policy)).Theta, model.Estimate(model.ApplyPolicy(changed, policy)).Theta, 1e-9);
        }

        Assert.IsFalse(raw.ContainsKey(unplayed));
        Assert.ThrowsException<InvalidOperationException>(() => model.Estimate(ImmutableDictionary<string, WalkureLamp>.Empty));
        Assert.ThrowsException<InvalidOperationException>(() => model.Estimate(ImmutableDictionary<string, WalkureLamp>.Empty.Add(model.Entries[0].Md5, WalkureLamp.Failed)));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Walkure", file));
    private static string Key(string md5, WalkureLamp lamp, double percent) => $"{md5}:{lamp}:{percent:R}";
    private static WalkureLamp ParseLamp(string name) => Enum.Parse<WalkureLamp>(name, true);
    private static ImmutableDictionary<string, WalkureLamp> ReadScores(JArray rows) => rows.ToImmutableDictionary(
        row => row.Value<string>("md5") ?? throw new FormatException(), row => ParseLamp(row.Value<string>("clearLamp") ?? throw new FormatException()), StringComparer.OrdinalIgnoreCase);
}
