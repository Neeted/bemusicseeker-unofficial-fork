using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>構成の共通のダイアログ窓口と専用引数が、構成の作る所有者へ届く経路を検証します。</summary>
/// <remarks>Windowを作らず構成から所有者までの接続だけを確認します。通知・確認の変換は BmsLibraryDialogServiceTests、確認結果の分岐は MaintenanceRescanWorkflowOwnerTests が担当します。</remarks>
[TestClass]
[TestCategory("Windows.Shell")]
public sealed class ApplicationCompositionDialogRoutingTests
{
    /// <summary>
    /// 条件：共通の窓口だけを渡し、ライブラリ用の専用引数を省略して <c>CreateBmsLibrary</c> で作ったライブラリの警告を起こします。
    /// 期待結果：警告は共通の窓口へちょうど1件、題名と本文つきで届き、確認は届きません。
    /// 検出する誤り：ライブラリが自前の窓口を作り続ける、または共通の窓口を無視します。
    /// </summary>
    [TestMethod]
    public void CreateBmsLibrary_WithoutLibraryDialogService_DeliversNotificationToCommonDialogService()
    {
        var common = new RecordingRootDialogService();
        string reason = "common-" + Guid.NewGuid().ToString("N");

        ShowLibraryWarning(common, libraryDialogService: null, reason);

        AssertSingleWarning(common, reason);
        Assert.AreEqual(0, common.ConfirmationCount);
    }

    /// <summary>
    /// 条件：共通の窓口と、ライブラリ用の専用の窓口を渡し、同じ警告を起こします。
    /// 期待結果：警告は専用の窓口へ1件届き、共通の窓口へは通知も確認も届きません。
    /// 検出する誤り：専用引数を無視して共通の窓口を使う、または両方の窓口へ送ります。
    /// </summary>
    [TestMethod]
    public void CreateBmsLibrary_WithLibraryDialogService_PrefersDedicatedOverCommon()
    {
        var common = new RecordingRootDialogService();
        var dedicated = new RecordingRootDialogService();
        string reason = "dedicated-" + Guid.NewGuid().ToString("N");

        ShowLibraryWarning(common, dedicated, reason);

        AssertSingleWarning(dedicated, reason);
        Assert.AreEqual(0, dedicated.ConfirmationCount);
        Assert.AreEqual(0, common.MessageCount);
        Assert.AreEqual(0, common.ConfirmationCount);
    }

    /// <summary>
    /// 条件：共通の窓口へ確認を拒否する応答を設定し、他の専用引数すべてへ別の窓口を渡した構成で、再検査の開始を要求します。
    /// 期待結果：再検査の確認は共通の窓口へ1件、本文と題名つきで届き、専用の窓口へは何も届きません。操作は拒否され、再検査は始まりません。
    /// 検出する誤り：子構成が自前の窓口を作り続ける、共通の窓口を無視する、またはこの経路へ専用引数の窓口を流用します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task MainWindowViewModel_MaintenanceRescanConfirmation_UsesCommonDialogService()
    {
        var common = new RecordingRootDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.Cancel),
        };
        var other = new RecordingRootDialogService();
        Settings settings = TestSettingsFactory.CreateIsolatedSettings();
        var composition = new ApplicationComposition(
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            playlistWorkspaceDialogService: other,
            fileDbMutationDialogService: other,
            restartFailureDialogs: other,
            settingsDialogService: other,
            libraryDialogService: other,
            dialogService: common);
        MainWindowViewModel vm = composition.CreateMainWindowViewModelForTest();
        using SettingsDialogViewModel settingsLifetime = vm.SettingDialog;

        MaintenanceRescanStartResult result = await vm.MaintenanceRescanWorkflow.RequestStartAsync();

        Assert.AreEqual(1, common.ConfirmationRequests.Count);
        Assert.AreEqual(Resources.Msg_rescan_all_charts_confirm, common.ConfirmationRequests[0].MessageBoxText);
        Assert.AreEqual(Resources.Confirm, common.ConfirmationRequests[0].Caption);
        Assert.AreEqual(0, common.MessageCount);
        Assert.AreEqual(0, other.ConfirmationCount);
        Assert.AreEqual(0, other.MessageCount);
        Assert.AreEqual(MaintenanceRescanStartResult.Rejected, result);
        Assert.IsFalse(vm.MaintenanceRescanWorkflow.IsActive);
    }

    private static void ShowLibraryWarning(RecordingRootDialogService common, RecordingRootDialogService? libraryDialogService, string reason)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(workspace.RootDirectory, "dialog-routing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string songDbPath = Path.Combine(directory, "song.db");
        var composition = new ApplicationComposition(
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
            bmsLibraryOptionsProvider: () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false },
            settingsEditSession: new NoOpSettingsEditSession(TestSettingsFactory.CreateIsolatedSettings()),
            uiScheduler: TestUiScheduler.CreateDefault(),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            libraryDialogService: libraryDialogService,
            dialogService: common);
        using (LR2SongDBExtended initialize = composition.DatabaseScope.OpenSongWrite(songDbPath)) { }
        BMSLibrary library = composition.CreateBmsLibrary(StartupLibraryConstructionTestSupport.CreateProfile(directory, songDbPath));
        try
        {
            library.ShowEverythingFallbackWarning(reason);
        }
        finally
        {
            library.RequestShutdown("dialog-routing-test");
        }
    }

    private static void AssertSingleWarning(RecordingRootDialogService recipient, string reason)
    {
        Assert.AreEqual(1, recipient.MessageRequests.Count);
        Assert.AreEqual(string.Format(Resources.Warn_EverythingFallbackScanUsed, reason), recipient.MessageRequests[0].MessageBoxText);
        Assert.AreEqual(Resources.MessageBoxTitle_Warning, recipient.MessageRequests[0].Caption);
    }
}
