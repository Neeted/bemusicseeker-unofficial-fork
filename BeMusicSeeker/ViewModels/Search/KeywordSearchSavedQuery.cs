using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Identifies the persisted saved-query scope.
/// </summary>
internal enum KeywordSearchSavedQueryScope
{
    /// <summary>The shared ChartList/PlaylistDetail/PlayHistory scope.</summary>
    Normal,
    /// <summary>The independent playlist-summary scope.</summary>
    PlaylistSummary,
}

/// <summary>
/// Identifies an immutable saved-query collection mutation result.
/// </summary>
internal sealed class KeywordSearchSavedQueryMutationResult
{
    private KeywordSearchSavedQueryMutationResult(bool succeeded, bool changed, Exception exception)
    {
        Succeeded = succeeded;
        Changed = changed;
        Exception = exception;
    }

    /// <summary>
    /// Gets whether the adapter mutation completed successfully.
    /// </summary>
    internal bool Succeeded { get; }

    /// <summary>
    /// Gets whether the persisted collection actually changed.
    /// </summary>
    internal bool Changed { get; }

    /// <summary>
    /// Gets the setter failure, when the mutation was rejected.
    /// </summary>
    internal Exception Exception { get; }

    /// <summary>Creates a successful mutation result.</summary>
    /// <param name="changed">Whether persistence changed the collection.</param>
    internal static KeywordSearchSavedQueryMutationResult Success(bool changed)
        => new(true, changed, null);

    /// <summary>Creates a failed mutation result.</summary>
    /// <param name="exception">The adapter failure.</param>
    internal static KeywordSearchSavedQueryMutationResult Failure(Exception exception)
        => new(false, false, exception ?? new InvalidOperationException("Saved-query persistence failed."));
}

/// <summary>
/// Immutable saved-query state exposed to presentation owners.
/// </summary>
internal sealed class KeywordSearchSavedQueryState
{
    /// <summary>
    /// Creates an immutable saved-query state snapshot.
    /// </summary>
    /// <param name="history">History in recent-first order.</param>
    /// <param name="favorites">Favorites in newest-pin-first order.</param>
    /// <param name="revision">The in-memory mutation revision.</param>
    internal KeywordSearchSavedQueryState(
        IReadOnlyList<string> history,
        IReadOnlyList<string> favorites,
        long revision)
    {
        History = history ?? [];
        Favorites = favorites ?? [];
        Revision = revision;
    }

    /// <summary>
    /// Gets history in recent-first order.
    /// </summary>
    internal IReadOnlyList<string> History { get; }

    /// <summary>
    /// Gets pinned favorites in newest-pin-first order.
    /// </summary>
    internal IReadOnlyList<string> Favorites { get; }

    /// <summary>
    /// Gets the monotonic in-memory revision.
    /// </summary>
    internal long Revision { get; }
}

/// <summary>
/// Owns persisted Favorites and History for one search scope.
/// </summary>
internal sealed class KeywordSearchSavedQueryOwner : INotifyPropertyChanged
{
    private readonly object syncRoot = new();

    private readonly KeywordSearchSavedQueryScope scope;

    private readonly IKeywordSearchHistorySettingsStore historySettingsStore;

    private readonly IKeywordSearchFavoritesSettingsStore favoritesSettingsStore;

    private List<string> history;

    private List<string> favorites;

    private KeywordSearchSavedQueryState state;

