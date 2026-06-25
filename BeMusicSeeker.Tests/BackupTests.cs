using System;
using System.Globalization;
using System.IO;
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
