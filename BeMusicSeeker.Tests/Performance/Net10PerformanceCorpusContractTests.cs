using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

[TestClass]
[TestCategory("Net10Performance")]
[TestCategory("contract")]
public sealed class Net10PerformanceCorpusContractTests
{
    [TestMethod]
    public void ConfiguredCorpus_UsesRequestedSeedAndScaleWithStableGolden()
    {
        int seed = GetConfiguredSeed();
        foreach (Net10PerformanceCorpusScale scale in GetConfiguredScales())
        {
            IReadOnlyList<Net10PerformanceCorpusRow> rows =
                Net10PerformanceCorpus.CreateRows(scale, seed);
            string fingerprint = Net10PerformanceCorpus.CreateFingerprint(rows);

            Assert.AreEqual(Net10PerformanceCorpus.GetRowCount(scale), rows.Count);
            if (seed == Net10PerformanceCorpus.DefaultSeed)
            {
                Assert.AreEqual(GetDefaultGoldenFingerprint(scale), fingerprint);
            }
            else
            {
                Assert.AreEqual(
                    fingerprint,
                    Net10PerformanceCorpus.CreateFingerprint(
                        Net10PerformanceCorpus.CreateRows(scale, seed)));
            }
        }
    }

    [TestMethod]
    public void DifferentSeedRows_ProduceDifferentFingerprint()
    {
        string first = Net10PerformanceCorpus.CreateFingerprint(
            Net10PerformanceCorpus.CreateRows(Net10PerformanceCorpusScale.Small, 1));
        string second = Net10PerformanceCorpus.CreateFingerprint(
            Net10PerformanceCorpus.CreateRows(Net10PerformanceCorpusScale.Small, 2));

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void TemporaryWorkspace_IsRemovedAfterCompletion()
    {
        string capturedPath = Net10PerformanceCorpus.WithTemporaryWorkspace(path =>
        {
            File.WriteAllText(Path.Combine(path, "fixture.txt"), "fixture");
            return path;
        });

        Assert.IsFalse(Directory.Exists(capturedPath));
    }

    [TestMethod]
    public void DisabledInstrumentation_DoesNotBuildOrPublishMessage()
    {
        int factoryCalls = 0;
        var messages = new List<string>();
        var writer = new Net10PerformanceEventWriter(() => false, messages.Add);
        PerformanceInteraction interaction = PerformanceInteraction.Existing(
            "normal_library",
            interactionId: 42,
            generation: 7);

        writer.Write(interaction, "snapshot_query_projection", () =>
        {
            factoryCalls++;
            return "rows=1000";
        });

        Assert.AreEqual(0, factoryCalls);
        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public void EnabledInstrumentation_UsesStableCorrelationSchema()
    {
        var messages = new List<string>();
        var writer = new Net10PerformanceEventWriter(() => true, messages.Add);
        PerformanceInteraction interaction = PerformanceInteraction.Existing(
            "playlist_summary",
            interactionId: 19,
            generation: 23);

        writer.Write(interaction, "ui_applied", () => "rows=100");

        CollectionAssert.AreEqual(
            new[]
            {
                "net10_perf route=playlist_summary interactionId=19 generation=23 stage=ui_applied rows=100"
            },
            messages);
    }

    private static int GetConfiguredSeed()
    {
        string value = Environment.GetEnvironmentVariable("BMS_NET10_PERF_SEED");
        return int.TryParse(value, out int seed)
            ? seed
            : Net10PerformanceCorpus.DefaultSeed;
    }

    private static IReadOnlyList<Net10PerformanceCorpusScale> GetConfiguredScales()
    {
        string value = Environment.GetEnvironmentVariable("BMS_NET10_PERF_SCALES");
        if (string.IsNullOrWhiteSpace(value))
        {
            return Enum.GetValues<Net10PerformanceCorpusScale>();
        }

        Net10PerformanceCorpusScale[] scales = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => Enum.Parse<Net10PerformanceCorpusScale>(item, ignoreCase: true))
            .Distinct()
            .ToArray();
        Assert.IsTrue(scales.Length > 0, "At least one performance corpus scale is required.");
        return scales;
    }

    private static string GetDefaultGoldenFingerprint(Net10PerformanceCorpusScale scale) => scale switch
    {
        Net10PerformanceCorpusScale.Small =>
            "51123503FA90BE44DE780C23D1F74795BE9785B66054869568C71341B727E0D5",
        Net10PerformanceCorpusScale.Medium =>
            "3CB33510F211A727BA3494A3F47F0ABE91D65666B06C247D5F5569B5448CA43A",
        Net10PerformanceCorpusScale.Large =>
            "F31E0CDECFB662740F3558F54BA02086C444D1BD33F9B35F1F65B3AB330C2CAC",
        _ => throw new ArgumentOutOfRangeException(nameof(scale))
    };
}
