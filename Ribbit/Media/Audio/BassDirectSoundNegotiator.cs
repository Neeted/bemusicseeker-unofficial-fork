using System;
using System.Collections.Generic;
using System.Linq;
using ManagedBass;
using ManagedBass.Mix;
using Ribbit.Media;

namespace Ribbit.Media.Audio;

/// <summary>Copies the DirectSound device identity returned after initialization.</summary>
internal readonly record struct BassDirectSoundDeviceSnapshot(string Name, string Driver);

/// <summary>Copies the initialized DirectSound sample rate.</summary>
internal readonly record struct BassDirectSoundInfoSnapshot(int SampleRate);

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
    /// <summary>Gets DirectSound catalog entries and captures the native failure on false.</summary>
    bool TryGetDevices(out IReadOnlyList<BassDirectSoundDevice> devices, out Errors error);

    /// <summary>Initializes the selected BASS output device with explicit core flags.</summary>
    bool InitializeCore(int deviceIndex, int rate, DeviceInitFlags flags);

    /// <summary>Gets the BASS device selected by initialization.</summary>
    int GetCoreDevice();

    /// <summary>Gets one device descriptor and captures the native failure on false.</summary>
    bool TryGetDeviceInfo(
        int deviceIndex,
        out BassDirectSoundDeviceSnapshot deviceInfo,
        out Errors error);

    /// <summary>Gets the initialized output rate and captures the native failure on false.</summary>
    bool TryGetInfo(out BassDirectSoundInfoSnapshot info, out Errors error);

    /// <summary>Sets one integer BASS configuration value.</summary>
    bool SetConfig(Configuration option, int value);

    /// <summary>Gets one integer BASS configuration value.</summary>
    int GetConfig(Configuration option);

    /// <summary>Creates the Float32 decode mixer.</summary>
    int CreateMixer(int rate, int channels, BassFlags flags);

    /// <summary>Creates the Float32 callback output stream.</summary>
    int CreateOutputStream(int rate, int channels, BassFlags flags, StreamProcedure callback);

    /// <summary>Starts the callback output stream.</summary>
    bool Play(int streamHandle);

    /// <summary>Creates a volume effect on the Float32 decode mixer.</summary>
    int CreateVolumeEffect(int mixerHandle);

    /// <summary>Sets the application gain on a mixer-owned volume effect.</summary>
    bool SetVolumeEffect(int effectHandle, float volume);

    /// <summary>Gets the BASS error immediately after a failed native call.</summary>
    Errors GetCoreError();
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
        StreamProcedure callback,
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
        if (!native.TryGetDevices(
                out IReadOnlyList<BassDirectSoundDevice> availableDevices,
                out Errors devicesError))
        {
            throw Failure(
                request,
                session,
                "BASS_GetDeviceInfos",
                devicesError,
                "BASS_GetDeviceInfos failed: "
                + BassNativeErrorFormatter.Format(devicesError));
        }

        BassDirectSoundDevice[] devices = GetSelectableDevices(availableDevices).ToArray();
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

        if (!native.InitializeCore(initializationIndex, 44100, DeviceInitFlags.Default))
        {
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_Init",
                error,
                "BASS_Init failed: " + BassNativeErrorFormatter.Format(error));
        }

        // BASS_Init succeeded, so cleanup owns the selected core before any readback can fail.
        session.CoreInitialized = true;
        session.CoreDeviceIndex = native.GetCoreDevice();
        if (!native.TryGetDeviceInfo(
                session.CoreDeviceIndex,
                out BassDirectSoundDeviceSnapshot actualInfo,
                out Errors deviceInfoError))
        {
            throw Failure(
                request,
                session,
                "BASS_GetDeviceInfo",
                deviceInfoError,
                "BASS_GetDeviceInfo failed: "
                + BassNativeErrorFormatter.Format(deviceInfoError));
        }
        var actualDevice = new BassAudioPlayer.DeviceDescriptor(actualInfo.Name, actualInfo.Driver);
        session.ActualDevice = actualDevice;

        if (!native.TryGetInfo(
                out BassDirectSoundInfoSnapshot info,
                out Errors infoError))
        {
            throw Failure(
                request,
                session,
                "BASS_GetInfo",
                infoError,
                "BASS_GetInfo failed: "
                + BassNativeErrorFormatter.Format(infoError));
        }
        if (info.SampleRate <= 0)
        {
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_GetInfo",
                error,
                "BASS_GetInfo failed: " + BassNativeErrorFormatter.Format(error));
        }

        int configuredLatency = request.LatencyMilliseconds <= 0f
            ? 100
            : (int)System.Math.Ceiling(request.LatencyMilliseconds + 50f);
        native.SetConfig(Configuration.UpdatePeriod, 5);
        int updatePeriod = native.GetConfig(Configuration.UpdatePeriod);
        native.SetConfig(
            Configuration.PlaybackBufferLength,
            System.Math.Max(updatePeriod + 1, configuredLatency));
        int actualLatency = native.GetConfig(Configuration.PlaybackBufferLength);

        if (request.Rate != SampleRate.AUTO && (int)request.Rate != info.SampleRate)
        {
            AddFallbackReason(
                fallbackReasons,
                "Requested DirectSound rate " + (int)request.Rate
                + " was normalized to device rate " + info.SampleRate + ".");
        }
        if (request.Format is not SampleFormat.AUTO and not SampleFormat.SAMPLE_FLOAT_32BIT)
        {
            AddFallbackReason(
                fallbackReasons,
                "Requested DirectSound engine format " + request.Format
                + " was normalized to Float32.");
        }

        BassFlags mixerFlags = BassFlags.Float
            | BassFlags.Prescan
            | BassFlags.Decode;
        int mixerHandle = native.CreateMixer(info.SampleRate, 2, mixerFlags);
        if (mixerHandle == 0)
        {
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_Mixer_StreamCreate",
                error,
                "BASS_Mixer_StreamCreate failed: " + BassNativeErrorFormatter.Format(error));
        }
        session.MixerHandle = mixerHandle;

        session.VolumeEffectHandle = native.CreateVolumeEffect(mixerHandle);
        if (session.VolumeEffectHandle == 0)
        {
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_ChannelSetFX(BASS_FX_BFX_VOLUME)",
                error,
                "BASS_ChannelSetFX for DirectSound mixer volume failed: "
                + BassNativeErrorFormatter.Format(error));
        }
        if (!native.SetVolumeEffect(session.VolumeEffectHandle, initialGain))
        {
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_FXSetParameters(BASS_FX_BFX_VOLUME)",
                error,
                "BASS_FXSetParameters for DirectSound mixer volume failed: "
                + BassNativeErrorFormatter.Format(error));
        }

        int outputHandle = native.CreateOutputStream(
            info.SampleRate,
            2,
            BassFlags.Float,
            callback);
        if (outputHandle == 0)
        {
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_StreamCreate",
                error,
                "BASS_StreamCreate failed: " + BassNativeErrorFormatter.Format(error));
        }
        session.OutputHandle = outputHandle;

        if (!native.Play(outputHandle))
        {
            Errors error = native.GetCoreError();
            throw Failure(
                request,
                session,
                "BASS_ChannelPlay",
                error,
                "BASS_ChannelPlay failed: " + BassNativeErrorFormatter.Format(error));
        }
        session.IsStarted = true;

        var result = new BassAudioBackendResult(
            request,
            actualDevice,
            (SampleRate)info.SampleRate,
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
        out Errors error)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ActualBackend != BassAudioPlayer.DeviceDriver.DIRECT_SOUND
            || session.MixerHandle == 0
            || session.VolumeEffectHandle == 0)
        {
            error = Errors.Handle;
            return false;
        }
        if (native.SetVolumeEffect(session.VolumeEffectHandle, volume))
        {
            error = Errors.OK;
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
        Errors? error,
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

/// <summary>Forwards DirectSound negotiation calls to ManagedBass.</summary>
internal sealed class BassDirectSoundNegotiationNativeBoundary
    : IDirectSoundNegotiationNativeBoundary
{
    /// <inheritdoc />
    public bool TryGetDevices(
        out IReadOnlyList<BassDirectSoundDevice> devices,
        out Errors error)
    {
        if (!BassAudioDeviceEnumeration.TryEnumerate(
                TryReadDeviceInfo,
                out DeviceInfo[] nativeDevices,
                out error))
        {
            devices = [];
            return false;
        }

        var result = new List<BassDirectSoundDevice>(nativeDevices.Length);
        for (int index = 0; index < nativeDevices.Length; index++)
        {
            DeviceInfo info = nativeDevices[index];
            result.Add(new BassDirectSoundDevice(
                index,
                new BassAudioPlayer.DeviceDescriptor(info.Name, info.Driver),
                info.IsEnabled,
                info.IsDefault));
        }

        devices = result.AsReadOnly();
        return true;
    }

    private static bool TryReadDeviceInfo(
        int index,
        out DeviceInfo deviceInfo,
        out Errors error)
    {
        if (Bass.GetDeviceInfo(index, out deviceInfo))
        {
            error = Errors.OK;
            return true;
        }

        error = Bass.LastError;
        return false;
    }

    /// <inheritdoc />
    public bool InitializeCore(int deviceIndex, int rate, DeviceInitFlags flags) =>
        Bass.Init(deviceIndex, rate, flags, IntPtr.Zero, IntPtr.Zero);

    /// <inheritdoc />
    public int GetCoreDevice() => Bass.CurrentDevice;

    /// <inheritdoc />
    public bool TryGetDeviceInfo(
        int deviceIndex,
        out BassDirectSoundDeviceSnapshot deviceInfo,
        out Errors error)
    {
        deviceInfo = default;
        error = Errors.Unknown;
        try
        {
            if (!Bass.GetDeviceInfo(deviceIndex, out DeviceInfo info))
            {
                error = Bass.LastError;
                return false;
            }

            deviceInfo = new BassDirectSoundDeviceSnapshot(info.Name, info.Driver);
            error = Errors.OK;
            return true;
        }
        catch (BassException exception)
        {
            error = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetInfo(
        out BassDirectSoundInfoSnapshot infoSnapshot,
        out Errors error)
    {
        infoSnapshot = default;
        error = Errors.Unknown;
        try
        {
            if (!Bass.GetInfo(out BassInfo info))
            {
                error = Bass.LastError;
                return false;
            }

            infoSnapshot = new BassDirectSoundInfoSnapshot(info.SampleRate);
            error = Errors.OK;
            return true;
        }
        catch (BassException exception)
        {
            error = exception.ErrorCode;
            return false;
        }
    }

    /// <inheritdoc />
    public bool SetConfig(Configuration option, int value) => Bass.Configure(option, value);

    /// <inheritdoc />
    public int GetConfig(Configuration option) => Bass.GetConfig(option);

    /// <inheritdoc />
    public int CreateMixer(int rate, int channels, BassFlags flags) =>
        BassMix.CreateMixerStream(rate, channels, flags);

    /// <inheritdoc />
    public int CreateOutputStream(int rate, int channels, BassFlags flags, StreamProcedure callback) =>
        Bass.CreateStream(rate, channels, flags, callback, IntPtr.Zero);

    /// <inheritdoc />
    public bool Play(int streamHandle) => Bass.ChannelPlay(streamHandle, false);

    /// <inheritdoc />
    public int CreateVolumeEffect(int mixerHandle) =>
        Bass.ChannelSetFX(mixerHandle, EffectType.VolumeBfx, 1);

    /// <inheritdoc />
    public bool SetVolumeEffect(int effectHandle, float volume) =>
        ManagedBassVolumeEffect.SetParameters(effectHandle, volume);

    /// <inheritdoc />
    public Errors GetCoreError() => Bass.LastError;
}
