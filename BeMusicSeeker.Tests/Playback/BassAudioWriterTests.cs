using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

/// <summary>音声writerの出力path契約を検証します。</summary>
[TestClass]
public sealed class BassAudioWriterTests
{
    [DataTestMethod]
    [DataRow(".mp3")]
    [DataRow(".wav")]
    public void OutputUsesTheExistingCollisionSuffixContract(string extension)
    {
        WithCollisionFixture(extension, path =>
        {
            Assert.AreEqual(
                path + " (2)" + extension,
                BassAudioWriter.GetAvailableOutputFile(path, extension));
        });
    }

    private static void WithCollisionFixture(string extension, Action<string> assertion)
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerWriterContracts",
            Guid.NewGuid().ToString("N"));
        string pathWithoutExtension = Path.Combine(directoryPath, "sample");
        try
        {
            Directory.CreateDirectory(directoryPath);
            File.WriteAllText(pathWithoutExtension + extension, string.Empty);
            assertion(pathWithoutExtension);
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
