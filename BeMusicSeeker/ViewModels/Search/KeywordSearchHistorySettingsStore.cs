using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 検索履歴の serialized settings だけを MainWindow composition へ提供します。
/// </summary>
internal interface IKeywordSearchHistorySettingsStore
{
    string KeywordSearchHistory { get; set; }

    string PlaylistSummaryKeywordSearchHistory { get; set; }
}

/// <summary>
/// Provides the serialized favorite queries for each search scope.
/// </summary>
internal interface IKeywordSearchFavoritesSettingsStore
{
    /// <summary>Gets or sets the serialized normal-scope Favorites.</summary>
    string KeywordSearchFavorites { get; set; }

    /// <summary>Gets or sets the serialized playlist-summary Favorites.</summary>
    string PlaylistSummaryKeywordSearchFavorites { get; set; }
}

/// <summary>
/// 検索履歴 settings を既存の user.config へ接続します。
/// </summary>
internal sealed class SettingsKeywordSearchHistorySettingsStore : IKeywordSearchHistorySettingsStore, IKeywordSearchFavoritesSettingsStore
{
    private readonly Func<Settings> settingsProvider;

    /// <summary>
    /// Creates a settings-backed boundary for both saved-query collections.
    /// </summary>
    /// <param name="settingsProvider">Provides the active user settings session.</param>
    internal SettingsKeywordSearchHistorySettingsStore(Func<Settings> settingsProvider)
    {
        this.settingsProvider = settingsProvider
            ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    public string KeywordSearchHistory
    {
        get => settingsProvider().KeywordSearchHistory;
        set => settingsProvider().KeywordSearchHistory = value;
    }

    public string PlaylistSummaryKeywordSearchHistory
    {
        get => settingsProvider().PlaylistSummaryKeywordSearchHistory;
        set => settingsProvider().PlaylistSummaryKeywordSearchHistory = value;
    }

    /// <summary>
    /// Gets or sets normal-scope favorite queries in the existing user settings store.
    /// </summary>
    public string KeywordSearchFavorites
    {
        get => settingsProvider().KeywordSearchFavorites;
        set => settingsProvider().KeywordSearchFavorites = value;
    }

    /// <summary>
    /// Gets or sets playlist-summary favorite queries in the existing user settings store.
    /// </summary>
    public string PlaylistSummaryKeywordSearchFavorites
    {
        get => settingsProvider().PlaylistSummaryKeywordSearchFavorites;
        set => settingsProvider().PlaylistSummaryKeywordSearchFavorites = value;
    }
}
