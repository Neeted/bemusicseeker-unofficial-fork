using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;


public sealed partial class MainWindowViewHostTests
{
    /// <summary>
    /// 実MainWindow・実設定モーダル・実開始ボタンで、初回の「この環境で開始」が必須UIの公開を待たずに設定画面を閉じ、失敗時は通知を閉じた後に同じ設定で開き直すことを確認します。
    /// 開始ボタンの連打は一回だけ受理し、閉鎖後の初期化中は設定画面を開き直せません。成功では再表示せず、初回完了と完了通知が各一回成立します。
    /// UI公開の失敗では、通知は通常起動と同じ文面で、設定操作の失敗通知を含む全経路の合計で一回だけ出ます。通知の保留中は再表示せず、通知を閉じた後の再表示の時点で元の操作が終端済み・同じViewModel・開始ボタンと案内文が表示かつ有効で、押すと再試行が成功して再び閉じます。
    /// </summary>
    /// <remarks>失敗通知の文面・受付の解放・閉鎖と再表示の順序の網羅はSettingDialogEditCompletionTestsが分担し、ここは実Windowの寿命と入力経路を代表します。</remarks>
    [TestMethod]
    [DataRow(false)] // 成功：閉鎖は一回だけで、再表示しない。
    [DataRow(true)] // 必須UIの公開が失敗：通知 → 再表示 → 利用者の再開始で成功する。
    public void MainWindow_InitialEnvironmentStartClosesBeforeInitializationAndReopensAfterFailureNotice(bool uiFails)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var lifetime = new RecordingApplicationLifetime(firstStartup: true);
            // 設定操作の失敗通知（reportApplyFailure）を記録し、画面の通知窓口と合わせて全経路の通知を数えます。
            var settingsFailures = new List<Exception>();
            using var fixture = new StartupCompletionTestFixture.CompletionFixture(lr2Mode: true, lifetime: lifetime, settingsFailureReporter: settingsFailures.Add,
                rootFileEnumerator: new FastRootFileEnumerator());
            fixture.OptionalRelease.TrySetResult();
            fixture.Owner.StartupUpdateWorkflow.NotifyClosing();
            string database = fixture.Library.InitializationSongDbPath;
            var uiFailure = new IOException("controlled initial settings UI failure");
            var messages = new List<string>();
            var notificationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool holdFailureNotification = false;
            (bool LibraryFree, bool PlaylistFree, bool UiBlocked)? notificationObservation = null;
            fixture.Dialogs.MessageObservedAsync = async request =>
            {
                messages.Add(request.MessageBoxText);
                if (!holdFailureNotification || !request.MessageBoxText.Contains(Resources.Msg_error_unexpected, StringComparison.Ordinal)) { return; }
                bool libraryFree = fixture.Composition.OperationAdmission.TryEnter(out IDisposable? libraryLease);
                bool playlistFree;
                using (libraryLease)
                {
                    playlistFree = fixture.Composition.PlaylistOperationAdmission.TryEnter(out IDisposable? playlistLease);
                    playlistLease?.Dispose();
                }
                notificationObservation = (libraryFree, playlistFree, fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                notificationEntered.TrySetResult();
                await notificationRelease.Task;
            };
            fixture.Dialogs.SettingsModalScopeFactory = window =>
            {
                if (!TestWindowPresentationScope.IsPrepared(window)) { scope.PrepareForOwnedPresentation(window); }
                return UiDialogOwnerResolver.PushActiveModal(window);
            };
            // 不正な設定のまま通常起動して案内状態にし、その後に設定を正して設定画面から開始します。
            fixture.Settings.LR2SongDBPath = database + ".not-configured";
            Task initial = fixture.Owner.ShellActivationWorkflow.ActivateRenderedShell(() => { }, action => action(), () => false);
            fixture.Track(initial);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(initial, "initial-start-wpf-guidance");
                Assert.IsFalse(((Task<bool>)initial).GetAwaiter().GetResult());
            }
            finally { fixture.Settings.LR2SongDBPath = database; }
            fixture.Owner.SettingDialog.RefreshDeficiencies();
            int messagesBeforeStart = messages.Count;

