using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

[TestClass]
[TestCategory("Net10Performance")]
public sealed class Net10ScanParserPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 同位置noteだけが1,000・4,000・16,000件続く入力の全位置で次音なしを保ち、同位置の音符を次の継続音として誤って返すことを検出する。
    /// </summary>
    /// <remarks>後続の異なる位置がある探索はChartInfoParserBehaviorTestsが担当する。このケースは継続指定の有無を混ぜた同位置だけの入力を全位置から調べる機能検査であり、走査回数や所要時間は計測しない。</remarks>
    [TestMethod]
    [TestCategory("parser")]
    public void BmsonContinuationLookup_EqualYOnlyGroupHasNoNextNoteFromAnyPosition()
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

            int nextDistinctNoteIndex = 0;
            for (int noteIndex = 0; noteIndex < notes.Length; noteIndex++)
            {
                BmsonSoundNote next = ChartInfoParser.AdvanceToNextBmsonContinuationNote(
                    notes,
                    noteIndex,
                    ref nextDistinctNoteIndex);
                Assert.IsNull(next);
            }

            TestContext.WriteLine(
                "route=bmson_continuation_lookup"
                + " scale=" + scale
                + " notes=" + count);
        }
    }
}