    /// <summary>
    /// Creates a saved-query owner with explicit persistence boundaries for both collections.
    /// </summary>
    /// <param name="scope">The independent normal or playlist-summary scope.</param>
    /// <param name="historySettingsStore">The explicit History persistence boundary.</param>
    /// <param name="favoritesSettingsStore">The explicit Favorites persistence boundary.</param>
    internal KeywordSearchSavedQueryOwner(
        KeywordSearchSavedQueryScope scope,
        IKeywordSearchHistorySettingsStore historySettingsStore,
        IKeywordSearchFavoritesSettingsStore favoritesSettingsStore)
    {
        this.scope = scope;
        this.historySettingsStore = historySettingsStore
            ?? throw new ArgumentNullException(nameof(historySettingsStore));
        this.favoritesSettingsStore = favoritesSettingsStore
            ?? throw new ArgumentNullException(nameof(favoritesSettingsStore));

        history = [.. KeywordSearchHistoryStore.Deserialize(ReadHistorySetting())];
        favorites = [.. KeywordSearchFavoritesStore.Deserialize(ReadFavoritesSetting())];
        state = CreateState(history, favorites, revision: 0L);
    }

    /// <summary>
    /// Raised after a successful persisted mutation has been published in memory.
    /// </summary>
    internal event EventHandler StateChanged;

    /// <summary>
    /// Implements property notification for the immutable presentation bindings.
    /// </summary>
    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// Gets the scope owned by this instance.
    /// </summary>
    internal KeywordSearchSavedQueryScope Scope => scope;

    /// <summary>
    /// Gets the immutable current saved-query state.
    /// </summary>
    internal KeywordSearchSavedQueryState State
    {
        get
        {
            lock (syncRoot)
            {
                return state;
            }
        }
    }

    /// <summary>
    /// Gets history in its persisted recent-first order.
    /// </summary>
    internal IReadOnlyList<string> History => State.History;

    /// <summary>
    /// Gets favorites in newest-pin-first order.
    /// </summary>
    internal IReadOnlyList<string> Favorites => State.Favorites;

    /// <summary>
    /// Commits a selected or submitted search query to History.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryCommitHistory(string query)
    {
        List<string> nextHistory;
        lock (syncRoot)
        {
            nextHistory = [.. KeywordSearchHistoryStore.AddEntry(history, query)];
            if (SequenceEqual(history, nextHistory))
            {
                return KeywordSearchSavedQueryMutationResult.Success(changed: false);
            }

            KeywordSearchSavedQueryMutationResult persistence = TryPersistHistoryUnsafe(nextHistory);
            if (!persistence.Succeeded)
            {
                return persistence;
            }
            history = nextHistory;
            state = CreateState(history, favorites, state.Revision + 1L);
        }

        PublishStateChanged(historyChanged: true, favoritesChanged: false);
        return KeywordSearchSavedQueryMutationResult.Success(changed: true);
    }

    /// <summary>
    /// Pins a query without reordering an already pinned favorite.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryAddFavorite(string query)
    {
        string normalizedQuery = KeywordSearchHistoryStore.NormalizeEntryForIdentity(query);
        if (normalizedQuery.Length == 0)
        {
            return KeywordSearchSavedQueryMutationResult.Failure(
                new ArgumentException("A non-empty query is required.", nameof(query)));
        }

        List<string> nextFavorites;
        lock (syncRoot)
        {
            if (favorites.Any(current => string.Equals(current, normalizedQuery, StringComparison.OrdinalIgnoreCase)))
            {
                return KeywordSearchSavedQueryMutationResult.Success(changed: false);
            }

            nextFavorites = [normalizedQuery, .. favorites];
            KeywordSearchSavedQueryMutationResult persistence = TryPersistFavoritesUnsafe(nextFavorites);
            if (!persistence.Succeeded)
            {
                return persistence;
            }
            favorites = nextFavorites;
            state = CreateState(history, favorites, state.Revision + 1L);
        }

        PublishStateChanged(historyChanged: false, favoritesChanged: true);
        return KeywordSearchSavedQueryMutationResult.Success(changed: true);
    }

