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
    /// <summary>通常語が題名・ジャンル・MD5・SHA256へ一致することを確認し、全体検索からメタデータや識別子を落とす誤りを検出します。</summary>
    [TestMethod]
    public void MatchesChartList_GlobalKeywordSearchesMetadataAndHashes()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("genrex").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("abcdef").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("1234567890").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    /// <summary>題名・作者・ハッシュを含む複数語は全て一致した場合だけ通すことを確認し、一語の一致でAND条件を満たす誤りを検出します。</summary>
    [TestMethod]
    public void MatchesChartList_UsesAndForMultipleTokens()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha artistx abcdef").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("alpha missing").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    /// <summary>題名・作者・各ハッシュの指定は指定項目だけへ一致し、別項目の値や非対応memoは通さないことを確認し、指定項目を全体検索へ広げる誤りを検出します。</summary>
    [TestMethod]
    public void MatchesChartList_FieldQueryLimitsSearchTarget()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:alpha artist:artistx md5:abcdef sha256:123456").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:artistx").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("memo:alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    /// <summary>複数の参照表を持つ譜面でplaylist・ref・tableと正規表現が表名へ一致し、記号だけでは一致しないことを確認し、表名を記号と取り違える誤りを検出します。</summary>
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

    /// <summary>SHA256で参照したbmsonでも表名の三別名検索が通り、記号だけは通らないことを確認し、BMS専用識別子に依存して参照表検索を欠落させる誤りを検出します。</summary>
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

    /// <summary>source行の題名・作者・分類・パス・各ハッシュと参照表に同時一致し、library行と同じ結果となることを確認し、source経路の投影または検索項目の欠落を検出します。</summary>
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

    /// <summary>bmsonの通常メタデータとモード・レベルをsource行に投影してlibrary行と同じ検索結果を得ることを確認し、BMS用投影に依存してbmsonの値を落とす誤りを検出します。</summary>
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

    /// <summary>BMSとbmsonの現行値を適用すると表示と識別子が更新され、詳細提供元のレベルと詳細参照は保持することを確認し、古い表示や詳細投影の切断を検出します。</summary>
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

    /// <summary>詳細とHARD・AAの成績を持つsource行で数値範囲と別名を組み合わせた検索がlibrary行と一致することを確認し、source経路だけ詳細や成績条件を落とす誤りを検出します。</summary>
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

    /// <summary>特徴・判定・クリア・ランク・EXスコア・BPの正規表現がsource行とlibrary行で一致することを確認し、数値や分類の検索文字列を経路ごとに変える誤りを検出します。</summary>
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

    /// <summary>未知の項目・空の値・空の項目名は譜面に一致しないことを確認し、不完全な指定を通常語や無条件一致へ置き換える誤りを検出します。</summary>
    [TestMethod]
    public void MatchesChartList_UnknownOrEmptyFieldQueryDoesNotMatch()
    {
        ChartFile file = CreateFile();

        Assert.IsFalse(GridKeywordSearchQuery.Parse("unknown:alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse(":alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    /// <summary>ドライブ付きパス・ドライブ名だけ・小文字パスが警告なしで一致することを確認し、Windowsのコロンを未知の検索項目と解釈する誤りを検出します。</summary>
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

    /// <summary>引用した複合題名とエスケープ引用符、閉じ引用符欠落を受理し、異なる句は除くことを確認し、引用句を単語へ分割したり互換入力を拒否する誤りを検出します。</summary>
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

    /// <summary>否定した作者だけを除外し、単独の否定記号は一致せず題名内のハイフンは保持することを確認し、否定の範囲や記号の解釈を取り違える誤りを検出します。</summary>
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

    /// <summary>同じ項目の選択肢と引用句にORを適用し、全選択肢不一致や空ORを除外することを確認し、ORを式全体の無条件一致へ広げる誤りを検出します。</summary>
    [TestMethod]
    public void MatchesChartList_OrSearchIsLimitedToTokenAlternatives()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:missing|alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha Title\"|missing").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:missing|other").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("|").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    /// <summary>全体および題名指定の正規表現が一致し、作者不一致と壊れた式は一致しないことを確認し、適用先の混同や不正正規表現の受理を検出します。</summary>
    [TestMethod]
    public void MatchesChartList_RegexSearchSupportsGlobalAndFieldQueries()
    {
        ChartFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("re:^alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:re:^alpha\\s+title$").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("artist:re:^alpha").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:re:[").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    /// <summary>memoの対応画面と非対応画面、および履歴だけの空date警告を区別することを確認し、表示先に関係なく同じ診断を出す誤りを検出します。</summary>
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

    /// <summary>未知項目・空指定・否定・OR・壊れた正規表現を同時に診断して検索結果は不一致を維持することを確認し、診断の欠落や警告による検索式の意味変更を検出します。</summary>
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

    /// <summary>引用句・否定・OR・正規表現を併用する正常式へ診断を出さないことを確認し、高度な有効構文を不正と判定する誤りを検出します。</summary>
    [TestMethod]
    public void GetDiagnostics_DoesNotReportValidAdvancedSyntax()
    {
        var query = GridKeywordSearchQuery.Parse("title:\"Alpha Title\" -artist:other title:alpha|beta title:re:^alpha");

        Assert.AreEqual(0, query.GetDiagnostics(GridKeywordSearchContext.ChartList).Count);
    }

    /// <summary>整数と小数・ミリ秒時間・丸め・難度・判定・複合特徴を利用者向け表記へ変換することを確認し、検索にも使う表示値の単位や分類の変換誤りを検出します。</summary>
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

    /// <summary>詳細の閉区間・比較・開区間と難度・特徴・scratchの条件で一致と不一致を区別することを確認し、範囲演算や詳細別名の誤解釈を検出します。</summary>
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

    /// <summary>値欠落と定義フラグ未設定をundefined・undef・nullで検索し、詳細全体の有無も区別することを確認し、欠損をゼロや定義済みとして扱う誤りを検出します。</summary>
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

    /// <summary>クリアとDJランクの別名・率の0から1の尺度・EXスコア等の範囲を検索し、EXH・PF・MAXも一致することを確認し、成績分類や率の百分率解釈を検出します。</summary>
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
        file = ChartFileProjection.WithScore(file, new ChartScoreSnapshot(ClearType.MAX, RankType.MAX));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:MAX dj:MAX").MatchesLibraryChartRow(LibraryChartRow.FromChartFile(file)));
    }

    /// <summary>未登録成績のランク・スコア・率・コンボ・BPは欠損別名に一致し、クリアの既定状態は定義済みとなることを確認し、成績項目の欠損区分を一律にする誤りを検出します。</summary>
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

    /// <summary>bmsonの詳細提供元からレベル・ノート数を表示し通常語と詳細検索が通ることを確認し、SHA256参照の詳細を表示や検索へ渡さない誤りを検出します。</summary>
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

    /// <summary>LR2のAssist履歴・forceEasy・Easy併用と内部Easyを区別してAEとEC検索へ反映することを確認し、画面のASSIST分類と検索用クリアの不一致を検出します。</summary>
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

    /// <summary>プレイリスト詳細の所持譜面成績でHARD表記とEXスコア・BP範囲が一致することを確認し、詳細行の成績投影を検索に使わない誤りを検出します。</summary>
    [TestMethod]
    public void MatchesPlaylistDetail_ScoreFieldsUseSourceSnapshot()
    {
        ChartFile file = CreateFile();
        file = ChartFileProjection.WithScore(file, ChartTestValues.Score(ClearType.HARD, RankType.AA, 850, 100, 1000, 900, 8, 0, false));
        var row = new PlaylistDetailSourceRow(new BMSTableEntry(file), (file));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:\"HARD CLEAR\" score:>=1800 bp:0..10").MatchesPlaylistDetail(row));
    }

    /// <summary>確定LR2履歴に対して日時・年月・種類・前後クリア・識別子・出典を検索し、異なる日付や未確定条件を除くことを確認し、履歴専用項目の混同を検出します。</summary>
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

    /// <summary>実際のローカル再生日時で両端を含む秒単位閉区間と外側の不一致を確認し、UTCのまま比較したり境界秒を落とす誤りを検出します。</summary>
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

    /// <summary>ハイフン・スラッシュ・連結の日付と秒単位時刻、および最大日を受理することを確認し、日付互換形式の拒否や最終日の上限溢れを検出します。</summary>
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

    /// <summary>項目を指定しない日時文字列は履歴の日付検索へ昇格しないことを確認し、通常語に暗黙のdate条件を追加する誤りを検出します。</summary>
    [TestMethod]
    public void MatchesPlayHistoryRow_UnqualifiedTimestampIsNotPromotedToDateCondition()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(
            finalized: true,
            playedAt: new DateTimeOffset(2026, 6, 19, 12, 34, 56, TimeSpan.Zero));

        Assert.IsFalse(GridKeywordSearchQuery.Parse(
            row.PlayedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)).MatchesPlayHistoryRow(row));
    }

    /// <summary>開区間・逆順・小数秒・時差・空白・区切り過多等の不正dateを診断して不一致とし、否定も全件一致にせず有効ORだけ通すことを確認し、日付エラーの隠蔽を検出します。</summary>
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

    /// <summary>同じ秒の二履歴を選ぶと同一の開始秒と終了秒を持つ検索句となることを確認し、同時刻選択を単一日や空の範囲へ広げる誤りを検出します。</summary>
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

    /// <summary>夏時間終了時の異なる二UTC時刻を指定地域の同じ壁時計秒として照合し、UTC表記では一致しないことを確認し、曖昧時刻の検索を絶対時刻へ置き換える誤りを検出します。</summary>
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

    /// <summary>未確定履歴が0・pendingと定義済みclearへ一致して確定後のHCへは一致しないことを確認し、未確定履歴へ完成成績を混ぜる誤りを検出します。</summary>
    [TestMethod]
    public void MatchesPlayHistoryRow_SupportsDiagnosticsFields()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(finalized: false);

        Assert.IsTrue(GridKeywordSearchQuery.Parse("finalized:0").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("finalized:pending").MatchesPlayHistoryRow(row));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("clear:defined").MatchesPlayHistoryRow(row));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("clear:HC").MatchesPlayHistoryRow(row));
    }

    /// <summary>通常検索をAND、選択した集計カード同士をORとして適用することを確認し、カードの一致だけで通常検索を通したり全カードをANDにする誤りを検出します。</summary>
    [TestMethod]
    public void MatchesPlayHistoryKeywordAndSummaryFilters_CombinesKeywordAndCards()
    {
        PlayHistoryRow row = CreatePlayHistoryRow(finalized: true);

        Assert.IsTrue(PlayHistoryWorkflowOwner.MatchesKeywordAndSummaryFilters(row, "artist:artistx", "type:bp", "type:score"));
        Assert.IsFalse(PlayHistoryWorkflowOwner.MatchesKeywordAndSummaryFilters(row, "artist:missing", "type:score"));
        Assert.IsFalse(PlayHistoryWorkflowOwner.MatchesKeywordAndSummaryFilters(row, "artist:artistx", "type:bp", "type:clear newclear:EC"));
    }

    /// <summary>output項目が出力先の表示名に一致する行だけを通すことを確認し、別の出力先や内部名で一覧検索を判定する誤りを検出します。</summary>
    [TestMethod]
    public void MatchesPlaylistSummary_OutputFieldUsesOutputBaseDisplayName()
    {
        PlaylistSummaryRow positive = new() { OutputBaseDisplayName = "Output Alpha" };
        PlaylistSummaryRow negative = new() { OutputBaseDisplayName = "Output Beta" };
        var query = GridKeywordSearchQuery.Parse("output:\"Output Alpha\"");

        Assert.IsTrue(query.MatchesPlaylistSummary(positive));
        Assert.IsFalse(query.MatchesPlaylistSummary(negative));
    }

    /// <summary>引用外のTab後は項目候補、引用内のTab後は表名値候補を出すことを確認し、候補解析で引用状態を無視して区切る誤りを検出します。</summary>
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

    /// <summary>開始引用符の直前にバックスラッシュがある未完引用句内のTabでは項目・値候補を開かないことを確認し、検索と支援で引用句の境界を変える誤りを検出します。</summary>
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

    /// <summary>各表示先と項目別名に対応する挿入可能な分類値・欠損語・動的表名だけを返すことを確認し、非対応項目や解析専用の別名を候補へ混入させる誤りを検出します。</summary>
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

    /// <summary>空白付き・大小文字重複・空の表名を受け取り、正規化済み候補と指定版を各対応画面へ渡すことを確認し、重複候補や異なる版の候補を使う誤りを検出します。</summary>
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
