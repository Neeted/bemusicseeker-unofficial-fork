using System;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Models;

/// <summary>
/// Provides the mutable settings used by the playback feature.
/// </summary>
internal interface IPlaybackSettingsStore
{
    PlayerPanelState PlayerPanelState { get; set; }

    bool RepeatPlay { get; set; }

    bool FolderSkipPlay { get; set; }

    bool SinglePlay { get; set; }

    bool UsesLr2Body { get; }

    bool UsesUbMplay { get; }

    bool UsesBmiIdxView { get; }

    bool UseExternalPanelImage { get; }

    string StagefilePath { get; }

    int PlayerVolume { get; set; }

    bool UsesLr2Database { get; }
}

/// <summary>
/// Adapts the persisted application settings to the playback feature boundary.
/// </summary>
internal sealed class SettingsPlaybackSettingsStore : IPlaybackSettingsStore
{
    private readonly Func<Settings> settingsProvider;

    internal SettingsPlaybackSettingsStore(Func<Settings> settingsProvider)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    private Settings Values => settingsProvider()
        ?? throw new InvalidOperationException("Playback settings provider returned null.");

    public PlayerPanelState PlayerPanelState
    {
        get => Values.PlayerPanelState;
        set => Values.PlayerPanelState = value;
    }

    public bool RepeatPlay
    {
        get => Values.RepeatPlayMode;
        set => Values.RepeatPlayMode = value;
    }

    public bool FolderSkipPlay
    {
        get => Values.FolderSkipPlayMode;
        set => Values.FolderSkipPlayMode = value;
    }

    public bool SinglePlay
    {
        get => Values.SinglePlayMode;
        set => Values.SinglePlayMode = value;
    }

    public bool UsesLr2Body => Values.UsePlayerLR2body;

    public bool UsesUbMplay => Values.UsePlayeruBMplay;

    public bool UsesBmiIdxView => Values.UsePlayerBMIIDXView;

    public bool UseExternalPanelImage => Values.UseExternalPanelImage;

    public string StagefilePath => Values.StagefilePath;

    public int PlayerVolume
    {
        get => Values.uBMplayVolume;
        set => Values.uBMplayVolume = value;
    }

    public bool UsesLr2Database => Values.OperationModeLR2DB;
}
