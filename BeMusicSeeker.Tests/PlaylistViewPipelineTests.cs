using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System;
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
        PlaylistDetailSourceRow zetaRow = CreateSourceRow("11111111111111111111111111111111", "Zeta", 7);
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("22222222222222222222222222222222", "Alpha", 7);
        PlaylistDetailSourceRow[] sourceRows = new PlaylistDetailSourceRow[] { zetaRow, alphaRow };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEqual(new[] { "Alpha", "Zeta" }, result.Select((PlaylistDetailRow row) => row.Title).ToArray());
        CollectionAssert.AreEqual(new[] { "22222222222222222222222222222222", "11111111111111111111111111111111" }, result.Select((PlaylistDetailRow row) => row.hash).ToArray());
        Assert.IsFalse(ReferenceEquals(sourceRows[0], result[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[1], result[1]));
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(2, modeCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(sortProfile));
        Assert.IsTrue(result.All((PlaylistDetailRow row) => !typeof(BMSFile).IsAssignableFrom(row.GetType())));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_KeywordFilterMatchesPlaylistMemoAndComment()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow("33333333333333333333333333333333", "Matched", 7, memo: "special memo");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow("44444444444444444444444444444444", "Filtered", 7, comment: "ordinary");
        PlaylistDetailSourceRow[] sourceRows = new PlaylistDetailSourceRow[] { matchedRow, filteredRow };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: "SPECIAL",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(ReferenceEquals(sourceRows[0], result[0]));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_ModeFilterRecomputesFromSourceRows()
    {
        PlaylistDetailSourceRow sevenKeysRow = CreateSourceRow("55555555555555555555555555555555", "SevenKeys", 7);
        PlaylistDetailSourceRow fourteenKeysRow = CreateSourceRow("66666666666666666666666666666666", "FourteenKeys", 14);
        PlaylistDetailSourceRow[] sourceRows = new PlaylistDetailSourceRow[] { sevenKeysRow, fourteenKeysRow };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType._14KEYS,
            sortParameters: sortParameters,
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("FourteenKeys", result[0].Title);
        Assert.AreEqual(2, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(ReferenceEquals(sourceRows[1], result[0]));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_RebuildsDetachedSnapshotsForEachApply()
    {
        PlaylistDetailSourceRow alphaRow = CreateSourceRow("77777777777777777777777777777777", "Alpha", 7);
        PlaylistDetailSourceRow[] sourceRows = new PlaylistDetailSourceRow[] { alphaRow };
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> first = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);
        List<PlaylistDetailRow> second = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string _,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
        Assert.IsFalse(ReferenceEquals(first[0], second[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[0], first[0]));
        Assert.IsFalse(ReferenceEquals(sourceRows[0], second[0]));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_LevelSortUsesPlaylistEntryDoubleValueNumerically()
    {
        PlaylistDetailSourceRow entryLevelTwelve = CreateSourceRow("88888888888888888888888888888888", "Twelve", 7, entryLevel: 12);
        PlaylistDetailSourceRow entryLevelTwoPointFive = CreateSourceRow("99999999999999999999999999999999", "TwoPointFive", 7, entryLevel: 2.5);
        PlaylistDetailSourceRow entryLevelThree = CreateSourceRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Three", 7, entryLevel: 3);
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.Level),
            Direction = ListSortDirection.Ascending
        };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            new[] { entryLevelTwelve, entryLevelTwoPointFive, entryLevelThree },
            keywordFilter: null,
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: sortParameters,
            out string sortProfile,
            out int _,
            out int _,
            out long _,
            out long _,
            out long _,
            out long _);

        CollectionAssert.AreEqual(new[] { "2.5", "3", "12" }, result.Select((PlaylistDetailRow row) => row.Level).ToArray());
        CollectionAssert.AreEqual(new[] { "TwoPointFive", "Three", "Twelve" }, result.Select((PlaylistDetailRow row) => row.Title).ToArray());
        Assert.AreEqual("level_mixed_double", sortProfile);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_NormalizesKeywordAndFolderForDedup()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.cSortParameters sortParameters = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };

        MainWindowViewModel.PlaylistRequestIdentity left = MainWindowViewModel.CreatePlaylistRequestIdentity(table, " FolderA ", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, " keyword ", MainWindowViewModel.ModeFilterType._7KEYS, sortParameters, libraryIndexVersion: 10, playlistRevision: 20, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity right = MainWindowViewModel.CreatePlaylistRequestIdentity(table, "FolderA", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "KEYWORD", MainWindowViewModel.ModeFilterType._7KEYS, sortParameters, libraryIndexVersion: 10, playlistRevision: 20, hasResolvedSelection: true);

        Assert.AreEqual(left, right);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentPlaylistRevisionBreaksDedup()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 5, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DistinguishesRootPlaylistAndEmptyFolderNode()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity rootPlaylist = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity emptyFolder = MainWindowViewModel.CreatePlaylistRequestIdentity(table, string.Empty, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, hasResolvedSelection: true);

        Assert.AreNotEqual(rootPlaylist, emptyFolder);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_SynchronizeEditableSnapshot_UpdatesEditableFieldsAndSearchText()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Editable", 7, memo: "old memo", comment: "old comment", entryLevel: 3);
        PlaylistDetailRow editedRow = sourceRow.CreateViewRow();

        editedRow.comment = "new comment";
        editedRow.memo = "new memo";
        editedRow.Level = "12.5";
        editedRow.Url = new Uri("https://example.com/original");
        editedRow.Url_diff = new Uri("https://example.com/diff");

        sourceRow.SynchronizeEditableSnapshot(editedRow);

        Assert.AreEqual("new comment", sourceRow.comment);
        Assert.AreEqual("new memo", sourceRow.memo);
        Assert.AreEqual("12.5", sourceRow.Level);
        Assert.AreEqual(12.5, sourceRow.EntryLevelSortKey);
        Assert.AreEqual(new Uri("https://example.com/original"), sourceRow.Url);
        Assert.AreEqual(new Uri("https://example.com/diff"), sourceRow.Url_diff);
        StringAssert.Contains(sourceRow.SearchText, "NEW MEMO");
        StringAssert.Contains(sourceRow.SearchText, "NEW COMMENT");
    }

    [TestMethod]
    public void ShouldRebuildRegularFolderStage_WhenIncrementalRegularUpdateHasMissingCaches_ReturnsTrue()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldRebuildRegularFolderStageForTest(
            81,
            hasFolderView: false,
            hasKeywordView: true,
            hasModeView: true,
            currentTreeMode: 17));
        Assert.IsTrue(MainWindowViewModel.ShouldRebuildRegularFolderStageForTest(
            65,
            hasFolderView: true,
            hasKeywordView: false,
            hasModeView: true,
            currentTreeMode: 17));
        Assert.IsFalse(MainWindowViewModel.ShouldRebuildRegularFolderStageForTest(
            81,
            hasFolderView: true,
            hasKeywordView: true,
            hasModeView: true,
            currentTreeMode: 17));
    }

    [TestMethod]
    public void ResolvePlaylistColumnSettingMode_ReturnsPlaylistViewModesForBothPlaylistFilters()
    {
        Assert.AreEqual(
            1,
            MainWindowViewModel.ResolvePlaylistColumnSettingModeForTest((int)MainWindowViewModel.PlaylistFilterType.PlaylistFilter));
        Assert.AreEqual(
            2,
            MainWindowViewModel.ResolvePlaylistColumnSettingModeForTest((int)MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected));
    }

    private static PlaylistDetailSourceRow CreateSourceRow(string hash, string title, int? mode, string memo = "", string comment = "", double? entryLevel = null)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot(hash, title, mode);
        BMSTableEntry entry = new BMSTableEntry(file)
        {
            memo = memo,
            comment = comment,
            level = entryLevel
        };
        return new PlaylistDetailSourceRow(entry, file);
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
