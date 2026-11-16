using System.Globalization;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.StartupLibraryConstructionTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class StartupLibraryFailureContractTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 保証：入れ子の不正score.dbの最適化失敗は保存維持の警告を一回返し、保存失敗通知へ変換しません。世代削除警告も共存し、公開bytesと元Scoreを保持します。
    /// </summary>
    /// <remarks>起動ownerから実保存・再帰列挙・SQLite最適化を通し、通常テストの専用FSと小さい実DBで通知の生産と公開内容を確認します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrepareBackupAsync_OptimizationFailurePreservesPublishedScoreBytesWithoutSaveFailureNotification(bool pruneFailure)
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string temporaryRoot = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_StartupLibraryOwner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        string songDbPath = Path.Combine(temporaryRoot, "song.db");
        string scoreDirectoryPath = Path.Combine(temporaryRoot, "LR2files", "Database", "Score");
        string nestedScoreDirectoryPath = Path.Combine(scoreDirectoryPath, "player", "nested");
        string scoreDbPath = Path.Combine(nestedScoreDirectoryPath, "score.db");
        string backupRoot = Path.Combine(temporaryRoot, "backups");
        string oldGeneration = Path.Combine(backupRoot, DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        string oldFile = Path.Combine(oldGeneration, "sentinel.db");
        byte[] scoreBytes = [110, 111, 116, 45, 97, 45, 115, 113, 108, 105, 116, 101, 45, 100, 98];
        try
        {
            CreateSongDatabase(databaseScope, songDbPath);
            Directory.CreateDirectory(nestedScoreDirectoryPath);
            Directory.CreateDirectory(backupRoot);
            File.WriteAllBytes(scoreDbPath, scoreBytes);
            if (pruneFailure)
            {
                Directory.CreateDirectory(oldGeneration);
                File.WriteAllText(oldFile, "old-content");
                File.SetAttributes(oldFile, FileAttributes.ReadOnly);
            }
            var settings = new StartupSettingsSnapshot
            {
                OperationModeLR2DB = true,
                IsLR2BackupEnabled = true,
                LR2BackupTarget = Backup.Target.SongDB | Backup.Target.ScoreDB,
                LR2RootPath = temporaryRoot,
                LR2SongDBPath = songDbPath,
                LR2BackupPath = backupRoot,
                LR2BackupSpan = 1,
                LR2BackupNum = 1
            };

            IReadOnlyList<UiMessageRequest> notifications = await StartupLibraryConstructionOwner.PrepareBackupAsync(databaseScope, settings);

            string publishedDirectory = Path.Combine(backupRoot, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Assert.IsTrue(File.Exists(Path.Combine(publishedDirectory, "song.db")));
            CollectionAssert.AreEqual(scoreBytes, File.ReadAllBytes(Path.Combine(publishedDirectory, "Score", "player", "nested", "score.db")));
            CollectionAssert.AreEqual(scoreBytes, File.ReadAllBytes(scoreDbPath));
            Assert.AreEqual(pruneFailure ? 2 : 1, notifications.Count);
            Assert.AreEqual(1, notifications.Count(notification => notification.MessageBoxText == Properties.Resources.Warn_LR2BackupOptimizationIncomplete));
            Assert.IsTrue(notifications.All(notification => notification.Caption == Properties.Resources.Warning));
            if (pruneFailure)
            {
                Assert.AreEqual(1, notifications.Count(notification => notification.MessageBoxText == string.Format(Properties.Resources.Warn_FileOrDirDeleteFailed, oldGeneration)));
                Assert.AreEqual("old-content", File.ReadAllText(oldFile));
            }
        }
        finally
        {
            if (File.Exists(oldFile))
            {
                File.SetAttributes(oldFile, FileAttributes.Normal);
            }
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>
    /// 保証：実効LR2ルートが空の起動時バックアップは、スコアDBのディレクトリを対象から外して song.db の保存を続け、失敗も警告も通知しない。
    /// 対象がスコアDBだけなら何も保存しない。空のルートから相対パスを作り、スコアDBの欠損として保存全体を失敗させる誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(Backup.Target.SongDB | Backup.Target.ScoreDB, true, DisplayName = "song.db とスコアDB")]
    [DataRow(Backup.Target.ScoreDB, false, DisplayName = "スコアDBだけ")]
    public async Task PrepareBackupAsync_EmptyEffectiveRootExcludesOnlyScoreDirectory(Backup.Target target, bool songSaved)
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_StartupLibraryOwner_" + Guid.NewGuid().ToString("N"));
        string song = Path.Combine(root, "data", "song.db");
        string backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(Path.GetDirectoryName(song)!);
        Directory.CreateDirectory(backups);
        CreateSongDatabase(databaseScope, song);
        var settings = new StartupSettingsSnapshot
        {
            OperationModeLR2DB = true,
            IsLR2BackupEnabled = true,
            LR2BackupTarget = target,
            LR2RootPath = string.Empty,
            LR2SongDBPath = song,
            LR2BackupPath = backups,
            LR2BackupSpan = 1,
            LR2BackupNum = 1
        };

        IReadOnlyList<UiMessageRequest> notifications = await StartupLibraryConstructionOwner.PrepareBackupAsync(databaseScope, settings);

        Assert.AreEqual(0, notifications.Count);
        string today = Path.Combine(backups, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (songSaved)
        {
            CollectionAssert.AreEqual(new[] { Path.Combine(today, "song.db") }, Directory.GetFileSystemEntries(today));
        }
        else
        {
            Assert.AreEqual(0, Directory.GetFileSystemEntries(backups).Length);
        }
    }

    /// <summary>正常な実song・score DBは保存後に最適化して警告を出さず、同日保存済みなら不正SQLiteでも最適化せず公開済み・元の内容を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrepareBackupAsync_SuccessAndSameDaySkipDoNotWarn(bool alreadySavedToday)
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_StartupLibraryOwner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string song = Path.Combine(root, "song.db");
        string scoreDirectory = Path.Combine(root, "LR2files", "Database", "Score");
        string score = Path.Combine(scoreDirectory, "score.db");
        string backups = Path.Combine(root, "backups");
        string today = Path.Combine(backups, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(scoreDirectory);
            Directory.CreateDirectory(backups);
            if (alreadySavedToday)
            {
                File.WriteAllText(song, "invalid-song");
                File.WriteAllText(score, "invalid-score");
                Directory.CreateDirectory(today);
                File.WriteAllText(Path.Combine(today, "sentinel.db"), "published-content");
            }
            else
            {
                CreateSongDatabase(databaseScope, song);
                using LR2ScoreDBExtended scoreDb = databaseScope.OpenScoreWrite(score);
                scoreDb.Execute("CREATE TABLE user_data(value TEXT);");
                scoreDb.Execute("INSERT INTO user_data VALUES ('score-content');");
            }
            byte[] songBytes = File.ReadAllBytes(song);
            byte[] scoreBytes = File.ReadAllBytes(score);
            var settings = new StartupSettingsSnapshot
            {
                OperationModeLR2DB = true,
                IsLR2BackupEnabled = true,
                LR2BackupTarget = Backup.Target.SongDB | Backup.Target.ScoreDB,
                LR2RootPath = root,
                LR2SongDBPath = song,
                LR2BackupPath = backups,
                LR2BackupSpan = 1,
                LR2BackupNum = 1
            };

            IReadOnlyList<UiMessageRequest> notifications = await StartupLibraryConstructionOwner.PrepareBackupAsync(databaseScope, settings);

            Assert.AreEqual(0, notifications.Count);
            Assert.AreEqual(1, Directory.GetDirectories(backups).Length);
            if (alreadySavedToday)
            {
                CollectionAssert.AreEqual(songBytes, File.ReadAllBytes(song));
                CollectionAssert.AreEqual(scoreBytes, File.ReadAllBytes(score));
                Assert.AreEqual("published-content", File.ReadAllText(Path.Combine(today, "sentinel.db")));
                Assert.AreEqual(1, Directory.GetFileSystemEntries(today).Length);
            }
            else
            {
                CollectionAssert.AreEqual(songBytes, File.ReadAllBytes(Path.Combine(today, "song.db")));
                CollectionAssert.AreEqual(scoreBytes, File.ReadAllBytes(Path.Combine(today, "Score", "score.db")));
                using LR2ScoreDBExtended scoreDb = databaseScope.OpenScoreWrite(score);
                Assert.AreEqual("score-content", scoreDb.ExecuteScalar<string>("SELECT value FROM user_data;"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
