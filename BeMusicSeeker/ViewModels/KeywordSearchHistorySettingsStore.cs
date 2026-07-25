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
/// 検索履歴 settings を既存の user.config へ接続します。
/// </summary>
internal sealed class SettingsKeywordSearchHistorySettingsStore : IKeywordSearchHistorySettingsStore
{
    private readonly Func<Settings> settingsProvider;

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
}
