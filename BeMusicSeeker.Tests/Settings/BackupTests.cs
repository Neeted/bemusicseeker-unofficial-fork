using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Components.BackupTestFixture;

namespace BeMusicSeeker.Tests;

/// <remarks>世代の公開と回収、長いパス、共有ロックは実ファイル操作が必要です。各ケースは専用領域を回収し、SQLite 整理は小さい実 DB で確認します。</remarks>
[TestClass]
public sealed class BackupTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>Score自身と深い子孫の内向き・外向きジャンクションを保存失敗として拒否し、先にコピーした設定も公開せず、旧世代・リンク・先の内容を保持してstageを回収します。</summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [DataRow("score-root")]
    [DataRow("descendant-inside")]
    [DataRow("descendant-outside")]
    [TestCategory("EnvironmentIntegration")]
    public void SaveSelectedBackupsWithResult_DirectoryLinkFailsBeforePublication(string scenario)
    {
        WithSelectedBackupTree(CaseWorkspace, (root, config, song, score) =>
        {
            string[] old = CreateOldGenerations(root);
            string? scoreParent = Path.GetDirectoryName(score);
            Assert.IsNotNull(scoreParent);
            string target = scenario == "descendant-inside" ? Path.Combine(score, "target") : Path.Combine(scoreParent, "external");
            Directory.CreateDirectory(target);
            string sentinel = Path.Combine(target, "sentinel.db");
            WriteAllText(sentinel, "target-content");
            string link = scenario == "score-root" ? score : Path.Combine(score, "deep", "child", "link");
            if (scenario == "score-root")
            {
                Directory.Delete(score, recursive: true);
            }
            string? linkParent = Path.GetDirectoryName(link);
            Assert.IsNotNull(linkParent);
            Directory.CreateDirectory(linkParent);
            try
            {
                CreateDirectoryJunction(link, target);
                Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(root, TimeSpan.Zero, 1,
                    Backup.Target.Config | Backup.Target.ScoreDB, config, song, score);

                Assert.IsFalse(result.Saved);
                Assert.IsNotNull(result.FailureException);
                Assert.AreEqual(string.Format(Properties.Resources.Error_DirectoryLinkCopyNotAllowedFormat, link), result.FailureException.Message);
                Assert.AreEqual(0, result.Warnings.Count);
                AssertOldGenerations(old);
                CollectionAssert.AreEquivalent(old, Directory.GetDirectories(root));
                Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                Assert.AreEqual("target-content", ReadAllText(sentinel));
            }
            finally
            {
                // 再帰清掃にリンク先を渡さず、リンク自体を先に除去します。
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(link);
                }
            }
        });
    }

    /// <summary>選択済みScoreのジャンクションも同日・間隔未到達なら辿らず省略し、未選択リンクは設定だけの保存を妨げません。</summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [DataRow("today")]
    [DataRow("not-due")]
    [DataRow("unselected")]
    [TestCategory("EnvironmentIntegration")]
    public void SaveSelectedBackupsWithResult_DirectoryLinkDoesNotAffectSkippedOrUnselectedScore(string scenario)
    {
        WithSelectedBackupTree(CaseWorkspace, (root, config, song, score) =>
        {
            string link = Path.Combine(score, "link");
            string? scoreParent = Path.GetDirectoryName(score);
            Assert.IsNotNull(scoreParent);
            string target = Path.Combine(scoreParent, "external");
            Directory.CreateDirectory(target);
            WriteAllText(Path.Combine(target, "sentinel.db"), "target-content");
            string? existing = scenario == "unselected" ? null : Path.Combine(root,
                DateTime.Today.AddDays(scenario == "today" ? 0 : -1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (existing != null)
            {
                Directory.CreateDirectory(existing);
                WriteAllText(Path.Combine(existing, "sentinel.db"), "existing-content");
            }
            try
            {
                CreateDirectoryJunction(link, target);
                Backup.BackupSaveResult result = Backup.SaveSelectedBackupsWithResult(root, TimeSpan.FromDays(7), 1,
                    scenario == "unselected" ? Backup.Target.Config : Backup.Target.ScoreDB, config, song, score);

                Assert.AreEqual(scenario == "unselected", result.Saved);
                Assert.IsNull(result.FailureException);
                Assert.AreEqual(0, result.Warnings.Count);
                Assert.AreEqual(1, Directory.GetDirectories(root).Length);
                if (existing != null)
                {
                    Assert.AreEqual("existing-content", ReadAllText(Path.Combine(existing, "sentinel.db")));
                }
                else
                {
                    CollectionAssert.AreEquivalent(new[] { "config.xml" }, Directory.GetFileSystemEntries(TodayPath(root)).Select(Path.GetFileName).ToArray());
                    Assert.AreEqual("config-content", ReadAllText(Path.Combine(TodayPath(root), "config.xml")));
                }
                Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
                Assert.AreEqual("target-content", ReadAllText(Path.Combine(target, "sentinel.db")));
            }
            finally
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link);
                }
            }
        });
    }

    private static void CreateDirectoryJunction(string link, string target)
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
    }
}
