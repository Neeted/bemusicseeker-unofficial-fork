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

            StartupSettingsSnapshot snapshot = StartupSettingsSnapshot.CreateCurrent();

            Assert.AreEqual(tableListUrl, snapshot.TableListURL);
            Assert.IsTrue(snapshot.IsLR2BackupEnabled);
            Assert.AreEqual(Backup.Target.Config | Backup.Target.ScoreDB, snapshot.LR2BackupTarget);
            Assert.AreEqual("backup-root", snapshot.LR2BackupPath);
            Assert.AreEqual(7, snapshot.LR2BackupSpan);
            Assert.AreEqual(4, snapshot.LR2BackupNum);
            Assert.IsTrue(snapshot.SkipInitPlaylistLoad);
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

            StartupSettingsSnapshot snapshot = StartupSettingsSnapshot.CreateCurrent();

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
