using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BackupTests
{
    [TestMethod]
    public void SaveBackups_CopiesLongPathFileAndDirectory()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BackupTests", Guid.NewGuid().ToString("N"));
        LongPathFileSystem.CreateDirectory(tempDirectory);
        try
        {
            string backupRoot = BuildLongDirectoryPath(tempDirectory, "backups");
            string sourceRoot = BuildLongDirectoryPath(tempDirectory, "source");
            string nestedSourceDirectory = Path.Combine(sourceRoot, "nested");
            LongPathFileSystem.CreateDirectory(backupRoot);
            LongPathFileSystem.CreateDirectory(nestedSourceDirectory);
            string songDbPath = Path.Combine(sourceRoot, "song.db");
            string scoreDbPath = Path.Combine(nestedSourceDirectory, "score.db");
            WriteAllText(songDbPath, "song");
            WriteAllText(scoreDbPath, "score");

            bool saved = Backup.SaveBackups(backupRoot, TimeSpan.Zero, 1, [songDbPath, nestedSourceDirectory]);

            string backupDirectory = Path.Combine(backupRoot, DateTime.Today.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo));
            Assert.IsTrue(saved);
            Assert.IsTrue(LongPathFileSystem.FileExists(Path.Combine(backupDirectory, "song.db")));
            Assert.IsTrue(LongPathFileSystem.FileExists(Path.Combine(backupDirectory, "nested", "score.db")));
            Assert.AreEqual("song", ReadAllText(Path.Combine(backupDirectory, "song.db")));
            Assert.AreEqual("score", ReadAllText(Path.Combine(backupDirectory, "nested", "score.db")));
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempDirectory))
            {
                LongPathFileSystem.DeleteDirectory(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void SaveBackupsWithResult_ReturnsFailureException_WhenDestinationIsMissing()
    {
        string missingBackupRoot = Path.Combine(Path.GetTempPath(), "BackupTests", Guid.NewGuid().ToString("N"));

        Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(missingBackupRoot, TimeSpan.Zero, 1, ["dummy.db"]);

        Assert.IsFalse(result.Saved);
        Assert.IsInstanceOfType<DirectoryNotFoundException>(result.FailureException);
        Assert.AreEqual(0, result.Warnings.Count);
    }

    [TestMethod]
    public void SaveBackups_RethrowsFailureException_ForLegacyCallers()
    {
        string missingBackupRoot = Path.Combine(Path.GetTempPath(), "BackupTests", Guid.NewGuid().ToString("N"));

        Assert.ThrowsException<DirectoryNotFoundException>(() => Backup.SaveBackups(missingBackupRoot, TimeSpan.Zero, 1, ["dummy.db"]));
    }

    // B5-LR2-BACKUP-1: each case owns its files, locks and attributes; return is completion.
    [TestMethod]
    public void SaveBackupsWithResult_CopyFailurePreservesOldGenerationsAndRemovesPartialCopy()
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);
            string locked = Path.Combine(Path.GetDirectoryName(source)!, "locked.db");
            WriteAllText(locked, "locked-source");
            using FileStream fileLock = LongPathFileSystem.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source, locked]);

            Assert.IsFalse(result.Saved);
            Assert.IsNotNull(result.FailureException);
            AssertOldGenerations(old);
            Assert.IsFalse(Directory.Exists(TodayPath(root)));
            CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
            Assert.AreEqual(0, result.Warnings.Count);
        });
    }

    [TestMethod]
    public void SaveBackupsWithResult_PublicationFileCollisionPreservesExistingContents()
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);
            WriteAllText(TodayPath(root), "competitor");

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source]);

            Assert.IsFalse(result.Saved);
            Assert.IsNotNull(result.FailureException);
            AssertOldGenerations(old);
            Assert.AreEqual("competitor", ReadAllText(TodayPath(root)));
            CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
        });
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SaveBackupsWithResult_MissingSelectedSourceFailsWholeSave(bool includeValidSource)
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);
            string missing = Path.Combine(Path.GetDirectoryName(source)!, "missing.db");
            string[] sources = includeValidSource ? [source, missing] : [missing];

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, sources);

            Assert.IsFalse(result.Saved);
            Assert.IsNotNull(result.FailureException);
            AssertOldGenerations(old);
            CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
        });
    }

    [TestMethod]
    public void SaveBackupsWithResult_NullOrEmptySelectionDoesNothing()
    {
        WithBackupTree((root, source) =>
        {
            foreach (string[] sources in new string[][] { null!, [] })
            {
                Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, sources);
                Assert.IsFalse(result.Saved);
                Assert.IsNull(result.FailureException);
                Assert.AreEqual(0, result.Warnings.Count);
                Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length);
            }
        });
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void SaveBackupsWithResult_RetentionIncludesNewGeneration(int generationCount)
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, generationCount, [source]);

            Assert.IsTrue(result.Saved);
            Assert.IsNull(result.FailureException);
            Assert.AreEqual(0, result.Warnings.Count);
            Assert.AreEqual(generationCount, Directory.GetDirectories(root).Length);
            Assert.AreEqual("source-content", ReadAllText(Path.Combine(TodayPath(root), "song.db")));
            for (int index = 0; index < old.Length; index++)
            {
                Assert.AreEqual(index < generationCount - 1, Directory.Exists(old[index]));
                if (index < generationCount - 1)
                {
                    Assert.AreEqual("old-content", ReadAllText(Path.Combine(old[index], "sentinel.db")));
                }
            }
        });
    }

    [TestMethod]
    public void SaveBackupsWithResult_PruneFailureWarnsAfterSuccessfulPublication()
    {
        WithBackupTree((root, source) =>
        {
            string old = CreateOldGenerations(root)[0];
            File.SetAttributes(Path.Combine(old, "sentinel.db"), FileAttributes.ReadOnly);

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source]);

            Assert.IsTrue(result.Saved);
            Assert.IsNull(result.FailureException);
            Assert.AreEqual("source-content", ReadAllText(Path.Combine(TodayPath(root), "song.db")));
            Assert.AreEqual("old-content", ReadAllText(Path.Combine(old, "sentinel.db")));
            Assert.IsTrue(result.Warnings.Any(warning => warning.Contains(old, StringComparison.Ordinal)));
            Assert.AreEqual(2, Directory.GetDirectories(root).Length);
        });
    }

    [TestMethod]
    public void SaveBackupsWithResult_CleanupFailurePreservesPrimaryAndLeavesStageIgnoredOnRetry()
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);
            File.SetAttributes(source, FileAttributes.ReadOnly);
            string locked = Path.Combine(Path.GetDirectoryName(source)!, "locked.db");
            WriteAllText(locked, "locked-source");
            Backup.BackupSaveResult failed;
            using (FileStream fileLock = LongPathFileSystem.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                failed = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source, locked]);
            }

            Assert.IsFalse(failed.Saved);
            Assert.IsNotNull(failed.FailureException);
            // The primary failure identifies the locked source, not the read-only staging copy.
            StringAssert.Contains(failed.FailureException.Message, locked);
            AssertOldGenerations(old);
            Assert.IsFalse(Directory.Exists(TodayPath(root)));
            string stage = Directory.GetDirectories(root).Except(old).Single();
            Assert.IsTrue(failed.Warnings.Any(warning => warning.Contains(stage, StringComparison.Ordinal)));
            Assert.AreEqual("source-content", ReadAllText(Path.Combine(stage, "song.db")));

            Backup.BackupSaveResult retried = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source, locked]);

            Assert.IsTrue(retried.Saved);
            Assert.IsNull(retried.FailureException);
            Assert.AreEqual(0, retried.Warnings.Count);
            Assert.AreEqual("locked-source", ReadAllText(Path.Combine(TodayPath(root), "locked.db")));
            Assert.AreEqual("source-content", ReadAllText(Path.Combine(stage, "song.db")));
            CollectionAssert.AreEquivalent(new[] { stage, TodayPath(root) }, Directory.GetDirectories(root));
        });
    }

    [TestMethod]
    public void SaveBackupsWithResult_SameDaySaveSkipsWithoutOverwriting()
    {
        WithBackupTree((root, source) =>
        {
            Assert.IsTrue(Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source]).Saved);
            WriteAllText(source, "changed-source");

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source]);

            Assert.IsFalse(result.Saved);
            Assert.IsNull(result.FailureException);
            Assert.AreEqual("source-content", ReadAllText(Path.Combine(TodayPath(root), "song.db")));
            Assert.AreEqual(1, Directory.GetDirectories(root).Length);
        });
    }

    [TestMethod]
    public void SaveBackupsWithResult_NotDueLeavesGenerationsUntouched()
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);
            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.FromDays(7), 1, [source]);
            Assert.IsFalse(result.Saved);
            Assert.IsNull(result.FailureException);
            AssertOldGenerations(old);
            CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
        });
    }

    [TestMethod]
    public void PublicationGateway_DirectoryCollisionDoesNotMergeOrOverwrite()
    {
        WithBackupTree((root, source) =>
        {
            string staged = Path.Combine(root, "candidate");
            Directory.CreateDirectory(staged);
            WriteAllText(Path.Combine(staged, "sentinel.db"), "candidate");
            Directory.CreateDirectory(TodayPath(root));
            WriteAllText(Path.Combine(TodayPath(root), "sentinel.db"), "competitor");

            Assert.ThrowsException<IOException>(() => LongPathFileSystem.MoveDirectory(staged, TodayPath(root), overwrite: false));

            Assert.AreEqual("competitor", ReadAllText(Path.Combine(TodayPath(root), "sentinel.db")));
            Assert.AreEqual("candidate", ReadAllText(Path.Combine(staged, "sentinel.db")));
        });
    }

    // B5-LR2-SELECTION-1 / S1: 選択済み Score の欠落は保存失敗として旧世代を保持する。
    [TestMethod]
    public void SaveSelectedBackupsWithResult_ConfigAndMissingScoreFailsAndPreservesOldGenerations()
    {
        WithSelectedBackupTree((root, configPath, songDbPath, scoreDirectoryPath) =>
        {
            string[] old = CreateOldGenerationsWithScore(root);
            LongPathFileSystem.DeleteDirectory(scoreDirectoryPath, recursive: true);

            Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(
                root,
                TimeSpan.Zero,
                1,
                Backup.Target.Config | Backup.Target.ScoreDB,
                configPath,
                songDbPath,
                scoreDirectoryPath);

            Assert.IsFalse(result.Saved);
            Assert.IsNotNull(result.FailureException);
            Assert.IsFalse(Directory.Exists(TodayPath(root)));
            AssertOldGenerations(old);
            AssertOldScoreGenerations(old);
            CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
        });
    }

    // B5-LR2-SELECTION-1 / S2: Score だけを選択して欠落した場合も no-op にしない。
    [TestMethod]
    public void SaveSelectedBackupsWithResult_MissingScoreOnlyFailsAndPreservesOldGenerations()
    {
        WithSelectedBackupTree((root, configPath, songDbPath, scoreDirectoryPath) =>
        {
            string[] old = CreateOldGenerationsWithScore(root);
            LongPathFileSystem.DeleteDirectory(scoreDirectoryPath, recursive: true);

            Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(
                root,
                TimeSpan.Zero,
                1,
                Backup.Target.ScoreDB,
                configPath,
                songDbPath,
                scoreDirectoryPath);

            Assert.IsFalse(result.Saved);
            Assert.IsNotNull(result.FailureException);
            Assert.IsFalse(Directory.Exists(TodayPath(root)));
            AssertOldGenerations(old);
            AssertOldScoreGenerations(old);
            CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
        });
    }

    // B5-LR2-SELECTION-1 / S3: 選択フラグごとの対象集合と内容を実保存で確認する。
    [TestMethod]
    [DataRow((int)Backup.Target.Config, "config.xml")]
    [DataRow((int)Backup.Target.SongDB, "song.db")]
    [DataRow((int)Backup.Target.ScoreDB, "Score")]
    [DataRow((int)Backup.Target.All, "config.xml|song.db|Score")]
    public void SaveSelectedBackupsWithResult_FlagsSelectOnlyConfiguredSources(int rawTargets, string expectedRootEntries)
    {
        WithSelectedBackupTree((root, configPath, songDbPath, scoreDirectoryPath) =>
        {
            Backup.Target targets = (Backup.Target)rawTargets;
            Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(
                root,
                TimeSpan.Zero,
                1,
                targets,
                configPath,
                songDbPath,
                scoreDirectoryPath);

            Assert.IsTrue(result.Saved);
            Assert.IsNull(result.FailureException);
            string today = TodayPath(root);
            string[] expectedEntries = expectedRootEntries.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (expectedEntries.Contains("config.xml", StringComparer.Ordinal))
            {
                Assert.AreEqual("config-content", ReadAllText(Path.Combine(today, Path.GetFileName(configPath))));
            }
            if (expectedEntries.Contains("song.db", StringComparer.Ordinal))
            {
                Assert.AreEqual("song-content", ReadAllText(Path.Combine(today, Path.GetFileName(songDbPath))));
            }
            if (expectedEntries.Contains("Score", StringComparer.Ordinal))
            {
                Assert.AreEqual("score-content", ReadAllText(Path.Combine(today, "Score", "score.db")));
            }

            CollectionAssert.AreEquivalent(expectedEntries, Directory.GetFileSystemEntries(today).Select(Path.GetFileName).ToArray());
        });
    }

    // B5-LR2-SELECTION-1 / S3: 未選択の欠落は選択対象の保存を妨げない。
    [TestMethod]
    public void SaveSelectedBackupsWithResult_UnselectedMissingScoreDoesNotBlockConfigAndSongDB()
    {
        WithSelectedBackupTree((root, configPath, songDbPath, scoreDirectoryPath) =>
        {
            LongPathFileSystem.DeleteDirectory(scoreDirectoryPath, recursive: true);

            Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(
                root,
                TimeSpan.Zero,
                1,
                Backup.Target.Config | Backup.Target.SongDB,
                configPath,
                songDbPath,
                scoreDirectoryPath);

            Assert.IsTrue(result.Saved);
            Assert.IsNull(result.FailureException);
            string today = TodayPath(root);
            CollectionAssert.AreEquivalent(
                new[] { Path.GetFileName(configPath), Path.GetFileName(songDbPath) },
                Directory.GetFileSystemEntries(today).Select(Path.GetFileName).ToArray());
            Assert.AreEqual("config-content", ReadAllText(Path.Combine(today, Path.GetFileName(configPath))));
            Assert.AreEqual("song-content", ReadAllText(Path.Combine(today, Path.GetFileName(songDbPath))));
            Assert.IsFalse(Directory.Exists(Path.Combine(today, Path.GetFileName(scoreDirectoryPath))));
        });
    }

    // B5-LR2-SELECTION-1 / S4: 新世代を含む保持数と全対象の内容を確認する。
    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void SaveSelectedBackupsWithResult_AllTargetsRetainNewGeneration(int generationCount)
    {
        WithSelectedBackupTree((root, configPath, songDbPath, scoreDirectoryPath) =>
        {
            string[] old = CreateOldGenerations(root);

            Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(
                root,
                TimeSpan.Zero,
                generationCount,
                Backup.Target.All,
                configPath,
                songDbPath,
                scoreDirectoryPath);

            Assert.IsTrue(result.Saved);
            Assert.IsNull(result.FailureException);
            Assert.AreEqual(generationCount, Directory.GetDirectories(root).Length);
            string today = TodayPath(root);
            Assert.AreEqual("config-content", ReadAllText(Path.Combine(today, "config.xml")));
            Assert.AreEqual("song-content", ReadAllText(Path.Combine(today, "song.db")));
            Assert.AreEqual("score-content", ReadAllText(Path.Combine(today, "Score", "score.db")));
            for (int index = 0; index < old.Length; index++)
            {
                Assert.AreEqual(index < generationCount - 1, Directory.Exists(old[index]));
                if (index < generationCount - 1)
                {
                    Assert.AreEqual("old-content", ReadAllText(Path.Combine(old[index], "sentinel.db")));
                }
            }
        });
    }

    // B5-LR2-SELECTION-1 / S5: None は対象パスの状態にかかわらず保存しない。
    [TestMethod]
    public void SaveSelectedBackupsWithResult_NoneDoesNotSave()
    {
        WithSelectedBackupTree((root, configPath, songDbPath, scoreDirectoryPath) =>
        {
            string[] old = CreateOldGenerationsWithScore(root);

            Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(
                root,
                TimeSpan.Zero,
                1,
                Backup.Target.None,
                configPath,
                songDbPath,
                scoreDirectoryPath);

            Assert.IsFalse(result.Saved);
            Assert.IsNull(result.FailureException);
            Assert.AreEqual(0, result.Warnings.Count);
            Assert.IsFalse(Directory.Exists(TodayPath(root)));
            AssertOldGenerations(old);
            AssertOldScoreGenerations(old);
            CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
        });
    }

    private static void WithBackupTree(Action<string, string> test)
    {
        string tree = Path.Combine(Path.GetTempPath(), "BackupTests", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(tree, "backups");
        Directory.CreateDirectory(root);
        string source = Path.Combine(tree, "song.db");
        try
        {
            WriteAllText(source, "source-content");
            test(root, source);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(tree, recursive: true);
        }
    }

    private static void WithSelectedBackupTree(Action<string, string, string, string> test)
    {
        WithBackupTree((root, songDbPath) =>
        {
            string tree = Path.GetDirectoryName(songDbPath)!;
            string configPath = Path.Combine(tree, "config.xml");
            string scoreDirectoryPath = Path.Combine(tree, "Score");
            Directory.CreateDirectory(scoreDirectoryPath);
            WriteAllText(configPath, "config-content");
            WriteAllText(songDbPath, "song-content");
            WriteAllText(Path.Combine(scoreDirectoryPath, "score.db"), "score-content");
            test(root, configPath, songDbPath, scoreDirectoryPath);
        });
    }

    private static string[] CreateOldGenerations(string root)
    {
        string[] generations = new string[4];
        for (int index = 0; index < generations.Length; index++)
        {
            string path = Path.Combine(root, DateTime.Today.AddDays(-index - 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(path);
            WriteAllText(Path.Combine(path, "sentinel.db"), "old-content");
            generations[index] = path;
        }
        return generations;
    }

    private static string[] CreateOldGenerationsWithScore(string root)
    {
        string[] generations = CreateOldGenerations(root);
        foreach (string generation in generations)
        {
            string scoreDirectory = Path.Combine(generation, "Score");
            Directory.CreateDirectory(scoreDirectory);
            WriteAllText(Path.Combine(scoreDirectory, "old-score.db"), "old-score-content");
        }
        return generations;
    }

    private static void AssertOldGenerations(string[] generations)
    {
        foreach (string generation in generations)
        {
            Assert.IsTrue(Directory.Exists(generation), generation);
            Assert.AreEqual("old-content", ReadAllText(Path.Combine(generation, "sentinel.db")));
        }
    }

    private static void AssertOldScoreGenerations(string[] generations)
    {
        foreach (string generation in generations)
        {
            Assert.AreEqual("old-score-content", ReadAllText(Path.Combine(generation, "Score", "old-score.db")));
        }
    }

    private static string TodayPath(string root) => Path.Combine(root, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static string ReadAllText(string path)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteAllText(string path, string contents)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(contents);
    }

    private static string BuildLongDirectoryPath(string root, string leaf)
    {
        string path = root;
        while (Path.Combine(path, leaf).Length <= 270)
        {
            path = Path.Combine(path, "segment-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        }
        return Path.Combine(path, leaf);
    }
}
