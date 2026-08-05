using System;
using System.Collections.Generic;
using System.Linq;
using Ribbit.Media;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using Un4seen.Bass.AddOn.Mix;

namespace Ribbit.Media.Audio;

/// <summary>Retains one DirectSound descriptor with its original BASS native index.</summary>
internal sealed class BassDirectSoundDevice
{
    /// <summary>Creates an indexed DirectSound catalog entry.</summary>
    internal BassDirectSoundDevice(
        int nativeIndex,
        BassAudioPlayer.DeviceDescriptor descriptor,
        bool isEnabled,
        bool isDefault)
    {
        NativeIndex = nativeIndex;
        Descriptor = descriptor;
        IsEnabled = isEnabled;
        IsDefault = isDefault;
    }

    /// <summary>Gets the original index accepted by BASS_Init.</summary>
    internal int NativeIndex { get; }

    /// <summary>Gets the persisted device identity and display name.</summary>
    internal BassAudioPlayer.DeviceDescriptor Descriptor { get; }

    /// <summary>Gets whether BASS reports the endpoint as enabled.</summary>
    internal bool IsEnabled { get; }

    /// <summary>Gets whether BASS reports the endpoint as the system default.</summary>
    internal bool IsDefault { get; }
}

/// <summary>
/// Exposes the native calls required to negotiate one DirectSound output graph.
/// </summary>
internal interface IDirectSoundNegotiationNativeBoundary
{
    /// <summary>Gets DirectSound catalog entries with their original native indices.</summary>
    IReadOnlyList<BassDirectSoundDevice> GetDevices();

    /// <summary>Initializes the selected BASS output device.</summary>
    bool InitializeCore(int deviceIndex, int rate);

    /// <summary>Gets the BASS device selected by initialization.</summary>
    int GetCoreDevice();

    /// <summary>Gets one device descriptor after initialization readback.</summary>
    BASS_DEVICEINFO GetDeviceInfo(int deviceIndex);

    /// <summary>Gets the initialized output capabilities and actual rate.</summary>
    BASS_INFO GetInfo();

    /// <summary>Sets one integer BASS configuration value.</summary>
    bool SetConfig(BASSConfig option, int value);

    /// <summary>Gets one integer BASS configuration value.</summary>
    int GetConfig(BASSConfig option);

    /// <summary>Creates the Float32 decode mixer.</summary>
    int CreateMixer(int rate, int channels, BASSFlag flags);

    /// <summary>Creates the Float32 callback output stream.</summary>
    int CreateOutputStream(int rate, int channels, BASSFlag flags, STREAMPROC callback);

    /// <summary>Starts the callback output stream.</summary>
    bool Play(int streamHandle);

    /// <summary>Creates a volume effect on the Float32 decode mixer.</summary>
    int CreateVolumeEffect(int mixerHandle);

    /// <summary>Sets the application gain on a mixer-owned volume effect.</summary>
    bool SetVolumeEffect(int effectHandle, float volume);

    /// <summary>Gets the BASS error immediately after a failed native call.</summary>
    BASSError GetCoreError();
}

/// <summary>Negotiates DirectSound device selection without losing native catalog indices.</summary>
internal sealed class BassDirectSoundNegotiator
{
    private readonly IDirectSoundNegotiationNativeBoundary native;

    /// <summary>Creates a DirectSound negotiator over a replaceable native boundary.</summary>
    internal BassDirectSoundNegotiator(IDirectSoundNegotiationNativeBoundary native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>Initializes one DirectSound graph and reads back its actual device and rate.</summary>
    internal BassAudioBackendResult Initialize(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        STREAMPROC callback,
        float initialGain)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(callback);
        if (request.Backend != BassAudioPlayer.DeviceDriver.DIRECT_SOUND)
        {
            throw new ArgumentException(
                "The DirectSound negotiator requires a DirectSound request.",
                nameof(request));
        }

        var attempts = new List<BassAudioBackendAttempt>();
        var fallbackReasons = new List<string>();
        BassDirectSoundDevice[] devices = GetSelectableDevices(native.GetDevices()).ToArray();
        if (devices.Length == 0)
        {
            throw Failure(
                request,
                session,
                "BASS_GetDeviceInfos",
                null,
                "DirectSound device not found.");
        }

        (int initializationIndex, BassAudioPlayer.DeviceDescriptor selectedDescriptor, string deviceFallback) =
            SelectDevice(devices, request.Device);
        session.ActualDevice = selectedDescriptor;
        session.CoreDeviceIndex = initializationIndex;
        AddFallbackReason(fallbackReasons, deviceFallback);

        if (!native.InitializeCore(initializationIndex, 44100))
        {
            BASSError error = native.GetCoreError();
            throw Failure(request, session, "BASS_Init", error, "BASS_Init failed: " + error);
        }

        // BASS_Init succeeded, so cleanup owns the selected core before any readback can fail.
        session.CoreInitialized = true;
        session.CoreDeviceIndex = native.GetCoreDevice();
        BASS_DEVICEINFO actualInfo = native.GetDeviceInfo(session.CoreDeviceIndex);
        if (actualInfo == null)
        {
            BASSError error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_GetDeviceInfo",
                error,
                "BASS_GetDeviceInfo failed: " + error);
        }
        var actualDevice = new BassAudioPlayer.DeviceDescriptor(actualInfo.name, actualInfo.driver);
        session.ActualDevice = actualDevice;

