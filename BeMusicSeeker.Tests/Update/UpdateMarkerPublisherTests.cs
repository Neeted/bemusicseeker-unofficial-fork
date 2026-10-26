using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateMarkerPublisherTests
{
    /// <summary>既存readyは元のバイト列を保って拒否し、decisionは完成内容へ置換する。両方で旧固定一時名と他writerの一時物を保護し、自身の一時物を残さない。</summary>
    [DataTestMethod]
    [DataRow(false, "updater-ready.txt", "1")]
    [DataRow(true, "updater-decision.txt", "proceed")]
    public void Publish_ExistingMarkerHonorsReplacementPolicyAndPreservesOtherTemporaryFiles(
        bool overwrite, string fileName, string contents)
    {
        WithTemporaryDirectory(root =>
        {
            string finalPath = Path.Combine(root, fileName);
            byte[] original = [0xEF, 0xBB, 0xBF, 0x32];
            File.WriteAllBytes(finalPath, original);
            string[] otherPaths = CreateOtherTemporaryFiles(finalPath);
            byte[] bytes = Encoding.UTF8.GetBytes(contents);

            void Publish() => UpdateMarkerPublisher.Publish(
                finalPath, overwrite, stream => stream.Write(bytes, 0, bytes.Length));
            if (overwrite)
            {
                Publish();
            }
            else
            {
                Assert.ThrowsException<IOException>(Publish);
            }

            CollectionAssert.AreEqual(overwrite ? bytes : original, File.ReadAllBytes(finalPath));
            foreach (string path in otherPaths)
            {
                Assert.AreEqual("other writer", File.ReadAllText(path));
            }
            CollectionAssert.AreEquivalent(otherPaths.Append(finalPath).ToArray(), Directory.GetFiles(root));
        });
    }

    /// <summary>実一時ファイルの部分出力後にproducerが失敗しても同じ例外を返し、完成名・旧固定一時名・他writerを保護して自身の一時物だけ回収する。</summary>
    [TestMethod]
    public void Publish_ProducerFailurePreservesOriginalExceptionAndRemovesOnlyOwnedTemporaryFile()
    {
        WithTemporaryDirectory(root =>
        {
            string finalPath = Path.Combine(root, "updater-ready.txt");
            File.WriteAllText(finalPath, "previous marker");
            string[] otherPaths = CreateOtherTemporaryFiles(finalPath);
            var expected = new InvalidOperationException("producer failed after partial output");
            string? ownedPath = null;

            InvalidOperationException actual = Assert.ThrowsException<InvalidOperationException>(() =>
                UpdateMarkerPublisher.Publish(finalPath, overwrite: false, stream =>
                {
                    stream.WriteByte((byte)'1');
                    ownedPath = Directory.GetFiles(root).Except(otherPaths.Append(finalPath)).Single();
                    Assert.IsFalse(otherPaths.Contains(ownedPath));
                    throw expected;
                }));

            Assert.AreSame(expected, actual);
            Assert.IsNotNull(ownedPath);
            Assert.IsFalse(File.Exists(ownedPath));
            Assert.AreEqual("previous marker", File.ReadAllText(finalPath));
            foreach (string path in otherPaths)
            {
                Assert.AreEqual("other writer", File.ReadAllText(path));
            }
            CollectionAssert.AreEquivalent(otherPaths.Append(finalPath).ToArray(), Directory.GetFiles(root));
        });
    }

    /// <summary>所有する実一時ファイルをreadonlyにして削除失敗を成立させても、producerの同じ例外を保ち、所有一時物を残して完成名を公開しない。</summary>
    [TestMethod]
    public void Publish_TemporaryCleanupFailurePreservesOriginalExceptionAndLeavesOwnedFile()
    {
        WithTemporaryDirectory(root =>
        {
            string? ownedPath = null;
            string finalPath = Path.Combine(root, "updater-ready.txt");
            var expected = new InvalidOperationException("producer failed before publication");

            InvalidOperationException actual = Assert.ThrowsException<InvalidOperationException>(() =>
                UpdateMarkerPublisher.Publish(finalPath, overwrite: false, stream =>
                {
                    stream.WriteByte((byte)'1');
                    ownedPath = Directory.GetFiles(root).Single();
                    File.SetAttributes(ownedPath, FileAttributes.ReadOnly);
                    throw expected;
                }));

            Assert.AreSame(expected, actual);
            Assert.IsNotNull(ownedPath);
            Assert.IsTrue(File.Exists(ownedPath), "The real readonly cleanup fault must leave the owned temporary file.");
            Assert.IsTrue(File.GetAttributes(ownedPath).HasFlag(FileAttributes.ReadOnly));
            Assert.ThrowsException<UnauthorizedAccessException>(() => File.Delete(ownedPath));
            Assert.IsFalse(File.Exists(finalPath));
        });
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_UpdateMarkerPublisherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? primaryFailure = null;
        try
        {
            action(root);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            try
            {
                // readonly故障も含め、このケースが所有する実ファイルの属性を戻してから回収します。
                foreach (string path in Directory.GetFiles(root))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["DirectorySecondaryDiagnostics: " + root] = cleanupFailure.ToString();
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private static string[] CreateOtherTemporaryFiles(string finalPath)
    {
        string[] paths = [finalPath + ".tmp", finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp"];
        foreach (string path in paths)
        {
            File.WriteAllText(path, "other writer");
        }
        return paths;
    }
}
