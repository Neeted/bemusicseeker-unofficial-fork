using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Exercises the persisted Favorites/History owner independently from WPF.
/// </summary>
[TestClass]
public sealed class KeywordSearchSavedQueryStoreTests
{
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

    [TestMethod]
    public void KsaFavorites_PinRetainsBackingHistoryAndUnpinRevealsIt()
    {
        TestSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["title:alpha"])
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);

        Assert.AreEqual("title:alpha", owner.History[0]);
        KeywordSearchSavedQueryMutationResult pinned = owner.TryAddFavorite("title:alpha");
        Assert.IsTrue(pinned.Succeeded);
        Assert.AreEqual(1, owner.History.Count);
        Assert.AreEqual(0, owner.GetProjectedHistory(GridKeywordSearchContext.ChartList).Count);
        Assert.AreEqual("title:alpha", owner.GetProjectedFavorites(GridKeywordSearchContext.ChartList)[0]);

        KeywordSearchSavedQueryMutationResult unpinned = owner.TryRemoveFavorite("TITLE:ALPHA");
        Assert.IsTrue(unpinned.Succeeded);
        CollectionAssert.AreEqual(
            new[] { "title:alpha" },
            owner.GetProjectedHistory(GridKeywordSearchContext.ChartList).ToArray());
    }

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

    [TestMethod]
    public void KsaHistory_UsesLegacyBase64CapCorruptLineSkipAndExactNormalizedDelete()
    {
        string[] entries = Enumerable.Range(0, KeywordSearchHistoryStore.MaxHistoryCount + 3)
            .Select(index => "title:" + index)
            .ToArray();
        TestSettingsStore store = new()
        {
            KeywordSearchHistory = string.Join(
                Environment.NewLine,
                new[] { "not-base64", KeywordSearchHistoryStore.Serialize(entries) })
        };
        KeywordSearchSavedQueryOwner owner = CreateOwner(store, KeywordSearchSavedQueryScope.Normal);

        Assert.AreEqual(KeywordSearchHistoryStore.MaxHistoryCount, owner.History.Count);
        Assert.AreEqual("title:0", owner.History[0]);

        KeywordSearchSavedQueryMutationResult deleted = owner.TryDeleteHistory("  TITLE:0  ");
        Assert.IsTrue(deleted.Succeeded);
        Assert.IsFalse(owner.History.Any(query => string.Equals(query, "title:0", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(KeywordSearchHistoryStore.MaxHistoryCount - 1, owner.History.Count);
    }

    [TestMethod]
    public void KsaHistory_RoundTripsDistinctCappedHistory()
    {
        string[] entries = [.. Enumerable.Range(0, KeywordSearchHistoryStore.MaxHistoryCount + 5)
            .Select(index => "title:" + index)];
        string serialized = KeywordSearchHistoryStore.Serialize(entries);

        string[] restored = [.. KeywordSearchHistoryStore.Deserialize(serialized)];

        Assert.AreEqual(KeywordSearchHistoryStore.MaxHistoryCount, restored.Length);
        Assert.AreEqual("title:0", restored[0]);
        CollectionAssert.DoesNotContain(restored, "title:24");
    }

    [TestMethod]
    public void KsaHistory_AddEntryMovesCaseInsensitiveDuplicateToFront()
    {
        string[] updated = [.. KeywordSearchHistoryStore.AddEntry(["alpha", "beta", "gamma"], " BETA ")];

        CollectionAssert.AreEqual(new[] { "BETA", "alpha", "gamma" }, updated);
    }

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

    [TestMethod]
    public void KsaPersistence_RequiresExplicitFavoritesSettingsDependency()
    {
        TestSettingsStore historyStore = new();

        Assert.ThrowsException<ArgumentNullException>(() => new KeywordSearchSavedQueryOwner(
            KeywordSearchSavedQueryScope.Normal,
            historyStore,
            null));
    }

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
