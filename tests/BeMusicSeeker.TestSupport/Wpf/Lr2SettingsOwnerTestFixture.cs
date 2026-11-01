using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.SettingsMediaTestFixture;
using static BeMusicSeeker.TestSupport.Wpf.SettingsStartupTestFixture;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>実LR2起動・後続操作・終了と媒体回収の同じ所有境界を共用します。</summary>
internal static class Lr2SettingsOwnerTestFixture
{

    /// <summary>旧環境依存ケースと移管済みLR2設定ケースで、同じ実起動・再起動・終了と専有媒体の回収を共用します。</summary>
    /// <param name="workspace">呼出元がケース全体の寿命とDisposeを所有する領域。helperは配下へ作ったrootだけを回収します。</param>
    /// <param name="prepare">初回保存前にrootと設定を準備する任意callback。その後、起動時走査を有効、保留導入の初期選択を無効にして保存します。</param>
    /// <param name="run">初回の実初期化Taskの成功と起動後続の終端を確認した後、Dispatcher上で呼ぶ同期の検証callback。</param>
    /// <param name="beforeInitialize">初回の実初期化を始める前に、Dispatcher上で設定とshellへ準備する任意callback。</param>
    /// <param name="ownedResource">rootに結び付く付加資源を作る任意factory。取得した資源のDisposeは各shellの終了処理より外側で行います。</param>
    /// <param name="scannerFactory">起動ごとのrootと設定からscannerを作る任意factory。省略時は空の走査fixtureを持つSettingsChartFileScannerを使います。</param>
    /// <param name="afterRestart">指定時は同じ保存設定を読み直して二回目を起動し、必須初期化の成功後に呼びます。そのcallback後に二回目の起動後続の終端を待ちます。</param>
    /// <param name="playlistFactory">各composition・profile・libraryに対するplaylist構築を差し替える任意factory。</param>
    /// <param name="beforeRestart">二回目の実初期化前に、読み直した設定とshellへDispatcher上で準備する任意callback。</param>
    /// <remarks>
    /// 各callbackは同期です。helperが直接待つのは実初期化Task、規定の時点での起動後続、各起動のfinallyで要求するshell終了Taskです。
    /// 実Task・DBの終了はそのshellの既存shutdownへ接続し、callback独自のTaskやDB接続・Windowの終端は呼出元が担います。
    /// 終了Taskが例外なく完了した後に設定VMをDisposeします。終了receiptの成否を追加表明する処理はありません。
    /// 終了待機が失敗すると設定VMのDisposeへ進まず、そのfinally例外がcallback側の失敗を置き換えることがあります。
    /// 外側のfinallyは起動・callback・終了待機の成否にかかわらず付加資源のDisposeを試み、その成功後にrootを再帰削除します。
    /// 先行する失敗がある状態でこの外側回収も失敗した場合は両方をAggregateExceptionで伝え、回収だけの失敗はそのまま伝播します。
    /// workspace全体の回収はhelperの復帰・例外伝播後も呼出元の所有に残ります。
    /// </remarks>
    internal static void WithInitializedLr2Owner(TestWorkspace workspace, Action<string, Settings>? prepare,
        Action<string, Settings, ApplicationComposition, MainWindowViewModel, StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory, List<Exception>, RecordingRootDialogService> run, Action<string, Settings, MainWindowViewModel>? beforeInitialize = null,
        Func<string, IDisposable>? ownedResource = null,
        Func<string, Settings, IChartFileScanner>? scannerFactory = null,
        Action<string, Settings, ApplicationComposition, MainWindowViewModel, StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory, List<Exception>, RecordingRootDialogService>? afterRestart = null,
        Func<ApplicationComposition, LibraryProfile, BMSLibrary, BMSPlaylist>? playlistFactory = null,
        Action<string, Settings, MainWindowViewModel>? beforeRestart = null)
    {
        string root = CreateTemporaryRoot(workspace);
        IDisposable? resource = null;
        Exception? primaryFailure = null;
        try
        {
            resource = ownedResource?.Invoke(root);
            Settings values = CreateValidCustomFolderSettings(root, [], TestSettingsFactory.OpenSettings(Path.Combine(root, "settings", "user.config")));
            prepare?.Invoke(root, values);
            values.ScanBmsFilesOnStartup = true;
            values.StartupSelectInstallPending = false;
            values.Save();
            int launches = afterRestart == null ? 1 : 2;
            for (int launch = 0; launch < launches; launch++)
            {
                if (launch > 0) { values = TestSettingsFactory.OpenSettings(Path.Combine(root, "settings", "user.config")); }
                TestUiDispatcherHost.Invoke(() =>
            {
                var failures = new List<Exception>();
                var dialogs = new RecordingRootDialogService { ConfirmationResult = UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK) };
                IChartFileScanner scanner = scannerFactory?.Invoke(root, values) ?? new SettingsChartFileScanner(CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), [values.BMSInstallDir]));
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values), reportSettingsApplyFailure: failures.Add,
                    settingsDialogService: dialogs, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe"))),
                    chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(), defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer(),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                { PlaylistCreator = playlistFactory == null ? null : (profile, library) => playlistFactory(composition, profile, library) };
                var shell = new MainWindowViewModel(composition, factory);
                try
                {
                    if (launch == 0) { beforeInitialize?.Invoke(root, values, shell); }
                    else { beforeRestart?.Invoke(root, values, shell); }
                    Task<bool> initialized = shell.InitializeAsync();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initialized, "lr2-owner-startup");
                    Assert.IsTrue(initialized.GetAwaiter().GetResult());
                    if (launch == 0) { AwaitStartupBackgroundCompletionOnDispatcher(shell, "lr2-owner-background"); }
                    (launch == 0 ? run : afterRestart ?? throw new AssertFailedException())(root, values, composition, shell, factory, failures, dialogs);
                    if (launch > 0) { AwaitStartupBackgroundCompletionOnDispatcher(shell, "lr2-owner-background"); }
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(shell.ShellShutdownWorkflow.RequestWindowCloseAsync(), "lr2-owner-cleanup");
                    shell.SettingDialog.Dispose();
                }
            });
            }
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            try
            {
                resource?.Dispose();
                // root はこのfixtureが生成した実パスです。別名ルートを再帰削除しません。
                Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupFailure) when (primaryFailure != null)
            { throw new AggregateException(primaryFailure, cleanupFailure); }
        }
    }
}
