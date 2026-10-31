using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LongPathFileSystemTests
{
    /// <summary>正規化済み包含はdrive/UNCrootと末尾区切りを保持し、要素境界のない接頭辞兄弟を含めません。実ドライブ・共有の列挙は行いません。</summary>
    [DataTestMethod]
    [DataRow(@"D:\Songs\a.bms", @"D:\", true)]
    [DataRow(@"d:\Songs\a.bms", @"D:\Songs\", true)]
    [DataRow(@"D:\Songs2\a.bms", @"D:\Songs", false)]
    [DataRow(@"\\server\share\Songs\a.bmson", @"\\server\share\", true)]
    [DataRow(@"\\server\share2\Songs\a.bmson", @"\\server\share", false)]
    public void NormalizedDirectoryContainment_RespectsRootAndElementBoundaries(string candidate, string ancestor, bool expected)
        => Assert.AreEqual(expected, LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(candidate, ancestor));

    /// <summary>既定のコピー呼出しでは安全な非循環ジャンクションを従来どおり辿り、通常ディレクトリとして内容をコピーして元リンクと先を保持します。</summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void CopyDirectory_DefaultFollowsNonCyclicDirectoryLink()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(LongPathFileSystemTests), Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        string destination = Path.Combine(root, "destination");
        string link = Path.Combine(source, "link");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "sentinel.txt"), "target-content");
        try
        {
            PowerShellTestProcess.Run(
                    PowerShellTestProcess.FindRepositoryRoot(),
                    "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:BMS_TEST_JUNCTION_PATH -Target $env:BMS_TEST_JUNCTION_TARGET | Out-Null",
                    "directory junction fixture",
                    new Dictionary<string, string>
                    {
                        ["BMS_TEST_JUNCTION_PATH"] = link,
                        ["BMS_TEST_JUNCTION_TARGET"] = target
                    });
            Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.AreEqual(target, new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true)?.FullName, ignoreCase: true);
            Assert.AreEqual(link, LongPathFileSystem.ConfirmDirectoryPathSpelling(Path.Combine(root, "SOURCE", "LINK")), "リンク登録位置を最終リンク先で置換しません。");
            LongPathFileSystem.CopyDirectory(source, destination, overwrite: false);

            Assert.AreEqual("target-content", File.ReadAllText(Path.Combine(destination, "link", "sentinel.txt")));
            Assert.IsFalse((File.GetAttributes(Path.Combine(destination, "link")) & FileAttributes.ReparsePoint) != 0);
            Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.AreEqual("target-content", File.ReadAllText(Path.Combine(target, "sentinel.txt")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
