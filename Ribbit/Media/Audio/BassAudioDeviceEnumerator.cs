using System;
using System.Collections.Generic;
using Ribbit.Media;
using Un4seen.BassAsio;
using Un4seen.BassWasapi;

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

/// <summary>Uses the bundled BASS family to enumerate endpoints without initializing an output device.</summary>
internal sealed class BassAudioDeviceEnumerator : IBassAudioDeviceEnumerator
{
    private static int enumerationInvocationCount;

    /// <summary>Gets how many backend enumerations were requested in this process.</summary>
    internal static int EnumerationInvocationCount => System.Threading.Volatile.Read(ref enumerationInvocationCount);

    /// <inheritdoc />
    public IReadOnlyList<BassAudioEnumeratedDevice> Enumerate(BassAudioPlayer.DeviceDriver backend)
    {
        System.Threading.Interlocked.Increment(ref enumerationInvocationCount);
        BassNet.Initialize();
        using BassAudioOperationLease operation = BassNet.EnterAudioOperation();
        return backend switch
        {
            BassAudioPlayer.DeviceDriver.WASAPI_SHARED => EnumerateWasapi(),
            BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE => EnumerateWasapi(),
            BassAudioPlayer.DeviceDriver.ASIO => EnumerateAsio(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(backend),
                backend,
                "A selectable WASAPI or ASIO backend is required.")
        };
    }

    private static IReadOnlyList<BassAudioEnumeratedDevice> EnumerateWasapi()
    {
        BASS_WASAPI_DEVICEINFO[] devices = BassWasapi.BASS_WASAPI_GetDeviceInfos() ?? [];
        var result = new List<BassAudioEnumeratedDevice>();
        for (int index = 0; index < devices.Length; index++)
        {
            BASS_WASAPI_DEVICEINFO device = devices[index];
            if (device == null
                || device.IsUnplugged
                || device.IsLoopback
                || !device.IsEnabled
                || device.IsInput
                || string.IsNullOrWhiteSpace(device.id))
            {
                continue;
            }
            result.Add(new BassAudioEnumeratedDevice(device.name, device.id, index, device.IsDefault));
        }
        return result.AsReadOnly();
    }

    private static IReadOnlyList<BassAudioEnumeratedDevice> EnumerateAsio()
    {
        BASS_ASIO_DEVICEINFO[] devices = BassAsio.BASS_ASIO_GetDeviceInfos() ?? [];
        var result = new List<BassAudioEnumeratedDevice>();
        for (int index = 0; index < devices.Length; index++)
        {
            BASS_ASIO_DEVICEINFO device = devices[index];
            if (device == null || string.IsNullOrWhiteSpace(device.driver))
            {
                continue;
            }
            result.Add(new BassAudioEnumeratedDevice(device.name, device.driver, index, isDefault: false));
        }
        return result.AsReadOnly();
    }
}
