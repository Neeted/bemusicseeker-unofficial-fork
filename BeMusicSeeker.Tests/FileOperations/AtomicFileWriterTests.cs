using System;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// AtomicFileWriter の staging、公開、失敗診断を検証します。
/// </summary>
[TestClass]
public sealed class AtomicFileWriterTests
{
    /// <summary>既存宛先への公開が失敗したとき元バイトを保持して所有stageを消し、元例外と対象場所を失う誤りや保存内容の破壊を検出する。</summary>
    /// <remarks>固有ディレクトリの実バイトとstageの寿命を観測し、portの呼出し回数だけで保存を保証しない。</remarks>
    [TestMethod]
    public void Write_WhenExistingDestinationPublishFails_PreservesDestinationAndCleansOwnedStage()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string destinationPath = Path.Combine(tempDirectoryPath, "existing.txt");
            byte[] previousBytes = Encoding.UTF8.GetBytes("previous bytes");
            File.WriteAllBytes(destinationPath, previousBytes);
            string[] siblingPathsBefore = Directory.GetFiles(tempDirectoryPath);
            var publishFailure = new IOException("publish failure");

            IOException failure = Assert.ThrowsException<IOException>(() =>
                AtomicFileWriter.Write(
                    destinationPath,
                    stream => WriteUtf8(stream, "replacement bytes"),
                    (_, _) => throw publishFailure,
                    LongPathFileSystem.DeleteFile));

