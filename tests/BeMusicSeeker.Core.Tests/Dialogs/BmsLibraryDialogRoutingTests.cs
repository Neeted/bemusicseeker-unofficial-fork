using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryDialogRoutingTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>上書き導入の確認を注入dialogへ一回渡し、拒否されたpackageを保留一覧から除きません。</summary>
    [TestMethod]
    public void ForceInstallPendingPackages_UsesDialogServiceForOverrideConfirmation()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(CaseWorkspace, delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.No
            };
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService, new TestUiScheduler(() => null!));
            ChartFile pendingFile = ChartTestValues.Empty() with
            {
                Path = "C:\\Pending\\Pkg\\chart.bms"
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

    /// <summary>消失フォルダのrenameで対象path付き警告を注入dialogへ一回渡し、無通知の成功へ置換しません。</summary>
    [TestMethod]
    public void RenameChartFolder_UsesDialogServiceForMissingFolderWarning()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(CaseWorkspace, delegate (string songDbPath)
        {
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService, new TestUiScheduler(() => null!));
            string missingDirectoryPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_Missing_" + Guid.NewGuid().ToString("N"));

            library.RenameChartFolder(missingDirectoryPath, "RenamedFolder");

            Assert.AreEqual(1, dialogService.Calls.Count);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, dialogService.Calls[0].Caption);
            StringAssert.Contains(dialogService.Calls[0].Message, missingDirectoryPath);
        });
    }

    /// <summary>Everythingの代替走査理由を警告dialogへ渡し、理由・警告種別・既定応答を失いません。</summary>
    [TestMethod]
    public void ShowEverythingFallbackWarning_UsesDialogService()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(CaseWorkspace, delegate (string songDbPath)
        {
            const string fallbackReason = "empty_results_with_roots";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService, new TestUiScheduler(() => null!));

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

    /// <summary>不完全走査を中止した理由を警告へ渡し、Everythingの失敗と取り違えた通知を出しません。</summary>
    [TestMethod]
    public void ShowFileScanSkippedIncompleteWarning_UsesDialogService()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(CaseWorkspace, delegate (string songDbPath)
        {
            const string failureReason = "directory_enumeration_failed:C:\\BMS";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService, new TestUiScheduler(() => null!));

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

    /// <summary>既存DBがある空走査では件数・DB・Everythingと原因を警告へ渡し、空の正常走査と取り違えません。</summary>
    [TestMethod]
    public void ShowEmptyScanWithExistingDbWarning_UsesDialogService()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(CaseWorkspace, delegate (string songDbPath)
        {
            const string failureReason = "empty_scan_with_existing_db";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService, new TestUiScheduler(() => null!));

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

    /// <summary>Everything警告の重複要求を一通知へ集約し、最初の理由を後続理由で上書きしません。</summary>
    [TestMethod]
    public void QueueEverythingFallbackWarning_CoalescesDuplicateRequests()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(CaseWorkspace, delegate (string songDbPath)
        {
            const string fallbackReason = "bridge_scan_failed:4";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService, new TestUiScheduler(() => null!));

            Assert.IsTrue(library.QueueEverythingFallbackWarning(fallbackReason));
            Assert.IsFalse(library.QueueEverythingFallbackWarning("second_reason"));
            Assert.AreEqual(1, dialogService.CallCount);

            DialogCall call = dialogService.GetCall(0);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, call.Caption);
            StringAssert.Contains(call.Message, fallbackReason);
            Assert.IsFalse(call.Message.Contains("second_reason"));
        });
    }

    /// <summary>不完全走査の重複警告を一通知へ集約し、最初の原因と走査中止の分類を保持します。</summary>
    [TestMethod]
    public void QueueFileScanSkippedIncompleteWarning_CoalescesDuplicateRequests()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(CaseWorkspace, delegate (string songDbPath)
        {
            const string failureReason = "root_not_found:C:\\BMS";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService, new TestUiScheduler(() => null!));

            Assert.IsTrue(library.QueueFileScanSkippedIncompleteWarning(failureReason));
            Assert.IsFalse(library.QueueFileScanSkippedIncompleteWarning("second_reason"));
            Assert.AreEqual(1, dialogService.CallCount);

            DialogCall call = dialogService.GetCall(0);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, call.Caption);
            StringAssert.Contains(call.Message, failureReason);
            Assert.IsFalse(call.Message.Contains("second_reason"));
            Assert.IsFalse(call.Message.Contains("Everything"));
        });
    }

    /// <summary>既存DBの空走査警告は一通知へ集約し、後続原因で最初の原因やEverythingの分類を置換しません。</summary>
    [TestMethod]
    public void QueueEmptyScanWithExistingDbWarning_CoalescesDuplicateRequests()
    {
        var databaseScope = new ApplicationDatabaseScope();

        WithTemporarySongDb(CaseWorkspace, delegate (string songDbPath)
        {
            const string failureReason = "empty_scan_with_existing_db";
            var dialogService = new RecordingDialogService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null!, null, null!, dialogService, new TestUiScheduler(() => null!));

            Assert.IsTrue(library.QueueEmptyScanWithExistingDbWarning(failureReason));
            Assert.IsFalse(library.QueueEmptyScanWithExistingDbWarning("second_reason"));
            Assert.AreEqual(1, dialogService.CallCount);

            DialogCall call = dialogService.GetCall(0);
            Assert.AreEqual(Properties.Resources.MessageBoxTitle_Warning, call.Caption);
            StringAssert.Contains(call.Message, failureReason);
            Assert.IsFalse(call.Message.Contains("second_reason"));
            StringAssert.Contains(call.Message, "Everything");
        });
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    private static void WithTemporarySongDb(TestWorkspace workspace, Action<string> testAction)
    {
        string tempRootPath = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_DialogTests_" + Guid.NewGuid().ToString("N"));
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

}
