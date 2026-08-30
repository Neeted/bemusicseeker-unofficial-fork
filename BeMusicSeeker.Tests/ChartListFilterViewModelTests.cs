using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartListFilterViewModelTests
{
    [TestMethod]
    public void ModeFilter_RejectsNoneButRefreshesBindingState()
    {
        var filters = CreateFilters();
        int changeCount = 0;
        List<string> propertyNames = [];
        filters.ModeFilterChanged += (_, _) => changeCount++;
        filters.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        filters.ModeFilter = ChartModeFilter._7KEYS;
        filters.ModeFilter = ChartModeFilter.None;

        Assert.AreEqual(ChartModeFilter._7KEYS, filters.ModeFilter);
        Assert.AreEqual(1, changeCount);
        Assert.AreEqual(2, propertyNames.Count);
        Assert.AreEqual(nameof(ChartListFilterViewModel.ModeFilter), propertyNames[0]);
        Assert.AreEqual(nameof(ChartListFilterViewModel.ModeFilter), propertyNames[1]);
    }

    [TestMethod]
    public void CaptureSnapshot_PreservesRawKeywordAndCanonicalModeTogether()
    {
        var filters = CreateFilters();
        int changeCount = 0;
        filters.KeywordFilterChanged += (_, _) => changeCount++;

        filters.KeywordFilter = "  title:Alpha  ";
        filters.ModeFilter = ChartModeFilter._5KEYS | ChartModeFilter._14KEYS;
        ChartListFilterSnapshot snapshot = filters.CaptureSnapshot();

        Assert.AreEqual(1, changeCount);
        Assert.AreEqual("  title:Alpha  ", snapshot.KeywordFilter);
        Assert.AreEqual(ChartModeFilter._5KEYS | ChartModeFilter._14KEYS, snapshot.ModeFilter);
    }

    [TestMethod]
    public void KeywordSearchAssistanceOwner_UsesContextCandidatesAndPersistedHistory()
    {
        var store = new InMemoryKeywordSearchHistorySettingsStore
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["title:old"])
        };
        var filters = new ChartListFilterViewModel(
            store,
            new InMemoryKeywordSearchFavoritesSettingsStore());

        filters.UpdateKeywordSearchContext(GridKeywordSearchContext.ChartList, []);
        filters.KeywordFilter = "memo:alpha";

        StringAssert.Contains(filters.KeywordSearchWarningText, "memo");

        filters.UpdateKeywordSearchContext(
            GridKeywordSearchContext.PlaylistDetail,
            ["Beta", "Alpha", "alpha"]);
        StringAssert.Contains(filters.KeywordSearchHelpText, "memo");

        const string valueText = "playlist:a";
        KeywordSearchPresentationState valueState = filters.FocusKeywordSearch(valueText, valueText.Length);
        KeywordSearchPresentationSection values = valueState.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.Values);
        Assert.AreEqual(1, values.Items.Count);
        Assert.AreEqual("Alpha", values.Items[0].DisplayText);

        KeywordSearchPresentationState emptyState = filters.FocusKeywordSearch(string.Empty, 0);
        KeywordSearchPresentationSection history = emptyState.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.History);
        Assert.AreEqual("title:old", history.Items.Single().Query);

        filters.CommitKeywordSearchHistory("title:new");
        Assert.AreEqual("title:new", KeywordSearchHistoryStore.Deserialize(store.KeywordSearchHistory)[0]);
    }

    [TestMethod]
    public void KeywordSearchAssistanceAdapter_UsesImmutableContextSnapshotAndRevision()
    {
        var filters = new ChartListFilterViewModel(
            new InMemoryKeywordSearchHistorySettingsStore(),
            new InMemoryKeywordSearchFavoritesSettingsStore());

        filters.UpdateKeywordSearchContext(
            GridKeywordSearchContext.PlaylistDetail,
            [" Beta ", "Alpha", "alpha"]);

        const string text = "playlist:a";
        KeywordSearchPresentationState state = filters.FocusKeywordSearch(text, text.Length);
        KeywordSearchPresentationItem candidate = state.VisibleItems.Single(item => item.DisplayText == "Alpha");

        Assert.AreEqual(GridKeywordSearchContext.PlaylistDetail, state.Context);
        Assert.AreEqual(1L, state.CatalogRevision);
        Assert.AreEqual(1, state.VisibleItems.Count);

        KeywordSearchApplyResult result = filters.TryApplyKeywordSearchPresentationItem(
            candidate,
            text,
            text.Length,
            state.Context,
            state.CatalogRevision);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("playlist:Alpha ", result.Text);
        Assert.AreEqual(result.Text.Length, result.CaretIndex);

        KeywordSearchPresentationState closed = filters.BlurKeywordSearch();
        Assert.IsFalse(closed.IsOpen);
    }

    [TestMethod]
    public void KeywordFilter_UpdatesWarningAndContextHelpNotifications()
    {
        var filters = CreateFilters();
        List<string> propertyNames = [];
        filters.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        filters.UpdateKeywordSearchContext(GridKeywordSearchContext.PlayHistory, []);
        filters.KeywordFilter = "memo:alpha";

        Assert.IsTrue(propertyNames.Contains(nameof(ChartListFilterViewModel.KeywordSearchWarningText)));
        Assert.IsTrue(propertyNames.Contains(nameof(ChartListFilterViewModel.HasKeywordSearchWarning)));
        Assert.IsTrue(propertyNames.Contains(nameof(ChartListFilterViewModel.KeywordSearchHelpText)));
        Assert.IsTrue(filters.KeywordSearchHelpText.IndexOf("finalized", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [TestMethod]
    public void KeywordFilter_InvalidPlayHistoryDateShowsLocalizedWarning()
    {
        var filters = CreateFilters();
        filters.UpdateKeywordSearchContext(GridKeywordSearchContext.PlayHistory, []);

        filters.KeywordFilter = "date:\"2026/06/19 12:34:56..\"";

        Assert.IsTrue(filters.HasKeywordSearchWarning);
        Assert.IsFalse(string.IsNullOrWhiteSpace(filters.KeywordSearchWarningText));
    }

    [TestMethod]
    public void PlayHistoryDateSearchTerm_AppendsWithoutNormalizingExistingKeyword()
    {
        Assert.IsTrue(PlayHistoryDateSearchTerm.TryParse(
            "2026/08/27 10:22:50..2026/08/27 11:04:12",
            out PlayHistoryDateSearchTerm term));

        const string clause = "date:\"2026/08/27 10:22:50..2026/08/27 11:04:12\"";
        Assert.AreEqual(clause, term.AppendTo(string.Empty));
        Assert.AreEqual("title:alpha " + clause, term.AppendTo("title:alpha "));
        Assert.AreEqual("title:alpha\t" + clause, term.AppendTo("title:alpha\t"));
        Assert.AreEqual("title:alpha " + clause, term.AppendTo("title:alpha"));
        Assert.AreEqual("date:bad " + clause, term.AppendTo("date:bad"));
    }

    private static ChartListFilterViewModel CreateFilters()
    {
        return new ChartListFilterViewModel(
            new InMemoryKeywordSearchHistorySettingsStore(),
            new InMemoryKeywordSearchFavoritesSettingsStore());
    }

    private sealed class InMemoryKeywordSearchHistorySettingsStore : IKeywordSearchHistorySettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;
    }

    private sealed class InMemoryKeywordSearchFavoritesSettingsStore : IKeywordSearchFavoritesSettingsStore
    {
        public string KeywordSearchFavorites { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchFavorites { get; set; } = string.Empty;
    }
}
