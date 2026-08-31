using System;
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

    /// <summary>
    /// ReadOnly ファイルでも直接削除できることを検証します。
    /// </summary>
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

    /// <summary>
    /// ReadOnly を含むディレクトリツリーでも直接削除できることを検証します。
    /// </summary>
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

    /// <summary>
    /// シェル経由の永久削除でも ReadOnly ファイルを削除できることを検証します。
    /// </summary>
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

    /// <summary>
    /// 上書き先が ReadOnly でもファイル移動が成功することを検証します。
    /// </summary>
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

    /// <summary>
    /// 長パス上の ReadOnly 上書き先でもファイル移動が成功することを検証します。
    /// </summary>
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

    /// <summary>
    /// 上書き移動で移動元を動かせない場合、既存の移動先を失わないことを検証します。
    /// </summary>
    [TestMethod]
    public void MoveFile_OverwritePreservesDestination_WhenSourceCannotMove()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceFilePath = Path.Combine(tempDirectoryPath, "locked-source.bms");
            string destinationFilePath = Path.Combine(tempDirectoryPath, "destination.bms");
            WriteAllText(sourceFilePath, "source");
            WriteAllText(destinationFilePath, "destination");

            using var lockedFileStream = LongPathFileSystem.Open(sourceFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.ThrowsException<IOException>(() =>
                LongPathFileSystem.MoveFile(sourceFilePath, destinationFilePath, overwrite: true));

            Assert.IsTrue(LongPathFileSystem.FileExists(sourceFilePath));
            Assert.IsTrue(LongPathFileSystem.FileExists(destinationFilePath));
            Assert.AreEqual("destination", ReadAllText(destinationFilePath));
        });
    }

    /// <summary>
    /// 別ボリュームへのファイル移動はコピー後に移動元を削除し、上書き先を置き換えることを検証します。
    /// </summary>
    [TestMethod]
    public void MoveFile_CrossVolumeOverwriteCopiesAndDeletesSource()
    {
        using CrossVolumeTestDirectories directories = CrossVolumeTestDirectories.CreateOrInconclusive(nameof(MoveFile_CrossVolumeOverwriteCopiesAndDeletesSource));
        string sourceFilePath = Path.Combine(directories.SourceBaseDirectory, "source.bms");
        string destinationFilePath = Path.Combine(directories.DestinationBaseDirectory, "destination.bms");
        WriteAllText(sourceFilePath, "source");
        WriteAllText(destinationFilePath, "destination");

        resilientFileMutationService.MoveFile(sourceFilePath, destinationFilePath, overwrite: true, targetOnlyFileMutationOptions);

        Assert.IsFalse(LongPathFileSystem.FileExists(sourceFilePath));
        Assert.IsTrue(LongPathFileSystem.FileExists(destinationFilePath));
        Assert.AreEqual("source", ReadAllText(destinationFilePath));
    }

    /// <summary>
    /// 長パス上のディレクトリ上書き移動は既存の宛先を消さず、衝突ファイルだけを上書きしてマージすることを検証します。
    /// </summary>
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

    /// <summary>
    /// 別ボリュームの存在しない長パス宛先へディレクトリを移動できることを検証します。
    /// </summary>
    [TestMethod]
    public void MoveDirectory_CrossVolumeCreatesLongDestinationAndDeletesSource()
    {
        using CrossVolumeTestDirectories directories = CrossVolumeTestDirectories.CreateOrInconclusive(nameof(MoveDirectory_CrossVolumeCreatesLongDestinationAndDeletesSource));
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

    /// <summary>
    /// 別ボリュームの既存宛先へディレクトリをマージ移動できることを検証します。
    /// </summary>
    [TestMethod]
    public void MoveDirectory_CrossVolumeOverwritesExistingDestinationByMerging()
    {
        using CrossVolumeTestDirectories directories = CrossVolumeTestDirectories.CreateOrInconclusive(nameof(MoveDirectory_CrossVolumeOverwritesExistingDestinationByMerging));
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

    /// <summary>
    /// 別ボリュームの既存宛先へ overwrite=false で移動すると失敗し、双方を保持することを検証します。
    /// </summary>
    [TestMethod]
    public void MoveDirectory_CrossVolumeOverwriteFalseExistingDestinationThrowsAndPreservesBothSides()
    {
        using CrossVolumeTestDirectories directories = CrossVolumeTestDirectories.CreateOrInconclusive(nameof(MoveDirectory_CrossVolumeOverwriteFalseExistingDestinationThrowsAndPreservesBothSides));
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

    /// <summary>
    /// ディレクトリコピーでコピー先をコピー元配下に置く自己再帰を禁止することを検証します。
    /// </summary>
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

    /// <summary>
    /// ディレクトリ移動で移動先を移動元配下に置く自己再帰を禁止することを検証します。
    /// </summary>
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

    /// <summary>
    /// ReadOnly ファイルでもタイムスタンプを更新できることを検証します。
    /// </summary>
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

    /// <summary>
    /// 長パス上の ReadOnly ディレクトリでもタイムスタンプを更新できることを検証します。
    /// </summary>
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

    /// <summary>
    /// ロック中のファイル削除失敗は診断情報付きの FileMutationException に変換されることを検証します。
    /// </summary>
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

    /// <summary>
    /// durable DB receipt 前の失敗では compensation を一度だけ行い、source と旧 destination を保持することを検証します。
    /// </summary>
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
            Assert.IsFalse(File.Exists(plan.Paths[0].StagingPath));
            Assert.IsFalse(File.Exists(plan.Paths[0].BackupPath));
        });
    }

    /// <summary>
    /// durable receipt 後は source cleanup を完了してから post-commit callback を呼ぶことを検証します。
    /// </summary>
    [TestMethod]
    public void FileDbMutationExecutor_DurableReceiptFinalizesBeforePostCommitCallback()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourcePath = Path.Combine(tempDirectoryPath, "source.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "destination.bms");
            File.WriteAllText(sourcePath, "source");
            FileDbMutationPlan plan = CreateFileDbMutationPlan(sourcePath, destinationPath);
            bool callbackSawSource = false;
            bool postCommitSawSource = true;
            var executor = new FileDbMutationExecutor(
                plan,
                resilientFileMutationService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions);

            FileDbMutationReceipt receipt = executor.Execute(() =>
            {
                callbackSawSource = File.Exists(sourcePath);
                return FileDbMutationCommitResult.Durable(() => postCommitSawSource = File.Exists(sourcePath));
            });

            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.AreEqual(0, receipt.CompensationAttemptCount);
            Assert.IsTrue(callbackSawSource);
            Assert.IsFalse(postCommitSawSource);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.AreEqual("source", File.ReadAllText(destinationPath));
        });
    }

    /// <summary>
    /// durable receipt 後の source cleanup failure は authoritative destination を維持し、cleanup failure として返すことを検証します。
    /// </summary>
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
            bool postCommitCalled = false;

            FileDbMutationReceipt receipt = executor.Execute(() =>
                FileDbMutationCommitResult.Durable(() => postCommitCalled = true));

            Assert.AreEqual(FileDbMutationTerminalState.CompletedWithCleanupFailure, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.AreEqual(0, receipt.CompensationAttemptCount);
            Assert.IsTrue(receipt.CleanupAttemptCount > 0);
            Assert.IsTrue(postCommitCalled);
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.AreEqual("source", File.ReadAllText(destinationPath));
        });
    }

    /// <summary>
    /// compensation failure は ManualRecoveryRequired で停止し、後続 cleanup を行わないことを検証します。
    /// </summary>
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
            Assert.IsFalse(receipt.DurableCommit);
            Assert.AreEqual(1, receipt.CompensationAttemptCount);
            Assert.IsTrue(receipt.RecoveryPaths.Contains(sourcePath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(receipt.RecoveryPaths.Contains(destinationPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(receipt.RecoveryPaths.Contains(plan.Paths[0].BackupPath, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.IsTrue(File.Exists(plan.Paths[0].BackupPath));
            Assert.IsFalse(File.Exists(plan.Paths[0].StagingPath));
        });
    }

    private static FileDbMutationPlan CreateFileDbMutationPlan(string sourcePath, string destinationPath)
    {
        string stagingPath = LongPathFileSystem.CreateMutationSiblingPath(destinationPath, "stage");
        string backupPath = LongPathFileSystem.EntryExists(destinationPath)
            ? LongPathFileSystem.CreateMutationSiblingPath(destinationPath, "backup")
            : string.Empty;
        return new FileDbMutationPlan(
            Guid.NewGuid(),
            [new FileDbMutationPathPlan(sourcePath, destinationPath, stagingPath, backupPath, isDirectory: false)],
            [sourcePath],
            [],
            recursiveSourceCleanup: false);
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
        using var stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(contents);
    }

    private static string ReadAllText(string path)
    {
        using var stream = LongPathFileSystem.OpenRead(path);
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

    private sealed class FailingDeleteFileMutationService(string failurePath) : IFileMutationService
    {
        private readonly ResilientFileMutationService inner = new();

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null)
            => inner.EnsureDirectory(directoryPath, options);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
            => inner.MoveFile(sourcePath, destinationPath, overwrite, options);

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
            => inner.MoveDirectory(sourcePath, destinationPath, overwrite, options);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
            => inner.CopyFile(sourcePath, destinationPath, overwrite, options);

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null)
            => inner.CopyDirectory(sourcePath, destinationPath, overwrite, options);

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null)
        {
            if (string.Equals(filePath, failurePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("injected-delete-failure");
            }
            inner.DeleteFileDirect(filePath, options);
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null)
            => inner.DeleteFileShell(filePath, uiOption, recycleOption, options);

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null)
            => inner.DeleteDirectoryDirect(directoryPath, recursive, options);

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null)
            => inner.DeleteDirectoryShell(directoryPath, uiOption, recycleOption, options);

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null)
            => inner.SetTimestamps(path, isDirectory, creationTime, lastWriteTime, options);
    }
}
