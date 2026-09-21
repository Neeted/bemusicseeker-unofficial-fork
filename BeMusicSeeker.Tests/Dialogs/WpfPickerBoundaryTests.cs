using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using Parago.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfPickerBoundaryTests
{
    [TestMethod]
    public void PickerFactoriesMapCompiledOptionsWithoutShowingInteractiveDialogs()
    {
        string root = FindRepositoryRoot();
        var fileRequest = new UiFilePickerRequest(
            "Open song",
            "song.db",
            root,
            "song.db (*.db)|*.db|All files|*.*",
            defaultExtension: null,
            multiselect: true,
            ensureFileExists: false,
            ensurePathExists: false);
        Assert.AreEqual("Open song", fileRequest.Title);
        Assert.AreEqual("song.db", fileRequest.FileName);
        Assert.AreEqual(root, fileRequest.InitialDirectory);
        Assert.AreEqual("song.db (*.db)|*.db|All files|*.*", fileRequest.Filter);
        Assert.IsTrue(fileRequest.Multiselect);
        Assert.IsFalse(fileRequest.EnsureFileExists);
        Assert.IsFalse(fileRequest.EnsurePathExists);
        Assert.AreEqual("db", UiFilePickerUtilities.InferDefaultExtension(fileRequest.FileName, fileRequest.Filter));
        Assert.AreEqual(fileRequest.Filter, UiFilePickerUtilities.BuildFilter(fileRequest.Filter));

        OpenFileDialog openFileDialog = UiDialogCoordinator.CreateOpenFileDialog(fileRequest);
        Assert.AreEqual(fileRequest.Title, openFileDialog.Title);
        Assert.AreEqual(fileRequest.FileName, openFileDialog.FileName);
        Assert.AreEqual(root, openFileDialog.InitialDirectory);
        Assert.AreEqual(fileRequest.Filter, openFileDialog.Filter);
        Assert.IsFalse(openFileDialog.CheckFileExists);
        Assert.IsFalse(openFileDialog.CheckPathExists);
        Assert.IsTrue(openFileDialog.Multiselect);
        Assert.AreEqual("db", openFileDialog.DefaultExt);
        Assert.IsTrue(openFileDialog.AddExtension);

        var folderRequest = new UiFolderPickerRequest("Select roots", root, multiselect: true);
        Assert.AreEqual("Select roots", folderRequest.Title);
        Assert.AreEqual(root, folderRequest.SelectedPath);
        Assert.IsTrue(folderRequest.Multiselect);

        OpenFolderDialog openFolderDialog = UiDialogCoordinator.CreateOpenFolderDialog(folderRequest);
        Assert.AreEqual(folderRequest.Title, openFolderDialog.Title);
        Assert.AreEqual(root, openFolderDialog.InitialDirectory);
        Assert.IsTrue(openFolderDialog.Multiselect);

        var saveRequest = new UiSaveFilePickerRequest(
            "Save backup",
            "backup.sql",
            ".sql",
            "sql files|*.sql",
            addExtension: true);
        Assert.AreEqual("Save backup", saveRequest.Title);
        Assert.AreEqual("backup.sql", saveRequest.FileName);
        Assert.AreEqual(".sql", saveRequest.DefaultExtension);
        Assert.AreEqual("sql files|*.sql", saveRequest.Filter);
        Assert.IsTrue(saveRequest.AddExtension);

        SaveFileDialog saveFileDialog = UiDialogCoordinator.CreateSaveFileDialog(saveRequest);
        Assert.AreEqual(saveRequest.Title, saveFileDialog.Title);
        Assert.AreEqual(saveRequest.FileName, saveFileDialog.FileName);
        Assert.AreEqual(saveRequest.DefaultExtension.TrimStart('.'), saveFileDialog.DefaultExt);
        Assert.AreEqual(saveRequest.Filter, saveFileDialog.Filter);
        Assert.IsTrue(saveFileDialog.AddExtension);
    }

    [TestMethod]
    public void PickerCoordinatorMapsOwnerUnavailableForOpenFolderAndSaveRoutes()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var coordinator = new UiDialogCoordinator(new UiDialogOwnerResolver(() => null));
            UiFilePickerResult file = coordinator.PickFileAsync(new UiFilePickerRequest()).GetAwaiter().GetResult();
            UiFolderPickerResult folder = coordinator.PickFolderAsync(new UiFolderPickerRequest()).GetAwaiter().GetResult();
            UiSaveFilePickerResult save = coordinator.PickSaveFileAsync(new UiSaveFilePickerRequest()).GetAwaiter().GetResult();

            Assert.AreEqual(UiDialogStatus.OwnerUnavailable, file.Status);
            Assert.AreEqual(UiDialogStatus.OwnerUnavailable, folder.Status);
            Assert.AreEqual(UiDialogStatus.OwnerUnavailable, save.Status);
            Assert.AreEqual(0, file.FileNames.Count);
            Assert.AreEqual(0, folder.FolderPaths.Count);
            Assert.IsNull(save.FileName);
        });
    }

    [TestMethod]
    public void WpfPickerUtilityBuildsNormalizedFilterAndRetiresCodePackDistribution()
    {
        string root = FindRepositoryRoot();
        Assert.AreEqual(
            "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*",
            UiFilePickerUtilities.BuildFilter("song.db (*.db)|*.db|すべてのファイル(*.*)|*.*"));
        Assert.AreEqual("*.*|*.*", UiFilePickerUtilities.BuildFilter("broken"));
        CollectionAssert.AreEqual(
            new[] { "*.db", "*.db" },
            UiFilePickerUtilities.ParseFilterPairs("db|*.db|all|*.db").Select(pair => pair.Item2).ToArray());

        string project = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "BeMusicSeeker.csproj"));
        string layout = File.ReadAllText(Path.Combine(root, "scripts", "portable-package-layout.ps1"));
        string notices = File.ReadAllText(Path.Combine(root, "ThirdPartyNotices.txt"));
        string japaneseNotices = File.ReadAllText(Path.Combine(root, "ThirdPartyNotices.ja.txt"));
        foreach (string assemblyName in new[]
        {
            "Microsoft.WindowsAPICodePack.dll",
            "Microsoft.WindowsAPICodePack.Shell.dll"
        })
        {
            Assert.IsFalse(project.Contains(assemblyName, StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(notices.Contains(assemblyName, StringComparison.Ordinal), assemblyName);
            Assert.IsFalse(japaneseNotices.Contains(assemblyName, StringComparison.Ordinal), assemblyName);
            StringAssert.Contains(layout, "\"" + assemblyName + "\"");
            StringAssert.Contains(layout, "\"libs/" + assemblyName + "\"");
            Assert.IsFalse(File.Exists(Path.Combine(root, "libs", assemblyName)), assemblyName);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new AssertFailedException("Could not locate repository root.");
    }
}
