using System;
using System.IO;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationPathPolicyTests
{
    [TestMethod]
    public void SnapshotDerivesAllApplicationPathsFromExecutableDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ApplicationPathPolicyTests), Guid.NewGuid().ToString("N"));
        string executablePath = Path.Combine(root, "BeMusicSeeker.exe");

        ApplicationPathSnapshot snapshot = ApplicationPathSnapshot.FromExecutablePath(executablePath);

        Assert.AreEqual(Path.GetFullPath(executablePath), snapshot.ExecutablePath);
        Assert.AreEqual(Path.GetFullPath(root), snapshot.BaseDirectory);
        Assert.AreEqual(Path.Combine(root, "config"), snapshot.ConfigDirectoryPath);
        Assert.AreEqual(Path.Combine(root, "config", "user.config"), snapshot.UserConfigPath);
        Assert.AreEqual(Path.Combine(root, "data"), snapshot.DataDirectoryPath);
        Assert.AreEqual(Path.Combine(root, "data", "song.db"), snapshot.StandaloneSongDbPath);
        Assert.AreEqual(Path.Combine(root, "lang"), snapshot.LanguageDirectory);
        Assert.AreEqual(Path.Combine(root, "test.mp3"), snapshot.TestSoundPath);
    }

    [TestMethod]
    public void SnapshotRejectsMissingExecutablePath()
    {
        Assert.ThrowsException<ArgumentException>(() => ApplicationPathSnapshot.FromExecutablePath(null));
    }
}
