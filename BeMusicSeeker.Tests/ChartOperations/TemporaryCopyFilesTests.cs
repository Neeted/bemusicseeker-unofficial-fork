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
    /// <remarks>実FSのコピー・file lock・借用先の残存を観測し、素材の所有権に応じた回収を確認します。</remarks>
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
            var copies = new temporarilyCopyFiles();
            using (copies)
            {
                copies.CopyFiles(relatives.Select(relative => Path.Combine(source, relative)), source, destination);
                Assert.AreEqual("borrowed/kick.wav", File.ReadAllText(Path.Combine(destination, "borrowed/kick.wav")));
                Assert.AreEqual("new/audio/kick.wav", File.ReadAllText(Path.Combine(destination, "new/audio/kick.wav")));
                Assert.AreEqual("empty/chart.bms", File.ReadAllText(Path.Combine(destination, "empty/chart.bms")));
                Assert.AreEqual("existing", File.ReadAllText(Path.Combine(destination, "keep/banner.png")));
                File.WriteAllText(Path.Combine(destination, "new/third-party.txt"), "unowned");
            }
            Assert.IsFalse(copies.HasCleanupFailure, "非所有物を残すことは回収失敗ではありません。");
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
    /// <remarks>実FSのコピー・file lock・借用先の残存を観測し、素材の所有権に応じた回収を確認します。</remarks>
    [TestMethod]
    public void CopyFailure_JoinsCopiesAndRemovesOnlyOwnedDestinations()
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
                var copies = new temporarilyCopyFiles();
                Assert.ThrowsException<AggregateException>(() =>
                {
                    using (copies) { copies.CopyFiles(files, source, destination); }
                });
                Assert.IsFalse(copies.HasCleanupFailure, "コピーの失敗と回収の成否を区別します。");
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(destination, "new")));
            CollectionAssert.AreEqual(new[] { "existing.txt" }, Directory.GetFiles(destination).Select(Path.GetFileName).ToArray());
            foreach (string file in files) { Assert.AreEqual("input", File.ReadAllText(file)); }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    /// <summary>実FSへの256件の並列コピーが全件追跡され、回収後も入力元を保持することを確認します。</summary>
    /// <remarks>実FSのコピー・file lock・借用先の残存を観測し、素材の所有権に応じた回収を確認します。</remarks>
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

            var copies = new temporarilyCopyFiles();
            using (copies)
            {
                copies.CopyFiles(sourceFiles, sourceDirectory, destinationDirectory);
                Assert.IsTrue(sourceFiles.All(source => File.Exists(Path.Combine(destinationDirectory, Path.GetFileName(source)))));
            }

            Assert.IsFalse(copies.HasCleanupFailure);
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

    /// <summary>
    /// 保証：Windowsの削除共有を許可しないowned fileだけを残し、他のowned fileと空ディレクトリを最後まで回収します。
    /// 借用・既存・第三者の素材を保ち、解放後の再Disposeでも残留物を再試行しません。
    /// </summary>
    /// <remarks>実WindowsのFileShare.Readで削除失敗を作り、画面基盤を用いずDisposeの終端と実FSの残存を観測します。Linuxの共有規則では代用しません。</remarks>
    [TestMethod]
    public void Dispose_DeleteShareDeniedLeavesOnlyOwnedResidueAndDoesNotRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windowsの削除共有規則を使う実FSケースです。");
        }

        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_TemporaryDeleteShare_" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        string[] relatives = ["borrowed/kick.wav", "held/audio/held.wav", "clean/audio/free.wav", "keep/banner.png", "thirdparty/copied.wav"];
        try
        {
            Directory.CreateDirectory(Path.Combine(destination, "borrowed"));
            Directory.CreateDirectory(Path.Combine(destination, "keep"));
            File.WriteAllText(Path.Combine(destination, "keep/banner.png"), "existing");
            foreach (string relative in relatives)
            {
                string path = Path.Combine(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, relative);
            }

            using var copies = new temporarilyCopyFiles();
            copies.CopyFiles(relatives.Select(relative => Path.Combine(source, relative)), source, destination);
            string heldPath = Path.Combine(destination, "held/audio/held.wav");
            File.WriteAllText(Path.Combine(destination, "thirdparty/added.txt"), "unowned");
            using (var held = new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                copies.Dispose();

                Assert.IsTrue(copies.HasCleanupFailure);
                Assert.IsTrue(File.Exists(heldPath), "削除共有を許可しない所有ファイルは残留します。");
                Assert.IsTrue(Directory.Exists(Path.Combine(destination, "held/audio")));
                Assert.IsFalse(File.Exists(Path.Combine(destination, "clean/audio/free.wav")));
                Assert.IsFalse(Directory.Exists(Path.Combine(destination, "clean/audio")));
                Assert.IsFalse(Directory.Exists(Path.Combine(destination, "clean")), "空の所有親ディレクトリも子の後に回収します。");
                Assert.IsFalse(File.Exists(Path.Combine(destination, "borrowed/kick.wav")));
                Assert.IsTrue(Directory.Exists(Path.Combine(destination, "borrowed")), "借用した空ディレクトリは残します。");
                Assert.IsFalse(File.Exists(Path.Combine(destination, "thirdparty/copied.wav")));
                Assert.AreEqual("unowned", File.ReadAllText(Path.Combine(destination, "thirdparty/added.txt")));
                Assert.AreEqual("existing", File.ReadAllText(Path.Combine(destination, "keep/banner.png")));
                copies.Dispose();
            }

            copies.Dispose();
            Assert.IsTrue(File.Exists(heldPath), "再Disposeで回収を再試行しません。");
            Assert.IsTrue(copies.HasCleanupFailure, "再Disposeで残留結果を成功へ変えません。");
            foreach (string relative in relatives) { Assert.AreEqual(relative, File.ReadAllText(Path.Combine(source, relative))); }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }
}
