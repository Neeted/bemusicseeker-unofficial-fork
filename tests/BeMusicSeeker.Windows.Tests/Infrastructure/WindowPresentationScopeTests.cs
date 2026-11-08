using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Windows.Tests.Infrastructure;

[TestClass]
public sealed class WindowPresentationScopeTests
{
    /// <summary>ApplicationIdleを連続して塞いでも実Windowの描画・Binding更新と回収が完了することを確認し、描画待ちや後片付けをIdle到達に依存させる誤りを検出します。</summary>
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
            // ループがidleを妨げている間の停止（Cleanupを含む）だけを検出します。表示自体の停止はscopeの表示観測が担うため、
            // 負荷で描画が遅れても偽って失敗しないよう、開始はContentRenderedの成立後（ループ開始と同時）にします。
            // 4引数のコンストラクターは生成時に開始するため、開始を遅らせるにはIntervalとTickを個別に設定する。
            var watchdog = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = ConcurrencyProbe.StallTimeout };
            watchdog.Tick += (_, _) =>
            {
                watchdogTriggered = true;
                keepWorking = false;
                pendingWork?.Abort();
            };
            window.ContentRendered += (_, _) =>
            {
                contentRendered = true;
                watchdog.Start();
                // 実描画の通知を妨げず、その成立後だけ一般 idle の到達を止める。
                ContinueWork();
                idleBarrier = dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle, new Action(() => idleReached = true));
            };
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
                nint handle = NativeWindowObservation.GetNativeHandle(window);
                Assert.AreNotEqual(0, handle);
                Assert.IsTrue(NativeWindowObservation.HasNoActivateStyle(handle));
                Assert.IsTrue(NativeWindowObservation.IsOutsideAllMonitors(handle));
                Assert.AreNotEqual(handle, NativeWindowObservation.ForegroundWindow);
                scope.Cleanup();
                Assert.IsFalse(watchdogTriggered, "Cleanup must complete from actual closure without general idle.");
                Assert.IsFalse(idleReached);
                Assert.IsFalse(Application.Current.Windows.Cast<Window>().Contains(window));
                Assert.IsFalse(NativeWindowObservation.NativeWindowExists(handle));
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

    /// <summary>内側のスコープ開始後に表示した外側の親とそのnative ownerを内側の回収で残し、外側終了では閉じることから、入れ子のWindow所有権を取り違える誤りを検出します。</summary>
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
            Assert.AreEqual(0, NativeWindowObservation.GetNativeHandle(parent));
            HashSet<nint> nativeBeforePresentation = NativeWindowObservation.EnumerateThreadWindows();
            var inner = new TestWindowPresentationScope(Application.Current, NativeWindowObservation.GetCurrentNativeThreadId());
            try
            {
                outer.ShowAndWaitForContentRendered(parent);
                nint parentHandle = NativeWindowObservation.GetNativeHandle(parent);
                Assert.AreNotEqual(0, parentHandle);
                nint nativeOwner = NativeWindowObservation.GetWindow(parentHandle, 4u);
                Assert.AreNotEqual(nint.Zero, nativeOwner);
                Assert.IsFalse(nativeBeforePresentation.Contains(nativeOwner), "The native owner is created after the inner baseline.");
                Assert.IsTrue(NativeWindowObservation.NativeWindowExists(nativeOwner));
                Assert.IsTrue(NativeWindowObservation.EnumerateThreadWindows().Contains(nativeOwner),
                    "The native owner would otherwise be a new cleanup candidate.");
                inner.Cleanup();
                Assert.AreEqual(0, parentClosed,
                    $"parent=0x{parentHandle:X}, native owner=0x{nativeOwner:X}; {closeStack}");
                Assert.IsTrue(parent.IsVisible);
                Assert.IsTrue(NativeWindowObservation.NativeWindowExists(parentHandle));
                Assert.IsTrue(NativeWindowObservation.NativeWindowExists(nativeOwner));
                outer.Cleanup();
                Assert.AreEqual(1, parentClosed);
                Assert.IsFalse(NativeWindowObservation.NativeWindowExists(parentHandle));
                Assert.IsFalse(NativeWindowObservation.NativeWindowExists(nativeOwner));
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

    /// <summary>実Windowのロード・配置とHWND取得が済んでもContentRenderedを抑止した場合は待機を成功させないことを確認し、描画通知の代わりに配置完了で判定する誤りを検出します。</summary>
    /// <remarks>短い注入期限はLoaded後の通知欠落だけに適用し、読込みや回収の期限を短縮しません。</remarks>
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
            Assert.ThrowsExactly<TimeoutException>(() => scope.ShowAndWaitForContentRendered(window, TimeSpan.FromMilliseconds(100)));
            Assert.IsFalse(contentRendered);
            Assert.IsTrue(window.IsLoaded);
            Assert.AreNotEqual(0, NativeWindowObservation.GetNativeHandle(window));
        });
    }

    private sealed class RenderNotificationSuppressedWindow : Window
    {
        protected override void OnContentRendered(EventArgs e)
        {
            // 実レイアウトが成立しても完了通知だけを欠いた場合を検査します。
        }
    }

    /// <summary>親・子・Popupの実表示が非活性かつ画面外となり、スコープ終了で全HWNDを回収することを確認し、利用者画面への干渉や子ウィンドウの残留を検出します。</summary>
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
            nint windowHandle = NativeWindowObservation.GetNativeHandle(window);
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
            nint popupHandle = NativeWindowObservation.GetNativeHandle(popup.Child);
            createdHandles.Add(popupHandle);

            Assert.AreNotEqual(0, windowHandle);
            Assert.AreNotEqual(0, popupHandle);
            Assert.IsTrue(
                NativeWindowObservation.HasNoActivateStyle(windowHandle),
                "The owner HWND must retain WS_EX_NOACTIVATE.");
            Assert.IsTrue(
                NativeWindowObservation.HasNoActivateStyle(popupHandle),
                "The popup HWND must retain WS_EX_NOACTIVATE.");
            Assert.IsTrue(NativeWindowObservation.IsOutsideAllMonitors(windowHandle));
            Assert.AreNotEqual(windowHandle, NativeWindowObservation.ForegroundWindow);
            Assert.IsTrue(NativeWindowObservation.IsOutsideAllMonitors(popupHandle));
            Assert.AreNotEqual(popupHandle, NativeWindowObservation.ForegroundWindow);

            var child = new Window
            {
                Owner = window,
                Width = 160,
                Height = 100,
                Content = new Border { Width = 40, Height = 20 }
            };
            child.Closed += (_, _) =>
            {
                Assert.IsFalse(NativeWindowObservation.NativeWindowExists(popupHandle));
                closeEvents.Add("child");
            };
            scope.ShowAndWaitForContentRendered(child);
            nint childHandle = NativeWindowObservation.GetNativeHandle(child);
            createdHandles.Add(childHandle);
            Assert.AreNotEqual(0, childHandle);
            Assert.IsTrue(
                NativeWindowObservation.HasNoActivateStyle(childHandle),
                "The owned child HWND must retain WS_EX_NOACTIVATE.");
            Assert.IsTrue(NativeWindowObservation.IsOutsideAllMonitors(childHandle));
            Assert.AreNotEqual(childHandle, NativeWindowObservation.ForegroundWindow);
        });

        CollectionAssert.AreEqual(new[] { "popup", "child", "owner" }, closeEvents);
        foreach (nint handle in createdHandles)
        {
            Assert.IsFalse(NativeWindowObservation.NativeWindowExists(handle));
        }
    }

    /// <summary>本体・表示・回収の失敗が重なる各組合せで優先例外と二次失敗情報を保持することを確認し、後片付けの失敗で元の失敗を上書きする誤りを検出します。</summary>
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
                Assert.AreNotEqual(0, NativeWindowObservation.GetNativeHandle(window));

                var popup = new Popup
                {
                    PlacementTarget = target,
                    Child = new Border { Width = 80, Height = 40 }
                };
                scope.TrackPopup(popup);
                popup.IsOpen = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreNotEqual(0, NativeWindowObservation.GetNativeHandle(popup.Child));
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

    /// <summary>共通scopeの内部実装を通さず、Win32の実結果を表示ポリシーの判定基準にします。</summary>
    private static class NativeWindowObservation
    {
        internal static nint GetNativeHandle(Visual visual)
            => visual is Window window ? new WindowInteropHelper(window).Handle
                : (PresentationSource.FromVisual(visual) as HwndSource)?.Handle ?? 0;

        internal static bool HasNoActivateStyle(nint handle)
        {
            Assert.IsTrue(NativeWindowExists(handle));
            Marshal.SetLastPInvokeError(0);
            nint style = GetWindowLongPtr(handle, -20);
            int error = Marshal.GetLastPInvokeError();
            if (style == 0 && error != 0) { throw new System.ComponentModel.Win32Exception(error); }
            return (style.ToInt64() & 0x08000000L) != 0;
        }

        internal static bool IsOutsideAllMonitors(nint handle)
        {
            if (!GetWindowRect(handle, out NativeRect bounds))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
            Assert.IsTrue(bounds.Right > bounds.Left && bounds.Bottom > bounds.Top, "実HWNDに非ゼロの矩形が必要です。");
            bool intersects = false;
            bool anyMonitor = false;
            if (!EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeRect rect, nint data) =>
                {
                    anyMonitor = true;
                    intersects |= bounds.Left < rect.Right && rect.Left < bounds.Right
                        && bounds.Top < rect.Bottom && rect.Top < bounds.Bottom;
                    return true;
                }, 0))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }
            Assert.IsTrue(anyMonitor, "観測するmonitorが必要です。");
            return !intersects;
        }

        internal static HashSet<nint> EnumerateThreadWindows()
        {
            HashSet<nint> handles = [];
            Marshal.SetLastPInvokeError(0);
            if (!EnumThreadWindows(GetCurrentNativeThreadId(), (handle, _) => { handles.Add(handle); return true; }, 0))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 0) { throw new System.ComponentModel.Win32Exception(error); }
            }
            return handles;
        }

        internal static nint ForegroundWindow => GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowCallback(nint handle, nint parameter);
        private delegate bool EnumMonitorCallback(nint monitor, nint dc, ref NativeRect rect, nint parameter);

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
        internal static extern uint GetCurrentNativeThreadId();
        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool NativeWindowExists(nint handle);
        [DllImport("user32.dll")]
        internal static extern nint GetWindow(nint handle, uint command);
        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern nint GetWindowLongPtr(nint handle, int index);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(nint handle, out NativeRect rect);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(nint dc, nint clip, EnumMonitorCallback callback, nint parameter);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowCallback callback, nint parameter);
    }

    /// <summary>
    /// 表示範囲で準備したWindowは、未準備のWindowの表示として記録されないことを確認します。準備済みを記録する誤り（誤検出）を検出します。
    /// モーダルは、モーダルループの入れ子と前面の変更を共有ホストの全ケースへ持ち込まないため、恒久テストにしません。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void PreparedWindow_IsNotRecordedAsUnprepared()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var window = new Window { Title = "prepared-marker", Width = 200, Height = 120, Content = new TextBlock { Text = "prepared" } };
            scope.ShowAndWaitForContentRendered(window);
        });

        // 自分のタイトルの記録だけを消費します。同じケースの別の由来のBは境界に残り、失敗になります。
        Assert.IsEmpty(HostObservationLog.TakeCurrentCase("B", record => record.Detail.Contains("title='prepared-marker'")));
    }

    /// <summary>
    /// 準備を通さずに表示したWindowが、閉じた後も記録に残り、記録しても表示・Close・Dispatcherの進行を変えないことを確認します。
    /// 記録しない実装と、検出器が閉じる・Dispatcherを進める実装を検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void UnpreparedWindow_IsRecordedAfterCleanup_WithoutChangingItsBehavior()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            bool backgroundRan = false;
            bool closing = false;
            var window = new Window
            {
                Title = "unprepared-marker",
                Width = 200,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2048d,
                Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 2048d
            };
            window.Closing += (_, _) => closing = true;
            DispatcherOperation background = window.Dispatcher.InvokeAsync(() => backgroundRan = true, DispatcherPriority.Background);
            try
            {
                window.Show();
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.IsTrue(window.IsLoaded);
                Assert.IsTrue(window.IsVisible);
                Assert.IsFalse(closing, "記録はWindowを閉じません。");
                Assert.IsFalse(backgroundRan, "記録はDispatcherをLoadedより先へ進めません。");
            }
            finally { background.Abort(); }
        });

        // このテストのタイトルの記録だけを消費します。同じケースの別の由来のBは境界に残り、失敗になります。
        IReadOnlyList<HostObservation> records = HostObservationLog.TakeCurrentCase("B", record => record.Detail.Contains("title='unprepared-marker'"));
        Assert.HasCount(1, records);
        StringAssert.Contains(records[0].Detail, typeof(Window).FullName ?? throw new AssertFailedException("The Window type has no full name."));
        StringAssert.Contains(records[0].Detail, "owner=(none)");
        StringAssert.Contains(records[0].Detail, "thread-modal=no");
    }

    /// <summary>
    /// 準備を通さずに表示した（画面外・非アクティブ・タスクバー非表示にしたが、準備は通していない）Windowを、復元が閉じ、
    /// 表示が外れ、Application.Windowsから外れ、復元の失敗が空であることを確認します。閉じない実装を検出します。
    /// 同じWindowの未準備の表示の記録（B）は、このテストのタイトルのものだけを消費します。
    /// 復元の直後に、同期でHWNDが破棄されていることを表明します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Restore_ClosesShownUnpreparedWindow()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            Application application = Application.Current;
            var state = new HostResidueState(HostResidueBoundary.Observe());
            var window = new Window
            {
                Title = "restore-f-marker",
                Width = 200,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2048d,
                Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 2048d
            };
            window.Show();
            TestUiDispatcherHost.ProcessQueuedPresentation();
            nint handle = new WindowInteropHelper(window).Handle;
            Assert.IsTrue(window.IsVisible);
            Assert.AreNotEqual((nint)0, handle);

            IReadOnlyList<HostResidue> found = HostResidueComparison.Compare(state, HostResidueBoundary.Observe());
            Assert.AreEqual(1, found.Count(residue => residue.Category == "E-a" && ReferenceEquals(residue.Window, window)));

            // このテストのWindowの残留だけを復元します（他の由来の残留を戻しません）。
            IReadOnlyList<HostRestoreFailure> failures = HostResidueRestoration.Restore(application, state, [.. found.Where(residue => ReferenceEquals(residue.Window, window))]);

            Assert.IsEmpty(failures);
            Assert.IsFalse(window.IsVisible);
            Assert.IsFalse(application.Windows.Cast<Window>().Contains(window));
            Assert.IsFalse(TestWindowPresentationScope.NativeWindowExists(handle));
        });

        Assert.HasCount(1, HostObservationLog.TakeCurrentCase("B", record => record.Detail.Contains("title='restore-f-marker'")));
    }
}
