using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Parago.Windows;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class WpfTestApplicationHostTests
{
    /// <summary>既終端と受理後終了の要求が、停止の二次失敗を含め元例外とthread回収を保持します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void OwnedDispatcher_HostFailureEndsAcceptedAndLaterRequests(bool requestAfterEnd, bool shutdownFails)
    {
        using var ready = new ManualResetEventSlim();
        using var fatalEntered = new ManualResetEventSlim();
        using var releaseFatal = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        var original = new InvalidOperationException("host failure");
        var secondary = new InvalidOperationException("shutdown failure");
        Dispatcher? dispatcher = null;
        Exception? terminalFailure = null;
        Task? request = null;
        DispatcherOperation? accepted = null;
        int actionCalls = 0;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            if (shutdownFails) { dispatcher.ShutdownStarted += (_, _) => throw secondary; }
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                fatalEntered.Set();
                releaseFatal.Wait();
                throw original;
            }));
            ready.Set();
            try { Dispatcher.Run(); }
            catch (Exception exception) { terminalFailure = exception; }
            finally
            {
                TestUiDispatcherHost.CompleteOwnedDispatcher(dispatcher, completed, exception =>
                {
                    if (terminalFailure == null) { terminalFailure = exception; }
                    else { terminalFailure.Data["TestDispatcherShutdownFailure"] = exception.ToString(); }
                });
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            ready.Wait();
            fatalEntered.Wait();
            Dispatcher target = dispatcher ?? throw new AssertFailedException("The owned dispatcher was not created.");
            if (requestAfterEnd)
            {
                releaseFatal.Set();
                completed.Wait();
                Assert.AreSame(original, Assert.ThrowsException<InvalidOperationException>(() =>
                    TestUiDispatcherHost.InvokeOnOwnedDispatcher(target, thread, completed, () => terminalFailure,
                        () => actionCalls++)));
            }
            else
            {
                var registered = new TaskCompletionSource<DispatcherOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherHookEventHandler posted = (_, args) =>
                {
                    if (args.Operation.Priority == DispatcherPriority.Send) { registered.TrySetResult(args.Operation); }
                };
                target.Hooks.OperationPosted += posted;
                try
                {
                    request = Task.Run(() => TestUiDispatcherHost.InvokeOnOwnedDispatcher(target, thread, completed,
                        () => terminalFailure, () => actionCalls++));
                    Task.WhenAny(registered.Task, request).GetAwaiter().GetResult();
                    if (request.IsCompleted) { request.GetAwaiter().GetResult(); Assert.Fail("The request ended before its actual registration."); }
                    accepted = registered.Task.GetAwaiter().GetResult();
                    releaseFatal.Set();
                    Assert.AreSame(original, Assert.ThrowsException<InvalidOperationException>(() => request.GetAwaiter().GetResult()));
                }
                finally { target.Hooks.OperationPosted -= posted; }
            }
            Assert.IsFalse(thread.IsAlive, "The target recovery must join its thread before returning the failure.");
            Assert.AreEqual(0, actionCalls);
            Assert.IsTrue(completed.IsSet);
            if (accepted != null) { Assert.IsTrue(accepted.Task.IsCompleted); }
            if (shutdownFails)
            {
                string detail = original.Data["TestDispatcherShutdownFailure"] as string
                    ?? throw new AssertFailedException("The secondary shutdown failure was not preserved.");
                StringAssert.Contains(detail, secondary.Message);
            }
            Assert.AreSame(original, Assert.ThrowsException<InvalidOperationException>(() =>
                TestUiDispatcherHost.JoinOwnedDispatcher(thread, completed, () => terminalFailure)));
            Assert.IsFalse(thread.IsAlive);
        }
        finally
        {
            releaseFatal.Set();
            completed.Wait();
            thread.Join();
            if (request != null)
            {
                try { request.GetAwaiter().GetResult(); }
                catch (Exception exception) when (ReferenceEquals(exception, original)) { }
            }
        }
    }

    [TestMethod]
    public void Invoke_ActionFailurePreservesHealthyHostAndFollowingRequest()
    {
        var original = new InvalidOperationException("action failure");
        Assert.AreSame(original, Assert.ThrowsException<InvalidOperationException>(() => TestUiDispatcherHost.Invoke(() => throw original)));
        bool followingCompleted = false;
        TestUiDispatcherHost.Invoke(() => followingCompleted = true);
        Assert.IsTrue(followingCompleted);
    }

    [TestMethod]
    public void OwnedDispatcher_NormalRecoveryJoinsBeforeReturning()
    {
        using var ready = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        Exception? terminalFailure = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            try { Dispatcher.Run(); }
            catch (Exception exception) { terminalFailure = exception; }
            finally { TestUiDispatcherHost.CompleteOwnedDispatcher(dispatcher, completed, exception => terminalFailure ??= exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            ready.Wait();
            (dispatcher ?? throw new AssertFailedException("The owned dispatcher was not created.")).BeginInvokeShutdown(DispatcherPriority.Send);
            TestUiDispatcherHost.JoinOwnedDispatcher(thread, completed, () => terminalFailure);
            Assert.IsFalse(thread.IsAlive);
            Assert.IsTrue(completed.IsSet);
            Assert.IsNull(terminalFailure);
        }
        finally
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            completed.Wait();
            thread.Join();
        }
    }

    /// <summary>通常Taskの待機が予約consumerの成功・失敗・取消をそのまま観測します。</summary>
    [DataTestMethod]
    [DataRow("success")]
    [DataRow("failure")]
    [DataRow("canceled")]
    [DataRow("producer_failure")]
    [DataRow("producer_canceled")]
    [DataRow("missing")]
    public void AwaitTaskOnDispatcher_ObservesScheduledConsumerTerminal(string outcome)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new InvalidOperationException("consumer failed");
            DispatcherOperation producer = TestUiDispatcherHost.Dispatcher.InvokeAsync(new Action(() =>
            {
                switch (outcome)
                {
                    case "success": completion.TrySetResult(); break;
                    case "failure": completion.TrySetException(failure); break;
                    case "canceled": completion.TrySetCanceled(); break;
                    case "producer_failure": throw failure;
                }
            }), DispatcherPriority.Background, outcome == "producer_canceled" ? new CancellationToken(canceled: true) : CancellationToken.None);
            Task observation = TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, producer.Task, outcome);
            try
            {
                switch (outcome)
                {
                    case "success": TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, outcome); break;
                    case "failure":
                    case "producer_failure":
                        Assert.AreSame(failure, Assert.ThrowsException<InvalidOperationException>(() =>
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, outcome)));
                        break;
                    case "canceled":
                    case "producer_canceled":
                        Assert.ThrowsException<TaskCanceledException>(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, outcome));
                        if (outcome == "producer_canceled") { Assert.IsTrue(producer.Task.IsCanceled); }
                        break;
                    case "missing":
                        Assert.ThrowsException<InvalidOperationException>(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, outcome));
                        break;
                }
            }
            finally
            {
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(producer.Task, "consumer-test-producer-terminal"); }
                catch (InvalidOperationException exception) when (outcome == "producer_failure" && ReferenceEquals(exception, failure)) { }
                catch (TaskCanceledException) when (outcome == "producer_canceled") { }
            }
        });
    }

    [TestMethod]
    public void Invoke_ReusesSingleApplicationDispatcherAndStaThread()
    {
        Application? firstApplication = null;
        Dispatcher? firstDispatcher = null;
        Thread? firstThread = null;
        ApartmentState firstApartmentState = ApartmentState.Unknown;
        TestUiDispatcherHost.Invoke(() =>
        {
            firstApplication = Application.Current;
            firstDispatcher = Dispatcher.CurrentDispatcher;
            firstThread = Thread.CurrentThread;
            firstApartmentState = Thread.CurrentThread.GetApartmentState();
        });

        Application? secondApplication = null;
        Dispatcher? secondDispatcher = null;
        Thread? secondThread = null;
        TestUiDispatcherHost.Invoke(() =>
        {
            secondApplication = Application.Current;
            secondDispatcher = Dispatcher.CurrentDispatcher;
            secondThread = Thread.CurrentThread;
        });

        Assert.IsNotNull(firstApplication);
        Assert.AreSame(firstApplication, secondApplication);
        Assert.AreSame(firstDispatcher, secondDispatcher);
        Assert.AreSame(TestUiDispatcherHost.Dispatcher, firstDispatcher);
        Assert.AreSame(firstThread, secondThread);
        Assert.AreEqual(ApartmentState.STA, firstApartmentState);
    }

    [TestMethod]
    public void Startup_AppliesLightComponentThemeWithSemanticResources()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Assert.IsNotNull(application);
            ResourceDictionary theme = application.Resources.MergedDictionaries.Single(dictionary =>
                string.Equals(
                    dictionary.Source?.OriginalString,
                    "/BeMusicSeeker;component/Themes/Light.xaml",
                    StringComparison.OrdinalIgnoreCase));

            Assert.IsInstanceOfType<SolidColorBrush>(theme["App.BackgroundBrush"]);
            Assert.IsInstanceOfType<SolidColorBrush>(theme["App.TextBrush"]);
            Assert.AreEqual(AppThemeService.Light, BeMusicSeeker.Properties.Settings.Default.AppearanceTheme);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void CompiledDialogSurfaces_ResolveSemanticBrushesOnConstructorOnlyControls()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var settingsWindow = new SettingsWindow();
            var settingsOperationRoot = (Grid)settingsWindow.FindName("settingDialogOperationGrid");
            AssertBrush(settingsOperationRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            AssertBrush(settingsWindow.Background, "App.DialogBackgroundBrush");
            var warningField = new SettingsField
            {
                Header = "header",
                ValidationMessage = "warning",
                Content = new TextBox(),
                Style = (Style)settingsWindow.FindResource(typeof(SettingsField))
            };
            warningField.ApplyTemplate();
            TextBlock warningText = FindVisualDescendantsInVisualTree<TextBlock>(warningField)
                .Single(textBlock => textBlock.Text == "warning");
            AssertBrush(warningText.Foreground, "App.WarningTextBrush");

            var initialSetupDialog = new InitialSetupLanguageDialog();
            Grid initialSetupRoot = FindVisualDescendants<Grid>(initialSetupDialog).Single();
            Rectangle overlay = FindVisualDescendants<Rectangle>(initialSetupDialog).Single();
            Border initialSetupSurface = FindVisualDescendants<Border>(initialSetupDialog).Single();
            AssertBrush(initialSetupRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            AssertBrush(overlay.Fill, "App.DialogOverlayBrush");
            AssertBrush(initialSetupSurface.Background, "App.DialogBackgroundBrush");
            AssertBrush(initialSetupSurface.BorderBrush, "App.DialogBorderBrush");

            var playlistPropertyDialog = new PlaylistPropertyDialog();
            Grid playlistPropertyRoot = FindVisualDescendants<Grid>(playlistPropertyDialog)
                .Single(grid => FindResourceInScope(grid.Resources, "App.Canonical.NativeWindowContentStyle") is Style);
            AssertBrush(playlistPropertyRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            Border playlistPropertyContent = FindElementWithScopedStyle<Border>(
                playlistPropertyRoot,
                "App.Canonical.NativeWindowContentStyle");
            Assert.IsNull(playlistPropertyContent.Background);
            Assert.IsNull(playlistPropertyContent.BorderBrush);

            var loadPlaylistDialog = new LoadPlaylistURIDialog();
            Grid loadPlaylistRoot = FindVisualDescendants<Grid>(loadPlaylistDialog)
                .Single(grid => FindResourceInScope(grid.Resources, "App.Canonical.DialogContentStyle") is Style);
            AssertBrush(loadPlaylistRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            Rectangle loadPlaylistOverlay = FindElementWithScopedStyle<Rectangle>(
                loadPlaylistRoot,
                "App.Canonical.DialogOverlayStyle");
            Border loadPlaylistContent = FindElementWithScopedStyle<Border>(
                loadPlaylistRoot,
                "App.Canonical.DialogContentStyle");
            AssertBrush(loadPlaylistOverlay.Fill, "App.DialogOverlayBrush");
            AssertBrush(loadPlaylistContent.Background, "App.DialogBackgroundBrush");
            AssertBrush(loadPlaylistContent.BorderBrush, "App.DialogBorderBrush");

            var pendingDeleteDialog = new PendingDeleteConfirmDialog();
            AssertBrush(pendingDeleteDialog.Background, "App.DialogBackgroundBrush");
            AssertBrush(pendingDeleteDialog.Foreground, "App.TextBrush");

            var progressDialog = new ProgressDialog(new ProgressDialogSettings());
            AssertBrush(progressDialog.Background, "App.DialogBackgroundBrush");
            AssertBrush(progressDialog.Foreground, "App.TextBrush");
            Border progressDialogContent = FindElementWithScopedStyle<Border>(
                progressDialog,
                "App.Canonical.NativeWindowContentStyle");
            Assert.IsNull(progressDialogContent.Background);
            Assert.IsNull(progressDialogContent.BorderBrush);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void PresentationAndCleanup_CompleteWhileApplicationIdleIsBlocked()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var source = new TextBox { Text = "before" };
            var reflected = new TextBox();
            reflected.SetBinding(TextBox.TextProperty, new Binding(nameof(TextBox.Text)) { Source = source });
            var selection = new ListBox { ItemsSource = new[] { "first", "second" }, SelectedIndex = 0 };
            var content = new StackPanel();
            content.Children.Add(source);
            content.Children.Add(reflected);
            content.Children.Add(selection);
            var window = new Window { Width = 320, Height = 200, Content = content };
            Dispatcher dispatcher = window.Dispatcher;
            bool keepWorking = true;
            bool idleReached = false;
            bool contentRendered = false;
            bool watchdogTriggered = false;
            int workCount = 0;
            DispatcherOperation? pendingWork = null;
            DispatcherOperation? idleBarrier = null;
            void ContinueWork()
            {
                workCount++;
                if (keepWorking)
                {
                    pendingWork = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ContinueWork));
                }
            }
            window.ContentRendered += (_, _) =>
            {
                contentRendered = true;
                // 実描画の通知を妨げず、その成立後だけ一般 idle の到達を止める。
                ContinueWork();
                idleBarrier = dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle, new Action(() => idleReached = true));
            };
            var watchdog = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Send, (_, _) =>
            {
                watchdogTriggered = true;
                keepWorking = false;
                pendingWork?.Abort();
            }, dispatcher);
            watchdog.Start();
            try
            {
                scope.ShowAndWaitForContentRendered(window);
                Assert.IsFalse(watchdogTriggered, "The presentation wait must complete from the real render outcome.");
                Assert.IsTrue(contentRendered);
                Assert.IsTrue(window.IsLoaded);
                Assert.IsTrue(window.ActualWidth > 0 && window.ActualHeight > 0);
                Assert.IsTrue(workCount > 0);
                Assert.IsFalse(idleReached);
                source.Text = "after";
                selection.SelectedIndex = 1;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual("after", reflected.Text);
                Assert.AreEqual("second", selection.SelectedItem);
                Assert.IsTrue(reflected.ActualWidth > 0 && reflected.ActualHeight > 0);
                Assert.IsTrue(selection.ActualWidth > 0 && selection.ActualHeight > 0);
                Assert.IsFalse(idleReached);
                Assert.IsFalse(watchdogTriggered);
                nint handle = TestWindowPresentationScope.GetNativeHandle(window);
                Assert.AreNotEqual(0, handle);
                Assert.IsTrue(TestWindowPresentationScope.HasNoActivateStyle(handle));
                Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(handle));
                Assert.AreNotEqual(handle, TestWindowPresentationScope.ForegroundWindow);
                scope.Cleanup();
                Assert.IsFalse(watchdogTriggered, "Cleanup must complete from actual closure without general idle.");
                Assert.IsFalse(idleReached);
                Assert.IsFalse(Application.Current.Windows.Cast<Window>().Contains(window));
                Assert.IsFalse(TestWindowPresentationScope.NativeWindowExists(handle));
            }
            finally
            {
                keepWorking = false;
                watchdog.Stop();
                pendingWork?.Abort();
                idleBarrier?.Abort();
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void NestedCleanup_PreservesExistingParentShownAfterInnerScopeStarts()
    {
        TestUiDispatcherHost.RunWindowTest(outer =>
        {
            var parent = new Window { Width = 320, Height = 200, Content = new Border() };
            int parentClosed = 0;
            string? closeStack = null;
            EventHandler closed = (_, _) =>
            {
                parentClosed++;
                closeStack = Environment.StackTrace;
            };
            parent.Closed += closed;
            // Application/native全体のbaselineを比べるため、別のpresentation scopeの再入を隔離します。
            Assert.IsTrue(Application.Current.Windows.Cast<Window>().Contains(parent));
            Assert.AreEqual(0, TestWindowPresentationScope.GetNativeHandle(parent));
            var inner = new TestWindowPresentationScope(Application.Current, TestWindowPresentationScope.GetCurrentNativeThreadId());
            try
            {
                outer.ShowAndWaitForContentRendered(parent);
                nint parentHandle = TestWindowPresentationScope.GetNativeHandle(parent);
                Assert.AreNotEqual(0, parentHandle);
                MethodInfo nativeOwnerQuery = typeof(TestWindowPresentationScope).GetMethod(
                    "GetWindow", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new AssertFailedException("The native owner query was not found.");
                nint nativeOwner = (nint)(nativeOwnerQuery.Invoke(null, [parentHandle, 4u])
                    ?? throw new AssertFailedException("The native owner query did not return a handle."));
                Assert.AreNotEqual(nint.Zero, nativeOwner);
                Assert.IsTrue(TestWindowPresentationScope.NativeWindowExists(nativeOwner));
                var baselineHandles = (HashSet<nint>)(typeof(TestWindowPresentationScope).GetField(
                    "baselineNativeWindows", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(inner)
                    ?? throw new AssertFailedException("The native baseline was not found."));
                Assert.IsFalse(baselineHandles.Contains(nativeOwner), "The native owner is created after the inner baseline.");
                var threadHandles = (IReadOnlyList<nint>)(typeof(TestWindowPresentationScope).GetMethod(
                    "EnumerateNativeWindows", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(
                        null, [TestWindowPresentationScope.GetCurrentNativeThreadId()])
                    ?? throw new AssertFailedException("The native thread inventory was not found."));
                Assert.IsTrue(threadHandles.Contains(nativeOwner), "The native owner would otherwise be a new cleanup candidate.");
                inner.Cleanup();
                Assert.AreEqual(0, parentClosed,
                    $"parent=0x{parentHandle:X}, native owner=0x{nativeOwner:X}; {closeStack}");
                Assert.IsTrue(parent.IsVisible);
                Assert.IsTrue(TestWindowPresentationScope.NativeWindowExists(parentHandle));
                Assert.IsTrue(TestWindowPresentationScope.NativeWindowExists(nativeOwner));
                outer.Cleanup();
                Assert.AreEqual(1, parentClosed);
                Assert.IsFalse(TestWindowPresentationScope.NativeWindowExists(parentHandle));
                Assert.IsFalse(TestWindowPresentationScope.NativeWindowExists(nativeOwner));
                Assert.IsFalse(Application.Current.Windows.Cast<Window>().Contains(parent));
            }
            finally
            {
                try { inner.Cleanup(); }
                finally
                {
                    try { outer.Cleanup(); }
                    finally { parent.Closed -= closed; }
                }
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void ShowAndWaitForContentRendered_DoesNotSucceedWithoutRenderNotification()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var window = new RenderNotificationSuppressedWindow
            {
                Width = 320,
                Height = 200,
                Content = new Border()
            };
            bool contentRendered = false;
            window.ContentRendered += (_, _) => contentRendered = true;
            Assert.ThrowsException<TimeoutException>(() => scope.ShowAndWaitForContentRendered(window));
            Assert.IsFalse(contentRendered);
            Assert.IsTrue(window.IsLoaded);
            Assert.AreNotEqual(0, TestWindowPresentationScope.GetNativeHandle(window));
        });
    }

    private sealed class RenderNotificationSuppressedWindow : Window
    {
        protected override void OnContentRendered(EventArgs e)
        {
            // 実レイアウトが成立しても完了通知だけを欠いた場合を検査します。
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void RunWindowTest_NonActivatingPresentationStaysOffscreenAndCleansWindowAndPopupHwnds()
    {
        var closeEvents = new List<string>();
        var createdHandles = new List<nint>();
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var target = new Button { Content = "target" };
            var window = new Window
            {
                Width = 320,
                Height = 200,
                Content = target
            };
            window.Closed += (_, _) => closeEvents.Add("owner");
            scope.ShowAndWaitForContentRendered(window);
            nint windowHandle = TestWindowPresentationScope.GetNativeHandle(window);
            createdHandles.Add(windowHandle);

            var popup = new Popup
            {
                PlacementTarget = target,
                Child = new Border { Width = 80, Height = 40 }
            };
            popup.Closed += (_, _) => closeEvents.Add("popup");
            scope.TrackPopup(popup);
            popup.IsOpen = true;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            nint popupHandle = TestWindowPresentationScope.GetNativeHandle(popup.Child);
            createdHandles.Add(popupHandle);

            Assert.AreNotEqual(0, windowHandle);
            Assert.AreNotEqual(0, popupHandle);
            Assert.IsTrue(
                TestWindowPresentationScope.HasNoActivateStyle(windowHandle),
                "The owner HWND must retain WS_EX_NOACTIVATE.");
            Assert.IsTrue(
                TestWindowPresentationScope.HasNoActivateStyle(popupHandle),
                "The popup HWND must retain WS_EX_NOACTIVATE.");
            Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(windowHandle));
            Assert.AreNotEqual(windowHandle, TestWindowPresentationScope.ForegroundWindow);
            Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(popupHandle));
            Assert.AreNotEqual(popupHandle, TestWindowPresentationScope.ForegroundWindow);

            var child = new Window
            {
                Owner = window,
                Width = 160,
                Height = 100,
                Content = new Border { Width = 40, Height = 20 }
            };
            child.Closed += (_, _) =>
            {
                Assert.IsFalse(TestWindowPresentationScope.NativeWindowExists(popupHandle));
                closeEvents.Add("child");
            };
            scope.ShowAndWaitForContentRendered(child);
            nint childHandle = TestWindowPresentationScope.GetNativeHandle(child);
            createdHandles.Add(childHandle);
            Assert.AreNotEqual(0, childHandle);
            Assert.IsTrue(
                TestWindowPresentationScope.HasNoActivateStyle(childHandle),
                "The owned child HWND must retain WS_EX_NOACTIVATE.");
            Assert.IsTrue(TestWindowPresentationScope.IsOutsideAllMonitors(childHandle));
            Assert.AreNotEqual(childHandle, TestWindowPresentationScope.ForegroundWindow);
        });

        CollectionAssert.AreEqual(new[] { "popup", "child", "owner" }, closeEvents);
        foreach (nint handle in createdHandles)
        {
            Assert.IsFalse(TestWindowPresentationScope.NativeWindowExists(handle));
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void RunWindowTest_FailurePriorityIsBodyThenPresentationThenCleanup()
    {
        var expected = new InvalidOperationException("body failure");
        var expectedPresentation = new InvalidOperationException("presentation failure");
        var expectedCleanup = new InvalidOperationException("cleanup failure");

        InvalidOperationException? actual = null;
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                var target = new Button { Content = "target" };
                var window = new Window { Width = 320, Height = 200, Content = target };
                scope.ShowAndWaitForContentRendered(window);
                Assert.AreNotEqual(0, TestWindowPresentationScope.GetNativeHandle(window));

                var popup = new Popup
                {
                    PlacementTarget = target,
                    Child = new Border { Width = 80, Height = 40 }
                };
                scope.TrackPopup(popup);
                popup.IsOpen = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreNotEqual(0, TestWindowPresentationScope.GetNativeHandle(popup.Child));
                scope.RegisterPresentationFailureForTesting(expectedPresentation);
                scope.RegisterCleanupFailureForTesting(expectedCleanup);
                throw expected;
            });
        }
        catch (InvalidOperationException ex)
        {
            actual = ex;
        }

        Assert.IsNotNull(actual);
        Assert.AreSame(expected, actual);
        Assert.AreEqual(
            expectedPresentation.ToString(),
            actual.Data["TestWindowPresentationFailure"]);
        Assert.AreEqual(
            expectedCleanup.ToString(),
            actual.Data["TestWindowPresentationCleanupFailure"]);

        Exception? presentationAndCleanupFailure = null;
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                scope.RegisterPresentationFailureForTesting(expectedPresentation);
                scope.RegisterCleanupFailureForTesting(expectedCleanup);
            });
        }
        catch (Exception ex)
        {
            presentationAndCleanupFailure = ex;
        }

        Assert.IsNotNull(presentationAndCleanupFailure);
        Assert.AreSame(expectedPresentation, presentationAndCleanupFailure);
        Assert.AreEqual(
            expectedCleanup.ToString(),
            presentationAndCleanupFailure.Data["TestWindowPresentationCleanupFailure"]);

        var presentationOnly = new InvalidOperationException("presentation only failure");
        Exception? presentationOnlyFailure = null;
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
                scope.RegisterPresentationFailureForTesting(presentationOnly));
        }
        catch (Exception ex)
        {
            presentationOnlyFailure = ex;
        }

        Assert.IsNotNull(presentationOnlyFailure);
        Assert.AreSame(presentationOnly, presentationOnlyFailure);

        Exception? cleanupOnlyFailure = null;
        try
        {
            TestUiDispatcherHost.RunWindowTest(scope =>
            {
                scope.RegisterCleanupFailureForTesting(expectedCleanup);
            });
        }
        catch (Exception ex)
        {
            cleanupOnlyFailure = ex;
        }

        Assert.IsNotNull(cleanupOnlyFailure);
        Assert.AreSame(expectedCleanup, cleanupOnlyFailure);
    }

    private static void AssertBrush(object value, string resourceKey)
    {
        Assert.IsInstanceOfType(value, typeof(SolidColorBrush), resourceKey + " must resolve to a SolidColorBrush.");
        Assert.AreSame(
            Application.Current.Resources[resourceKey],
            value,
            resourceKey + " must resolve through the application semantic resource.");
    }

    private static T FindElementWithScopedStyle<T>(FrameworkElement scope, object resourceKey)
        where T : FrameworkElement
    {
        object? resource = FindResourceInScope(scope.Resources, resourceKey);
        Assert.IsInstanceOfType(resource, typeof(Style), resourceKey + " must resolve from the dialog-local resource facade.");
        var expectedStyle = (Style)resource;

        T[] matches = FindVisualDescendants<T>(scope)
            .Where(element => ReferenceEquals(element.Style, expectedStyle))
            .ToArray();
        Assert.AreEqual(
            1,
            matches.Length,
            resourceKey + " must identify exactly one applied canonical role surface in its dialog scope.");
        return matches[0];
    }

    private static object? FindResourceInScope(ResourceDictionary resources, object key)
    {
        // Keep application fallback out of this lookup so a missing dialog-local facade cannot pass accidentally.
        if (resources.Contains(key))
        {
            return resources[key];
        }

        foreach (ResourceDictionary mergedDictionary in resources.MergedDictionaries.Reverse())
        {
            object? value = FindResourceInScope(mergedDictionary, key);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (object childValue in LogicalTreeHelper.GetChildren(root))
        {
            if (childValue is not DependencyObject child)
            {
                continue;
            }

            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<T> FindVisualDescendantsInVisualTree<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendantsInVisualTree<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
