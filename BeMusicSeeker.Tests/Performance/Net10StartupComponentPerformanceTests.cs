using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

[TestClass]
[TestCategory("Net10Performance")]
[TestCategory("startup")]
public sealed class Net10StartupComponentPerformanceTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [TestCategory("song-table")]
    public void SongTablePublication_NormalizesStorageRowsWithOneReferenceCopy()
    {
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            int count = Net10PerformanceCorpus.GetRowCount(scale);
            BMSFile[] input = Enumerable.Range(0, count)
                .Select(index => new BMSFile
                {
                    path = $@"C:\Charts\{index:D8}.bms",
                    hash = index.ToString("x32")
                })
                .ToArray();

            _ = BMSLibrary.NormalizeBmsStorageRows(input);
            long legacyStart = GC.GetAllocatedBytesForCurrentThread();
            List<BMSFile> legacyFirstCopy = [.. input];
            List<BMSFile> legacyNormalized = [.. legacyFirstCopy];
            long legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - legacyStart;

            long currentStart = GC.GetAllocatedBytesForCurrentThread();
            List<BMSFile> currentNormalized = BMSLibrary.NormalizeBmsStorageRows(input);
            long currentAllocated = GC.GetAllocatedBytesForCurrentThread() - currentStart;

            Assert.AreEqual(count, currentNormalized.Count);
            for (int index = 0; index < count; index++)
            {
                Assert.AreSame(input[index], currentNormalized[index]);
            }
            Assert.IsTrue(currentAllocated < legacyAllocated);
            TestContext.WriteLine(
                "route=song_table_publication"
                + " scale=" + scale
                + " rows=" + count
                + " referenceCopiesBefore=2"
                + " referenceCopiesAfter=1"
                + " legacyAllocatedBytes=" + legacyAllocated
                + " currentAllocatedBytes=" + currentAllocated);
        }
    }

    [TestMethod]
    [TestCategory("song-table")]
    public void BmsonSongTablePublication_NormalizesStorageRowsWithOneReferenceCopy()
    {
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            int count = Net10PerformanceCorpus.GetRowCount(scale);
            LR2SongDBExtended.bmson_song[] input = Enumerable.Range(0, count)
                .Select(index => new LR2SongDBExtended.bmson_song
                {
                    path = $@"C:\Charts\{index:D8}.bmson",
                    md5 = index.ToString("x32")
                })
                .ToArray();

            _ = BMSLibrary.NormalizeBmsonStorageRows(input);
            long legacyStart = GC.GetAllocatedBytesForCurrentThread();
            List<LR2SongDBExtended.bmson_song> legacyFirstCopy = [.. input];
            List<LR2SongDBExtended.bmson_song> legacyNormalized = [.. legacyFirstCopy];
            long legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - legacyStart;

            long currentStart = GC.GetAllocatedBytesForCurrentThread();
            List<LR2SongDBExtended.bmson_song> currentNormalized =
                BMSLibrary.NormalizeBmsonStorageRows(input);
            long currentAllocated = GC.GetAllocatedBytesForCurrentThread() - currentStart;

            Assert.AreEqual(count, currentNormalized.Count);
            for (int index = 0; index < count; index++)
            {
                Assert.AreSame(input[index], currentNormalized[index]);
            }
            Assert.IsTrue(currentAllocated < legacyAllocated);
            TestContext.WriteLine(
                "route=bmson_song_table_publication"
                + " scale=" + scale
                + " rows=" + count
                + " referenceCopiesBefore=2"
                + " referenceCopiesAfter=1"
                + " legacyAllocatedBytes=" + legacyAllocated
                + " currentAllocatedBytes=" + currentAllocated);
        }
    }

    [TestMethod]
    [TestCategory("resource-health")]
    public void ResourceHealth_FullBuildSinglePassPreservesProjectionAndReducesAllocation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var maintenanceService = new BmsLibraryMaintenanceService();
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            IReadOnlyList<ChartFile> targets = CreateResourceHealthTargets(
                Net10PerformanceCorpus.GetRowCount(scale));

            _ = BuildLegacyResourceHealth(targets);
            _ = ResourceHealthIndexSnapshot.Build(targets, maintenanceService, version: 1);

            long legacyStart = GC.GetAllocatedBytesForCurrentThread();
            LegacyResourceHealthSnapshot legacy = BuildLegacyResourceHealth(targets);
            long legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - legacyStart;

            long currentStart = GC.GetAllocatedBytesForCurrentThread();
            ResourceHealthIndexSnapshot current =
                ResourceHealthIndexSnapshot.Build(targets, maintenanceService, version: 2);
            long currentAllocated = GC.GetAllocatedBytesForCurrentThread() - currentStart;

            Assert.AreEqual(legacy.TargetCount, current.TargetCount);
            Assert.AreEqual(legacy.NeedFixCount, current.NeedFixCount);
            Assert.AreEqual(legacy.IgnoredCount, current.IgnoredCount);
            Assert.AreEqual(
                CreateLegacyWarningFingerprint(legacy, targets),
                CreateCurrentWarningFingerprint(current, targets));
            Assert.IsTrue(
                currentAllocated < legacyAllocated,
                $"Expected single-pass allocation below legacy allocation for {scale}: "
                + $"legacy={legacyAllocated}, current={currentAllocated}.");
            TestContext.WriteLine(
                "route=resource_health_full_build"
                + " scale=" + scale
                + " inputTargets=" + targets.Count
                + " uniqueTargets=" + current.TargetCount
                + " needFix=" + current.NeedFixCount
                + " ignored=" + current.IgnoredCount
                + " distinctContainersBefore=2"
                + " distinctContainersAfter=1"
                + " legacyAllocatedBytes=" + legacyAllocated
                + " currentAllocatedBytes=" + currentAllocated);
        }
    }

    private static IReadOnlyList<ChartFile> CreateResourceHealthTargets(int count)
    {
        var targets = new ChartFile[count];
        ChartFile previous = null;
        for (int index = 0; index < count; index++)
        {
            if (index > 0 && index % 20 == 0)
            {
                BMSFileMaintenanceInfo duplicateInfo = CreateResourceHealthInfo(
                    index - 1,
                    hasWarning: true,
                    isIgnored: true);
                duplicateInfo.path = previous.Path.ToUpperInvariant();
                duplicateInfo.hash = previous.Md5.ToUpperInvariant();
                targets[index] = CreateResourceHealthChart(duplicateInfo, "duplicate");
                continue;
            }
            bool hasWarning = index % 4 == 0;
            BMSFileMaintenanceInfo info = CreateResourceHealthInfo(
                index,
                hasWarning,
                isIgnored: index % 8 == 0);
            previous = CreateResourceHealthChart(info, index.ToString());
            targets[index] = previous;
        }
        return targets;
    }

    private static BMSFileMaintenanceInfo CreateResourceHealthInfo(
        int index,
        bool hasWarning,
        bool isIgnored) =>
        new()
        {
            path = $@"C:\Charts\Fixture\{index:D8}.bms",
            hash = index.ToString("x32"),
            wav_files_defined = hasWarning ? 2 : 1,
            wav_files_existing = 1,
            is_files_warning_ignored = isIgnored
        };

    private static ChartFile CreateResourceHealthChart(
        BMSFileMaintenanceInfo info,
        string title) =>
        new(
                ChartFileKind.Bms,
                info.path,
                info.hash,
                sha256: null,
                title,
                rawTitle: title,
                artist: string.Empty,
                genre: string.Empty,
                folder: "Fixture",
                tag: string.Empty,
                levelText: string.Empty,
                level: null,
                mode: null,
                chartInfo: null,
                bmsFile: null,
                bmsonSong: null,
                resourceHealthWarningsIgnored: info.is_files_warning_ignored,
                resourceHealthMaintenanceSnapshot: ResourceHealthMaintenanceSnapshot.From(info));

    private static LegacyResourceHealthSnapshot BuildLegacyResourceHealth(
        IEnumerable<ChartFile> targets)
    {
        var uniqueTargets = new List<ChartFile>();
        var uniqueKeys = new HashSet<LegacyResourceKey>();
        foreach (ChartFile target in targets ?? [])
        {
            var key = LegacyResourceKey.From(target);
            if (target != null
                && key.IsValid
                && uniqueKeys.Add(key))
            {
                uniqueTargets.Add(target);
            }
        }

        var finalKeys = new HashSet<LegacyResourceKey>();
        var projections = new Dictionary<LegacyResourceKey, ResourceHealthWarningProjection>();
        var activeTargets = new List<ChartFile>();
        var ignoredTargets = new List<ChartFile>();
        foreach (ChartFile target in uniqueTargets)
        {
            LegacyResourceKey key = LegacyResourceKey.From(target);
            finalKeys.Add(key);
            BMSFileMaintenanceInfo warningInfo =
                target.ResourceHealthMaintenanceSnapshot?.ToMutable()
                ?? BmsLibraryMaintenanceService.GetResourceHealthMaintenanceInfo(target);
            IReadOnlyList<ChartWarning> warnings =
                BmsLibraryMaintenanceService.BuildResourceHealthWarnings(warningInfo);
            if (warnings.Count == 0)
            {
                continue;
            }
            BMSFileMaintenanceInfo ignoredInfo =
                target.ResourceHealthMaintenanceSnapshot?.ToMutable()
                ?? BmsLibraryMaintenanceService.GetResourceHealthMaintenanceInfo(target);
            bool isIgnored = ignoredInfo?.is_files_warning_ignored == true;
            projections[key] = new ResourceHealthWarningProjection(1, warnings, isIgnored);
            if (isIgnored)
            {
                ignoredTargets.Add(target);
            }
            else
            {
                activeTargets.Add(target);
            }
        }
        return new LegacyResourceHealthSnapshot(
            finalKeys,
            projections,
            activeTargets,
            ignoredTargets);
    }

    private static string CreateLegacyWarningFingerprint(
        LegacyResourceHealthSnapshot snapshot,
        IEnumerable<ChartFile> targets)
    {
        return string.Join("|", EnumerateUniqueKeys(targets).Select(key =>
        {
            snapshot.Projections.TryGetValue(key, out ResourceHealthWarningProjection projection);
            return key.Path
                + ":" + (projection?.IsIgnored == true ? "ignored" : "active")
                + ":" + string.Join(",", (projection?.Warnings ?? [])
                .Select(warning => warning.Kind + "/" + warning.Message));
        }));
    }

    private static string CreateCurrentWarningFingerprint(
        ResourceHealthIndexSnapshot snapshot,
        IEnumerable<ChartFile> targets)
    {
        return string.Join("|", EnumerateUniqueCharts(targets).Select(chart =>
        {
            ResourceHealthWarningProjection projection = snapshot.GetProjection(chart);
            return chart.Path
                + ":" + (projection.IsIgnored ? "ignored" : "active")
                + ":" + string.Join(",", projection.Warnings
                .Select(warning => warning.Kind + "/" + warning.Message));
        }));
    }

    private static IEnumerable<LegacyResourceKey> EnumerateUniqueKeys(IEnumerable<ChartFile> targets)
    {
        var seen = new HashSet<LegacyResourceKey>();
        foreach (ChartFile target in targets ?? [])
        {
            LegacyResourceKey key = LegacyResourceKey.From(target);
            if (key.IsValid && seen.Add(key))
            {
                yield return key;
            }
        }
    }

    private static IEnumerable<ChartFile> EnumerateUniqueCharts(IEnumerable<ChartFile> targets)
    {
        var seen = new HashSet<LegacyResourceKey>();
        foreach (ChartFile target in targets ?? [])
        {
            LegacyResourceKey key = LegacyResourceKey.From(target);
            if (key.IsValid && seen.Add(key))
            {
                yield return target;
            }
        }
    }

    private sealed class LegacyResourceHealthSnapshot
    {
        internal LegacyResourceHealthSnapshot(
            HashSet<LegacyResourceKey> targetKeys,
            Dictionary<LegacyResourceKey, ResourceHealthWarningProjection> projections,
            List<ChartFile> activeTargets,
            List<ChartFile> ignoredTargets)
        {
            TargetKeys = targetKeys;
            Projections = projections;
            ActiveTargets = activeTargets;
            IgnoredTargets = ignoredTargets;
        }

        internal HashSet<LegacyResourceKey> TargetKeys { get; }

        internal Dictionary<LegacyResourceKey, ResourceHealthWarningProjection> Projections { get; }

        internal List<ChartFile> ActiveTargets { get; }

        internal List<ChartFile> IgnoredTargets { get; }

        internal int TargetCount => TargetKeys.Count;

        internal int NeedFixCount => ActiveTargets.Count + IgnoredTargets.Count;

        internal int IgnoredCount => IgnoredTargets.Count;
    }

    private readonly struct LegacyResourceKey : IEquatable<LegacyResourceKey>
    {
        private LegacyResourceKey(ChartFileKind kind, string path, string md5)
        {
            Kind = kind;
            Path = path ?? string.Empty;
            Md5 = md5 ?? string.Empty;
        }

        internal ChartFileKind Kind { get; }

        internal string Path { get; }

        internal string Md5 { get; }

        internal bool IsValid => !string.IsNullOrWhiteSpace(Path);

        internal static LegacyResourceKey From(ChartFile chart) =>
            chart == null ? default : new LegacyResourceKey(chart.Kind, chart.Path, chart.Md5);

        public bool Equals(LegacyResourceKey other) =>
            Kind == other.Kind
            && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Md5, other.Md5, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) =>
            obj is LegacyResourceKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(
                Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(Path),
                StringComparer.OrdinalIgnoreCase.GetHashCode(Md5));
    }
}
