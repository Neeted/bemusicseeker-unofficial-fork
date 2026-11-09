using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>構成の共通のダイアログ窓口と専用引数が、構成の作る所有者へ届く経路を検証します。</summary>
/// <remarks>`MainWindow_UpdateFailureNotification_UsesCommonDialogService` と `MainWindow_DropWarning_*` は、MainWindow を ctor だけで構築します。他はWindowを作らず構成から所有者までの接続だけを確認します。通知・確認の変換は BmsLibraryDialogServiceTests、確認結果の分岐は MaintenanceRescanWorkflowOwnerTests が担当します。</remarks>
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

    /// <summary>
    /// 条件：共通の窓口とプレイリストの窓口を別のインスタンスにした構成で、通信せずに失敗する更新の URL（相対URL）を指定し、
    /// 起動時の更新確認を開始する MainWindow を ctor だけで構築して、更新確認の終端まで待ちます。
    /// 期待結果：更新確認は Failed で終端し、更新失敗の通知が共通の窓口へちょうど1件届きます。
    /// 見出しは <c>UpdateDialog_FailedTitle</c>、本文は表示時の文化圏で整形した <c>UpdateDialog_FailedMessage_Format</c> と例外メッセージで、
    /// ボタンは OK、アイコンは Error、Owner は null です。確認は届かず、プレイリストの窓口には何も届きません。
    /// 検出する誤り：更新失敗の通知が固定の窓口へ送られる、プレイリストの窓口へ送られる、または共通の窓口を無視します。
    /// Owner が null であることは、更新失敗の通知の Owner を窓口の解決に任せる決定に基づきます。
    /// 待ち合わせの根拠：失敗の表示の要求は、終端の通知より前に積んだ UI の操作の中で、最初の await より前に窓口へ渡します（表示の完了は待ちません）。
    /// そのため、終端の待ちと ProcessQueuedBackground の後には要求が届いています。この順序は、表示を非同期にした後も維持する実装の制約です。
    /// 更新の条件：Owner の方針、失敗の通知の宛先、表示の要求を窓口へ渡す時機を変える時に、この期待を見直します。
    /// </summary>
    [TestMethod]
    public void MainWindow_UpdateFailureNotification_UsesCommonDialogService()
    {
        var common = new RecordingRootDialogService();
        IUiDialogService playlist = UndeclaredDialogs.Create();
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            MainWindowViewModel viewModel = new ApplicationComposition(
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(
                    ApplicationPathSnapshot.FromExecutablePath(Path.Combine(workspace.RootDirectory, "BeMusicSeeker.exe")),
                    updateManifestUrl: "relative/update.json"),
                settingsEditSession: new NoOpSettingsEditSession(TestSettingsFactory.CreateIsolatedSettings()),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                dialogService: common,
                playlistWorkspaceDialogService: playlist)
                .CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            ownership.Run(() =>
            {
                MainWindowPresentationTestHarness.PrepareConstructorOnlyShell(viewModel);
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                // NotifyClosing は呼ばず、MainWindow の構築が開始する更新確認を失敗まで進めます。
                ownership.CreateWindow(() => new MainWindow(viewModel));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartupUpdateWorkflow.WaitForTerminalAsync(),
                    "ApplicationCompositionDialogRoutingTests.startup-update-terminal");
                TestUiDispatcherHost.ProcessQueuedBackground();

                Assert.AreEqual(StartupUpdateWorkflowState.Terminal, viewModel.StartupUpdateWorkflow.CaptureState(out StartupUpdateWorkflowCompletionReceipt? receipt));
                Assert.IsNotNull(receipt);
                Assert.AreEqual(StartupUpdateWorkflowOutcome.Failed, receipt.Outcome);
                Assert.IsNotNull(receipt.Exception);
                Assert.AreEqual(1, common.MessageRequests.Count);
                Assert.AreEqual(0, common.ConfirmationCount);
                UiMessageRequest request = common.MessageRequests[0];
                Assert.AreEqual(Resources.UpdateDialog_FailedTitle, request.Caption);
                Assert.AreEqual(
                    string.Format(CultureInfo.CurrentCulture, Resources.UpdateDialog_FailedMessage_Format, receipt.Exception.Message),
                    request.MessageBoxText);
                Assert.AreEqual(System.Windows.MessageBoxButton.OK, request.Button);
                Assert.AreEqual(System.Windows.MessageBoxImage.Error, request.Icon);
                Assert.IsNull(request.Owner);
            });
        });
    }

    /// <summary>
    /// 条件：ファイル形式を含まないドロップ（text だけ）を、構築だけの MainWindow へ配送します。構成の共通の窓口は通知の表示が成功、失敗（Failed）、窓口の例外のいずれかで、
    /// プレイリストの窓口は別の未宣言の窓口です。
    /// 期待結果：ハンドラーの終端は失敗せず、共通の窓口へ未対応形式の警告（見出し Warning、OK、Exclamation、Owner は MainWindow）がちょうど 1 件届き、確認は届きません。
    /// 通知の呼出し時点でも終了後でも Effects は None、Handled は true です。プレイリストの窓口には何も届きません。
    /// 表示の失敗は任意の通知としてログに残すだけで、例外を外へ出さず、Effects と Handled を変えません。
    /// 検出する誤り：警告の宛先や内容・Owner の取り違え、通知の後での Effects と Handled の確定、表示失敗の例外の流出。
    /// 反射の承認事項：理由は DragEventArgs に公開の ctor がないこと、範囲は ctor の呼出しだけで製品の状態には触れないこと、退役の条件は公開の生成手段ができることです。
    /// </summary>
    [TestMethod]
    [DataRow("shown")]
    [DataRow("failed")]
    [DataRow("faulted")]
    public void MainWindow_DropWarning_UsesCommonDialogServiceAndKeepsDropOutcome(string scenario)
    {
        var failure = new InvalidOperationException("drop warning presentation failure");
        DragEventArgs? args = null;
        DragDropEffects effectsAtNotification = DragDropEffects.Copy;
        bool? handledAtNotification = null;
        var common = new RecordingRootDialogService
        {
            MessageResult = scenario switch
            {
                "shown" => UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK),
                _ => UiDialogResult.Failed(failure),
            },
            MessageObserved = () =>
            {
                Assert.IsNotNull(args);
                effectsAtNotification = args.Effects;
                handledAtNotification = args.Handled;
            },
        };
        if (scenario == "faulted")
        {
            common.MessageObservedAsync = () => Task.FromException(failure);
        }
        IUiDialogService playlist = UndeclaredDialogs.Create();

        MainWindowPresentationTestHarness.RunConstructorOnly(
            TestSettingsFactory.CreateIsolatedSettings(),
            (_, window) =>
            {
                ConstructorInfo constructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                    [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)
                    ?? throw new AssertFailedException("DragEventArgsの入力生成ctorがありません。");
                args = (DragEventArgs)constructor.Invoke(
                    [new DataObject(DataFormats.Text, "not a chart package"), DragDropKeyStates.None, DragDropEffects.Copy, window, new Point(1, 1)]);
                args.RoutedEvent = DragDrop.DropEvent;

                Task completion = MainWindowEventCompletion.CaptureAsyncEventCompletion(() => window.RaiseEvent(args));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(completion, "drop warning");

                Assert.AreEqual(1, common.MessageCount);
                Assert.AreEqual(0, common.ConfirmationCount);
                UiMessageRequest request = common.MessageRequests.Single();
                Assert.AreEqual(Resources.Warn_DropInstallUnsupportedFormat, request.MessageBoxText);
                Assert.AreEqual(Resources.Warning, request.Caption);
                Assert.AreEqual(MessageBoxButton.OK, request.Button);
                Assert.AreEqual(MessageBoxImage.Exclamation, request.Icon);
                Assert.AreSame(window, request.Owner);
                Assert.AreEqual(DragDropEffects.None, effectsAtNotification);
                Assert.AreEqual(true, handledAtNotification);
                Assert.AreEqual(DragDropEffects.None, args.Effects);
                Assert.IsTrue(args.Handled);
            },
            allowStartupUiInteraction: true,
            playlistWorkspaceDialogService: playlist,
            dialogService: common);
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
