using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

[TestClass]
[TestCategory("Net10Performance")]
[TestCategory("list")]
public sealed class Net10ListPerformanceTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void NormalSummary_IndexedSnapshotPreservesOutputAndRemovesReferenceCollection()
    {
        int seed = Net10PerformanceCorpus.GetConfiguredSeed();
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            IReadOnlyList<Net10PerformanceCorpusRow> corpusRows =
                Net10PerformanceCorpus.CreateRows(scale, seed);
            IReadOnlyList<ChartListSourceRow> sourceRows = CreateSourceRows(corpusRows);
            int[] indexes = Enumerable.Range(0, sourceRows.Count).Reverse().ToArray();
            ChartListOrder orderTemplate = ChartListOrder.CreateTitleAscending(sourceRows);

            IReadOnlyList<ChartListSourceRow> warmLegacy =
                RegularChartListOwner.SelectSourceRowsByOrder(sourceRows, indexes);
            _ = RegularChartListOwner.CountDistinctFolders(warmLegacy);
            ChartListOrder warmSnapshot = orderTemplate.WithIndexes(indexes);
            _ = RegularChartListOwner.CountDistinctFoldersByIndex(
                sourceRows,
                warmSnapshot.Indexes,
                shouldStop: null,
                out _,
                out _);

            long legacyStart = GC.GetAllocatedBytesForCurrentThread();
            IReadOnlyList<ChartListSourceRow> materialized =
                RegularChartListOwner.SelectSourceRowsByOrder(sourceRows, indexes);
            int legacyFolderCount = RegularChartListOwner.CountDistinctFolders(materialized);
            long legacyAllocated = GC.GetAllocatedBytesForCurrentThread() - legacyStart;

            long indexedStart = GC.GetAllocatedBytesForCurrentThread();
            ChartListOrder ownedSnapshot = orderTemplate.WithIndexes(indexes);
            int indexedFolderCount = RegularChartListOwner.CountDistinctFoldersByIndex(
                sourceRows,
                ownedSnapshot.Indexes,
                shouldStop: null,
                out int scannedIndexCount,
                out bool stopped);
            long indexedAllocated = GC.GetAllocatedBytesForCurrentThread() - indexedStart;

            Assert.AreEqual(indexes.Length, materialized.Count);
            Assert.AreEqual(legacyFolderCount, indexedFolderCount);
            Assert.AreEqual(indexes.Length, scannedIndexCount);
            Assert.IsFalse(stopped);
            Assert.IsTrue(
                indexedAllocated < legacyAllocated,
                $"Expected indexed allocation below legacy allocation for {scale}: "
                + $"legacy={legacyAllocated}, indexed={indexedAllocated}.");
            TestContext.WriteLine(
                "route=normal_summary"
                + " scale=" + scale
                + " rows=" + sourceRows.Count
                + " sourceReferenceMaterializationsBefore=" + materialized.Count
                + " sourceReferenceMaterializationsAfter=0"
                + " legacyAllocatedBytes=" + legacyAllocated
                + " indexedAllocatedBytes=" + indexedAllocated
                + " includesOwnedIndexSnapshot=True"
                + " folderCount=" + indexedFolderCount);
        }
    }

    private static IReadOnlyList<ChartListSourceRow> CreateSourceRows(
        IReadOnlyList<Net10PerformanceCorpusRow> rows)
    {
        var result = new ChartListSourceRow[rows.Count];
        for (int index = 0; index < rows.Count; index++)
        {
            Net10PerformanceCorpusRow row = rows[index];
            string fileName = row.Sequence.ToString("D8") + ".bms";
            var chart = new ChartFile(
                ChartFileKind.Bms,
                System.IO.Path.Combine(row.Folder, fileName),
                md5: row.Sequence.ToString("X32"),
                sha256: null,
                title: row.Title,
                rawTitle: row.Title,
                artist: row.Artist,
                genre: string.Empty,
                folder: row.Folder,
                tag: string.Empty,
                levelText: string.Empty,
                level: null,
                mode: row.Mode,
                chartInfo: null,
                bmsFile: null,
                bmsonSong: null);
            result[index] = ChartListSourceRow.FromChartFile(chart);
        }
        return result;
    }
}