    /// <summary>
    /// Removes exactly one normalized favorite from this scope.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryRemoveFavorite(string query)
    {
        string normalizedQuery = KeywordSearchHistoryStore.NormalizeEntryForIdentity(query);
        if (normalizedQuery.Length == 0)
        {
            return KeywordSearchSavedQueryMutationResult.Success(changed: false);
        }

        List<string> nextFavorites;
        lock (syncRoot)
        {
            int index = favorites.FindIndex(current =>
                string.Equals(current, normalizedQuery, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return KeywordSearchSavedQueryMutationResult.Success(changed: false);
            }
            nextFavorites = [.. favorites];
            nextFavorites.RemoveAt(index);
            KeywordSearchSavedQueryMutationResult persistence = TryPersistFavoritesUnsafe(nextFavorites);
            if (!persistence.Succeeded)
            {
                return persistence;
            }
            favorites = nextFavorites;
            state = CreateState(history, favorites, state.Revision + 1L);
        }

        PublishStateChanged(historyChanged: false, favoritesChanged: true);
        return KeywordSearchSavedQueryMutationResult.Success(changed: true);
    }

    /// <summary>
    /// Deletes exactly one normalized history entry while retaining a favorite with the same query.
    /// </summary>
    internal KeywordSearchSavedQueryMutationResult TryDeleteHistory(string query)
    {
        string normalizedQuery = KeywordSearchHistoryStore.NormalizeEntryForIdentity(query);
        if (normalizedQuery.Length == 0)
        {
            return KeywordSearchSavedQueryMutationResult.Success(changed: false);
        }

        List<string> nextHistory;
        lock (syncRoot)
        {
            int index = history.FindIndex(current =>
                string.Equals(current, normalizedQuery, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return KeywordSearchSavedQueryMutationResult.Success(changed: false);
            }
            nextHistory = [.. history];
            nextHistory.RemoveAt(index);
            KeywordSearchSavedQueryMutationResult persistence = TryPersistHistoryUnsafe(nextHistory);
            if (!persistence.Succeeded)
            {
                return persistence;
            }
            history = nextHistory;
            state = CreateState(history, favorites, state.Revision + 1L);
        }

        PublishStateChanged(historyChanged: true, favoritesChanged: false);
        return KeywordSearchSavedQueryMutationResult.Success(changed: true);
    }

    /// <summary>
    /// Projects History for a context without deleting incompatible backing entries.
    /// </summary>
    internal IReadOnlyList<string> GetProjectedHistory(GridKeywordSearchContext context)
    {
        KeywordSearchSavedQueryState current = State;
        HashSet<string> favoriteIdentities = new(
            current.Favorites.Select(KeywordSearchHistoryStore.NormalizeEntryForIdentity),
            StringComparer.OrdinalIgnoreCase);
        List<string> projected = [];
        foreach (string query in current.History)
        {
            if (favoriteIdentities.Contains(KeywordSearchHistoryStore.NormalizeEntryForIdentity(query)))
            {
                continue;
            }
            if (!IsCompatibleWithContext(query, context))
            {
                continue;
            }
            if (!projected.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                projected.Add(query);
            }
        }
        return new ReadOnlyCollection<string>(projected);
    }

    /// <summary>
    /// Projects favorites for a context without changing their persisted order.
    /// </summary>
    internal IReadOnlyList<string> GetProjectedFavorites(GridKeywordSearchContext context)
    {
        KeywordSearchSavedQueryState current = State;
        return new ReadOnlyCollection<string>(
            current.Favorites
                .Where(query => IsCompatibleWithContext(query, context))
                .ToArray());
    }

    private string ReadHistorySetting()
    {
        return scope == KeywordSearchSavedQueryScope.PlaylistSummary
            ? historySettingsStore.PlaylistSummaryKeywordSearchHistory
            : historySettingsStore.KeywordSearchHistory;
    }

    private string ReadFavoritesSetting()
    {
        return scope == KeywordSearchSavedQueryScope.PlaylistSummary
            ? favoritesSettingsStore.PlaylistSummaryKeywordSearchFavorites
            : favoritesSettingsStore.KeywordSearchFavorites;
    }

    private KeywordSearchSavedQueryMutationResult TryPersistHistoryUnsafe(IReadOnlyList<string> nextHistory)
    {
        string serialized = KeywordSearchHistoryStore.Serialize(nextHistory);
        try
        {
            if (scope == KeywordSearchSavedQueryScope.PlaylistSummary)
            {
                historySettingsStore.PlaylistSummaryKeywordSearchHistory = serialized;
            }
            else
            {
                historySettingsStore.KeywordSearchHistory = serialized;
            }
            return KeywordSearchSavedQueryMutationResult.Success(changed: true);
        }
        catch (Exception ex)
        {
            return KeywordSearchSavedQueryMutationResult.Failure(ex);
        }
    }

    private KeywordSearchSavedQueryMutationResult TryPersistFavoritesUnsafe(IReadOnlyList<string> nextFavorites)
    {
        string serialized = KeywordSearchFavoritesStore.Serialize(nextFavorites);
        try
        {
            if (scope == KeywordSearchSavedQueryScope.PlaylistSummary)
            {
                favoritesSettingsStore.PlaylistSummaryKeywordSearchFavorites = serialized;
            }
            else
            {
                favoritesSettingsStore.KeywordSearchFavorites = serialized;
            }
            return KeywordSearchSavedQueryMutationResult.Success(changed: true);
        }
        catch (Exception ex)
        {
            return KeywordSearchSavedQueryMutationResult.Failure(ex);
        }
    }

    private void PublishStateChanged(bool historyChanged, bool favoritesChanged)
    {
        if (historyChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(History)));
        }
        if (favoritesChanged)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Favorites)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static KeywordSearchSavedQueryState CreateState(
        IEnumerable<string> history,
        IEnumerable<string> favorites,
        long revision)
    {
        return new KeywordSearchSavedQueryState(
            new ReadOnlyCollection<string>((history ?? []).ToArray()),
            new ReadOnlyCollection<string>((favorites ?? []).ToArray()),
            revision);
    }

