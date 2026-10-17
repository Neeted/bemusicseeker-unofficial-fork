using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>原文保全・置換・退避の失敗境界は実ファイルの共有条件が必要です。独立した設定ファイルと provider を使って後片付けします。</remarks>
[TestClass]
public sealed class PortableSettingsPersistenceTests
{
    /// <summary>利用者の言語・音声品質・確認設定と全有効 thread 数を保存し新 wrapper で読み戻し、未知キー保持と一時ファイル回収を確認します。</summary>
    [TestMethod]
    public void SaveRoundTripsThroughFreshGeneratedSettingsAndPreservesUnknownKeys()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US").Replace("</BeMusicSeeker", "<setting name=\"FutureKey\" serializeAs=\"String\"><value>retain</value></setting></BeMusicSeeker"));
        Settings settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR";
        settings.PlayerResamplingQuality = 4;
        settings.ShowNewPackageInstallConfirmMsg = false;
        settings.RequireCompleteUsedResourcesForAutoInstall = true;
        // 有効な 1〜4 thread の利用者値をすべて保持し、同じ値への正規化で隠さないことを確認します。
        foreach (int threadCount in Enumerable.Range(1, 4))
        {
            settings.PlayerMixerThreadCount = threadCount;
            settings.Save();
            Settings loaded = OpenSettings(files.Path);
            Assert.AreEqual("fr-FR", loaded.Lang);
            Assert.IsFalse(loaded.ShowNewPackageInstallConfirmMsg);
            Assert.IsTrue(loaded.RequireCompleteUsedResourcesForAutoInstall);
            Assert.AreEqual(4, loaded.PlayerResamplingQuality, "既存の有効な品質4は新しい既定値2へ移行せず保持します。");
            Assert.AreEqual(threadCount, loaded.PlayerMixerThreadCount);
        }
        settings.ShowNewPackageInstallConfirmMsg = true;
        settings.Save();
        Assert.IsTrue(OpenSettings(files.Path).ShowNewPackageInstallConfirmMsg);
        Assert.AreEqual("retain", XDocument.Load(files.Path).Descendants("setting").Single(e => (string?)e.Attribute("name") == "FutureKey").Element("value")!.Value);
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    /// <summary>ファイルなしと旧ファイルの個別キー欠損で互換既定を利用し、確認設定や音声設定が未初期化になる誤りを検出します。</summary>
    [TestMethod]
    public void MissingFileAndIndividualKeyUseDefaults()
    {
        using var files = new SettingsFiles();
        string defaultLanguage = OpenSettings(files.Path).Lang;
        Assert.IsFalse(OpenSettings(files.Path).RequireCompleteUsedResourcesForAutoInstall);
        Assert.IsTrue(OpenSettings(files.Path).ShowNewPackageInstallConfirmMsg);
        File.WriteAllText(files.Path, Config());
        Assert.AreEqual(defaultLanguage, OpenSettings(files.Path).Lang);
        Assert.IsFalse(OpenSettings(files.Path).RequireCompleteUsedResourcesForAutoInstall);
        Assert.IsNull(OpenSettings(files.Path).AssemblyVersion);
        Assert.IsTrue(OpenSettings(files.Path).ShowNewPackageInstallConfirmMsg);
        Assert.AreEqual(2, OpenSettings(files.Path).PlayerResamplingQuality);
        Assert.AreEqual(1, OpenSettings(files.Path).PlayerMixerThreadCount);
    }

    /// <summary>不正 XML と必須構造の欠損を外部編集として置き、読込みと保存を Read 失敗にし元 bytes を既定値で上書きしないことを確認します。</summary>
    [DataTestMethod]
    [DataRow("<configuration>")]
    [DataRow("<other><userSettings><BeMusicSeeker.Properties.Settings /></userSettings></other>")]
    [DataRow("<configuration />")]
    [DataRow("<configuration><userSettings /></configuration>")]
    public void ExistingInvalidInputFailsReadAndSaveWithoutReplacingBytes(string input)
    {
        using var files = new SettingsFiles();
        Settings settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR"; // Materialize while missing, then model an external edit before Save.
        File.WriteAllText(files.Path, input);
        byte[] before = File.ReadAllBytes(files.Path);
        PortableSettingsException read = Assert.ThrowsException<PortableSettingsException>(() => _ = OpenSettings(files.Path).Lang);
        AssertFailure(read, files.Path, "Read");
        PortableSettingsException save = Assert.ThrowsException<PortableSettingsException>(() => settings.Save());
        AssertFailure(save, files.Path, "Read");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
        Assert.AreEqual(1, Directory.GetFiles(files.Directory).Length);
    }

    /// <summary>保存値に XML 不可文字を含め、直列化失敗を Save として返して既存内容と一時ファイル回収を保つことを確認します。</summary>
    [TestMethod]
    public void SupportedStringSerializationFailurePreservesOriginalBytes()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        byte[] before = File.ReadAllBytes(files.Path);
        Settings settings = OpenSettings(files.Path);
        settings.Lang = "invalid XML character: \u0001";
        AssertFailure(Assert.ThrowsException<PortableSettingsException>(() => settings.Save()), files.Path, "Save");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    /// <summary>読取り専用の実ファイルへ保存し、対象と原因を伴う Save 失敗を返して元 bytes を保持することを確認します。</summary>
    [TestMethod]
    public void ReadOnlySaveFailsWithPathAndCauseAndPreservesBytes()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        Settings settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR";
        byte[] before = File.ReadAllBytes(files.Path);
        File.SetAttributes(files.Path, FileAttributes.ReadOnly);
        PortableSettingsException failure = Assert.ThrowsException<PortableSettingsException>(() => settings.Save());
        AssertFailure(failure, files.Path, "Save");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    /// <summary>排他ロック下で読込みと保存を要求し、読取り失敗を空設定へ置き換えず元ファイルを保持することを確認します。</summary>
    [TestMethod]
    public void ReadSharingFailureDoesNotBecomeEmptySettingsOrOverwriteInput()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        Settings settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR";
        byte[] before = File.ReadAllBytes(files.Path);
        using (var blocker = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AssertFailure(Assert.ThrowsException<PortableSettingsException>(() => _ = OpenSettings(files.Path).Lang), files.Path, "Read");
            AssertFailure(Assert.ThrowsException<PortableSettingsException>(() => settings.Save()), files.Path, "Read");
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
    }

    /// <summary>書込みは許して置換を拒否する共有条件で保存し、直接上書きによる入力破壊を検出し、一時ファイルの回収を確認します。</summary>
    [TestMethod]
    public void ReplacementFailurePreservesBytesAndCleansTemporaryFile()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        Settings settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR";
        byte[] before = File.ReadAllBytes(files.Path);
        // 直接書込みはできるが置換できない条件にし、元ファイルを壊す実装を区別します。
        using (var blocker = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            AssertFailure(Assert.ThrowsException<PortableSettingsException>(() => settings.Save()), files.Path, "Save");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
            Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
        }
    }

    /// <summary>破損 XML の二回の起動で別々に原文を退避して新規作成前に警告し、旧設定再輸入や以前の退避上書きを防ぐことを確認します。</summary>
    [TestMethod]
    public void StartupCorruptionWarnsWithPreservedBackupBeforeCreatingDefaultsAndNeverImportsLegacy()
    {
        using var files = new SettingsFiles();
        byte[] corrupt = System.Text.Encoding.UTF8.GetBytes("<configuration><setting>lost?");
        string? earlierBackup = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            File.WriteAllBytes(files.Path, corrupt);
            string? backup = null;
            new PortableSettingsStartupFile(files.Path).Prepare(
                () => Assert.Fail("Corrupt portable input must not import legacy settings."),
                preserved =>
                {
                    backup = preserved;
                    Assert.IsFalse(File.Exists(files.Path), "Warning must precede fresh creation and first getter.");
                    CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(preserved));
                    if (earlierBackup != null)
                    {
                        CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(earlierBackup));
                    }
                });
            Assert.IsNotNull(backup);
            Assert.AreNotEqual(earlierBackup, backup);
            Assert.IsNull(OpenSettings(files.Path).AssemblyVersion);
            Assert.AreEqual(0, XDocument.Load(files.Path).Descendants("setting").Count());
            earlierBackup = backup;
        }
        Assert.AreEqual(2, Directory.GetFiles(files.Directory, "user.config.broken-*").Length);
    }

    /// <summary>XML として正常だが必須構造がない入力は Read 失敗とし、破損回復や旧設定輸入で元入力を置き換えないことを確認します。</summary>
    [DataTestMethod]
    [DataRow("<configuration />")]
    [DataRow("<configuration><userSettings /></configuration>")]
    public void StartupStructureFailureDoesNotQuarantineOrImport(string input)
    {
        int unexpectedNotificationCount = 0;
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, input);
        byte[] before = File.ReadAllBytes(files.Path);
        PortableSettingsException failure = Assert.ThrowsException<PortableSettingsException>(() =>
            new PortableSettingsStartupFile(files.Path).Prepare(
                () => Assert.Fail("Must not import."), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount)));
        AssertFailure(failure, files.Path, "Read");
        Assert.IsFalse(failure.InnerException is XmlException);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
        Assert.AreEqual(1, Directory.GetFiles(files.Directory).Length);
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>共有条件による Read 失敗と退避 rename 失敗の双方で、輸入・警告・新規作成を開始せず元入力を保つことを確認します。</summary>
    [TestMethod]
    public void StartupReadAndQuarantineFailuresDoNotWarnOrCreate()
    {
        int unexpectedNotificationCount = 0;
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, "<configuration>");
        byte[] before = File.ReadAllBytes(files.Path);
        foreach (FileShare share in new[] { FileShare.None, FileShare.Read })
        {
            using (var blocker = new FileStream(files.Path, FileMode.Open, FileAccess.Read, share))
            {
                PortableSettingsException failure = Assert.ThrowsException<PortableSettingsException>(() =>
                    new PortableSettingsStartupFile(files.Path).Prepare(
                        () => Assert.Fail("Must not import."), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount)));
                AssertFailure(failure, files.Path, share == FileShare.None ? "Read" : "Quarantine");
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
            Assert.AreEqual(1, Directory.GetFiles(files.Directory).Length);
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>退避警告中に作成先を占有し、退避内容を保って Create 失敗を返し、成功や一時ファイル残存へ進まないことを確認します。</summary>
    [TestMethod]
    public void StartupFreshCreateFailureIsFatalAfterBackupAndWarning()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, "<configuration>");
        byte[] before = File.ReadAllBytes(files.Path);
        string? backup = null;
        PortableSettingsException failure = Assert.ThrowsException<PortableSettingsException>(() =>
            new PortableSettingsStartupFile(files.Path).Prepare(() => Assert.Fail("Must not import."), preserved =>
            {
                backup = preserved;
                Directory.CreateDirectory(files.Path); // External path occupation between notice and create.
            }));
        AssertFailure(failure, files.Path, "Create");
        Assert.IsNotNull(backup);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(backup));
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    /// <summary>回復警告中に別のファイルを公開し、後続の新規作成が競合内容を上書きせず Create 失敗になることを確認します。</summary>
    [TestMethod]
    public void FreshCreationDoesNotOverwriteAFilePublishedDuringRecoveryWarning()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, "<configuration>");
        byte[] concurrent = System.Text.Encoding.UTF8.GetBytes(Config("Lang", "fr-FR"));
        PortableSettingsException failure = Assert.ThrowsException<PortableSettingsException>(() =>
            new PortableSettingsStartupFile(files.Path).Prepare(() => Assert.Fail(), _ => File.WriteAllBytes(files.Path, concurrent)));
        AssertFailure(failure, files.Path, "Create");
        CollectionAssert.AreEqual(concurrent, File.ReadAllBytes(files.Path));
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    /// <summary>設定ファイルなしでは輸入判断を一回行って有効な初回設定構造を作り、欠損を破損扱いする誤りを検出します。</summary>
    [TestMethod]
    public void StartupMissingFileCreatesValidFirstRunConfiguration()
    {
        int unexpectedNotificationCount = 0;
        using var files = new SettingsFiles();
        int imports = 0;
        new PortableSettingsStartupFile(files.Path).Prepare(() => imports++, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount));
        Assert.AreEqual(1, imports);
        Assert.IsNull(OpenSettings(files.Path).AssemblyVersion);
        Assert.IsNotNull(XDocument.Load(files.Path).Root!.Element("userSettings")!.Element(PortableSettingsProvider.SettingsSectionName));
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>専用パスの本番 provider を生成済み Settings wrapper に接続し、実保存・読戻しを確認できるようにします。</summary>
    internal static Settings OpenSettings(string path)
    {
        var settings = new Settings();
        var provider = new PortableSettingsProvider(path);
        provider.Initialize(nameof(PortableSettingsProvider), null!);
        settings.Providers.Clear();
        settings.Providers.Add(provider);
        foreach (SettingsProperty property in settings.Properties)
        {
            property.Provider = provider;
        }

        return settings;
    }

    private static void AssertFailure(PortableSettingsException failure, string path, string operation)
    {
        Assert.AreEqual(path, failure.FilePath);
        Assert.AreEqual(operation, failure.Operation);
        Assert.IsNotNull(failure.InnerException);
        StringAssert.Contains(failure.Message, path);
    }

    private static string Config(string? key = null, string? value = null) =>
        "<configuration><userSettings><BeMusicSeeker.Properties.Settings>" +
        (key == null ? "" : $"<setting name=\"{key}\" serializeAs=\"String\"><value>{value}</value></setting>") +
        "</BeMusicSeeker.Properties.Settings></userSettings></configuration>";

    /// <summary>保存テストが独占する設定ファイルと一時ディレクトリを所有します。</summary>
    internal sealed class SettingsFiles : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BmsSettings-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "user.config");
        internal SettingsFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.SetAttributes(Path, FileAttributes.Normal);
            }

            System.IO.Directory.Delete(Directory, true);
        }
    }
}
