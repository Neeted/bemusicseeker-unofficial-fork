using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>候補・Windowを介さず、確定登録の保存媒体、限定公開と用途別readerを検証します。</summary>
[TestClass]
public sealed class BmsRootRegistrationTests
{
    /// <summary>standaloneの欠落・親子・順序を保持し、失敗時は旧公開値、成功時だけ次要求の入力へ揃えます。解除は導入先や別項目を書きません。</summary>
    [TestMethod]
    public void StandaloneRegistration_PreservesMissingNestedRootsAndPublishesOnlyAfterSave()
    {
        string directory = NewDirectory();
        try
        {
            string config = Path.Combine(directory, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(config);
            string parent = Path.Combine(directory, "parent");
            string child = Path.Combine(parent, "missing-child");
            string retained = Path.Combine(directory, "retained");
            settings.OperationModeLR2DB = false;
            settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([parent, child, retained]);
            settings.BMSRootPath = parent;
            settings.BMSInstallDir = retained;
            settings.Save();
            var store = new BmsRootRegistrationStore(settings, () => null);
            IReadOnlyList<string> before = store.CaptureUserRoots();
            CollectionAssert.AreEqual(new[] { parent, child, retained }, before.ToArray());
            using (File.Open(config, FileMode.Open, FileAccess.Read, FileShare.Read))
            { Assert.ThrowsException<PortableSettingsException>(() => store.SaveUserRoots([retained])); }
            CollectionAssert.AreEqual(before.ToArray(), store.CaptureUserRoots().ToArray());
            settings.Commit((nameof(Settings.StartupSortCachePrewarmPriority), 3));
            store.SaveUserRoots([retained]);
            CollectionAssert.AreEqual(new[] { retained }, store.CaptureUserRoots().ToArray());
            CollectionAssert.AreEqual(new[] { parent, child, retained }, before.ToArray(), "受理済み入力は後発公開で差し替わりません。");
            Settings fresh = PortableSettingsPersistenceTests.OpenSettings(config);
            Assert.AreEqual(retained, fresh.BMSRootPath);
            Assert.AreEqual(retained, fresh.BMSInstallDir);
            Assert.AreEqual(3, fresh.StartupSortCachePrewarmPriority);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>LR2ユーザーreaderは欠落と順序を保持し、公開配置の全管理領域を除きます。走査は通常出力を含む別の集合を維持します。</summary>
    [TestMethod]
    public void Lr2Registration_ClassifiesPublishedPlacementWithoutChangingScanRoots()
    {
        string directory = NewDirectory();
        try
        {
            string user = Directory.CreateDirectory(Path.Combine(directory, "user")).FullName;
            string missing = Path.Combine(directory, "missing");
            string normal = Directory.CreateDirectory(Path.Combine(directory, "normal")).FullName;
            string additional = Directory.CreateDirectory(Path.Combine(directory, "additional")).FullName;
            string root = Directory.CreateDirectory(Path.Combine(directory, "root-output")).FullName;
            string normalChild = Directory.CreateDirectory(Path.Combine(normal, "child")).FullName;
            string sibling = Directory.CreateDirectory(Path.Combine(directory, "normal2")).FullName;
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(directory, "user.config"));
            settings.OperationModeLR2DB = true;
            settings.LR2CustomFolderOutputBaseDir = normal;
            settings.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additional]);
            settings.LR2CustomFolderOutputBaseDirRootType = root;
            LR2Config xml = BmsPlaylistTestSupport.CreateLr2Config(directory, user, missing, normal, normalChild, additional, root, sibling);
            settings.LR2ConfigXmlPath = xml.ConfigFilePath;
            var owner = new Lr2ConfigOwner(xml);
            var store = new BmsRootRegistrationStore(settings, () => owner);
            CollectionAssert.AreEqual(new[] { user, missing, sibling }, store.CaptureUserRoots().ToArray());
            Lr2SearchRootSnapshot scan = new Lr2SearchRootSnapshotOwner(owner,
                () => BmsLibraryOptionsSnapshot.CreateCurrent(settings).WithLr2Input(owner.Capture())).Capture();
            Assert.IsTrue(scan.Roots.Any(path => Same(path, normal)));
            Assert.IsFalse(scan.Roots.Any(path => Same(path, additional) || Same(path, root)));
            Assert.IsTrue(scan.RequestedRoots.Any(path => Same(path, missing)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>実XML/configの片側失敗、最新別入力・同値入力の明示再保存を共通保存主体で保証します。UIの候補捕捉と差分終端は既存Settings接続へ分担します。</summary>
    [DataTestMethod]
    [DataRow("xml-failure")]
    [DataRow("latest-input")]
    [DataRow("same-input")]
    [DataRow("runtime-equal")]
    public void Lr2Registration_ExplicitSaveResavesLatestInputAcrossMediaFailures(string scenario)
    {
        string directory = NewDirectory();
        try
        {
            string original = Directory.CreateDirectory(Path.Combine(directory, "original")).FullName;
            string first = Directory.CreateDirectory(Path.Combine(directory, "first")).FullName;
            string latest = Directory.CreateDirectory(Path.Combine(directory, "latest")).FullName;
            LR2Config xml = BmsPlaylistTestSupport.CreateLr2Config(directory, original);
            var document = XDocument.Load(xml.ConfigFilePath);
            document.Root!.Add(new XElement("unknown", "preserved"));
            document.Save(xml.ConfigFilePath);
            string configPath = Path.Combine(directory, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(configPath);
            settings.OperationModeLR2DB = true;
            settings.LR2ConfigXmlPath = xml.ConfigFilePath;
            settings.BMSInstallDir = original;
            settings.Save();
            var owner = new Lr2ConfigOwner(new LR2Config(xml.ConfigFilePath));
            var store = new BmsRootRegistrationStore(settings, () => owner);
            byte[] originalConfig = File.ReadAllBytes(configPath);
            using (File.Open(scenario == "xml-failure" ? xml.ConfigFilePath : configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? error = null;
                try { store.SaveUserRoots([first], first); } catch (Exception failure) { error = failure; }
                Assert.IsNotNull(error);
                if (scenario == "xml-failure") { Assert.IsInstanceOfType<IOException>(error); }
                else { Assert.IsInstanceOfType<PortableSettingsException>(error); }
            }
            Assert.AreEqual(original, settings.BMSInstallDir);
            CollectionAssert.AreEqual(new[] { original }, store.CaptureUserRoots().ToArray());
            CollectionAssert.AreEqual(originalConfig, File.ReadAllBytes(configPath));
            CollectionAssert.AreEqual(new[] { scenario == "xml-failure" ? original : first }, ReadRoots(xml.ConfigFilePath));
            settings.Commit((nameof(Settings.StartupSortCachePrewarmPriority), 3));
            settings.TreeViewWidth = 321d; settings.Save();
            byte[] partialConfig = File.ReadAllBytes(configPath);
            byte[] partialXml = File.ReadAllBytes(xml.ConfigFilePath);
            var reopened = new BmsRootRegistrationStore(settings, () => owner);
            CollectionAssert.AreEqual(new[] { original }, reopened.CaptureUserRoots().ToArray());
            CollectionAssert.AreEqual(partialConfig, File.ReadAllBytes(configPath));
            CollectionAssert.AreEqual(partialXml, File.ReadAllBytes(xml.ConfigFilePath));
            CollectionAssert.AreEqual(new[] { scenario == "xml-failure" ? original : first }, new Lr2ConfigOwner(new LR2Config(xml.ConfigFilePath)).Capture().RegisteredRoots.Select(Trim).ToArray());
            string target = scenario == "runtime-equal" ? original : scenario == "same-input" ? first : latest;
            if (scenario == "same-input")
            {
                using (File.Open(xml.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                { Assert.ThrowsException<IOException>(() => store.SaveUserRoots([target], target)); }
            }
            if (scenario == "runtime-equal")
            {
                using (File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                { Assert.ThrowsException<PortableSettingsException>(() => store.SaveUserRoots([target], target)); }
            }
            store.SaveUserRoots([target], target);
            CollectionAssert.AreEqual(new[] { target }, store.CaptureUserRoots().ToArray());
            CollectionAssert.AreEqual(new[] { target }, ReadRoots(xml.ConfigFilePath));
            Settings fresh = PortableSettingsPersistenceTests.OpenSettings(configPath);
            Assert.AreEqual(target, fresh.BMSInstallDir);
            Assert.AreEqual(3, fresh.StartupSortCachePrewarmPriority);
            Assert.AreEqual(321d, fresh.TreeViewWidth);
            Assert.AreEqual("preserved", XDocument.Load(xml.ConfigFilePath).Root?.Element("unknown")?.Value);
            // roots-onlyの解除保存は、変更しないuser.configの共有拒否に依存しません。
            using (File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read)) { store.SaveUserRoots([target]); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string[] ReadRoots(string path) => new LR2Config(path).GetBMSSearchDirectoriesForChangeTracking().Select(Trim).ToArray();
    private static string Trim(string value) => value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static bool Same(string left, string right) => string.Equals(Trim(left), Trim(right), StringComparison.OrdinalIgnoreCase);
    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsRootRegistration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
