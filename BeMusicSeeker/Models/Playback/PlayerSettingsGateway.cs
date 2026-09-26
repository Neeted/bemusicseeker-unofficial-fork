using System;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Models;

/// <summary>音声出力要求の用途を再生経路と設定テストで区別します。</summary>
internal enum AudioOutputPurpose
{
    /// <summary>既存仕様のbackend・機器内代替を許す通常再生です。</summary>
    Playback,

    /// <summary>選択した機器と明示条件を確認する設定テストです。</summary>
    DeviceTest
}

/// <summary>一回の音声出力開始に使う、変更不能な出力条件です。</summary>
internal sealed class AudioOutputRequest : IEquatable<AudioOutputRequest>
{
    /// <summary>方式が適用する出力条件だけを捕捉します。共有のmix条件とASIOのnative形式は保存値から制約しません。</summary>
    internal AudioOutputRequest(
        AudioDriver backend,
        string deviceIdentity,
        string deviceName,
        SampleRate rate,
        SampleFormat format,
        float bufferSize,
        bool eventMode,
        int sampleRateConversionQuality,
        AudioOutputPurpose purpose)
    {
        AudioOutputSelection selection = AudioDriverPolicy.NormalizePersistedSelection(
            new AudioOutputSelection(backend, deviceIdentity, deviceName));
        Backend = selection.Backend;
        DeviceIdentity = selection.DeviceIdentity;
        DeviceName = selection.DeviceName;
        Rate = Backend == AudioDriver.WasapiShared ? SampleRate.AUTO : rate;
        Format = Backend == AudioDriver.WasapiExclusive ? format : SampleFormat.AUTO;
        BufferSize = Backend == AudioDriver.WasapiShared ? 0 : bufferSize;
        EventMode = Backend is AudioDriver.WasapiShared or AudioDriver.WasapiExclusive && eventMode;
        SampleRateConversionQuality = AudioResamplingQuality.Validate(sampleRateConversionQuality);
        Purpose = purpose;
    }

    /// <summary>要求した出力方式を取得します。</summary>
    internal AudioDriver Backend { get; }

    /// <summary>Defaultまたは要求した機器識別子を取得します。</summary>
    internal string DeviceIdentity { get; }

    /// <summary>要求した機器名を取得します。</summary>
    internal string DeviceName { get; }

    /// <summary>要求したレートまたはAutoを取得します。</summary>
    internal SampleRate Rate { get; }

    /// <summary>要求した形式またはAutoを取得します。</summary>
    internal SampleFormat Format { get; }

    /// <summary>希望する出力バッファ長をミリ秒で取得します。</summary>
    internal float BufferSize { get; }

    /// <summary>イベント駆動の出力を要求したか取得します。</summary>
    internal bool EventMode { get; }

    /// <summary>この出力sessionで使う標本化周波数変換品質を取得します。</summary>
    internal int SampleRateConversionQuality { get; }

    /// <summary>この要求を処理する用途を取得します。</summary>
    internal AudioOutputPurpose Purpose { get; }

    /// <summary>すべての出力開始条件が一致するか比較します。音量は実時間操作なので含みません。</summary>
    public bool Equals(AudioOutputRequest other) =>
        other != null
        && Backend == other.Backend
        && string.Equals(DeviceIdentity, other.DeviceIdentity, StringComparison.Ordinal)
        && string.Equals(DeviceName, other.DeviceName, StringComparison.Ordinal)
        && Rate == other.Rate
        && Format == other.Format
        && BufferSize.Equals(other.BufferSize)
        && EventMode == other.EventMode
        && SampleRateConversionQuality == other.SampleRateConversionQuality
        && Purpose == other.Purpose;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is AudioOutputRequest other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        Backend,
        DeviceIdentity,
        DeviceName,
        Rate,
        Format,
        BufferSize,
        EventMode,
        HashCode.Combine(SampleRateConversionQuality, Purpose));
}

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
        WindowPlacement lr2bodyWindowPlacement,
        int sampleRateConversionQuality = AudioResamplingQuality.Default)
    {
        AudioOutputSelection normalized = AudioDriverPolicy.NormalizePersistedSelection(
            new AudioOutputSelection(playerDriver, playerDevice, playerDeviceName));
        PlayerDriver = normalized.Backend;
        PlayerDevice = normalized.DeviceIdentity;
        PlayerDeviceName = normalized.DeviceName;
        PlayerSampleRate = playerSampleRate;
        PlayerFormat = playerFormat;
        PlayerBufferSize = playerBufferSize;
        PlayerWASAPIParam = playerWasapiParam;
        PlayerVolume = playerVolume;
        SampleRateConversionQuality = AudioResamplingQuality.Validate(sampleRateConversionQuality);
        AudioOutputRequest = new AudioOutputRequest(
            PlayerDriver,
            PlayerDevice,
            PlayerDeviceName,
            PlayerSampleRate,
            PlayerFormat,
            PlayerBufferSize,
            PlayerWASAPIParam,
            SampleRateConversionQuality,
            AudioOutputPurpose.Playback);
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

    /// <summary>再生session用に捕捉したサンプルレート変換品質を取得します。</summary>
    internal int SampleRateConversionQuality { get; }

    /// <summary>通常再生へ渡す一回分の音声出力条件を取得します。</summary>
    internal AudioOutputRequest AudioOutputRequest { get; }

    internal PlayerResolution LR2bodyResolution { get; }

    internal bool IsSaveLR2bodyWindowPosition { get; }

    internal WindowPlacement LR2bodyWindowPlacement { get; }
}

/// <summary>
/// Owns the settings snapshot and runtime window-placement boundary used by players.
/// </summary>
internal interface IPlayerSettingsGateway
{
    PlayerSettingsSnapshot CaptureSnapshot();

    /// <summary>Captures placement in memory for the next normal or terminal settings save.</summary>
    void UpdateWindowPlacement(WindowPlacement windowPlacement);
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
        AudioOutputSelection selection = AudioDriverPolicy.NormalizePersistedSelection(new AudioOutputSelection(
            BassAudioMapping.FromBassDriver(values.PlayerDriver),
            values.PlayerDevice,
            values.PlayerDeviceName));
        return new PlayerSettingsSnapshot(
            selection.Backend,
            selection.DeviceIdentity,
            selection.DeviceName,
            values.PlayerSampleRate,
            values.PlayerFormat,
            values.PlayerBufferSize,
            values.PlayerWASAPIParam,
            values.uBMplayVolume,
            PlayerResolutionSettingsAdapter.FromSettings(values),
            values.IsSaveLR2bodyWindowPosition,
            Win32WindowPlacementAdapter.FromNative(values.LR2bodyWindowPlacement),
            values.PlayerResamplingQuality);
    }

    /// <summary>Updates runtime placement without requesting persistence during player cleanup.</summary>
    public void UpdateWindowPlacement(WindowPlacement windowPlacement)
    {
        Values.LR2bodyWindowPlacement = Win32WindowPlacementAdapter.ToNative(windowPlacement);
    }
}
