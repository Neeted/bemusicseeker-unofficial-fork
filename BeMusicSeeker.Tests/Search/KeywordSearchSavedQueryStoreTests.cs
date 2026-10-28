using System;
using System.Linq;
using System.Text;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 履歴とお気に入りの保存・表示先投影・失敗時の不変条件を、設定の接続先で検証します。
/// </summary>
[TestClass]
public sealed class KeywordSearchSavedQueryStoreTests
{
    /// <summary>既に25件あるお気に入りへ追加して先頭に保存し、大小文字だけが異なる再追加は書込みを増やさないことを確認し、件数制限や重複保存を検出します。</summary>
    [TestMethod]
    public void KsaFavorites_UnlimitedNewestPinFirstAndCaseInsensitiveIdentity()
    {
        TestSettingsStore store = new()
        {
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(
                Enumerable.Range(1, 25).Select(index => "title:" + index))
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);

        Assert.AreEqual(25, owner.Favorites.Count);
        Assert.AreEqual("title:1", owner.Favorites[0]);

        KeywordSearchSavedQueryMutationResult added = owner.TryAddFavorite("  title:new  ");
        Assert.IsTrue(added.Succeeded);
        Assert.AreEqual("title:new", owner.Favorites[0]);
        Assert.AreEqual(26, KeywordSearchFavoritesStore.Deserialize(store.KeywordSearchFavorites).Count);

        int setterCount = store.KeywordSearchFavoritesSetterCount;
        KeywordSearchSavedQueryMutationResult duplicate = owner.TryAddFavorite("TITLE:NEW");
        Assert.IsTrue(duplicate.Succeeded);
        Assert.IsFalse(duplicate.Changed);
        Assert.AreEqual(setterCount, store.KeywordSearchFavoritesSetterCount);
        Assert.AreEqual("title:new", owner.Favorites[0]);
    }

    /// <summary>両保存範囲で反対のregex文字クラスを共存させ、pin・解除・削除・再構築を確認し、全文大小文字無視による別式の削除や表示抑制を検出します。</summary>
    [TestMethod]
    public void KsaFavorites_RegexIdentitySurvivesPinUnpinDeleteAndReconstructionInBothScopes()
    {
        foreach (KeywordSearchSavedQueryScope scope in Enum.GetValues<KeywordSearchSavedQueryScope>())
        {
            TestSettingsStore store = new();
            KeywordSearchSavedQueryOwner owner = CreateOwner(store, scope);
            GridKeywordSearchContext context = scope == KeywordSearchSavedQueryScope.Normal
                ? GridKeywordSearchContext.ChartList
                : GridKeywordSearchContext.PlaylistSummary;
            const string digit = @"re:\d";
            const string nonDigit = @"re:\D";
            Assert.IsTrue(owner.TryCommitHistory(digit).Succeeded);
            Assert.IsTrue(owner.TryCommitHistory(nonDigit).Succeeded);
            CollectionAssert.AreEqual(new[] { nonDigit, digit }, owner.History.ToArray());

            Assert.IsTrue(owner.TryAddFavorite(@"RE:\d").Succeeded);
            Assert.IsTrue(owner.TryAddFavorite(nonDigit).Succeeded);
            CollectionAssert.AreEqual(new[] { nonDigit, @"RE:\d" }, owner.Favorites.ToArray());
            Assert.IsTrue(owner.TryRemoveFavorite(@"RE:\D").Succeeded);
            CollectionAssert.AreEqual(new[] { @"RE:\d" }, owner.Favorites.ToArray());
            CollectionAssert.AreEqual(new[] { nonDigit, digit }, owner.History.ToArray());
            CollectionAssert.AreEqual(new[] { nonDigit }, owner.GetProjectedHistory(context).ToArray());
            CollectionAssert.AreEqual(new[] { @"RE:\d" }, owner.GetProjectedFavorites(context).ToArray());

            Assert.IsTrue(owner.TryRemoveFavorite(digit).Succeeded);
            CollectionAssert.AreEqual(new[] { nonDigit, digit }, owner.GetProjectedHistory(context).ToArray());
            Assert.IsTrue(owner.TryDeleteHistory(@" RE:\d ").Succeeded);
            CollectionAssert.AreEqual(new[] { nonDigit }, owner.History.ToArray());

            KeywordSearchSavedQueryOwner restored = CreateOwner(store, scope);
            CollectionAssert.AreEqual(new[] { nonDigit }, restored.History.ToArray());
            CollectionAssert.AreEqual(new[] { nonDigit }, restored.GetProjectedHistory(context).ToArray());
            Assert.AreEqual(0, restored.Favorites.Count);
        }
    }

