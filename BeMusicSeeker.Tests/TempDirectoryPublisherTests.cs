using System;
using System.IO;
using BeMusicSeeker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TempDirectoryPublisherTests
{
    [TestMethod]
    public void GetCreatesManagedDirectory()
    {
        string directoryPath = TempDirectoryPublisher.Get("unit-test");

        try
        {
            Assert.IsTrue(Directory.Exists(directoryPath));
            Assert.IsTrue(TempDirectoryPublisher.IsManagedPath(directoryPath));
            StringAssert.Contains(directoryPath, Path.Combine(Path.GetTempPath(), "BeMusicSeeker"));
        }
        finally
        {
            TempDirectoryPublisher.TryDeleteManagedPath(directoryPath);
        }
    }

    [TestMethod]
    public void TryDeleteManagedPathDeletesOnlyManagedPaths()
    {
        string directoryPath = TempDirectoryPublisher.Get("unit-test-delete");
        string filePath = Path.Combine(directoryPath, "archive.zip");
        File.WriteAllText(filePath, "temporary archive");

        bool deletedFile = TempDirectoryPublisher.TryDeleteManagedPath(filePath);
        bool deletedDirectory = TempDirectoryPublisher.TryDeleteManagedPath(directoryPath);

        Assert.IsTrue(deletedFile);
        Assert.IsTrue(deletedDirectory);
        Assert.IsFalse(File.Exists(filePath));
        Assert.IsFalse(Directory.Exists(directoryPath));
    }

    [TestMethod]
    public void TryDeleteManagedPathDoesNotDeleteManualFileUnderAppTempRoot()
    {
        string managedRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker");
        string manualFilePath = Path.Combine(managedRootPath, "manual-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(managedRootPath);
        File.WriteAllText(manualFilePath, "user owned");

        try
        {
            Assert.IsFalse(TempDirectoryPublisher.IsManagedPath(manualFilePath));
            Assert.IsFalse(TempDirectoryPublisher.TryDeleteManagedPath(manualFilePath));
            Assert.IsTrue(File.Exists(manualFilePath));
        }
        finally
        {
            File.Delete(manualFilePath);
        }
    }

    [TestMethod]
    public void TryDeleteManagedPathDoesNotDeleteManualSessionNamedDirectoryWithoutMarker()
    {
        string managedRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker");
        string manualDirectoryPath = Path.Combine(managedRootPath, "session-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(manualDirectoryPath);
        File.WriteAllText(Path.Combine(manualDirectoryPath, "archive.zip"), "user owned");

        try
        {
            Assert.IsFalse(TempDirectoryPublisher.IsManagedPath(manualDirectoryPath));
            Assert.IsFalse(TempDirectoryPublisher.TryDeleteManagedPath(manualDirectoryPath));
            Assert.IsTrue(Directory.Exists(manualDirectoryPath));
        }
        finally
        {
            Directory.Delete(manualDirectoryPath, recursive: true);
        }
    }
}
