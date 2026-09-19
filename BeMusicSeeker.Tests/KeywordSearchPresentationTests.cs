using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Properties;
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
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();

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
    public void KsaEmptyPresentation_OrdersProjectedFavoritesHistoryAndFieldsByContext()
    {
        PresentationSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["memo:note", "title:chart", "title:pinned"]),
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["title:pinned", "memo:favorite"])
        };
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(7L, ["Alpha"]));

        KeywordSearchPresentationState state = assistance.Focus(string.Empty, 0);

        Assert.IsTrue(state.IsOpen);
        CollectionAssert.AreEqual(
            new[]
            {
                KeywordSearchPresentationSectionKind.Favorites,
                KeywordSearchPresentationSectionKind.History,
                KeywordSearchPresentationSectionKind.Fields
            },
            state.Sections.Select(section => section.Kind).ToArray());
        Assert.AreEqual(Resources.Keyword_search_completion_favorites_header, state.Sections[0].HeaderText);
        Assert.AreEqual(Resources.Keyword_search_completion_history_header, state.Sections[1].HeaderText);
        Assert.AreEqual(Resources.Keyword_search_completion_fields_header, state.Sections[2].HeaderText);
        CollectionAssert.AreEqual(new[] { "title:pinned" }, state.Sections[0].Items.Select(item => item.Query).ToArray());
        CollectionAssert.AreEqual(new[] { "title:chart" }, state.Sections[1].Items.Select(item => item.Query).ToArray());
        Assert.IsTrue(state.Sections[2].Items.Any(item => item.DisplayText == "title:"));
        Assert.IsFalse(state.Sections[0].Items[0].CanAddFavorite);
        Assert.IsTrue(state.Sections[1].Items[0].CanAddFavorite);
        Assert.IsTrue(state.Sections[1].Items[0].CanDeleteHistory);

        KeywordSearchPresentationState detailState = assistance.UpdateContext(
            GridKeywordSearchContext.PlaylistDetail,
            new KeywordSearchCatalogSnapshot(8L, ["Alpha"]));
        Assert.IsTrue(detailState.Sections.Any(section => section.Items.Any(item => item.Query == "memo:note")));
    }

    [TestMethod]
    public void KsaPresentationSections_ExposeAllRowsAndMarkIndependentScrollCapacity()
    {
        string[] favorites = Enumerable.Range(1, 6).Select(index => "title:fav" + index).ToArray();
        string[] history = Enumerable.Range(1, 6).Select(index => "title:hist" + index).ToArray();
        PresentationSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(history),
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(favorites)
        };
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            KeywordSearchCatalogSnapshot.Empty);

        KeywordSearchPresentationState emptyState = assistance.Focus(string.Empty, 0);
        KeywordSearchPresentationSection favoriteSection = emptyState.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.Favorites);
        KeywordSearchPresentationSection historySection = emptyState.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.History);

        Assert.AreEqual(6, favoriteSection.Items.Count);
        Assert.IsTrue(favoriteSection.IsScrollable);
        Assert.AreEqual(6, historySection.Items.Count);
        Assert.IsTrue(historySection.IsScrollable);

        assistance.UpdateContext(
            GridKeywordSearchContext.PlayHistory,
            KeywordSearchCatalogSnapshot.Empty);
        KeywordSearchPresentationState valuesState = assistance.Focus("month:", "month:".Length);
        KeywordSearchPresentationSection valuesSection = valuesState.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.Values);

        Assert.AreEqual(12, valuesSection.Items.Count);
        Assert.IsTrue(valuesSection.IsScrollable);
    }

    [TestMethod]
    public void KsaFieldPresentation_CompletesCaseInsensitivePrefixAndPreservesNegation()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(3L, []));

        KeywordSearchPresentationState state = assistance.Focus("-Ti", 3);
        KeywordSearchPresentationItem title = state.VisibleItems.Single(item => item.DisplayText == "-title:");

        KeywordSearchApplyResult result = assistance.TryApply(
            title,
            "-Ti",
            3,
            GridKeywordSearchContext.ChartList,
            3L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("-title:", result.Text);
        Assert.AreEqual("-title:".Length, result.CaretIndex);
    }

    [TestMethod]
    public void KsaValuePresentation_UsesSnapshotAndAppliesOneAsciiSeparatorWithCaretAfterIt()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(12L, ["Alpha One", "Alpine", "alpha one"]));

        const string text = "playlist:a";
        KeywordSearchPresentationState state = assistance.Focus(text, text.Length);
        KeywordSearchPresentationSection values = state.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.Values);
        Assert.AreEqual(Resources.Keyword_search_completion_values_header, values.HeaderText);
        KeywordSearchPresentationItem value = values.Items.Single(item => item.DisplayText == "Alpha One");

        KeywordSearchApplyResult result = assistance.TryApply(
            value,
            text,
            text.Length,
            GridKeywordSearchContext.ChartList,
            12L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("playlist:\"Alpha One\" ", result.Text);
        Assert.AreEqual(result.Text.Length, result.CaretIndex);
        Assert.AreEqual(2, state.VisibleItems.Count);
        KeywordSearchPresentationState nextFields = assistance.Refresh(result.Text, result.CaretIndex);
        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, nextFields.Sections.Single().Kind);
    }

    [TestMethod]
    public void KsaValueApply_NormalizesSeparatorBeforeExistingSuffix()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(13L, ["Alpha", "Alpine"]));

        const string text = "playlist:aXYZ";
        KeywordSearchPresentationState state = assistance.Focus(text, "playlist:a".Length);
        KeywordSearchPresentationItem value = state.VisibleItems.Single(item => item.DisplayText == "Alpha");

        KeywordSearchApplyResult result = assistance.TryApply(
            value,
            text,
            "playlist:a".Length,
            GridKeywordSearchContext.ChartList,
            13L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("playlist:Alpha XYZ", result.Text);
        Assert.AreEqual("playlist:Alpha ".Length, result.CaretIndex);
    }

    [TestMethod]
    public void KsaValueApply_QuotedCaretInMiddleReplacesClosingQuoteAndPreservesFollowingClause()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(14L, ["Alpha", "Alpine"]));

        const string text = "playlist:\"Al\" title:x";
        int caretIndex = "playlist:\"Al".Length;
        KeywordSearchPresentationState state = assistance.Focus(text, caretIndex);
        KeywordSearchPresentationItem value = state.VisibleItems.Single(
            item => item.DisplayText == "Alpha");

        KeywordSearchApplyResult result = assistance.TryApply(
            value,
            text,
            caretIndex,
            GridKeywordSearchContext.ChartList,
            14L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("playlist:\"Alpha\" title:x", result.Text);
        Assert.AreEqual("playlist:\"Alpha\" ".Length, result.CaretIndex);
    }

    [TestMethod]
    public void KsaValueApply_QuotedCaretInMiddleEscapesQuoteBackslashAndPipeValues()
    {
        (string value, string expectedText)[] cases =
        [
            ("Alpha\"Mix", "playlist:\"Alpha\\\"Mix\" title:x"),
            ("Alpha\\Mix", "playlist:\"Alpha\\\\Mix\" title:x"),
            ("Alpha|Mix", "playlist:\"Alpha|Mix\" title:x")
        ];

        foreach ((string value, string expectedText) in cases)
        {
            PresentationSettingsStore store = new();
            KeywordSearchSavedQueryOwner savedQueries = new(
                KeywordSearchSavedQueryScope.Normal,
                store,
                store);
            KeywordSearchAssistanceOwner assistance = new(
                savedQueries,
                GridKeywordSearchContext.ChartList,
                new KeywordSearchCatalogSnapshot(15L, [value]));

            const string text = "playlist:\"Al\" title:x";
            int caretIndex = "playlist:\"Al".Length;
            KeywordSearchPresentationState state = assistance.Focus(text, caretIndex);
            KeywordSearchPresentationItem candidate = state.VisibleItems.Single(
                item => item.DisplayText == value);

            KeywordSearchApplyResult result = assistance.TryApply(
                candidate,
                text,
                caretIndex,
                GridKeywordSearchContext.ChartList,
                15L);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(expectedText, result.Text);
            Assert.AreEqual(expectedText.IndexOf(" title:x", StringComparison.Ordinal) + 1, result.CaretIndex);
        }
    }

    [TestMethod]
    public void KsaValueApply_FiniteQuotedCaretInMiddlePreservesClosingQuoteAndSuffix()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            KeywordSearchCatalogSnapshot.Empty);

        const string text = "clear:\"N\" title:x";
        int caretIndex = "clear:\"N".Length;
        KeywordSearchPresentationState state = assistance.Focus(text, caretIndex);
        KeywordSearchPresentationItem candidate = state.VisibleItems.Single(
            item => item.DisplayText == "NP");

        KeywordSearchApplyResult result = assistance.TryApply(
            candidate,
            text,
            caretIndex,
            GridKeywordSearchContext.ChartList,
            0L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("clear:\"NP\" title:x", result.Text);
        Assert.AreEqual("clear:\"NP\" ".Length, result.CaretIndex);
    }

    [TestMethod]
    public void KsaValueApply_ExistingUnquotedSuffixTransitionsToFields()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            KeywordSearchCatalogSnapshot.Empty);

        const string text = "clear:N tail";
        int caretIndex = "clear:N".Length;
        KeywordSearchPresentationState state = assistance.Focus(text, caretIndex);
        KeywordSearchPresentationItem candidate = state.VisibleItems.Single(
            item => item.DisplayText == "NP");

        KeywordSearchApplyResult result = assistance.TryApply(
            candidate,
            text,
            caretIndex,
            GridKeywordSearchContext.ChartList,
            0L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("clear:NP tail", result.Text);
        Assert.AreEqual("clear:NP ".Length, result.CaretIndex);

        KeywordSearchPresentationState next = assistance.Refresh(result.Text, result.CaretIndex);
        KeywordSearchPresentationSection fields = next.Sections.Single();
        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, fields.Kind);
        Assert.IsTrue(fields.Items.Any(item => item.DisplayText == "title:"));
    }

    [TestMethod]
    public void KsaValuePresentation_WhitespaceInsideQuotedValueRemainsValueContext()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(16L, ["Alpha One"]));

        const string text = "playlist:\"Alpha One\" tail";
        int caretIndex = "playlist:\"Alpha ".Length;
        KeywordSearchPresentationState state = assistance.Focus(text, caretIndex);

        Assert.AreEqual(KeywordSearchPresentationSectionKind.Values, state.Sections.Single().Kind);
        Assert.IsFalse(state.Sections.Any(section => section.Kind == KeywordSearchPresentationSectionKind.Fields));
        Assert.IsTrue(state.VisibleItems.Any(item => item.DisplayText == "Alpha One"));
    }

    [TestMethod]
    public void KsaSavedRowApply_ReplacesWholeQueryAndCommitsHistory()
    {
        PresentationSettingsStore store = new()
        {
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["title:favorite"])
        };
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(4L, []));

        KeywordSearchPresentationState state = assistance.Focus(string.Empty, 0);
        KeywordSearchPresentationItem favorite = state.VisibleItems.Single(
            item => item.Kind == KeywordSearchPresentationItemKind.Favorite);

        KeywordSearchApplyResult result = assistance.TryApply(
            favorite,
            string.Empty,
            0,
            GridKeywordSearchContext.ChartList,
            4L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("title:favorite", result.Text);
        Assert.AreEqual(result.Text.Length, result.CaretIndex);
        CollectionAssert.AreEqual(
            new[] { "title:favorite" },
            KeywordSearchHistoryStore.Deserialize(store.KeywordSearchHistory).ToArray());
    }

    [TestMethod]
    public void KsaSummaryPresentation_IncludesOutputFieldAndNeverValues()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.PlaylistSummary,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.PlaylistSummary,
            KeywordSearchCatalogSnapshot.Empty);

        KeywordSearchPresentationState state = assistance.Focus("ou", 2);

        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(1, state.Sections.Count);
        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, state.Sections[0].Kind);
        Assert.IsTrue(state.VisibleItems.Any(item => item.DisplayText == "output:"));
        Assert.IsFalse(state.Sections.Any(section => section.Kind == KeywordSearchPresentationSectionKind.Values));
    }

    [TestMethod]
    public void KsaRevisionCandidate_RejectsSameShapeCandidateAfterSnapshotChanges()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(20L, ["Alpha"]));
        const string text = "playlist:a";
        KeywordSearchPresentationItem candidate = assistance.Focus(text, text.Length).VisibleItems.Single();

        assistance.UpdateCatalogSnapshot(new KeywordSearchCatalogSnapshot(21L, ["Beta"]));
        KeywordSearchApplyResult result = assistance.TryApply(
            candidate,
            text,
            text.Length,
            GridKeywordSearchContext.ChartList,
            20L);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, result.Failure);
        Assert.AreEqual(text, result.Text);
    }

    [TestMethod]
    public void KsaRevisionCandidate_RejectsTextCaretAndContextMismatches()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(22L, ["Alpha"]));
        const string text = "playlist:a";
        KeywordSearchPresentationItem candidate = assistance.Focus(text, text.Length).VisibleItems.Single();

        KeywordSearchApplyResult textResult = assistance.TryApply(
            candidate,
            "playlist:b",
            text.Length,
            GridKeywordSearchContext.ChartList,
            22L);
        KeywordSearchApplyResult caretResult = assistance.TryApply(
            candidate,
            text,
            text.Length - 1,
            GridKeywordSearchContext.ChartList,
            22L);
        KeywordSearchApplyResult contextResult = assistance.TryApply(
            candidate,
            text,
            text.Length,
            GridKeywordSearchContext.PlaylistDetail,
            22L);

        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, textResult.Failure);
        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, caretResult.Failure);
        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, contextResult.Failure);
    }

    [TestMethod]
    public void KsaRefresh_OnlyNotifiesWhenPresentationActuallyChanges()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(1L, []));
        int changed = 0;
        assistance.PresentationChanged += (_, _) => changed++;

        assistance.Focus("ti", 2);
        assistance.Refresh("ti", 2);
        assistance.SuppressCurrentSnapshot();
        assistance.Refresh("ti", 2);
        assistance.Refresh("tit", 3);

        Assert.AreEqual(3, changed);
    }

    private sealed class PresentationSettingsStore : IKeywordSearchHistorySettingsStore, IKeywordSearchFavoritesSettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;

        public string KeywordSearchFavorites { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchFavorites { get; set; } = string.Empty;
    }
}
