using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class MainWindowViewHostTests
{
    /// <summary>実復元失敗のExitを実Window.Closeへ渡し、親L/P・gateの解放、初期化終端、既存終了準備と最終終了作用へ到達します。登録は保持し、起動成功は公開しません。</summary>
    /// <remarks>fake寿命の後片付けCloseを到達の証明に使わず、その前に実CloseCompletionと終了要求一回を観測します。通常OnClosingのcancel/reclose回数は固定しません。終了取消join・重複要求の条件表は既存Shell系列へ分担します。</remarks>
    [TestMethod]
    public void MainWindow_PendingRestoreExitUsesExistingCloseAndTerminalLifetime()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var input = new PendingRestoreInput();
            var lifetime = new RecordingApplicationLifetime();
            using var fixture = new MainWindowViewModelStartupProgressTests.CompletionFixture(lifetime: lifetime);
            fixture.OptionalRelease.TrySetResult();
            input.Register(fixture.Library.InitializationSongDbPath);
            using FileStream held = input.LockB();
            fixture.Owner.StartupUpdateWorkflow.NotifyClosing();
            var choiceReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var choice = new TaskCompletionSource<PendingPackageRestoreChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Dialogs.PendingRestoreObservedAsync = async () =>
            {
                choiceReached.TrySetResult();
                return new(UiDialogStatus.Accepted, await choice.Task);
            };
            bool releasedAtExit = false;
            SemaphoreSlim gate = typeof(MainWindowViewModel).GetField("_semaphore", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) as SemaphoreSlim
                ?? throw new AssertFailedException("起動gateが見つかりません。");
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
                initialization = fixture.Owner.ShellActivationWorkflow.ActivateRenderedShell(() => { }, action => action(), () => false);
                fixture.Track(initialization);
                ShowMainWindow(scope, window);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(choiceReached.Task, initialization, "pending-exit-real-choice"), "pending-exit-real-choice");
                choice.TrySetResult(PendingPackageRestoreChoice.Exit);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "pending-exit-real-initialization");
                Assert.IsFalse(((Task<bool>)initialization).GetAwaiter().GetResult());
                Assert.IsTrue(releasedAtExit);
                Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
                Assert.IsTrue(fixture.Owner.ShellShutdownWorkflow.IsClosingOrClosed, "手動cleanup前に実Windowの通常Close経路へ到達します。");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(lifetime.ShutdownRequested.Task,
                    window.CloseCompletion, "pending-exit-real-terminal"), "pending-exit-real-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "pending-exit-real-close-completion");
                Assert.AreEqual(1, lifetime.RequestShutdownCount);
                Assert.AreEqual(1, fixture.Dialogs.PendingRestoreCount);
                Assert.AreEqual(3, new BmsLibraryDbGateway(fixture.Library.InitializationSongDbPath).LoadInstallPackages().Count);
                Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
            }
            catch (Exception failure) { primary = failure; throw; }
            finally
            {
                choice.TrySetResult(PendingPackageRestoreChoice.None);
                try
                {
                    if (initialization != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(initialization, "pending-exit-real-initialization-cleanup"); }
                }
                catch (Exception failure) when (primary != null) { primary.Data["PendingExitInitializationCleanup"] = failure.ToString(); }
                finally
                {
                    fixture.Owner.ShellShutdownWorkflow.InitializationExitRequested -= observeExit;
                    ownership?.DisposeAfterBodyFailure(primary);
                }
            }
        });
    }
}
