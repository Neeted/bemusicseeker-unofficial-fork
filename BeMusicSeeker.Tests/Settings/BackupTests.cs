using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <remarks>世代の公開と回収、長いパス、共有ロックは実ファイル操作が必要です。各ケースは専用領域を回収し、SQLite 整理は小さい実 DB で確認します。</remarks>
[TestClass]
public sealed class BackupTests
{
    /// <summary>実song・score SQLiteのVACUUMとREINDEX後に表・索引定義と値、対象外ファイルを保持し、本番DB窓口からの次保存を新readonly接続で確認します。DBの物理bytesや容量は固定しません。</summary>
    [TestMethod]
    public void RebuildDatabase_PreservesRealSongAndScoreSchemaValuesAndAllowsNextSave()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(BackupTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string songPath = Path.Combine(root, "song.db");
        string scorePath = Path.Combine(root, "score.db");
        string unrelated = Path.Combine(root, "unrelated.bin");
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        try
        {
            File.WriteAllBytes(unrelated, [2, 4, 9]);
            using (var song = new LR2SongDBExtended(songPath))
            {
                song.RunInTransaction(() =>
                {
                    song.CreateTable<LR2SongDB.song>();
                    song.CreateTable<LR2SongDB.folder>();
                    song.Execute("CREATE INDEX normal_song_title ON song(title);");
                    song.Execute("CREATE TABLE user_data(key TEXT PRIMARY KEY, value TEXT);");
                    song.Execute("INSERT INTO user_data VALUES ('custom', 'kept');");
                    song.Execute("INSERT INTO song(path, hash, title, artist) VALUES (?, ?, 'before', 'artist');", Path.Combine(root, "chart.bms"), hash);
                });
            }
            using (var score = new LR2ScoreDBExtended(scorePath))
            {
                score.RunInTransaction(() =>
                {
                    score.CreateTable<LR2ScoreDB.score>();
                    score.Execute("CREATE UNIQUE INDEX normal_score_hash ON score(hash);");
                    score.Execute("CREATE TABLE user_data(key TEXT PRIMARY KEY, value TEXT);");
                    score.Execute("INSERT INTO user_data VALUES ('custom', 'score-kept');");
                    score.Execute("INSERT INTO score(hash, perfect, great, totalnotes, playcount) VALUES (?, 10, 3, 20, 7);", hash);
                });
            }
            string ReadSchema(SQLiteConnection db) => db.ExecuteScalar<string>(
                "SELECT group_concat(type || ':' || name || ':' || coalesce(sql, ''), char(10)) FROM (SELECT type, name, sql FROM sqlite_schema WHERE type IN ('table', 'index') ORDER BY type, name);");
            string songSchema;
            string scoreSchema;
            using (var song = new LR2SongDBExtended(songPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false)) { songSchema = ReadSchema(song); }
            using (var score = new LR2ScoreDBExtended(scorePath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false)) { scoreSchema = ReadSchema(score); }

            Assert.IsTrue(Backup.RebuildDatabase(songPath, [scorePath]));

            using (var song = new LR2SongDBExtended(songPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(songSchema, ReadSchema(song));
                Assert.AreEqual("before", song.ExecuteScalar<string>("SELECT title FROM song WHERE hash = ?;", hash));
                Assert.AreEqual("artist", song.ExecuteScalar<string>("SELECT artist FROM song WHERE hash = ?;", hash));
                Assert.AreEqual("kept", song.ExecuteScalar<string>("SELECT value FROM user_data WHERE key = 'custom';"));
            }
            using (var score = new LR2ScoreDBExtended(scorePath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(scoreSchema, ReadSchema(score));
                LR2ScoreDB.score row = score.Table<LR2ScoreDB.score>().Single();
                Assert.AreEqual(hash, row.hash);
                Assert.AreEqual(10, row.perfect);
                Assert.AreEqual(3, row.great);
                Assert.AreEqual(20, row.totalnotes);
                Assert.AreEqual(7, row.playcount);
                Assert.AreEqual("score-kept", score.ExecuteScalar<string>("SELECT value FROM user_data WHERE key = 'custom';"));
            }
            using (var song = new LR2SongDBExtended(songPath)) { song.Execute("UPDATE song SET title = 'after' WHERE hash = ?;", hash); }
            using (var score = new LR2ScoreDBExtended(scorePath)) { score.Execute("UPDATE score SET playcount = 8 WHERE hash = ?;", hash); }
            using (var song = new LR2SongDBExtended(songPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual("after", song.ExecuteScalar<string>("SELECT title FROM song WHERE hash = ?;", hash));
                Assert.AreEqual(songSchema, ReadSchema(song));
            }
            using (var score = new LR2ScoreDBExtended(scorePath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(8, score.Table<LR2ScoreDB.score>().Single().playcount);
                Assert.AreEqual(scoreSchema, ReadSchema(score));
            }
            CollectionAssert.AreEqual(new byte[] { 2, 4, 9 }, File.ReadAllBytes(unrelated));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>長いパスにあるファイルと入れ子のディレクトリを実保存し、内容欠落や通常パス API による保存失敗を検出します。</summary>
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

    /// <summary>保存先がないとき保存失敗と元の欠損分類を返し、成功や警告だけへ置換する誤りを検出します。</summary>
    [TestMethod]
    public void SaveBackupsWithResult_ReturnsFailureException_WhenDestinationIsMissing()
    {
        string missingBackupRoot = Path.Combine(Path.GetTempPath(), "BackupTests", Guid.NewGuid().ToString("N"));

        Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(missingBackupRoot, TimeSpan.Zero, 1, ["dummy.db"]);

        Assert.IsFalse(result.Saved);
        Assert.IsInstanceOfType<DirectoryNotFoundException>(result.FailureException);
        Assert.AreEqual(0, result.Warnings.Count);
    }

    /// <summary>旧 API で保存先欠損を呼出し、結果返却への変更で従来の例外通知を失う誤りを検出します。</summary>
    [TestMethod]
    public void SaveBackups_RethrowsFailureException_ForLegacyCallers()
    {
        string missingBackupRoot = Path.Combine(Path.GetTempPath(), "BackupTests", Guid.NewGuid().ToString("N"));

        Assert.ThrowsException<DirectoryNotFoundException>(() => Backup.SaveBackups(missingBackupRoot, TimeSpan.Zero, 1, ["dummy.db"]));
    }

    /// <summary>後半の対象を排他ロックしてコピーを失敗させ、旧世代の維持と途中コピーの回収を確認します。</summary>
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

    /// <summary>日付世代の公開先に競合ファイルがある場合、競合内容と旧世代を保持して公開前の失敗を返すことを確認します。</summary>
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

    /// <summary>欠損対象を単独または正常対象と混在させ、部分成功で公開したり旧世代を削除する誤りを検出します。</summary>
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

    /// <summary>対象が null と空配列の場合の双方で、保存・警告・ファイル作成をしないことを確認します。</summary>
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

    /// <summary>保持数を一世代と複数世代に指定し、新世代を含めて数え、新しい旧世代から保持することを内容と残存先で確認します。</summary>
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

    /// <summary>最古の旧世代の削除だけを失敗させても他の旧世代を回収し、公開済みbytesと保存成功、正確な失敗先の警告を保持することを確認します。</summary>
    [TestMethod]
    public void SaveBackupsWithResult_PruneFailureWarnsAfterSuccessfulPublication()
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);
            string failedGeneration = old[^1];
            string failedFile = Path.Combine(failedGeneration, "sentinel.db");
            FileAttributes originalAttributes = File.GetAttributes(failedFile);
            try
            {
                File.SetAttributes(failedFile, originalAttributes | FileAttributes.ReadOnly);

                Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source]);

                Assert.IsTrue(result.Saved);
                Assert.IsNull(result.FailureException);
                CollectionAssert.AreEqual(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(TodayPath(root), "song.db")));
                Assert.AreEqual("old-content", ReadAllText(failedFile));
                Assert.AreEqual(1, result.Warnings.Count);
                Assert.AreEqual(string.Format(Properties.Resources.Warn_FileOrDirDeleteFailed, failedGeneration), result.Warnings.Single());
                foreach (string removableGeneration in old.Where(path => path != failedGeneration))
                {
                    Assert.IsFalse(Directory.Exists(removableGeneration), removableGeneration);
                }
                CollectionAssert.AreEquivalent(new[] { failedGeneration, TodayPath(root) }, Directory.GetDirectories(root));
            }
            finally
            {
                if (File.Exists(failedFile))
                {
                    File.SetAttributes(failedFile, originalAttributes);
                }
            }
        });
    }

    /// <summary>コピー失敗後に作業領域も削除できない場合、主原因を保持して残留先を警告し、次保存では残留先を世代として再利用・削除しないことを確認します。</summary>
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
            // 主原因がコピー元のロックを示し、後片付けの失敗で置き換わらないことを確認します。
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

    /// <summary>保存後に元ファイルを変更し未来世代を混在させても、同日は既存世代と未来内容を上書きせず正常に省略します。</summary>
    [TestMethod]
    public void SaveBackupsWithResult_SameDaySaveSkipsWithoutOverwriting()
    {
        WithBackupTree((root, source) =>
        {
            Assert.IsTrue(Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source]).Saved);
            WriteAllText(source, "changed-source");
            string future = Path.Combine(root, DateTime.Today.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(future);
            WriteAllText(Path.Combine(future, "sentinel.db"), "future-content");

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source]);

            Assert.IsFalse(result.Saved);
            Assert.IsNull(result.FailureException);
            Assert.AreEqual("source-content", ReadAllText(Path.Combine(TodayPath(root), "song.db")));
            Assert.AreEqual("future-content", ReadAllText(Path.Combine(future, "sentinel.db")));
            Assert.AreEqual(2, Directory.GetDirectories(root).Length);
        });
    }

    /// <summary>未来世代を除いた最新世代から設定間隔が経過していない場合、過去・未来の内容と集合を保って省略します。</summary>
    [TestMethod]
    public void SaveBackupsWithResult_NotDueLeavesGenerationsUntouched()
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);
            string future = Path.Combine(root, DateTime.Today.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(future);
            WriteAllText(Path.Combine(future, "sentinel.db"), "future-content");
            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.FromDays(7), 1, [source]);
            Assert.IsFalse(result.Saved);
            Assert.IsNull(result.FailureException);
            AssertOldGenerations(old);
            Assert.AreEqual("future-content", ReadAllText(Path.Combine(future, "sentinel.db")));
            CollectionAssert.AreEquivalent(old.Append(future).ToArray(), Directory.GetDirectories(root));
        });
    }

    /// <summary>最終公開のディレクトリ衝突で、競合先と候補の内容を混在・上書きせず例外を返す実ファイル境界を確認します。</summary>
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

    /// <summary>設定と欠損スコアを選択し、欠損対象を除外して部分公開する誤りを旧世代保持と新世代非公開で検出します。</summary>
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

    /// <summary>欠損スコアだけを選択し、対象なしの正常省略へ置換せず旧世代を維持することを確認します。</summary>
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

    /// <summary>設定・楽曲・スコアの個別選択と全選択で実保存し、フラグが要求した対象だけを内容付きで公開することを確認します。</summary>
    [TestMethod]
    [DataRow(Backup.Target.Config, "config.xml")]
    [DataRow(Backup.Target.SongDB, "song.db")]
    [DataRow(Backup.Target.ScoreDB, "Score")]
    [DataRow(Backup.Target.All, "config.xml|song.db|Score")]
    public void SaveSelectedBackupsWithResult_FlagsSelectOnlyConfiguredSources(Backup.Target targets, string expectedRootEntries)
    {
        WithSelectedBackupTree((root, configPath, songDbPath, scoreDirectoryPath) =>
        {
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

    /// <summary>未選択のスコアだけが欠損しても選択した設定・楽曲 DB を保存し、無関係な欠損で全体を失敗させる誤りを検出します。</summary>
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

    /// <summary>選択入口から全対象を一世代または複数世代保持で保存し、対象の配送と新世代を含む保持数の接続を確認します。</summary>
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

    /// <summary>選択フラグなしでは旧世代やスコアを変更せず、新しい世代と警告を作らないことを確認します。</summary>
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

    /// <summary>未来世代を間隔・保持枠・削除から除外し、未来のみでも過去との混在でも今日を保存して内容を保持します。保持数0は1へ補正します。</summary>
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 3)]
    [DataRow(true, 1)]
    [DataRow(true, 3)]
    public void SaveBackupsWithResult_FutureGenerationsDoNotBlockSaveOrConsumeRetention(bool includePast, int generationCount)
    {
        WithBackupTree((root, source) =>
        {
            string[] old = includePast ? CreateOldGenerations(root) : [];
            string future = Path.Combine(root, DateTime.Today.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(future);
            WriteAllText(Path.Combine(future, "sentinel.db"), "future-content");

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, generationCount, [source]);

            Assert.IsTrue(result.Saved);
            Assert.IsNull(result.FailureException);
            Assert.AreEqual(0, result.Warnings.Count);
            Assert.AreEqual("source-content", ReadAllText(Path.Combine(TodayPath(root), "song.db")));
            Assert.AreEqual("future-content", ReadAllText(Path.Combine(future, "sentinel.db")));
            int retainedPast = Math.Min(old.Length, Math.Max(1, generationCount) - 1);
            Assert.AreEqual(retainedPast + 2, Directory.GetDirectories(root).Length);
            for (int index = 0; index < old.Length; index++)
            {
                Assert.AreEqual(index < retainedPast, Directory.Exists(old[index]));
                if (index < retainedPast)
                {
                    Assert.AreEqual("old-content", ReadAllText(Path.Combine(old[index], "sentinel.db")));
                }
            }
        });
    }

    /// <summary>日付形式だけが一致する無効日付は従来どおり解析失敗とし、旧世代と無効名の内容を保持して公開しません。</summary>
    [TestMethod]
    public void SaveBackupsWithResult_InvalidCalendarDateFailsWithoutChangingGenerations()
    {
        WithBackupTree((root, source) =>
        {
            string[] old = CreateOldGenerations(root);
            string invalid = Path.Combine(root, "9999-99-99");
            Directory.CreateDirectory(invalid);
            WriteAllText(Path.Combine(invalid, "sentinel.db"), "invalid-date-content");

            Backup.BackupSaveResult result = Backup.SaveBackupsWithResult(root, TimeSpan.Zero, 1, [source]);

            Assert.IsFalse(result.Saved);
            Assert.IsInstanceOfType<FormatException>(result.FailureException);
            AssertOldGenerations(old);
            Assert.AreEqual("invalid-date-content", ReadAllText(Path.Combine(invalid, "sentinel.db")));
            CollectionAssert.AreEquivalent(old.Append(invalid).ToArray(), Directory.GetDirectories(root));
        });
    }

    /// <summary>Score自身と深い子孫の内向き・外向きジャンクションを保存失敗として拒否し、先にコピーした設定も公開せず、旧世代・リンク・先の内容を保持してstageを回収します。</summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [DataRow("score-root")]
    [DataRow("descendant-inside")]
    [DataRow("descendant-outside")]
    [TestCategory("EnvironmentIntegration")]
    public void SaveSelectedBackupsWithResult_DirectoryLinkFailsBeforePublication(string scenario)
    {
        WithSelectedBackupTree((root, config, song, score) =>
        {
            string[] old = CreateOldGenerations(root);
            string? scoreParent = Path.GetDirectoryName(score);
            Assert.IsNotNull(scoreParent);
            string target = scenario == "descendant-inside" ? Path.Combine(score, "target") : Path.Combine(scoreParent, "external");
            Directory.CreateDirectory(target);
            string sentinel = Path.Combine(target, "sentinel.db");
            WriteAllText(sentinel, "target-content");
            string link = scenario == "score-root" ? score : Path.Combine(score, "deep", "child", "link");
            if (scenario == "score-root")
            {
                Directory.Delete(score, recursive: true);
            }
            string? linkParent = Path.GetDirectoryName(link);
            Assert.IsNotNull(linkParent);
            Directory.CreateDirectory(linkParent);
            try
            {
                CreateDirectoryJunction(link, target);
                Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(root, TimeSpan.Zero, 1,
                    Backup.Target.Config | Backup.Target.ScoreDB, config, song, score);

                Assert.IsFalse(result.Saved);
                Assert.IsNotNull(result.FailureException);
                Assert.AreEqual(string.Format(Properties.Resources.Error_DirectoryLinkCopyNotAllowedFormat, link), result.FailureException.Message);
                Assert.AreEqual(0, result.Warnings.Count);
                AssertOldGenerations(old);
                CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
                Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                Assert.AreEqual("target-content", ReadAllText(sentinel));
            }
            finally
            {
                // 再帰清掃にリンク先を渡さず、リンク自体を先に除去します。
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(link);
                }
            }
        });
    }

    /// <summary>選択済みScoreのジャンクションも同日・間隔未到達なら辿らず省略し、未選択リンクは設定だけの保存を妨げません。</summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [DataRow("today")]
    [DataRow("not-due")]
    [DataRow("unselected")]
    [TestCategory("EnvironmentIntegration")]
    public void SaveSelectedBackupsWithResult_DirectoryLinkDoesNotAffectSkippedOrUnselectedScore(string scenario)
    {
        WithSelectedBackupTree((root, config, song, score) =>
        {
            string link = Path.Combine(score, "link");
            string? scoreParent = Path.GetDirectoryName(score);
            Assert.IsNotNull(scoreParent);
            string target = Path.Combine(scoreParent, "external");
            Directory.CreateDirectory(target);
            WriteAllText(Path.Combine(target, "sentinel.db"), "target-content");
            string? existing = scenario == "unselected" ? null : Path.Combine(root,
                DateTime.Today.AddDays(scenario == "today" ? 0 : -1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (existing != null)
            {
                Directory.CreateDirectory(existing);
                WriteAllText(Path.Combine(existing, "sentinel.db"), "existing-content");
            }
            try
            {
                CreateDirectoryJunction(link, target);
                Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(root, TimeSpan.FromDays(7), 1,
                    scenario == "unselected" ? Backup.Target.Config : Backup.Target.ScoreDB, config, song, score);

                Assert.AreEqual(scenario == "unselected", result.Saved);
                Assert.IsNull(result.FailureException);
                Assert.AreEqual(0, result.Warnings.Count);
                Assert.AreEqual(1, Directory.GetDirectories(root).Length);
                if (existing != null)
                {
                    Assert.AreEqual("existing-content", ReadAllText(Path.Combine(existing, "sentinel.db")));
                }
                else
                {
                    CollectionAssert.AreEquivalent(new[] { "config.xml" }, Directory.GetFileSystemEntries(TodayPath(root)).Select(Path.GetFileName).ToArray());
                    Assert.AreEqual("config-content", ReadAllText(Path.Combine(TodayPath(root), "config.xml")));
                }
                Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                Assert.AreEqual("target-content", ReadAllText(Path.Combine(target, "sentinel.db")));
            }
            finally
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
            }
        });
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        PowerShellTestProcess.Run(
                    PowerShellTestProcess.FindRepositoryRoot(),
                    "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:BMS_TEST_JUNCTION_PATH -Target $env:BMS_TEST_JUNCTION_TARGET | Out-Null",
                    "directory junction fixture",
                    new Dictionary<string, string>
                    {
                        ["BMS_TEST_JUNCTION_PATH"] = link,
                        ["BMS_TEST_JUNCTION_TARGET"] = target
                    });
        Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        Assert.AreEqual(target, new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true)?.FullName, ignoreCase: true);
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
