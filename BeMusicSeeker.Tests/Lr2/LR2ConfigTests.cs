using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LR2ConfigTests
{
    /// <summary>実在に依存しない登録計画で、root-relative・drive-relative・通常相対をLR2 root基準で解決し、要求・保存本文・raw読取りの基準を揃えます。</summary>
    [DataTestMethod]
    [DataRow(@"\Songs", @"D:\Songs")]
    [DataRow(@"D:Songs", @"D:\LR2\Songs")]
    [DataRow(@"C:Songs", @"C:\Songs")]
    [DataRow(@"Songs", @"D:\LR2\Songs")]
    [DataRow(@".\Songs\.\Pack\..", @"D:\LR2\Songs")]
    [DataRow(@"..\Songs", @"D:\Songs")]
    [DataRow(@"E:\Songs\.\Pack\..", @"E:\Songs")]
    public void RootPlanningDocument_ResolvesRegistrationsAgainstLr2RootWithoutFileAccess(string input, string expected)
    {
        var config = LR2Config.CreateRootPlanningDocument(@"D:\LR2\LR2files\Config\config.xml", [input]);
        Assert.AreEqual(expected, config.NormalizeRequestedSearchDirectory(input));
        XElement path = config.Element("config")?.Element("jukebox")?.Element("path") ?? throw new AssertFailedException();
        Assert.AreEqual(expected + "\\", path.Value);

        path.Value = input;
        CollectionAssert.AreEqual(new[] { expected }, config.GetBMSSearchDirectoriesForChangeTracking());
        CollectionAssert.AreEqual(new[] { expected }, new Lr2ConfigOwner(config).Capture().RegisteredRoots.ToArray());
        Assert.AreEqual(input, path.Value, "raw読取りと公開入力の捕捉は元のXML本文を変更しません。");
    }

    /// <summary>ドライブ・UNCルートと通常基点の包含を方向付きで判定し、大小文字・末尾区切りの差を吸収しつつ接頭辞兄弟や別領域を保持します。実媒体へはアクセスしません。</summary>
    [DataTestMethod]
    [DataRow(@"D:\", @"D:\", true)]
    [DataRow(@"D:\", @"d:\Table", true)]
    [DataRow(@"D:\", @"D:\Table\Level", true)]
    [DataRow(@"D:\", @"E:\Table", false)]
    [DataRow(@"D:\Managed", @"d:\managed\", true)]
    [DataRow(@"D:\Managed", @"D:\Managed\Child", true)]
    [DataRow(@"D:\Managed\", @"D:\ManagedSibling", false)]
    [DataRow(@"D:\Managed\", @"D:\", false)]
    [DataRow(@"\\test-server\share\", @"\\test-server\share", true)]
    [DataRow(@"\\test-server\share\", @"\\TEST-SERVER\SHARE\Table", true)]
    [DataRow(@"\\test-server\share\", @"\\test-server\share\Table\Level", true)]
    [DataRow(@"\\test-server\share", @"\\test-server\share\Table", true)]
    [DataRow(@"\\test-server\share\", @"\\test-server\share2\Table", false)]
    [DataRow(@"\\test-server\share\", @"\\other-server\share\Table", false)]
    [DataRow(@"\\test-server\share\Managed", @"\\test-server\share\ManagedSibling", false)]
    public void OutputBaseContainsDirectory_RespectsRootAndDirectoryBoundaries(string parent, string candidate, bool expected)
    {
        Assert.AreEqual(expected, CustomFolderOutputBaseSearchRootSyncService.IsSameOrChildDirectory(candidate, parent));
    }

    /// <summary>
    /// 未指定・空白・不正文字の設定パスは読取り不能として返し、設定があるという誤報を防ぐ。
    /// </summary>
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("invalid\0path")]
    public void SessionTryResolve_UnsetOrInvalidPathReturnsNoConfig(string? path)
    {
        Assert.IsFalse(new Lr2ConfigSession().TryResolve(path, out Lr2ConfigOwner? config));
        Assert.IsNull(config);
    }

    /// <summary>
    /// 不完全XML・必須jukebox欠落・別ルート要素を拒否して元ファイルを保持し、読取り時の無断修復を検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [DataTestMethod]
    [DataRow("<config>")]
    [DataRow("<config />")]
    [DataRow("<config><system /></config>")]
    [DataRow("<other><jukebox /></other>")]
    public void SessionTryResolve_InvalidDocumentRejectsWithoutChangingFile(string xml)
    {
        WithConfigFile(xml, configPath =>
        {
            byte[] before = File.ReadAllBytes(configPath);

            Assert.IsFalse(new Lr2ConfigSession().TryResolve(configPath, out Lr2ConfigOwner? config));
            Assert.IsNull(config);
            Assert.ThrowsException<XmlException>(() => new LR2Config(configPath));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(configPath));
        });
    }

    /// <summary>
    /// xmlとxmhの設定を読み、現在存在しない登録ルートも変更追跡用に保持して一時的な不在を登録削除へ読み替える誤りを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [DataTestMethod]
    [DataRow("config.xml")]
    [DataRow("config.xmh")]
    public void SessionTryResolve_ValidConfigPreservesUnavailableRegisteredRoots(string fileName)
    {
        WithConfigFile("<config><jukebox><path>missing-bms</path></jukebox></config>", configPath =>
        {
            string candidatePath = Path.Combine(Path.GetDirectoryName(configPath) ?? throw new AssertFailedException(), fileName);
            if (!string.Equals(configPath, candidatePath, StringComparison.Ordinal))
            {
                File.Move(configPath, candidatePath);
            }
            byte[] before = File.ReadAllBytes(candidatePath);
            string lr2Root = Directory.GetParent(candidatePath)?.Parent?.Parent?.FullName ?? throw new AssertFailedException();
            string missingRoot = Path.Combine(lr2Root, "missing-bms");

            Assert.IsFalse(Directory.Exists(missingRoot));
            Assert.IsTrue(new Lr2ConfigSession().TryResolve(candidatePath, out Lr2ConfigOwner? config));
            Assert.IsNotNull(config);
            CollectionAssert.AreEqual(new[] { missingRoot }, config.Capture().RegisteredRoots.ToArray());
            CollectionAssert.AreEqual(before, File.ReadAllBytes(candidatePath));
        });
    }

    /// <summary>
    /// 空jukeboxを有効設定として読み任意system要素とファイルを変更せず、読取りに伴う不要な補完を検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void SessionTryResolve_EmptyJukeboxIsValidWithoutCreatingOptionalSections()
    {
        WithConfigFile("<config><jukebox /></config>", configPath =>
        {
            byte[] before = File.ReadAllBytes(configPath);

            Assert.IsTrue(new Lr2ConfigSession().TryResolve(configPath, out Lr2ConfigOwner? config));
            Assert.IsNotNull(config);
            Assert.AreEqual(0, config.Capture().RegisteredRoots.Count);
            Assert.IsNull(XDocument.Load(configPath).Element("config")?.Element("system"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(configPath));
        });
    }

    /// <summary>
    /// 異文化でも共通整数の空白・符号を固定解釈し、自動再読込み2を手動専用0へ実保存して新しい文書から読み戻す。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [DataTestMethod]
    [DoNotParallelize]
    [DataRow("ja-JP")]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    [DataRow("ar-SA")]
    public void EnsureDatabaseAutoReloadManualOnly_ChangesExistingAutoReloadMode(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            WithConfig("<config><system><customfolder> +12 </customfolder><titleflash> +24 </titleflash><autoreload> +2 </autoreload></system><jukebox /></config>", delegate (string configPath, LR2Config config)
            {
                Assert.AreEqual(12, config.GetCustomFolderMask());
                Assert.AreEqual(24, config.GetTitleFlashHours());
                Assert.AreEqual(2, config.GetDatabaseAutoReloadMode());
                bool changed = config.EnsureDatabaseAutoReloadManualOnly();

                Assert.IsTrue(changed);
                Assert.AreEqual(LR2Config.DatabaseAutoReloadManualOnly, config.GetDatabaseAutoReloadMode());
                config.Save(config.ConfigFilePath);
                Assert.AreEqual("0", XDocument.Load(configPath).Element("config")?.Element("system")?.Element("autoreload")?.Value);
                var reloaded = new LR2Config(configPath);
                Assert.AreEqual(12, reloaded.GetCustomFolderMask());
                Assert.AreEqual(24, reloaded.GetTitleFlashHours());
                Assert.AreEqual(0, reloaded.GetDatabaseAutoReloadMode());
                Assert.IsFalse(reloaded.EnsureDatabaseAutoReloadManualOnly());
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// 既に手動専用の設定は変更なしと返して保持し、不要な変更扱いを検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void EnsureDatabaseAutoReloadManualOnly_DoesNotChangeManualOnlyMode()
    {
        WithConfig("<config><system><autoreload>0</autoreload></system><jukebox /></config>", delegate (string _, LR2Config config)
        {
            bool changed = config.EnsureDatabaseAutoReloadManualOnly();

            Assert.IsFalse(changed);
            Assert.AreEqual(LR2Config.DatabaseAutoReloadManualOnly, config.GetDatabaseAutoReloadMode());
        });
    }

    /// <summary>
    /// 自動再読込み要素がなくても手動専用0を追加して保存し、安全なLR2設定が欠ける誤りを検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void EnsureDatabaseAutoReloadManualOnly_AddsMissingAutoReloadElement()
    {
        WithConfig("<config><system><customfolder>0</customfolder></system><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            bool changed = config.EnsureDatabaseAutoReloadManualOnly();

            Assert.IsTrue(changed);
            Assert.AreEqual(LR2Config.DatabaseAutoReloadManualOnly, config.GetDatabaseAutoReloadMode());
            config.Save(config.ConfigFilePath);
            Assert.AreEqual("0", XDocument.Load(configPath).Element("config")?.Element("system")?.Element("autoreload")?.Value);
        });
    }

    /// <summary>
    /// 設定変更の保存でUTF-8宣言と日本語を保持し、文字コード変更による既存設定の破損を検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void Save_PreservesDeclaredXmlEncodingAndNonAsciiContent()
    {
        WithConfig(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><config><system><customfolder>日本語</customfolder></system><jukebox /></config>",
            delegate (string configPath, LR2Config config)
            {
                var owner = new Lr2ConfigOwner(config);
                owner.EnsureDatabaseAutoReloadManualOnly();

                byte[] bytes = File.ReadAllBytes(configPath);
                string savedXml = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
                Assert.IsTrue(savedXml.Contains("日本語", StringComparison.Ordinal));
                var savedDocument = XDocument.Load(configPath);
                Assert.IsTrue(string.Equals("utf-8", savedDocument.Declaration?.Encoding, StringComparison.OrdinalIgnoreCase));
                Assert.AreEqual(
                    "0",
                    savedDocument.Element("config")?.Element("system")?.Element("autoreload")?.Value);
            });
    }

    /// <summary>
    /// 保存先がロックされると元バイトと兄弟ファイルを保持して失敗原因を返し、部分保存や一時ファイル残留を検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void Save_WhenDestinationCannotBeReplaced_PreservesExistingBytes()
    {
        WithConfig("<config><system><autoreload>2</autoreload></system><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            byte[] previousBytes = File.ReadAllBytes(configPath);
            string[] siblingPathsBefore = Directory.GetFiles(Path.GetDirectoryName(configPath)!);
            var owner = new Lr2ConfigOwner(config);

            using (var lockedDestination = new FileStream(
                configPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                IOException failure = Assert.ThrowsException<IOException>(() => owner.EnsureDatabaseAutoReloadManualOnly());

                Assert.IsNotNull(failure.InnerException);
                StringAssert.Contains(failure.Message, Path.GetDirectoryName(configPath));
                CollectionAssert.AreEquivalent(siblingPathsBefore, Directory.GetFiles(Path.GetDirectoryName(configPath)!));
            }

            CollectionAssert.AreEqual(previousBytes, File.ReadAllBytes(configPath));
            Assert.AreEqual(2, owner.CaptureSaved().DatabaseAutoReloadMode);
        });
    }

    /// <summary>
    /// 登録ルート削除の保存がロックで失敗した場合は実ファイルとメモリ上の登録を戻し、保存前だけ削除された状態を防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void OwnerRemoveRoots_WhenDestinationCannotBeReplaced_PreservesSavedInput()
    {
        WithConfig("<config><system /><jukebox><path>Managed\\</path></jukebox></config>", delegate (string configPath, LR2Config config)
        {
            byte[] previousBytes = File.ReadAllBytes(configPath);
            string[] siblingPathsBefore = Directory.GetFiles(Path.GetDirectoryName(configPath)!);
            var owner = new Lr2ConfigOwner(config);

            using (var lockedDestination = new FileStream(
                configPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                IOException failure = Assert.ThrowsException<IOException>(() =>
                    owner.SaveRoots(new Lr2RootChange(owner.CaptureSaved().RegisteredRoots, [])));
                Assert.IsNotNull(failure.InnerException);
                StringAssert.Contains(failure.Message, Path.GetDirectoryName(configPath));
                CollectionAssert.AreEquivalent(siblingPathsBefore, Directory.GetFiles(Path.GetDirectoryName(configPath)!));
            }

            CollectionAssert.AreEqual(previousBytes, File.ReadAllBytes(configPath));
            CollectionAssert.AreEqual(
                new[] { "Managed" },
                owner.CaptureSaved().RegisteredRoots.Select(Path.GetFileName).ToArray());
        });
    }

    /// <summary>
    /// 同じ要求の親子ルートを拒否し、重複範囲を登録して二重管理する誤りを検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void AddBMSSearchDirectories_RejectsNestedPathsWithinSameRequest()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!, "BMS");
            string childPath = Path.Combine(rootPath, "Child");
            Directory.CreateDirectory(childPath);

            Assert.ThrowsException<ArgumentException>(() => config.AddBMSSearchDirectories([rootPath, childPath]));
        });
    }

    /// <summary>
    /// 存在する登録ルートだけを読取り結果へ返しても不在登録を含む設定ファイルは変更せず、参照に伴う登録消失を防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void GetBMSSearchDirectoriesReadOnly_DoesNotRewriteConfig()
    {
        WithConfig("<config><system /><jukebox><path>BMS\\</path><path>Missing\\</path></jukebox></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string bmsRoot = Path.Combine(tempRoot, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string before = File.ReadAllText(configPath);

            List<string> directories = config.GetBMSSearchDirectoriesReadOnly();

            CollectionAssert.AreEqual(new[] { bmsRoot }, directories);
            Assert.AreEqual(before, File.ReadAllText(configPath));
        });
    }

    /// <summary>
    /// 同一・正規化同一・子・親・兄弟・接頭辞類似・ボリューム親を区別し、親出力をルート出力時だけ許可して範囲衝突を防ぐ。
    /// </summary>
    [DataTestMethod]
    [DataRow("same", false, true)]
    [DataRow("same", true, true)]
    [DataRow("normalized-same", false, true)]
    [DataRow("child", false, false)]
    [DataRow("child", true, false)]
    [DataRow("parent", false, false)]
    [DataRow("parent", true, true)]
    [DataRow("sibling", false, true)]
    [DataRow("prefix", false, true)]
    [DataRow("volume-parent", false, false)]
    [DataRow("volume-parent", true, true)]
    public void OutputBaseSearchRootValidation_OnlyRootOutputCanContainRegisteredDirectories(
        string relation, bool rootOutput, bool allowed)
    {
        string registered = Path.Combine(Path.GetTempPath(), "BMS", "Songs");
        string output = relation switch
        {
            "same" => registered,
            "normalized-same" => Path.Combine(registered, ".").ToUpperInvariant() + Path.DirectorySeparatorChar,
            "child" => Path.Combine(registered, "Custom"),
            "parent" => Path.GetDirectoryName(registered)!,
            "sibling" => Path.Combine(Path.GetDirectoryName(registered)!, "Custom"),
            "prefix" => registered + "Extra",
            "volume-parent" => Path.GetPathRoot(registered)!,
            _ => throw new ArgumentOutOfRangeException(nameof(relation))
        };
        void Validate() => CustomFolderOutputBaseSearchRootSyncService.ValidateOutputBaseAgainstSearchRoots(
            output, [registered], "出力先", allowRegisteredChildren: rootOutput);

        if (allowed)
        {
            Validate();
        }
        else
        {
            ArgumentException error = Assert.ThrowsException<ArgumentException>(Validate);
            StringAssert.Contains(error.Message, output);
            StringAssert.Contains(error.Message, registered);
        }
    }

    /// <summary>
    /// 既存登録を保持しつつ指定された既定・追加出力先を全て登録し、出力先の未登録や既存登録の消失を検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void RepairNormalOutputBaseRoots_AddsConfiguredDefaultAndAdditionalRoots()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string existingRoot = Path.Combine(tempRoot, "ExistingBmsRoot");
            string defaultOutputBase = Path.Combine(tempRoot, "DefaultOutput");
            string additionalOutputBase1 = Path.Combine(tempRoot, "AdditionalOutput1");
            string additionalOutputBase2 = Path.Combine(tempRoot, "AdditionalOutput2");
            Directory.CreateDirectory(existingRoot);
            Directory.CreateDirectory(defaultOutputBase);
            Directory.CreateDirectory(additionalOutputBase1);
            Directory.CreateDirectory(additionalOutputBase2);
            config.AddBMSSearchDirectories([existingRoot]);

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(
                    config,
                    defaultOutputBase,
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase1, additionalOutputBase2]));

            Assert.IsTrue(result.Changed);
            Assert.AreEqual(3, result.AddedCount);
            Assert.AreEqual(0, result.RemovedCount);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), existingRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), defaultOutputBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), additionalOutputBase1);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), additionalOutputBase2);
        });
    }

    /// <summary>
    /// 既に登録済みの指定出力先を変更なしとして保持し、重複登録と不要な修復を検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void RepairNormalOutputBaseRoots_DoesNotRewriteExistingRoots()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string defaultOutputBase = Path.Combine(tempRoot, "DefaultOutput");
            string additionalOutputBase = Path.Combine(tempRoot, "AdditionalOutput");
            Directory.CreateDirectory(defaultOutputBase);
            Directory.CreateDirectory(additionalOutputBase);
            config.AddBMSSearchDirectories([defaultOutputBase, additionalOutputBase]);

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(
                    config,
                    defaultOutputBase,
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]));

            Assert.IsFalse(result.Changed);
            Assert.AreEqual(0, result.AddedCount);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), defaultOutputBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), additionalOutputBase);
        });
    }

    /// <summary>
    /// 通常出力先が登録ルートの親・子となる両条件を拒否し、元の登録集合を保持する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [DataTestMethod]
    // false は出力先が子、true は出力先が親となる衝突。
    [DataRow(false)]
    [DataRow(true)]
    public void RepairNormalOutputBaseRoots_RejectsParentOrChildOfRegisteredRoot(bool outputIsParent)
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string parent = Path.Combine(tempRoot, "Output");
            string child = Path.Combine(parent, "Songs");
            string bmsRoot = outputIsParent ? child : parent;
            string outputBase = outputIsParent ? parent : child;
            Directory.CreateDirectory(bmsRoot);
            config.AddBMSSearchDirectories([bmsRoot]);

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(config, outputBase, "[]"));

            CollectionAssert.AreEqual(new[] { bmsRoot }, config.GetBMSSearchDirectoriesForChangeTracking());
        });
    }

    /// <summary>
    /// 既定と追加出力先が入れ子の場合は両方の採用前に拒否し、途中まで登録する誤りを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void RepairNormalOutputBaseRoots_RejectsNestedDefaultAndAdditionalBeforeAdoption()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string bmsRoot = Path.Combine(tempRoot, "BMS");
            string defaultOutputBase = Path.Combine(bmsRoot, "DefaultOutput");
            string nestedAdditionalOutputBase = Path.Combine(defaultOutputBase, "AdditionalOutput");
            Directory.CreateDirectory(nestedAdditionalOutputBase);
            config.AddBMSSearchDirectories([bmsRoot]);

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(
                    config,
                    defaultOutputBase,
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([nestedAdditionalOutputBase])));

            CollectionAssert.Contains(config.GetBMSSearchDirectories(), bmsRoot);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), defaultOutputBase);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), nestedAdditionalOutputBase);
        });
    }

    /// <summary>
    /// 既定と追加の同一出力先を採用前に拒否し、重複設定から登録を増やす誤りを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void RepairNormalOutputBaseRoots_RejectsDuplicateDefaultAndAdditionalBeforeAdoption()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string bmsRoot = Path.Combine(tempRoot, "BMS");
            string outputBase = Path.Combine(bmsRoot, "Output");
            Directory.CreateDirectory(outputBase);
            config.AddBMSSearchDirectories([bmsRoot]);

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(
                    config,
                    outputBase,
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([outputBase])));

            CollectionAssert.Contains(config.GetBMSSearchDirectories(), bmsRoot);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), outputBase);
        });
    }

    /// <summary>
    /// 追加出力先が登録ルートと衝突すると新しい既定先も登録・作成せず、失敗前の副作用を防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void RepairNormalOutputBaseRoots_RejectsInvalidAdditionalBeforeAddingDefault()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string bmsRoot = Path.Combine(tempRoot, "BMS");
            string defaultOutput = Path.Combine(tempRoot, "NewDefault");
            string additionalOutput = Path.Combine(bmsRoot, "NewAdditional");
            Directory.CreateDirectory(bmsRoot);
            config.AddBMSSearchDirectories([bmsRoot]);

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(
                    config, defaultOutput,
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutput])));

            CollectionAssert.AreEqual(new[] { bmsRoot }, config.GetBMSSearchDirectoriesForChangeTracking());
            Assert.IsFalse(Directory.Exists(defaultOutput));
            Assert.IsFalse(Directory.Exists(additionalOutput));
        });
    }

    /// <summary>
    /// 追加出力先のJSONが壊れている場合は引数エラーを返し、指定が空であったかのように採用する誤りを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void RepairNormalOutputBaseRoots_RejectsInvalidAdditionalOutputBaseJson()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string defaultOutputBase = Path.Combine(tempRoot, "DefaultOutput");
            Directory.CreateDirectory(defaultOutputBase);

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(config, defaultOutputBase, "{"));
        });
    }

    /// <summary>
    /// 追加出力先の変更に合わせてjukebox登録を一件追加・削除し、保存設定との同期漏れを検出する。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void SyncAdditionalOutputBaseRoots_AddsAndRemovesJukeboxPath()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string additionalBase = Path.Combine(tempRoot, "Additional");
            Directory.CreateDirectory(additionalBase);
            string serializedAdditionalBase = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalBase]);

            CustomFolderOutputBaseSearchRootSyncResult addResult =
                CustomFolderOutputBaseSearchRootSyncService.SyncAdditionalOutputBaseRoots(config, "[]", serializedAdditionalBase);

            Assert.IsTrue(addResult.Changed);
            Assert.AreEqual(1, addResult.AddedCount);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), additionalBase);

            CustomFolderOutputBaseSearchRootSyncResult removeResult =
                CustomFolderOutputBaseSearchRootSyncService.SyncAdditionalOutputBaseRoots(config, serializedAdditionalBase, "[]");

            Assert.IsTrue(removeResult.Changed);
            Assert.AreEqual(1, removeResult.RemovedCount);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), additionalBase);
        });
    }

    /// <summary>
    /// 登録ルートの親・子となる追加先を拒否して登録と保存バイトを保持し、失敗時に登録が置換される誤りを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [DataTestMethod]
    // false は出力先が子、true は出力先が親となる衝突。
    [DataRow(false)]
    [DataRow(true)]
    public void SyncAdditionalOutputBaseRoots_RejectsParentOrChildWithoutReplacingRegistration(bool outputIsParent)
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string parent = Path.Combine(tempRoot, "Additional");
            string child = Path.Combine(parent, "Songs");
            string bmsRoot = outputIsParent ? child : parent;
            string additionalBase = outputIsParent ? parent : child;
            Directory.CreateDirectory(bmsRoot);
            config.AddBMSSearchDirectories([bmsRoot]);
            config.Save(config.ConfigFilePath);
            byte[] savedXml = File.ReadAllBytes(configPath);

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseSearchRootSyncService.SyncAdditionalOutputBaseRoots(
                    config, "[]", CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalBase])));

            CollectionAssert.AreEqual(new[] { bmsRoot }, config.GetBMSSearchDirectoriesForChangeTracking());
            CollectionAssert.AreEqual(savedXml, File.ReadAllBytes(configPath));
        });
    }

    /// <summary>
    /// 追加先から外しても通常出力先として必要な登録を残し、他用途の登録まで削除する誤りを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void SyncAdditionalOutputBaseRoots_DoesNotRemovePathPreservedByNormalOutputBase()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string outputBase = Path.Combine(tempRoot, "FormerAdditional");
            Directory.CreateDirectory(outputBase);
            string previousAdditionalBase = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([outputBase]);
            config.AddBMSSearchDirectories([outputBase]);

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.SyncAdditionalOutputBaseRoots(
                    config,
                    previousAdditionalBase,
                    "[]",
                    [outputBase]);

            Assert.IsFalse(result.Changed);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), outputBase);
        });
    }

    /// <summary>
    /// 準備時に新既定先を追加して旧登録を保持し、完了時だけ旧登録を削除して処理途中の参照切れを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrepareNormalOutputBaseRoots_AddsNewDefaultAndRemovesOldDefault(bool oldIsParent)
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string oldDefaultBase = Path.Combine(tempRoot, "OldDefault");
            string newDefaultBase = oldIsParent ? Path.Combine(oldDefaultBase, "NewDefault") : Path.Combine(tempRoot, "NewDefault");
            Directory.CreateDirectory(oldDefaultBase);
            Directory.CreateDirectory(newDefaultBase);
            config.AddBMSSearchDirectories([oldDefaultBase]);

            CustomFolderOutputBaseSearchRootSyncPlan plan =
                CustomFolderOutputBaseSearchRootSyncService.PrepareNormalOutputBaseRoots(
                    config,
                    oldDefaultBase,
                    newDefaultBase,
                    "[]",
                    "[]");

            CollectionAssert.Contains(config.GetBMSSearchDirectoriesForChangeTracking(), oldDefaultBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectoriesForChangeTracking(), newDefaultBase);
            CollectionAssert.Contains(plan.RemovedPaths.ToArray(), oldDefaultBase);

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.CompleteAdditionalOutputBaseRootSync(config, plan);

            Assert.IsTrue(result.Changed);
            Assert.AreEqual(1, result.AddedCount);
            Assert.AreEqual(1, result.RemovedCount);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectoriesForChangeTracking(), oldDefaultBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectoriesForChangeTracking(), newDefaultBase);
        });
    }

    /// <summary>
    /// 追加先を既定先へ昇格させる場合はその登録を削除候補にせず、用途変更時に必要な登録を失う誤りを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void PrepareNormalOutputBaseRoots_PreservesOldAdditionalWhenPromotedToDefault()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string oldDefaultBase = Path.Combine(tempRoot, "OldDefault");
            string promotedBase = Path.Combine(tempRoot, "Promoted");
            Directory.CreateDirectory(oldDefaultBase);
            Directory.CreateDirectory(promotedBase);
            config.AddBMSSearchDirectories([oldDefaultBase, promotedBase]);
            string previousAdditionalBase = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([promotedBase]);

            CustomFolderOutputBaseSearchRootSyncPlan plan =
                CustomFolderOutputBaseSearchRootSyncService.PrepareNormalOutputBaseRoots(
                    config,
                    oldDefaultBase,
                    promotedBase,
                    previousAdditionalBase,
                    "[]");

            CollectionAssert.Contains(config.GetBMSSearchDirectories(), promotedBase);
            CollectionAssert.Contains(plan.RemovedPaths.ToArray(), oldDefaultBase);
            CollectionAssert.DoesNotContain(plan.RemovedPaths.ToArray(), promotedBase);
        });
    }

    /// <summary>
    /// 追加先変更の準備で新旧登録を共存させ、完了後に旧登録だけを削除して移行途中の参照切れを防ぐ。
    /// </summary>
    /// <remarks>実設定ファイルと登録ディレクトリを使い、読取り・保存・登録変更の接続を確認する。</remarks>
    [TestMethod]
    public void PrepareAdditionalOutputBaseRoots_AddsNewRootBeforeOldRootRemoval()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)))!;
            string oldAdditionalBase = Path.Combine(tempRoot, "OldAdditional");
            string newAdditionalBase = Path.Combine(tempRoot, "NewAdditional");
            Directory.CreateDirectory(oldAdditionalBase);
            Directory.CreateDirectory(newAdditionalBase);
            config.AddBMSSearchDirectories([oldAdditionalBase]);

            CustomFolderOutputBaseSearchRootSyncPlan plan =
                CustomFolderOutputBaseSearchRootSyncService.PrepareAdditionalOutputBaseRoots(
                    config,
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([oldAdditionalBase]),
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([newAdditionalBase]));

            CollectionAssert.Contains(config.GetBMSSearchDirectories(), oldAdditionalBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), newAdditionalBase);
            CollectionAssert.Contains(plan.RemovedPaths.ToArray(), oldAdditionalBase);

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.CompleteAdditionalOutputBaseRootSync(config, plan);

            Assert.IsTrue(result.Changed);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), oldAdditionalBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), newAdditionalBase);
        });
    }

    /// <summary>dot・有効な親要素を含む相対XML/通常DBを本番起動調整と差分で絶対化し、消費側のexact root照合を二周期通して再探索・旧曲NEW・重複を発生させず利用者列とscoreを保持します。</summary>
    /// <remarks>固定OpenLR2の必要な文字列比較とSQLだけを実SQLiteへ適用します。集計画面全体や実LR2の起動は保証しません。</remarks>
    [TestMethod]
    public void StartupRelativeRegistration_ConvergesForExactLr2ConsumerAcrossTwoCycles()
    {
        WithConfigFile("<config><system><autoreload>0</autoreload></system><jukebox><path>.\\LR2files\\..\\songs\\</path></jukebox></config>", configPath =>
        {
            string root = Directory.GetParent(configPath)?.Parent?.Parent?.FullName ?? throw new AssertFailedException();
            string songs = Directory.CreateDirectory(Path.Combine(root, "Songs")).FullName;
            string pack = Directory.CreateDirectory(Path.Combine(songs, "Pack")).FullName;
            string chart = Path.Combine(pack, "chart.bms");
            File.WriteAllText(chart, CreateValidBmsText("Old song"), Encoding.ASCII);
            DateTime timestamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(chart, timestamp);
            Directory.SetLastWriteTimeUtc(pack, timestamp);
            Directory.SetLastWriteTimeUtc(songs, timestamp);
            ChartFile parsed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chart));
            string songDbPath = Path.Combine(root, "LR2files", "Database", "song.db");
            Directory.CreateDirectory(Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException());
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.CreateTable<LR2SongDB.song>(); db.CreateTable<LR2SongDB.folder>();
                    db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(parsed with { Path = Path.Combine("Songs", "Pack", "chart.bms"), AddDate = 12345, Favorite = 3, Tag = "keep" }), typeof(LR2SongDB.song));
                    db.InsertOrReplace(new LR2SongDB.folder { path = "Songs\\", parent = "e2977170", type = 1, date = ToUnixSeconds(timestamp), adddate = 12345 }, typeof(LR2SongDB.folder));
                    db.InsertOrReplace(new LR2SongDB.folder { path = "Songs\\Pack\\", parent = "old", type = 1, date = ToUnixSeconds(timestamp), adddate = 12345 }, typeof(LR2SongDB.folder));
                    db.Execute("CREATE TABLE consumer_score(hash TEXT PRIMARY KEY, score INTEGER);");
                    db.Execute("INSERT INTO consumer_score VALUES (?, ?);", parsed.Md5, 9876);
                });
            }
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true; values.LR2RootPath = root; values.LR2ConfigXmlPath = configPath;
                values.LR2CustomFolderOutputBaseDir = string.Empty; values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
                values.LR2CustomFolderOutputBaseDirRootType = string.Empty;
            });
            var owner = new Lr2ConfigOwner(new LR2Config(configPath));
            StartupLibraryConstructionOwner.RepairCustomFolderSearchRoots(StartupSettingsSnapshot.CreateCurrent(settings), owner);
            var service = new BmsLibraryInitializationService();
            var gateway = new BmsLibraryDbGateway(songDbPath);
            BmsLibraryOptionsSnapshot options = BmsLibraryOptionsSnapshot.CreateCurrent(settings).WithLr2Input(owner.Capture());
            SongTableLoadResult loaded = service.LoadSongTable(gateway, options, null, new TestFileMutationService(), null, failure => failure.Message);
            IReadOnlyList<ChartFile> current = loaded.LoadedFiles;
            for (int cycle = 0; cycle < 2; cycle++)
            {
                string confirmed = LongPathFileSystem.ConfirmDirectoryPathSpelling(owner.Capture().RegisteredRoots.Single());
                owner.SaveRoots(new Lr2RootChange(owner.Capture().RegisteredRoots, [confirmed]));
                options = options.WithLr2Input(owner.Capture());
                ChartScanExecutionResult scan = new FastDirectoryFileScanner().Scan([songs], ChartDirectoryScanBuilder.ChartExtensions,
                    includeTextSurface: true, includeDirectorySurface: true);
                SongTableFileCheckResult diff = service.ApplyFileScanDiff(gateway, new EverythingNative(ApplicationPathPolicy.Current), options,
                    current, scan, 0, null, null, currentBmsonSongs: [], lr2NormalFolderSyncRootDirectories: [songs],
                    folderTableSnapshot: CaptureNormalFolderSnapshot(songDbPath));
                current = diff.NextFiles;
                Assert.IsFalse(diff.Lr2NormalFolderSyncFailed);
                if (cycle == 1) { Assert.AreEqual(0, diff.Lr2NormalFolderUpsertedCount + diff.Lr2NormalFolderDeletedCount); }
                using LR2SongDBExtended db = gateway.OpenSongDb();
                // OpenLR2 a87100a0: LR2_configsave.cpp Read_JukeboxPath/adjust_trailing_slashは末尾区切り一つだけを補い、絶対化/case変換はしません。
                // strclass.cpp isSameはstrcmp、LR2_songmanage.cpp ReloadSongsのROOT SELECT/一致検査とGetFolderDataFromPathのINSERT成功が再探索契機です。
                // https://github.com/GOMazk/OpenLR2/blob/a87100a0a0f629a5e8130d1e8a3b5c807b9bf96c/LR2/LR2_songmanage.cpp#L2827-L2863
                string[] jukebox = XDocument.Load(configPath).Root?.Element("jukebox")?.Elements("path")
                    .Select(element => element.Value.TrimEnd('\\', '/') + "\\").ToArray() ?? [];
                CollectionAssert.AreEqual(new[] { songs + "\\" }, jukebox);
                foreach (LR2SongDB.folder folder in db.Query<LR2SongDB.folder>("SELECT * FROM folder WHERE parent = ?;", "e2977170"))
                { CollectionAssert.Contains(jukebox, folder.path); }
                int reexplore = jukebox.Sum(path => db.Execute("INSERT OR IGNORE INTO folder(path) VALUES (?);", path));
                Assert.AreEqual(0, reexplore);
                // 同版LR2_songmanage.cpp:2914のNEW条件、実song集合/hash別件数とscore結合の一重性を観測します。
                Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE adddate > ?;", ToUnixSeconds(DateTime.UtcNow.AddHours(-24))));
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song;"));
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE hash = ?;", parsed.Md5));
                Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song JOIN consumer_score ON song.hash = consumer_score.hash;"));
                Assert.AreEqual(9876, db.ExecuteScalar<int>("SELECT score FROM song JOIN consumer_score ON song.hash = consumer_score.hash;"));
                LR2SongDB.song row = db.Table<LR2SongDB.song>().Single();
                Assert.AreEqual(chart, row.path); Assert.AreEqual(12345, row.adddate); Assert.AreEqual(3, row.favorite); Assert.AreEqual("keep", row.tag);
                Assert.AreEqual(ToUnixSeconds(timestamp), row.date);
            }
        });
    }

    private static void WithConfig(string xml, Action<string, LR2Config> action)
    {
        WithConfigFile(xml, configPath => action(configPath, new LR2Config(configPath)));
    }

    private static void WithConfigFile(string xml, Action<string> action)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LR2Config_" + Guid.NewGuid().ToString("N"));
        string configDirectoryPath = Path.Combine(tempRootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectoryPath);
        string configPath = Path.Combine(configDirectoryPath, "config.xml");
        File.WriteAllText(configPath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Exception? primaryFailure = null;
        try
        {
            action(configPath);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
            catch (Exception cleanupFailure)
            {
                if (primaryFailure == null)
                {
                    throw;
                }

                throw new AggregateException(
                    "LR2Config テスト fixture の cleanup に失敗しました。",
                    primaryFailure,
                    cleanupFailure);
            }
        }
    }
}
