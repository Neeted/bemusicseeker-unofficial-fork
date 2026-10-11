using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TemporaryCopyFilesTests
{
    /// <summary>相対配置と同名別ディレクトリを実FSで確認し、既存素材・借用ディレクトリ・追加された非所有ファイルを保全します。所有した空ディレクトリだけを子から回収します。</summary>
    [TestMethod]
    public void Dispose_PreservesRelativeStructureExistingMaterialsAndUnownedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_TemporaryOwnership_" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        string[] relatives = ["borrowed/kick.wav", "new/audio/kick.wav", "empty/chart.bms", "keep/banner.png"];
        try
        {
            Directory.CreateDirectory(destination);
            foreach (string relative in relatives)
            {
                string path = Path.Combine(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, relative);
            }
            Directory.CreateDirectory(Path.Combine(destination, "borrowed"));
            Directory.CreateDirectory(Path.Combine(destination, "keep"));
            File.WriteAllText(Path.Combine(destination, "keep/banner.png"), "existing");
            using (new temporarilyCopyFiles(relatives.Select(relative => Path.Combine(source, relative)), source, destination))
            {
                Assert.AreEqual("borrowed/kick.wav", File.ReadAllText(Path.Combine(destination, "borrowed/kick.wav")));
                Assert.AreEqual("new/audio/kick.wav", File.ReadAllText(Path.Combine(destination, "new/audio/kick.wav")));
                Assert.AreEqual("empty/chart.bms", File.ReadAllText(Path.Combine(destination, "empty/chart.bms")));
                Assert.AreEqual("existing", File.ReadAllText(Path.Combine(destination, "keep/banner.png")));
                File.WriteAllText(Path.Combine(destination, "new/third-party.txt"), "unowned");
            }
            Assert.IsTrue(Directory.Exists(Path.Combine(destination, "borrowed")), "借用した空ディレクトリを削除しません。");
            Assert.IsFalse(Directory.Exists(Path.Combine(destination, "new/audio")));
            Assert.IsFalse(Directory.Exists(Path.Combine(destination, "empty")));
            Assert.IsFalse(File.Exists(Path.Combine(destination, "borrowed/kick.wav")));
            Assert.AreEqual("unowned", File.ReadAllText(Path.Combine(destination, "new/third-party.txt")));
            Assert.AreEqual("existing", File.ReadAllText(Path.Combine(destination, "keep/banner.png")));
            foreach (string relative in relatives) { Assert.AreEqual(relative, File.ReadAllText(Path.Combine(source, relative))); }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    /// <summary>並列コピー中の実読取り失敗が開始済みコピーを合流してから所有ファイル・ディレクトリを回収し、借用素材と元入力を残すことを確認します。</summary>
    [TestMethod]
    public void ConstructorFailure_JoinsCopiesAndRemovesOnlyOwnedDestinations()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_TemporaryFailure_" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "new"));
            Directory.CreateDirectory(destination);
            string[] files = Enumerable.Range(0, 32).Select(index => Path.Combine(source, "new", index + ".wav")).ToArray();
            foreach (string file in files) { File.WriteAllText(file, "input"); }
            File.WriteAllText(Path.Combine(destination, "existing.txt"), "existing");
            using (var held = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsException<AggregateException>(() => new temporarilyCopyFiles(files, source, destination));
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(destination, "new")));
            CollectionAssert.AreEqual(new[] { "existing.txt" }, Directory.GetFiles(destination).Select(Path.GetFileName).ToArray());
            foreach (string file in files) { Assert.AreEqual("input", File.ReadAllText(file)); }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    /// <summary>実FSへの256件の並列コピーが全件追跡され、回収後も入力元を保持することを確認します。</summary>
    [TestMethod]
    public void Dispose_RemovesEveryFileTrackedDuringParallelCopy()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_TemporaryCopy_" + Guid.NewGuid().ToString("N"));
        string sourceDirectory = Path.Combine(root, "Source");
        string destinationDirectory = Path.Combine(root, "Destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        string[] sourceFiles = Enumerable.Range(0, 256)
            .Select(index => Path.Combine(sourceDirectory, index.ToString("D4") + ".dat"))
            .ToArray();
        try
        {
            foreach (string sourceFile in sourceFiles)
            {
                File.WriteAllText(sourceFile, "test");
            }

            using (new temporarilyCopyFiles(sourceFiles, sourceDirectory, destinationDirectory))
            {
                Assert.IsTrue(sourceFiles.All(source => File.Exists(Path.Combine(destinationDirectory, Path.GetFileName(source)))));
            }

            Assert.IsTrue(sourceFiles.All(source => !File.Exists(Path.Combine(destinationDirectory, Path.GetFileName(source)))));
            Assert.IsTrue(sourceFiles.All(File.Exists));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
