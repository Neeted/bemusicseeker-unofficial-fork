using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
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
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);
            var pendingFile = new TestableBmsFile
            {
                path = "C:\\Pending\\Pkg\\chart.bms"
            };
            var pendingPackage = ChartPackage.FromChartEntries(
                [ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, "C:\\Installed\\Pkg")]);
            pendingPackage.path = "C:\\Pending\\Pkg";
            pendingPackage.delete_parent = false;
            library.ChartPackagesPending = CreatePackageCollection([pendingPackage]);

            library.ForceInstallPendingPackages([pendingPackage]);

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.Confirm_NormalInstallTitle, dialogService.Calls[0].Caption);
            Assert.AreEqual(MessageBoxButton.YesNo, dialogService.Calls[0].Button);
            Assert.AreSame(pendingPackage, library.ChartPackagesPending.Single());
        });
    }

    [TestMethod]
    public void RenameChartFolder_UsesDialogServiceForMissingFolderWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);
            string missingDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Missing_" + Guid.NewGuid().ToString("N"));

            library.RenameChartFolder(missingDirectoryPath, "RenamedFolder");

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, dialogService.Calls[0].Caption);
            StringAssert.Contains(dialogService.Calls[0].Message, missingDirectoryPath);
        });
    }

    [TestMethod]
    public void ShowEverythingFallbackWarning_UsesDialogService()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string fallbackReason = "empty_results_with_roots";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);

            library.ShowEverythingFallbackWarning(fallbackReason);

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, dialogService.Calls[0].Caption);
            Assert.AreEqual(MessageBoxButton.OK, dialogService.Calls[0].Button);
            Assert.AreEqual(MessageBoxImage.Exclamation, dialogService.Calls[0].Icon);
            Assert.AreEqual(MessageBoxResult.OK, dialogService.Calls[0].DefaultResult);
            StringAssert.Contains(dialogService.Calls[0].Message, "Everything");
            StringAssert.Contains(dialogService.Calls[0].Message, fallbackReason);
        });
    }

    [TestMethod]
    public void ShowFileScanSkippedIncompleteWarning_UsesDialogService()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string failureReason = "directory_enumeration_failed:C:\\BMS";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);

            library.ShowFileScanSkippedIncompleteWarning(failureReason);

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, dialogService.Calls[0].Caption);
            Assert.AreEqual(MessageBoxButton.OK, dialogService.Calls[0].Button);
            Assert.AreEqual(MessageBoxImage.Exclamation, dialogService.Calls[0].Icon);
            Assert.AreEqual(MessageBoxResult.OK, dialogService.Calls[0].DefaultResult);
            StringAssert.Contains(dialogService.Calls[0].Message, failureReason);
            Assert.IsFalse(dialogService.Calls[0].Message.Contains("Everything"));
        });
    }

    [TestMethod]
    public void ShowEmptyScanWithExistingDbWarning_UsesDialogService()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string failureReason = "empty_scan_with_existing_db";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);

            library.ShowEmptyScanWithExistingDbWarning(failureReason);

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, dialogService.Calls[0].Caption);
            Assert.AreEqual(MessageBoxButton.OK, dialogService.Calls[0].Button);
            Assert.AreEqual(MessageBoxImage.Exclamation, dialogService.Calls[0].Icon);
            Assert.AreEqual(MessageBoxResult.OK, dialogService.Calls[0].DefaultResult);
            StringAssert.Contains(dialogService.Calls[0].Message, "0");
            StringAssert.Contains(dialogService.Calls[0].Message, "song.db");
            StringAssert.Contains(dialogService.Calls[0].Message, "Everything");
            StringAssert.Contains(dialogService.Calls[0].Message, failureReason);
        });
    }

    [TestMethod]
    public void QueueEverythingFallbackWarning_CoalescesDuplicateRequests()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string fallbackReason = "bridge_scan_failed:4";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);

            Assert.IsTrue(library.QueueEverythingFallbackWarning(fallbackReason));
            Assert.IsFalse(library.QueueEverythingFallbackWarning("second_reason"));
            Assert.IsTrue(SpinWait.SpinUntil(() => dialogService.CallCount == 1, TimeSpan.FromSeconds(5)));

            DialogCall call = dialogService.GetCall(0);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, call.Caption);
            StringAssert.Contains(call.Message, fallbackReason);
            Assert.IsFalse(call.Message.Contains("second_reason"));
        });
    }

    [TestMethod]
    public void QueueFileScanSkippedIncompleteWarning_CoalescesDuplicateRequests()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string failureReason = "root_not_found:C:\\BMS";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);

            Assert.IsTrue(library.QueueFileScanSkippedIncompleteWarning(failureReason));
            Assert.IsFalse(library.QueueFileScanSkippedIncompleteWarning("second_reason"));
            Assert.IsTrue(SpinWait.SpinUntil(() => dialogService.CallCount == 1, TimeSpan.FromSeconds(5)));

            DialogCall call = dialogService.GetCall(0);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, call.Caption);
            StringAssert.Contains(call.Message, failureReason);
            Assert.IsFalse(call.Message.Contains("second_reason"));
            Assert.IsFalse(call.Message.Contains("Everything"));
        });
    }

    [TestMethod]
    public void QueueEmptyScanWithExistingDbWarning_CoalescesDuplicateRequests()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string failureReason = "empty_scan_with_existing_db";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(songDbPath, null!, null, null!, dialogService);

            Assert.IsTrue(library.QueueEmptyScanWithExistingDbWarning(failureReason));
            Assert.IsFalse(library.QueueEmptyScanWithExistingDbWarning("second_reason"));
            Assert.IsTrue(SpinWait.SpinUntil(() => dialogService.CallCount == 1, TimeSpan.FromSeconds(5)));

            DialogCall call = dialogService.GetCall(0);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, call.Caption);
            StringAssert.Contains(call.Message, failureReason);
            Assert.IsFalse(call.Message.Contains("second_reason"));
            StringAssert.Contains(call.Message, "Everything");
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
        private readonly object lockObject = new();

        public List<DialogCall> Calls { get; } = [];

        public MessageBoxResult ResultToReturn { get; set; } = MessageBoxResult.OK;

        public int CallCount
        {
            get
            {
                lock (lockObject)
                {
                    return Calls.Count;
                }
            }
        }

        public DialogCall GetCall(int index)
        {
            lock (lockObject)
            {
                return Calls[index];
            }
        }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            lock (lockObject)
            {
                Calls.Add(new DialogCall
                {
                    Message = messageBoxText,
                    Caption = caption,
                    Button = button,
                    Icon = icon,
                    DefaultResult = defaultResult
                });
            }
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
