using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>XMLの採用・保存・限定公開と試聴一時値の境界を実ファイルで検証します。</summary>
[TestClass]
public sealed class Lr2ConfigOwnerTests
{
    /// <summary>外部の変更・破損を取り込まず、実保存では初回全文と今回の登録変更を出力し、未知要素・属性・継続pathの情報を保持します。</summary>
    [TestMethod]
    public void AdoptedDocumentIgnoresExternalChangesAndPreservesUnknownXmlOnSave()
    {
        WithConfig("<config vendor='original'><system><customfolder>7</customfolder><titleflash>12</titleflash></system><player><id>original</id></player><jukebox vendor='jukebox'><unknown value='keep'/><path marker='retained'>A</path></jukebox><future>keep</future></config>", (path, owner) =>
        {
            Lr2ConfigSnapshot first = owner.Capture();
            Assert.AreEqual("original", first.PlayerId);
            Assert.AreEqual(7, first.CustomFolderMask);
            Assert.AreEqual(12, first.TitleFlashHours);
            File.WriteAllText(path, "<broken");
            Assert.AreEqual("original", owner.Capture().PlayerId);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // 同内容の確定は外部修復のために読み書きしません。
                owner.SaveRoots(new Lr2RootChange(first.RegisteredRoots, first.RegisteredRoots));
            }
            string added = Path.Combine(Path.GetDirectoryName(path) ?? throw new InvalidOperationException(), "B");
            owner.SaveRoots(new Lr2RootChange([], [added]));
            var saved = XDocument.Load(path);
            XElement config = saved.Element("config") ?? throw new AssertFailedException();
            Assert.AreEqual("original", config.Attribute("vendor")?.Value);
            Assert.AreEqual("keep", config.Element("future")?.Value);
            XElement jukebox = config.Element("jukebox") ?? throw new AssertFailedException();
            Assert.AreEqual("jukebox", jukebox.Attribute("vendor")?.Value);
            Assert.AreEqual("keep", jukebox.Element("unknown")?.Attribute("value")?.Value);
            Assert.AreEqual("retained", jukebox.Elements("path").Single(value => value.Attribute("marker") != null).Attribute("marker")?.Value);
            Assert.AreEqual("original", config.Element("player")?.Element("id")?.Value);
            Assert.AreEqual(1, first.RegisteredRoots.Count);
            Assert.AreEqual(2, owner.Capture().RegisteredRoots.Count);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<string>)first.RegisteredRoots)[0] = "changed");
        });
    }

    /// <summary>実保存失敗では正本・公開入力を変えず、次の別変更が失敗した登録を巻き込みません。</summary>
    [TestMethod]
    public void FailedSaveDoesNotAdoptDraftOrPublishItThroughLaterChange()
    {
        WithConfig("<config><jukebox><path>A</path></jukebox></config>", (path, owner) =>
        {
            IReadOnlyList<string> original = owner.Capture().RegisteredRoots;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsException<IOException>(() => owner.SaveRoots(new Lr2RootChange([], ["B"])));
            }
            CollectionAssert.AreEqual(original.ToArray(), owner.CaptureSaved().RegisteredRoots.ToArray());
            owner.SaveRoots(new Lr2RootChange([], ["G"]));
            string?[] actual = owner.Capture().RegisteredRoots.Select(Path.GetFileName).ToArray();
            CollectionAssert.AreEquivalent(new[] { "A", "G" }, actual);
            CollectionAssert.AreEquivalent(actual, new LR2Config(path).GetBMSSearchDirectoriesForChangeTracking().Select(Path.GetFileName).ToArray());
        });
    }

    /// <summary>保存済みB（候補修正時C）・公開Aの途中でGだけ削除し、明示公開でも削除を巻き戻しません。登録と配置の捕捉値も不変です。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PartialSavePublishesOnlyTheCompletedOperationAndKeepsCapturedPlacement(bool reviseCandidate)
    {
        WithConfig("<config><jukebox><path>A</path><path>G</path></jukebox></config>", (_, owner) =>
        {
            var oldPlacement = new CustomFolderOutputSettingsSnapshot { LR2CustomFolderOutputBaseDir = "Old" };
            owner.SetPlacement(oldPlacement);
            Lr2ConfigSnapshot oldInput = owner.Capture();
            string a = oldInput.RegisteredRoots.Single(path => Path.GetFileName(path) == "A");
            string g = oldInput.RegisteredRoots.Single(path => Path.GetFileName(path) == "G");
            Lr2RootChange bms = owner.SaveRoots(new Lr2RootChange([a], ["B"]), publish: false);
            string finalRoot = reviseCandidate ? "C" : "B";
            if (reviseCandidate)
            {
                string savedB = owner.CaptureSaved().RegisteredRoots.Single(path => Path.GetFileName(path) == "B");
                bms = bms.Then(owner.SaveRoots(new Lr2RootChange([savedB], ["C"]), publish: false));
            }
            owner.SaveRoots(new Lr2RootChange([g], []));
            CollectionAssert.AreEqual(new[] { "A" }, owner.Capture().RegisteredRoots.Select(Path.GetFileName).ToArray());
            CollectionAssert.AreEqual(new[] { finalRoot }, owner.CaptureSaved().RegisteredRoots.Select(Path.GetFileName).ToArray());
            owner.Publish(bms, new CustomFolderOutputSettingsSnapshot { LR2CustomFolderOutputBaseDir = "New" });
            CollectionAssert.AreEqual(new[] { finalRoot }, owner.Capture().RegisteredRoots.Select(Path.GetFileName).ToArray());
            Assert.AreEqual("Old", oldInput.Placement.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(2, oldInput.RegisteredRoots.Count);
            Assert.AreEqual("New", owner.Capture().Placement.LR2CustomFolderOutputBaseDir);
        });
    }

    /// <summary>overlay中の通常保存は実ファイルだけに試聴値を維持し、初回復元後は再適用せず、欠落親と後続登録を現在正本から復元します。</summary>
    [TestMethod]
    public void PreviewOverlayEndsOnFirstRestoreAndRetainsLaterNormalChanges()
    {
        WithConfig("<config><jukebox /></config>", (path, owner) =>
        {
            var other = new Lr2ConfigOwner(new LR2Config(path));
            Lr2ConfigOwner.PreviewScope scope = owner.BeginPreview();
            Assert.ThrowsException<ArgumentException>(() => other.RestorePreview(scope));
            owner.PublishPreview(scope, 800, 600, true, 75, true);
            owner.SaveRoots(new Lr2RootChange([], ["B"]));
            Assert.AreEqual("800", XDocument.Load(path).Element("config")?.Element("system")?.Element("windowsize_x")?.Value);
            Assert.AreEqual(640, owner.Capture().WindowSizeX);
            Assert.AreEqual(100, owner.Capture().MasterVolume);
            owner.RestorePreview(scope);
            Assert.IsNull(XDocument.Load(path).Element("config")?.Element("system"));
            Assert.IsNull(XDocument.Load(path).Element("config")?.Element("sound"));
            owner.SaveRoots(new Lr2RootChange([], ["C"]));
            Assert.IsNull(XDocument.Load(path).Element("config")?.Element("system"));
            owner.RestorePreview(scope);
            CollectionAssert.AreEquivalent(new[] { "B", "C" }, new LR2Config(path).GetBMSSearchDirectoriesForChangeTracking().Select(Path.GetFileName).ToArray());
            owner.EndPreview(scope);
            Assert.ThrowsException<InvalidOperationException>(() => owner.RestorePreview(scope));
        });
    }

    /// <summary>未採用候補の検証は稼働正本を作らず、採用前の外部破損は次の入力検証で拒否します。採用後は同じ検証済み全文を使います。</summary>
    [TestMethod]
    public void UnadoptedCandidateIsValidatedAgainUntilExplicitAdoption()
    {
        WithConfig("<config><player><id>candidate</id></player><jukebox /></config>", (path, _) =>
        {
            var session = new Lr2ConfigSession();
            Assert.IsTrue(session.TryResolve(path, out Lr2ConfigOwner? candidate, adoptInitial: false));
            File.WriteAllText(path, "<external-broken");
            Assert.IsFalse(session.TryResolve(path, out Lr2ConfigOwner? rejected, adoptInitial: false));
            Assert.IsNull(rejected);
            Assert.IsNotNull(candidate);
            session.Adopt(candidate);
            Assert.IsTrue(session.TryResolve(path, out Lr2ConfigOwner? adopted));
            Assert.IsNotNull(adopted);
            Assert.AreEqual("candidate", adopted.Capture().PlayerId);
        });
    }

    /// <summary>同じ登録を削除して再追加した最終順序を保存・公開し、継続pathの未知属性と兄弟要素を保持します。</summary>
    [TestMethod]
    public void UserRootOrderChangePreservesUnknownPathInformation()
    {
        WithConfig("<config><jukebox><path keep='a'>A</path><unknown keep='middle'/><path keep='b'>B</path></jukebox></config>", (path, owner) =>
        {
            string[] before = owner.Capture().RegisteredRoots.ToArray();
            string[] after = before.Reverse().ToArray();
            owner.SaveUserRoots(after, []);
            owner.PublishUserRoots(before, after);
            CollectionAssert.AreEqual(after, owner.Capture().RegisteredRoots.ToArray());
            XElement jukebox = XDocument.Load(path).Element("config")?.Element("jukebox") ?? throw new AssertFailedException();
            CollectionAssert.AreEqual(new[] { "b", "a" }, jukebox.Elements("path").Select(value => value.Attribute("keep")?.Value).ToArray());
            Assert.AreEqual("middle", jukebox.Elements().ElementAt(1).Attribute("keep")?.Value);
        });
    }

    /// <summary>同じ管理登録のXMLが既に保存済みでも、その登録だけを明示公開し、別BMS操作の未公開値を巻き込みません。</summary>
    [TestMethod]
    public void ManagedRootPublicationDoesNotDependOnAnotherXmlWrite()
    {
        WithConfig("<config><jukebox><path>A</path></jukebox></config>", (path, owner) =>
        {
            string a = owner.Capture().RegisteredRoots.Single();
            owner.SaveRoots(new Lr2RootChange([a], ["B"]), publish: false);
            owner.SaveRoots(new Lr2RootChange([], ["G"]), publish: false);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            { owner.SaveRoots(new Lr2RootChange([], ["G"])); }
            CollectionAssert.AreEquivalent(new[] { "A", "G" }, owner.Capture().RegisteredRoots.Select(Path.GetFileName).ToArray());
            CollectionAssert.AreEquivalent(new[] { "B", "G" }, owner.CaptureSaved().RegisteredRoots.Select(Path.GetFileName).ToArray());
        });
    }

    private static void WithConfig(string xml, Action<string, Lr2ConfigOwner> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "BmsLr2Owner", Guid.NewGuid().ToString("N"));
        string configDirectory = Path.Combine(root, "LR2files", "Config");
        Directory.CreateDirectory(configDirectory);
        string path = Path.Combine(configDirectory, "config.xml");
        try
        {
            File.WriteAllText(path, xml);
            run(path, new Lr2ConfigOwner(new LR2Config(path)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
