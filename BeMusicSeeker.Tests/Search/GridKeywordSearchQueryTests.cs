using System;
using System.Collections.Generic;
using System.Globalization;
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
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("genrex").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("abcdef").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("1234567890").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_UsesAndForMultipleTokens()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha artistx abcdef").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("alpha missing").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_FieldQueryLimitsSearchTarget()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:alpha artist:artistx md5:abcdef sha256:123456").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:artistx").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("memo:alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_PlaylistFieldSearchesReferenceNames()
    {
        ChartFile file = CreateFile();
        BMSTable[] tables =
        [
            CreateTable("Satellite sl", "★"),
            CreateTable("Second Table", "★★"),
            CreateTable("GENOSIDE", "▽")
        ];
        PlaylistReferenceIndex index = CreatePlaylistReferenceIndex(file, tables);
        var libraryRow = LibraryChartRow.FromChartFile(file);
        libraryRow.SetPlaylistReferenceDisplayProvider(row => index.Find(row.Chart));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Satellite sl\"").MatchesLibraryChartRow(libraryRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("ref:\"Second Table\"").MatchesLibraryChartRow(libraryRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("table:GENOSIDE").MatchesLibraryChartRow(libraryRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:re:^GENOSIDE$").MatchesLibraryChartRow(libraryRow));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("playlist:★").MatchesLibraryChartRow(libraryRow));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("playlist:★★").MatchesLibraryChartRow(libraryRow));

        var playlistRow = new PlaylistDetailSourceRow(
            new BMSTableEntry(file),
            (file),
            playlistReferenceDisplayProvider: chart => index.Find(chart));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Satellite sl\"").MatchesLibraryChartRow(libraryRow));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("playlist:\"Second Table\"").MatchesPlaylistDetail(playlistRow));
    }

    [TestMethod]
    public void MatchesChartList_PlaylistFieldSearchesBmsonReferenceProjection()
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Songs\Bmson\chart.bmson",
            Folder = "BmsonFolder",
            Title = "BmsonTitle",
            RawTitle = "BmsonTitle",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        };
        var table = new BMSTable
        {
            name = "Bmson Table",
            symbol = "BMSN",
            entries = [CreateBmsonPlaylistEntry(song.Sha256)]
        };
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        index.ReplaceSnapshotTable(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));
        var row = LibraryChartRow.FromChartFile((song));
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
        ChartFile file = CreateFile();
        BMSTable[] tables =
        [
            CreateTable("Satellite sl", "★"),
            CreateTable("GENOSIDE", "▽")
        ];
        PlaylistReferenceIndex index = CreatePlaylistReferenceIndex(file, tables);
        ChartListSourceRow sourceRow = CreateSourceRow(file, row => index.Find(row.Chart));
        var libraryRow = LibraryChartRow.FromChartFile(file);
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
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Songs\Bmson\chart.bmson",
            Folder = "BmsonFolder",
            Title = "BmsonTitle Sub",
            RawTitle = "BmsonTitle",
            Subtitle = "Sub",
            Artist = "BmsonArtist",
            RawArtist = "BmsonArtist",
            Genre = "BmsonGenre",
            ModeHint = "beat-7k",
            Mode = 7,
            Level = 7,
            LevelText = "7",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        };
        ChartListSourceRow sourceRow = CreateSourceRow(song);

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
        Assert.AreEqual(query.MatchesLibraryChartRow(LibraryChartRow.FromChartFile((song))), query.MatchesChartListSourceRow(sourceRow));
    }

    [TestMethod]
    public void ChartListSourceRow_AppliesCommonCurrentAndReadsChartInfoProvider()
    {
        ChartFile file = CreateFile() with { Token = new OwnedChartToken() };
        ChartFile captured = file;
        BeMusicSeeker.Models.ChartDetails bmsChartInfo = CreateChartInfo(level: 10, sha256: file.Sha256, md5: file.Md5);
        ChartListSourceRow bmsSourceRow = CreateSourceRow(file, chartInfo: bmsChartInfo);

        file = file with { Title = "Changed Title", RawTitle = "Changed Title" };
        file = file with { Genre = "Changed Genre" };
        file = file with { Tag = "Changed Tag" };
        file = file with { Path = @"C:\Songs\Changed\chart.bms" };
        file = file with { Md5 = "ffffffffffffffffffffffffffffffff" };

        bmsSourceRow.ApplyCurrentChart(file);
        Assert.AreEqual("Alpha Title", captured.Title);
        Assert.AreEqual("Changed Title", bmsSourceRow.Title);
        Assert.AreEqual("Changed Genre", bmsSourceRow.Genre);
        Assert.AreEqual("Changed Tag", bmsSourceRow.Tag);
        Assert.AreEqual(@"C:\Songs\Changed\chart.bms", bmsSourceRow.Path);
        Assert.AreEqual("ffffffffffffffffffffffffffffffff", bmsSourceRow.Hash);
        Assert.AreSame(bmsChartInfo, bmsSourceRow.ChartInfo);
        Assert.AreSame(bmsChartInfo, bmsSourceRow.Chart.ChartInfo);
        Assert.AreEqual(10, bmsSourceRow.ChartLevelSortKey);

        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Songs\Bmson\chart.bmson",
            Title = "BmsonTitle",
            RawTitle = "BmsonTitle",
            Artist = "BmsonArtist",
            RawArtist = "BmsonArtist",
            ModeHint = "beat-7k",
            Mode = 7,
            Level = 7,
            LevelText = "7",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        };
        song = song with { Token = new OwnedChartToken() };
        BeMusicSeeker.Models.ChartDetails bmsonChartInfo = CreateChartInfo(level: 11, sha256: song.Sha256, md5: song.Md5);
        ChartListSourceRow bmsonSourceRow = CreateSourceRow(song, chartInfo: bmsonChartInfo);

        song = song with { Title = "Changed Bmson", RawTitle = "Changed Bmson" };
        song = song with { Genre = "Changed Genre" };
        song = song with { Level = 9, LevelText = "9" };
        song = song with { ModeHint = "beat-5k", Mode = 5 };
        song = song with { Path = @"C:\Songs\Changed\chart.bmson" };
        song = song with { Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" };

        bmsonSourceRow.ApplyCurrentChart(song);
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
        ChartFile file = CreateFile();
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(sha256: file.Sha256, md5: file.Md5);
        file = ChartFileProjection.WithScore(file, ChartTestValues.Score(ClearType.HARD, RankType.AA, 850, 100, 1000, 900, 8, 0, false));
        ChartListSourceRow sourceRow = CreateSourceRow(file, chartInfo: chartInfo);
        var libraryRow = LibraryChartRow.FromChartFile(file);
        libraryRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        var query = GridKeywordSearchQuery.Parse("level:12 feature:random notes:>=2000 clear:HC rank:AA score:>=1800 bp:<10");

        Assert.IsTrue(query.MatchesChartListSourceRow(sourceRow));
        Assert.AreEqual(query.MatchesLibraryChartRow(libraryRow), query.MatchesChartListSourceRow(sourceRow));
    }

    [TestMethod]
    public void MatchesChartListSourceRow_RegexScoreAndChartInfoFieldsMatchLibraryChartRow()
    {
        ChartFile file = CreateFile();
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(sha256: file.Sha256, md5: file.Md5);
        file = ChartFileProjection.WithScore(file, ChartTestValues.Score(ClearType.HARD, RankType.AA, 850, 100, 1000, 900, 8, 0, false));
        ChartListSourceRow sourceRow = CreateSourceRow(file, chartInfo: chartInfo);
        var libraryRow = LibraryChartRow.FromChartFile(file);
        libraryRow.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        var query = GridKeywordSearchQuery.Parse("feature:re:RANDOM judge:re:EASY clear:re:HARD rank:re:^AA$ score:re:^1800$ bp:re:^8$");

        Assert.IsTrue(query.MatchesChartListSourceRow(sourceRow));
        Assert.AreEqual(query.MatchesLibraryChartRow(libraryRow), query.MatchesChartListSourceRow(sourceRow));
    }

    [TestMethod]
    public void MatchesChartList_UnknownOrEmptyFieldQueryDoesNotMatch()
    {
        ChartFile file = CreateFile();

        Assert.IsFalse(GridKeywordSearchQuery.Parse("unknown:alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse(":alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_WindowsDriveLetterTokenIsGlobalKeyword()
    {
        ChartFile file = CreateFile();
        file = file with { Path = @"D:\BMS\Alpha\chart.bms" };

        var drivePathQuery = GridKeywordSearchQuery.Parse(@"D:\BMS\");
        var driveRelativeQuery = GridKeywordSearchQuery.Parse("D:");
        var lowerDrivePathQuery = GridKeywordSearchQuery.Parse(@"d:\bms\");

        Assert.IsTrue(drivePathQuery.MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(driveRelativeQuery.MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(lowerDrivePathQuery.MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.AreEqual(0, drivePathQuery.GetDiagnostics(GridKeywordSearchContext.ChartList).Count);
        Assert.AreEqual(0, driveRelativeQuery.GetDiagnostics(GridKeywordSearchContext.ChartList).Count);
        Assert.AreEqual(0, lowerDrivePathQuery.GetDiagnostics(GridKeywordSearchContext.ChartList).Count);
    }

    [TestMethod]
    public void MatchesChartList_QuoteSearchTreatsPhraseAsSingleToken()
    {
        ChartFile file = CreateFile();
        ChartFile quotedFile = ChartTestValues.Empty();
        quotedFile = quotedFile with { Title = "Alpha \"Quoted\"", RawTitle = "Alpha \"Quoted\"" };

        Assert.IsTrue(GridKeywordSearchQuery.Parse("\"Alpha Title\"").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha Title\"").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha \\\"Quoted\\\"\"").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(quotedFile)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("\"Alpha Title").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("\"Alpha Missing\"").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_NegationExcludesMatchingRows()
    {
        ChartFile file = CreateFile();
        ChartFile hyphenatedFile = ChartTestValues.Empty();
        hyphenatedFile = hyphenatedFile with { Title = "foo-bar", RawTitle = "foo-bar" };

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha -artist:other").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("alpha -artist:artistx").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("-").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("foo-bar").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(hyphenatedFile)));
    }

    [TestMethod]
    public void MatchesChartList_OrSearchIsLimitedToTokenAlternatives()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:missing|alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha Title\"|missing").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:missing|other").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("|").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_RegexSearchSupportsGlobalAndFieldQueries()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("re:^alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:re:^alpha\\s+title$").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("artist:re:^alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:re:[").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void GetDiagnostics_ReportsContextSpecificWarnings()
    {
        var memoQuery = GridKeywordSearchQuery.Parse("memo:alpha");

        Assert.AreEqual(0, memoQuery.GetDiagnostics(GridKeywordSearchContext.PlaylistDetail).Count);
        Assert.AreEqual(GridKeywordSearchDiagnosticKind.UnknownField, memoQuery.GetDiagnostics(GridKeywordSearchContext.ChartList)[0].Kind);
        Assert.AreEqual(GridKeywordSearchDiagnosticKind.UnknownField, memoQuery.GetDiagnostics(GridKeywordSearchContext.PlaylistSummary)[0].Kind);

        var emptyDateQuery = GridKeywordSearchQuery.Parse("date:");
        Assert.IsTrue(emptyDateQuery.GetDiagnostics(GridKeywordSearchContext.PlayHistory)
            .Any(diagnostic => diagnostic.Kind == GridKeywordSearchDiagnosticKind.InvalidDate));
        Assert.IsFalse(emptyDateQuery.GetDiagnostics(GridKeywordSearchContext.ChartList)
            .Any(diagnostic => diagnostic.Kind == GridKeywordSearchDiagnosticKind.InvalidDate));
    }

    [TestMethod]
    public void GetDiagnostics_ReportsInvalidConditionsWithoutChangingMatchSemantics()
    {
        ChartFile file = CreateFile();
        var query = GridKeywordSearchQuery.Parse("unknown:alpha title: - | title:re:[");
        GridKeywordSearchDiagnosticKind[] kinds = [.. query.GetDiagnostics(GridKeywordSearchContext.ChartList).Select(diagnostic => diagnostic.Kind)];

        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.UnknownField);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyFieldTerm);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyNegation);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyOr);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.InvalidRegex);
        Assert.IsFalse(query.MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
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
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo();

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
        ChartFile file = CreateFile();
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(sha256: file.Sha256, md5: file.Md5);
        var row = LibraryChartRow.FromChartFile(file);
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
        ChartFile file = CreateFile();
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo(level: null, difficultyDefined: false, totalDefined: false, sha256: file.Sha256, md5: file.Md5);
        var row = LibraryChartRow.FromChartFile(file);
        row.SetChartInfoProjectionProvider(CreateChartInfoProvider(chartInfo));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:undefined difficulty:undefined total:undefined").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:undef difficulty:null total:undef").MatchesLibraryChartRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("level:defined").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("feature:defined").MatchesLibraryChartRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("feature:undefined").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(CreateFile())));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("feature:null").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(CreateFile())));
    }

    [TestMethod]
    public void MatchesChartList_ScoreFieldsSupportAliasesAndNumericRanges()
    {
        ChartFile file = CreateFile();
        file = ChartFileProjection.WithScore(file, ChartTestValues.Score(ClearType.HARD, RankType.AAA, 900, 100, 1000, 1200, 5, 0, false));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:HC").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:\"HARD CLEAR\"").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("rank:AAA").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("djlevel:AAA").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("rate:0.95").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("rate:>=0.9").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("rate:95").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("score:>=1700 combo:1000.. bp:0..10").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:defined score:defined").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));

        file = ChartFileProjection.WithScore(file, new ChartScoreSnapshot(ClearType.EX_HARD, RankType.AAA));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:EXH").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        file = ChartFileProjection.WithScore(file, new ChartScoreSnapshot(ClearType.PA, RankType.AAA));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:PF").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        file = ChartFileProjection.WithScore(file, new ChartScoreSnapshot(ClearType.MAX, RankType.AAA));
        file = ChartFileProjection.WithScore(file, new ChartScoreSnapshot(ClearType.MAX, RankType.MAX));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:MAX dj:MAX").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void MatchesChartList_ScoreFieldsSupportUndefinedTerms()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("rank:undefined score:undefined rate:undefined combo:undefined bp:undefined").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("rank:undef score:null rate:undef combo:null bp:undef").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:defined").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("rank:defined").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("score:defined").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    [TestMethod]
    public void LibraryChartRow_FromBmsonChart_ExposesChartInfoForDisplayAndSearch()
    {
        BeMusicSeeker.Models.ChartDetails chartInfo = CreateChartInfo();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Songs\Alpha\chart.bmson",
            Title = "Alpha Bmson",
            RawTitle = "Alpha Bmson",
            Artist = "ArtistX",
            RawArtist = "ArtistX",
            Genre = "GenreX",
            Folder = "Alpha",
            ModeHint = "beat-7k",
            Mode = 7,
            Md5 = chartInfo.md5,
            Sha256 = chartInfo.sha256
        };

        var row = LibraryChartRow.FromChartFile((song));
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
        ChartFile file = CreateFile();
        file = ChartFileProjection.WithScore(file, ChartTestValues.Score(ClearType.HARD, RankType.AA, 850, 100, 1000, 900, 8, 0, false));
        var row = LibraryChartRow.FromChartFile(file);

        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:HC rank:AA score:>=1800 bp:<10").MatchesLibraryChartRow(row));
    }

    [TestMethod]
    public void MatchesLibraryChartRow_Lr2AssistClearUsesOptionHistory()
    {
        ChartFile assistFile = CreateFile();
        assistFile = ChartFileProjection.WithScore(assistFile, ChartTestValues.Score(ClearType.EASY, RankType.F, 100, 50, 200, 120, 20, ClearTypeStorageConverter.OptionHistoryAssist, true));
        var assistRow = LibraryChartRow.FromChartFile(assistFile);

        Assert.AreEqual(ClearType.INVALID, assistRow.clear);
        Assert.AreEqual("ASSIST", assistRow.ClearDisplayText);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:AE").MatchesLibraryChartRow(assistRow));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("clear:EC").MatchesLibraryChartRow(assistRow));

        ChartFile forceEasyFile = CreateFile();
        forceEasyFile = ChartFileProjection.WithScore(forceEasyFile, ChartTestValues.Score(ClearType.EASY, RankType.F, 100, 50, 200, 120, 20, 0, true));
        var forceEasyRow = LibraryChartRow.FromChartFile(forceEasyFile);

        Assert.AreEqual(ClearType.INVALID, forceEasyRow.clear);
        Assert.AreEqual("ASSIST", forceEasyRow.ClearDisplayText);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:AE").MatchesLibraryChartRow(forceEasyRow));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("clear:EC").MatchesLibraryChartRow(forceEasyRow));

        ChartFile easyFile = CreateFile();
        easyFile = ChartFileProjection.WithScore(easyFile, ChartTestValues.Score(ClearType.EASY, RankType.F, 100, 50, 200, 120, 20, ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy, true));
        var easyRow = LibraryChartRow.FromChartFile(easyFile);

        Assert.AreEqual(ClearType.EASY, easyRow.clear);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:EC").MatchesLibraryChartRow(easyRow));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("clear:AE").MatchesLibraryChartRow(easyRow));

        ChartFile internalEasyFile = CreateFile();
        internalEasyFile = ChartFileProjection.WithScore(internalEasyFile, ChartTestValues.Score(ClearType.EASY, RankType.F, 100, 50, 200, 120, 20, 0, false));
        var internalEasyRow = LibraryChartRow.FromChartFile(internalEasyFile);

        Assert.AreEqual(ClearType.EASY, internalEasyRow.clear);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:EC").MatchesLibraryChartRow(internalEasyRow));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("clear:AE").MatchesLibraryChartRow(internalEasyRow));
    }

    [TestMethod]
    public void MatchesPlaylistDetail_ScoreFieldsUseSourceSnapshot()
    {
        ChartFile file = CreateFile();
        file = ChartFileProjection.WithScore(file, ChartTestValues.Score(ClearType.HARD, RankType.AA, 850, 100, 1000, 900, 8, 0, false));
        var row = new PlaylistDetailSourceRow(new BMSTableEntry(file), (file));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:\"HARD CLEAR\" score:>=1800 bp:0..10").MatchesPlaylistDetail(row));
    }

    [TestMethod]
    public void MatchesPlayHistoryRow_SearchesPlayHistoryFields()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(finalized: true);

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha artistx").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:alpha artist:artistx folder:SL playlist:\"Satellite sl\"").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("date:2026-06-19 year:2026 month:06 month:2026-06 type:score kind:score clear:HC finalized:true").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("oldclear:NC newclear:HC").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("oldclear:HC").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("newclear:EASY").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("md5:abcdef sha256:123456 source:LR2").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("date:2026-06-18").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("month:2026").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("finalized:false").MatchesPlayHistoryRow(row));
    }

    [TestMethod]
    public void MatchesPlayHistoryRow_DateSupportsClosedLocalTimestampRanges()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(
            finalized: true,
            playedAt: new DateTimeOffset(2026, 6, 19, 12, 34, 56, TimeSpan.Zero));
        DateTime localPlayedAt = row.PlayedAt;
        string start = localPlayedAt.AddSeconds(-1).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        string end = localPlayedAt.AddSeconds(1).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        string expression = $"date:\"{start}..{end}\"";

        Assert.IsTrue(GridKeywordSearchQuery.Parse(expression).MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse($"date:\"{start}..{localPlayedAt:yyyy/MM/dd HH:mm:ss}\"").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse($"date:\"{localPlayedAt:yyyy/MM/dd HH:mm:ss}..{end}\"").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse($"date:\"{localPlayedAt.AddSeconds(1):yyyy/MM/dd HH:mm:ss}..{end}\"").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse($"date:\"{start}..{localPlayedAt.AddSeconds(-1):yyyy/MM/dd HH:mm:ss}\"").MatchesPlayHistoryRow(row));
    }

    [TestMethod]
    public void MatchesPlayHistoryRow_DateSupportsExactDayAndLocalTimestampForms()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(
            finalized: true,
            playedAt: new DateTimeOffset(2026, 6, 19, 12, 34, 56, TimeSpan.Zero));
        DateTime localPlayedAt = row.PlayedAt;

        Assert.IsTrue(GridKeywordSearchQuery.Parse($"date:{localPlayedAt:yyyy-MM-dd}").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse($"date:{localPlayedAt:yyyy/M/d}").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse($"date:{localPlayedAt:yyyy/MM/dd}").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse($"date:{localPlayedAt:yyyyMMdd}").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse($"date:\"{localPlayedAt:yyyy/MM/dd HH:mm:ss}\"").MatchesPlayHistoryRow(row));

        Assert.IsTrue(PlayHistoryDateSearchTerm.TryParse("9999-12-31", out PlayHistoryDateSearchTerm maximumDay));
        Assert.IsTrue(maximumDay.Matches(PlayHistoryWallClockSecond.FromDateTime(DateTime.MaxValue)));
    }

    [TestMethod]
    public void MatchesPlayHistoryRow_UnqualifiedTimestampIsNotPromotedToDateCondition()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(
            finalized: true,
            playedAt: new DateTimeOffset(2026, 6, 19, 12, 34, 56, TimeSpan.Zero));

        Assert.IsFalse(GridKeywordSearchQuery.Parse(
            row.PlayedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)).MatchesPlayHistoryRow(row));
    }

    [TestMethod]
    public void MatchesPlayHistoryRow_InvalidDateAndNegatedInvalidDateMatchNone()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(
            finalized: true,
            playedAt: new DateTimeOffset(2026, 6, 19, 12, 34, 56, TimeSpan.Zero));

        foreach (string expression in new[]
        {
            "date:\"2026/06/19 12:34:56..\"",
            "date:\"2026/06/19 12:34:56..2026/06/19 12:34:55\"",
            "date:\"2026/06/19 12:34:56.000\"",
            "date:\"2026/06/19 12:34:56+09:00\"",
            "date:\"2026/06/19 12:34:56 ..2026/06/19 12:34:57\"",
            "date:\"2026/06/19 12:34:56.. 2026/06/19 12:34:57\"",
            "date:\"2026/06/19 12:34:56..2026/06/19 12:34:57..2026/06/19 12:34:58\"",
            "date:\"2026/06/19 12:34:56..2026/06/19 12:34:57",
            "date:>=2026/06/19",
            "date:"
        })
        {
            var query = GridKeywordSearchQuery.Parse(expression);
            Assert.IsFalse(query.MatchesPlayHistoryRow(row), expression);
            Assert.IsTrue(
                query.GetDiagnostics(GridKeywordSearchContext.PlayHistory)
                    .Any(diagnostic => diagnostic.Kind == GridKeywordSearchDiagnosticKind.InvalidDate),
                expression);
        }

        var negatedInvalid = GridKeywordSearchQuery.Parse("-date:not-a-date");
        Assert.IsFalse(negatedInvalid.MatchesPlayHistoryRow(row));
        Assert.IsTrue(negatedInvalid.GetDiagnostics(GridKeywordSearchContext.PlayHistory)
            .Any(diagnostic => diagnostic.Kind == GridKeywordSearchDiagnosticKind.InvalidDate));

        var mixedOr = GridKeywordSearchQuery.Parse(
            $"date:not-a-date|{row.PlayedAt:yyyy/MM/dd}");
        Assert.IsTrue(mixedOr.MatchesPlayHistoryRow(row));
        Assert.IsTrue(mixedOr.GetDiagnostics(GridKeywordSearchContext.PlayHistory)
            .Any(diagnostic => diagnostic.Kind == GridKeywordSearchDiagnosticKind.InvalidDate));
    }

    [TestMethod]
    public void PlayHistoryDateSearchTerm_SameSecondSelectionRemainsClosedRange()
    {
        DateTimeOffset playedAt = new(2026, 6, 19, 12, 34, 56, TimeSpan.Zero);
        PlayHistoryRow first = CreatePlayHistoryRow(finalized: true, playedAt: playedAt);
        PlayHistoryRow second = CreatePlayHistoryRow(finalized: true, playedAt: playedAt);

        Assert.IsTrue(PlayHistoryDateSearchTerm.TryCreate([first, second], out PlayHistoryDateSearchTerm term));

        string timestamp = first.PlayedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.AreEqual($"date:\"{timestamp}..{timestamp}\"", term.Clause);
    }

    [TestMethod]
    public void PlayHistoryDateSearchTerm_UsesExplicitZoneWallClockForAmbiguousSeconds()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        DateTimeOffset firstInstant = new(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        DateTimeOffset secondInstant = new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);

        var firstEastern = PlayHistoryWallClockSecond.FromUnixSeconds(
            firstInstant.ToUnixTimeSeconds(),
            eastern);
        var secondEastern = PlayHistoryWallClockSecond.FromUnixSeconds(
            secondInstant.ToUnixTimeSeconds(),
            eastern);

        Assert.AreNotEqual(firstInstant.UtcDateTime, secondInstant.UtcDateTime);
        Assert.AreEqual("2026/11/01 01:30:00", firstEastern.ToCanonicalTimestamp());
        Assert.AreEqual(firstEastern, secondEastern);
        Assert.AreEqual(2026, firstEastern.Year);
        Assert.AreEqual(11, firstEastern.Month);
        Assert.AreEqual(1, firstEastern.Day);
        Assert.AreEqual(1, firstEastern.Hour);
        Assert.AreEqual(30, firstEastern.Minute);
        Assert.AreEqual(0, firstEastern.Second);

        Assert.IsTrue(PlayHistoryDateSearchTerm.TryParse(
            "2026/11/01 01:30:00",
            out PlayHistoryDateSearchTerm singleTimestamp));
        Assert.IsTrue(PlayHistoryDateSearchTerm.TryParse(
            "2026/11/01 01:29:59..2026/11/01 01:30:01",
            out PlayHistoryDateSearchTerm containingRange));
        Assert.IsTrue(singleTimestamp.Matches(firstEastern));
        Assert.IsTrue(singleTimestamp.Matches(secondEastern));
        Assert.IsTrue(containingRange.Matches(firstEastern));
        Assert.IsTrue(containingRange.Matches(secondEastern));

        var firstUtc = PlayHistoryWallClockSecond.FromUnixSeconds(
            firstInstant.ToUnixTimeSeconds(),
            TimeZoneInfo.Utc);
        var secondUtc = PlayHistoryWallClockSecond.FromUnixSeconds(
            secondInstant.ToUnixTimeSeconds(),
            TimeZoneInfo.Utc);

        Assert.AreNotEqual(firstEastern, firstUtc);
        Assert.AreNotEqual(secondEastern, secondUtc);
        Assert.IsFalse(singleTimestamp.Matches(firstUtc));
        Assert.IsFalse(singleTimestamp.Matches(secondUtc));
        Assert.IsFalse(containingRange.Matches(firstUtc));
        Assert.IsFalse(containingRange.Matches(secondUtc));
    }

    [TestMethod]
    public void MatchesPlayHistoryRow_SupportsDiagnosticsFields()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(finalized: false);

        Assert.IsTrue(GridKeywordSearchQuery.Parse("finalized:0").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("finalized:pending").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:defined").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("clear:HC").MatchesPlayHistoryRow(row));
    }

    [TestMethod]
    public void MatchesPlayHistoryKeywordAndSummaryFilters_CombinesKeywordAndCards()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(finalized: true);

        Assert.IsTrue(PlayHistoryWorkflowOwner.MatchesKeywordAndSummaryFilters(row, "artist:artistx", "type:bp", "type:score"));
        Assert.IsFalse(PlayHistoryWorkflowOwner.MatchesKeywordAndSummaryFilters(row, "artist:missing", "type:score"));
        Assert.IsFalse(PlayHistoryWorkflowOwner.MatchesKeywordAndSummaryFilters(row, "artist:artistx", "type:bp", "type:clear newclear:EC"));
    }

    [TestMethod]
    public void MatchesPlaylistSummary_OutputFieldUsesOutputBaseDisplayName()
    {
        PlaylistSummaryRow positive = new() { OutputBaseDisplayName = "Output Alpha" };
        PlaylistSummaryRow negative = new() { OutputBaseDisplayName = "Output Beta" };
        var query = GridKeywordSearchQuery.Parse("output:\"Output Alpha\"");

        Assert.IsTrue(query.MatchesPlaylistSummary(positive));
        Assert.IsFalse(query.MatchesPlaylistSummary(negative));
    }

    [TestMethod]
    public void KeywordSearchAssistance_TabSeparatorUsesFieldsAndQuotedTabStaysValueContext()
    {
        SearchAssistanceSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(52L, ["Alpha\tOne"]));

        const string unquotedSeparator = "clear:NP\t";
        KeywordSearchPresentationState fields = assistance.Focus(
            unquotedSeparator,
            unquotedSeparator.Length);

        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, fields.Sections.Single().Kind);
        Assert.IsTrue(fields.VisibleItems.Any(item => item.DisplayText == "title:"));

        const string quotedTab = "playlist:\"Alpha\t";
        KeywordSearchPresentationState values = assistance.Focus(quotedTab, quotedTab.Length);

        Assert.AreEqual(KeywordSearchPresentationSectionKind.Values, values.Sections.Single().Kind);
        Assert.IsTrue(values.VisibleItems.Any(item => item.DisplayText == "Alpha\tOne"));
        Assert.IsFalse(values.Sections.Any(section => section.Kind == KeywordSearchPresentationSectionKind.Fields));
    }

    [TestMethod]
    public void KeywordSearchAssistance_BackslashBeforeOpeningQuoteKeepsTabInUnmatchedQuotedToken()
    {
        SearchAssistanceSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(53L, ["Alpha\tOne"]));

        const string unmatchedQuotedTab = "playlist:\\\"\t";
        KeywordSearchPresentationState state = assistance.Focus(
            unmatchedQuotedTab,
            unmatchedQuotedTab.Length);

        Assert.IsFalse(state.IsOpen);
        Assert.IsFalse(state.Sections.Any(section =>
            section.Kind == KeywordSearchPresentationSectionKind.Fields
            || section.Kind == KeywordSearchPresentationSectionKind.Values));
    }

    [TestMethod]
    public void KsaCatalog_UsesApprovedCanonicalValuesByContextAndField()
    {
        KeywordSearchCatalogSnapshot normal = new(1L, []);
        KeywordSearchCatalogSnapshot playHistory = new(2L, []);

        AssertSetEqual(
            new[] { "nosong", "NP", "F", "AE", "LAE", "EC", "NC", "HC", "EXH", "FC", "PF", "MAX" },
            normal.GetValues(GridKeywordSearchContext.ChartList, "clear"));
        AssertSetEqual(
            new[] { "F", "E", "D", "C", "B", "A", "AA", "AAA", "MAX", "defined", "undefined" },
            normal.GetValues(GridKeywordSearchContext.PlaylistDetail, "djlevel"));
        AssertSetEqual(
            new[] { "beginner", "normal", "hyper", "another", "insane", "defined", "undefined" },
            normal.GetValues(GridKeywordSearchContext.ChartList, "difficulty"));
        AssertSetEqual(
            new[] { "veryhard", "hard", "normal", "easy", "veryeasy", "defined", "undefined" },
            normal.GetValues(GridKeywordSearchContext.ChartList, "judge"));
        AssertSetEqual(new[] { "defined", "undefined" }, normal.GetValues(GridKeywordSearchContext.ChartList, "judgepct"));
        AssertSetEqual(
            new[] { "ln", "mine", "random", "lnmode", "cn", "hcn", "stop", "scroll", "defined", "undefined" },
            normal.GetValues(GridKeywordSearchContext.ChartList, "feature"));
        AssertSetEqual(new[] { "defined", "undefined" }, normal.GetValues(GridKeywordSearchContext.ChartList, "peakdensity"));
        AssertSetEqual(new[] { "defined", "undefined" }, normal.GetValues(GridKeywordSearchContext.ChartList, "bp"));

        AssertSetEqual(
            new[] { "nosong", "NP", "F", "AE", "LAE", "EC", "NC", "HC", "EXH", "FC", "PF", "MAX", "defined", "undefined" },
            playHistory.GetValues(GridKeywordSearchContext.PlayHistory, "oldclear"));
        AssertSetEqual(new[] { "true", "false" }, playHistory.GetValues(GridKeywordSearchContext.PlayHistory, "finalized"));
        AssertSetEqual(Enumerable.Range(1, 12).Select(value => value.ToString()), playHistory.GetValues(GridKeywordSearchContext.PlayHistory, "month"));
        AssertSetEqual(new[] { "score", "bp", "clear", "combo", "play" }, playHistory.GetValues(GridKeywordSearchContext.PlayHistory, "type"));

        Assert.AreEqual(0, normal.GetValues(GridKeywordSearchContext.ChartList, "clear").Count(value => value is "defined" or "undefined"));
        AssertSetEqual(Array.Empty<string>(), normal.GetValues(GridKeywordSearchContext.ChartList, "clear").Where(value => value is "undef" or "null"));
    }

    [TestMethod]
    public void KsaCatalog_ExactAuthoritySetsCoverEveryFieldAliasAndContext()
    {
        KeywordSearchCatalogSnapshot snapshot = new(41L, ["Alpha", "Beta"]);
        string[] mainClear = ["nosong", "NP", "F", "AE", "LAE", "EC", "NC", "HC", "EXH", "FC", "PF", "MAX"];
        string[] playHistoryClear = ["nosong", "NP", "F", "AE", "LAE", "EC", "NC", "HC", "EXH", "FC", "PF", "MAX", "defined", "undefined"];
        string[] rank = ["F", "E", "D", "C", "B", "A", "AA", "AAA", "MAX", "defined", "undefined"];
        string[] difficulty = ["beginner", "normal", "hyper", "another", "insane", "defined", "undefined"];
        string[] judge = ["veryhard", "hard", "normal", "easy", "veryeasy", "defined", "undefined"];
        string[] defined = ["defined", "undefined"];
        string[] feature = ["ln", "mine", "random", "lnmode", "cn", "hcn", "stop", "scroll", "defined", "undefined"];
        string[] months = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12"];
        string[] playHistoryTypes = ["score", "bp", "clear", "combo", "play"];

        AssertSetEqual(mainClear, snapshot.GetValues(GridKeywordSearchContext.ChartList, "clear"));
        AssertSetEqual(mainClear, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, "clear"));
        AssertSetEqual(playHistoryClear, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, "clear"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, "clear"));
        AssertSetEqual(playHistoryClear, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, "oldclear"));
        AssertSetEqual(playHistoryClear, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, "newclear"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.ChartList, "oldclear"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.ChartList, "newclear"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, "oldclear"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, "newclear"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, "oldclear"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, "newclear"));

        foreach (string field in new[] { "rank", "djlevel", "dj" })
        {
            AssertSetEqual(rank, snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(rank, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlayHistory, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, field));
        }
        foreach (string field in new[] { "difficulty" })
        {
            AssertSetEqual(difficulty, snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(difficulty, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlayHistory, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, field));
        }
        foreach (string field in new[] { "judge" })
        {
            AssertSetEqual(judge, snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(judge, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlayHistory, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, field));
        }
        foreach (string field in new[] { "judge%", "judgepct" })
        {
            AssertSetEqual(defined, snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(defined, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlayHistory, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, field));
        }
        foreach (string field in new[] { "feature" })
        {
            AssertSetEqual(feature, snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(feature, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlayHistory, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, field));
        }

        string[] chartInfoAliases =
        [
            "level", "mainbpm", "maxbpm", "minbpm", "duration", "length", "notes",
            "long", "ln", "scratch", "total", "tn", "t/n", "density", "peak",
            "peakdensity", "end", "enddensity", "soflan"
        ];
        foreach (string field in chartInfoAliases)
        {
            AssertSetEqual(defined, snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(defined, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlayHistory, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, field));
        }
        foreach (string field in new[] { "rate", "score", "combo", "bp" })
        {
            AssertSetEqual(defined, snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(defined, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlayHistory, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, field));
        }

        AssertSetEqual(new[] { "true", "false" }, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, "finalized"));
        AssertSetEqual(months, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, "month"));
        AssertSetEqual(playHistoryTypes, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, "type"));
        AssertSetEqual(playHistoryTypes, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, "kind"));
        foreach (string field in new[] { "finalized", "month", "type", "kind" })
        {
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
        }
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, "finalized"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, "month"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, "type"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, "kind"));

        foreach (string field in new[] { "playlist", "ref", "table" })
        {
            AssertSetEqual(new[] { "Alpha", "Beta" }, snapshot.GetValues(GridKeywordSearchContext.ChartList, field));
            AssertSetEqual(new[] { "Alpha", "Beta" }, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, field));
            AssertSetEqual(new[] { "Alpha", "Beta" }, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, field));
            AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, field));
        }

        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.ChartList, "undef"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.ChartList, "null"));
        Assert.IsFalse(snapshot.GetValues(GridKeywordSearchContext.ChartList, "clear").Contains("defined"));
        Assert.IsFalse(snapshot.GetValues(GridKeywordSearchContext.ChartList, "clear").Contains("undefined"));
    }

    [TestMethod]
    public void KsaCatalog_DynamicPlaylistSnapshotIsTrimmedDedupedSortedAndRevisionStamped()
    {
        KeywordSearchCatalogSnapshot snapshot = new(
            31L,
            new[] { " Beta ", "alpha", "ALPHA", string.Empty, "  " });

        Assert.AreEqual(31L, snapshot.Revision);
        CollectionAssert.AreEqual(new[] { "alpha", "Beta" }, snapshot.PlaylistNames.ToArray());
        AssertSetEqual(new[] { "alpha", "Beta" }, snapshot.GetValues(GridKeywordSearchContext.ChartList, "playlist"));
        AssertSetEqual(new[] { "alpha", "Beta" }, snapshot.GetValues(GridKeywordSearchContext.PlaylistDetail, "ref"));
        AssertSetEqual(new[] { "alpha", "Beta" }, snapshot.GetValues(GridKeywordSearchContext.PlayHistory, "table"));
        AssertSetEqual(Array.Empty<string>(), snapshot.GetValues(GridKeywordSearchContext.PlaylistSummary, "playlist"));
    }

    private sealed class SearchAssistanceSettingsStore : IKeywordSearchHistorySettingsStore, IKeywordSearchFavoritesSettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;

        public string KeywordSearchFavorites { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchFavorites { get; set; } = string.Empty;
    }

    private static void AssertSetEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        HashSet<T> expectedSet = new(expected);
        HashSet<T> actualSet = new(actual);
        Assert.IsTrue(
            expectedSet.SetEquals(actualSet),
            $"Expected {{{string.Join(", ", expectedSet)}}}, actual {{{string.Join(", ", actualSet)}}}.");
    }

    private static ChartFile CreateFile()
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Title = "Alpha Title", RawTitle = "Alpha Title", Artist = "ArtistX", RawArtist = "ArtistX", Genre = "GenreX", Tag = "TagX", Path = @"C:\Songs\Alpha\chart.bms", Md5 = "abcdefabcdefabcdefabcdefabcdefab", Sha256 = "1234567890123456789012345678901234567890123456789012345678901234" };
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

    private static PlaylistReferenceIndex CreatePlaylistReferenceIndex(ChartFile file, params BMSTable[] tables)
    {
        PlaylistReferenceIndex index = PlaylistReferenceIndex.Empty;
        foreach (BMSTable table in tables ?? [])
        {
            table.entries = [new BMSTableEntry(file)];
            index.ReplaceSnapshotTable(new PlaylistReferenceTableSnapshot(table, table.symbol, table.name, table.entries));
        }
        return index;
    }

    private static PlayHistoryRow CreatePlayHistoryRow(bool finalized, DateTimeOffset? playedAt = null)
    {
        ChartFile file = CreateFile();
        BMSTable table = CreateTable("Satellite sl", "SL");
        var resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromCharts([file]);
        var projectionIndex = PlayHistoryProjectionIndex.Create(
            resolveIndex,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [file.Md5] = file.Sha256 },
            (md5, sha256) => new PlaylistReferenceDisplay(
                [new PlaylistReferenceTableDisplaySnapshot(table.symbol, table.name)]),
            (sha256, md5) => null);
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2(@"C:\LR2\score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 1,
                        hash = file.Md5,
                        played_at = (playedAt ?? new DateTimeOffset(2026, 6, 19, 12, 34, 56, TimeSpan.Zero)).ToUnixTimeSeconds(),
                        finalized = finalized ? 1 : 0,
                        score_write_type = "update",
                        old_clear = 3,
                        new_clear = finalized ? 4 : 3,
                        old_exscore = finalized ? 1000 : null,
                        new_exscore = finalized ? 1200 : null,
                        new_totalnotes = 1000,
                        new_playcount = 1,
                        playcount_delta = 1
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            projectionIndex);
        return projected.Rows.Single();
    }

    private static BMSTableEntry CreateBmsonPlaylistEntry(string sha256)
    {
        var entry = new BMSTableEntry();
        entry.MarkAsBmsonPlaylistIdentity(sha256);
        return entry;
    }

    private static ChartListSourceRow CreateSourceRow(ChartFile file, Func<ChartListSourceRow, PlaylistReferenceDisplay>? playlistReferenceDisplayProvider = null, BeMusicSeeker.Models.ChartDetails? chartInfo = null)
    {
        return ChartListSourceRow.FromChartFile(
            (file),
            ChartListSourceProjectionMode.OwnerBacked,
            playlistReferenceDisplayProvider: playlistReferenceDisplayProvider,
            chartInfoProjectionProvider: CreateChartInfoProvider(chartInfo));
    }

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateChartInfoProvider(params BeMusicSeeker.Models.ChartDetails?[] rows)
    {
        return chart => ResolveChartInfoByIdentity(chart, rows);
    }

    private static BeMusicSeeker.Models.ChartDetails ResolveChartInfoByIdentity(ChartFile chart, IEnumerable<BeMusicSeeker.Models.ChartDetails?> rows)
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

    private static BeMusicSeeker.Models.ChartDetails CreateChartInfo(int? level = 12, bool difficultyDefined = true, bool totalDefined = true, string? sha256 = null, string? md5 = null)
    {
        return new BeMusicSeeker.Models.ChartDetails
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

}
