using BeMusicSeeker.Models.Utils;
using static BeMusicSeeker.TestSupport.Components.ResilientFileMutationServiceTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// ResilientFileMutationService の ReadOnly 補正とエラー集約の基本契約を検証します。
/// </summary>
[TestClass]
[TestCategory("Suite.CrossVolume")]
public sealed class ResilientFileMutationServiceTests
{
    private readonly ResilientFileMutationService resilientFileMutationService = new();

    /// <summary>別volumeへの上書き移動で新内容をコピーして元を消し、renameだけの実装による移動失敗や旧内容残留を検出する。</summary>
    /// <remarks>実際に異なるvolumeを用いる。明示した二つのfixture基点のdrive rootと実volumeを確認し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void MoveFile_CrossVolumeOverwriteCopiesAndDeletesSource()
    {
        CrossVolumeTestDirectories.Run(directories =>
        {
            string sourceFilePath = Path.Combine(directories.SourceBaseDirectory, "source.bms");
            string destinationFilePath = Path.Combine(directories.DestinationBaseDirectory, "destination.bms");
            WriteAllText(sourceFilePath, "source");
            WriteAllText(destinationFilePath, "destination");

            resilientFileMutationService.MoveFile(sourceFilePath, destinationFilePath, overwrite: true, targetOnlyFileMutationOptions);

            Assert.IsFalse(LongPathFileSystem.FileExists(sourceFilePath));
            Assert.IsTrue(LongPathFileSystem.FileExists(destinationFilePath));
            Assert.AreEqual("source", ReadAllText(destinationFilePath));
        });
    }

    /// <summary>別volumeの未作成長パス宛先へroot・子の内容を移して元を消し、ディレクトリ作成やコピー後回収の漏れを検出する。</summary>
    /// <remarks>実際に異なるvolumeを用いる。明示した二つのfixture基点のdrive rootと実volumeを確認し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void MoveDirectory_CrossVolumeCreatesLongDestinationAndDeletesSource()
    {
        CrossVolumeTestDirectories.Run(directories =>
        {
            string sourceDirectoryPath = Path.Combine(directories.SourceBaseDirectory, "source");
            string sourceChildDirectoryPath = Path.Combine(sourceDirectoryPath, "child");
            string destinationDirectoryPath = BuildLongDirectoryPath(directories.DestinationBaseDirectory, "destination");
            LongPathFileSystem.CreateDirectory(sourceChildDirectoryPath);
            WriteAllText(Path.Combine(sourceDirectoryPath, "root.bms"), "root");
            WriteAllText(Path.Combine(sourceChildDirectoryPath, "nested.wav"), "nested");

            resilientFileMutationService.MoveDirectory(sourceDirectoryPath, destinationDirectoryPath, overwrite: false, recursiveDirectoryTreeFileMutationOptions);

            Assert.IsFalse(LongPathFileSystem.DirectoryExists(sourceDirectoryPath));
            Assert.AreEqual("root", ReadAllText(Path.Combine(destinationDirectoryPath, "root.bms")));
            Assert.AreEqual("nested", ReadAllText(Path.Combine(destinationDirectoryPath, "child", "nested.wav")));
        });
    }

