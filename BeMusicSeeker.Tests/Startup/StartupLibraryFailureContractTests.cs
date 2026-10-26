using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.StartupLibraryConstructionTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class StartupLibraryFailureContractTests
{
    /// <summary>
    /// 保証：作成先がnullまたは例外の場合は途中組だけを停止して旧組と共有受付を維持し、後の成功時だけ旧組を停止する。
    /// </summary>
    /// <remarks>本番のモデル組を作り、失敗時と次の成功時の実停止状態・新規受付の成立まで観測する。</remarks>
    [TestMethod]
    public void StartupLibraryConstructionOwner_PropagatesFactoryNullAndExceptions()
    {
        LibraryProfile profile = CreateProfile("startup-owner-null", "startup-owner-null.db");
        var nullFactory = new NullStartupLibraryFactory();
        var nullOwner = new StartupLibraryConstructionOwner(nullFactory);
        var nullApplication = new RecordingStartupLibraryApplicationPort([]);

        Assert.ThrowsException<InvalidOperationException>(() => nullOwner.CreateAndApply(profile, nullApplication));
        Assert.AreEqual(1, nullFactory.LibraryCallCount);
        Assert.AreEqual(0, nullFactory.PlaylistCallCount);

        string temporaryRoot = CreateTemporaryRoot();
        string songDbPath = Path.Combine(temporaryRoot, "song.db");
        CreateSongDatabase(songDbPath);
        try
        {
            ApplicationComposition composition = CreateComposition();
            var events = new List<string>();
            var factory = new RecordingDelegatingStartupLibraryFactory(composition, events)
            {
                ReturnNullPlaylist = false
            };
            var application = new RecordingStartupLibraryApplicationPort(events);
            var owner = new StartupLibraryConstructionOwner(factory);
            profile = CreateProfile(temporaryRoot, songDbPath);
            StartupLibraryServices previous = owner.CreateAndApply(profile, application);
            events.Clear();
            application = new RecordingStartupLibraryApplicationPort(events);
            factory.ReturnNullPlaylist = true;

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application, previousLibrary: previous.Library, previousPlaylist: previous.Playlist));

            StringAssert.Contains(exception.Message, "Startup playlist factory returned null");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "factory-playlist" },
                events);
            Assert.IsNull(application.AttachedLibrary);
            Assert.IsTrue((factory.CreatedLibrary ?? throw new InvalidOperationException("Construction did not create the library.")).IsShutdownRequested);
            Assert.IsFalse(previous.Library.IsShutdownRequested);
            Assert.IsFalse(previous.Playlist.IsShutdownRequested);
            Assert.IsFalse(composition.OperationAdmission.IsAdmissionClosed);
            Assert.IsFalse(composition.PlaylistOperationAdmission.IsAdmissionClosed);

            events.Clear();
            factory.ReturnNullPlaylist = false;
            factory.ThrowOnLibrary = true;
            exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application, previousLibrary: previous.Library, previousPlaylist: previous.Playlist));
            StringAssert.Contains(exception.Message, "recording library factory failure");
            CollectionAssert.AreEqual(new[] { "factory-library" }, events);

            events.Clear();
            factory.ThrowOnLibrary = false;
            factory.ReturnNullPlaylist = false;
            factory.ThrowOnPlaylist = true;
            exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application, previousLibrary: previous.Library, previousPlaylist: previous.Playlist));
            StringAssert.Contains(exception.Message, "recording playlist failure");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "factory-playlist" },
                events);
            Assert.IsFalse(previous.Library.IsShutdownRequested);
            Assert.IsFalse(previous.Playlist.IsShutdownRequested);
            factory.ThrowOnPlaylist = false;
            StartupLibraryServices current = owner.CreateAndApply(profile, application, previousLibrary: previous.Library, previousPlaylist: previous.Playlist);
            Assert.IsTrue(previous.Library.IsShutdownRequested);
            Assert.IsTrue(previous.Playlist.IsShutdownRequested);
            Assert.IsFalse(current.Library.IsShutdownRequested);
            Assert.IsFalse(current.Playlist.IsShutdownRequested);
            Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable libraryLease));
            libraryLease.Dispose();
            Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable playlistLease));
            playlistLease.Dispose();
            current.Playlist.RequestStop("test_cleanup");
            current.Library.RequestStop("test_cleanup");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>
    /// 保証：入れ子の不正score.dbの最適化失敗は保存維持の警告を一回返し、保存失敗通知へ変換しません。世代削除警告も共存し、公開bytesと元Scoreを保持します。
    /// </summary>
    /// <remarks>起動ownerから実保存・再帰列挙・SQLite最適化を通し、通常テストの専用FSと小さい実DBで通知の生産と公開内容を確認します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrepareBackupAsync_OptimizationFailurePreservesPublishedScoreBytesWithoutSaveFailureNotification(bool pruneFailure)
    {
        string temporaryRoot = CreateTemporaryRoot();
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
            CreateSongDatabase(songDbPath);
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

            IReadOnlyList<UiMessageRequest> notifications = await StartupLibraryConstructionOwner.PrepareBackupAsync(settings);

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

    /// <summary>正常な実song・score DBは保存後に最適化して警告を出さず、同日保存済みなら不正SQLiteでも最適化せず公開済み・元の内容を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PrepareBackupAsync_SuccessAndSameDaySkipDoNotWarn(bool alreadySavedToday)
    {
        string root = CreateTemporaryRoot();
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
                CreateSongDatabase(song);
                using var scoreDb = new LR2ScoreDBExtended(score);
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

            IReadOnlyList<UiMessageRequest> notifications = await StartupLibraryConstructionOwner.PrepareBackupAsync(settings);

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
                using var scoreDb = new LR2ScoreDBExtended(score);
                Assert.AreEqual("score-content", scoreDb.ExecuteScalar<string>("SELECT value FROM user_data;"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 保証：ライブラリ適用・サービス適用の各失敗はその段階の例外で返し、後段へ進まず、先に適用済みの事実は保持する。
    /// </summary>
    /// <remarks>本番の作成へ委譲し、適用窓口の失敗と呼出し順・受信済みモデルを確認する。</remarks>
    [TestMethod]
    public void StartupLibraryConstructionOwner_PropagatesApplicationExceptions()
    {
        string temporaryRoot = CreateTemporaryRoot();
        string songDbPath = Path.Combine(temporaryRoot, "song.db");
        CreateSongDatabase(songDbPath);
        try
        {
            LibraryProfile profile = CreateProfile(temporaryRoot, songDbPath);
            ApplicationComposition composition = CreateComposition();

            var libraryEvents = new List<string>();
            var libraryFactory = new RecordingDelegatingStartupLibraryFactory(composition, libraryEvents);
            var libraryApplication = new RecordingStartupLibraryApplicationPort(libraryEvents)
            {
                ThrowOnLibrary = true
            };
            InvalidOperationException libraryException = Assert.ThrowsException<InvalidOperationException>(
                () => new StartupLibraryConstructionOwner(libraryFactory).CreateAndApply(profile, libraryApplication));
            StringAssert.Contains(libraryException.Message, "recording library application failure");
            CollectionAssert.AreEqual(new[] { "factory-library", "factory-playlist", "application-library" }, libraryEvents);

            var servicesEvents = new List<string>();
            var servicesFactory = new RecordingDelegatingStartupLibraryFactory(composition, servicesEvents);
            var servicesApplication = new RecordingStartupLibraryApplicationPort(servicesEvents)
            {
                ThrowOnServices = true
            };
            InvalidOperationException servicesException = Assert.ThrowsException<InvalidOperationException>(
                () => new StartupLibraryConstructionOwner(servicesFactory).CreateAndApply(profile, servicesApplication));
            StringAssert.Contains(servicesException.Message, "recording services application failure");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "factory-playlist", "application-library", "application-services" },
                servicesEvents);
            Assert.AreSame(servicesFactory.CreatedLibrary, servicesApplication.AttachedLibrary);
            Assert.AreSame(servicesFactory.CreatedPlaylist, servicesApplication.AttachedServices?.Playlist);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

}
