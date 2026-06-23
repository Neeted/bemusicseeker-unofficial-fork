using System;
using System.IO;
using System.Text;
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
}