    /// <summary>別volumeの既存宛先をマージして衝突内容を置換し、双方だけのファイル・新しい子を保持して全体置換や子の脱落を検出する。</summary>
    /// <remarks>実際に異なるvolumeを用いる。明示した二つのfixture基点のdrive rootと実volumeを確認し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void MoveDirectory_CrossVolumeOverwritesExistingDestinationByMerging()
    {
        CrossVolumeTestDirectories.Run(directories =>
        {
            string sourceDirectoryPath = Path.Combine(directories.SourceBaseDirectory, "source");
            string destinationDirectoryPath = Path.Combine(directories.DestinationBaseDirectory, "destination");
            string sourceChildDirectoryPath = Path.Combine(sourceDirectoryPath, "child");
            string sourceNewChildDirectoryPath = Path.Combine(sourceDirectoryPath, "new-child");
            string destinationChildDirectoryPath = Path.Combine(destinationDirectoryPath, "child");
            LongPathFileSystem.CreateDirectory(sourceChildDirectoryPath);
            LongPathFileSystem.CreateDirectory(sourceNewChildDirectoryPath);
            LongPathFileSystem.CreateDirectory(destinationChildDirectoryPath);
            WriteAllText(Path.Combine(sourceDirectoryPath, "same.bms"), "source");
            WriteAllText(Path.Combine(sourceDirectoryPath, "source-only.bms"), "source-only");
            WriteAllText(Path.Combine(sourceChildDirectoryPath, "nested.bms"), "nested-source");
            WriteAllText(Path.Combine(sourceNewChildDirectoryPath, "new.bms"), "new-child");
            WriteAllText(Path.Combine(destinationDirectoryPath, "same.bms"), "destination");
            WriteAllText(Path.Combine(destinationDirectoryPath, "destination-only.bms"), "destination-only");
            WriteAllText(Path.Combine(destinationChildDirectoryPath, "old.bms"), "nested-destination");

            resilientFileMutationService.MoveDirectory(sourceDirectoryPath, destinationDirectoryPath, overwrite: true, recursiveDirectoryTreeFileMutationOptions);

            Assert.IsFalse(LongPathFileSystem.DirectoryExists(sourceDirectoryPath));
            Assert.AreEqual("source", ReadAllText(Path.Combine(destinationDirectoryPath, "same.bms")));
            Assert.AreEqual("source-only", ReadAllText(Path.Combine(destinationDirectoryPath, "source-only.bms")));
            Assert.AreEqual("destination-only", ReadAllText(Path.Combine(destinationDirectoryPath, "destination-only.bms")));
            Assert.AreEqual("nested-source", ReadAllText(Path.Combine(destinationChildDirectoryPath, "nested.bms")));
            Assert.AreEqual("nested-destination", ReadAllText(Path.Combine(destinationChildDirectoryPath, "old.bms")));
            Assert.AreEqual("new-child", ReadAllText(Path.Combine(destinationDirectoryPath, "new-child", "new.bms")));
        });
    }

    /// <summary>別volumeの既存宛先で上書き禁止を守って双方の内容を保持し、失敗後の部分移動や誤上書きを検出する。</summary>
    /// <remarks>実際に異なるvolumeを用いる。明示した二つのfixture基点のdrive rootと実volumeを確認し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void MoveDirectory_CrossVolumeOverwriteFalseExistingDestinationThrowsAndPreservesBothSides()
    {
        CrossVolumeTestDirectories.Run(directories =>
        {
            string sourceDirectoryPath = Path.Combine(directories.SourceBaseDirectory, "source");
            string destinationDirectoryPath = Path.Combine(directories.DestinationBaseDirectory, "destination");
            LongPathFileSystem.CreateDirectory(sourceDirectoryPath);
            LongPathFileSystem.CreateDirectory(destinationDirectoryPath);
            WriteAllText(Path.Combine(sourceDirectoryPath, "source.bms"), "source");
            WriteAllText(Path.Combine(destinationDirectoryPath, "destination.bms"), "destination");

            Assert.ThrowsExactly<FileMutationException>(() =>
                resilientFileMutationService.MoveDirectory(sourceDirectoryPath, destinationDirectoryPath, overwrite: false, recursiveDirectoryTreeFileMutationOptions));

            Assert.IsTrue(LongPathFileSystem.DirectoryExists(sourceDirectoryPath));
            Assert.IsTrue(LongPathFileSystem.DirectoryExists(destinationDirectoryPath));
            Assert.AreEqual("source", ReadAllText(Path.Combine(sourceDirectoryPath, "source.bms")));
            Assert.AreEqual("destination", ReadAllText(Path.Combine(destinationDirectoryPath, "destination.bms")));
        });
    }

}
