using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TempDirectoryPublisherTests
{
    /// <summary>取得した作業ディレクトリがアプリtemp配下で実在して管理対象と認識されることを確認し、作成と所有判定の不一致を検出する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
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

    /// <summary>管理された実ディレクトリ内のファイルとディレクトリだけを削除し、所有対象の回収漏れを検出する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
    [TestMethod]
    public void TryDeleteManagedPathDeletesOnlyManagedPaths()
    {
        string directoryPath = TempDirectoryPublisher.Get("unit-test-delete");
        try
        {
            string filePath = Path.Combine(directoryPath, "archive.zip");
            File.WriteAllText(filePath, "temporary archive");

            bool deletedFile = TempDirectoryPublisher.TryDeleteManagedPath(filePath);
            bool deletedDirectory = TempDirectoryPublisher.TryDeleteManagedPath(directoryPath);

            Assert.IsTrue(deletedFile);
            Assert.IsTrue(deletedDirectory);
            Assert.IsFalse(File.Exists(filePath));
            Assert.IsFalse(Directory.Exists(directoryPath));
        }
        finally
        {
            TempDirectoryPublisher.TryDeleteManagedPath(directoryPath);
        }

    }

    /// <summary>アプリtemp配下でも利用者が作ったファイルは管理外として保持し、場所だけを根拠に誤削除する危険を検出する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
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

    /// <summary>session風の名前でも管理markerのない実ディレクトリを保持し、名前だけから所有を推定して誤削除する危険を検出する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
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
