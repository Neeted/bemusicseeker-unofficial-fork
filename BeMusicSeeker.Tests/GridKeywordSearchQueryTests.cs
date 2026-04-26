using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class GridKeywordSearchQueryTests
{
    [TestMethod]
    public void MatchesBmsFile_GlobalKeywordSearchesMetadataPathPlaylistAndHashes()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("genrex").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("abcdef").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("1234567890").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_UsesAndForMultipleTokens()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha artistx abcdef").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("alpha missing").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_FieldQueryLimitsSearchTarget()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:alpha artist:artistx md5:abcdef sha256:123456").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:artistx").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("memo:alpha").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_UnknownOrEmptyFieldQueryDoesNotMatch()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsFalse(GridKeywordSearchQuery.Parse("unknown:alpha").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse(":alpha").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_QuoteSearchTreatsPhraseAsSingleToken()
    {
        TestableBmsFile file = CreateFile();
        TestableBmsFile quotedFile = new TestableBmsFile();
        quotedFile.SetTitleForTest("Alpha \"Quoted\"");

        Assert.IsTrue(GridKeywordSearchQuery.Parse("\"Alpha Title\"").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha Title\"").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha \\\"Quoted\\\"\"").MatchesBmsFile(quotedFile));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("\"Alpha Title").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("\"Alpha Missing\"").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_NegationExcludesMatchingRows()
    {
        TestableBmsFile file = CreateFile();
        TestableBmsFile hyphenatedFile = new TestableBmsFile();
        hyphenatedFile.SetTitleForTest("foo-bar");

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha -artist:other").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("alpha -artist:artistx").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("-").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("foo-bar").MatchesBmsFile(hyphenatedFile));
    }

    [TestMethod]
    public void MatchesBmsFile_OrSearchIsLimitedToTokenAlternatives()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:missing|alpha").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:\"Alpha Title\"|missing").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:missing|other").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("|").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_RegexSearchSupportsGlobalAndFieldQueries()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("re:^alpha").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:re:^alpha\\s+title$").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("artist:re:^alpha").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:re:[").MatchesBmsFile(file));
    }

    [TestMethod]
    public void GetDiagnostics_ReportsContextSpecificWarnings()
    {
        GridKeywordSearchQuery memoQuery = GridKeywordSearchQuery.Parse("memo:alpha");

        Assert.AreEqual(0, memoQuery.GetDiagnostics(GridKeywordSearchContext.PlaylistDetail).Count);
        Assert.AreEqual(GridKeywordSearchDiagnosticKind.UnknownField, memoQuery.GetDiagnostics(GridKeywordSearchContext.BmsFile)[0].Kind);
        Assert.AreEqual(GridKeywordSearchDiagnosticKind.UnknownField, memoQuery.GetDiagnostics(GridKeywordSearchContext.PlaylistSummary)[0].Kind);
    }

    [TestMethod]
    public void GetDiagnostics_ReportsInvalidConditionsWithoutChangingMatchSemantics()
    {
        TestableBmsFile file = CreateFile();
        GridKeywordSearchQuery query = GridKeywordSearchQuery.Parse("unknown:alpha title: - | title:re:[");
        GridKeywordSearchDiagnosticKind[] kinds = query.GetDiagnostics(GridKeywordSearchContext.BmsFile)
            .Select((GridKeywordSearchDiagnostic diagnostic) => diagnostic.Kind)
            .ToArray();

        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.UnknownField);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyFieldTerm);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyNegation);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.EmptyOr);
        CollectionAssert.Contains(kinds, GridKeywordSearchDiagnosticKind.InvalidRegex);
        Assert.IsFalse(query.MatchesBmsFile(file));
    }

    [TestMethod]
    public void GetDiagnostics_DoesNotReportValidAdvancedSyntax()
    {
        GridKeywordSearchQuery query = GridKeywordSearchQuery.Parse("title:\"Alpha Title\" -artist:other title:alpha|beta title:re:^alpha");

        Assert.AreEqual(0, query.GetDiagnostics(GridKeywordSearchContext.BmsFile).Count);
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
    public void MatchesBmsFile_ChartInfoNumericRangeAndAliases()
    {
        TestableBmsFile file = CreateFile();
        file.SetChartInfo(CreateChartInfo());

        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:10..12 notes:>=2000 duration:<124").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("difficulty:another feature:random tn:2.0..").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("-feature:mine scratch:12").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("feature:mine").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("difficulty:insane").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_ChartInfoDefinedUndefinedTerms()
    {
        TestableBmsFile file = CreateFile();
        file.SetChartInfo(CreateChartInfo(level: null, difficultyDefined: false, totalDefined: false));

        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:undefined difficulty:undefined total:undefined").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("level:defined").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("feature:defined").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("feature:undefined").MatchesBmsFile(CreateFile()));
    }

    [TestMethod]
    public void CreateFromBmsonSong_ExposesChartInfoForDisplayAndSearch()
    {
        LR2SongDBExtended.chart_info chartInfo = CreateChartInfo();
        LR2SongDBExtended.bmson_song song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\Alpha\chart.bmson",
            title = "Alpha Bmson",
            artist = "ArtistX",
            folder = "Alpha",
            mode_hint = "beat-7k",
            md5 = chartInfo.md5,
            sha256 = chartInfo.sha256,
            ChartInfo = chartInfo
        };

        PendingChartEntry row = PendingChartEntry.CreateFromBmsonSong(song);

        Assert.AreSame(chartInfo, row.ChartInfo);
        Assert.AreEqual("12", row.ChartLevelText);
        Assert.AreEqual(2500, row.ChartNotes);
        Assert.IsTrue(GridKeywordSearchQuery.Parse("level:12 feature:random notes:>=2000").MatchesBmsFile(row));
    }

    [TestMethod]
    public void CreateFieldCompletion_CompletesContextSpecificFields()
    {
        GridKeywordSearchCompletionResult bmsResult = GridKeywordSearchCompletion.CreateFieldCompletion("tit", 3, GridKeywordSearchContext.BmsFile);
        GridKeywordSearchCompletionResult negatedResult = GridKeywordSearchCompletion.CreateFieldCompletion("-ar", 3, GridKeywordSearchContext.BmsFile);
        GridKeywordSearchCompletionResult detailResult = GridKeywordSearchCompletion.CreateFieldCompletion("mem", 3, GridKeywordSearchContext.PlaylistDetail);
        GridKeywordSearchCompletionResult summaryResult = GridKeywordSearchCompletion.CreateFieldCompletion("na", 2, GridKeywordSearchContext.PlaylistSummary);

        Assert.IsTrue(bmsResult.Items.Any((KeywordSearchSuggestionItem item) => item.DisplayText == "title:"));
        Assert.IsTrue(negatedResult.Items.Any((KeywordSearchSuggestionItem item) => item.DisplayText == "-artist:"));
        Assert.IsTrue(detailResult.Items.Any((KeywordSearchSuggestionItem item) => item.DisplayText == "memo:"));
        Assert.IsTrue(summaryResult.Items.Any((KeywordSearchSuggestionItem item) => item.DisplayText == "name:"));
        Assert.AreEqual(0, GridKeywordSearchCompletion.CreateFieldCompletion("mem", 3, GridKeywordSearchContext.BmsFile).Items.Count);
    }

    [TestMethod]
    public void CreateFieldCompletion_DoesNotCompleteTermsAfterFieldSeparator()
    {
        GridKeywordSearchCompletionResult result = GridKeywordSearchCompletion.CreateFieldCompletion("title:alpha", 11, GridKeywordSearchContext.BmsFile);

        Assert.AreEqual(0, result.Items.Count);
    }

    [TestMethod]
    public void KeywordSearchSuggestionItem_ApplyReplacesOnlyFieldPrefix()
    {
        KeywordSearchSuggestionItem suggestion = GridKeywordSearchCompletion.CreateFieldCompletion("foo tit bar", 7, GridKeywordSearchContext.BmsFile)
            .Items
            .Single((KeywordSearchSuggestionItem item) => item.DisplayText == "title:");

        string applied = suggestion.Apply("foo tit bar", out int caretIndex);

        Assert.AreEqual("foo title: bar", applied);
        Assert.AreEqual("foo title:".Length, caretIndex);
    }

    [TestMethod]
    public void KeywordSearchHistoryStore_RoundTripsDistinctCappedHistory()
    {
        string[] entries = Enumerable.Range(0, KeywordSearchHistoryStore.MaxHistoryCount + 5)
            .Select((int index) => "title:" + index)
            .ToArray();
        string serialized = KeywordSearchHistoryStore.Serialize(entries);

        string[] restored = KeywordSearchHistoryStore.Deserialize(serialized).ToArray();

        Assert.AreEqual(KeywordSearchHistoryStore.MaxHistoryCount, restored.Length);
        Assert.AreEqual("title:0", restored[0]);
        CollectionAssert.DoesNotContain(restored, "title:24");
    }

    [TestMethod]
    public void KeywordSearchHistoryStore_AddEntryMovesDuplicateToFront()
    {
        string[] updated = KeywordSearchHistoryStore.AddEntry(new[] { "alpha", "beta", "gamma" }, " BETA ")
            .ToArray();

        CollectionAssert.AreEqual(new[] { "BETA", "alpha", "gamma" }, updated);
    }

    private static TestableBmsFile CreateFile()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot();
        return file;
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(int? level = 12, bool difficultyDefined = true, bool totalDefined = true)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
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
    }
}
