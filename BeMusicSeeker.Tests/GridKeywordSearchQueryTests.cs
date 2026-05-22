using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class GridKeywordSearchQueryTests
{
    [TestMethod]
    public void MatchesChartList_GlobalKeywordSearchesMetadataPathPlaylistAndHashes()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("genrex").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("abcdef").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("1234567890").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_UsesAndForMultipleTokens()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha artistx abcdef").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("alpha missing").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_FieldQueryLimitsSearchTarget()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:alpha artist:artistx md5:abcdef sha256:123456").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:artistx").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("memo:alpha").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_PlaylistFieldSearchesReferenceNames()
    {
        TestableBmsFile file = CreateFile();
        BMSTable[] tables =
        [
            CreateTable("Satellite sl", "★"),
            CreateTable("Second Table", "★★"),
            CreateTable("GENOSIDE", "▽")
        ];
        PlaylistReferenceIndex index = CreatePlaylistReferenceIndex(file, tables);
        LibraryChartRow libraryRow = LibraryChartRow.FromBmsFile(file);
        libraryRow.SetPlaylistReferenceDisplayProvider(row => index.Find(row.Chart));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Satellite sl\"").MatchesLibraryChartRow(libraryRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("ref:\"Second Table\"").MatchesLibraryChartRow(libraryRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("table:GENOSIDE").MatchesLibraryChartRow(libraryRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:re:^GENOSIDE$").MatchesLibraryChartRow(libraryRow));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("playlist:★").MatchesLibraryChartRow(libraryRow));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("playlist:★★").MatchesLibraryChartRow(libraryRow));

        var playlistRow = new PlaylistDetailSourceRow(
            new BMSTableEntry(file),
            ChartFileProjection.FromBmsFile(file),
            playlistReferenceDisplayProvider: chart => index.Find(chart));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Satellite sl\"").MatchesLibraryChartRow(libraryRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Second Table\"").MatchesPlaylistDetail(playlistRow));
    }

    [TestMethod]
    public void MatchesChartList_PlaylistFieldSearchesBmsonReferenceProjection()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\Bmson\chart.bmson",
            folder = "BmsonFolder",
            title = "BmsonTitle",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        };
        var table = new BMSTable
        {
            name = "Bmson Table",
            symbol = "BMSN",
            entries = [CreateBmsonPlaylistEntry(song.sha256)]
        };
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceTable(table, table.entries);
        LibraryChartRow row = LibraryChartRow.FromBmsonSong(song);
        row.SetPlaylistReferenceDisplayProvider(row => index.Find(row.hash, row.sha256));

        Assert.AreEqual("BMSN", row.RefTablesSymbols);
        Assert.AreEqual("Bmson Table", row.RefTablesNames);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Bmson Table\"").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("ref:\"Bmson Table\"").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("table:\"Bmson Table\"").MatchesLibraryChartRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("playlist:BMSN").MatchesLibraryChartRow(row));
    }

    [TestMethod]
    public void MatchesChartListSourceRow_UsesSafeIdentityFieldsWithoutLibraryChartRow()
    {
        TestableBmsFile file = CreateFile();
        BMSTable[] tables =
        [
            CreateTable("Satellite sl", "★"),
            CreateTable("GENOSIDE", "▽")
        ];
        PlaylistReferenceIndex index = CreatePlaylistReferenceIndex(file, tables);
        var sourceRow = CreateSourceRow(file, row => index.Find(row.Chart));
        var libraryRow = LibraryChartRow.FromBmsFile(file);
        libraryRow.SetPlaylistReferenceDisplayProvider(row => index.Find(row.Chart));

        var query = GridKeywordSearchQuery.Parse("alpha artist:artistx genre:genrex tag:tagx path:alpha md5:abcdef sha256:123456 playlist:GENOSIDE");

        Assert.AreEqual(sourceRow.Chart.Title, sourceRow.Title);
        Assert.AreEqual(sourceRow.Chart.Artist, sourceRow.Artist);
        Assert.AreEqual(sourceRow.Chart.Genre, sourceRow.Genre);
        Assert.AreEqual(sourceRow.Chart.Folder, sourceRow.Folder);
        Assert.AreEqual(sourceRow.Chart.Tag, sourceRow.Tag);
        Assert.AreEqual(sourceRow.Chart.Path, sourceRow.Path);
        Assert.AreEqual(sourceRow.Chart.Md5, sourceRow.Hash);
        Assert.AreEqual(sourceRow.Chart.Sha256, sourceRow.Sha256);
        Assert.IsTrue(query.MatchesChartListSourceRow(sourceRow));
        Assert.AreEqual(query.MatchesLibraryChartRow(libraryRow), query.MatchesChartListSourceRow(sourceRow));
    }

    [TestMethod]
    public void MatchesChartListSourceRow_UsesBmsonFallbackValues()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\Bmson\chart.bmson",
            folder = "BmsonFolder",
            title = "BmsonTitle",
            subtitle = "Sub",
            artist = "BmsonArtist",
            genre = "BmsonGenre",
            mode_hint = "beat-7k",
            level = 7,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        };
        var sourceRow = CreateSourceRow(song);

        var query = GridKeywordSearchQuery.Parse("title:BmsonTitle artist:BmsonArtist genre:BmsonGenre path:bmson md5:bbbb sha256:cccc");

        Assert.AreEqual(sourceRow.Chart.Title, sourceRow.Title);
        Assert.AreEqual(sourceRow.Chart.Artist, sourceRow.Artist);
        Assert.AreEqual(sourceRow.Chart.Genre, sourceRow.Genre);
        Assert.AreEqual(sourceRow.Chart.Folder, sourceRow.Folder);
        Assert.AreEqual(sourceRow.Chart.LevelText, sourceRow.Level);
        Assert.AreEqual(sourceRow.Chart.Path, sourceRow.Path);
        Assert.AreEqual(sourceRow.Chart.Mode, sourceRow.Mode);
        Assert.AreEqual(sourceRow.Chart.Level, sourceRow.LevelValue);
        Assert.AreEqual(sourceRow.Chart.Md5, sourceRow.Hash);
        Assert.AreEqual(sourceRow.Chart.Sha256, sourceRow.Sha256);
        Assert.IsTrue(query.MatchesChartListSourceRow(sourceRow));
        Assert.AreEqual(query.MatchesLibraryChartRow(LibraryChartRow.FromBmsonSong(song)), query.MatchesChartListSourceRow(sourceRow));
    }

    [TestMethod]
    public void ChartListSourceRow_StorageOwnerStateAndChartInfoFollowCurrentProvider()
    {
        TestableBmsFile file = CreateFile();
        LR2SongDBExtended.chart_info bmsChartInfo = CreateChartInfo(level: 10, sha256: file.sha256, md5: file.hash);
        var bmsSourceRow = CreateSourceRow(file, chartInfo: bmsChartInfo);

        file.SetTitleForTest("Changed Title");
        file.SetGenreForTest("Changed Genre");
        file.tag = "Changed Tag";
        file.path = @"C:\Songs\Changed\chart.bms";
        file.SetHashForTest("ffffffffffffffffffffffffffffffff");

        Assert.AreEqual("Changed Title", bmsSourceRow.Title);
        Assert.AreEqual("Changed Genre", bmsSourceRow.Genre);
        Assert.AreEqual("Changed Tag", bmsSourceRow.Tag);
        Assert.AreEqual(@"C:\Songs\Changed\chart.bms", bmsSourceRow.Path);
        Assert.AreEqual("ffffffffffffffffffffffffffffffff", bmsSourceRow.Hash);
        Assert.AreSame(bmsChartInfo, bmsSourceRow.ChartInfo);
        Assert.AreSame(bmsChartInfo, bmsSourceRow.Chart.ChartInfo);
        Assert.AreEqual(10, bmsSourceRow.ChartLevelSortKey);

        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\Bmson\chart.bmson",
            title = "BmsonTitle",
            artist = "BmsonArtist",
            mode_hint = "beat-7k",
            level = 7,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        };
        LR2SongDBExtended.chart_info bmsonChartInfo = CreateChartInfo(level: 11, sha256: song.sha256, md5: song.md5);
        var bmsonSourceRow = CreateSourceRow(song, chartInfo: bmsonChartInfo);

        song.title = "Changed Bmson";
        song.genre = "Changed Genre";
        song.level = 9;
        song.mode_hint = "beat-5k";
        song.path = @"C:\Songs\Changed\chart.bmson";
        song.md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        Assert.AreEqual("Changed Bmson", bmsonSourceRow.Title);
        Assert.AreEqual("Changed Genre", bmsonSourceRow.Genre);
        Assert.AreEqual("9", bmsonSourceRow.Level);
        Assert.AreEqual(5, bmsonSourceRow.Mode);
        Assert.AreEqual(@"C:\Songs\Changed\chart.bmson", bmsonSourceRow.Path);
        Assert.AreEqual("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", bmsonSourceRow.Hash);
        Assert.AreSame(bmsonChartInfo, bmsonSourceRow.ChartInfo);
        Assert.AreSame(bmsonChartInfo, bmsonSourceRow.Chart.ChartInfo);
        Assert.AreEqual(11, bmsonSourceRow.ChartLevelSortKey);
    }

    [TestMethod]
    public void MatchesChartListSourceRow_ScoreAndChartInfoFieldsMatchLibraryChartRow()
    {
        TestableBmsFile file = CreateFile();
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(sha256: file.sha256, md5: file.hash);
        file.SetScoreForTest(ClearType.HARD, RankType.AA, perfect: 850, great: 100, totalnotes: 1000, maxcombo: 900, minbp: 8);
        var sourceRow = CreateSourceRow(file, chartInfo: chartInfo);
        var libraryRow = LibraryChartRow.FromBmsFile(file);
        libraryRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        var query = GridKeywordSearchQuery.Parse("level:12 feature:random notes:>=2000 clear:HC rank:AA score:>=1800 bp:<10");

        Assert.IsTrue(query.MatchesChartListSourceRow(sourceRow));
        Assert.AreEqual(query.MatchesLibraryChartRow(libraryRow), query.MatchesChartListSourceRow(sourceRow));
    }

    [TestMethod]
    public void MatchesChartListSourceRow_RegexScoreAndChartInfoFieldsMatchLibraryChartRow()
    {
        TestableBmsFile file = CreateFile();
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(sha256: file.sha256, md5: file.hash);
        file.SetScoreForTest(ClearType.HARD, RankType.AA, perfect: 850, great: 100, totalnotes: 1000, maxcombo: 900, minbp: 8);
        var sourceRow = CreateSourceRow(file, chartInfo: chartInfo);
        var libraryRow = LibraryChartRow.FromBmsFile(file);
        libraryRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        var query = GridKeywordSearchQuery.Parse("feature:re:RANDOM judge:re:EASY clear:re:HARD rank:re:^AA$ score:re:^1800$ bp:re:^8$");

        Assert.IsTrue(query.MatchesChartListSourceRow(sourceRow));
        Assert.AreEqual(query.MatchesLibraryChartRow(libraryRow), query.MatchesChartListSourceRow(sourceRow));
    }

    [TestMethod]
    public void MatchesChartList_UnknownOrEmptyFieldQueryDoesNotMatch()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsFalse(GridKeywordSearchQuery.Parse("unknown:alpha").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse(":alpha").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_WindowsDriveLetterTokenIsGlobalKeyword()
    {
        TestableBmsFile file = CreateFile();
        file.path = @"D:\BMS\Alpha\chart.bms";

        var drivePathQuery = GridKeywordSearchQuery.Parse(@"D:\BMS\");
        var driveRelativeQuery = GridKeywordSearchQuery.Parse("D:");
        var lowerDrivePathQuery = GridKeywordSearchQuery.Parse(@"d:\bms\");

        Assert.IsTrue(drivePathQuery.MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(driveRelativeQuery.MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(lowerDrivePathQuery.MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.AreEqual(0, drivePathQuery.GetDiagnostics(GridKeywordSearchContext.ChartList).Count);
        Assert.AreEqual(0, driveRelativeQuery.GetDiagnostics(GridKeywordSearchContext.ChartList).Count);
        Assert.AreEqual(0, lowerDrivePathQuery.GetDiagnostics(GridKeywordSearchContext.ChartList).Count);
    }

    [TestMethod]
    public void MatchesChartList_QuoteSearchTreatsPhraseAsSingleToken()
    {
        TestableBmsFile file = CreateFile();
        var quotedFile = new TestableBmsFile();
        quotedFile.SetTitleForTest("Alpha \"Quoted\"");

        Assert.IsTrue(GridKeywordSearchQuery.Parse("\"Alpha Title\"").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha Title\"").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha \\\"Quoted\\\"\"").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(quotedFile)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("\"Alpha Title").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("\"Alpha Missing\"").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_NegationExcludesMatchingRows()
    {
        TestableBmsFile file = CreateFile();
        var hyphenatedFile = new TestableBmsFile();
        hyphenatedFile.SetTitleForTest("foo-bar");

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha -artist:other").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("alpha -artist:artistx").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("-").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("foo-bar").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(hyphenatedFile)));
    }

    [TestMethod]
    public void MatchesChartList_OrSearchIsLimitedToTokenAlternatives()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:missing|alpha").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha Title\"|missing").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:missing|other").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("|").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_RegexSearchSupportsGlobalAndFieldQueries()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("re:^alpha").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:re:^alpha\\s+title$").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("artist:re:^alpha").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:re:[").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void GetDiagnostics_ReportsContextSpecificWarnings()
    {
        var memoQuery = GridKeywordSearchQuery.Parse("memo:alpha");

        Assert.AreEqual(0, memoQuery.GetDiagnostics(GridKeywordSearchContext.PlaylistDetail).Count);
        Assert.AreEqual(GridKeywordSearchDiagnosticKind.UnknownField, memoQuery.GetDiagnostics(GridKeywordSearchContext.ChartList)[0].Kind);
        Assert.AreEqual(GridKeywordSearchDiagnosticKind.UnknownField, memoQuery.GetDiagnostics(GridKeywordSearchContext.PlaylistSummary)[0].Kind);
    }

    [TestMethod]
    public void GetDiagnostics_ReportsInvalidConditionsWithoutChangingMatchSemantics()
    {
        TestableBmsFile file = CreateFile();
        var query = GridKeywordSearchQuery.Parse("unknown:alpha title: - | title:re:[");
        GridKeywordSearchDiagnosticKind[] kinds = [.. query.GetDiagnostics(GridKeywordSearchContext.ChartList).Select(diagnostic => diagnostic.Kind)];

        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.UnknownField);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyFieldTerm);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyNegation);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyOr);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.InvalidRegex);
        Assert.IsFalse(query.MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void GetDiagnostics_DoesNotReportValidAdvancedSyntax()
    {
        var query = GridKeywordSearchQuery.Parse("title:\"Alpha Title\" -artist:other title:alpha|beta title:re:^alpha");

        Assert.AreEqual(0, query.GetDiagnostics(GridKeywordSearchContext.ChartList).Count);
    }

    [TestMethod]
    public void ChartInfoDisplayFormatter_FormatsDisplayValues()
    {
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo();

        Assert.AreEqual("180", ChartInfoDisplayFormatter.FormatOptionalDouble(180.0));
        Assert.AreEqual("180.25", ChartInfoDisplayFormatter.FormatOptionalDouble(180.25));
        Assert.AreEqual("123.13 s", ChartInfoDisplayFormatter.FormatDuration(123130));
        Assert.AreEqual("2.47", ChartInfoDisplayFormatter.FormatFixedTwo(2.468));
        Assert.AreEqual("ANOTHER", ChartInfoDisplayFormatter.FormatDifficulty(4));
        Assert.AreEqual("EASY", ChartInfoDisplayFormatter.FormatJudge(100));
        Assert.AreEqual("LN RANDOM STOP", ChartInfoDisplayFormatter.FormatFeature(chartInfo.feature));
    }

    [TestMethod]
    public void MatchesChartList_ChartInfoNumericRangeAndAliases()
    {
        TestableBmsFile file = CreateFile();
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(sha256: file.sha256, md5: file.hash);
        LibraryChartRow row = LibraryChartRow.FromBmsFile(file);
        row.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:10..12 notes:>=2000 duration:<124").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("difficulty:another feature:random tn:2.0..").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("-feature:mine scratch:12").MatchesLibraryChartRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("feature:mine").MatchesLibraryChartRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("difficulty:insane").MatchesLibraryChartRow(row));
    }

    [TestMethod]
    public void MatchesChartList_ChartInfoDefinedUndefinedTerms()
    {
        TestableBmsFile file = CreateFile();
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo(level: null, difficultyDefined: false, totalDefined: false, sha256: file.sha256, md5: file.hash);
        LibraryChartRow row = LibraryChartRow.FromBmsFile(file);
        row.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:undefined difficulty:undefined total:undefined").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:undef difficulty:null total:undef").MatchesLibraryChartRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("level:defined").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("feature:defined").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("feature:undefined").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(CreateFile())));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("feature:null").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(CreateFile())));
    }

    [TestMethod]
    public void MatchesChartList_ScoreFieldsSupportAliasesAndNumericRanges()
    {
        TestableBmsFile file = CreateFile();
        file.SetScoreForTest(ClearType.HARD, RankType.AAA, perfect: 900, great: 100, totalnotes: 1000, maxcombo: 1200, minbp: 5);

        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:HC").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:\"HARD CLEAR\"").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("rank:AAA").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("djlevel:AAA").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("rate:0.95").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("rate:>=0.9").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("rate:95").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("score:>=1700 combo:1000.. bp:0..10").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:defined score:defined").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));

        file.bmsScore.clear = ClearType.EX_HARD;
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:EXH").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        file.bmsScore.clear = ClearType.PA;
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:PF").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        file.bmsScore.clear = ClearType.MAX;
        file.bmsScore.rank = RankType.MAX;
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:MAX dj:MAX").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_ScoreFieldsSupportUndefinedTerms()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("rank:undefined score:undefined rate:undefined combo:undefined bp:undefined").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("rank:undef score:null rate:undef combo:null bp:undef").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:defined").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("rank:defined").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("score:defined").MatchesLibraryChartRow(LibraryChartRow.FromBmsFile(file)));
    }

    [TestMethod]
    public void LibraryChartRow_FromBmsonSong_ExposesChartInfoForDisplayAndSearch()
    {
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo();
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\Alpha\chart.bmson",
            title = "Alpha Bmson",
            artist = "ArtistX",
            genre = "GenreX",
            folder = "Alpha",
            mode_hint = "beat-7k",
            md5 = chartInfo.md5,
            sha256 = chartInfo.sha256
        };

        var row = LibraryChartRow.FromBmsonSong(song);
        row.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        Assert.AreSame(chartInfo, row.ChartInfo);
        Assert.AreEqual("12", row.ChartLevelText);
        Assert.AreEqual(2500, row.ChartNotes);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha artistx genrex sha256:" + chartInfo.sha256.Substring(0, 8)).MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:12 feature:random notes:>=2000").MatchesLibraryChartRow(row));
    }

    [TestMethod]
    public void MatchesLibraryChartRow_ScoreFieldsUseBmsFileScore()
    {
        TestableBmsFile file = CreateFile();
        file.SetScoreForTest(ClearType.HARD, RankType.AA, perfect: 850, great: 100, totalnotes: 1000, maxcombo: 900, minbp: 8);
        var row = LibraryChartRow.FromBmsFile(file);

        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:HC rank:AA score:>=1800 bp:<10").MatchesLibraryChartRow(row));
    }

    [TestMethod]
    public void MatchesPlaylistDetail_ScoreFieldsUseSourceSnapshot()
    {
        TestableBmsFile file = CreateFile();
        file.SetScoreForTest(ClearType.HARD, RankType.AA, perfect: 850, great: 100, totalnotes: 1000, maxcombo: 900, minbp: 8);
        var row = new PlaylistDetailSourceRow(new BMSTableEntry(file), ChartFileProjection.FromBmsFile(file, includeScoreSnapshot: true));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:\"HARD CLEAR\" score:>=1800 bp:0..10").MatchesPlaylistDetail(row));
    }

    [TestMethod]
    public void CreateFieldCompletion_CompletesContextSpecificFields()
    {
        GridKeywordSearchCompletionResult bmsResult = GridKeywordSearchCompletion.CreateFieldCompletion("tit", 3, GridKeywordSearchContext.ChartList);
        GridKeywordSearchCompletionResult negatedResult = GridKeywordSearchCompletion.CreateFieldCompletion("-ar", 3, GridKeywordSearchContext.ChartList);
        GridKeywordSearchCompletionResult detailResult = GridKeywordSearchCompletion.CreateFieldCompletion("mem", 3, GridKeywordSearchContext.PlaylistDetail);
        GridKeywordSearchCompletionResult summaryResult = GridKeywordSearchCompletion.CreateFieldCompletion("na", 2, GridKeywordSearchContext.PlaylistSummary);
        GridKeywordSearchCompletionResult clearResult = GridKeywordSearchCompletion.CreateFieldCompletion("cle", 3, GridKeywordSearchContext.ChartList);
        GridKeywordSearchCompletionResult djResult = GridKeywordSearchCompletion.CreateFieldCompletion("dj", 2, GridKeywordSearchContext.ChartList);
        GridKeywordSearchCompletionResult rateRankResult = GridKeywordSearchCompletion.CreateFieldCompletion("ra", 2, GridKeywordSearchContext.ChartList);
        GridKeywordSearchCompletionResult tableResult = GridKeywordSearchCompletion.CreateFieldCompletion("tab", 3, GridKeywordSearchContext.ChartList);

        Assert.IsTrue(bmsResult.Items.Any(item => item.DisplayText == "title:"));
        Assert.IsTrue(negatedResult.Items.Any(item => item.DisplayText == "-artist:"));
        Assert.IsTrue(detailResult.Items.Any(item => item.DisplayText == "memo:"));
        Assert.IsTrue(summaryResult.Items.Any(item => item.DisplayText == "name:"));
        Assert.IsTrue(clearResult.Items.Any(item => item.DisplayText == "clear:"));
        Assert.IsTrue(djResult.Items.Any(item => item.DisplayText == "dj:"));
        Assert.IsTrue(djResult.Items.Any(item => item.DisplayText == "djlevel:"));
        Assert.IsTrue(rateRankResult.Items.Any(item => item.DisplayText == "rank:"));
        Assert.IsTrue(rateRankResult.Items.Any(item => item.DisplayText == "rate:"));
        Assert.IsTrue(tableResult.Items.Any(item => item.DisplayText == "table:"));
        Assert.AreEqual(0, GridKeywordSearchCompletion.CreateFieldCompletion("mem", 3, GridKeywordSearchContext.ChartList).Items.Count);
        Assert.AreEqual(0, GridKeywordSearchCompletion.CreateFieldCompletion("D:", 2, GridKeywordSearchContext.ChartList).Items.Count);
    }

    [TestMethod]
    public void CreateFieldCompletion_DoesNotCompleteTermsAfterFieldSeparator()
    {
        GridKeywordSearchCompletionResult result = GridKeywordSearchCompletion.CreateFieldCompletion("title:alpha", 11, GridKeywordSearchContext.ChartList);

        Assert.AreEqual(0, result.Items.Count);
    }

    [TestMethod]
    public void CreatePlaylistValueCompletion_CompletesPlaylistNames()
    {
        string[] names =
        [
            "Satellite sl",
            "NoSpace",
            "A \"Quote\" \\ Path",
            "Pipe|Name",
            "Satellite sl"
        ];

        KeywordSearchSuggestionItem spaced = GridKeywordSearchCompletion.CreatePlaylistValueCompletion("playlist:Sat", "playlist:Sat".Length, GridKeywordSearchContext.ChartList, names)
            .Items
            .Single(item => item.DisplayText == "Satellite sl");
        KeywordSearchSuggestionItem noSpace = GridKeywordSearchCompletion.CreatePlaylistValueCompletion("ref:No", "ref:No".Length, GridKeywordSearchContext.PlaylistDetail, names)
            .Items
            .Single(item => item.DisplayText == "NoSpace");
        KeywordSearchSuggestionItem table = GridKeywordSearchCompletion.CreatePlaylistValueCompletion("table:No", "table:No".Length, GridKeywordSearchContext.ChartList, names)
            .Items
            .Single(item => item.DisplayText == "NoSpace");
        KeywordSearchSuggestionItem escaped = GridKeywordSearchCompletion.CreatePlaylistValueCompletion("playlist:A", "playlist:A".Length, GridKeywordSearchContext.ChartList, names)
            .Items
            .Single(item => item.DisplayText == "A \"Quote\" \\ Path");
        KeywordSearchSuggestionItem pipe = GridKeywordSearchCompletion.CreatePlaylistValueCompletion("playlist:Pipe", "playlist:Pipe".Length, GridKeywordSearchContext.ChartList, names)
            .Items
            .Single(item => item.DisplayText == "Pipe|Name");
        KeywordSearchSuggestionItem quotedPrefix = GridKeywordSearchCompletion.CreatePlaylistValueCompletion("playlist:\"Satellite s", "playlist:\"Satellite s".Length, GridKeywordSearchContext.ChartList, names)
            .Items
            .Single(item => item.DisplayText == "Satellite sl");

        Assert.AreEqual("playlist:\"Satellite sl\"", spaced.Apply("playlist:Sat", out int spacedCaret));
        Assert.AreEqual("playlist:\"Satellite sl\"".Length, spacedCaret);
        Assert.AreEqual("ref:NoSpace", noSpace.Apply("ref:No", out int noSpaceCaret));
        Assert.AreEqual("ref:NoSpace".Length, noSpaceCaret);
        Assert.AreEqual("table:NoSpace", table.Apply("table:No", out int tableCaret));
        Assert.AreEqual("table:NoSpace".Length, tableCaret);
        Assert.AreEqual("playlist:\"A \\\"Quote\\\" \\\\ Path\"", escaped.Apply("playlist:A", out int escapedCaret));
        Assert.AreEqual("playlist:\"A \\\"Quote\\\" \\\\ Path\"".Length, escapedCaret);
        Assert.AreEqual("playlist:\"Pipe|Name\"", pipe.Apply("playlist:Pipe", out int pipeCaret));
        Assert.AreEqual("playlist:\"Pipe|Name\"".Length, pipeCaret);
        Assert.AreEqual("playlist:\"Satellite sl\"", quotedPrefix.Apply("playlist:\"Satellite s", out int quotedPrefixCaret));
        Assert.AreEqual("playlist:\"Satellite sl\"".Length, quotedPrefixCaret);
        Assert.AreEqual(0, GridKeywordSearchCompletion.CreatePlaylistValueCompletion("title:Sat", "title:Sat".Length, GridKeywordSearchContext.ChartList, names).Items.Count);
        Assert.AreEqual(0, GridKeywordSearchCompletion.CreatePlaylistValueCompletion("playlist:Sat", "playlist:Sat".Length, GridKeywordSearchContext.PlaylistSummary, names).Items.Count);
    }

    [TestMethod]
    public void KeywordSearchSuggestionItem_ApplyReplacesOnlyFieldPrefix()
    {
        KeywordSearchSuggestionItem suggestion = GridKeywordSearchCompletion.CreateFieldCompletion("foo tit bar", 7, GridKeywordSearchContext.ChartList)
            .Items
            .Single(item => item.DisplayText == "title:");

        string applied = suggestion.Apply("foo tit bar", out int caretIndex);

        Assert.AreEqual("foo title: bar", applied);
        Assert.AreEqual("foo title:".Length, caretIndex);
    }

    [TestMethod]
    public void KeywordSearchHistoryStore_RoundTripsDistinctCappedHistory()
    {
        string[] entries = [.. Enumerable.Range(0, KeywordSearchHistoryStore.MaxHistoryCount + 5).Select(index => "title:" + index)];
        string serialized = KeywordSearchHistoryStore.Serialize(entries);

        string[] restored = [.. KeywordSearchHistoryStore.Deserialize(serialized)];

        Assert.AreEqual(KeywordSearchHistoryStore.MaxHistoryCount, restored.Length);
        Assert.AreEqual("title:0", restored[0]);
        CollectionAssert.DoesNotContain(restored, "title:24");
    }

    [TestMethod]
    public void KeywordSearchHistoryStore_AddEntryMovesDuplicateToFront()
    {
        string[] updated = [.. KeywordSearchHistoryStore.AddEntry(["alpha", "beta", "gamma"], " BETA ")];

        CollectionAssert.AreEqual(new[] { "BETA", "alpha", "gamma" }, updated);
    }

    private static TestableBmsFile CreateFile()
    {
        var file = new TestableBmsFile();
        file.ApplySnapshot();
        return file;
    }

    private static BMSTable CreateTable(string name, string symbol)
    {
        return new BMSTable
        {
            name = name,
            symbol = symbol
        };
    }

    private static PlaylistReferenceIndex CreatePlaylistReferenceIndex(BMSFile file, params BMSTable[] tables)
    {
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        foreach (BMSTable table in tables ?? [])
        {
            table.entries = [new BMSTableEntry(file)];
            index.ReplaceTable(table, table.entries);
        }
        return index;
    }

    private static BMSTableEntry CreateBmsonPlaylistEntry(string sha256)
    {
        var entry = new BMSTableEntry();
        entry.MarkAsBmsonPlaylistIdentity(sha256);
        return entry;
    }

    private static ChartListSourceRow CreateSourceRow(BMSFile file, Func<ChartListSourceRow, PlaylistReferenceDisplay>? playlistReferenceDisplayProvider = null, LR2SongDBExtended.chart_info? chartInfo = null)
    {
        return ChartListSourceRow.FromChartFile(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
            ChartListSourceProjectionMode.OwnerBacked,
            playlistReferenceDisplayProvider: playlistReferenceDisplayProvider,
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));
    }

    private static ChartListSourceRow CreateSourceRow(LR2SongDBExtended.bmson_song song, LR2SongDBExtended.chart_info? chartInfo = null)
    {
        return ChartListSourceRow.FromChartFile(
            ChartFileProjection.FromBmsonSong(song, includeWarningSnapshot: false),
            ChartListSourceProjectionMode.OwnerBacked,
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));
    }

    private static Func<ChartFile, LR2SongDBExtended.chart_info> CreateChartInfoProvider(params LR2SongDBExtended.chart_info?[] rows)
    {
        return chart => ResolveChartInfoByIdentity(chart, rows);
    }

    private static LR2SongDBExtended.chart_info ResolveChartInfoByIdentity(ChartFile chart, IEnumerable<LR2SongDBExtended.chart_info?> rows)
    {
        if (chart == null)
        {
            return null!;
        }
        return rows
            .Where(row => row != null)
            .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Sha256) && string.Equals(row!.sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase))
            ?? rows
                .Where(row => row != null)
                .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Md5) && string.Equals(row!.md5, chart.Md5, StringComparison.OrdinalIgnoreCase))
            ?? null!;
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(int? level = 12, bool difficultyDefined = true, bool totalDefined = true, string? sha256 = null, string? md5 = null)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256 ?? "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            md5 = md5 ?? "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            charthash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            level = level,
            difficulty = 4,
            difficulty_defined = difficultyDefined,
            mainbpm = 180,
            maxbpm = 220.5,
            minbpm = 90,
            length = 123130,
            judge = 100,
            feature = ChartInfoDisplayFormatter.FeatureUndefinedLongNote
                | ChartInfoDisplayFormatter.FeatureRandom
                | ChartInfoDisplayFormatter.FeatureStopSequence,
            notes = 2500,
            ln = 30,
            s = 10,
            ls = 2,
            total = 6169.0,
            total_defined = totalDefined,
            density = 20.3,
            peakdensity = 42,
            enddensity = 12,
            speedchange_count = 3
        };
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void ApplySnapshot()
        {
            Title = "Alpha Title";
            Artist = "ArtistX";
            genre = "GenreX";
            tag = "TagX";
            path = @"C:\Songs\Alpha\chart.bms";
            hash = "abcdefabcdefabcdefabcdefabcdefab";
            sha256 = "1234567890123456789012345678901234567890123456789012345678901234";
        }

        internal void SetTitleForTest(string value)
        {
            Title = value;
        }

        internal void SetHashForTest(string value)
        {
            hash = value;
        }

        internal void SetGenreForTest(string value)
        {
            genre = value;
        }

        internal void SetScoreForTest(ClearType clear, RankType rank, int perfect, int great, int totalnotes, int maxcombo, int minbp)
        {
            bmsScore = new BMSScore
            {
                hash = hash,
                clear = clear,
                rank = rank,
                perfect = perfect,
                great = great,
                totalnotes = totalnotes,
                maxcombo = maxcombo,
                minbp = minbp
            };
        }
    }
}
