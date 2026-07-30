using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

[TestClass]
[TestCategory("Net10Performance")]
public sealed class Net10ScanParserPerformanceTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [TestCategory("scan")]
    public void ManagedResourceIndex_ReusesSortedDistinctScanArrays()
    {
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            int count = Net10PerformanceCorpus.GetRowCount(scale);
            uint[] hashes = Enumerable.Range(1, count).Select(index => (uint)index).ToArray();

            _ = new DirectoryResourceLookupCache.Entry(hashes, hashes, hashes);
            _ = DirectoryResourceLookupCache.Entry.FromSortedDistinctArrays(
                hashes,
                hashes,
                hashes);

            long legacyStart = GC.GetAllocatedBytesForCurrentThread();
            var legacy = new DirectoryResourceLookupCache.Entry(hashes, hashes, hashes);
            long legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - legacyStart;

            long currentStart = GC.GetAllocatedBytesForCurrentThread();
            DirectoryResourceLookupCache.Entry current =
                DirectoryResourceLookupCache.Entry.FromSortedDistinctArrays(
                    hashes,
                    hashes,
                    hashes);
            long currentAllocated = GC.GetAllocatedBytesForCurrentThread() - currentStart;

            CollectionAssert.AreEqual(legacy.AudioRelativePathHashArray, current.AudioRelativePathHashArray);
            CollectionAssert.AreEqual(legacy.ImageRelativePathHashArray, current.ImageRelativePathHashArray);
            CollectionAssert.AreEqual(legacy.MovieRelativePathHashArray, current.MovieRelativePathHashArray);
            Assert.IsTrue(currentAllocated < legacyAllocated);

            string directory = @"C:\Charts\Fixture";
            var scanResult = new ChartScanResult
            {
                ResourceHashArraysAreSortedDistinct = true,
                ChartDirectories = new HashSet<string>([directory], StringComparer.OrdinalIgnoreCase),
                AudioRelativePathHashesByChartDirectory =
                    new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [directory] = hashes
                    },
                ImageRelativePathHashesByChartDirectory =
                    new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [directory] = hashes
                    },
                MovieRelativePathHashesByChartDirectory =
                    new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [directory] = hashes
                    }
            };
            DirectoryResourceLookupCache cache =
                DirectoryResourceLookupCache.CreateFromScanResult(scanResult);
            DirectoryResourceLookupCache.Entry published = cache.GetEntryOrNull(directory);
            CollectionAssert.AreEqual(hashes, published.AudioRelativePathHashArray);
            CollectionAssert.AreEqual(hashes, published.ImageRelativePathHashArray);
            CollectionAssert.AreEqual(hashes, published.MovieRelativePathHashArray);

            TestContext.WriteLine(
                "route=managed_resource_index"
                + " scale=" + scale
                + " hashesPerCategory=" + count
                + " legacyAllocatedBytes=" + legacyAllocated
                + " currentAllocatedBytes=" + currentAllocated);
        }
    }

    [TestMethod]
    [TestCategory("parser")]
    public void BmsonContinuationLookup_AdvancesLinearlyWithEqualYGroups()
    {
        AssertContinuationLookupMatchesLegacy([0, 0, 1, 1, 1, 4, 8, 8, 13]);
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            int count = scale switch
            {
                Net10PerformanceCorpusScale.Small => 1_000,
                Net10PerformanceCorpusScale.Medium => 4_000,
                _ => 16_000
            };
            BmsonSoundNote[] notes = Enumerable.Range(0, count)
                .Select(index => new BmsonSoundNote
                {
                    Y = 0,
                    Continue = index % 3 != 0
                })
                .ToArray();

            long legacyProbeCount = (long)count * (count - 1) / 2;

            long currentProbeUpperBound = 0;
            int nextDistinctNoteIndex = 0;
            for (int noteIndex = 0; noteIndex < notes.Length; noteIndex++)
            {
                int before = nextDistinctNoteIndex;
                BmsonSoundNote next = ChartInfoParser.AdvanceToNextBmsonContinuationNote(
                    notes,
                    noteIndex,
                    ref nextDistinctNoteIndex);
                currentProbeUpperBound += Math.Max(1, nextDistinctNoteIndex - Math.Max(before, noteIndex));
                Assert.IsNull(next);
            }

            Assert.IsTrue(currentProbeUpperBound <= count * 2L);
            Assert.IsTrue(currentProbeUpperBound < legacyProbeCount);
            TestContext.WriteLine(
                "route=bmson_continuation_lookup"
                + " scale=" + scale
                + " notes=" + count
                + " legacyProbeCount=" + legacyProbeCount
                + " currentProbeUpperBound=" + currentProbeUpperBound);
        }
    }

    private static void AssertContinuationLookupMatchesLegacy(int[] notePositions)
    {
        BmsonSoundNote[] notes = notePositions
            .Select(position => new BmsonSoundNote { Y = position, Continue = true })
            .ToArray();
        int nextDistinctNoteIndex = 0;
        for (int noteIndex = 0; noteIndex < notes.Length; noteIndex++)
        {
            BmsonSoundNote legacy = notes
                .Skip(noteIndex + 1)
                .FirstOrDefault(note => note.Y > notes[noteIndex].Y);
            BmsonSoundNote current = ChartInfoParser.AdvanceToNextBmsonContinuationNote(
                notes,
                noteIndex,
                ref nextDistinctNoteIndex);
            Assert.AreSame(legacy, current);
        }
    }
}