    /// <summary>memo検索は譜面一覧で隠し詳細で表示し、元の履歴とお気に入りは残すことを確認し、画面非対応の保存式を削除する誤りを検出します。</summary>
    [TestMethod]
    public void KsaContextProjection_HidesIncompatibleNamedFieldsWithoutDeletingBackingRows()
    {
        TestSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["memo:playlist-note", "title:chart"]),
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["memo:favorite-note"])
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);

        CollectionAssert.AreEqual(
            new[] { "title:chart" },
            owner.GetProjectedHistory(GridKeywordSearchContext.ChartList).ToArray());
        CollectionAssert.AreEqual(
            new[] { "memo:playlist-note", "title:chart" },
            owner.GetProjectedHistory(GridKeywordSearchContext.PlaylistDetail).ToArray());
        Assert.AreEqual(2, owner.History.Count);
        Assert.AreEqual(1, owner.Favorites.Count);
        Assert.AreEqual(0, owner.GetProjectedFavorites(GridKeywordSearchContext.ChartList).Count);
        Assert.AreEqual(1, owner.GetProjectedFavorites(GridKeywordSearchContext.PlaylistDetail).Count);
    }

    /// <summary>通常部分だけ大小文字を無視して重複を先頭へ移し、regex本体・空白・式順・別名は原文で区別することを確認し、過剰な正規化と部分文字列によるre誤認を検出します。</summary>
    [TestMethod]
    public void KsaHistory_AddEntryUsesSyntaxIdentityAndPreservesOtherOrder()
    {
        (string first, string second, bool same)[] cases =
        [
            ("songtitle", " SONGTITLE ", true),
            ("title:songtitle", "TITLE:SONGTITLE", true),
            (@"title:re:\d", @"TITLE:RE:\d", true),
            (@"re:\d", @"re:\D", false),
            (@"title:re:\w", @"title:re:\W", false),
            ("re:a", "re:A", false),
            ("re:[a", "re:[A", false),
            (@"word title:re:\d -artist:Alpha", @"WORD TITLE:RE:\d -ARTIST:ALPHA", true),
            (@"word title:re:\d -artist:Alpha", @"WORD TITLE:RE:\D -ARTIST:ALPHA", false),
            ("\"re:a\"", "\"RE:A\"", true),
            ("title:\"re:a\"", "TITLE:\"RE:A\"", true),
            (@"title:alpha|re:\d", @"TITLE:ALPHA|RE:\D", true),
            (@"title:re:alpha|\d", @"TITLE:RE:alpha|\D", false),
            (@"C:\re:\d", @"c:\RE:\D", true),
            (@"-C:\re:\d", @"-c:\RE:\D", true),
            (@"-re:\d", @"-RE:\D", false),
            (@"-title:re:\d", @"-TITLE:RE:\d", true),
            ("-\"re:a\"", "-\"RE:A\"", true),
            ("title:alpha  artist:beta", "TITLE:ALPHA\r\nARTIST:BETA", true),
            ("title:alpha artist:beta", "title:alpha  artist:beta", false),
            ("title:alpha\tartist:beta", "title:alpha artist:beta", false),
            ("title:alpha artist:beta", "artist:beta title:alpha", false),
            ("playlist:alpha", "ref:alpha", false),
            ("re:a", "re:[a]", false),
            ("title:alpha", "title:\"alpha\"", false),
            (@"re:""\d""", @"RE:""\D""", false),
            (@"title:""a\""re:b""", @"TITLE:""A\""RE:B""", true)
        ];
        foreach ((string first, string second, bool same) in cases)
        {
            // 入力順を反転しても同じ同一性となり、新しい表記だけが先頭へ移る。
            foreach ((string existing, string added) in new[] { (first, second), (second, first) })
            {
                string[] updated = [.. KeywordSearchHistoryStore.AddEntry(["before", existing, "after"], added)];
                string normalizedAdded = added.Replace('\r', ' ').Replace('\n', ' ').Trim();
                string normalizedExisting = existing.Replace('\r', ' ').Replace('\n', ' ').Trim();
                string[] expected = same
                    ? [normalizedAdded, "before", "after"]
                    : [normalizedAdded, "before", normalizedExisting, "after"];
                CollectionAssert.AreEqual(expected, updated, $"{existing} / {added}");
            }
        }
    }

    /// <summary>独立した旧Base64行から不正行・通常重複を除きregex大小と原文整形・上限を保持して復元し、通常式の削除と両serializerの出力を確認して保存互換の退行を検出します。</summary>
    [TestMethod]
    public void KsaStores_LegacyRowsPreserveRegexIdentityAndLineNormalization()
    {
        string[] rawEntries =
        [
            "  songtitle\r\n ", "SONGTITLE", @"title:re:\d", @"TITLE:RE:\D",
            " title:alpha\r\nartist:beta ", "TITLE:ALPHA  ARTIST:BETA",
            @"re:[a", @"re:[A", "\t", .. Enumerable.Range(0, 23).Select(index => "title:" + index)
        ];
        string serialized = "not-base64\n" + string.Join("\n", rawEntries.Select(
            entry => Convert.ToBase64String(Encoding.UTF8.GetBytes(entry))));
        string[] expected =
        [
            "songtitle", @"title:re:\d", @"TITLE:RE:\D", "title:alpha  artist:beta",
            @"re:[a", @"re:[A", .. Enumerable.Range(0, 23).Select(index => "title:" + index)
        ];
        CollectionAssert.AreEqual(expected.Take(20).ToArray(), KeywordSearchHistoryStore.Deserialize(serialized).ToArray());
        CollectionAssert.AreEqual(expected, KeywordSearchFavoritesStore.Deserialize(serialized).ToArray());

        string[] historyOutput = KeywordSearchHistoryStore.Serialize(rawEntries)
            .Split(Environment.NewLine).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
        string[] favoritesOutput = KeywordSearchFavoritesStore.Serialize(rawEntries)
            .Split(Environment.NewLine).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
        CollectionAssert.AreEqual(expected.Take(20).ToArray(), historyOutput);
        CollectionAssert.AreEqual(expected, favoritesOutput);

        TestSettingsStore store = new() { KeywordSearchHistory = serialized };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);
        Assert.IsTrue(owner.TryDeleteHistory("  SONGTITLE  ").Succeeded);
        CollectionAssert.AreEqual(expected.Take(20).Skip(1).ToArray(), owner.History.ToArray());
    }

    /// <summary>お気に入り設定の書込みが失敗した場合に一回の試行だけで旧保存値・順序・履歴・版と通知なしを維持することを確認し、保存前にメモリ状態を確定する誤りを検出します。</summary>
    [TestMethod]
    public void KsaPersistence_SetterFailureLeavesCollectionsOrderProjectionAndNotificationsUnchanged()
    {
        TestSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["title:old"]),
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["title:pinned"]),
            ThrowOnFavoriteSet = true
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);
        int notifications = 0;
        owner.StateChanged += (_, _) => notifications++;
        string oldSerialized = store.KeywordSearchFavorites;
        int oldSetterCount = store.KeywordSearchFavoritesSetterCount;
        long oldRevision = owner.State.Revision;

        KeywordSearchSavedQueryMutationResult result = owner.TryAddFavorite("title:new");

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Exception);
        Assert.AreEqual(oldSetterCount + 1, store.KeywordSearchFavoritesSetterCount);
        Assert.AreEqual(oldSerialized, store.KeywordSearchFavorites);
        Assert.AreEqual(oldRevision, owner.State.Revision);
        CollectionAssert.AreEqual(new[] { "title:pinned" }, owner.Favorites.ToArray());
        CollectionAssert.AreEqual(new[] { "title:old" }, owner.History.ToArray());
        Assert.AreEqual(0, notifications);
    }

    /// <summary>お気に入りの保存先が未指定なら構築を拒否することを確認し、履歴の保存先へ暗黙に代用して保存範囲を混ぜる誤りを検出します。</summary>
    [TestMethod]
    public void KsaPersistence_RequiresExplicitFavoritesSettingsDependency()
    {
        TestSettingsStore historyStore = new();

        Assert.ThrowsException<ArgumentNullException>(() => new KeywordSearchSavedQueryOwner(
            KeywordSearchSavedQueryScope.Normal,
            historyStore,
            null));
    }

    /// <summary>通常画面の履歴追加で設定書込みが失敗しても、元状態・両コレクション・画面投影を保持して変更通知を出さないことを確認し、未保存履歴の公開を検出します。</summary>
    [TestMethod]
    public void KsaPersistence_HistoryCommitSetterFailureLeavesNormalStateUnchanged()
    {
        TestSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["title:old", "title:older"]),
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["title:pinned"]),
            ThrowOnHistorySet = true
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);

        AssertHistoryMutationFailureLeavesStateUnchanged(
            owner,
            store,
            GridKeywordSearchContext.ChartList,
            summary: false,
            () => owner.TryCommitHistory("title:new"));
    }

    /// <summary>通常画面の履歴削除で設定書込みが失敗しても、元の順序・投影・お気に入りを保持して変更通知を出さないことを確認し、保存されなかった削除を画面へ反映する誤りを検出します。</summary>
    [TestMethod]
    public void KsaPersistence_HistoryDeleteSetterFailureLeavesNormalStateUnchanged()
    {
        TestSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["title:old", "title:older"]),
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["title:pinned"]),
            ThrowOnHistorySet = true
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);

        AssertHistoryMutationFailureLeavesStateUnchanged(
            owner,
            store,
            GridKeywordSearchContext.ChartList,
            summary: false,
            () => owner.TryDeleteHistory(" TITLE:OLD "));
    }

    /// <summary>一覧専用の履歴追加で専用設定の書込みが失敗すると、旧状態と両投影を通知なしで保持することを確認し、別の保存範囲へ代用したり未保存結果を公開する誤りを検出します。</summary>
    [TestMethod]
    public void KsaPersistence_HistoryCommitSetterFailureLeavesPlaylistSummaryStateUnchanged()
    {
        TestSettingsStore store = new()
        {
            PlaylistSummaryKeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["output:old", "output:older"]),
            PlaylistSummaryKeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["output:pinned"]),
            ThrowOnPlaylistSummaryHistorySet = true
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.PlaylistSummary);

        AssertHistoryMutationFailureLeavesStateUnchanged(
            owner,
            store,
            GridKeywordSearchContext.PlaylistSummary,
            summary: true,
            () => owner.TryCommitHistory("output:new"));
    }

    /// <summary>一覧専用履歴の削除が専用設定で失敗しても旧順序・投影・お気に入りと通知なしを保持することを確認し、保存失敗後も一覧だけ削除が進む誤りを検出します。</summary>
    [TestMethod]
    public void KsaPersistence_HistoryDeleteSetterFailureLeavesPlaylistSummaryStateUnchanged()
    {
        TestSettingsStore store = new()
        {
            PlaylistSummaryKeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["output:old", "output:older"]),
            PlaylistSummaryKeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["output:pinned"]),
            ThrowOnPlaylistSummaryHistorySet = true
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.PlaylistSummary);

        AssertHistoryMutationFailureLeavesStateUnchanged(
            owner,
            store,
            GridKeywordSearchContext.PlaylistSummary,
            summary: true,
            () => owner.TryDeleteHistory(" OUTPUT:OLD "));
    }

    /// <summary>通常画面と一覧専用範囲で履歴とお気に入りをそれぞれ変更し、四つの保存値が独立することを確認し、画面間で保存式を上書きする誤りを検出します。</summary>
    [TestMethod]
    public void KsaScopes_NormalAndPlaylistSummaryPersistIndependently()
    {
        TestSettingsStore store = new();
        KeywordSearchSavedQueryOwner normal = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);
        KeywordSearchSavedQueryOwner summary = CreateOwner(store, KeywordSearchSavedQueryScope.PlaylistSummary);

        Assert.IsTrue(normal.TryCommitHistory("title:normal").Succeeded);
        Assert.IsTrue(normal.TryAddFavorite("title:favorite").Succeeded);
        Assert.IsTrue(summary.TryCommitHistory("output:summary").Succeeded);
        Assert.IsTrue(summary.TryAddFavorite("output:favorite").Succeeded);

        CollectionAssert.AreEqual(new[] { "title:normal" }, KeywordSearchHistoryStore.Deserialize(store.KeywordSearchHistory).ToArray());
        CollectionAssert.AreEqual(new[] { "output:summary" }, KeywordSearchHistoryStore.Deserialize(store.PlaylistSummaryKeywordSearchHistory).ToArray());
        CollectionAssert.AreEqual(new[] { "title:favorite" }, KeywordSearchFavoritesStore.Deserialize(store.KeywordSearchFavorites).ToArray());
        CollectionAssert.AreEqual(new[] { "output:favorite" }, KeywordSearchFavoritesStore.Deserialize(store.PlaylistSummaryKeywordSearchFavorites).ToArray());
    }

    private static KeywordSearchSavedQueryOwner CreateOwner(
        TestSettingsStore store,
        KeywordSearchSavedQueryScope scope)
    {
        return new KeywordSearchSavedQueryOwner(scope, store, store);
    }

    private static void AssertHistoryMutationFailureLeavesStateUnchanged(
        KeywordSearchSavedQueryOwner owner,
        TestSettingsStore store,
        GridKeywordSearchContext context,
        bool summary,
        Func<KeywordSearchSavedQueryMutationResult> mutation)
    {
        KeywordSearchSavedQueryState previousState = owner.State;
        string[] previousHistory = owner.History.ToArray();
        string[] previousFavorites = owner.Favorites.ToArray();
        string[] previousProjectedHistory = owner.GetProjectedHistory(context).ToArray();
        string[] previousProjectedFavorites = owner.GetProjectedFavorites(context).ToArray();
        string previousSerialized = summary
            ? store.PlaylistSummaryKeywordSearchHistory
            : store.KeywordSearchHistory;
        int previousSetterCount = summary
            ? store.PlaylistSummaryKeywordSearchHistorySetterCount
            : store.KeywordSearchHistorySetterCount;
        int stateChangedNotifications = 0;
        int propertyChangedNotifications = 0;
        owner.StateChanged += (_, _) => stateChangedNotifications++;
        owner.PropertyChanged += (_, _) => propertyChangedNotifications++;

        KeywordSearchSavedQueryMutationResult result = mutation();

        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Exception);
        Assert.AreSame(previousState, owner.State);
        CollectionAssert.AreEqual(previousHistory, owner.History.ToArray());
        CollectionAssert.AreEqual(previousFavorites, owner.Favorites.ToArray());
        CollectionAssert.AreEqual(previousProjectedHistory, owner.GetProjectedHistory(context).ToArray());
        CollectionAssert.AreEqual(previousProjectedFavorites, owner.GetProjectedFavorites(context).ToArray());
        Assert.AreEqual(previousSerialized, summary
            ? store.PlaylistSummaryKeywordSearchHistory
            : store.KeywordSearchHistory);
        Assert.AreEqual(previousSetterCount + 1, summary
            ? store.PlaylistSummaryKeywordSearchHistorySetterCount
            : store.KeywordSearchHistorySetterCount);
        Assert.AreEqual(0, stateChangedNotifications);
        Assert.AreEqual(0, propertyChangedNotifications);
    }

    private sealed class TestSettingsStore : IKeywordSearchHistorySettingsStore, IKeywordSearchFavoritesSettingsStore
    {
        private string keywordSearchHistory = string.Empty;
        private string playlistSummaryKeywordSearchHistory = string.Empty;
        private string keywordSearchFavorites = string.Empty;
        private string playlistSummaryKeywordSearchFavorites = string.Empty;

        public bool ThrowOnFavoriteSet { get; set; }

        public bool ThrowOnHistorySet { get; set; }

        public bool ThrowOnPlaylistSummaryHistorySet { get; set; }

        public int KeywordSearchFavoritesSetterCount { get; private set; }

        public int KeywordSearchHistorySetterCount { get; private set; }

        public int PlaylistSummaryKeywordSearchHistorySetterCount { get; private set; }

        public string KeywordSearchHistory
        {
            get => keywordSearchHistory;
            set
            {
                KeywordSearchHistorySetterCount++;
                if (ThrowOnHistorySet)
                {
                    throw new InvalidOperationException("history setter failure");
                }
                keywordSearchHistory = value;
            }
        }

        public string PlaylistSummaryKeywordSearchHistory
        {
            get => playlistSummaryKeywordSearchHistory;
            set
            {
                PlaylistSummaryKeywordSearchHistorySetterCount++;
                if (ThrowOnPlaylistSummaryHistorySet)
                {
                    throw new InvalidOperationException("summary history setter failure");
                }
                playlistSummaryKeywordSearchHistory = value;
            }
        }

        public string KeywordSearchFavorites
        {
            get => keywordSearchFavorites;
            set
            {
                KeywordSearchFavoritesSetterCount++;
                if (ThrowOnFavoriteSet)
                {
                    throw new InvalidOperationException("favorite setter failure");
                }
                keywordSearchFavorites = value;
            }
        }

        public string PlaylistSummaryKeywordSearchFavorites
        {
            get => playlistSummaryKeywordSearchFavorites;
            set => playlistSummaryKeywordSearchFavorites = value;
        }
    }
}