            var settingsWindows = new List<SettingsWindow>();
            var interactions = new List<Task>();
            var scenarioDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? firstOperation = null;
            Window? previousMain = Application.Current.MainWindow;
            var ownership = new MainWindowTestLifetime(fixture.Owner, lifetime.ShutdownRequested.Task);
            Exception? bodyFailure = null;
            try
            {
                MainWindow main = ownership.CreateWindow(() => new MainWindow(fixture.Owner, created =>
                {
                    settingsWindows.Add(created);
                    int index = settingsWindows.Count;
                    scope.PrepareForOwnedPresentation(created);
                    created.ContentRendered += (_, _) => created.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                        (Action)(() => interactions.Add(ExerciseAsync(created, index))));
                }));
                scope.PrepareForOwnedPresentation(main);
                Application.Current.MainWindow = main;
                main.Show(); main.UpdateLayout();
                fixture.Owner.SettingDialog.OpenCommand.Execute();
                // Executeの復帰は最初のモーダルが閉じた後です。再表示した画面の操作も含め、操作の終端を待ちます。
                TestUiDispatcherHost.AwaitTaskOnDispatcher(scenarioDone.Task, "initial-start-wpf-scenario");
                foreach (Task interaction in interactions.ToArray()) { TestUiDispatcherHost.AwaitTaskOnDispatcher(interaction, "initial-start-wpf-interaction"); }

