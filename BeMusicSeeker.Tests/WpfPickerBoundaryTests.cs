using System;
using System.IO;
using System.Reflection;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfPickerBoundaryTests
{
    [TestMethod]
    public void PickerCoordinatorMapsTypedRequestsToWpfDialogOptions()
    {
        Assembly assembly = typeof(MainWindow).Assembly;
        Type coordinatorType = assembly.GetType("BeMusicSeeker.Views.Dialogs.UiDialogCoordinator");
        Type fileRequestType = assembly.GetType("BeMusicSeeker.Views.Dialogs.UiFilePickerRequest");
        Type folderRequestType = assembly.GetType("BeMusicSeeker.Views.Dialogs.UiFolderPickerRequest");
        Assert.IsNotNull(coordinatorType);
        Assert.IsNotNull(fileRequestType);
        Assert.IsNotNull(folderRequestType);

        object fileRequest = Activator.CreateInstance(
            fileRequestType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: ["Open song", "song.db", FindRepositoryRoot(), "song.db (*.db)|*.db|All files|*.*", null, true, false, false, null],
            culture: null);
        MethodInfo fileFactory = coordinatorType.GetMethod("CreateOpenFileDialog", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(fileFactory);
        var fileDialog = (OpenFileDialog)fileFactory.Invoke(null, [fileRequest]);
        Assert.AreEqual("Open song", fileDialog.Title);
        Assert.AreEqual("song.db", fileDialog.FileName);
        Assert.AreEqual(FindRepositoryRoot(), fileDialog.InitialDirectory);
        Assert.AreEqual("song.db (*.db)|*.db|All files|*.*", fileDialog.Filter);
        Assert.AreEqual("db", fileDialog.DefaultExt);
        Assert.IsTrue(fileDialog.AddExtension);
        Assert.IsTrue(fileDialog.Multiselect);
        Assert.IsFalse(fileDialog.CheckFileExists);
        Assert.IsFalse(fileDialog.CheckPathExists);

        object folderRequest = Activator.CreateInstance(
            folderRequestType,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: ["Select roots", FindRepositoryRoot(), true, null],
            culture: null);
        MethodInfo folderFactory = coordinatorType.GetMethod("CreateOpenFolderDialog", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(folderFactory);
        var folderDialog = (OpenFolderDialog)folderFactory.Invoke(null, [folderRequest]);
        Assert.AreEqual("Select roots", folderDialog.Title);
        Assert.AreEqual(FindRepositoryRoot(), folderDialog.InitialDirectory);
        Assert.IsTrue(folderDialog.Multiselect);
    }

    [TestMethod]
    public void WpfPickerUtilityBuildsNormalizedFilterAndRetiresCodePackDistribution()
    {
        string root = FindRepositoryRoot();
        Type utilityType = typeof(MainWindow).Assembly.GetType("BeMusicSeeker.Views.Dialogs.UiFilePickerUtilities");
        Assert.IsNotNull(utilityType);
        MethodInfo buildFilterMethod = utilityType.GetMethod("BuildFilter", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(buildFilterMethod);
        Assert.AreEqual(
            "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*",
            buildFilterMethod.Invoke(null, ["song.db (*.db)|*.db|すべてのファイル(*.*)|*.*"]));
        Assert.AreEqual("*.*|*.*", buildFilterMethod.Invoke(null, ["broken"]));

        string project = File.ReadAllText(Path.Combine(root, "BeMusicSeeker.csproj"));
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
        DirectoryInfo directory = new(AppContext.BaseDirectory);
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
