using System;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Models;

/// <summary>
/// 再生機能の設定と、操作終端で保存する音量候補を提供します。
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

    /// <summary>現在再生へ即時反映する音量候補です。保存済み設定の読取りには公開しません。</summary>
    int PlayerVolume { get; set; }

    /// <summary>音量候補だけを保存します。失敗時も候補を破棄し、最新の確定音量へ戻します。</summary>
    void CommitPlayerVolume();

    bool UsesLr2Database { get; }
}

/// <summary>
/// 再生設定の確定値と、操作終端まで公開しない音量候補を再生機能へ接続します。
/// </summary>
internal sealed class SettingsPlaybackSettingsStore : IPlaybackSettingsStore
{
    private readonly Func<Settings> settingsProvider;
    private int? previewPlayerVolume;

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

    /// <inheritdoc />
    public int PlayerVolume
    {
        get => previewPlayerVolume ?? Values.uBMplayVolume;
        set => previewPlayerVolume = value;
    }

    /// <inheritdoc />
    public void CommitPlayerVolume()
    {
        if (previewPlayerVolume is not int volume) { return; }
        try { Values.Commit((nameof(Settings.uBMplayVolume), volume)); }
        finally { previewPlayerVolume = null; }
    }

    public bool UsesLr2Database => Values.OperationModeLR2DB;
}
