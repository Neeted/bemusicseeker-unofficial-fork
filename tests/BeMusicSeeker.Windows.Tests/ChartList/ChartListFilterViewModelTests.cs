using System.Globalization;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ChartListFilterTestDoubles;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class ChartListFilterViewModelTests
{

    /// <summary>通常・詳細・履歴の警告あり／なしで表示用言語通知だけを一回送り、入力・候補・保存値を保ちます。切離し中は通知せず再接続で現言語へ同期します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(GridKeywordSearchContext.ChartList, true)]
    [DataRow(GridKeywordSearchContext.ChartList, false)]
    [DataRow(GridKeywordSearchContext.PlaylistDetail, true)]
    [DataRow(GridKeywordSearchContext.PlaylistDetail, false)]
    [DataRow(GridKeywordSearchContext.PlayHistory, true)]
    [DataRow(GridKeywordSearchContext.PlayHistory, false)]
    public void KeywordSearchLanguage_RefreshesOnlyAttachedPresentation(GridKeywordSearchContext context, bool warning)
    {
        CultureInfo? previousCulture = Resources.Culture;
        var historyStore = new InMemoryKeywordSearchHistorySettingsStore();
        var favoritesStore = new InMemoryKeywordSearchFavoritesSettingsStore();
        var filters = new ChartListFilterViewModel(historyStore, favoritesStore);
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            filters.UpdateKeywordSearchContext(context, ["Alpha", "Beta"]);
            string raw = warning ? "  unknown:alpha ti  " : "  title:alpha ti  ";
            filters.KeywordFilter = raw;
            filters.IsKeywordSearchHelpOpen = true;
            KeywordSearchAssistanceOwner assistance = filters.KeywordSearchAssistanceOwner;
            Assert.IsTrue(assistance.SavedQueryOwner.TryCommitHistory("title:history").Succeeded);
            Assert.IsTrue(assistance.TryAddFavorite("title:favorite").Succeeded);
            KeywordSearchPresentationState presentation = assistance.Focus(raw, raw.Length - 2);
            KeywordSearchSavedQueryState saved = assistance.SavedQueryOwner.State;
            string history = historyStore.KeywordSearchHistory;
            string favorites = favoritesStore.KeywordSearchFavorites;
            var notifications = new List<string>();
            int searches = 0;
            filters.KeywordFilterChanged += (_, _) => searches++;
            filters.PropertyChanged += (_, args) => notifications.Add(args.PropertyName ?? string.Empty);
            filters.AttachKeywordSearchPresentation();
            filters.AttachKeywordSearchPresentation();
            notifications.Clear();

            ResourceService.Current.ChangeCulture("en-US");

            string fields = context switch
            {
                GridKeywordSearchContext.PlaylistDetail => Resources.Keyword_search_help_fields_playlist_detail,
                GridKeywordSearchContext.PlayHistory => Resources.Keyword_search_help_fields_play_history,
                _ => Resources.Keyword_search_help_fields_bmsfile
            };
            Assert.AreEqual(warning ? string.Format(Resources.Keyword_search_warning_unknown_field, "unknown") : string.Empty, filters.KeywordSearchWarningText);
            Assert.AreEqual(string.Format(Resources.Keyword_search_help_template, fields), filters.KeywordSearchHelpText);
            Assert.AreEqual(warning, filters.HasKeywordSearchWarning);
            Assert.AreEqual(1, notifications.Count(name => name == nameof(filters.KeywordSearchHelpText)));
            Assert.AreEqual(warning ? 1 : 0, notifications.Count(name => name == nameof(filters.KeywordSearchWarningText)));
            Assert.AreEqual(0, searches);
            Assert.AreEqual(raw, filters.KeywordFilter);
            Assert.AreEqual(context, filters.CurrentKeywordSearchContext);
            Assert.IsTrue(filters.IsKeywordSearchHelpOpen);
            Assert.AreSame(presentation, assistance.Presentation);
            Assert.AreSame(saved, assistance.SavedQueryOwner.State);
            Assert.AreEqual(history, historyStore.KeywordSearchHistory);
            Assert.AreEqual(favorites, favoritesStore.KeywordSearchFavorites);

            filters.DetachKeywordSearchPresentation();
            notifications.Clear();
            ResourceService.Current.ChangeCulture("ja-JP");
            Assert.AreEqual(0, notifications.Count);
            filters.AttachKeywordSearchPresentation();
            Assert.AreEqual(warning ? string.Format(Resources.Keyword_search_warning_unknown_field, "unknown") : string.Empty, filters.KeywordSearchWarningText);
            Assert.AreEqual(1, notifications.Count(name => name == nameof(filters.KeywordSearchHelpText)));
            Assert.AreEqual(0, searches);
            Assert.AreSame(presentation, assistance.Presentation);
            Assert.AreSame(saved, assistance.SavedQueryOwner.State);
        }
        finally
        {
            filters.DetachKeywordSearchPresentation();
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }

}
