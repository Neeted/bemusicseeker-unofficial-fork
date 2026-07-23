using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class KeywordSearchPresentationTests
{
    [TestInitialize]
    public void TestInitialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

    [TestMethod]
    public void BuildKeywordSearchWarningText_UsesContextSpecificFields()
    {
        string chartListWarning = KeywordSearchPresentationText.BuildWarningText("memo:alpha", GridKeywordSearchContext.ChartList);
        string playlistDetailWarning = KeywordSearchPresentationText.BuildWarningText("memo:alpha", GridKeywordSearchContext.PlaylistDetail);
        string summaryWarning = KeywordSearchPresentationText.BuildWarningText("memo:alpha", GridKeywordSearchContext.PlaylistSummary);
        string playHistoryWarning = KeywordSearchPresentationText.BuildWarningText("finalized:false memo:alpha", GridKeywordSearchContext.PlayHistory);

        StringAssert.Contains(chartListWarning, "memo");
        Assert.AreEqual(string.Empty, playlistDetailWarning);
        StringAssert.Contains(summaryWarning, "memo");
        StringAssert.Contains(playHistoryWarning, "memo");
        Assert.IsFalse(playHistoryWarning.Contains("finalized"));
    }

    [TestMethod]
    public void BuildKeywordSearchWarningText_ReportsInvalidSyntax()
    {
        string warning = KeywordSearchPresentationText.BuildWarningText("title: - | title:re:[", GridKeywordSearchContext.ChartList);

        StringAssert.Contains(warning, "title");
        StringAssert.Contains(warning, "-");
        StringAssert.Contains(warning, "OR");
        StringAssert.Contains(warning, "[");
    }

    [TestMethod]
    public void PlaylistSummaryKeywordFilterChangedThroughWorkspaceUpdatesWarningPresentation()
    {
        var viewModel = MainWindowViewModelTestFactory.Create();

        viewModel.PlaylistWorkspace.PlaylistSummaryKeywordFilter = "memo:alpha";

        StringAssert.Contains(viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchWarningText, "memo");
        Assert.IsTrue(viewModel.PlaylistWorkspace.HasPlaylistSummaryKeywordSearchWarning);
    }

    [TestMethod]
    public void BuildKeywordSearchHelpText_ContainsContextFields()
    {
        string chartListHelp = KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.ChartList);
        string playlistDetailHelp = KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.PlaylistDetail);
        string summaryHelp = KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.PlaylistSummary);
        string playHistoryHelp = KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.PlayHistory);

        StringAssert.Contains(chartListHelp, "sha256");
        StringAssert.Contains(chartListHelp, "clear");
        StringAssert.Contains(chartListHelp, "rate");
        StringAssert.Contains(chartListHelp, "bp");
        StringAssert.Contains(playlistDetailHelp, "memo");
        StringAssert.Contains(playlistDetailHelp, "clear");
        StringAssert.Contains(playlistDetailHelp, "rate");
        StringAssert.Contains(playlistDetailHelp, "bp");
        StringAssert.Contains(summaryHelp, "symbol");
        StringAssert.Contains(playHistoryHelp, "finalized");
        StringAssert.Contains(playHistoryHelp, "date");
    }

    [TestMethod]
    public void BuildKeywordSearchSuggestionHeaderText_DescribesSuggestionKind()
    {
        string fieldHeader = KeywordSearchPresentationText.BuildSuggestionHeaderText(KeywordSearchSuggestionKind.Field);
        string valueHeader = KeywordSearchPresentationText.BuildSuggestionHeaderText(KeywordSearchSuggestionKind.Value);
        string historyHeader = KeywordSearchPresentationText.BuildSuggestionHeaderText(KeywordSearchSuggestionKind.History);

        Assert.IsFalse(string.IsNullOrWhiteSpace(fieldHeader));
        Assert.IsFalse(string.IsNullOrWhiteSpace(valueHeader));
        Assert.IsFalse(string.IsNullOrWhiteSpace(historyHeader));
        Assert.AreNotEqual(fieldHeader, historyHeader);
        Assert.AreNotEqual(fieldHeader, valueHeader);
        Assert.AreNotEqual(valueHeader, historyHeader);
    }

    [TestMethod]
    public void BuildKeywordSearchHistorySuggestions_ReplacesWholeSearchText()
    {
        KeywordSearchSuggestionItem suggestion = KeywordSearchPresentationText.BuildHistorySuggestions(["title:alpha"], "current")
            [0];

        string applied = suggestion.Apply("current", out int caretIndex);

        Assert.AreEqual("title:alpha", applied);
        Assert.AreEqual("title:alpha".Length, caretIndex);
    }
}
