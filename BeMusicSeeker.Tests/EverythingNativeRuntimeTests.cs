using System;
using System.IO;
using System.Security.Cryptography;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class EverythingNativeRuntimeTests
{
    [TestMethod]
    public void MissingBridgeIsReportedWithoutUsingCurrentDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(EverythingNativeRuntimeTests), Guid.NewGuid().ToString("N"));
        var snapshot = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe"));
        var native = new EverythingNative(snapshot);

        try
        {
            ChartScanExecutionResult result = native.ExecuteScan("chart", "audio", "image", "movie");

            Assert.IsFalse(result.Success);
            StringAssert.StartsWith(result.NativeBridgeReason, "bridge_dll_not_found:");
            StringAssert.Contains(result.NativeBridgeReason, Path.Combine(snapshot.BaseDirectory, "native", "EverythingBridge_x64.dll"));
        }
        finally
        {
            native.Dispose();
        }
    }

    [TestMethod]
    public void TrackedEverythingNativeAssetsAreX64AndHashPinned()
    {
        string repositoryRoot = FindRepositoryRoot();
        AssertX64AndHash(
            Path.Combine(repositoryRoot, "native", "Everything3_x64.dll"),
            "BE25B01C73BBF359B50DDF30255133225F93B4BC40A8D208173319373BCDAA5C");
        AssertX64AndHash(
            Path.Combine(repositoryRoot, "native", "EverythingBridge_x64.dll"),
            "24863BFD06BFDFE0419DF767152575ADCD38B4104DC2063ED411AA7F956AA835");
    }

    private static void AssertX64AndHash(string path, string expectedHash)
    {
        byte[] image = File.ReadAllBytes(path);
        Assert.IsTrue(image.Length > 0x40, $"Native asset is too small to be a PE image: {path}");
        Assert.AreEqual((byte)'M', image[0], $"Native asset is not an MZ image: {path}");
        Assert.AreEqual((byte)'Z', image[1], $"Native asset is not an MZ image: {path}");
        int peOffset = BitConverter.ToInt32(image, 0x3c);
        Assert.IsTrue(peOffset >= 0 && peOffset + 6 <= image.Length, $"PE header is outside the native asset: {path}");
        Assert.AreEqual((byte)'P', image[peOffset], $"Native asset is not a PE image: {path}");
        Assert.AreEqual((byte)'E', image[peOffset + 1], $"Native asset is not a PE image: {path}");
        Assert.AreEqual((ushort)0x8664, BitConverter.ToUInt16(image, peOffset + 4), $"Native asset must be x64: {path}");
        Assert.AreEqual(expectedHash, Convert.ToHexString(SHA256.HashData(image)), $"Native asset hash changed: {path}");
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker.sln")))
            {
                return directoryPath!;
            }

            directoryPath = Directory.GetParent(directoryPath)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

}
