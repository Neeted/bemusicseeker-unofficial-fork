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
    public void InstallBMSPackagesForce_UsesDialogServiceForOverrideConfirmation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            RecordingDialogService dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.No
            };
            BMSLibrary library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            TestableBmsFile pendingFile = new TestableBmsFile
            {
                path = "C:\\Pending\\Pkg\\chart.bms",
                instl_dst = "C:\\Installed\\Pkg"
            };
            BMSPackage pendingPackage = new BMSPackage(new BMSFile[] { pendingFile })
            {
                path = "C:\\Pending\\Pkg",
                delete_parent = false
            };
            library.BMSPackagesPending = CreatePackageCollection(new[] { pendingPackage });

            library.InstallBMSPackagesForce(new[] { pendingPackage });

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.Confirm_NormalInstallTitle, dialogService.Calls[0].Caption);
            Assert.AreEqual(MessageBoxButton.YesNo, dialogService.Calls[0].Button);
            Assert.AreSame(pendingPackage, library.BMSPackagesPending.Single());
        });
    }

    [TestMethod]
    public void RenameBMSFolder_UsesDialogServiceForMissingFolderWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            RecordingDialogService dialogService = new RecordingDialogService();
            BMSLibrary library = new BMSLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Missing_" + Guid.NewGuid().ToString("N"));

            library.RenameBMSFolder(missingDirectoryPath, "RenamedFolder");

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, dialogService.Calls[0].Caption);
            StringAssert.Contains(dialogService.Calls[0].Message, missingDirectoryPath);
        });
    }

    private static DispatcherCollection<BMSPackage> CreatePackageCollection(IEnumerable<BMSPackage> packages)
    {
        return new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>((packages ?? Enumerable.Empty<BMSPackage>()).ToList()), Dispatcher.CurrentDispatcher);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_DialogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, Array.Empty<byte>());
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
        public List<DialogCall> Calls { get; } = new List<DialogCall>();

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