        BASS_INFO info = native.GetInfo();
        if (info == null || info.freq <= 0)
        {
            BASSError error = native.GetCoreError();
            throw Failure(request, session, "BASS_GetInfo", error, "BASS_GetInfo failed: " + error);
        }

        int configuredLatency = request.LatencyMilliseconds <= 0f
            ? 100
            : (int)System.Math.Ceiling(request.LatencyMilliseconds + 50f);
        native.SetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD, 5);
        int updatePeriod = native.GetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD);
        native.SetConfig(
            BASSConfig.BASS_CONFIG_BUFFER,
            System.Math.Max(updatePeriod + 1, configuredLatency));
        int actualLatency = native.GetConfig(BASSConfig.BASS_CONFIG_BUFFER);

        if (request.Rate != SampleRate.AUTO && (int)request.Rate != info.freq)
        {
            AddFallbackReason(
                fallbackReasons,
                "Requested DirectSound rate " + (int)request.Rate
                + " was normalized to device rate " + info.freq + ".");
        }
        if (request.Format is not SampleFormat.AUTO and not SampleFormat.SAMPLE_FLOAT_32BIT)
        {
            AddFallbackReason(
                fallbackReasons,
                "Requested DirectSound engine format " + request.Format
                + " was normalized to Float32.");
        }

        BASSFlag mixerFlags = BASSFlag.BASS_SAMPLE_FLOAT
            | BASSFlag.BASS_STREAM_PRESCAN
            | BASSFlag.BASS_STREAM_DECODE;
        int mixerHandle = native.CreateMixer(info.freq, 2, mixerFlags);
        if (mixerHandle == 0)
        {
            BASSError error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_Mixer_StreamCreate",
                error,
                "BASS_Mixer_StreamCreate failed: " + error);
        }
        session.MixerHandle = mixerHandle;

        session.VolumeEffectHandle = native.CreateVolumeEffect(mixerHandle);
        if (session.VolumeEffectHandle == 0)
        {
            BASSError error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_ChannelSetFX(BASS_FX_BFX_VOLUME)",
                error,
                "BASS_ChannelSetFX for DirectSound mixer volume failed: " + error);
        }
        if (!native.SetVolumeEffect(session.VolumeEffectHandle, initialGain))
        {
            BASSError error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_FXSetParameters(BASS_FX_BFX_VOLUME)",
                error,
                "BASS_FXSetParameters for DirectSound mixer volume failed: " + error);
        }

        int outputHandle = native.CreateOutputStream(
            info.freq,
            2,
            BASSFlag.BASS_SAMPLE_FLOAT,
            callback);
        if (outputHandle == 0)
        {
            BASSError error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_StreamCreate",
                error,
                "BASS_StreamCreate failed: " + error);
        }
        session.OutputHandle = outputHandle;

        if (!native.Play(outputHandle))
        {
            BASSError error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_ChannelPlay",
                error,
                "BASS_ChannelPlay failed: " + error);
        }
        session.IsStarted = true;

        var result = new BassAudioBackendResult(
            request,
            actualDevice,
            (SampleRate)info.freq,
            SampleFormat.SAMPLE_FLOAT_32BIT,
            SampleFormat.UNKNOWN,
            actualLatency,
            mixerHandle,
            attempts.AsReadOnly(),
            fallbackReasons.Count == 0 ? null : string.Join(" ", fallbackReasons));
        session.NegotiationResult = result;
        return result;
    }

    /// <summary>
    /// Applies application volume to the effect owned by an initialized DirectSound mixer
    /// and returns the immediately captured BASS error when the native call fails.
    /// </summary>
    internal bool TrySetMixerGain(
        BassAudioSession session,
        float volume,
        out BASSError error)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ActualBackend != BassAudioPlayer.DeviceDriver.DIRECT_SOUND
            || session.MixerHandle == 0
            || session.VolumeEffectHandle == 0)
        {
            error = BASSError.BASS_ERROR_HANDLE;
            return false;
        }
        if (native.SetVolumeEffect(session.VolumeEffectHandle, volume))
        {
            error = BASSError.BASS_OK;
            return true;
        }

        error = native.GetCoreError();
        return false;
    }

    /// <summary>
    /// Removes BASS's index-zero no-sound device and entries that cannot identify an audible driver.
    /// </summary>
    internal static IReadOnlyList<BassDirectSoundDevice> GetSelectableDevices(
        IReadOnlyList<BassDirectSoundDevice> devices) =>
        devices
            .Where(device =>
                device.NativeIndex != 0
                && device.IsEnabled
                && !string.IsNullOrWhiteSpace(device.Descriptor.Driver))
            .ToArray();

    private static (
        int InitializationIndex,
        BassAudioPlayer.DeviceDescriptor SelectedDescriptor,
        string FallbackReason) SelectDevice(
        IReadOnlyList<BassDirectSoundDevice> devices,
        BassAudioPlayer.DeviceDescriptor requestedDevice)
    {
        if (!requestedDevice.Equals(default(BassAudioPlayer.DeviceDescriptor)))
        {
            BassDirectSoundDevice exact = devices.FirstOrDefault(device =>
                requestedDevice.Name == device.Descriptor.Name
                && requestedDevice.Driver == device.Descriptor.Driver);
            if (exact != null)
            {
                return (exact.NativeIndex, exact.Descriptor, null);
            }

            BassDirectSoundDevice compatibleName = devices.FirstOrDefault(device =>
                requestedDevice.Name == device.Descriptor.Name);
            if (compatibleName != null)
            {
                return (
                    compatibleName.NativeIndex,
                    compatibleName.Descriptor,
                    "Requested DirectSound device identity was stale; a compatible name match was used.");
            }
        }

        BassDirectSoundDevice defaultDevice = devices.FirstOrDefault(device => device.IsDefault);
        if (requestedDevice.Equals(default(BassAudioPlayer.DeviceDescriptor)))
        {
            return (-1, defaultDevice?.Descriptor ?? default, null);
        }
        return (
            -1,
            defaultDevice?.Descriptor ?? default,
            "Requested DirectSound device was unavailable; the backend default device was used.");
    }

    private static void AddFallbackReason(List<string> reasons, string reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            reasons.Add(reason);
        }
    }

    private static AudioInitializationException Failure(
        BassAudioNegotiationRequest request,
        BassAudioSession session,
        string stage,
        BASSError? error,
        string message) =>
        new(
            request.Backend,
            BassAudioPlayer.DeviceDriver.DIRECT_SOUND,
            stage,
            request.Device,
            session.ActualDevice,
            "BASS",
            error,
            message);
}

