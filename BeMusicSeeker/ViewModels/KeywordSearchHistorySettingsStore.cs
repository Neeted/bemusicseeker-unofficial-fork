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
    public string KeywordSearchHistory
    {
        get => Settings.Default.KeywordSearchHistory;
        set => Settings.Default.KeywordSearchHistory = value;
    }

    public string PlaylistSummaryKeywordSearchHistory
    {
        get => Settings.Default.PlaylistSummaryKeywordSearchHistory;
        set => Settings.Default.PlaylistSummaryKeywordSearchHistory = value;
    }
}
