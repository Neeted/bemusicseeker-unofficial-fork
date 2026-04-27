using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
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
    public void ApplyPlaylistViewFromSource_KeywordFilterSupportsAndAndHashFields()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow(
            "33333333333333333333333333333333",
            "Matched",
            7,
            memo: "special memo",
            sha256: "abababababababababababababababababababababababababababababababab");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow(
            "44444444444444444444444444444444",
            "Filtered",
            7,
            memo: "special memo",
            sha256: "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");
        PlaylistDetailSourceRow[] sourceRows = new PlaylistDetailSourceRow[] { matchedRow, filteredRow };

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            sourceRows,
            keywordFilter: "title:Matched memo:special md5:333333 sha256:abab",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: new MainWindowViewModel.cSortParameters { ColumnsName = nameof(BMSFile.Title), Direction = ListSortDirection.Ascending },
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
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_UnknownFieldQueryDoesNotMatch()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow("33333333333333333333333333333333", "Matched", 7, memo: "special memo");

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            new[] { matchedRow },
            keywordFilter: "unknown:Matched",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: new MainWindowViewModel.cSortParameters { ColumnsName = nameof(BMSFile.Title), Direction = ListSortDirection.Ascending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(0, result.Count);
        Assert.AreEqual(0, keywordCount);
        Assert.AreEqual(0, modeCount);
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_KeywordFilterSupportsQuoteNegationOrAndRegex()
    {
        PlaylistDetailSourceRow matchedRow = CreateSourceRow(
            "33333333333333333333333333333333",
            "Matched Alpha",
            7,
            memo: "special memo",
            comment: "safe comment",
            sha256: "abababababababababababababababababababababababababababababababab");
        PlaylistDetailSourceRow filteredRow = CreateSourceRow(
            "44444444444444444444444444444444",
            "Filtered Alpha",
            7,
            memo: "special memo",
            comment: "ordinary comment",
            sha256: "cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd");

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            new[] { matchedRow, filteredRow },
            keywordFilter: "memo:\"special memo\" -comment:ordinary md5:333333|555555 sha256:abab|efef title:re:^matched",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: new MainWindowViewModel.cSortParameters { ColumnsName = nameof(BMSFile.Title), Direction = ListSortDirection.Ascending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Matched Alpha", result[0].Title);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
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

        MainWindowViewModel.PlaylistRequestIdentity left = MainWindowViewModel.CreatePlaylistRequestIdentity(table, " FolderA ", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, " keyword ", MainWindowViewModel.ModeFilterType._7KEYS, sortParameters, libraryIndexVersion: 10, playlistRevision: 20, scoreSnapshotVersion: 30, chartInfoIndexVersion: 40, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity right = MainWindowViewModel.CreatePlaylistRequestIdentity(table, "FolderA", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "KEYWORD", MainWindowViewModel.ModeFilterType._7KEYS, sortParameters, libraryIndexVersion: 10, playlistRevision: 20, scoreSnapshotVersion: 30, chartInfoIndexVersion: 40, hasResolvedSelection: true);

        Assert.AreEqual(left, right);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentPlaylistRevisionBreaksDedup()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 5, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentScoreSnapshotVersionBreaksDedup()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 6, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DifferentChartInfoIndexVersionBreaksDedup()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 7, hasResolvedSelection: true);

        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenOnlyScoreSnapshotChanges_ReturnsScoreSnapshotVersion()
    {
        string reason = MainWindowViewModel.DeterminePlaylistSourceInvalidationReasonForTest(
            selectionChanged: false,
            libraryIndexInvalidated: false,
            playlistRevisionInvalidated: false,
            scoreSnapshotInvalidated: true,
            chartInfoIndexInvalidated: false,
            sourceMissing: false);

        Assert.AreEqual("score_snapshot_version", reason);
    }

    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenOnlyChartInfoIndexChanges_ReturnsChartInfoIndex()
    {
        string reason = MainWindowViewModel.DeterminePlaylistSourceInvalidationReasonForTest(
            selectionChanged: false,
            libraryIndexInvalidated: false,
            playlistRevisionInvalidated: false,
            scoreSnapshotInvalidated: false,
            chartInfoIndexInvalidated: true,
            sourceMissing: false);

        Assert.AreEqual("chart_info_index", reason);
    }

    [TestMethod]
    public void DeterminePlaylistSourceInvalidationReason_WhenSelectionChanges_TakesPrecedence()
    {
        string reason = MainWindowViewModel.DeterminePlaylistSourceInvalidationReasonForTest(
            selectionChanged: true,
            libraryIndexInvalidated: true,
            playlistRevisionInvalidated: true,
            scoreSnapshotInvalidated: true,
            chartInfoIndexInvalidated: true,
            sourceMissing: true);

        Assert.AreEqual("selection_changed", reason);
    }

    [TestMethod]
    public void CreatePlaylistRequestIdentity_DistinguishesRootPlaylistAndEmptyFolderNode()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity rootPlaylist = MainWindowViewModel.CreatePlaylistRequestIdentity(table, null, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity emptyFolder = MainWindowViewModel.CreatePlaylistRequestIdentity(table, string.Empty, MainWindowViewModel.PlaylistFilterType.PlaylistFilter, null, MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

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
    public void PlaylistDetailRow_PreservesSha256SnapshotAcrossViewMaterialization()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", "ShaVisible", 7, sha256: "abababababababababababababababababababababababababababababababab");

        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.AreEqual("abababababababababababababababababababababababababababababababab", sourceRow.sha256);
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", row.sha256);
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", GridRowResolver.GetSha256(row));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_BmsonOwnedWithoutRealFile_UsesBmsonMetadata()
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.comment = "comment";
        entry.memo = "memo";
        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            subtitle = "[Another]",
            artist = "Artist",
            genre = "Genre",
            level = 11,
            mode_hint = "beat-7k",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);

        PlaylistDetailSourceRow sourceRow = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: bmson);
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.IsTrue(sourceRow.IsOwned);
        Assert.IsNull(row.RealFile);
        Assert.AreSame(bmson, row.ResolvedBmson);
        Assert.AreEqual("Bmson [Another]", row.Title);
        Assert.AreEqual("Artist", row.Artist);
        Assert.AreEqual("Genre", row.genre);
        Assert.AreEqual(7, row.mode);
        Assert.AreEqual(bmson.path, row.path);
        Assert.AreEqual(bmson.sha256, row.sha256);
        Assert.AreEqual(ClearType.NO_PLAY, row.clear);
        Assert.AreEqual(RankType.INVALID, row.rank);
        Assert.IsNull(row.score);
        Assert.AreEqual(BMSFile.BMSFileStatus.NONE, row.status);
        Assert.IsNull(GridRowResolver.GetOperationBmsFile(row));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithoutResolvedFiles_PreservesPlaylistEntryFallbackValues()
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry
        {
            folder = "EntryFolder",
            level = 12
        };
        entry.SetTitle("EntryTitle");
        entry.SetArtist("EntryArtist");
        entry.SetMd5("abababababababababababababababab");
        entry.SetSha256(new string('c', 64));

        PlaylistDetailSourceRow sourceRow = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: null);

        Assert.IsFalse(sourceRow.IsOwned);
        Assert.AreEqual("EntryTitle", sourceRow.Title);
        Assert.AreEqual("EntryArtist", sourceRow.Artist);
        Assert.AreEqual("EntryFolder", sourceRow.Folder);
        Assert.AreEqual("12", sourceRow.Level);
        Assert.AreEqual(entry.md5, sourceRow.hash);
        Assert.AreEqual(entry.sha256, sourceRow.sha256);
        Assert.AreEqual(ClearType.NO_SONG, sourceRow.clear);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithEntryChartInfo_DisplaysMetadataWithoutChangingOwnership()
    {
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(
            sha256: new string('d', 64),
            md5: "abababababababababababababababab",
            level: 12,
            notes: 2500,
            total: 777.5);
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetTitle("Missing");
        entry.SetMd5(chartInfo.md5);
        entry.SetSha256(chartInfo.sha256);

        PlaylistDetailSourceRow sourceRow = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: null, entryChartInfo: chartInfo);
        PlaylistDetailRow row = sourceRow.CreateViewRow();

        Assert.IsFalse(sourceRow.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, sourceRow.clear);
        Assert.AreSame(chartInfo, sourceRow.EntryChartInfo);
        Assert.AreSame(chartInfo, sourceRow.ChartInfo);
        Assert.AreEqual("12", row.ChartLevelText);
        Assert.AreEqual("ANOTHER", row.ChartDifficultyText);
        Assert.AreEqual(2500, row.ChartNotes);
        Assert.AreEqual("777.5", row.ChartTotalText);
    }

    [TestMethod]
    public void ResolveChartInfoForPlaylistEntry_PrefersSha256ThenFallsBackToMd5()
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetMd5("abababababababababababababababab");
        entry.SetSha256(new string('c', 64));
        LR2SongDBExtended.chart_info shaMatch = CreateChartInfo(entry.sha256, "ffffffffffffffffffffffffffffffff", level: 12);
        LR2SongDBExtended.chart_info md5Match = CreateChartInfo(new string('d', 64), entry.md5, level: 3);
        Dictionary<string, LR2SongDBExtended.chart_info> bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
        {
            [shaMatch.sha256] = shaMatch
        };
        Dictionary<string, LR2SongDBExtended.chart_info> byMd5 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
        {
            [md5Match.md5] = md5Match
        };

        Assert.AreSame(shaMatch, MainWindowViewModel.ResolveChartInfoForPlaylistEntry(entry, byMd5, bySha256));

        TestablePlaylistEntry md5OnlyEntry = new TestablePlaylistEntry();
        md5OnlyEntry.SetMd5(md5Match.md5);
        Assert.AreSame(md5Match, MainWindowViewModel.ResolveChartInfoForPlaylistEntry(md5OnlyEntry, byMd5, bySha256));
    }

    [TestMethod]
    public void ApplyPlaylistViewFromSource_MissingChartInfoParticipatesInKeywordAndNumericSort()
    {
        LR2SongDBExtended.chart_info highNotesInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 12, notes: 2500, total: 500);
        LR2SongDBExtended.chart_info lowNotesInfo = CreateChartInfo(new string('f', 64), "ffffffffffffffffffffffffffffffff", level: 3, notes: 500, total: 100);
        PlaylistDetailSourceRow highNotesRow = CreateMissingSourceRow("HighNotes", highNotesInfo);
        PlaylistDetailSourceRow lowNotesRow = CreateMissingSourceRow("LowNotes", lowNotesInfo);

        List<PlaylistDetailRow> result = MainWindowViewModel.ApplyPlaylistViewFromSource(
            new[] { highNotesRow, lowNotesRow },
            keywordFilter: "notes:>=2000 feature:random level:12",
            modeFilter: MainWindowViewModel.ModeFilterType.All,
            sortParameters: new MainWindowViewModel.cSortParameters { ColumnsName = nameof(PlaylistDetailRow.ChartNotes), Direction = ListSortDirection.Descending },
            out string _,
            out int keywordCount,
            out int modeCount,
            out long _,
            out long _,
            out long _,
            out long _);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("HighNotes", result[0].Title);
        Assert.AreEqual(2500, result[0].ChartNotes);
        Assert.AreEqual(1, keywordCount);
        Assert.AreEqual(1, modeCount);
        Assert.IsFalse(result[0].IsOwned);
    }

    [TestMethod]
    public void ShouldCreatePlaylistScoreProbeForTest_SkipsBmsonOwnedRows()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldCreatePlaylistScoreProbeForTest(hasRealFile: false, hasResolvedBmson: false));
        Assert.IsFalse(MainWindowViewModel.ShouldCreatePlaylistScoreProbeForTest(hasRealFile: false, hasResolvedBmson: true));
        Assert.IsFalse(MainWindowViewModel.ShouldCreatePlaylistScoreProbeForTest(hasRealFile: true, hasResolvedBmson: false));
    }

    [TestMethod]
    public void ResolveBmsonForPlaylistEntry_PrefersMd5AndRepresentativePathOrder()
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetMd5("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        entry.SetSha256(new string('f', 64));

        LR2SongDBExtended.bmson_song laterPath = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Zeta\\chart.bmson",
            md5 = entry.md5,
            sha256 = new string('1', 64)
        };
        LR2SongDBExtended.bmson_song earlierPath = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Alpha\\chart.bmson",
            md5 = entry.md5,
            sha256 = new string('2', 64)
        };
        LR2SongDBExtended.bmson_song shaOnly = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Sha\\chart.bmson",
            md5 = "99999999999999999999999999999999",
            sha256 = entry.sha256
        };

        LR2SongDBExtended.bmson_song preferred = MainWindowViewModel.ChoosePreferredBmsonRepresentative(laterPath, earlierPath);
        LR2SongDBExtended.bmson_song resolved = MainWindowViewModel.ResolveBmsonForPlaylistEntry(
            entry,
            new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase) { { entry.md5, preferred } },
            new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.OrdinalIgnoreCase) { { entry.sha256, shaOnly } });

        Assert.AreSame(earlierPath, preferred);
        Assert.AreSame(earlierPath, resolved);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_UsesScoreSnapshotForOwnedRowsBeforeGlobalHydration()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("cccccccccccccccccccccccccccccccc", "Owned", 7);
        BMSTableEntry entry = new BMSTableEntry(file);
        BMSScore score = new BMSScore
        {
            hash = file.hash,
            clear = ClearType.HARD,
            totalnotes = 1000,
            perfect = 800,
            great = 100,
            rank = RankType.AA,
            minbp = 3
        };
        PlaylistDetailSourceRow sourceRow = new PlaylistDetailSourceRow(entry, file, scoreSnapshot: score);

        Assert.AreEqual(ClearType.HARD, sourceRow.clear);
        Assert.AreEqual(RankType.AA, sourceRow.rank);
        Assert.AreEqual(1700, sourceRow.score);
        Assert.AreEqual(1000, sourceRow.totalnotes);
        Assert.AreEqual(3, sourceRow.minbp);
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

    private static PlaylistDetailSourceRow CreateSourceRow(string hash, string title, int? mode, string memo = "", string comment = "", double? entryLevel = null, string sha256 = null)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot(hash, title, mode);
        if (sha256 != null)
        {
            file.SetSha256(sha256);
        }
        TestablePlaylistEntry entry = new TestablePlaylistEntry(file)
        {
            memo = memo,
            comment = comment,
            level = entryLevel
        };
        if (sha256 != null)
        {
            entry.SetSha256(sha256);
        }
        return new PlaylistDetailSourceRow(entry, file);
    }

    private static PlaylistDetailSourceRow CreateMissingSourceRow(string title, LR2SongDBExtended.chart_info chartInfo)
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetTitle(title);
        entry.SetMd5(chartInfo.md5);
        entry.SetSha256(chartInfo.sha256);
        return new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: null, entryChartInfo: chartInfo);
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(string sha256, string md5, int? level = 12, int notes = 2500, double total = 500)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            charthash = new string('a', 64),
            level = level,
            difficulty = 4,
            difficulty_defined = true,
            mainbpm = 180,
            maxbpm = 180,
            minbpm = 120,
            length = 123000,
            mode = 7,
            judge = 100,
            feature = ChartInfoDisplayFormatter.FeatureRandom,
            notes = notes,
            n = notes,
            ln = 10,
            s = 20,
            ls = 5,
            total = total,
            total_defined = true,
            density = 10,
            peakdensity = 20,
            enddensity = 2,
            distribution = "0,1,2",
            speedchange = "0=180",
            speedchange_count = 1,
            lanenotes = "1,2,3,4,5,6,7",
            parser_version = 1,
            updated_at = DateTime.UtcNow
        };
    }

    private sealed class TestablePlaylistEntry : BMSTableEntry
    {
        public TestablePlaylistEntry()
        {
        }

        public TestablePlaylistEntry(BMSFile bmsFile)
            : base(bmsFile)
        {
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetMd5(string value)
        {
            md5 = value;
        }

        public void SetTitle(string value)
        {
            title = value;
        }

        public void SetArtist(string value)
        {
            artist = value;
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

        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }
}