                Assert.AreEqual(uiFails ? 2 : 1, settingsWindows.Count);
                Assert.IsTrue(firstOperation!.IsCompletedSuccessfully);
                Assert.IsTrue(fixture.Owner.HasActiveLibraryProfile);
                Assert.IsTrue(fixture.Owner.IsInitializationCompleted);
                Assert.AreEqual(1, fixture.CompletionNotifications, "完了通知は一回だけです。");
                Assert.IsFalse(lifetime.IsFirstStartup, "成功した開始で初回完了が成立します。");
                Assert.AreEqual(0, messages.Skip(messagesBeforeStart).Count(text => text.Contains(Resources.Warn_LibraryOperationBusy, StringComparison.Ordinal)),
                    "受理済みの初期化を自己Busyとして拒否しません。");
                int unexpectedNotices = messages.Count(text => text.Contains(Resources.Msg_error_unexpected, StringComparison.Ordinal));
                Assert.AreEqual(uiFails ? 1 : 0, unexpectedNotices, "失敗の通知は通常起動と同じ文面で一回だけです。");
                Assert.AreEqual(0, settingsFailures.Count, "閉鎖後の失敗は設定操作の失敗通知を通しません。" + string.Join(Environment.NewLine, settingsFailures));
                Assert.AreEqual(uiFails ? 1 : 0, unexpectedNotices + settingsFailures.Count, "失敗の通知は全経路の合計で一回だけです。");
                if (uiFails)
                {
                    string notice = messages.Single(text => text.Contains(Resources.Msg_error_unexpected, StringComparison.Ordinal));
                    StringAssert.Contains(notice, uiFailure.Message, "通知の文面に元の例外が含まれます。");
                }
                foreach (SettingsWindow window in settingsWindows) { Assert.IsFalse(window.IsVisible); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.WaitForBackgroundTerminalAsync(), "initial-start-wpf-background-terminal");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Composition.OperationAdmission.WaitForIdleAsync(), "initial-start-wpf-library-idle");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Composition.PlaylistOperationAdmission.WaitForIdleAsync(), "initial-start-wpf-playlist-idle");
                Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
                Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);

                async Task ExerciseAsync(SettingsWindow presented, int index)
                {
                    try
                    {
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        var start = (Button)presented.FindName("buttonOK");
                        if (index == 2)
                        {
                            // 通知を閉じた後の再表示：元の操作は終端済みで、同じ設定のViewModelから開始ボタンと案内文を表示して有効にします。
                            Assert.IsTrue(firstOperation!.IsCompleted);
                            Assert.IsTrue(notificationEntered.Task.IsCompleted);
                            Assert.AreSame(fixture.Owner.SettingDialog, presented.DataContext);
                            Assert.AreEqual(Visibility.Visible, start.Visibility);
                            Assert.IsTrue(((TextBlock)presented.FindName("settingsStartGuidanceText")).IsVisible);
                            Assert.IsTrue(start.IsEnabled);
                            Assert.IsTrue(lifetime.IsFirstStartup, "失敗した開始は初回の状態を保ちます。");
                            Assert.IsFalse(fixture.Owner.HasActiveLibraryProfile);
                            Assert.IsFalse(fixture.Owner.IsInitializationCompleted);
                            Assert.IsTrue(fixture.Owner.ProgressHub.StartupProgress.IsFailed);
                            Assert.IsTrue(fixture.Owner.ProgressHub.StartupProgress.IsRetryableFailure);
                            Assert.IsFalse(fixture.Owner.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked);
                        }
                        else
                        {
                            Assert.IsTrue(start.IsEnabled);
                            Assert.AreEqual(Visibility.Visible, start.Visibility);
                        }
                        fixture.Ui.Arm();
                        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Task operation = fixture.Owner.SettingDialog.SettingsOperationCompletionTask;
                        // 連打は一回だけ受理し、同じ操作のTaskを共有します。
                        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.AreSame(operation, fixture.Owner.SettingDialog.SettingsOperationCompletionTask);
                        if (index == 1) { firstOperation = operation; }
                        await TestUiDispatcherHost.AwaitNotificationAsync(fixture.Ui.Entered.Task, operation, "initial-start-wpf-required-ui");
                        // 必須UIの境界：設定画面は閉鎖済みで、初期化は未完了、受付は親へ戻っています。
                        Assert.IsFalse(presented.IsVisible);
                        Assert.IsNull(presented.DataContext);
                        Assert.IsFalse(operation.IsCompleted);
                        Assert.IsFalse(fixture.Composition.OperationAdmission.IsActive);
                        Assert.IsFalse(fixture.Composition.PlaylistOperationAdmission.IsActive);
                        // 初期化中は設定画面を開き直せません。
                        int windowsAtBoundary = settingsWindows.Count;
                        fixture.Owner.SettingDialog.OpenCommand.Execute();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(windowsAtBoundary, settingsWindows.Count);
                        bool failThisRound = uiFails && index == 1;
                        if (failThisRound)
                        {
                            holdFailureNotification = true;
                            fixture.Ui.Release.TrySetException(uiFailure);
                            await TestUiDispatcherHost.AwaitNotificationAsync(notificationEntered.Task, operation, "initial-start-wpf-failure-notification");
                            Assert.IsNotNull(notificationObservation);
                            Assert.IsTrue(notificationObservation.Value.LibraryFree);
                            Assert.IsTrue(notificationObservation.Value.PlaylistFree);
                            Assert.IsFalse(notificationObservation.Value.UiBlocked);
                            // 通知の保留中もUIスレッドは進み、再表示は通知を閉じるまで出ません。
                            Assert.IsFalse(operation.IsCompleted);
                            await presented.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(1, settingsWindows.Count);
                            notificationRelease.TrySetResult();
                            await operation;
                        }
                        else
                        {
                            fixture.Ui.Release.TrySetResult();
                            await operation;
                            scenarioDone.TrySetResult();
                        }
                    }
                    catch (Exception failure)
                    {
                        scenarioDone.TrySetException(failure);
                        if (presented.IsVisible) { presented.CloseForOwnerShutdown(); }
                        throw;
                    }
                }
            }
            catch (Exception failure) { bodyFailure = failure; throw; }
            finally
            {
                fixture.Ui.Release.TrySetResult(); notificationRelease.TrySetResult();
                try
                {
                    foreach (Task interaction in interactions.ToArray())
                    {
                        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(interaction, "initial-start-wpf-interaction-cleanup"); }
                        catch (Exception failure) when (bodyFailure != null) { bodyFailure.Data["InitialStartInteractionCleanup"] = failure.ToString(); }
                    }
                }
                finally
                {
                    foreach (SettingsWindow window in settingsWindows) { if (window.IsVisible) { window.CloseForOwnerShutdown(); } }
                    ownership.DisposeAfterBodyFailure(bodyFailure);
                    Application.Current.MainWindow = previousMain;
                }
            }
        });
    }
}
