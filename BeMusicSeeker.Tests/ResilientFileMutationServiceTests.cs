using System;
using System.IO;
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
    private static readonly FileMutationOptions targetOnlyFileMutationOptions = new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly);

    private static readonly FileMutationOptions recursiveDirectoryTreeFileMutationOptions = new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree);

    private readonly ResilientFileMutationService resilientFileMutationService = new ResilientFileMutationService();

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
            DateTime expectedLastWriteTime = new DateTime(2025, 12, 1, 10, 20, 30);

            resilientFileMutationService.SetTimestamps(filePath, isDirectory: false, creationTime: null, lastWriteTime: expectedLastWriteTime, targetOnlyFileMutationOptions);

            DateTime actualLastWriteTime = File.GetLastWriteTime(filePath);
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

            using (FileStream lockedFileStream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                FileMutationException fileMutationException = Assert.ThrowsException<FileMutationException>(() =>
                    resilientFileMutationService.DeleteFileDirect(filePath, targetOnlyFileMutationOptions));

                Assert.AreEqual(FileMutationKind.DeleteFileDirect, fileMutationException.Kind);
                Assert.AreEqual(filePath, fileMutationException.PrimaryPath);
                Assert.IsTrue(fileMutationException.AttemptCount >= 1);
                Assert.IsTrue(fileMutationException.WasRetried);
                Assert.IsInstanceOfType(fileMutationException.RootCause, typeof(Exception));
            }
        });
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
        FileAttributes currentAttributes = File.GetAttributes(fileSystemPath);
        File.SetAttributes(fileSystemPath, currentAttributes | FileAttributes.ReadOnly);
    }

    private static void DeleteDirectoryIfExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (string childFilePath in Directory.EnumerateFiles(directoryPath, "*", System.IO.SearchOption.AllDirectories))
        {
            File.SetAttributes(childFilePath, FileAttributes.Normal);
        }

        foreach (string childDirectoryPath in Directory.EnumerateDirectories(directoryPath, "*", System.IO.SearchOption.AllDirectories))
        {
            File.SetAttributes(childDirectoryPath, FileAttributes.Normal);
        }

        File.SetAttributes(directoryPath, FileAttributes.Normal);
        Directory.Delete(directoryPath, recursive: true);
    }
}
