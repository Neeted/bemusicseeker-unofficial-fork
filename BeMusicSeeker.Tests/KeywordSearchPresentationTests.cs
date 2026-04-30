using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class KeywordSearchPresentationTests
{
    [TestMethod]
    public void BuildKeywordSearchWarningText_UsesContextSpecificFields()
    {
        string bmsFileWarning = MainWindowViewModel.BuildKeywordSearchWarningText("memo:alpha", GridKeywordSearchContext.BmsFile);
        string playlistDetailWarning = MainWindowViewModel.BuildKeywordSearchWarningText("memo:alpha", GridKeywordSearchContext.PlaylistDetail);
        string summaryWarning = MainWindowViewModel.BuildKeywordSearchWarningText("memo:alpha", GridKeywordSearchContext.PlaylistSummary);

        StringAssert.Contains(bmsFileWarning, "memo");
        Assert.AreEqual(string.Empty, playlistDetailWarning);
        StringAssert.Contains(summaryWarning, "memo");
    }

    [TestMethod]
    public void BuildKeywordSearchWarningText_ReportsInvalidSyntax()
    {
        string warning = MainWindowViewModel.BuildKeywordSearchWarningText("title: - | title:re:[", GridKeywordSearchContext.BmsFile);

        StringAssert.Contains(warning, "title");
        StringAssert.Contains(warning, "-");
        StringAssert.Contains(warning, "OR");
        StringAssert.Contains(warning, "[");
    }

    [TestMethod]
    public void BuildKeywordSearchHelpText_ContainsContextFields()
    {
        string bmsFileHelp = MainWindowViewModel.BuildKeywordSearchHelpText(GridKeywordSearchContext.BmsFile);
        string playlistDetailHelp = MainWindowViewModel.BuildKeywordSearchHelpText(GridKeywordSearchContext.PlaylistDetail);
        string summaryHelp = MainWindowViewModel.BuildKeywordSearchHelpText(GridKeywordSearchContext.PlaylistSummary);

        StringAssert.Contains(bmsFileHelp, "sha256");
        StringAssert.Contains(bmsFileHelp, "clear");
        StringAssert.Contains(bmsFileHelp, "rate");
        StringAssert.Contains(bmsFileHelp, "bp");
        StringAssert.Contains(playlistDetailHelp, "memo");
        StringAssert.Contains(playlistDetailHelp, "clear");
        StringAssert.Contains(playlistDetailHelp, "rate");
        StringAssert.Contains(playlistDetailHelp, "bp");
        StringAssert.Contains(summaryHelp, "symbol");
    }

    [TestMethod]
    public void BuildKeywordSearchSuggestionHeaderText_DescribesSuggestionKind()
    {
        string fieldHeader = MainWindowViewModel.BuildKeywordSearchSuggestionHeaderText(KeywordSearchSuggestionKind.Field);
        string historyHeader = MainWindowViewModel.BuildKeywordSearchSuggestionHeaderText(KeywordSearchSuggestionKind.History);

        Assert.IsFalse(string.IsNullOrWhiteSpace(fieldHeader));
        Assert.IsFalse(string.IsNullOrWhiteSpace(historyHeader));
        Assert.AreNotEqual(fieldHeader, historyHeader);
    }

    [TestMethod]
    public void BuildKeywordSearchHistorySuggestions_ReplacesWholeSearchText()
    {
        KeywordSearchSuggestionItem suggestion = MainWindowViewModel.BuildKeywordSearchHistorySuggestions(new[] { "title:alpha" }, "current")
            [0];

        string applied = suggestion.Apply("current", out int caretIndex);

        Assert.AreEqual("title:alpha", applied);
        Assert.AreEqual("title:alpha".Length, caretIndex);
    }
}
