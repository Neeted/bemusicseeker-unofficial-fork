using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// ResilientFileMutationService の ReadOnly 補正とエラー集約の基本契約を検証します。
/// </summary>
[TestClass]
public sealed class ResilientFileMutationServiceTests
{
    private static readonly FileMutationOptions targetOnlyFileMutationOptions = new(ReadOnlyNormalizationScope.TargetOnly);

    private static readonly FileMutationOptions recursiveDirectoryTreeFileMutationOptions = new(ReadOnlyNormalizationScope.RecursiveDirectoryTree);

    private readonly ResilientFileMutationService resilientFileMutationService = new();

    /// <summary>ReadOnlyの実ファイルを直接削除し、属性補正の欠落による回収失敗を検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void DeleteFileDirect_RemovesReadOnlyFile()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string filePath = Path.Combine(tempDirectoryPath, "readonly-delete-direct.txt");
            File.WriteAllText(filePath, "delete");
            SetReadOnly(filePath);

            resilientFileMutationService.DeleteFileDirect(filePath, targetOnlyFileMutationOptions);

            Assert.IsFalse(File.Exists(filePath));
        });
    }

    /// <summary>子ファイル・子ディレクトリ・rootがReadOnlyの実ツリーを再帰削除し、対象だけの属性補正で子を残す誤りを検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void DeleteDirectoryDirect_RemovesReadOnlyTree()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string targetDirectoryPath = Path.Combine(tempDirectoryPath, "readonly-tree");
            string childDirectoryPath = Path.Combine(targetDirectoryPath, "child");
            Directory.CreateDirectory(childDirectoryPath);
            string childFilePath = Path.Combine(childDirectoryPath, "song.bms");
            File.WriteAllText(childFilePath, "data");
            SetReadOnly(childFilePath);
            SetReadOnly(childDirectoryPath);
            SetReadOnly(targetDirectoryPath);

            resilientFileMutationService.DeleteDirectoryDirect(targetDirectoryPath, recursive: true, recursiveDirectoryTreeFileMutationOptions);

            Assert.IsFalse(Directory.Exists(targetDirectoryPath));
        });
    }

    /// <summary>ReadOnlyファイルをshell経由の永久削除で回収し、直接削除だけで成立する属性補正へ限定する誤りを検出する。</summary>
    /// <remarks>Windows shellの実削除経路を使い、成功時にdialogを出さない条件で物理回収を確認する。</remarks>
    [TestMethod]
    public void DeleteFileShell_RemovesReadOnlyFile()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string filePath = Path.Combine(tempDirectoryPath, "readonly-delete-shell.txt");
            File.WriteAllText(filePath, "delete");
            SetReadOnly(filePath);

            resilientFileMutationService.DeleteFileShell(filePath, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently, targetOnlyFileMutationOptions);

            Assert.IsFalse(File.Exists(filePath));
        });
    }

    /// <summary>ReadOnlyの既存宛先へ移動して新内容だけを残し、上書き失敗や移動元の残留を検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void MoveFile_OverwritesReadOnlyDestination()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceFilePath = Path.Combine(tempDirectoryPath, "source.txt");
            string destinationFilePath = Path.Combine(tempDirectoryPath, "destination.txt");
            File.WriteAllText(sourceFilePath, "new-content");
            File.WriteAllText(destinationFilePath, "old-content");
            SetReadOnly(destinationFilePath);

            resilientFileMutationService.MoveFile(sourceFilePath, destinationFilePath, overwrite: true, targetOnlyFileMutationOptions);

            Assert.IsFalse(File.Exists(sourceFilePath));
            Assert.IsTrue(File.Exists(destinationFilePath));
            Assert.AreEqual("new-content", File.ReadAllText(destinationFilePath));
        });
    }

    /// <summary>長パスのReadOnly宛先を新内容へ置換して移動元を消し、通常パスでは見えない属性補正・パス変換の退行を検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void MoveFile_OverwritesLongReadOnlyDestination()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string longDirectoryPath = BuildLongDirectoryPath(tempDirectoryPath, "move-file");
            LongPathFileSystem.CreateDirectory(longDirectoryPath);
            string sourceFilePath = Path.Combine(longDirectoryPath, "source.bms");
            string destinationFilePath = Path.Combine(longDirectoryPath, "destination.bms");
            WriteAllText(sourceFilePath, "new-content");
            WriteAllText(destinationFilePath, "old-content");
            SetReadOnly(destinationFilePath);

            resilientFileMutationService.MoveFile(sourceFilePath, destinationFilePath, overwrite: true, targetOnlyFileMutationOptions);

            Assert.IsFalse(LongPathFileSystem.FileExists(sourceFilePath));
            Assert.IsTrue(LongPathFileSystem.FileExists(destinationFilePath));
            Assert.AreEqual("new-content", ReadAllText(destinationFilePath));
        });
    }

    /// <summary>実排他で移動元を動かせない上書きでは双方を保持し、先に既存宛先を削って内容を失う誤りを検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void MoveFile_OverwritePreservesDestination_WhenSourceCannotMove()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceFilePath = Path.Combine(tempDirectoryPath, "locked-source.bms");
            string destinationFilePath = Path.Combine(tempDirectoryPath, "destination.bms");
            WriteAllText(sourceFilePath, "source");
            WriteAllText(destinationFilePath, "destination");

            using FileStream lockedFileStream = LongPathFileSystem.Open(sourceFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.ThrowsException<IOException>(() =>
                LongPathFileSystem.MoveFile(sourceFilePath, destinationFilePath, overwrite: true));

            Assert.IsTrue(LongPathFileSystem.FileExists(sourceFilePath));
            Assert.IsTrue(LongPathFileSystem.FileExists(destinationFilePath));
            Assert.AreEqual("destination", ReadAllText(destinationFilePath));
        });
    }

    /// <summary>別volumeへの上書き移動で新内容をコピーして元を消し、renameだけの実装による移動失敗や旧内容残留を検出する。</summary>
    /// <remarks>実際に異なるvolumeを用いる。明示した二つのfixture基点のdrive rootと実volumeを確認し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void MoveFile_CrossVolumeOverwriteCopiesAndDeletesSource()
    {
        using var directories = CrossVolumeTestDirectories.Create(nameof(MoveFile_CrossVolumeOverwriteCopiesAndDeletesSource));
        string sourceFilePath = Path.Combine(directories.SourceBaseDirectory, "source.bms");
        string destinationFilePath = Path.Combine(directories.DestinationBaseDirectory, "destination.bms");
        WriteAllText(sourceFilePath, "source");
        WriteAllText(destinationFilePath, "destination");

        resilientFileMutationService.MoveFile(sourceFilePath, destinationFilePath, overwrite: true, targetOnlyFileMutationOptions);

        Assert.IsFalse(LongPathFileSystem.FileExists(sourceFilePath));
        Assert.IsTrue(LongPathFileSystem.FileExists(destinationFilePath));
        Assert.AreEqual("source", ReadAllText(destinationFilePath));
    }

    /// <summary>長パスの既存ディレクトリをマージ移動し、衝突は置換して宛先だけのファイルと子を保持する保証の退行を検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void MoveDirectory_OverwritesLongDestinationByMerging()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string longRootPath = BuildLongDirectoryPath(tempDirectoryPath, "move-directory");
            string sourceDirectoryPath = Path.Combine(longRootPath, "source");
            string destinationDirectoryPath = Path.Combine(longRootPath, "destination");
            string sourceChildDirectoryPath = Path.Combine(sourceDirectoryPath, "child");
            string destinationChildDirectoryPath = Path.Combine(destinationDirectoryPath, "child");
            LongPathFileSystem.CreateDirectory(sourceChildDirectoryPath);
            LongPathFileSystem.CreateDirectory(destinationChildDirectoryPath);
            WriteAllText(Path.Combine(sourceDirectoryPath, "same.bms"), "source");
            WriteAllText(Path.Combine(sourceDirectoryPath, "source-only.bms"), "source-only");
            WriteAllText(Path.Combine(sourceChildDirectoryPath, "nested.bms"), "nested-source");
            string destinationSamePath = Path.Combine(destinationDirectoryPath, "same.bms");
            WriteAllText(destinationSamePath, "destination");
            WriteAllText(Path.Combine(destinationDirectoryPath, "destination-only.bms"), "destination-only");
            WriteAllText(Path.Combine(destinationChildDirectoryPath, "old.bms"), "nested-destination");
            SetReadOnly(destinationSamePath);

            resilientFileMutationService.MoveDirectory(sourceDirectoryPath, destinationDirectoryPath, overwrite: true, recursiveDirectoryTreeFileMutationOptions);

            Assert.IsFalse(LongPathFileSystem.DirectoryExists(sourceDirectoryPath));
            Assert.AreEqual("source", ReadAllText(Path.Combine(destinationDirectoryPath, "same.bms")));
            Assert.AreEqual("source-only", ReadAllText(Path.Combine(destinationDirectoryPath, "source-only.bms")));
            Assert.AreEqual("destination-only", ReadAllText(Path.Combine(destinationDirectoryPath, "destination-only.bms")));
            Assert.AreEqual("nested-source", ReadAllText(Path.Combine(destinationChildDirectoryPath, "nested.bms")));
            Assert.AreEqual("nested-destination", ReadAllText(Path.Combine(destinationChildDirectoryPath, "old.bms")));
        });
    }

    /// <summary>別volumeの未作成長パス宛先へroot・子の内容を移して元を消し、ディレクトリ作成やコピー後回収の漏れを検出する。</summary>
    /// <remarks>実際に異なるvolumeを用いる。明示した二つのfixture基点のdrive rootと実volumeを確認し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void MoveDirectory_CrossVolumeCreatesLongDestinationAndDeletesSource()
    {
        using var directories = CrossVolumeTestDirectories.Create(nameof(MoveDirectory_CrossVolumeCreatesLongDestinationAndDeletesSource));
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
    }

    /// <summary>別volumeの既存宛先をマージして衝突内容を置換し、双方だけのファイル・新しい子を保持して全体置換や子の脱落を検出する。</summary>
    /// <remarks>実際に異なるvolumeを用いる。明示した二つのfixture基点のdrive rootと実volumeを確認し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void MoveDirectory_CrossVolumeOverwritesExistingDestinationByMerging()
    {
        using var directories = CrossVolumeTestDirectories.Create(nameof(MoveDirectory_CrossVolumeOverwritesExistingDestinationByMerging));
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
    }

    /// <summary>別volumeの既存宛先で上書き禁止を守って双方の内容を保持し、失敗後の部分移動や誤上書きを検出する。</summary>
    /// <remarks>実際に異なるvolumeを用いる。明示した二つのfixture基点のdrive rootと実volumeを確認し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void MoveDirectory_CrossVolumeOverwriteFalseExistingDestinationThrowsAndPreservesBothSides()
    {
        using var directories = CrossVolumeTestDirectories.Create(nameof(MoveDirectory_CrossVolumeOverwriteFalseExistingDestinationThrowsAndPreservesBothSides));
        string sourceDirectoryPath = Path.Combine(directories.SourceBaseDirectory, "source");
        string destinationDirectoryPath = Path.Combine(directories.DestinationBaseDirectory, "destination");
        LongPathFileSystem.CreateDirectory(sourceDirectoryPath);
        LongPathFileSystem.CreateDirectory(destinationDirectoryPath);
        WriteAllText(Path.Combine(sourceDirectoryPath, "source.bms"), "source");
        WriteAllText(Path.Combine(destinationDirectoryPath, "destination.bms"), "destination");

        Assert.ThrowsException<FileMutationException>(() =>
            resilientFileMutationService.MoveDirectory(sourceDirectoryPath, destinationDirectoryPath, overwrite: false, recursiveDirectoryTreeFileMutationOptions));

        Assert.IsTrue(LongPathFileSystem.DirectoryExists(sourceDirectoryPath));
        Assert.IsTrue(LongPathFileSystem.DirectoryExists(destinationDirectoryPath));
        Assert.AreEqual("source", ReadAllText(Path.Combine(sourceDirectoryPath, "source.bms")));
        Assert.AreEqual("destination", ReadAllText(Path.Combine(destinationDirectoryPath, "destination.bms")));
    }

    /// <summary>コピー先が元配下なら作成前に拒否して元を保持し、自己再帰による無限複製や部分出力を検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void CopyDirectory_Throws_WhenDestinationIsInsideSource()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "source");
            string nestedDestinationPath = Path.Combine(sourceDirectoryPath, "nested-copy");
            LongPathFileSystem.CreateDirectory(sourceDirectoryPath);
            WriteAllText(Path.Combine(sourceDirectoryPath, "song.bms"), "source");

            Assert.ThrowsException<IOException>(() =>
                LongPathFileSystem.CopyDirectory(sourceDirectoryPath, nestedDestinationPath, overwrite: false));

            Assert.IsFalse(LongPathFileSystem.DirectoryExists(nestedDestinationPath));
            Assert.IsTrue(LongPathFileSystem.FileExists(Path.Combine(sourceDirectoryPath, "song.bms")));
        });
    }

    /// <summary>移動先が元配下なら双方を保持して拒否し、親子を含む自己移動による内容破壊を検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void MoveDirectory_Throws_WhenDestinationIsInsideSource()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "source");
            string nestedDestinationPath = Path.Combine(sourceDirectoryPath, "nested-destination");
            LongPathFileSystem.CreateDirectory(nestedDestinationPath);
            WriteAllText(Path.Combine(sourceDirectoryPath, "song.bms"), "source");

            Assert.ThrowsException<IOException>(() =>
                LongPathFileSystem.MoveDirectory(sourceDirectoryPath, nestedDestinationPath, overwrite: true));

            Assert.IsTrue(LongPathFileSystem.DirectoryExists(sourceDirectoryPath));
            Assert.IsTrue(LongPathFileSystem.DirectoryExists(nestedDestinationPath));
            Assert.IsTrue(LongPathFileSystem.FileExists(Path.Combine(sourceDirectoryPath, "song.bms")));
        });
    }

    /// <summary>ReadOnlyファイルの指定更新日時をfilesystem分解能内で保存し、属性を理由に日時更新を省く誤りを検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void SetTimestamps_UpdatesReadOnlyFile()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string filePath = Path.Combine(tempDirectoryPath, "readonly-timestamp.txt");
            File.WriteAllText(filePath, "timestamp");
            SetReadOnly(filePath);
            var expectedLastWriteTime = new DateTime(2025, 12, 1, 10, 20, 30);

            resilientFileMutationService.SetTimestamps(filePath, isDirectory: false, creationTime: null, lastWriteTime: expectedLastWriteTime, targetOnlyFileMutationOptions);

            DateTime actualLastWriteTime = File.GetLastWriteTime(filePath);
            Assert.IsTrue(Math.Abs((actualLastWriteTime - expectedLastWriteTime).TotalSeconds) < 2.0, $"Expected last write time near {expectedLastWriteTime:o}, but got {actualLastWriteTime:o}.");
        });
    }

    /// <summary>長パスのReadOnlyディレクトリへ指定更新日時を保存し、ファイル専用処理や通常パスだけの日時更新への退行を検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void SetTimestamps_UpdatesLongReadOnlyDirectory()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string directoryPath = BuildLongDirectoryPath(tempDirectoryPath, "timestamp-directory");
            LongPathFileSystem.CreateDirectory(directoryPath);
            SetReadOnly(directoryPath);
            var expectedLastWriteTime = new DateTime(2025, 12, 2, 11, 22, 33);

            resilientFileMutationService.SetTimestamps(directoryPath, isDirectory: true, creationTime: null, lastWriteTime: expectedLastWriteTime, targetOnlyFileMutationOptions);

            DateTime actualLastWriteTime = LongPathFileSystem.GetLastWriteTime(directoryPath, isDirectory: true);
            Assert.IsTrue(Math.Abs((actualLastWriteTime - expectedLastWriteTime).TotalSeconds) < 2.0, $"Expected last write time near {expectedLastWriteTime:o}, but got {actualLastWriteTime:o}.");
        });
    }

    /// <summary>排他で開かれた実ファイルの削除を再試行後の構造化失敗へ変換し、対象パス・試行事実・原因の欠落を検出する。</summary>
    /// <remarks>固有ディレクトリの実属性・内容・排他を使い、I/Oの成功結果と回収を観測する。</remarks>
    [TestMethod]
    public void DeleteFileDirect_ThrowsStructuredException_WhenFileIsLocked()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string filePath = Path.Combine(tempDirectoryPath, "locked-file.txt");
            File.WriteAllText(filePath, "locked");

            using var lockedFileStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            FileMutationException fileMutationException = Assert.ThrowsException<FileMutationException>(() =>
                resilientFileMutationService.DeleteFileDirect(filePath, targetOnlyFileMutationOptions));

            Assert.AreEqual(FileMutationKind.DeleteFileDirect, fileMutationException.Kind);
            Assert.AreEqual(filePath, fileMutationException.PrimaryPath);
            Assert.IsTrue(fileMutationException.AttemptCount >= 1);
            Assert.IsTrue(fileMutationException.WasRetried);
            Assert.IsInstanceOfType(fileMutationException.RootCause, typeof(Exception));
        });
    }

    /// <summary>DB確定portが失敗したとき実公開を一度だけ補償して元と旧宛先を保持し、未確定の新内容やstageを残す誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_PrecommitFailureCompensatesOnceAndPreservesPriorFilesystem()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination.bms");
            File.WriteAllText(sourcePath, "source");
            File.WriteAllText(destinationPath, "prior");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            bool callbackSawSource = false;
            bool callbackSawPromotedDestination = false;
            var executor = new FileDbMutationExecutor(
                plan,
                resilientFileMutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                callbackSawSource = File.Exists(sourcePath);
                callbackSawPromotedDestination = File.ReadAllText(destinationPath) == "source";
                return FileDbMutationCommitResult.Failed(new InvalidOperationException("db-before-receipt"));
            });

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.AreEqual(1, receipt.CompensationAttemptCount);
            Assert.IsTrue(callbackSawSource);
            Assert.IsTrue(callbackSawPromotedDestination);
            Assert.AreEqual("source", File.ReadAllText(sourcePath));
            Assert.AreEqual("prior", File.ReadAllText(destinationPath));
            Assert.IsFalse(receipt.HasUnrestoredExistingDestination);
            Assert.IsFalse(File.Exists(plan.Paths[0].StagingPath));
            Assert.IsFalse(File.Exists(plan.Paths[0].BackupPath));
        });
    }

    /// <summary>ファイル予定先に実ディレクトリがある要求をI/O・DB確定前に拒否し、sentinelや元を破壊する誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_RejectsFileDestinationWhenExistingDirectory()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination");
            WriteAllText(sourcePath, "source");
            LongPathFileSystem.CreateDirectory(destinationPath);
            string sentinelPath = Path.Combine(destinationPath, "sentinel.txt");
            WriteAllText(sentinelPath, "sentinel");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            bool commitCalled = false;
            var mutationService = new InterleavingFileMutationService();
            var executor = new FileDbMutationExecutor(
                plan,
                mutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                commitCalled = true;
                return FileDbMutationCommitResult.Durable();
            });

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.IsFalse(commitCalled);
            Assert.AreEqual(0, mutationService.MutationOperations.Count);
            Assert.IsNotNull(receipt.Failure);
            Assert.IsTrue(LongPathFileSystem.FileExists(sourcePath));
            Assert.IsTrue(LongPathFileSystem.DirectoryExists(destinationPath));
            Assert.AreEqual("sentinel", ReadAllText(sentinelPath));
            Assert.IsFalse(LongPathFileSystem.FileExists(plan.Paths[0].StagingPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(plan.Paths[0].BackupPath));
        });
    }

    /// <summary>ディレクトリ予定先に実ファイルがある要求を変更前に拒否し、逆向きの型衝突を見落として既存内容を破壊する誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_RejectsDirectoryDestinationWhenExistingFile()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source-directory");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination-directory");
            LongPathFileSystem.CreateDirectory(sourcePath);
            WriteAllText(Path.Combine(sourcePath, "source.bms"), "source");
            WriteAllText(destinationPath, "existing file");
            FileDbMutationPathPlan path = CreateMutationPathPlanForTest(sourcePath, destinationPath, isDirectory: true);
            var plan = new FileDbMutationPlan(
                Guid.NewGuid(),
                [path],
                [sourcePath],
                [],
                recursiveSourceCleanup: true);
            bool commitCalled = false;
            var mutationService = new InterleavingFileMutationService();
            var executor = new FileDbMutationExecutor(
                plan,
                mutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                commitCalled = true;
                return FileDbMutationCommitResult.Durable();
            });

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.IsFalse(commitCalled);
            Assert.AreEqual(0, mutationService.MutationOperations.Count);
            var conflict = receipt.Failure as FileDbMutationDestinationTypeConflictException;
            Assert.IsNotNull(conflict);
            Assert.AreEqual(sourcePath, conflict.SourcePath);
            Assert.AreEqual(destinationPath, conflict.DestinationPath);
            Assert.IsTrue(conflict.ExpectedIsDirectory);
            Assert.IsFalse(conflict.ExistingIsDirectory);
            Assert.IsTrue(LongPathFileSystem.DirectoryExists(sourcePath));
            Assert.AreEqual("existing file", ReadAllText(destinationPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(path.StagingPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(path.BackupPath));
        });
    }

    /// <summary>計画上backupなしでも実宛先の型を再検査し、予定値だけで型guardを迂回して外部ディレクトリを消す誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_RejectsTypeConflictWhenBackupFlagSaysDestinationIsAbsent()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination-directory");
            string stagingPath = LongPathFileSystem.CreateMutationSiblingPath(destinationPath, "stage");
            WriteAllText(sourcePath, "source");
            LongPathFileSystem.CreateDirectory(destinationPath);
            string sentinelPath = Path.Combine(destinationPath, "sentinel.txt");
            WriteAllText(sentinelPath, "sentinel");
            var path = new FileDbMutationPathPlan(
                sourcePath,
                destinationPath,
                stagingPath,
                backupPath: string.Empty,
                isDirectory: false);
            var plan = new FileDbMutationPlan(
                Guid.NewGuid(),
                [path],
                [sourcePath],
                [],
                recursiveSourceCleanup: false);

            var mutationService = new InterleavingFileMutationService();
            FileDbMutationReceipt receipt = new FileDbMutationExecutor(
                plan,
                mutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions).Execute(
                    () => FileDbMutationCommitResult.Durable());

            Assert.AreEqual(0, mutationService.MutationOperations.Count);
            var conflict = receipt.Failure as FileDbMutationDestinationTypeConflictException;
            Assert.IsNotNull(conflict);
            Assert.AreEqual(destinationPath, conflict.DestinationPath);
            Assert.IsFalse(conflict.ExpectedIsDirectory);
            Assert.IsTrue(conflict.ExistingIsDirectory);
            Assert.IsTrue(LongPathFileSystem.FileExists(sourcePath));
            Assert.AreEqual("sentinel", ReadAllText(sentinelPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(path.StagingPath));
        });
    }

    /// <summary>後半の宛先に型衝突がある計画は全変更・DB確定前に拒否し、先行対象だけを部分公開する誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_RejectsWholePlanWhenLaterDestinationTypeConflicts()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string firstSourcePath = Path.Combine(tempDirectoryPath, "first.bms");
            string firstDestinationPath = Path.Combine(tempDirectoryPath, "first-destination.bms");
            string secondSourcePath = Path.Combine(tempDirectoryPath, "second.bms");
            string secondDestinationPath = Path.Combine(tempDirectoryPath, "second-destination");
            WriteAllText(firstSourcePath, "first");
            WriteAllText(secondSourcePath, "second");
            LongPathFileSystem.CreateDirectory(secondDestinationPath);
            string sentinelPath = Path.Combine(secondDestinationPath, "sentinel.txt");
            WriteAllText(sentinelPath, "sentinel");
            FileDbMutationPathPlan firstPath = CreateMutationPathPlanForTest(firstSourcePath, firstDestinationPath, isDirectory: false);
            FileDbMutationPathPlan secondPath = CreateMutationPathPlanForTest(secondSourcePath, secondDestinationPath, isDirectory: false);
            var plan = new FileDbMutationPlan(
                Guid.NewGuid(),
                [firstPath, secondPath],
                [firstSourcePath, secondSourcePath],
                [],
                recursiveSourceCleanup: false);
            bool commitCalled = false;
            var mutationService = new InterleavingFileMutationService();
            var executor = new FileDbMutationExecutor(
                plan,
                mutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                commitCalled = true;
                return FileDbMutationCommitResult.Durable();
            });

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.IsFalse(commitCalled);
            Assert.AreEqual(0, mutationService.MutationOperations.Count);
            Assert.IsNotNull(receipt.Failure);
            Assert.IsTrue(LongPathFileSystem.FileExists(firstSourcePath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(firstDestinationPath));
            Assert.IsTrue(LongPathFileSystem.FileExists(secondSourcePath));
            Assert.IsTrue(LongPathFileSystem.DirectoryExists(secondDestinationPath));
            Assert.AreEqual("sentinel", ReadAllText(sentinelPath));
            Assert.IsFalse(LongPathFileSystem.FileExists(firstPath.StagingPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(firstPath.BackupPath));
            Assert.IsFalse(LongPathFileSystem.FileExists(secondPath.StagingPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(secondPath.BackupPath));
        });
    }

    /// <summary>事前検査後に外部が作った異なる型の宛先を公開直前に検出し、一回補償とstage回収を行って外部sentinelを守る保証の退行を検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_RechecksDestinationTypeBeforePromotion()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination");
            WriteAllText(sourcePath, "source");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            var mutationService = new InterleavingFileMutationService(
                afterFirstCopy: () =>
                {
                    LongPathFileSystem.CreateDirectory(destinationPath);
                    WriteAllText(Path.Combine(destinationPath, "sentinel.txt"), "external");
                });
            var executor = new FileDbMutationExecutor(
                plan,
                mutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);
            bool commitCalled = false;

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                commitCalled = true;
                return FileDbMutationCommitResult.Durable();
            });

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.IsFalse(commitCalled);
            Assert.AreEqual(1, receipt.CompensationAttemptCount);
            var conflict = receipt.Failure as FileDbMutationDestinationTypeConflictException;
            Assert.IsNotNull(conflict);
            Assert.AreEqual(destinationPath, conflict.DestinationPath);
            Assert.IsTrue(LongPathFileSystem.FileExists(sourcePath));
            Assert.AreEqual("external", ReadAllText(Path.Combine(destinationPath, "sentinel.txt")));
            Assert.IsFalse(LongPathFileSystem.EntryExists(plan.Paths[0].StagingPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(plan.Paths[0].BackupPath));
        });
    }

    /// <summary>先行公開後の後半宛先の外部型衝突で一回だけ補償し、先行公開を残したまま通常拒否で終了する誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_RechecksLaterDestinationAndCompensatesEarlierPromotion()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string firstSourcePath = Path.Combine(tempDirectoryPath, "first.bms");
            string firstDestinationPath = Path.Combine(tempDirectoryPath, "first-destination.bms");
            string secondSourcePath = Path.Combine(tempDirectoryPath, "second.bms");
            string secondDestinationPath = Path.Combine(tempDirectoryPath, "second-destination");
            WriteAllText(firstSourcePath, "first");
            WriteAllText(secondSourcePath, "second");
            FileDbMutationPathPlan firstPath = CreateMutationPathPlanForTest(firstSourcePath, firstDestinationPath, isDirectory: false);
            FileDbMutationPathPlan secondPath = CreateMutationPathPlanForTest(secondSourcePath, secondDestinationPath, isDirectory: false);
            var plan = new FileDbMutationPlan(
                Guid.NewGuid(),
                [firstPath, secondPath],
                [firstSourcePath, secondSourcePath],
                [],
                recursiveSourceCleanup: false);
            var mutationService = new InterleavingFileMutationService(
                promotionDestinationPath: firstDestinationPath,
                afterFirstPromotion: () =>
                {
                    LongPathFileSystem.CreateDirectory(secondDestinationPath);
                    WriteAllText(Path.Combine(secondDestinationPath, "sentinel.txt"), "external");
                });
            var executor = new FileDbMutationExecutor(
                plan,
                mutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);
            bool commitCalled = false;

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                commitCalled = true;
                return FileDbMutationCommitResult.Durable();
            });

            Assert.AreEqual(FileDbMutationTerminalState.Failed, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.IsFalse(commitCalled);
            Assert.AreEqual(1, receipt.CompensationAttemptCount);
            Assert.IsInstanceOfType(receipt.Failure, typeof(FileDbMutationDestinationTypeConflictException));
            Assert.IsTrue(LongPathFileSystem.FileExists(firstSourcePath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(firstDestinationPath));
            Assert.IsTrue(LongPathFileSystem.FileExists(secondSourcePath));
            Assert.IsTrue(LongPathFileSystem.DirectoryExists(secondDestinationPath));
            Assert.AreEqual("external", ReadAllText(Path.Combine(secondDestinationPath, "sentinel.txt")));
            Assert.IsFalse(LongPathFileSystem.EntryExists(firstPath.StagingPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(secondPath.StagingPath));
        });
    }

    /// <summary>後半の型衝突と先行公開の補償失敗が重なると手動復旧の事実・パスを保持し、通常の拒否へ格下げして復旧対象を失う誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_RechecksLaterDestinationAndPreservesCompensationFailure()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string firstSourcePath = Path.Combine(tempDirectoryPath, "first.bms");
            string firstDestinationPath = Path.Combine(tempDirectoryPath, "first-destination.bms");
            string secondSourcePath = Path.Combine(tempDirectoryPath, "second.bms");
            string secondDestinationPath = Path.Combine(tempDirectoryPath, "second-destination");
            WriteAllText(firstSourcePath, "first");
            WriteAllText(secondSourcePath, "second");
            FileDbMutationPathPlan firstPath = CreateMutationPathPlanForTest(firstSourcePath, firstDestinationPath, isDirectory: false);
            FileDbMutationPathPlan secondPath = CreateMutationPathPlanForTest(secondSourcePath, secondDestinationPath, isDirectory: false);
            var plan = new FileDbMutationPlan(
                Guid.NewGuid(),
                [firstPath, secondPath],
                [firstSourcePath, secondSourcePath],
                [],
                recursiveSourceCleanup: false);
            var mutationService = new InterleavingFileMutationService(
                promotionDestinationPath: firstDestinationPath,
                afterFirstPromotion: () =>
                {
                    LongPathFileSystem.CreateDirectory(secondDestinationPath);
                    WriteAllText(Path.Combine(secondDestinationPath, "sentinel.txt"), "external");
                },
                compensationFailurePath: firstDestinationPath);
            var executor = new FileDbMutationExecutor(
                plan,
                mutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(
                () => FileDbMutationCommitResult.Durable());

            Assert.AreEqual(FileDbMutationTerminalState.ManualRecoveryRequired, receipt.TerminalState);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.IsFalse(receipt.HasUnrestoredExistingDestination, "新規宛先だけの残留は既存宛先の復元不能ではありません。");
            Assert.AreEqual(1, receipt.CompensationAttemptCount);
            Assert.IsInstanceOfType(receipt.Failure, typeof(IOException));
            StringAssert.Contains(receipt.Failure.ToString(), "injected-compensation-delete-failure");
            Assert.IsTrue(receipt.RecoveryPaths.Contains(firstSourcePath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(receipt.RecoveryPaths.Contains(firstDestinationPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(receipt.RecoveryPaths.Contains(firstPath.StagingPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(LongPathFileSystem.FileExists(firstSourcePath));
            Assert.IsTrue(LongPathFileSystem.FileExists(firstDestinationPath));
            Assert.AreEqual("first", ReadAllText(firstDestinationPath));
            Assert.IsTrue(LongPathFileSystem.FileExists(secondSourcePath));
            Assert.IsTrue(LongPathFileSystem.DirectoryExists(secondDestinationPath));
            Assert.AreEqual("external", ReadAllText(Path.Combine(secondDestinationPath, "sentinel.txt")));
        });
    }

    /// <summary>DBのdurable確定後にfinalizerを元回収前へ一度だけ接続し、順序違反や補償の再実行、成功後の元残留を検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_DurableFinalizerRunsBeforeCleanup()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination.bms");
            File.WriteAllText(sourcePath, "source");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            bool callbackSawSource = false;
            bool durableFinalizerSawSource = false;
            int durableFinalizerInvocationCount = 0;
            var executor = new FileDbMutationExecutor(
                plan,
                resilientFileMutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                callbackSawSource = File.Exists(sourcePath);
                return FileDbMutationCommitResult.Durable(() =>
                {
                    durableFinalizerInvocationCount++;
                    durableFinalizerSawSource = File.Exists(sourcePath);
                });
            });

            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.AreEqual(0, receipt.CompensationAttemptCount);
            Assert.IsTrue(callbackSawSource);
            Assert.AreEqual(1, durableFinalizerInvocationCount);
            Assert.IsTrue(durableFinalizerSawSource);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.AreEqual("source", File.ReadAllText(destinationPath));
        });
    }

    /// <summary>durable後のfinalizer失敗でも宛先確定と元回収を保持し、補償で確定内容を戻す誤りや失敗分類の消失を検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_DurableFinalizerFailureKeepsDurableOutcome()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination.bms");
            File.WriteAllText(sourcePath, "source");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            var executor = new FileDbMutationExecutor(
                plan,
                resilientFileMutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
                FileDbMutationCommitResult.Durable(() => throw new InvalidOperationException("deferred-effect")));

            Assert.AreEqual(FileDbMutationTerminalState.DurableFinalizationFailed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsNotNull(receipt.Failure);
            Assert.AreEqual("deferred-effect", receipt.Failure.Message);
            Assert.AreEqual("deferred-effect", receipt.FinalizationFailure.Message);
            Assert.IsFalse(receipt.HasCleanupFailure);
            Assert.AreEqual(0, receipt.CompensationAttemptCount);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.AreEqual("source", File.ReadAllText(destinationPath));
        });
    }

    /// <summary>finalizerと元回収の両方が失敗するとdurable状態・両原因・復旧パスを保持し、二次失敗で主因や未回収元を隠す誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_DurableFinalizerAndCleanupFailurePreserveBothFailures()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination.bms");
            File.WriteAllText(sourcePath, "source");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            var executor = new FileDbMutationExecutor(
                plan,
                new FailingDeleteFileMutationService(sourcePath),
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);
            var finalizationFailure = new InvalidOperationException("deferred-effect");

            FileDbMutationReceipt receipt = executor.Execute(() =>
                FileDbMutationCommitResult.Durable(() => throw finalizationFailure));

            Assert.AreEqual(FileDbMutationTerminalState.DurableFinalizationFailed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.AreEqual(0, receipt.CompensationAttemptCount);
            Assert.IsTrue(receipt.HasCleanupFailure);
            Assert.IsTrue(receipt.RecoveryPaths.Any(path =>
                string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase)));
            Assert.AreSame(finalizationFailure, receipt.FinalizationFailure);
            Assert.IsNotNull(receipt.CleanupFailure);
            Assert.IsTrue(ContainsException(receipt.Failure, finalizationFailure));
            Assert.IsTrue(receipt.Failure.ToString().Contains("injected-delete-failure", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.AreEqual("source", File.ReadAllText(destinationPath));
        });
    }

    /// <summary>確定portがdurableと失敗を同時に返した場合にfinalization失敗として保持し、未確定失敗と誤認して補償する誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_DurableCommitResultFailureIsDurableFinalizationFailure()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination.bms");
            File.WriteAllText(sourcePath, "source");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            var finalizationFailure = new InvalidOperationException("durable-commit-failure");
            var executor = new FileDbMutationExecutor(
                plan,
                resilientFileMutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
                FileDbMutationCommitResult.Durable(durableFailure: finalizationFailure));

            Assert.AreEqual(FileDbMutationTerminalState.DurableFinalizationFailed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.AreEqual(0, receipt.CompensationAttemptCount);
            Assert.AreSame(finalizationFailure, receipt.Failure);
            Assert.AreSame(finalizationFailure, receipt.FinalizationFailure);
            Assert.IsFalse(receipt.HasCleanupFailure);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.AreEqual("source", File.ReadAllText(destinationPath));
        });
    }

    /// <summary>durable後に元回収だけが失敗した場合は新宛先を保持して回収失敗付きの完了を返し、確定済み内容への補償を検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_CleanupFailureReturnsCompletedWithCleanupFailureWithoutCompensation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination.bms");
            File.WriteAllText(sourcePath, "source");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            var executor = new FileDbMutationExecutor(
                plan,
                new FailingDeleteFileMutationService(sourcePath),
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);
            bool durableFinalizerCalled = false;

            FileDbMutationReceipt receipt = executor.Execute(() =>
                FileDbMutationCommitResult.Durable(() => durableFinalizerCalled = true));

            Assert.AreEqual(FileDbMutationTerminalState.CompletedWithCleanupFailure, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.AreEqual(0, receipt.CompensationAttemptCount);
            Assert.IsTrue(receipt.CleanupAttemptCount > 0);
            Assert.IsTrue(durableFinalizerCalled);
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.AreEqual("source", File.ReadAllText(destinationPath));
        });
    }

    /// <summary>DB確定前の補償が失敗したとき手動復旧で停止して元・宛先・backupを保持し、後続回収で復旧用の内容を失う誤りを検出する。</summary>
    /// <remarks>実filesystemのstage・公開・補償を観測する。DB確定portは結果を注入し、実DBのtransactionは上位保存caseが分担する。</remarks>
    [TestMethod]
    public void FileDbMutationExecutor_CompensationFailureReturnsManualRecoveryAndRetainsRecoveryPaths()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination.bms");
            File.WriteAllText(sourcePath, "source");
            File.WriteAllText(destinationPath, "prior");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            var executor = new FileDbMutationExecutor(
                plan,
                new FailingDeleteFileMutationService(destinationPath),
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
                FileDbMutationCommitResult.Failed(new InvalidOperationException("db-before-receipt")));

            Assert.AreEqual(FileDbMutationTerminalState.ManualRecoveryRequired, receipt.TerminalState);
            Assert.IsTrue(receipt.HasUnrestoredExistingDestination);
            Assert.IsFalse(receipt.DurableCommit);
            Assert.AreEqual(1, receipt.CompensationAttemptCount);
            Assert.IsTrue(receipt.RecoveryPaths.Contains(sourcePath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(receipt.RecoveryPaths.Contains(destinationPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(receipt.RecoveryPaths.Contains(plan.Paths[0].BackupPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.IsTrue(File.Exists(plan.Paths[0].BackupPath));
            Assert.AreEqual("prior", File.ReadAllText(plan.Paths[0].BackupPath));
            Assert.AreEqual("source", File.ReadAllText(destinationPath));
            Assert.IsTrue(receipt.WithFailure(new IOException("later failure")).HasUnrestoredExistingDestination);
            Assert.IsFalse(File.Exists(plan.Paths[0].StagingPath));
        });
    }

    /// <summary>公開前のstage回収失敗だけでは既存宛先の未復元に昇格せず、元と旧宛先の内容を保持する。</summary>
    [TestMethod]
    public void FileDbMutationExecutor_StageCleanupFailureDoesNotMeanUnrestoredDestination()
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "source.bms");
            string destination = Path.Combine(root, "destination.bms");
            File.WriteAllText(source, "new");
            File.WriteAllText(destination, "old");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(source, destination);
            var service = new InterleavingFileMutationService(
                afterFirstCopy: () => throw new IOException("stage copy failure"),
                compensationFailurePath: plan.Paths.Single().StagingPath);
            var executor = new FileDbMutationExecutor(plan, service,
                targetOnlyFileMutationOptions, recursiveDirectoryTreeFileMutationOptions);
            bool committed = false;
            FileDbMutationReceipt receipt = executor.Execute(() => { committed = true; return FileDbMutationCommitResult.Durable(); });
            Assert.IsFalse(committed);
            Assert.AreEqual(FileDbMutationTerminalState.ManualRecoveryRequired, receipt.TerminalState);
            Assert.IsFalse(receipt.HasUnrestoredExistingDestination);
            Assert.AreEqual(1, receipt.CompensationAttemptCount);
            Assert.AreEqual("old", File.ReadAllText(destination));
            Assert.AreEqual("new", File.ReadAllText(source));
            Assert.IsTrue(File.Exists(plan.Paths.Single().StagingPath));
            Assert.IsFalse(File.Exists(plan.Paths.Single().BackupPath));
        });
    }

    private static FileDbMutationPlan CreateFileDbMutationPlan(string sourcePath, string destinationPath)
    {
        FileDbMutationPathPlan path = CreateMutationPathPlanForTest(sourcePath, destinationPath, isDirectory: false);
        return new FileDbMutationPlan(
            Guid.NewGuid(),
            [path],
            [sourcePath],
            [],
            recursiveSourceCleanup: false);
    }

    private static FileDbMutationPathPlan CreateMutationPathPlanForTest(
        string sourcePath,
        string destinationPath,
        bool isDirectory)
    {
        string stagingPath = LongPathFileSystem.CreateMutationSiblingPath(destinationPath, "stage");
        string backupPath = LongPathFileSystem.EntryExists(destinationPath)
            ? LongPathFileSystem.CreateMutationSiblingPath(destinationPath, "backup")
            : string.Empty;
        return new FileDbMutationPathPlan(sourcePath, destinationPath, stagingPath, backupPath, isDirectory);
    }

    private sealed class InterleavingFileMutationService : IFileMutationService
    {
        private readonly ResilientFileMutationService inner = new();
        private readonly string? promotionDestinationPath;
        private readonly Action? afterFirstCopy;
        private readonly Action? afterFirstPromotion;
        private readonly string? compensationFailurePath;
        private bool copyCallbackInvoked;
        private bool promotionCallbackInvoked;

        /// <summary>事前検証テストが変更 I/O の試行を検出できるよう、全 mutation 呼び出しを記録します。</summary>
        internal List<string> MutationOperations { get; } = [];

        public InterleavingFileMutationService(
            string? promotionDestinationPath = null,
            Action? afterFirstCopy = null,
            Action? afterFirstPromotion = null,
            string? compensationFailurePath = null)
        {
            this.promotionDestinationPath = promotionDestinationPath;
            this.afterFirstCopy = afterFirstCopy;
            this.afterFirstPromotion = afterFirstPromotion;
            this.compensationFailurePath = compensationFailurePath;
        }

        public void EnsureDirectory(string directoryPath, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(EnsureDirectory));
            inner.EnsureDirectory(directoryPath, options);
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(MoveFile));
            inner.MoveFile(sourcePath, destinationPath, overwrite, options);
            if (!promotionCallbackInvoked
                && afterFirstPromotion != null
                && string.Equals(destinationPath, promotionDestinationPath, StringComparison.OrdinalIgnoreCase))
            {
                promotionCallbackInvoked = true;
                afterFirstPromotion();
            }
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(MoveDirectory));
            inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(CopyFile));
            inner.CopyFile(sourcePath, destinationPath, overwrite, options);
            if (!copyCallbackInvoked && afterFirstCopy != null)
            {
                copyCallbackInvoked = true;
                afterFirstCopy();
            }
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(CopyDirectory));
            inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(DeleteFileDirect));
            if (!string.IsNullOrWhiteSpace(compensationFailurePath)
                && string.Equals(filePath, compensationFailurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected-compensation-delete-failure");
            }
            inner.DeleteFileDirect(filePath, options);
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(DeleteFileShell));
            inner.DeleteFileShell(filePath, uiOption, recycleOption, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(DeleteDirectoryDirect));
            inner.DeleteDirectoryDirect(directoryPath, recursive, options);
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(DeleteDirectoryShell));
            inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions? options = null)
        {
            MutationOperations.Add(nameof(SetTimestamps));
            inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
        }
    }

    private static void WithTemporaryDirectory(Action<string> testAction)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FileMutationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            testAction(tempDirectoryPath);
        }
        finally
        {
            DeleteDirectoryIfExists(tempDirectoryPath);
        }
    }

    private static void SetReadOnly(string fileSystemPath)
    {
        FileAttributes currentAttributes = LongPathFileSystem.GetAttributes(fileSystemPath);
        LongPathFileSystem.SetAttributes(fileSystemPath, currentAttributes | FileAttributes.ReadOnly);
    }

    private static string BuildLongDirectoryPath(string tempDirectoryPath, string leafName)
    {
        string path = tempDirectoryPath;
        for (int i = 0; path.Length < 285; i++)
        {
            path = Path.Combine(path, "segment_" + i.ToString("00") + "_" + new string('a', 32));
        }
        return Path.Combine(path, leafName);
    }

    private static void WriteAllText(string path, string contents)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(contents);
    }

    private static string ReadAllText(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void DeleteDirectoryIfExists(string directoryPath)
    {
        if (!LongPathFileSystem.DirectoryExists(directoryPath))
        {
            return;
        }

        foreach (string childFilePath in LongPathFileSystem.EnumerateFiles(directoryPath, "*", System.IO.SearchOption.AllDirectories))
        {
            LongPathFileSystem.SetAttributes(childFilePath, FileAttributes.Normal);
        }

        foreach (string childDirectoryPath in LongPathFileSystem.EnumerateDirectories(directoryPath, "*", System.IO.SearchOption.AllDirectories))
        {
            LongPathFileSystem.SetAttributes(childDirectoryPath, FileAttributes.Normal);
        }

        LongPathFileSystem.SetAttributes(directoryPath, FileAttributes.Normal);
        LongPathFileSystem.DeleteDirectory(directoryPath, recursive: true);
    }

    private static bool ContainsException(Exception? candidate, Exception expected)
    {
        if (candidate == null)
        {
            return false;
        }
        if (ReferenceEquals(candidate, expected))
        {
            return true;
        }
        if (candidate is AggregateException aggregate
            && aggregate.InnerExceptions.Any(exception => ContainsException(exception, expected)))
        {
            return true;
        }
        return ContainsException(candidate.InnerException, expected);
    }

    private sealed class FailingDeleteFileMutationService(string failurePath) : IFileMutationService
    {
        private readonly ResilientFileMutationService inner = new();

        public void EnsureDirectory(string directoryPath, FileMutationOptions? options = null)
            => inner.EnsureDirectory(directoryPath, options);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
            => inner.MoveFile(sourcePath, destinationPath, overwrite, options);

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
            => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
            => inner.CopyFile(sourcePath, destinationPath, overwrite, options);

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions? options = null)
            => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);

        public void DeleteFileDirect(string filePath, FileMutationOptions? options = null)
        {
            if (string.Equals(filePath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected-delete-failure");
            }
            inner.DeleteFileDirect(filePath, options);
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null)
            => inner.DeleteFileShell(filePath, uiOption, recycleOption, options);

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions? options = null)
            => inner.DeleteDirectoryDirect(directoryPath, recursive, options);

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions? options = null)
            => inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions? options = null)
            => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
    }
}
