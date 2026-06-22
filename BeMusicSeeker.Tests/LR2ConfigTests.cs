using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
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

    [TestMethod]
    public void GetBMSSearchDirectoriesReadOnly_DoesNotRewriteConfig()
    {
        WithConfig("<config><system /><jukebox><path>BMS\\</path><path>Missing\\</path></jukebox></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
            string bmsRoot = Path.Combine(tempRoot, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string before = File.ReadAllText(configPath);

            var directories = config.GetBMSSearchDirectoriesReadOnly();

            CollectionAssert.AreEqual(new[] { bmsRoot }, directories);
            Assert.AreEqual(before, File.ReadAllText(configPath));
        });
    }

    [TestMethod]
    public void RepairNormalOutputBaseRoots_AddsConfiguredDefaultAndAdditionalRoots()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
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

    [TestMethod]
    public void RepairNormalOutputBaseRoots_DoesNotRewriteExistingRoots()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
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

    [TestMethod]
    public void RepairNormalOutputBaseRoots_AdoptsChildOfRegisteredBmsRoot()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
            string bmsRoot = Path.Combine(tempRoot, "BMS");
            string defaultOutputBase = Path.Combine(bmsRoot, "DefaultOutput");
            Directory.CreateDirectory(defaultOutputBase);
            config.AddBMSSearchDirectories([bmsRoot]);

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(config, defaultOutputBase, "[]");

            Assert.IsTrue(result.Changed);
            Assert.AreEqual(1, result.AddedCount);
            Assert.AreEqual(1, result.RemovedCount);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), bmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), defaultOutputBase);
        });
    }

    [TestMethod]
    public void RepairNormalOutputBaseRoots_RejectsInvalidAdditionalOutputBaseJson()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
            string defaultOutputBase = Path.Combine(tempRoot, "DefaultOutput");
            Directory.CreateDirectory(defaultOutputBase);

            Assert.ThrowsException<ArgumentException>(() =>
                CustomFolderOutputBaseSearchRootSyncService.RepairNormalOutputBaseRoots(config, defaultOutputBase, "{"));
        });
    }

    [TestMethod]
    public void SyncAdditionalOutputBaseRoots_AddsAndRemovesJukeboxPath()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
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

    [TestMethod]
    public void SyncAdditionalOutputBaseRoots_AdoptsChildOfRegisteredBmsRoot()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
            string bmsRoot = Path.Combine(tempRoot, "BMS");
            string additionalBase = Path.Combine(bmsRoot, "Additional");
            Directory.CreateDirectory(additionalBase);
            config.AddBMSSearchDirectories([bmsRoot]);
            string serializedAdditionalBase = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalBase]);

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.SyncAdditionalOutputBaseRoots(config, "[]", serializedAdditionalBase);

            Assert.IsTrue(result.Changed);
            Assert.AreEqual(1, result.AddedCount);
            Assert.AreEqual(1, result.RemovedCount);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), bmsRoot);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), additionalBase);
        });
    }

    [TestMethod]
    public void SyncAdditionalOutputBaseRoots_DoesNotRemovePathPreservedByNormalOutputBase()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
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

    [TestMethod]
    public void PrepareNormalOutputBaseRoots_AddsNewDefaultAndRemovesOldDefault()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
            string oldDefaultBase = Path.Combine(tempRoot, "OldDefault");
            string newDefaultBase = Path.Combine(tempRoot, "NewDefault");
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

            CollectionAssert.Contains(config.GetBMSSearchDirectories(), oldDefaultBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), newDefaultBase);
            CollectionAssert.Contains(plan.RemovedPaths.ToArray(), oldDefaultBase);

            CustomFolderOutputBaseSearchRootSyncResult result =
                CustomFolderOutputBaseSearchRootSyncService.CompleteAdditionalOutputBaseRootSync(config, plan);

            Assert.IsTrue(result.Changed);
            Assert.AreEqual(1, result.AddedCount);
            Assert.AreEqual(1, result.RemovedCount);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), oldDefaultBase);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), newDefaultBase);
        });
    }

    [TestMethod]
    public void PrepareNormalOutputBaseRoots_PreservesOldAdditionalWhenPromotedToDefault()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
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

    [TestMethod]
    public void PrepareAdditionalOutputBaseRoots_AddsNewRootBeforeOldRootRemoval()
    {
        WithConfig("<config><system /><jukebox /></config>", delegate (string configPath, LR2Config config)
        {
            string tempRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(configPath)));
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
