using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class MainWindowViewHostTests
{
    /// <summary>通常起動と初回設定開始で非同期schema確認の取消・×を実Closeへ渡し、L/P・gate解放後の一回終了と旧DB保持を確認します。Escの結果生成は実dialogのOKCancelケースへ分担します。</summary>
    [DataTestMethod]
    [DataRow(false, MessageBoxResult.Cancel)]
    [DataRow(false, MessageBoxResult.None)]
    [DataRow(true, MessageBoxResult.Cancel)]
    [DataRow(true, MessageBoxResult.None)]
    public void MainWindow_AppSchemaCancelUsesExistingCloseAfterAdmissionRelease(bool fromSettings, MessageBoxResult result)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var lifetime = new RecordingApplicationLifetime(firstStartup: fromSettings);
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture(lr2Mode: true, lifetime: lifetime);
            fixture.OptionalRelease.TrySetResult();
            fixture.Owner.StartupUpdateWorkflow.NotifyClosing();
            string database = fixture.Library.InitializationSongDbPath;
            if (fromSettings)
            {
                // 実画面の初回設定は、通常Shellの不正環境案内が終端した後に開始します。
                // Show時に別の通常起動を発生させず、確認中の初期化を二重化しません。
                fixture.Settings.LR2SongDBPath = database + ".not-configured";
                Task initial = fixture.Owner.ShellActivationWorkflow.ActivateRenderedShell(() => { }, action => action(), () => false);
                fixture.Track(initial);
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(initial, "schema-initial-invalid-environment");
                    Assert.IsFalse(((Task<bool>)initial).GetAwaiter().GetResult());
                }
                finally { fixture.Settings.LR2SongDBPath = database; }
            }

            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(database,
                db => db.Execute("UPDATE app_schema_version SET version = 0 WHERE name = 'app_schema'"));
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var choice = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Dialogs.ConfirmationObservedAsync = _ => { reached.TrySetResult(); return choice.Task; };
            SemaphoreSlim gate = typeof(MainWindowViewModel).GetField("_semaphore", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as SemaphoreSlim
                ?? throw new AssertFailedException("起動gateが見つかりません。");
            bool releasedAtExit = false;
            Action observeExit = () => releasedAtExit = !fixture.Composition.OperationAdmission.IsActive
                && !fixture.Composition.PlaylistOperationAdmission.IsActive && gate.CurrentCount == 1;
            fixture.Owner.ShellShutdownWorkflow.InitializationExitRequested += observeExit;
            MainWindowTestLifetime? ownership = null;
            Task? initialization = null;
            Exception? primary = null;
            try
            {
                ownership = new MainWindowTestLifetime(fixture.Owner, lifetime.ShutdownRequested.Task);
                MainWindow window = ownership.CreateWindow(() => new MainWindow(fixture.Owner));
                initialization = fromSettings ? fixture.Owner.SettingDialog.StartConfirmedEnvironmentAsync()
                    : fixture.Owner.ShellActivationWorkflow.ActivateRenderedShell(() => { }, action => action(), () => false);
                fixture.Track(initialization);
                ShowMainWindow(scope, window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(reached.Task, initialization, "schema-exit-choice"), "schema-exit-choice");
                Assert.IsFalse(initialization.IsCompleted);
                choice.TrySetResult(UiDialogResult.FromMessageBoxResult(result));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "schema-exit-initialization");
                Assert.IsTrue(releasedAtExit);
                Assert.IsTrue(fixture.Owner.ShellShutdownWorkflow.IsClosingOrClosed);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(lifetime.ShutdownRequested.Task, window.CloseCompletion, "schema-exit-shutdown"), "schema-exit-shutdown");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "schema-exit-close");
                Assert.AreEqual(1, lifetime.RequestShutdownCount);
                Assert.AreEqual(1, fixture.Dialogs.ConfirmationCount);
                Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
                using LR2SongDBExtended db = new BmsLibraryDbGateway(database).OpenSongDbReadOnly();
                Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name = 'app_schema'"));
            }
            catch (Exception failure) { primary = failure; throw; }
            finally
            {
                choice.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
                try { if (initialization != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "schema-exit-cleanup"); } }
                catch (Exception failure) when (primary != null) { primary.Data["SchemaExitCleanup"] = failure.ToString(); }
                finally
                {
                    fixture.Owner.ShellShutdownWorkflow.InitializationExitRequested -= observeExit;
                    ownership?.DisposeAfterBodyFailure(primary);
                }
            }
        });
    }
}
