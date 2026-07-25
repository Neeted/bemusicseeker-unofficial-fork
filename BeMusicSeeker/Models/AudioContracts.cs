using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Properties;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Models;

public enum AudioDriver
{
    Invalid = -2,
    NullDevice = -1,
    DirectSound = 0,
    WasapiShared = 1,
    WasapiExclusive = 2,
    Asio = 3
}

public enum AudioNormalization
{
    None = 0,
    PeakLevel = 1,
    RmsValue = 2
}

public readonly struct AudioDeviceInfo
{
    internal AudioDeviceInfo(string name, string driver)
    {
        Name = name;
        Driver = driver;
    }

    public string Name { get; }

    public string Driver { get; }

    public string FriendlyName => string.IsNullOrWhiteSpace(Name) ? "(Default device)" : Name;
}

internal interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDriver driver);

    bool IsEncoderAvailable(EncoderType encoder, string encoderDirectory);
}

internal sealed class BassAudioDeviceCatalog : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDriver driver)
    {
        Ribbit.Media.BassAudioPlayer.DeviceDriver bassDriver = BassAudioMapping.ToBassDriver(driver);
        return [.. Ribbit.Media.BassAudioPlayer.DeviceList[bassDriver].Select(device => new AudioDeviceInfo(device.Name, device.Driver))];
    }

    public bool IsEncoderAvailable(EncoderType encoder, string encoderDirectory)
    {
        string previousDirectory = Ribbit.Media.BassAudioWriter.EncoderDirectory;
        try
        {
            Ribbit.Media.BassAudioWriter.EncoderDirectory = encoderDirectory;
            return Ribbit.Media.BassAudioWriter.IsEncoderAvailable(encoder);
        }
        finally
        {
            Ribbit.Media.BassAudioWriter.EncoderDirectory = previousDirectory;
        }
    }
}

internal static class BassAudioMapping
{
    internal static Ribbit.BMS.BMSAutoPlayWriter.Normalization ToBassNormalization(AudioNormalization normalization)
    {
        return (Ribbit.BMS.BMSAutoPlayWriter.Normalization)(int)normalization;
    }

    internal static AudioNormalization FromBassNormalization(Ribbit.BMS.BMSAutoPlayWriter.Normalization normalization)
    {
        return (AudioNormalization)(int)normalization;
    }

    internal static Ribbit.Media.BassAudioPlayer.DeviceDriver ToBassDriver(AudioDriver driver)
    {
        return (Ribbit.Media.BassAudioPlayer.DeviceDriver)(int)driver;
    }

    internal static AudioDriver FromBassDriver(Ribbit.Media.BassAudioPlayer.DeviceDriver driver)
    {
        return (AudioDriver)(int)driver;
    }
}

internal sealed class AudioEncodingSettingsSnapshot
{
    internal AudioEncodingSettingsSnapshot(
        EncoderType encoder,
        SampleRate encoderSampleRate,
        SampleFormat encoderFormat,
        AudioNormalization encoderNormalization,
        float encoderQuality,
        string encoderExeDirectory,
        float encoderAmplifier,
        string encodeFileNameFormat)
    {
        Encoder = encoder;
        EncoderSampleRate = encoderSampleRate;
        EncoderFormat = encoderFormat;
        EncoderNormalization = encoderNormalization;
        EncoderQuality = encoderQuality;
        EncoderExeDirectory = encoderExeDirectory;
        EncoderAmplifier = encoderAmplifier;
        EncodeFileNameFormat = encodeFileNameFormat;
    }

    internal EncoderType Encoder { get; }

    internal SampleRate EncoderSampleRate { get; }

    internal SampleFormat EncoderFormat { get; }

    internal AudioNormalization EncoderNormalization { get; }

    internal float EncoderQuality { get; }

    internal string EncoderExeDirectory { get; }

    internal float EncoderAmplifier { get; }

    internal string EncodeFileNameFormat { get; }
}

internal interface IAudioSettingsGateway
{
    AudioDriver PlayerDriver { get; set; }

    AudioNormalization EncoderNormalization { get; set; }

    AudioEncodingSettingsSnapshot CaptureEncodingSettings();

    void ApplyEncoderFallback(EncoderType encoder);
}

internal sealed class SettingsAudioGateway : IAudioSettingsGateway
{
    private readonly Func<Settings> settingsProvider;

