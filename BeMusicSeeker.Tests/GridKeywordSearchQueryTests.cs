using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

    private static TestableBmsFile CreateFile()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot();
        return file;
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
