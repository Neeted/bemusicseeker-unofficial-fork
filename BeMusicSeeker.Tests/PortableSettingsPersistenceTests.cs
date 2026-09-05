using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

// PORTABLE-SETTINGS-FAILURE-20260905 P01-P04: explicit path, real filesystem faults,
// user-owned input byte preservation. No process-global Settings or path changes.
[TestClass]
public sealed class PortableSettingsPersistenceTests
{
    [TestMethod]
    public void SaveRoundTripsThroughFreshGeneratedSettingsAndPreservesUnknownKeys()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US").Replace("</BeMusicSeeker", "<setting name=\"FutureKey\" serializeAs=\"String\"><value>retain</value></setting></BeMusicSeeker"));
        var settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR";
        settings.Save();
        Assert.AreEqual("fr-FR", OpenSettings(files.Path).Lang);
        Assert.AreEqual("retain", XDocument.Load(files.Path).Descendants("setting").Single(e => (string?)e.Attribute("name") == "FutureKey").Element("value")!.Value);
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    [TestMethod]
    public void MissingFileAndIndividualKeyUseDefaults()
    {
        using var files = new SettingsFiles();
        string defaultLanguage = OpenSettings(files.Path).Lang;
        File.WriteAllText(files.Path, Config());
        Assert.AreEqual(defaultLanguage, OpenSettings(files.Path).Lang);
        Assert.IsNull(OpenSettings(files.Path).AssemblyVersion);
    }

    [DataTestMethod]
    [DataRow("<configuration>")]
    [DataRow("<other><userSettings><BeMusicSeeker.Properties.Settings /></userSettings></other>")]
    [DataRow("<configuration />")]
    [DataRow("<configuration><userSettings /></configuration>")]
    public void ExistingInvalidInputFailsReadAndSaveWithoutReplacingBytes(string input)
    {
        using var files = new SettingsFiles();
        var settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR"; // Materialize while missing, then model an external edit before Save.
        File.WriteAllText(files.Path, input);
        byte[] before = File.ReadAllBytes(files.Path);
        var read = Assert.ThrowsException<PortableSettingsException>(() => _ = OpenSettings(files.Path).Lang);
        AssertFailure(read, files.Path, "Read");
        var save = Assert.ThrowsException<PortableSettingsException>(() => settings.Save());
        AssertFailure(save, files.Path, "Read");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
        Assert.AreEqual(1, Directory.GetFiles(files.Directory).Length);
    }

    [TestMethod]
    public void SupportedStringSerializationFailurePreservesOriginalBytes()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        byte[] before = File.ReadAllBytes(files.Path);
        var settings = OpenSettings(files.Path);
        settings.Lang = "invalid XML character: \u0001";
        AssertFailure(Assert.ThrowsException<PortableSettingsException>(() => settings.Save()), files.Path, "Save");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    [TestMethod]
    public void ReadOnlySaveFailsWithPathAndCauseAndPreservesBytes()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        var settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR";
        byte[] before = File.ReadAllBytes(files.Path);
        File.SetAttributes(files.Path, FileAttributes.ReadOnly);
        var failure = Assert.ThrowsException<PortableSettingsException>(() => settings.Save());
        AssertFailure(failure, files.Path, "Save");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    [TestMethod]
    public void ReadSharingFailureDoesNotBecomeEmptySettingsOrOverwriteInput()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        var settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR";
        byte[] before = File.ReadAllBytes(files.Path);
        using (var blocker = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AssertFailure(Assert.ThrowsException<PortableSettingsException>(() => _ = OpenSettings(files.Path).Lang), files.Path, "Read");
            AssertFailure(Assert.ThrowsException<PortableSettingsException>(() => settings.Save()), files.Path, "Read");
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
    }

    [TestMethod]
    public void ReplacementFailurePreservesBytesAndCleansTemporaryFile()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        var settings = OpenSettings(files.Path);
        settings.Lang = "fr-FR";
        byte[] before = File.ReadAllBytes(files.Path);
        // Deny DELETE/replacement but permit WRITE so an incorrect direct-write implementation is distinguishable.
        using (var blocker = new FileStream(files.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            AssertFailure(Assert.ThrowsException<PortableSettingsException>(() => settings.Save()), files.Path, "Save");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
            Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
        }
    }

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
                    if (earlierBackup != null) CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(earlierBackup));
                });
            Assert.IsNotNull(backup);
            Assert.AreNotEqual(earlierBackup, backup);
            Assert.IsNull(OpenSettings(files.Path).AssemblyVersion);
            Assert.AreEqual(0, XDocument.Load(files.Path).Descendants("setting").Count());
            earlierBackup = backup;
        }
        Assert.AreEqual(2, Directory.GetFiles(files.Directory, "user.config.broken-*").Length);
    }

    [DataTestMethod]
    [DataRow("<configuration />")]
    [DataRow("<configuration><userSettings /></configuration>")]
    public void StartupStructureFailureDoesNotQuarantineOrImport(string input)
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, input);
        byte[] before = File.ReadAllBytes(files.Path);
        var failure = Assert.ThrowsException<PortableSettingsException>(() =>
            new PortableSettingsStartupFile(files.Path).Prepare(
                () => Assert.Fail("Must not import."), _ => Assert.Fail("Must not recover.")));
        AssertFailure(failure, files.Path, "Read");
        Assert.IsFalse(failure.InnerException is XmlException);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
        Assert.AreEqual(1, Directory.GetFiles(files.Directory).Length);
    }

    [TestMethod]
    public void StartupReadAndQuarantineFailuresDoNotWarnOrCreate()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, "<configuration>");
        byte[] before = File.ReadAllBytes(files.Path);
        foreach (FileShare share in new[] { FileShare.None, FileShare.Read })
        {
            using (var blocker = new FileStream(files.Path, FileMode.Open, FileAccess.Read, share))
            {
                var failure = Assert.ThrowsException<PortableSettingsException>(() =>
                    new PortableSettingsStartupFile(files.Path).Prepare(
                        () => Assert.Fail("Must not import."), _ => Assert.Fail("Must not warn after failed read/move.")));
                AssertFailure(failure, files.Path, share == FileShare.None ? "Read" : "Quarantine");
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path));
            Assert.AreEqual(1, Directory.GetFiles(files.Directory).Length);
        }
    }

    [TestMethod]
    public void StartupFreshCreateFailureIsFatalAfterBackupAndWarning()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, "<configuration>");
        byte[] before = File.ReadAllBytes(files.Path);
        string? backup = null;
        var failure = Assert.ThrowsException<PortableSettingsException>(() =>
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

    [TestMethod]
    public void FreshCreationDoesNotOverwriteAFilePublishedDuringRecoveryWarning()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, "<configuration>");
        byte[] concurrent = System.Text.Encoding.UTF8.GetBytes(Config("Lang", "fr-FR"));
        var failure = Assert.ThrowsException<PortableSettingsException>(() =>
            new PortableSettingsStartupFile(files.Path).Prepare(() => Assert.Fail(), _ => File.WriteAllBytes(files.Path, concurrent)));
        AssertFailure(failure, files.Path, "Create");
        CollectionAssert.AreEqual(concurrent, File.ReadAllBytes(files.Path));
        Assert.AreEqual(0, Directory.GetFiles(files.Directory, "*.tmp").Length);
    }

    [TestMethod]
    public void StartupMissingFileCreatesValidFirstRunConfiguration()
    {
        using var files = new SettingsFiles();
        int imports = 0;
        new PortableSettingsStartupFile(files.Path).Prepare(() => imports++, _ => Assert.Fail("Missing is not corruption."));
        Assert.AreEqual(1, imports);
        Assert.IsNull(OpenSettings(files.Path).AssemblyVersion);
        Assert.IsNotNull(XDocument.Load(files.Path).Root!.Element("userSettings")!.Element(PortableSettingsProvider.SettingsSectionName));
    }

    /// <summary>Uses the generated settings wrapper with an explicit instance-owned production provider.</summary>
    internal static Settings OpenSettings(string path)
    {
        var settings = new Settings();
        var provider = new PortableSettingsProvider(path);
        provider.Initialize(nameof(PortableSettingsProvider), null!);
        settings.Providers.Clear();
        settings.Providers.Add(provider);
        foreach (SettingsProperty property in settings.Properties) property.Provider = provider;
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

    private sealed class SettingsFiles : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BmsSettings-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(Directory, "user.config");
        internal SettingsFiles() => System.IO.Directory.CreateDirectory(Directory);
        public void Dispose()
        {
            if (File.Exists(Path)) File.SetAttributes(Path, FileAttributes.Normal);
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
