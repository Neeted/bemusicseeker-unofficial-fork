using System;
using System.Windows;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Models;

/// <summary>
/// Immutable player configuration captured for one playback operation.
/// </summary>
internal sealed class PlayerSettingsSnapshot
{
    internal PlayerSettingsSnapshot(
        AudioDriver playerDriver,
        string playerDevice,
        string playerDeviceName,
        SampleRate playerSampleRate,
        SampleFormat playerFormat,
        float playerBufferSize,
        bool playerWasapiParam,
        int playerVolume,
        Point lr2bodyResolution,
        bool isSaveLr2bodyWindowPosition,
        ExternalWindowPlacement lr2bodyWindowPlacement)
    {
        PlayerDriver = playerDriver;
        PlayerDevice = playerDevice;
        PlayerDeviceName = playerDeviceName;
        PlayerSampleRate = playerSampleRate;
        PlayerFormat = playerFormat;
        PlayerBufferSize = playerBufferSize;
        PlayerWASAPIParam = playerWasapiParam;
        PlayerVolume = playerVolume;
        LR2bodyResolution = lr2bodyResolution;
        IsSaveLR2bodyWindowPosition = isSaveLr2bodyWindowPosition;
        LR2bodyWindowPlacement = lr2bodyWindowPlacement;
    }

    internal AudioDriver PlayerDriver { get; }

    internal string PlayerDevice { get; }

    internal string PlayerDeviceName { get; }

    internal SampleRate PlayerSampleRate { get; }

    internal SampleFormat PlayerFormat { get; }

    internal float PlayerBufferSize { get; }

    internal bool PlayerWASAPIParam { get; }

    internal int PlayerVolume { get; }

    internal Point LR2bodyResolution { get; }

    internal bool IsSaveLR2bodyWindowPosition { get; }

    internal ExternalWindowPlacement LR2bodyWindowPlacement { get; }
}

/// <summary>
/// Owns the persisted settings boundary used by playback player implementations.
/// </summary>
internal interface IPlayerSettingsGateway
{
    PlayerSettingsSnapshot CaptureSnapshot();

    void ApplyNegotiatedAudioSettings(
        AudioDriver playerDriver,
        string playerDevice,
        string playerDeviceName,
        SampleRate playerSampleRate,
        SampleFormat playerFormat);

    void SaveWindowPlacement(ExternalWindowPlacement windowPlacement);
}

/// <summary>
/// Adapts generated settings to the player settings boundary.
/// </summary>
internal sealed class SettingsPlayerSettingsGateway : IPlayerSettingsGateway
{
    private readonly Func<Settings> settingsProvider;

    internal SettingsPlayerSettingsGateway(Func<Settings> settingsProvider)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    private Settings Values => settingsProvider()
        ?? throw new InvalidOperationException("Player settings provider returned null.");

    public PlayerSettingsSnapshot CaptureSnapshot()
    {
        Settings values = Values;
        return new PlayerSettingsSnapshot(
            BassAudioMapping.FromBassDriver(values.PlayerDriver),
            values.PlayerDevice,
            values.PlayerDeviceName,
            values.PlayerSampleRate,
            values.PlayerFormat,
            values.PlayerBufferSize,
            values.PlayerWASAPIParam,
            values.uBMplayVolume,
            values.LR2bodyResolution,
            values.IsSaveLR2bodyWindowPosition,
            ExternalWindowPlacement.FromNative(values.LR2bodyWindowPlacement));
    }

    public void ApplyNegotiatedAudioSettings(
        AudioDriver playerDriver,
        string playerDevice,
        string playerDeviceName,
        SampleRate playerSampleRate,
        SampleFormat playerFormat)
    {
        Settings values = Values;
        values.PlayerDriver = BassAudioMapping.ToBassDriver(playerDriver);
        values.PlayerDevice = playerDevice;
        values.PlayerDeviceName = playerDeviceName;
        values.PlayerSampleRate = playerSampleRate;
        values.PlayerFormat = playerFormat;
    }

    public void SaveWindowPlacement(ExternalWindowPlacement windowPlacement)
    {
        Values.LR2bodyWindowPlacement = windowPlacement?.ToNative()
            ?? throw new ArgumentNullException(nameof(windowPlacement));
        Values.Save();
    }
}
