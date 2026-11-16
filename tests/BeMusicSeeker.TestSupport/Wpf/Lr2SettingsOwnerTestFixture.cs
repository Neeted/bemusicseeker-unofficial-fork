using System.Runtime.ExceptionServices;
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
    /// 終了Taskの実終端・準備成功と設定VMのDispose成功後だけ、そのownerの媒体利用終了を確認します。
    /// この境界は既存の媒体利用TaskとDB drainであり、実WindowのterminalやBASS停止を追加実行しません。
    /// owner構築開始後の終了未確認では、付加資源とrootを回収せずworkspaceも保全します。
    /// 本体と終了・回収が共に失敗すると、本体を先頭に集約します。単独の失敗は元例外を伝えます。
    /// 保全は消費者の終端や取消の代用ではなく、所有資源の診断を残して呼出元へ失敗を伝えます。
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
        bool mediaReleased = true;
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
                mediaReleased = false;
                var composition = new ApplicationComposition(settingsEditSession: new SettingsEditSession(values), reportSettingsApplyFailure: failures.Add,
                    settingsDialogService: dialogs, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                    runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe"))),
                    chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(), defaultBmsPlayerFactory: () => new RecordingPlaybackPlayer(),
                    applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup: false), cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                    dialogService: UndeclaredDialogs.Create());
                var factory = new StartupLibraryConstructionTestSupport.RecordingDelegatingStartupLibraryFactory(composition, [])
                { PlaylistCreator = playlistFactory == null ? null : (profile, library) => playlistFactory(composition, profile, library) };
                var shell = new MainWindowViewModel(composition, factory);
                Exception? bodyFailure = null;
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
                catch (Exception failure) { bodyFailure = failure; throw; }
                finally
                {
                    try
                    {
                        CompleteOwnerShutdown(workspace, shell.ShellShutdownWorkflow.RequestWindowCloseAsync,
                            shell.SettingDialog.Dispose, (task, phase) => TestUiDispatcherHost.AwaitTaskOnDispatcher(task, phase));
                        mediaReleased = true;
                    }
                    catch (Exception shutdownFailure) when (bodyFailure != null)
                    { throw new AggregateException(bodyFailure, shutdownFailure); }
                }
            });
            }
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            if (!mediaReleased)
            {
                workspace.PreserveArtifacts();
                RecordPreservedArtifacts(failure, $"root={root}; resource={resource}; reason=ownerの媒体利用終了を確認できません。");
            }
            throw;
        }
        finally
        {
            if (mediaReleased)
            {
                try
                {
                    resource?.Dispose();
                    // root はこのfixtureが生成した実パスです。別名ルートを再帰削除しません。
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception cleanupFailure)
                {
                    workspace.PreserveArtifacts();
                    RecordPreservedArtifacts(cleanupFailure, $"root={root}; resource={resource}; reason=媒体回収に失敗しました。");
                    if (primaryFailure != null) { throw new AggregateException(primaryFailure, cleanupFailure); }
                    throw;
                }
            }
        }
    }

    /// <summary>手動回収で必要な所有診断を、回帰観測用の例外Dataとrunnerが収集する標準エラーへ残します。</summary>
    private static void RecordPreservedArtifacts(Exception failure, string diagnostic)
    {
        failure.Data["Lr2OwnerPreservedArtifacts"] = diagnostic;
        try { Console.Error.WriteLine(diagnostic); }
        catch (Exception diagnosticFailure)
        {
            // 出力先の故障で本体・終了・回収の元原因を置換しません。
            failure.Data["Lr2OwnerPreservedArtifactsDiagnosticFailure"] = diagnosticFailure;
        }
    }

    /// <summary>元の終了Taskの実終端と準備成功、設定VM解放の成功を同じ媒体寿命の境界として確認します。</summary>
    /// <remarks>待機が未終端のまま戻る場合も保全して失敗させます。保全後の自動回収・再試行は行いません。</remarks>
    /// <param name="workspace">終了を確認できない場合に、外側のDisposeでも媒体を削除させない所有領域。</param>
    /// <param name="requestShutdown">一度だけ呼ぶ実終了Taskの取得処理。</param>
    /// <param name="disposeSettings">準備成功後に呼ぶ設定購読の解放処理。</param>
    /// <param name="waitForTask">元Taskを待つ既存の待機処理。戻った後にも実終端を確認します。</param>
    internal static void CompleteOwnerShutdown(TestWorkspace workspace,
        Func<Task<ShellShutdownWorkflowCompletionReceipt>> requestShutdown, Action disposeSettings, Action<Task, string> waitForTask)
    {
        try
        {
            Task<ShellShutdownWorkflowCompletionReceipt> shutdown = requestShutdown();
            waitForTask(shutdown, "lr2-owner-cleanup");
            if (!shutdown.IsCompleted) { throw new InvalidOperationException("終了待機後も元の終了Taskが未終端です。"); }
            ShellShutdownWorkflowCompletionReceipt receipt = shutdown.GetAwaiter().GetResult();
            if (!receipt.PreparationSucceeded)
            {
                ExceptionDispatchInfo.Capture(receipt.Exception ?? new InvalidOperationException("ownerの終了準備に失敗しました。")).Throw();
            }
            disposeSettings();
        }
        catch
        {
            workspace.PreserveArtifacts();
            throw;
        }
    }
}
