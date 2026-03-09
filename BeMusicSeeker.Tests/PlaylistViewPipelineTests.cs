using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistViewPipelineTests
{
    [TestMethod]
    public void ApplyPlaylistViewFromSource_SortKeepsAllRowsVisible()
    {
        VirtualBMSFile zetaRow = CreateVirtualRow("11111111111111111111111111111111", "Zeta", 7);
        VirtualBMSFile alphaRow = CreateVirtualRow("22222222222222222222222222222222", "Alpha", 7);
        BMSFile[] sourceRows = new BMSFile[] { zetaRow, alphaRow };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<BMSFile> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEqual(new[] { "Alpha", "Zeta" }, result.Select((BMSFile row) => row.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "22222222222222222222222222222222", "11111111111111111111111111111111" }, result.Select((BMSFile row) => row.hash).ToArray());
        Assert.IsFalse(ReferenceEquals(sourceRows[0], result[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[1], result[1]));
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(2, modeCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sortProfile));

        DisposeRows(sourceRows);
        DisposeRows(result);
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_KeywordFilterMatchesPlaylistMemoAndComment()
    {
        VirtualBMSFile matchedRow = CreateVirtualRow("33333333333333333333333333333333", "Matched", 7, memo: "special memo");
        VirtualBMSFile filteredRow = CreateVirtualRow("44444444444444444444444444444444", "Filtered", 7, comment: "ordinary");
        BMSFile[] sourceRows = new BMSFile[] { matchedRow, filteredRow };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<BMSFile> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: "SPECIAL",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(ReferenceEquals(sourceRows[0], result[0]));

        DisposeRows(sourceRows);
        DisposeRows(result);
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_ModeFilterRecomputesFromSourceRows()
    {
        VirtualBMSFile sevenKeysRow = CreateVirtualRow("55555555555555555555555555555555", "SevenKeys", 7);
        VirtualBMSFile fourteenKeysRow = CreateVirtualRow("66666666666666666666666666666666", "FourteenKeys", 14);
        BMSFile[] sourceRows = new BMSFile[] { sevenKeysRow, fourteenKeysRow };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<BMSFile> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType._14KEYS,
            sortParameters: sortParameters,
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("FourteenKeys", result[0].Title);
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(ReferenceEquals(sourceRows[1], result[0]));

        DisposeRows(sourceRows);
        DisposeRows(result);
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_RebuildsDetachedSnapshotsForEachApply()
    {
        VirtualBMSFile alphaRow = CreateVirtualRow("77777777777777777777777777777777", "Alpha", 7);
        BMSFile[] sourceRows = new BMSFile[] { alphaRow };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<BMSFile> first = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _);
        List<BMSFile> second = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
        Assert.IsFalse(ReferenceEquals(first[0], second[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[0], first[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[0], second[0]));

        DisposeRows(sourceRows);
        DisposeRows(first);
        DisposeRows(second);
    }

    private static VirtualBMSFile CreateVirtualRow(string hash, string title, int? mode, string memo = "", string comment = "")
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot(hash, title, mode);
        BMSTableEntry entry = new BMSTableEntry(file)
        {
            memo = memo,
            comment = comment
        };
        return new VirtualBMSFile(entry, file);
    }

    private static void DisposeRows(IEnumerable<BMSFile> rows)
    {
        foreach (BMSFile row in rows)
        {
            if (row is VirtualBMSFile virtualRow)
            {
                virtualRow.Dispose();
            }
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void ApplySnapshot(string snapshotHash, string snapshotTitle, int? snapshotMode)
        {
            hash = snapshotHash;
            path = snapshotTitle + ".bms";
            Title = snapshotTitle;
            Artist = "TestArtist";
            genre = "TestGenre";
            mode = snapshotMode;
        }
    }
}
