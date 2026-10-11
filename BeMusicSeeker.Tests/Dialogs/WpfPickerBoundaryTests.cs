using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfPickerBoundaryTests
{
    /// <summary>ファイル・フォルダ・保存の要求を実picker設定へ渡し、拡張子推定・存在条件・複数選択を取り違えません。</summary>
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

        SaveFileDialog saveFileDialog = UiDialogCoordinator.CreateSaveFileDialog(saveRequest);
        Assert.AreEqual(saveRequest.Title, saveFileDialog.Title);
        Assert.AreEqual(saveRequest.FileName, saveFileDialog.FileName);
        Assert.AreEqual(saveRequest.DefaultExtension.TrimStart('.'), saveFileDialog.DefaultExt);
        Assert.AreEqual(saveRequest.Filter, saveFileDialog.Filter);
        Assert.IsTrue(saveFileDialog.AddExtension);
    }

    /// <summary>所有windowがないpicker要求は全種でOwnerUnavailableを返し、空の選択を受理結果にしません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void PickerCoordinatorMapsOwnerUnavailableForOpenFolderAndSaveRoutes()
    {
        TestUiDispatcherHost.Invoke(() =>
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

    /// <summary>有効filterの組と重複拡張子を保持し、不正filterだけを全ファイルの有効な組へ正規化します。</summary>
    [TestMethod]
    public void WpfPickerUtilityPreservesPairsAndNormalizesMalformedFilter()
    {
        Assert.AreEqual(
            "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*",
            UiFilePickerUtilities.BuildFilter("song.db (*.db)|*.db|すべてのファイル(*.*)|*.*"));
        Assert.AreEqual("*.*|*.*", UiFilePickerUtilities.BuildFilter("broken"));
        CollectionAssert.AreEqual(
            new[] { "*.db", "*.db" },
            UiFilePickerUtilities.ParseFilterPairs("db|*.db|all|*.db").Select(pair => pair.Item2).ToArray());


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
