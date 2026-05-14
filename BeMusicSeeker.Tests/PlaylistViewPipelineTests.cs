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
        Assert.IsTrue(before.SourceIdentity.EqualsIgnoringChartInfoIndex(after.SourceIdentity));
    }

    [TestMethod]
    public void PlaylistIdentity_KeywordModeAndSortOnlyChangePresentationIdentity()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.cSortParameters titleAscending = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Ascending
        };
        MainWindowViewModel.cSortParameters titleDescending = new MainWindowViewModel.cSortParameters
        {
            ColumnsName = nameof(BMSFile.Title),
            Direction = ListSortDirection.Descending
        };

        MainWindowViewModel.PlaylistRequestIdentity before = MainWindowViewModel.CreatePlaylistRequestIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "alpha", MainWindowViewModel.ModeFilterType.All, titleAscending, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = MainWindowViewModel.CreatePlaylistRequestIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "beta", MainWindowViewModel.ModeFilterType._7KEYS, titleDescending, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreEqual(before.SourceIdentity, after.SourceIdentity);
        Assert.AreNotEqual(before.PresentationIdentity, after.PresentationIdentity);
        Assert.AreNotEqual(before, after);
    }

    [TestMethod]
    public void PlaylistIdentity_SourceVersionsOnlyChangeSourceIdentity()
    {
        BMSTable table = new BMSTable();
        MainWindowViewModel.PlaylistRequestIdentity before = MainWindowViewModel.CreatePlaylistRequestIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "keyword", MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 3, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);
        MainWindowViewModel.PlaylistRequestIdentity after = MainWindowViewModel.CreatePlaylistRequestIdentity(table, "Folder", MainWindowViewModel.PlaylistFilterType.PlaylistFilter, "keyword", MainWindowViewModel.ModeFilterType.All, sortParameters: null, libraryIndexVersion: 4, playlistRevision: 4, scoreSnapshotVersion: 5, chartInfoIndexVersion: 6, hasResolvedSelection: true);

        Assert.AreNotEqual(before.SourceIdentity, after.SourceIdentity);
        Assert.AreEqual(before.PresentationIdentity, after.PresentationIdentity);
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
        Assert.AreEqual("abababababababababababababababababababababababababababababababab", GridRowResolver.GetRepositorySha256(row));
    }

    [TestMethod]
    public void GridRowResolver_GetRepositorySha256_UsesBmsFileChartInfoFallback()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "ChartInfoSha", 7);
        file.SetChartInfo(CreateChartInfo(new string('d', 64), file.hash));

        Assert.AreEqual(new string('d', 64), GridRowResolver.GetRepositorySha256(file));
    }

    [TestMethod]
    public void GridRowResolver_GetRepositorySha256_ReturnsNullWhenMissing()
    {
        PlaylistDetailSourceRow sourceRow = CreateSourceRow("cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd", "NoSha", 7);

        Assert.IsNull(GridRowResolver.GetRepositorySha256(sourceRow.CreateViewRow()));
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
        Assert.AreEqual(bmson.sha256, GridRowResolver.GetRepositorySha256(row));
        Assert.AreEqual(ClearType.NO_PLAY, row.clear);
        Assert.AreEqual(RankType.INVALID, row.rank);
        Assert.IsNull(row.score);
        Assert.AreEqual(BMSFile.BMSFileStatus.NONE, row.status);
        Assert.IsNull(GridRowResolver.GetOperationBmsFile(row));
    }

    [TestMethod]
    public void ChartOperationTarget_PlaylistOwnedBmson_IsOwnedWithoutOperationBmsFile()
    {
        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            level = 11,
            mode_hint = "beat-7k",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64)
        };
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: bmson).CreateViewRow();

        Assert.IsNull(GridRowResolver.GetOperationBmsFile(row));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(OwnedChartKind.Bmson, target.Chart.Kind);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPlaylistMissing);
        Assert.AreSame(bmson, target.Chart.BmsonSong);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseScoreViewer));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunZeroNoteCheck));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void ChartOperationTarget_PlaylistOwnedBms_HasBmsOnlyAndLocalCapabilities()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        file.SetSha256(new string('a', 64));
        TestablePlaylistEntry entry = new TestablePlaylistEntry(file);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, file).CreateViewRow();

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(OwnedChartKind.Bms, target.Chart.Kind);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPlaylistMissing);
        Assert.AreSame(file, target.Chart.BmsFile);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseScoreViewer));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateRanking));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunZeroNoteCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.ConvertToAudio));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
    }

    [TestMethod]
    public void ChartOperationTarget_RegularBmson_DisablesBmsOnlyCapabilities()
    {
        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            sha256 = new string('f', 64)
        };
        PendingChartEntry row = PendingChartEntry.CreateFromBmsonSong(bmson);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(OwnedChartKind.Bmson, target.Chart.Kind);
        Assert.AreSame(row, target.Chart.BmsFile);
        Assert.AreSame(bmson, target.Chart.BmsonSong);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UpdateRanking));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.ConvertToAudio));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
    }

    [TestMethod]
    public void ChartOperationTarget_LibraryChartRowBmson_DisablesBmsOnlyCapabilities()
    {
        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            md5 = "12121212121212121212121212121212",
            sha256 = new string('1', 64)
        };
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(bmson);

        Assert.IsNotInstanceOfType(row, typeof(PendingChartEntry));
        Assert.IsNull(GridRowResolver.GetOperationBmsFile(row));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(OwnedChartKind.Bmson, target.Chart.Kind);
        Assert.AreSame(bmson, target.Chart.BmsonSong);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
    }

    [TestMethod]
    public void LibraryChartRow_FromPendingBmson_PreservesPendingInstallState()
    {
        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Bmson\\chart.bmson",
            folder = "C:\\Pending\\Bmson",
            title = "Pending Bmson",
            artist = "Artist",
            level = 9,
            mode_hint = "beat-7k",
            md5 = "34343434343434343434343434343434",
            sha256 = new string('3', 64)
        };
        bmson.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(bmson.path, bmson.md5);
        bmson.MaintenanceInfo.wav_files_defined = 4;
        bmson.MaintenanceInfo.wav_files_existing = 1;

        PendingChartEntry pending = PendingChartEntry.CreateFromBmsonSong(bmson);
        pending.SetWarning(ChartWarningKind.AlreadyInstalled, "installed chart warning");
        pending.instl_dst = "C:\\Library\\Destination";
        LibraryChartRow row = LibraryChartRow.FromBmsFile(pending);

        Assert.AreSame(pending, row.BmsFile);
        Assert.AreSame(bmson, row.BmsonSong);
        Assert.AreEqual(OwnedChartKind.Bmson, row.Chart.Kind);
        Assert.AreSame(pending, row.Chart.BmsFile);
        Assert.AreSame(bmson, row.Chart.BmsonSong);
        Assert.AreSame(pending, GridRowResolver.GetOperationBmsFile(row));
        Assert.AreEqual(pending.DisplayWarning, row.DisplayWarning);
        Assert.AreEqual(pending.WarningDigestText, row.WarningDigestText);
        Assert.AreEqual(pending.WarningTooltipText, row.WarningTooltipText);
        Assert.AreEqual(pending.instl_dst, row.instl_dst);
        Assert.AreEqual(pending.WAVHealth, row.WAVHealth);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, isPendingSection: true, out ChartOperationTarget target));
        Assert.AreEqual(OwnedChartKind.Bmson, target.Chart.Kind);
        Assert.AreSame(pending, target.Chart.BmsFile);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RunZeroNoteCheck));

        LibraryChartRef chartRef = LibraryChartRef.FromBmsFile(pending);
        BMSFile compatibilityFile = chartRef.ToCompatibilityBmsFile();
        Assert.AreSame(pending, compatibilityFile);
        Assert.AreEqual(pending.DisplayWarning, compatibilityFile.DisplayWarning);
        Assert.AreEqual(pending.WarningDigestText, compatibilityFile.WarningDigestText);
        Assert.AreEqual(pending.WarningTooltipText, compatibilityFile.WarningTooltipText);
        Assert.AreEqual(pending.instl_dst, compatibilityFile.instl_dst);
    }

    [TestMethod]
    public void ChartOperationTarget_PendingBmson_UsesPendingPathAndPendingCapabilities()
    {
        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Original\\Bmson\\chart.bmson",
            folder = "C:\\Original\\Bmson",
            title = "Pending Bmson",
            artist = "Artist",
            md5 = "56565656565656565656565656565656",
            sha256 = new string('5', 64)
        };
        PendingChartEntry pending = PendingChartEntry.CreateFromBmsonSong(bmson);
        pending.path = "C:\\Pending\\Package\\chart.bmson";
        pending.folder = "C:\\Pending\\Package";
        LibraryChartRow row = LibraryChartRow.FromBmsFile(pending);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, target.SourceScope);
        Assert.AreEqual(OwnedChartKind.Bmson, target.Chart.Kind);
        Assert.AreEqual(pending.path, target.Chart.Path);
        Assert.IsFalse(target.IsOwned);
        Assert.IsTrue(target.IsPending);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
    }

    [TestMethod]
    public void MainViewOperationContext_MapsViewModeToRowOperationScope()
    {
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected,
            MainWindowViewModel.MainViewOperationSection.InstallPending,
            ChartOperationSourceScope.PendingPackage);
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.NewlyInstalledFolderSelected,
            MainWindowViewModel.MainViewOperationSection.InstallInstalled,
            ChartOperationSourceScope.NewlyInstalledPackage);
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.PlaylistFilterSelected,
            MainWindowViewModel.MainViewOperationSection.Playlist,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.PlaylistNotOwnedFilterSelected,
            MainWindowViewModel.MainViewOperationSection.Playlist,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.FullScanAllChartsFilterSelected,
            MainWindowViewModel.MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected,
            MainWindowViewModel.MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.FileMissingIgnoredFilterSelected,
            MainWindowViewModel.MainViewOperationSection.FullScanCheck,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.ChartInfoParseErrorFilterSelected,
            MainWindowViewModel.MainViewOperationSection.ChartInfoParseError,
            ChartOperationSourceScope.Library);
        AssertMainViewOperationContext(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            MainWindowViewModel.MainViewOperationSection.Library,
            ChartOperationSourceScope.Library);
    }

    [TestMethod]
    public void MainViewOperationContext_PendingModeMakesLibraryChartRowPendingScoped()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Pending Bms", 7);
        LibraryChartRow row = LibraryChartRow.FromBmsFile(file);
        ChartOperationSourceScope sourceScope = MainWindowViewModel.ResolveMainViewChartOperationSourceScope(
            MainWindowViewModel.ResolveMainViewOperationSection(MainWindowViewModel.viewUpdateMode.PendingInstallFolderSelected));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, sourceScope, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, target.SourceScope);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void DeleteTargetResolver_NewlyInstalledRoutesToLibrary()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.NewlyInstalledPackage, ChartOperationCapabilities.RemoveFromLibrary);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            new[] { target },
            target,
            MainWindowViewModel.MainViewOperationSection.InstallInstalled);

        Assert.AreEqual(ChartDeleteRoute.Library, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
        Assert.IsFalse(resolution.UsedContextFallback);
    }

    [TestMethod]
    public void DeleteTargetResolver_PendingRoutesToPending()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.PendingPackage, ChartOperationCapabilities.UpdateInstallDestination);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            new[] { target },
            target,
            MainWindowViewModel.MainViewOperationSection.InstallPending);

        Assert.AreEqual(ChartDeleteRoute.Pending, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
    }

    [TestMethod]
    public void DeleteTargetResolver_UsesContextFallbackWhenSelectionIsEmpty()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.Library, ChartOperationCapabilities.RemoveFromLibrary);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            Array.Empty<ChartOperationTarget>(),
            target,
            MainWindowViewModel.MainViewOperationSection.Library);

        Assert.AreEqual(ChartDeleteRoute.Library, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(target, resolution.Targets[0]);
        Assert.IsTrue(resolution.UsedContextFallback);
    }

    [TestMethod]
    public void DeleteTargetResolver_PlaylistMissingIsNotFileDeleteTarget()
    {
        ChartOperationTarget target = CreateDeleteTarget(ChartOperationSourceScope.PlaylistMissing, ChartOperationCapabilities.None);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            new[] { target },
            target,
            MainWindowViewModel.MainViewOperationSection.Playlist);

        Assert.AreEqual(ChartDeleteRoute.None, resolution.Route);
        Assert.AreEqual(0, resolution.Targets.Count);
    }

    [TestMethod]
    public void DeleteTargetResolver_ContextScopeFiltersMixedSelection()
    {
        ChartOperationTarget libraryTarget = CreateDeleteTarget(ChartOperationSourceScope.Library, ChartOperationCapabilities.RemoveFromLibrary);
        ChartOperationTarget pendingTarget = CreateDeleteTarget(ChartOperationSourceScope.PendingPackage, ChartOperationCapabilities.UpdateInstallDestination);

        ChartDeleteTargetResolution resolution = ChartDeleteTargetResolver.Resolve(
            new[] { libraryTarget, pendingTarget },
            pendingTarget,
            MainWindowViewModel.MainViewOperationSection.InstallPending);

        Assert.AreEqual(ChartDeleteRoute.Pending, resolution.Route);
        Assert.AreEqual(1, resolution.Targets.Count);
        Assert.AreSame(pendingTarget, resolution.Targets[0]);
        Assert.AreEqual(1, resolution.MixedScopeDroppedCount);
    }

    [TestMethod]
    public void ChartOperationTarget_NewlyInstalledBmson_UsesInstalledPathAndLibraryCapabilities()
    {
        LR2SongDBExtended.bmson_song original = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Package\\chart.bmson",
            folder = "C:\\Pending\\Package",
            title = "Installed Bmson",
            artist = "Artist",
            md5 = "67676767676767676767676767676767",
            sha256 = new string('6', 64)
        };
        PendingChartEntry pending = PendingChartEntry.CreateFromBmsonSong(original);
        LR2SongDBExtended.bmson_song installed = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Library\\Package\\chart.bmson",
            folder = "C:\\Library\\Package",
            title = original.title,
            artist = original.artist,
            md5 = original.md5,
            sha256 = original.sha256
        };
        pending.ReplaceBmsonSongReferenceAfterInstall(installed);
        LibraryChartRow row = LibraryChartRow.FromBmsFile(pending);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.NewlyInstalledPackage, out ChartOperationTarget target));

        Assert.AreEqual(ChartOperationSourceScope.NewlyInstalledPackage, target.SourceScope);
        Assert.IsTrue(target.IsOwned);
        Assert.IsFalse(target.IsPending);
        Assert.AreEqual(installed.path, target.Chart.Path);
        Assert.AreSame(installed, target.Chart.BmsonSong);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.AreEqual(installed.path, LibraryChartRef.FromBmsFile(pending).Path);
    }

    [TestMethod]
    public void ChartOperationTarget_BmsRow_HasBmsOnlyCapabilities()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Bms", 7);
        file.SetSha256(new string('a', 64));

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(file, out ChartOperationTarget target));
        Assert.AreEqual(OwnedChartKind.Bms, target.Chart.Kind);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseScoreViewer));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateRanking));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunZeroNoteCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.ConvertToAudio));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
    }

    [TestMethod]
    public void ChartOperationTarget_CapabilityMatrix_SeparatesBmsOnlyAndBmsonCommonOperations()
    {
        TestableBmsFile bms = new TestableBmsFile();
        bms.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Bms", 7);
        bms.SetSha256(new string('a', 64));
        LibraryChartRow bmson = LibraryChartRow.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Bmson",
            artist = "Artist",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        });
        PendingChartEntry pendingBmson = PendingChartEntry.CreateFromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Bmson\\chart.bmson",
            folder = "C:\\Pending\\Bmson",
            title = "Pending Bmson",
            artist = "Artist",
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('c', 64)
        });

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(bms, out ChartOperationTarget bmsTarget));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(bmson, out ChartOperationTarget bmsonTarget));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(LibraryChartRow.FromBmsFile(pendingBmson), ChartOperationSourceScope.PendingPackage, out ChartOperationTarget pendingBmsonTarget));

        foreach (ChartOperationCapabilities commonCapability in new[]
        {
            ChartOperationCapabilities.OpenFile,
            ChartOperationCapabilities.OpenFolder,
            ChartOperationCapabilities.OpenRepositoryBySha256,
            ChartOperationCapabilities.RunResourceHealthCheck
        })
        {
            Assert.IsTrue(bmsTarget.HasCapability(commonCapability), commonCapability + " should apply to BMS.");
            Assert.IsTrue(bmsonTarget.HasCapability(commonCapability), commonCapability + " should apply to owned bmson.");
            Assert.IsTrue(pendingBmsonTarget.HasCapability(commonCapability), commonCapability + " should apply to pending bmson.");
        }

        foreach (ChartOperationCapabilities bmsOnlyCapability in new[]
        {
            ChartOperationCapabilities.UseLr2Ir,
            ChartOperationCapabilities.UseScoreViewer,
            ChartOperationCapabilities.UpdateRanking,
            ChartOperationCapabilities.RunBmsEncodingFix,
            ChartOperationCapabilities.RunZeroNoteCheck,
            ChartOperationCapabilities.RenameInvalidExtension,
            ChartOperationCapabilities.ConvertToAudio
        })
        {
            Assert.IsTrue(bmsTarget.HasCapability(bmsOnlyCapability), bmsOnlyCapability + " should apply to BMS.");
            Assert.IsFalse(bmsonTarget.HasCapability(bmsOnlyCapability), bmsOnlyCapability + " must not apply to owned bmson.");
            Assert.IsFalse(pendingBmsonTarget.HasCapability(bmsOnlyCapability), bmsOnlyCapability + " must not apply to pending bmson.");
        }

        Assert.IsTrue(bmsonTarget.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsTrue(bmsonTarget.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(bmsonTarget.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsTrue(pendingBmsonTarget.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsFalse(pendingBmsonTarget.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsFalse(pendingBmsonTarget.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void ChartOperationTarget_PendingBms_SeparatesInstallDestinationFromInstalledRepair()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Pending Bms", 7);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(file, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));

        Assert.AreEqual(OwnedChartKind.Bms, target.Chart.Kind);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunResourceHealthCheck));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RunBmsEncodingFix));
    }

    [TestMethod]
    public void ChartOperationTarget_ChartNamedApis_MatchCompatibilityBmsShim()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Bms", 7);

        Assert.IsTrue(GridRowResolver.TryGetChartRef(file, out OwnedChartRef chart));
        Assert.AreEqual(OwnedChartKind.Bms, chart.Kind);
        Assert.AreSame(file, chart.BmsFile);

        Assert.IsTrue(GridRowResolver.TryGetOperationChartTarget(file, out ChartOperationTarget target));
        Assert.AreEqual(OwnedChartKind.Bms, target.Chart.Kind);
        Assert.AreSame(file, GridRowResolver.GetOperationBmsFile(file));
        Assert.AreSame(file, target.Chart.BmsFile);
    }

    private static void AssertMainViewOperationContext(
        MainWindowViewModel.viewUpdateMode mode,
        MainWindowViewModel.MainViewOperationSection expectedSection,
        ChartOperationSourceScope expectedScope)
    {
        MainWindowViewModel.MainViewOperationSection section = MainWindowViewModel.ResolveMainViewOperationSection(mode);

        Assert.AreEqual(expectedSection, section);
        Assert.AreEqual(expectedScope, MainWindowViewModel.ResolveMainViewChartOperationSourceScope(section));
    }

    [TestMethod]
    public void ChartOperationTarget_PlaylistMissing_DoesNotAllowLocalFileOperations()
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetTitle("Missing");
        entry.SetMd5("abababababababababababababababab");
        entry.lr2_bmsid = "12345";
        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: null).CreateViewRow();

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(OwnedChartKind.Bms, target.Chart.Kind);
        Assert.IsFalse(target.IsOwned);
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.OpenFile));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.OpenFolder));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RepairInstalledLocation));
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UseLr2Ir));
    }

    [TestMethod]
    public void PlaylistDetailContextMenuPolicy_OwnedRowsAllowEntryAndFileDeleteButMissingRowsAllowEntryOnly()
    {
        TestableBmsFile bms = new TestableBmsFile();
        bms.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        PlaylistDetailRow ownedBmsRow = new PlaylistDetailSourceRow(new TestablePlaylistEntry(bms), bms).CreateViewRow();

        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        TestablePlaylistEntry bmsonEntry = new TestablePlaylistEntry();
        bmsonEntry.SetMd5(bmson.md5);
        bmsonEntry.SetSha256(bmson.sha256);
        PlaylistDetailRow ownedBmsonRow = new PlaylistDetailSourceRow(bmsonEntry, realFile: null, resolvedBmson: bmson).CreateViewRow();

        TestablePlaylistEntry missingEntry = new TestablePlaylistEntry();
        missingEntry.SetMd5("cccccccccccccccccccccccccccccccc");
        PlaylistDetailRow missingRow = new PlaylistDetailSourceRow(missingEntry, realFile: null, resolvedBmson: null).CreateViewRow();

        AssertPlaylistRowFileDeletePolicy(ownedBmsRow, expectedRemoveFromLibrary: true);
        AssertPlaylistRowFileDeletePolicy(ownedBmsonRow, expectedRemoveFromLibrary: true);
        AssertPlaylistRowFileDeletePolicy(missingRow, expectedRemoveFromLibrary: false);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_AfterBmsRemoval_RematerializesEntryAsMissingNoSong()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "Owned Bms", 7);
        TestablePlaylistEntry entry = new TestablePlaylistEntry(file);

        PlaylistDetailSourceRow ownedSource = new PlaylistDetailSourceRow(entry, file);
        PlaylistDetailSourceRow missingSource = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: null);
        PlaylistDetailRow missingRow = missingSource.CreateViewRow();

        Assert.IsTrue(ownedSource.IsOwned);
        Assert.IsFalse(missingSource.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, missingSource.clear);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(missingRow, out ChartOperationTarget target));
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_AfterBmsonRemoval_RematerializesEntryAsMissingNoSong()
    {
        LR2SongDBExtended.bmson_song bmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Songs\\Bmson\\chart.bmson",
            folder = "C:\\Songs\\Bmson",
            title = "Owned Bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('d', 64)
        };
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetMd5(bmson.md5);
        entry.SetSha256(bmson.sha256);

        PlaylistDetailSourceRow ownedSource = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: bmson);
        PlaylistDetailSourceRow missingSource = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: null);
        PlaylistDetailRow missingRow = missingSource.CreateViewRow();

        Assert.IsTrue(ownedSource.IsOwned);
        Assert.IsFalse(missingSource.IsOwned);
        Assert.AreEqual(ClearType.NO_SONG, missingSource.clear);
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(missingRow, out ChartOperationTarget target));
        Assert.IsTrue(target.IsPlaylistMissing);
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
    }

    [TestMethod]
    public void BmsonSongsChangedRefreshPolicy_RefreshesPlaylistDetailModes()
    {
        Assert.IsTrue(MainWindowViewModel.ShouldRefreshPlaylistViewAfterBmsonSongsChangedForTest((int)MainWindowViewModel.PlaylistFilterType.PlaylistFilter));
        Assert.IsTrue(MainWindowViewModel.ShouldRefreshPlaylistViewAfterBmsonSongsChangedForTest((int)MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected));
        Assert.IsFalse(MainWindowViewModel.ShouldRefreshPlaylistViewAfterBmsonSongsChangedForTest(17));
    }

    [TestMethod]
    public void ChartOperationTarget_MissingSha256_DisablesRepositoryCapability()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("abababababababababababababababab", "NoSha", 7);

        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(file, out ChartOperationTarget target));
        Assert.IsFalse(target.HasCapability(ChartOperationCapabilities.OpenRepositoryBySha256));
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
        Assert.AreEqual(chartInfo.sha256, sourceRow.sha256);
        Assert.AreEqual(chartInfo.sha256, GridRowResolver.GetRepositorySha256(row));
        Assert.AreEqual("12", row.ChartLevelText);
        Assert.AreEqual("ANOTHER", row.ChartDifficultyText);
        Assert.AreEqual(2500, row.ChartNotes);
        Assert.AreEqual("777.5", row.ChartTotalText);
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_MissingWithChartInfoFallback_UsesChartInfoSha256()
    {
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(new string('a', 64), "abababababababababababababababab");
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetTitle("MissingShaFallback");
        entry.SetMd5(chartInfo.md5);

        PlaylistDetailRow row = new PlaylistDetailSourceRow(entry, realFile: null, resolvedBmson: null, entryChartInfo: chartInfo).CreateViewRow();

        Assert.AreEqual(chartInfo.sha256, row.sha256);
        Assert.AreEqual(chartInfo.sha256, GridRowResolver.GetRepositorySha256(row));
    }

    [TestMethod]
    public void PlaylistDetailSourceRow_MissingEntryChartInfoCanBePatchedForViewRematerialize()
    {
        LR2SongDBExtended.chart_info oldInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 3, notes: 500, total: 100);
        LR2SongDBExtended.chart_info newInfo = CreateChartInfo(new string('e', 64), "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", level: 12, notes: 2500, total: 500);
        PlaylistDetailSourceRow sourceRow = CreateMissingSourceRow("PatchChartInfo", oldInfo);

        Assert.IsTrue(sourceRow.HasEntryChartInfoDependency);
        Assert.AreEqual("3", sourceRow.ChartLevelText);
        Assert.AreEqual(500, sourceRow.ChartNotes);

        Assert.IsTrue(sourceRow.SetEntryChartInfo(newInfo));

        PlaylistDetailRow viewRow = sourceRow.CreateViewRow();
        Assert.AreSame(newInfo, sourceRow.EntryChartInfo);
        Assert.AreSame(newInfo, sourceRow.ChartInfo);
        Assert.AreEqual(newInfo.sha256, viewRow.sha256);
        Assert.AreEqual(newInfo.sha256, GridRowResolver.GetRepositorySha256(viewRow));
        Assert.AreEqual("12", viewRow.ChartLevelText);
        Assert.AreEqual(2500, viewRow.ChartNotes);
        Assert.AreEqual("500", viewRow.ChartTotalText);
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
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesRealFileSha256ForMd5OnlyEntry()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("cccccccccccccccccccccccccccccccc", "Owned", 7);
        file.SetSha256(new string('a', 64));
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetMd5(file.hash);
        BMSScore score = new BMSScore
        {
            hash = file.sha256,
            clear = ClearType.HARD,
            perfect = 800,
            great = 100,
            totalnotes = 1000
        };
        BMSLibrary.ScoreSnapshot snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [file.sha256] = score
            }
        };

        BMSScore resolved = MainWindowViewModel.ResolvePlaylistEntryScoreSnapshotForTest(
            entry,
            file,
            entryChartInfo: null,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(file.hash, resolved.hash);
        Assert.AreEqual(ClearType.HARD, resolved.clear);
        Assert.AreEqual(1700, resolved.score);
    }

    [TestMethod]
    public void ResolvePlaylistEntryScoreSnapshot_BeatorajaUsesEntryChartInfoSha256ForMissingEntry()
    {
        TestablePlaylistEntry entry = new TestablePlaylistEntry();
        entry.SetMd5("dddddddddddddddddddddddddddddddd");
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(new string('b', 64), entry.md5);
        BMSScore score = new BMSScore
        {
            hash = chartInfo.sha256,
            clear = ClearType.EX_HARD,
            perfect = 600,
            great = 50,
            totalnotes = 800
        };
        BMSLibrary.ScoreSnapshot snapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [chartInfo.sha256] = score
            }
        };

        BMSScore resolved = MainWindowViewModel.ResolvePlaylistEntryScoreSnapshotForTest(
            entry,
            realFile: null,
            entryChartInfo: chartInfo,
            snapshot,
            scoresByHash: new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
            scoresBySha256: snapshot.ScoresBySha256);

        Assert.IsNotNull(resolved);
        Assert.AreEqual(entry.md5, resolved.hash);
        Assert.AreEqual(ClearType.EX_HARD, resolved.clear);
        Assert.AreEqual(1250, resolved.score);
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
    public void BuildStandardLibraryRowsForView_MixesBmsAndBmsonRowsAndAppliesFolderFilter()
    {
        TestableBmsFile keepBms = new TestableBmsFile();
        keepBms.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Keep Bms", 7);
        keepBms.path = "C:\\Keep\\bms\\chart.bms";
        TestableBmsFile skipBms = new TestableBmsFile();
        skipBms.ApplySnapshot("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Skip Bms", 7);
        skipBms.path = "C:\\Skip\\bms\\chart.bms";
        LibraryChartRow keepBmson = LibraryChartRow.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Keep\\bmson\\chart.bmson",
            folder = "C:\\Keep\\bmson",
            title = "Keep Bmson",
            artist = "Artist",
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('c', 64)
        });
        LibraryChartRow skipBmson = LibraryChartRow.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Skip\\bmson\\chart.bmson",
            folder = "C:\\Skip\\bmson",
            title = "Skip Bmson",
            artist = "Artist",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('d', 64)
        });

        List<LibraryChartRow> rows = MainWindowViewModel.BuildStandardLibraryRowsForView(
            new[] { keepBms, skipBms },
            new[] { keepBmson, skipBmson },
            file => file.path.StartsWith("C:\\Keep", StringComparison.OrdinalIgnoreCase),
            out LibraryRowsBuildMetrics metrics);

        Assert.AreEqual(2, rows.Count);
        CollectionAssert.AreEquivalent(new[] { "Keep Bms", "Keep Bmson" }, rows.Select((LibraryChartRow row) => row.Title).ToArray());
        Assert.IsTrue(rows.Any((LibraryChartRow row) => row.Chart.Kind == OwnedChartKind.Bms && row.BmsFile == keepBms));
        Assert.IsTrue(rows.Any((LibraryChartRow row) => row.Chart.Kind == OwnedChartKind.Bmson && row.BmsonSong == keepBmson.BmsonSong));
        Assert.IsFalse(rows.Any((LibraryChartRow row) => row.Title == "Skip Bms" || row.Title == "Skip Bmson"));
        Assert.IsTrue(rows.All((LibraryChartRow row) => row.GetType() == typeof(LibraryChartRow)));
        Assert.IsTrue(metrics.FolderFilterApplied);
        Assert.AreEqual(2, metrics.SourceBmsCount);
        Assert.AreEqual(2, metrics.SourceBmsonCount);
        Assert.AreEqual(1, metrics.FilteredBmsCount);
        Assert.AreEqual(1, metrics.FilteredBmsonCount);
        Assert.AreEqual(2, metrics.FolderCount);
        Assert.IsTrue(metrics.FolderMs >= 0);
    }

    [TestMethod]
    public void NormalLibraryRowCache_ReusesRowsAndPrunesRemovedFiles()
    {
        TestableBmsFile fileA = new TestableBmsFile();
        fileA.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", 7);
        TestableBmsFile fileB = new TestableBmsFile();
        fileB.ApplySnapshot("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "B", 7);
        NormalLibraryRowCache cache = new NormalLibraryRowCache();
        LibraryRowCacheBuildStats firstStats = new LibraryRowCacheBuildStats();
        LibraryChartRow firstA = cache.GetOrCreate(fileA, firstStats);
        LibraryChartRow firstB = cache.GetOrCreate(fileB, firstStats);
        LibraryRowCacheBuildStats secondStats = new LibraryRowCacheBuildStats();
        LibraryChartRow secondA = cache.GetOrCreate(fileA, secondStats);

        Assert.AreSame(firstA, secondA);
        Assert.AreEqual(0, firstStats.HitCount);
        Assert.AreEqual(2, firstStats.MissCount);
        Assert.AreEqual(1, secondStats.HitCount);
        Assert.AreEqual(0, secondStats.MissCount);
        Assert.AreEqual(1, cache.Prune(new[] { fileA }));
        Assert.AreEqual(1, cache.Count);
        fileB.SetTitle("B2");
        fileA.SetTitle("A2");
        Assert.AreSame(firstA, cache.GetOrCreate(fileA, new LibraryRowCacheBuildStats()));
    }

    [TestMethod]
    public void BuildStandardLibraryRowsForView_UsesProvidedBmsRowFactoryAndReportsCacheMetrics()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", 7);
        LibraryChartRow cachedRow = LibraryChartRow.FromBmsFile(file);
        LibraryRowCacheBuildStats stats = new LibraryRowCacheBuildStats
        {
            HitCount = 1,
            PrunedCount = 2
        };

        List<LibraryChartRow> rows = MainWindowViewModel.BuildStandardLibraryRowsForView(
            new[] { file },
            Array.Empty<LibraryChartRow>(),
            null,
            _ => cachedRow,
            stats,
            out LibraryRowsBuildMetrics metrics);

        Assert.AreEqual(1, rows.Count);
        Assert.AreSame(cachedRow, rows[0]);
        Assert.AreEqual(1, metrics.RegularRowCacheHitCount);
        Assert.AreEqual(0, metrics.RegularRowCacheMissCount);
        Assert.AreEqual(2, metrics.RegularRowCachePrunedCount);
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

    private static PlaylistDetailSourceRow CreateSourceRow(string hash, string title, int? mode, string memo = "", string comment = "", double? entryLevel = null, string? sha256 = null)
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

    private static void AssertPlaylistRowFileDeletePolicy(PlaylistDetailRow row, bool expectedRemoveFromLibrary)
    {
        Assert.IsTrue(GridRowResolver.IsPlaylistRow(row));
        Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(row, out ChartOperationTarget target));
        Assert.AreEqual(expectedRemoveFromLibrary, target.HasCapability(ChartOperationCapabilities.RemoveFromLibrary));
        Assert.AreEqual(expectedRemoveFromLibrary, target.HasCapability(ChartOperationCapabilities.MoveInLibrary));
    }

    private static ChartOperationTarget CreateDeleteTarget(ChartOperationSourceScope sourceScope, ChartOperationCapabilities capabilities)
    {
        OwnedChartRef chart = new OwnedChartRef(
            OwnedChartKind.Bms,
            "C:\\Library\\Song\\chart.bms",
            "abababababababababababababababab",
            null,
            "Title",
            "Artist",
            7,
            7,
            null,
            null,
            null);
        bool isPending = sourceScope == ChartOperationSourceScope.PendingPackage;
        bool isPlaylistMissing = sourceScope == ChartOperationSourceScope.PlaylistMissing;
        return new ChartOperationTarget(
            chart,
            null,
            sourceScope,
            !isPending && !isPlaylistMissing,
            isPending,
            isPlaylistMissing,
            capabilities);
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

        public void SetTitle(string value)
        {
            Title = value;
        }
    }
}
