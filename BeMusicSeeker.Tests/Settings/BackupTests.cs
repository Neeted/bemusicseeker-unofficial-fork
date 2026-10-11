using System;
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

    /// <summary>旧世代の読取り専用ファイルで削除を失敗させ、公開済み内容を保持し保存成功と対象を示す警告を返すことを確認します。</summary>
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

    /// <summary>保存後に元ファイルを変更して同日に再保存し、既存世代を上書きせず正常な省略を返すことを確認します。</summary>
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

    /// <summary>最新世代から設定間隔が経過していない場合、世代の内容と集合を保って保存を省略することを確認します。</summary>
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
