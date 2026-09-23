using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.Asio;
using ManagedBass.Wasapi;
using Ribbit.Media;

namespace Ribbit.Media.Audio;

/// <summary>Describes one native audio endpoint without conflating it with the Default placeholder.</summary>
internal readonly struct BassAudioEnumeratedDevice
{
    /// <summary>Creates one native endpoint descriptor.</summary>
    internal BassAudioEnumeratedDevice(string name, string identity, int nativeIndex, bool isDefault)
    {
        Name = name;
        Identity = identity;
        NativeIndex = nativeIndex;
        IsDefault = isDefault;
    }

    /// <summary>Gets the display name reported by the native API.</summary>
    internal string Name { get; }

    /// <summary>Gets the backend-specific stable identity.</summary>
    internal string Identity { get; }

    /// <summary>Gets the index used by the native API.</summary>
    internal int NativeIndex { get; }

    /// <summary>Gets whether the native API marks this endpoint as its current default.</summary>
    internal bool IsDefault { get; }
}

/// <summary>Enumerates one backend at a time so failures remain isolated.</summary>
internal interface IBassAudioDeviceEnumerator
{
    /// <summary>Enumerates selectable native endpoints for an audible backend.</summary>
    IReadOnlyList<BassAudioEnumeratedDevice> Enumerate(BassAudioPlayer.DeviceDriver backend);
}

/// <summary>Reads one native audio device descriptor during index-based enumeration.</summary>
internal delegate bool BassAudioDeviceInfoReader<T>(
    int index,
    out T deviceInfo,
    out Errors error);

/// <summary>Applies the common BASS device-list termination and failure contract.</summary>
internal static class BassAudioDeviceEnumeration
{
    /// <summary>
    /// Reads consecutive native indices until the expected <see cref="Errors.Device"/> end
    /// marker. Any other failure returns no partial result and preserves its error code.
    /// </summary>
    internal static bool TryEnumerate<T>(
        BassAudioDeviceInfoReader<T> reader,
        out T[] devices,
        out Errors error)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var result = new List<T>();
        for (int index = 0; ; index++)
        {
            try
            {
                if (!reader(index, out T deviceInfo, out Errors nativeError))
                {
                    if (nativeError == Errors.Device)
                    {
                        devices = [.. result];
                        error = Errors.OK;
                        return true;
                    }

                    devices = [];
                    error = nativeError;
                    return false;
                }

                result.Add(deviceInfo);
            }
            catch (BassException exception)
            {
                devices = [];
                error = exception.ErrorCode;
                return false;
            }
        }
    }
}

/// <summary>Reports a non-terminal native failure while enumerating one audio backend.</summary>
internal sealed class BassAudioDeviceEnumerationException : InvalidOperationException
{
    /// <summary>Creates an empty enumeration exception.</summary>
    internal BassAudioDeviceEnumerationException()
    {
    }

    /// <summary>Creates an enumeration exception with a diagnostic message.</summary>
    internal BassAudioDeviceEnumerationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an enumeration exception with a diagnostic message and cause.</summary>
    internal BassAudioDeviceEnumerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates an exception that retains the failed native operation and error code.</summary>
    internal BassAudioDeviceEnumerationException(
        BassAudioPlayer.DeviceDriver backend,
        string stage,
        string nativeErrorSource,
        Errors nativeErrorCode,
        Exception innerException = null)
        : base(
            "Audio device enumeration failed: backend=" + backend
            + " stage=" + stage
            + " nativeErrorSource=" + nativeErrorSource
            + " nativeErrorCode=" + BassNativeErrorFormatter.Format(nativeErrorCode),
            innerException)
    {
        Backend = backend;
        Stage = stage;
        NativeErrorSource = nativeErrorSource;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>Gets the backend whose native device list could not be enumerated.</summary>
    internal BassAudioPlayer.DeviceDriver Backend { get; }

    /// <summary>Gets the failed native operation.</summary>
    internal string Stage { get; }

    /// <summary>Gets the native component that reported the failure.</summary>
    internal string NativeErrorSource { get; }

    /// <summary>Gets the native error code captured immediately after the failed call.</summary>
    internal Errors NativeErrorCode { get; }
}

/// <summary>Uses the ManagedBass family to enumerate endpoints without initializing an output device.</summary>
internal sealed class BassAudioDeviceEnumerator : IBassAudioDeviceEnumerator
{
    private static int enumerationInvocationCount;