            Assert.AreSame(publishFailure, failure.InnerException);
            CollectionAssert.AreEqual(previousBytes, File.ReadAllBytes(destinationPath));
            CollectionAssert.AreEquivalent(siblingPathsBefore, Directory.GetFiles(tempDirectoryPath));
            StringAssert.Contains(failure.Message, tempDirectoryPath);
        });
    }

    /// <summary>stageへ一部書いた後の失敗でも既存宛先を保持してstageを回収し、未完成データの公開や残留を検出する。</summary>
    /// <remarks>固有ディレクトリの実バイトとstageの寿命を観測し、portの呼出し回数だけで保存を保証しない。</remarks>
    [TestMethod]
    public void Write_WhenStagingWriteFails_PreservesExistingDestinationAndCleansOwnedStage()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string destinationPath = Path.Combine(tempDirectoryPath, "write-failure.txt");
            byte[] previousBytes = Encoding.UTF8.GetBytes("previous bytes");
            File.WriteAllBytes(destinationPath, previousBytes);
            string[] siblingPathsBefore = Directory.GetFiles(tempDirectoryPath);
            var writeFailure = new IOException("staging write failure");

            IOException failure = Assert.ThrowsException<IOException>(() =>
                AtomicFileWriter.Write(
                    destinationPath,
                    stream =>
                    {
                        WriteUtf8(stream, "partial bytes");
                        throw writeFailure;
                    }));

            Assert.AreSame(writeFailure, failure.InnerException);
            CollectionAssert.AreEqual(previousBytes, File.ReadAllBytes(destinationPath));
            CollectionAssert.AreEquivalent(siblingPathsBefore, Directory.GetFiles(tempDirectoryPath));
        });
    }

    /// <summary>新規宛先への公開が失敗したとき宛先を生成せずstageも回収し、失敗を空の正常ファイルとして残す誤りを検出する。</summary>
    /// <remarks>固有ディレクトリの実バイトとstageの寿命を観測し、portの呼出し回数だけで保存を保証しない。</remarks>
    [TestMethod]
    public void Write_WhenInitialDestinationPublishFails_DoesNotCreateDestination()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string destinationPath = Path.Combine(tempDirectoryPath, "new.txt");
            string[] siblingPathsBefore = Directory.GetFiles(tempDirectoryPath);
            var publishFailure = new IOException("initial publish failure");

            IOException failure = Assert.ThrowsException<IOException>(() =>
                AtomicFileWriter.Write(
                    destinationPath,
                    stream => WriteUtf8(stream, "candidate bytes"),
                    (_, _) => throw publishFailure,
                    LongPathFileSystem.DeleteFile));

            Assert.AreSame(publishFailure, failure.InnerException);
            Assert.IsFalse(LongPathFileSystem.FileExists(destinationPath));
            CollectionAssert.AreEquivalent(siblingPathsBefore, Directory.GetFiles(tempDirectoryPath));
        });
    }

    /// <summary>日本語をUTF8のBOMなしバイトとして実保存し、内容・符号化の変化や成功後のstage残留を検出する。</summary>
    /// <remarks>固有ディレクトリの実バイトとstageの寿命を観測し、portの呼出し回数だけで保存を保証しない。</remarks>
    [TestMethod]
    public void Write_WhenSuccessfulPublishesUtf8BytesAndLeavesNoStage()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string destinationPath = Path.Combine(tempDirectoryPath, "result.txt");
            const string contents = "成功した保存: 日本語";
            string[] siblingPathsBefore = Directory.GetFiles(tempDirectoryPath);

            AtomicFileWriter.Write(
                destinationPath,
                stream => WriteUtf8(stream, contents));

            CollectionAssert.AreEqual(
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents),
                File.ReadAllBytes(destinationPath));
            CollectionAssert.AreEquivalent(
                siblingPathsBefore.Append(destinationPath).ToArray(),
                Directory.GetFiles(tempDirectoryPath));
        });
    }

    /// <summary>公開portが排他でstageを開けることを確認してから実公開し、書込みstreamを開いたまま公開する順序違反を検出する。</summary>
    /// <remarks>固有ディレクトリの実バイトとstageの寿命を観測し、portの呼出し回数だけで保存を保証しない。</remarks>
    [TestMethod]
    public void Write_PublisherObservesClosedStageBeforePublishing()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string destinationPath = Path.Combine(tempDirectoryPath, "closed-before-publish.txt");
            string[] siblingPathsBefore = Directory.GetFiles(tempDirectoryPath);
            bool stageWasClosed = false;

            AtomicFileWriter.Write(
                destinationPath,
                stream => WriteUtf8(stream, "closed first"),
                (stagingPath, targetPath) =>
                {
                    using (FileStream stage = LongPathFileSystem.Open(
                        stagingPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None))
                    {
                        stageWasClosed = true;
                    }
                    LongPathFileSystem.PublishFile(stagingPath, targetPath);
                },
                LongPathFileSystem.DeleteFile);

            Assert.IsTrue(stageWasClosed);
            Assert.AreEqual("closed first", File.ReadAllText(destinationPath, new UTF8Encoding(false)));
            CollectionAssert.AreEquivalent(
                siblingPathsBefore.Append(destinationPath).ToArray(),
                Directory.GetFiles(tempDirectoryPath));
        });
    }

    /// <summary>公開とstage回収の両方を失敗させ、両原因と絶対stageパスを診断へ保持して二次失敗による主因の消失を検出する。</summary>
    /// <remarks>固有ディレクトリの実バイトとstageの寿命を観測し、portの呼出し回数だけで保存を保証しない。</remarks>
    [TestMethod]
    public void Write_WhenPublishAndCleanupFail_RetainsBothCausesAndStagePath()
    {
        WithTemporaryDirectory(tempDirectoryPath =>
        {
            string destinationPath = Path.Combine(tempDirectoryPath, "diagnostics.txt");
            string[] siblingPathsBefore = Directory.GetFiles(tempDirectoryPath);
            var publishFailure = new IOException("primary publish failure");
            var cleanupFailure = new IOException("stage cleanup failure");

            IOException failure = Assert.ThrowsException<IOException>(() =>
                AtomicFileWriter.Write(
                    destinationPath,
                    stream => WriteUtf8(stream, "unpublished"),
                    (_, _) => throw publishFailure,
                    _ => throw cleanupFailure));

            Assert.IsInstanceOfType(failure.InnerException, typeof(AggregateException));
            var causes = (AggregateException)failure.InnerException;
            Assert.IsTrue(causes.InnerExceptions.Contains(publishFailure));
            Assert.IsTrue(causes.InnerExceptions.Contains(cleanupFailure));
            string[] siblingPathsAfter = Directory.GetFiles(tempDirectoryPath);
            string stagingPath = siblingPathsAfter
                .Except(siblingPathsBefore, StringComparer.OrdinalIgnoreCase)
                .Single();
            Assert.IsTrue(Path.IsPathFullyQualified(stagingPath));
            StringAssert.Contains(failure.Message, stagingPath);
            StringAssert.Contains(failure.Message, publishFailure.Message);
            StringAssert.Contains(failure.Message, cleanupFailure.Message);

            LongPathFileSystem.DeleteFile(stagingPath);
            CollectionAssert.AreEquivalent(siblingPathsBefore, Directory.GetFiles(tempDirectoryPath));
        });
    }

    private static void WriteUtf8(Stream stream, string contents)
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WithTemporaryDirectory(Action<string> testAction)
    {
        string tempDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            nameof(AtomicFileWriterTests) + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            testAction(tempDirectoryPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }
}