    private static bool IsCompatibleWithContext(string query, GridKeywordSearchContext context)
    {
        return !GridKeywordSearchQuery.Parse(query)
            .GetDiagnostics(context)
            .Any(diagnostic => diagnostic.Kind == GridKeywordSearchDiagnosticKind.UnknownField);
    }

    private static bool SequenceEqual(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        return first.Count == second.Count
            && first.SequenceEqual(second, StringComparer.Ordinal);
    }
}

/// <summary>
/// Serializes unlimited pinned favorites as UTF-8 Base64 lines.
/// </summary>
internal static class KeywordSearchFavoritesStore
{
    /// <summary>
    /// Restores favorites while skipping malformed lines and case-insensitive duplicates.
    /// </summary>
    internal static IReadOnlyList<string> Deserialize(string serializedFavorites)
    {
        if (string.IsNullOrWhiteSpace(serializedFavorites))
        {
            return [];
        }

        List<string> favorites = [];
        foreach (string line in serializedFavorites.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string entry = System.Text.Encoding.UTF8.GetString(
                    Convert.FromBase64String(line.Trim()));
                string normalizedEntry = KeywordSearchHistoryStore.NormalizeEntryForIdentity(entry);
                if (normalizedEntry.Length > 0
                    && !favorites.Contains(normalizedEntry, StringComparer.OrdinalIgnoreCase))
                {
                    favorites.Add(normalizedEntry);
                }
            }
            catch (FormatException)
            {
                // Malformed user.config lines are isolated to their own saved query.
            }
        }
        return new ReadOnlyCollection<string>(favorites);
    }

    /// <summary>
    /// Serializes favorites without applying the history twenty-entry cap.
    /// </summary>
    internal static string Serialize(IEnumerable<string> favorites)
    {
        IEnumerable<string> normalized = favorites ?? [];
        List<string> entries = [];
        foreach (string favorite in normalized)
        {
            string value = KeywordSearchHistoryStore.NormalizeEntryForIdentity(favorite);
            if (value.Length > 0 && !entries.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(value);
            }
        }
        return string.Join(
            Environment.NewLine,
            entries.Select(entry => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(entry))));
    }
}
