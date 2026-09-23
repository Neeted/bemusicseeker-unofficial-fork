using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class AudioDeviceCatalogTests
{
    [TestMethod]
    public void Refresh_ReflectsConnectedAndDisconnectedFakeDevices()
    {
        var enumerator = new FakeDeviceEnumerator();
        enumerator.Results[BassAudioPlayer.DeviceDriver.WASAPI_SHARED] =
        [new("Speakers", "speaker-1", 3, true)];
        var catalog = new BassAudioDeviceCatalog(enumerator);

        catalog.Refresh();
        IReadOnlyList<AudioDeviceInfo> first = catalog.GetDevices(AudioDriver.WasapiShared);

        Assert.AreEqual(2, first.Count);
        Assert.IsTrue(first[0].IsDefaultPlaceholder);
        Assert.AreEqual("speaker-1", first[1].Driver);
        Assert.AreEqual(3, first[1].NativeIndex);

        enumerator.Results[BassAudioPlayer.DeviceDriver.WASAPI_SHARED] =
        [new("Headphones", "headphones-2", 5, false)];
        catalog.Refresh();
        IReadOnlyList<AudioDeviceInfo> second = catalog.GetDevices(AudioDriver.WasapiShared);

        Assert.IsFalse(second.Any(device => device.Driver == "speaker-1"));
        Assert.AreEqual("headphones-2", second[1].Driver);
        Assert.IsFalse(enumerator.RequestedBackends.Contains(BassAudioPlayer.DeviceDriver.DIRECT_SOUND));
        Assert.AreEqual(0, catalog.GetDevices(AudioDriver.DirectSound).Count);
    }

    [TestMethod]
    public void Refresh_BackendFailureRetainsOnlyThatBackendsLastGoodList()
    {
        var enumerator = new FakeDeviceEnumerator();
        enumerator.Results[BassAudioPlayer.DeviceDriver.WASAPI_SHARED] =
        [new("Speakers", "speaker", 1, false)];
        enumerator.Results[BassAudioPlayer.DeviceDriver.ASIO] =
        [new("ASIO A", "asio-a", 0, false)];
        var catalog = new BassAudioDeviceCatalog(enumerator);
        catalog.Refresh();

        enumerator.Failures.Add(BassAudioPlayer.DeviceDriver.ASIO);
        enumerator.Results[BassAudioPlayer.DeviceDriver.WASAPI_SHARED] =
        [new("Headphones", "headphones", 2, true)];
        catalog.Refresh();

        Assert.AreEqual("headphones", catalog.GetDevices(AudioDriver.WasapiShared)[1].Driver);
        Assert.AreEqual("asio-a", catalog.GetDevices(AudioDriver.Asio)[1].Driver);
    }

    private sealed class FakeDeviceEnumerator : IBassAudioDeviceEnumerator
    {
        internal Dictionary<BassAudioPlayer.DeviceDriver, IReadOnlyList<BassAudioEnumeratedDevice>> Results { get; } = [];

        internal HashSet<BassAudioPlayer.DeviceDriver> Failures { get; } = [];

        internal List<BassAudioPlayer.DeviceDriver> RequestedBackends { get; } = [];

        public IReadOnlyList<BassAudioEnumeratedDevice> Enumerate(BassAudioPlayer.DeviceDriver backend)
        {
            RequestedBackends.Add(backend);
            if (Failures.Contains(backend))
            {
                throw new InvalidOperationException("enumeration failed");
            }
            return Results.TryGetValue(backend, out IReadOnlyList<BassAudioEnumeratedDevice>? devices)
                ? devices!
                : [];
        }
    }
}
