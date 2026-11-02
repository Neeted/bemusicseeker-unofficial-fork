using System.Diagnostics;
using System.Globalization;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests.Performance;

[TestClass]
[TestCategory("Suite.bmson-continuation")]
public sealed class Net10ScanParserPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 同位置noteだけが1,000・4,000・16,000件続く入力の全位置で次音なしを保ち、同位置の音符を次の継続音として誤って返すことを検出する。
    /// </summary>
    /// <remarks>後続の異なる位置がある探索はChartInfoParserBehaviorTestsが担当する。このケースは継続指定の有無を混ぜた同位置だけの入力を全位置から調べる機能検査であり、全位置の探索だけを計時し、生成・結果領域の確保・全位置の表明は計時外とする。走査回数と速度閾値は保証しない。</remarks>
    [TestMethod]
    public void BmsonContinuationLookup_EqualYOnlyGroupHasNoNextNoteFromAnyPosition()
    {
        foreach (InputScale scale in Enum.GetValues<InputScale>())
        {
            int count = scale switch
            {
                InputScale.Small => 1_000,
                InputScale.Medium => 4_000,
                _ => 16_000
            };
            BmsonSoundNote[] notes = Enumerable.Range(0, count)
                .Select(index => new BmsonSoundNote
                {
                    Y = 0,
                    Continue = index % 3 != 0
                })
                .ToArray();

            var results = new BmsonSoundNote?[notes.Length];
            int nextDistinctNoteIndex = 0;
            var clock = Stopwatch.StartNew();
            for (int noteIndex = 0; noteIndex < notes.Length; noteIndex++)
            {
                results[noteIndex] = ChartInfoParser.AdvanceToNextBmsonContinuationNote(
                    notes,
                    noteIndex,
                    ref nextDistinctNoteIndex);
            }

            clock.Stop();
            foreach (BmsonSoundNote? next in results) { Assert.IsNull(next); }

            TestContext.WriteLine(
                "route=bmson_continuation_lookup"
                + " scale=" + scale
                + " notes=" + count
                + " bodyElapsedMs=" + clock.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
        }
    }
}
