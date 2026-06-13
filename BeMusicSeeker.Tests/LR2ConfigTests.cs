using System;
using System.IO;
using System.Text;
using System.Xml.Linq;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LR2ConfigTests
{
    [TestMethod]
    public void EnsureDatabaseAutoReloadManualOnly_ChangesExistingAutoReloadMode()
    {
        WithConfig("<config><system><autoreload>2</autoreload></system><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            bool changed = config.EnsureDatabaseAutoReloadManualOnly();

            Assert.IsTrue(changed);
            Assert.AreEqual(LR2Config.DatabaseAutoReloadManualOnly, config.GetDatabaseAutoReloadMode());
            config.Save();
            Assert.AreEqual("0", XDocument.Load(configPath).Element("config")?.Element("system")?.Element("autoreload")?.Value);
        });
    }

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

    [TestMethod]
    public void EnsureDatabaseAutoReloadManualOnly_AddsMissingAutoReloadElement()
    {
        WithConfig("<config><system><customfolder>0</customfolder></system><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            bool changed = config.EnsureDatabaseAutoReloadManualOnly();

            Assert.IsTrue(changed);
            Assert.AreEqual(LR2Config.DatabaseAutoReloadManualOnly, config.GetDatabaseAutoReloadMode());
            config.Save();
            Assert.AreEqual("0", XDocument.Load(configPath).Element("config")?.Element("system")?.Element("autoreload")?.Value);
        });
    }

    [TestMethod]
    public void AddBMSSearchDirectories_RejectsNestedPathsWithinSameRequest()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string rootPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath))), "BMS");
            string childPath = Path.Combine(rootPath, "Child");
            Directory.CreateDirectory(childPath);

            Assert.ThrowsException<ArgumentException>(() => config.AddBMSSearchDirectories([rootPath, childPath]));
        });
    }

    private static void WithConfig(string xml, Action<string, LR2Config> action)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LR2Config_" + Guid.NewGuid().ToString("N"));
        string configDirectoryPath = Path.Combine(tempRootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectoryPath);
        string configPath = Path.Combine(configDirectoryPath, "config.xml");
        File.WriteAllText(configPath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            action(configPath, new LR2Config(configPath));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
            catch
            {
            }
        }
    }
}
