using System.Collections.Generic;
using ManagedBass;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassAudioDeviceEnumerationTests
{
    /// <summary>
    /// 保証：BASSのDeviceエラーを列挙終端として成功扱いし、それまでに取得した機器だけを番号順に公開する。
    /// </summary>
    [TestMethod]
    public void DeviceErrorIsTheNormalEndAndPublishesOnlyCollectedDescriptors()
    {
        var requestedIndices = new List<int>();

        bool ReadDevice(int index, out int device, out Errors error)
        {
            requestedIndices.Add(index);
            if (index < 2)
            {
                device = index + 10;
                error = Errors.OK;
                return true;
            }

            device = 0;
            error = Errors.Device;
            return false;
        }

        bool succeeded = BassAudioDeviceEnumeration.TryEnumerate(
            ReadDevice,
            out int[] devices,
            out Errors errorCode);

        Assert.IsTrue(succeeded);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, requestedIndices);
        CollectionAssert.AreEqual(new[] { 10, 11 }, devices);
        Assert.AreEqual(Errors.OK, errorCode);
    }

    /// <summary>
    /// 保証：列挙途中のInitエラーでは部分的な機器を公開せず、失敗と元のnativeエラーを返す。
    /// </summary>
    [TestMethod]
    public void NonTerminalErrorFailsWithoutPublishingPartialDescriptors()
    {
        bool ReadDevice(int index, out int device, out Errors error)
        {
            device = index;
            error = index == 0 ? Errors.OK : Errors.Init;
            return index == 0;
        }

        bool succeeded = BassAudioDeviceEnumeration.TryEnumerate(
            ReadDevice,
            out int[] devices,
            out Errors errorCode);

        Assert.IsFalse(succeeded);
        Assert.AreEqual(0, devices.Length);
        Assert.AreEqual(Errors.Init, errorCode);
    }

    /// <summary>
    /// 保証：機器readerが投げたBASS例外は通常終端へ変換せず、例外のnativeエラーを保持した失敗として返す。
    /// </summary>
    [TestMethod]
    public void ReaderBassExceptionUsesItsNativeErrorCode()
    {
        bool ReadDevice(int index, out int device, out Errors error)
        {
            device = 0;
            error = Errors.Unknown;
            throw new BassException(Errors.Device);
        }

        bool succeeded = BassAudioDeviceEnumeration.TryEnumerate(
            ReadDevice,
            out int[] devices,
            out Errors errorCode);

        Assert.IsFalse(succeeded);
        Assert.AreEqual(0, devices.Length);
        Assert.AreEqual(Errors.Device, errorCode);
    }
}
