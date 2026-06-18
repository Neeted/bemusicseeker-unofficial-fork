using System;
using System.IO;
using System.Reflection;
using System.Text;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingDialogCustomFolderOutputBaseTests
{
    [TestMethod]
    public void Lr2BmsDirectoryChoices_ExcludeManagedCustomFolderOutputBases()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string additionalOutputBase = Path.Combine(tempRootPath, "AdditionalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([manualBmsRoot, normalOutputBase, additionalOutputBase, rootOutputChild]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            Settings.Default.BMSInstallDir = manualBmsRoot;
            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();
            dialog.CustomFolderAdditionalOutputBaseDirList.Add(additionalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);

            CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories, manualBmsRoot);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, normalOutputBase);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, additionalOutputBase);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, rootOutputChild);
            CollectionAssert.Contains(dialog.AvailableBMSDirectories, manualBmsRoot);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, normalOutputBase);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, additionalOutputBase);
            CollectionAssert.DoesNotContain(dialog.AvailableBMSDirectories, rootOutputChild);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBase_ChildJukeboxRowsDoNotInvalidateRootOutputBase()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([manualBmsRoot, normalOutputBase, rootOutputChild]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = manualBmsRoot;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            object?[] args = [rootOutputBase, null];
            bool isValid = (bool)typeof(MainWindowViewModel.SettingDialogViewModel)
                .GetMethod("ValidateCustomFolderAsRootOutputBaseDir", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(string), typeof(string).MakeByRefType()], null)!
                .Invoke(dialog, args)!;

            Assert.IsTrue(isValid, args[1] as string ?? string.Empty);
            CollectionAssert.Contains(dialog.LR2ConfigBMSDirectories, manualBmsRoot);
            CollectionAssert.DoesNotContain(dialog.LR2ConfigBMSDirectories, rootOutputChild);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void StartupRepair_MovesManagedOutputInstallDirToFirstUserBmsRoot()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string previousBmsInstallDir = Settings.Default.BMSInstallDir;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "PlaylistOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputChild);

            LR2Config config = CreateConfig(tempRootPath);
            config.SetBMSSearchDirectories([manualBmsRoot, normalOutputBase, rootOutputBase, rootOutputChild]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.BMSInstallDir = rootOutputChild;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            bool repaired = (bool)typeof(MainWindowViewModel)
                .GetMethod("RepairBmsInstallDirIfManagedOutputSearchRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(viewModel, null)!;

            Assert.IsTrue(repaired);
            Assert.AreEqual(manualBmsRoot, Settings.Default.BMSInstallDir);
            Assert.AreEqual(manualBmsRoot, GetDialogField<string>(dialog, "tempBMSInstallDir"));
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            Settings.Default.BMSInstallDir = previousBmsInstallDir;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void NormalOutputBase_CannotBeChangedToManualBmsRoot()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRoot = Path.Combine(tempRootPath, "ManualBmsRoot");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([manualBmsRoot, normalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            dialog.LR2CustomFolderOutputDir = manualBmsRoot;

            Assert.AreEqual(normalOutputBase, Settings.Default.LR2CustomFolderOutputBaseDir);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void RootOutputBase_CannotBeChangedToManualBmsRootParent()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string manualBmsRootParent = Path.Combine(tempRootPath, "ManualBmsRootParent");
            string manualBmsRoot = Path.Combine(manualBmsRootParent, "Songs");
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            Directory.CreateDirectory(manualBmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([manualBmsRoot, normalOutputBase, rootOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", "[]");

            dialog.LR2CustomFolderAsRootOutputDir = manualBmsRootParent;

            Assert.AreEqual(rootOutputBase, Settings.Default.LR2CustomFolderOutputBaseDirRootType);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    [TestMethod]
    public void AdditionalOutputBaseRename_CannotMoveToOldAdditionalOutputBaseParent()
    {
        string previousNormalOutputBase = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBase = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBases = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialog_" + Guid.NewGuid().ToString("N"));
        try
        {
            string normalOutputBase = Path.Combine(tempRootPath, "NormalOutput");
            string rootOutputBase = Path.Combine(tempRootPath, "RootOutput");
            string additionalOutputBaseParent = Path.Combine(tempRootPath, "AdditionalParent");
            string additionalOutputBase = Path.Combine(additionalOutputBaseParent, "AdditionalOutput");
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(rootOutputBase);
            Directory.CreateDirectory(additionalOutputBase);

            LR2Config config = CreateConfig(tempRootPath);
            config.AddBMSSearchDirectories([normalOutputBase, rootOutputBase, additionalOutputBase]);
            MainWindowViewModel viewModel = CreateViewModel(config);
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            dialog.CustomFolderAdditionalOutputBaseDirList.Clear();
            dialog.CustomFolderAdditionalOutputBaseDirList.Add(additionalOutputBase);
            dialog.SelectedCustomFolderAdditionalOutputBaseDir = additionalOutputBase;
            SetDialogField(dialog, "tempLR2CustomFolderOutputDir", normalOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAsRootOutputDir", rootOutputBase);
            SetDialogField(dialog, "tempLR2CustomFolderAdditionalOutputBaseDirs", Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);

            dialog.SelectedCustomFolderAdditionalOutputBaseName = ".";
            dialog.RenameSelectedCustomFolderAdditionalOutputBaseDir();

            CollectionAssert.Contains(dialog.CustomFolderAdditionalOutputBaseDirList, additionalOutputBase);
            CollectionAssert.DoesNotContain(dialog.CustomFolderAdditionalOutputBaseDirList, additionalOutputBaseParent);
        }
        finally
        {
            Settings.Default.LR2CustomFolderOutputBaseDir = previousNormalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBases;
            TryDeleteDirectory(tempRootPath);
        }
    }

    private static MainWindowViewModel CreateViewModel(LR2Config config)
    {
        var viewModel = new MainWindowViewModel();
        typeof(MainWindowViewModel)
            .GetField("lr2config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, config);
        SetDialogField(viewModel.settingDialog, "operationModeLR2DB", true);
        return viewModel;
    }

    private static LR2Config CreateConfig(string tempRootPath)
    {
        string configDirectoryPath = Path.Combine(tempRootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectoryPath);
        string configPath = Path.Combine(configDirectoryPath, "config.xml");
        File.WriteAllText(configPath, "<config><system /><jukebox /></config>", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new LR2Config(configPath);
    }

    private static void SetDialogField(MainWindowViewModel.SettingDialogViewModel dialog, string fieldName, object value)
    {
        typeof(MainWindowViewModel.SettingDialogViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(dialog, value);
    }

    private static T GetDialogField<T>(MainWindowViewModel.SettingDialogViewModel dialog, string fieldName)
    {
        return (T)typeof(MainWindowViewModel.SettingDialogViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(dialog)!;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
