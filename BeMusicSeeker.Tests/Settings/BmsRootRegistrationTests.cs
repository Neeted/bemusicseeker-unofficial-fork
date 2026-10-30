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
            xml.Descendants("path").First().SetAttributeValue("future", "keep");
            xml.Save(xml.ConfigFilePath);
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

    /// <summary>確認済みprefixを元順序の登録・管理先・関連導入先へ限定適用し、未確認suffix・無関係な導入先・他操作の保存済み未公開rootを保持します。保存失敗では通知しません。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void RootSpellingChange_PreservesOtherRegistrationsAndPublishesAfterAllMedia(bool linked, bool relatedInstall)
    {
        string directory = NewDirectory();
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(directory, "MixedSongs")).FullName;
            string before = Path.Combine(directory, "mixedsongs");
            string nested = Path.Combine(before, "MissingChild");
            string missing = Path.Combine(directory, "Missing");
            string unrelated = Path.Combine(before, "UnrelatedInstall");
            string configPath = Path.Combine(directory, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(configPath);
            settings.OperationModeLR2DB = linked;
            settings.LR2RootPath = directory;
            settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([before, nested, missing]);
            settings.BMSRootPath = before;
            settings.BMSInstallDir = relatedInstall ? before : unrelated;
            settings.LR2CustomFolderOutputBaseDir = Path.Combine(before, "Managed");
            settings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(before, "RootOutput");
            settings.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(before, "Additional")]);
            settings.Save();
            LR2Config xml = BmsPlaylistTestSupport.CreateLr2Config(directory, before, nested, missing,
                settings.LR2CustomFolderOutputBaseDir, settings.LR2CustomFolderOutputBaseDirRootType, Path.Combine(before, "Additional"));
            xml.Descendants("path").First().SetAttributeValue("future", "keep");
            xml.Save(xml.ConfigFilePath);
            var owner = new Lr2ConfigOwner(xml);
            var store = new BmsRootRegistrationStore(settings, () => owner);
            BmsLibraryOptionsSnapshot input = (BmsLibraryOptionsSnapshot.CreateCurrent(settings) with { StandaloneBmsRootPaths = store.CaptureStandaloneRoots() })
                .WithLr2Input(linked ? owner.Capture() : null);
            string privateRoot = Path.Combine(directory, "Private");
            if (linked) { owner.SaveRoots(new Lr2RootChange([], [privateRoot]), publish: false); }
            BmsLibraryOptionsSnapshot confirmed = input.WithConfirmedRootPrefixes([before], [root]);
            int notices = 0;
            store.Published += (_, _) => notices++;
            if (linked)
            {
                using (File.Open(owner.ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                { Assert.ThrowsException<IOException>(() => store.SaveRootChanges(input, confirmed)); }
                Assert.AreEqual(0, notices);
                CollectionAssert.AreEqual(new[] { before, nested, missing }, store.CaptureUserRoots().ToArray());
            }
            {
                using (File.Open(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                { Assert.ThrowsException<PortableSettingsException>(() => store.SaveRootChanges(input, confirmed)); }
                Assert.AreEqual(0, notices);
                CollectionAssert.AreEqual(new[] { before, nested, missing }, store.CaptureUserRoots().ToArray());
            }
            store.SaveRootChanges(input, confirmed);
            Assert.AreEqual(1, notices);
            CollectionAssert.AreEqual(new[] { root, Path.Combine(root, "MissingChild"), missing }, store.CaptureUserRoots().ToArray());
            Assert.AreEqual(relatedInstall ? root : unrelated, settings.BMSInstallDir);
            Settings fresh = PortableSettingsPersistenceTests.OpenSettings(configPath);
            Assert.AreEqual(Path.Combine(root, "Managed"), fresh.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(Path.Combine(root, "RootOutput"), fresh.LR2CustomFolderOutputBaseDirRootType);
            Assert.AreEqual(Path.Combine(root, "Additional"), CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(fresh.LR2CustomFolderAdditionalOutputBaseDirs).Single());
            if (linked)
            {
                CollectionAssert.Contains(owner.CaptureSaved().RegisteredRoots.ToArray(), privateRoot);
                Assert.IsFalse(owner.Capture().RegisteredRoots.Contains(privateRoot));
                CollectionAssert.Contains(owner.Capture().RegisteredRoots.ToArray(), Path.Combine(root, "Managed"));
                Assert.AreEqual("keep", (string?)XDocument.Load(owner.ConfigFilePath).Descendants("path").First().Attribute("future"));
            }
            store.Published += (_, _) => throw new InvalidOperationException("notification failure");
            store.SaveUserRoots(store.CaptureUserRoots());
            Assert.AreEqual(2, notices);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>保存だけ成功したBと公開Aを分離し、同所補正A′は公開側だけへ反映します。実providerの読取失敗は公開・通知を止めます。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RootSpellingChange_DoesNotReAddReplacedSavedRootOrPublishUnrelatedPlacement(bool readFailure)
    {
        string directory = NewDirectory();
        try
        {
            string before = Path.Combine(directory, "songs");
            string confirmedRoot = Path.Combine(directory, "Songs");
            string other = Path.Combine(directory, "Other");
            string settingsPath = Path.Combine(directory, "user.config");
            Settings settings = PortableSettingsPersistenceTests.OpenSettings(settingsPath);
            settings.OperationModeLR2DB = true;
            settings.LR2RootPath = directory;
            settings.LR2CustomFolderOutputBaseDir = Path.Combine(before, "Managed");
            settings.Save();
            var applied = CustomFolderOutputSettingsSnapshot.CreateCurrent(settings);
            var owner = new Lr2ConfigOwner(BmsPlaylistTestSupport.CreateLr2Config(directory, before, settings.LR2CustomFolderOutputBaseDir));
            owner.SetPlacement(applied);
            var store = new BmsRootRegistrationStore(settings, () => owner, () => applied, placement => applied = placement);
            BmsLibraryOptionsSnapshot input = BmsLibraryOptionsSnapshot.CreateCurrent(settings, applied).WithLr2Input(owner.Capture());
            BmsLibraryOptionsSnapshot confirmed = input.WithConfirmedRootPrefixes([before], [confirmedRoot]);
            owner.SaveRoots(new Lr2RootChange(owner.CaptureSaved().RegisteredRoots, [other]), publish: false);
            settings.Persist((nameof(Settings.LR2CustomFolderOutputBaseDir), Path.Combine(other, "Managed")));
            int notices = 0;
            store.Published += (_, _) => notices++;
            if (readFailure)
            {
                using (File.Open(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
                { Assert.ThrowsException<PortableSettingsException>(() => store.SaveRootChanges(input, confirmed)); }
            }
            else { store.SaveRootChanges(input, confirmed); }
            CollectionAssert.AreEqual(new[] { other }, owner.CaptureSaved().RegisteredRoots.ToArray());
            CollectionAssert.AreEqual(new[] { other }, ReadRoots(owner.ConfigFilePath));
            string publishedRoot = readFailure ? before : confirmedRoot;
            CollectionAssert.AreEqual(new[] { publishedRoot, Path.Combine(publishedRoot, "Managed") }, owner.Capture().RegisteredRoots.ToArray());
            Assert.AreEqual(Path.Combine(publishedRoot, "Managed"), settings.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(Path.Combine(publishedRoot, "Managed"), applied.LR2CustomFolderOutputBaseDir);
            Assert.AreEqual(Path.Combine(other, "Managed"), PortableSettingsPersistenceTests.OpenSettings(settingsPath).LR2CustomFolderOutputBaseDir);
            Assert.AreSame(applied, owner.Capture().Placement);
            Assert.AreEqual(readFailure ? 0 : 1, notices);
        }
        finally { Directory.Delete(directory, true); }
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
