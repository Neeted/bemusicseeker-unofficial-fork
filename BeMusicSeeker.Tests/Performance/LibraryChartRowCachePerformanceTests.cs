using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

/// <summary>外側行cacheのwarm列読取りと一行更新を同条件で測り、全体表示や代表規模の測定とは分けます。</summary>
[TestClass, TestCategory("Net10Performance"), DoNotParallelize]
public sealed class LibraryChartRowCachePerformanceTests
{
    /// <summary>明示性能区分の各標本と比較条件を標準TRXへ記録します。</summary>
    public TestContext? TestContext { get; set; }

    /// <summary>64可視行の10列hitと一行のmissから後続hitまでを各5回測ります。準備・warmup・結果検査を時間外へ置き、独自の性能閾値は設けません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void VisibleColumns_ReportWarmHitsAndChangedRowMisses(bool package)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const int visible = 64, hitRounds = 10_000, changes = 10_000;
            var entries = new PackageChartEntry[visible];
            var score = new ChartScoreSnapshot(ClearType.NO_PLAY, score: 100);
            var detail = new ChartDetails { level = 3 };
            int detailResolutions = 0, scoreResolutions = 0;
            LibraryChartRow[] rows = Enumerable.Range(0, visible).Select(index =>
            {
                ChartFile chart = ChartTestValues.Empty() with { Path = $@"cache\{index}.bms", Title = "First" };
                entries[index] = PackageChartEntry.FromChart(chart);
                LibraryChartRow row = package ? LibraryChartRow.FromPackageChartEntry(entries[index]) : LibraryChartRow.FromChartFile(chart);
                row.SetChartInfoProjectionProvider(_ => { detailResolutions++; return detail; });
                row.SetScoreProjectionProvider(_ => { scoreResolutions++; return score; }, () => 0);
                return row;
            }).ToArray();
            ChartFile first = ChartTestValues.Empty() with { Path = @"cache\0.bms", Title = "First" };
            ChartFile second = first with { Title = "Second" };
            void Update(int index)
            {
                ChartFile value = (index & 1) == 0 ? first : second;
                if (package) { entries[0].ApplyCurrentChart(value); }
                else { rows[0].UpdateSourceProjection(value); }
            }
            long ReadColumns(LibraryChartRow row) => row.Title.Length + row.path.Length + row.instl_dst.Length
                + (int)row.status + (long)(row.ChartLevelSortKey ?? 0) + (row.score ?? 0)
                + (int)row.clear + (row.WAVHealth ?? 0) + row.Artist.Length + row.genre.Length;
            long Hit(int rounds)
            {
                long checksum = 0;
                for (int round = 0; round < rounds; round++)
                { foreach (LibraryChartRow row in rows) { checksum += ReadColumns(row); } }
                return checksum;
            }
            long Miss(int count)
            {
                long checksum = 0;
                for (int index = 0; index < count; index++)
                {
                    Update(index);
                    checksum += ReadColumns(rows[0]);
                    checksum += ReadColumns(rows[0]);
                    checksum += ReadColumns(rows[1]);
                }
                return checksum;
            }
            _ = Hit(1000);
            _ = Miss(1000);
            TestContext?.WriteLine($"row_cache condition package={package} visible={visible} columns=10 runtime={RuntimeInformation.FrameworkDescription} os={Environment.OSVersion} configuration=Release tieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"} scope=UI-getters-only no-render-or-view-build=true warmupExcluded=true");
            foreach (string route in new[] { "hit", "miss" })
            {
                double[] times = new double[5];
                long[] allocations = new long[5];
                for (int repetition = 0; repetition < 5; repetition++)
                {
                    Update(1);
                    _ = Hit(1);
                    ChartFile unaffected = rows[1].Chart;
                    int detailsBefore = detailResolutions, scoresBefore = scoreResolutions;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    long checksum = route == "hit" ? Hit(hitRounds) : Miss(changes);
                    times[repetition] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    allocations[repetition] = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.IsTrue(checksum > 0);
                    Assert.AreEqual("Second", rows[0].Title);
                    Assert.AreEqual(100, rows[0].score);
                    Assert.AreEqual(3d, rows[0].ChartLevelSortKey);
                    Assert.AreSame(unaffected, rows[1].Chart);
                    Assert.AreEqual(route == "hit" ? 0 : changes, detailResolutions - detailsBefore);
                    Assert.AreEqual(route == "hit" ? 0 : changes, scoreResolutions - scoresBefore);
                    TestContext?.WriteLine($"row_cache package={package} route={route} repetition={repetition + 1} columnSets={(route == "hit" ? visible * hitRounds : changes * 3)} updates={(route == "hit" ? 0 : changes)} elapsedMs={times[repetition]:F4} allocatedBytes={allocations[repetition]} detailResolutions={detailResolutions - detailsBefore} scoreResolutions={scoreResolutions - scoresBefore}");
                }
                double[] ordered = times.Order().ToArray();
                TestContext?.WriteLine($"row_cache package={package} route={route} medianMs={ordered[2]:F4} minMs={ordered[0]:F4} maxMs={ordered[^1]:F4} allocationRange={allocations.Min()}..{allocations.Max()}");
            }
        });
    }
}
