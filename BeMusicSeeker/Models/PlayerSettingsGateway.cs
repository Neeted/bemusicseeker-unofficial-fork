using System;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Models;

public readonly struct PlayerResolution(double width, double height) : IEquatable<PlayerResolution>
{
    public double Width { get; } = width;

    public double Height { get; } = height;

    public bool Equals(PlayerResolution other)
        => Width.Equals(other.Width) && Height.Equals(other.Height);

    public override bool Equals(object obj)
        => obj is PlayerResolution other && Equals(other);

    public override int GetHashCode()
        => unchecked((Width.GetHashCode() * 397) ^ Height.GetHashCode());

    public static bool operator ==(PlayerResolution left, PlayerResolution right)
        => left.Equals(right);

    public static bool operator !=(PlayerResolution left, PlayerResolution right)
        => !left.Equals(right);
}

internal static class PlayerResolutionSettingsAdapter
{
    internal static PlayerResolution FromSettings(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new PlayerResolution(settings.LR2bodyResolution.X, settings.LR2bodyResolution.Y);
    }

    internal static void SaveToSettings(Settings settings, PlayerResolution resolution)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        settings.LR2bodyResolution = new System.Windows.Point(resolution.Width, resolution.Height);
    }
}

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
        PlayerResolution lr2bodyResolution,
        bool isSaveLr2bodyWindowPosition,
        WindowPlacement lr2bodyWindowPlacement)
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

    internal PlayerResolution LR2bodyResolution { get; }

    internal bool IsSaveLR2bodyWindowPosition { get; }

    internal WindowPlacement LR2bodyWindowPlacement { get; }
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

    void SaveWindowPlacement(WindowPlacement windowPlacement);
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
            PlayerResolutionSettingsAdapter.FromSettings(values),
            values.IsSaveLR2bodyWindowPosition,
            Win32WindowPlacementAdapter.FromNative(values.LR2bodyWindowPlacement));
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

    public void SaveWindowPlacement(WindowPlacement windowPlacement)
    {
        Values.LR2bodyWindowPlacement = Win32WindowPlacementAdapter.ToNative(windowPlacement);
        Values.Save();
    }
}