    internal SettingsAudioGateway(Func<Settings> settingsProvider)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    private Settings Values => settingsProvider()
        ?? throw new InvalidOperationException("Audio settings provider returned null.");

    public AudioDriver PlayerDriver
    {
        get => BassAudioMapping.FromBassDriver(Values.PlayerDriver);
        set => Values.PlayerDriver = BassAudioMapping.ToBassDriver(value);
    }

    public AudioNormalization EncoderNormalization
    {
        get => BassAudioMapping.FromBassNormalization(Values.EncoderNormalization);
        set => Values.EncoderNormalization = BassAudioMapping.ToBassNormalization(value);
    }

    public AudioEncodingSettingsSnapshot CaptureEncodingSettings()
    {
        Settings values = Values;
        return new AudioEncodingSettingsSnapshot(
            values.Encoder,
            values.EncoderSampleRate,
            values.EncoderFormat,
            BassAudioMapping.FromBassNormalization(values.EncoderNormalization),
            values.EncoderQuality,
            values.EncoderExeDir,
            values.EncoderAmplifier,
            values.EncodeFileNameFormat);
    }

    public void ApplyEncoderFallback(EncoderType encoder)
    {
        Values.Encoder = encoder;
    }
}

internal sealed class AudioPlaybackInitializationResult
{
    internal AudioPlaybackInitializationResult(
        AudioDriver playerDriver,
        string playerDevice,
        string playerDeviceName,
        SampleRate playerSampleRate,
        SampleFormat playerFormat,
        double playerLatency)
    {
        PlayerDriver = playerDriver;
        PlayerDevice = playerDevice;
        PlayerDeviceName = playerDeviceName;
        PlayerSampleRate = playerSampleRate;
        PlayerFormat = playerFormat;
        PlayerLatency = playerLatency;
    }

    internal AudioDriver PlayerDriver { get; }

    internal string PlayerDevice { get; }

    internal string PlayerDeviceName { get; }

    internal SampleRate PlayerSampleRate { get; }

    internal SampleFormat PlayerFormat { get; }

    internal double PlayerLatency { get; }
}

internal interface IAudioPlaybackRuntime
{
    AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings);

    int CurrentVoices { get; }

    int MaxVoices { get; }

    void ClearMaxVoices();

    void SetVolume(int volume);

    void Free();
}

internal sealed class BassAudioPlaybackRuntime : IAudioPlaybackRuntime
{
    public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings)
    {
        Ribbit.Media.BassAudioPlayer.DeviceDescriptor descriptor = string.IsNullOrWhiteSpace(settings.PlayerDevice)
            ? default
            : new Ribbit.Media.BassAudioPlayer.DeviceDescriptor(settings.PlayerDeviceName, settings.PlayerDevice);
        Ribbit.Media.BassAudioPlayer.Frequency = settings.PlayerSampleRate;
        Ribbit.Media.BassAudioPlayer.Format = settings.PlayerFormat;
        SetVolume(settings.PlayerVolume);
        descriptor = Ribbit.Media.BassAudioPlayer.Initialize(
            BassAudioMapping.ToBassDriver(settings.PlayerDriver),
            descriptor,
            settings.PlayerBufferSize,
            settings.PlayerWASAPIParam);
        AudioDriver driver = BassAudioMapping.FromBassDriver(Ribbit.Media.BassAudioPlayer.DriverType);
        if (driver < AudioDriver.DirectSound)
        {
            driver = AudioDriver.DirectSound;
            Ribbit.Logging.NLogWrapper.TraceLogger.Warn("Sound device not found?");
        }

        return new AudioPlaybackInitializationResult(
            driver,
            descriptor.Driver,
            descriptor.Name,
            Ribbit.Media.BassAudioPlayer.Frequency,
            Ribbit.Media.BassAudioPlayer.Format,
            Ribbit.Media.BassAudioPlayer.Latency);
    }

    public int CurrentVoices => Ribbit.Media.BassAudioPlayer.CurrentVoices;

    public int MaxVoices => Ribbit.Media.BassAudioPlayer.MaxVoices;

    public void ClearMaxVoices()
    {
        Ribbit.Media.BassAudioPlayer.ClearMaxVoices();
    }

    public void SetVolume(int volume)
    {
        Ribbit.Media.BassAudioPlayer.DeviceVolume = Math.Min(100, Math.Max(0, volume)) / 100f;
    }

    public void Free()
    {
        Ribbit.Media.BassAudioPlayer.Free();
    }
}
