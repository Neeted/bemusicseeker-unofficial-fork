using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryDialogRoutingTests
{
    [TestMethod]
    public void ForceInstallPendingPackages_UsesDialogServiceForOverrideConfirmation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.No
            };
            var library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            var pendingFile = new TestableBmsFile
            {
                path = "C:\\Pending\\Pkg\\chart.bms",
                instl_dst = "C:\\Installed\\Pkg"
            };
            var pendingPackage = new ChartPackage([pendingFile])
            {
                path = "C:\\Pending\\Pkg",
                delete_parent = false
            };
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);

            library.ForceInstallPendingPackages([pendingPackage]);

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.Confirm_NormalInstallTitle, dialogService.Calls[0].Caption);
            Assert.AreEqual(MessageBoxButton.YesNo, dialogService.Calls[0].Button);
            Assert.AreSame(pendingPackage, library.ChartPackagesPending.Single());
        });
    }

    [TestMethod]
    public void RenameBMSFolder_UsesDialogServiceForMissingFolderWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService();
            var library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Missing_" + Guid.NewGuid().ToString("N"));

            library.RenameBMSFolder(missingDirectoryPath, "RenamedFolder");

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, dialogService.Calls[0].Caption);
            StringAssert.Contains(dialogService.Calls[0].Message, missingDirectoryPath);
        });
    }

    private static DispatcherCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>([.. (packages ?? [])]), Dispatcher.CurrentDispatcher);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DialogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public List<DialogCall> Calls { get; } = [];

        public MessageBoxResult ResultToReturn { get; set; } = MessageBoxResult.OK;

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            Calls.Add(new DialogCall
            {
                Message = messageBoxText,
                Caption = caption,
                Button = button,
                Icon = icon,
                DefaultResult = defaultResult
            });
            return ResultToReturn;
        }
    }

    private sealed class DialogCall
    {
        public string Message { get; set; } = string.Empty;

        public string Caption { get; set; } = string.Empty;

        public MessageBoxButton Button { get; set; }

        public MessageBoxImage Icon { get; set; }

        public MessageBoxResult DefaultResult { get; set; }
    }

    private sealed class TestableBmsFile : BMSFile
    {
    }
}
