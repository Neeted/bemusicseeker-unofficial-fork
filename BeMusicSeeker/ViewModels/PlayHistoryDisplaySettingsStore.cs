using System;
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
    private readonly Func<Settings> settingsProvider;

    internal SettingsPlayHistoryDisplaySettingsStore(Func<Settings> settingsProvider = null)
    {
        this.settingsProvider = settingsProvider ?? (() => Settings.Default);
    }

    public string SelectedDisplayTargetIdentity
    {
        get => settingsProvider().PlayHistorySelectedDisplayTargetIdentity;
        set => settingsProvider().PlayHistorySelectedDisplayTargetIdentity = value;
    }

    public string DisplayTargetSetsJson
    {
        get => settingsProvider().PlayHistoryDisplayTargetSetsJson;
        set => settingsProvider().PlayHistoryDisplayTargetSetsJson = value;
    }
}
