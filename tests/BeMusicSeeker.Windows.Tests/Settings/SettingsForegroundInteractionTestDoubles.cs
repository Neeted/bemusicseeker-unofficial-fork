using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal sealed class TestSettingsDialogPlayerFactoryPort : ISettingsDialogPlayerFactoryPort
{
    private readonly IList<string>? sequence;

    internal TestSettingsDialogPlayerFactoryPort(IList<string>? sequence = null)
    {
        this.sequence = sequence;
    }

    internal Exception? DefaultFactoryFailure { get; set; }

    internal Exception? ConfiguredFactoryFailure { get; set; }

    internal StartupSettingsSnapshot? LastConfiguredSettings { get; private set; }

    /// <summary>実player型の能力を確認するケースで、process起動を必要としない構成を返します。</summary>
    internal Func<StartupSettingsSnapshot, IBMSPlayer>? ConfiguredPlayerFactory { get; set; }

    public IBMSPlayer CreateDefaultBmsPlayer()
    {
        sequence?.Add("factory-default");
        if (DefaultFactoryFailure != null)
        {
            throw DefaultFactoryFailure;
        }
        return new RecordingPlaybackPlayer();
    }

    public IBMSPlayer CreateBmsPlayerForSettings(StartupSettingsSnapshot settings)
    {
        sequence?.Add("factory-configured");
        LastConfiguredSettings = settings;
        if (ConfiguredFactoryFailure != null)
        {
            throw ConfiguredFactoryFailure;
        }
        return ConfiguredPlayerFactory?.Invoke(settings) ?? new RecordingPlaybackPlayer();
    }

}

internal sealed class TestSettingsDialogPlaybackRuntimePort : ISettingsDialogPlaybackRuntimePort
{
    private readonly IList<string>? sequence;

    internal TestSettingsDialogPlaybackRuntimePort(IList<string>? sequence = null)
    {
        this.sequence = sequence;
    }

    internal int ApplyCount { get; private set; }

    internal int NotifyCount { get; private set; }

    internal IBMSPlayer? LastReplacementPlayer { get; private set; }

    public void UsePlayerForNextPlayback(Func<IBMSPlayer> createPlayer, bool supportsBmson)
    {
        ApplyCount++;
        LastReplacementPlayer = null;
        sequence?.Add("apply");
        return;
    }

    public void NotifySettingsChanged()
    {
        NotifyCount++;
        sequence?.Add("notify");
    }

    public Task StopPlayback()
    {
        return Task.CompletedTask;
    }
}
