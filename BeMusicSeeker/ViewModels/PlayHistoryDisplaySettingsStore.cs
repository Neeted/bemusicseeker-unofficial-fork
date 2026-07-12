using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Play History の表示 target に関する serialized settings だけを提供します。
/// </summary>
internal interface IPlayHistoryDisplaySettingsStore
{
    string SelectedDisplayTargetIdentity { get; set; }

    string DisplayTargetSetsJson { get; set; }
}

/// <summary>
/// Play History の表示 target settings を既存の user.config へ接続します。
/// </summary>
internal sealed class SettingsPlayHistoryDisplaySettingsStore : IPlayHistoryDisplaySettingsStore
{
    public string SelectedDisplayTargetIdentity
    {
        get => Settings.Default.PlayHistorySelectedDisplayTargetIdentity;
        set => Settings.Default.PlayHistorySelectedDisplayTargetIdentity = value;
    }

    public string DisplayTargetSetsJson
    {
        get => Settings.Default.PlayHistoryDisplayTargetSetsJson;
        set => Settings.Default.PlayHistoryDisplayTargetSetsJson = value;
    }
}
