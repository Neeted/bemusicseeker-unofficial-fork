using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StartupSettingsSnapshotTests
{
    [TestMethod]
    public void CreateCurrentCapturesStartupTailSettingsAsOneSnapshot()
    {
        Uri previousTableListUrl = Settings.Default.TableListURL;
        bool previousBackupEnabled = Settings.Default.IsLR2BackupEnabled;
        Backup.Target previousBackupTarget = Settings.Default.LR2BackupTarget;
        string previousBackupPath = Settings.Default.LR2BackupPath;
        int previousBackupSpan = Settings.Default.LR2BackupSpan;
        int previousBackupNum = Settings.Default.LR2BackupNum;
        bool previousSkipInitPlaylistLoad = Settings.Default.SkipInitPlaylistLoad;
        bool previousStartupSelectInstallPending = Settings.Default.StartupSelectInstallPending;
        bool previousShowDuplicateFileCheckConfirmMsg = Settings.Default.ShowDuplicateFileCheckConfirmMsg;
        bool previousUseBeatorajaScoreDb = Settings.Default.UseBeatorajaScoreDb;
        string previousBeatorajaRootPath = Settings.Default.BeatorajaRootPath;
        string previousBeatorajaPlayerId = Settings.Default.BeatorajaPlayerId;
        string previousBeatorajaScoreDbPath = Settings.Default.BeatorajaScoreDbPath;
        try
        {
            Uri tableListUrl = new("https://example.invalid/startup-table-list");
            Settings.Default.TableListURL = tableListUrl;
            Settings.Default.IsLR2BackupEnabled = true;
            Settings.Default.LR2BackupTarget = Backup.Target.Config | Backup.Target.ScoreDB;
            Settings.Default.LR2BackupPath = "backup-root";
            Settings.Default.LR2BackupSpan = 7;
            Settings.Default.LR2BackupNum = 4;
            Settings.Default.SkipInitPlaylistLoad = true;
            Settings.Default.StartupSelectInstallPending = true;
            Settings.Default.ShowDuplicateFileCheckConfirmMsg = true;
            Settings.Default.UseBeatorajaScoreDb = true;
            Settings.Default.BeatorajaRootPath = "beatoraja-root";
            Settings.Default.BeatorajaPlayerId = "player-id";
            Settings.Default.BeatorajaScoreDbPath = "beatoraja-score.db";

            StartupSettingsSnapshot snapshot = StartupSettingsSnapshot.CreateCurrent(Settings.Default);

            Assert.AreEqual(tableListUrl, snapshot.TableListURL);
            Assert.IsTrue(snapshot.IsLR2BackupEnabled);
            Assert.AreEqual(Backup.Target.Config | Backup.Target.ScoreDB, snapshot.LR2BackupTarget);
            Assert.AreEqual("backup-root", snapshot.LR2BackupPath);
            Assert.AreEqual(7, snapshot.LR2BackupSpan);
            Assert.AreEqual(4, snapshot.LR2BackupNum);
            Assert.IsTrue(snapshot.SkipInitPlaylistLoad);
            Assert.IsTrue(snapshot.StartupSelectInstallPending);
            Assert.IsTrue(snapshot.ShowDuplicateFileCheckConfirmMsg);
            Assert.IsTrue(snapshot.UseBeatorajaScoreDb);
            Assert.AreEqual("beatoraja-root", snapshot.BeatorajaRootPath);
            Assert.AreEqual("player-id", snapshot.BeatorajaPlayerId);
            Assert.AreEqual("beatoraja-score.db", snapshot.BeatorajaScoreDbPath);
        }
        finally
        {
            Settings.Default.TableListURL = previousTableListUrl;
            Settings.Default.IsLR2BackupEnabled = previousBackupEnabled;
            Settings.Default.LR2BackupTarget = previousBackupTarget;
            Settings.Default.LR2BackupPath = previousBackupPath;
            Settings.Default.LR2BackupSpan = previousBackupSpan;
            Settings.Default.LR2BackupNum = previousBackupNum;
            Settings.Default.SkipInitPlaylistLoad = previousSkipInitPlaylistLoad;
            Settings.Default.StartupSelectInstallPending = previousStartupSelectInstallPending;
            Settings.Default.ShowDuplicateFileCheckConfirmMsg = previousShowDuplicateFileCheckConfirmMsg;
            Settings.Default.UseBeatorajaScoreDb = previousUseBeatorajaScoreDb;
            Settings.Default.BeatorajaRootPath = previousBeatorajaRootPath;
            Settings.Default.BeatorajaPlayerId = previousBeatorajaPlayerId;
            Settings.Default.BeatorajaScoreDbPath = previousBeatorajaScoreDbPath;
        }
    }

    [TestMethod]
    public void CreateCurrentReadsStandaloneRootsThroughSettingsAdapter()
    {
        string previousStandaloneRoots = Settings.Default.StandaloneBmsRootPaths;
        string previousLegacyRoot = Settings.Default.BMSRootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "StartupSettingsSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.StandaloneBmsRootPaths = tempDirectory;
            Settings.Default.BMSRootPath = null;

            StartupSettingsSnapshot snapshot = StartupSettingsSnapshot.CreateCurrent(Settings.Default);

            Assert.IsTrue(snapshot.StandaloneBmsRootPaths.Contains(tempDirectory, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Settings.Default.StandaloneBmsRootPaths = previousStandaloneRoots;
            Settings.Default.BMSRootPath = previousLegacyRoot;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