/// <summary>Forwards DirectSound negotiation calls to the currently bundled Bass.Net API.</summary>
internal sealed class BassDirectSoundNegotiationNativeBoundary
    : IDirectSoundNegotiationNativeBoundary
{
    /// <inheritdoc />
    public IReadOnlyList<BassDirectSoundDevice> GetDevices() =>
        Bass.BASS_GetDeviceInfos()
            .Select((info, index) => new BassDirectSoundDevice(
                index,
                new BassAudioPlayer.DeviceDescriptor(info.name, info.driver),
                info.IsEnabled,
                info.IsDefault))
            .ToArray();

    /// <inheritdoc />
    public bool InitializeCore(int deviceIndex, int rate) =>
        // Phase 2 must evaluate BASS_DEVICE_DSOUND with an upgraded compatible BASS set.
        // The bundled API keeps the established flags and WPF-independent null window handle.
        Bass.BASS_Init(deviceIndex, rate, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero);

    /// <inheritdoc />
    public int GetCoreDevice() => Bass.BASS_GetDevice();

    /// <inheritdoc />
    public BASS_DEVICEINFO GetDeviceInfo(int deviceIndex) => Bass.BASS_GetDeviceInfo(deviceIndex);

    /// <inheritdoc />
    public BASS_INFO GetInfo() => Bass.BASS_GetInfo();

    /// <inheritdoc />
    public bool SetConfig(BASSConfig option, int value) => Bass.BASS_SetConfig(option, value);

    /// <inheritdoc />
    public int GetConfig(BASSConfig option) => Bass.BASS_GetConfig(option);

    /// <inheritdoc />
    public int CreateMixer(int rate, int channels, BASSFlag flags) =>
        BassMix.BASS_Mixer_StreamCreate(rate, channels, flags);

    /// <inheritdoc />
    public int CreateOutputStream(int rate, int channels, BASSFlag flags, STREAMPROC callback) =>
        Bass.BASS_StreamCreate(rate, channels, flags, callback, IntPtr.Zero);

    /// <inheritdoc />
    public bool Play(int streamHandle) => Bass.BASS_ChannelPlay(streamHandle, restart: false);

    /// <inheritdoc />
    public int CreateVolumeEffect(int mixerHandle) =>
        Bass.BASS_ChannelSetFX(mixerHandle, BASSFXType.BASS_FX_BFX_VOLUME, 1);

    /// <inheritdoc />
    public bool SetVolumeEffect(int effectHandle, float volume) =>
        Bass.BASS_FXSetParameters(effectHandle, new BASS_BFX_VOLUME(volume));

    /// <inheritdoc />
    public BASSError GetCoreError() => Bass.BASS_ErrorGetCode();
}
