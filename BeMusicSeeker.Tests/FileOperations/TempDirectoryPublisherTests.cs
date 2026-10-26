using System;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TempDirectoryPublisherTests
{
    /// <summary>非Gregorian文化でも固定UTC形式のsession名で管理ディレクトリを作り、実在・所有判定と回収を確認する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
    [DataTestMethod]
    [DoNotParallelize]
    [DataRow("th-TH")]
    public void GetCreatesManagedDirectory(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            string directoryPath = TempDirectoryPublisher.Get("unit-test");

            try
            {
                Assert.IsTrue(Directory.Exists(directoryPath));
                Assert.IsTrue(TempDirectoryPublisher.IsManagedPath(directoryPath));
                string sessionName = Path.GetFileName(Path.GetDirectoryName(directoryPath)) ?? throw new AssertFailedException();
                StringAssert.Matches(sessionName, new System.Text.RegularExpressions.Regex(@"^session-[0-9]{17}-[0-9]+-[a-f0-9]{32}$"));
                var sessionTime = DateTime.ParseExact(sessionName.Substring(8, 17), "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                Assert.IsTrue(sessionTime <= DateTime.UtcNow, "管理名はGregorianのUTC日時を使います。");
                StringAssert.Contains(directoryPath, Path.Combine(Path.GetTempPath(), "BeMusicSeeker"));
            }
            finally
            {
                TempDirectoryPublisher.TryDeleteManagedPath(directoryPath);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
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
