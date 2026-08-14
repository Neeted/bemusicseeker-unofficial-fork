using System;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

[TestClass]
[TestCategory("Net10Performance")]
public sealed class Net10ScanParserPerformanceTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [TestCategory("parser")]
    public void BmsonContinuationLookup_EqualYGroupsStayWithinLinearProbeBound()
    {
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
            TestContext.WriteLine(
                "route=bmson_continuation_lookup"
                + " scale=" + scale
                + " notes=" + count
                + " currentProbeUpperBound=" + currentProbeUpperBound);
        }
    }
}