    /// <summary>Gets how many backend enumerations were requested in this process.</summary>
    internal static int EnumerationInvocationCount => System.Threading.Volatile.Read(ref enumerationInvocationCount);

    /// <inheritdoc />
    public IReadOnlyList<BassAudioEnumeratedDevice> Enumerate(BassAudioPlayer.DeviceDriver backend)
    {
        System.Threading.Interlocked.Increment(ref enumerationInvocationCount);
        BassAudioRuntime.Initialize();
        using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        return backend switch
        {
            BassAudioPlayer.DeviceDriver.WASAPI_SHARED => EnumerateWasapi(backend),
            BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE => EnumerateWasapi(backend),
            BassAudioPlayer.DeviceDriver.ASIO => EnumerateAsio(backend),
            _ => throw new ArgumentOutOfRangeException(
                nameof(backend),
                backend,
                "A selectable WASAPI or ASIO backend is required.")
        };
    }

    private static IReadOnlyList<BassAudioEnumeratedDevice> EnumerateWasapi(
        BassAudioPlayer.DeviceDriver backend)
    {
        if (!BassAudioDeviceEnumeration.TryEnumerate(
                TryReadWasapiDeviceInfo,
                out WasapiDeviceInfo[] nativeDevices,
                out Errors error))
        {
            throw new BassAudioDeviceEnumerationException(
                backend,
                "BASS_WASAPI_GetDeviceInfo",
                "BASSWASAPI",
                error);
        }

        var result = new List<BassAudioEnumeratedDevice>();
        for (int index = 0; index < nativeDevices.Length; index++)
        {
            WasapiDeviceInfo device = nativeDevices[index];
            if (device.IsUnplugged
                || device.IsLoopback
                || !device.IsEnabled
                || device.IsInput
                || string.IsNullOrWhiteSpace(device.ID))
            {
                continue;
            }
            result.Add(new BassAudioEnumeratedDevice(device.Name, device.ID, index, device.IsDefault));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<BassAudioEnumeratedDevice> EnumerateAsio(
        BassAudioPlayer.DeviceDriver backend)
    {
        if (!BassAudioDeviceEnumeration.TryEnumerate(
                TryReadAsioDeviceInfo,
                out AsioDeviceInfo[] nativeDevices,
                out Errors error))
        {
            throw new BassAudioDeviceEnumerationException(
                backend,
                "BASS_ASIO_GetDeviceInfo",
                "BASSASIO",
                error);
        }

        var result = new List<BassAudioEnumeratedDevice>();
        for (int index = 0; index < nativeDevices.Length; index++)
        {
            AsioDeviceInfo device = nativeDevices[index];
            if (string.IsNullOrWhiteSpace(device.Driver))
            {
                continue;
            }
            result.Add(new BassAudioEnumeratedDevice(device.Name, device.Driver, index, isDefault: false));
        }
        return result.AsReadOnly();
    }

    private static bool TryReadWasapiDeviceInfo(
        int index,
        out WasapiDeviceInfo deviceInfo,
        out Errors error)
    {
        if (BassWasapi.GetDeviceInfo(index, out deviceInfo))
        {
            error = Errors.OK;
            return true;
        }

        error = Bass.LastError;
        return false;
    }

    private static bool TryReadAsioDeviceInfo(
        int index,
        out AsioDeviceInfo deviceInfo,
        out Errors error)
    {
        if (BassAsio.GetDeviceInfo(index, out deviceInfo))
        {
            error = Errors.OK;
            return true;
        }

        error = BassAsio.LastError;
        return false;
    }
}
