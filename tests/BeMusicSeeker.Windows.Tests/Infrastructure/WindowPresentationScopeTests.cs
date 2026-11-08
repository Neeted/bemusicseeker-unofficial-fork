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
    /// 準備済みのモーダルの扱いは <see cref="PreparedModalWindow_IsNeitherClosedNorRecordedByDetector"/> が確認します。
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
            var window = new Window { Title = "unprepared-marker" };
            PlaceOffscreen(window);
            window.Closing += (_, _) => closing = true;
            DispatcherOperation background = window.Dispatcher.InvokeAsync(() => backgroundRan = true, DispatcherPriority.Background);
            try
            {
                window.Show();
                TestUiDispatcherHost.ProcessQueuedPresentation();

                // Loadedの発火はこの消化が保証しないため、Loadedに依存しないHWNDの生成と表示だけを確認します。
                Assert.AreNotEqual(0, NativeWindowObservation.GetNativeHandle(window));
                Assert.IsTrue(window.IsVisible);
                Assert.IsFalse(closing, "記録はWindowを閉じません。");
                Assert.IsFalse(backgroundRan, "記録はBackground優先度の予約を実行しません。");
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
            var window = new Window { Title = "restore-f-marker" };
            PlaceOffscreen(window);
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

    /// <summary>
    /// 準備を通さずに表示するテスト用Window（モーダル・非モーダル）を、画面外・タスクバー非表示・非アクティブ（<c>WS_EX_NOACTIVATE</c>）に置きます。
    /// 準備扱いにしないため、検出器はBを記録します。
    /// </summary>
    private static void PlaceOffscreen(Window window) => TestWindowPresentationScope.PlaceUnpreparedNonActivating(window);

    private static bool IsListed(Window window) => Application.Current.Windows.Cast<Window>().Contains(window);

    /// <summary>
    /// 検出器が閉じない誤りでテストが停止しないよう、期限の後に救済して<c>ShowDialog</c>を戻らせる見張りです。救済したかを返す関数と停止を返します。
    /// </summary>
    private static (Func<bool> WasRescued, Action Stop) StartRescueWatchdog(Window window, Action rescue)
    {
        bool rescued = false;
        var timer = new DispatcherTimer(DispatcherPriority.Send, window.Dispatcher) { Interval = ConcurrencyProbe.StallTimeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            rescued = true;
            rescue();
        };
        timer.Start();
        return (() => rescued, timer.Stop);
    }

    /// <summary>
    /// 準備していないモーダル（<c>ShowDialog</c>）のWindowを、テストの介入なしに検出器が閉じて<c>ShowDialog</c>が戻ることを確認します。
    /// Bの記録が自分のタイトルで一件あり、閉鎖の予約（close-scheduled）を含み、閉鎖の失敗（close-failed）を含まないことも確認します。
    /// 検出器が閉じない実装（見張りの救済になる）と、閉鎖の予約の追記を付けない実装を検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void UnpreparedModalWindow_IsClosedByDetectorAndStillRecorded()
    {
        bool rescued = false;
        bool listedAfter = true;
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var window = new Window { Title = "u1a-marker" };
            PlaceOffscreen(window);
            (Func<bool> wasRescued, Action stop) = StartRescueWatchdog(window, () => window.Close());
            try { window.ShowDialog(); }
            finally { stop(); }
            rescued = wasRescued();
            listedAfter = IsListed(window);
        });

        Assert.IsFalse(rescued, "The detector must close the unprepared modal window without the test's help.");
        Assert.IsFalse(listedAfter);
        IReadOnlyList<HostObservation> records = HostObservationLog.TakeCurrentCase("B", record => record.Detail.Contains("title='u1a-marker'"));
        Assert.HasCount(1, records);
        StringAssert.Contains(records[0].Detail, "action=close-scheduled");
        Assert.IsFalse(records[0].Detail.Contains("close-failed"));
    }

    /// <summary>
    /// 準備済みのモーダルを検出器が閉じず、記録もしないことを確認します（テストが自分で閉じます）。
    /// 準備済みまで閉じる誤りを検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void PreparedModalWindow_IsNeitherClosedNorRecordedByDetector()
    {
        bool listedWhenTestClosed = false;
        bool rescued = false;
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var window = new Window { Title = "u1b-marker", Content = new TextBlock { Text = "prepared modal" } };
            scope.PrepareForOwnedPresentation(window);
            // Sendの閉鎖があれば、Backgroundのこの処理より先に実行されます。
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                listedWhenTestClosed = IsListed(window);
                window.Close();
            }));
            (Func<bool> wasRescued, Action stop) = StartRescueWatchdog(window, () => window.Close());
            try { window.ShowDialog(); }
            finally { stop(); }
            rescued = wasRescued();
        });

        Assert.IsFalse(rescued);
        Assert.IsTrue(listedWhenTestClosed, "The detector must not close a prepared modal window.");
        Assert.IsEmpty(HostObservationLog.TakeCurrentCase("B", record => record.Detail.Contains("title='u1b-marker'")));
    }

    /// <summary>
    /// スレッドがモーダルでないときの未準備の非モーダルWindowは、検出器が閉じず記録だけにする（閉鎖の予約も付けない）ことを確認します。
    /// テストが自分で閉じ、記録を消費します。モーダルでないWindowまで閉じる誤りを検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void UnpreparedNonModalWindow_IsNotClosedByDetector()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var window = new Window { Title = "u1c-marker" };
            PlaceOffscreen(window);
            try
            {
                window.Show();
                TestUiDispatcherHost.ProcessQueuedBackground();
                Assert.IsTrue(window.IsLoaded);
                Assert.IsTrue(IsListed(window), "The detector must not close an unprepared non-modal window.");
            }
            finally { window.Close(); }
        });

        IReadOnlyList<HostObservation> records = HostObservationLog.TakeCurrentCase("B", record => record.Detail.Contains("title='u1c-marker'"));
        Assert.HasCount(1, records);
        StringAssert.Contains(records[0].Detail, "thread-modal=no");
        Assert.IsFalse(records[0].Detail.Contains("action="));
    }

    /// <summary>
    /// 選択関数の規則を確認します（<c>CloseForOwnerShutdown</c>を持つ三つの型はその入口、それ以外は<c>Close</c>）。
    /// 検出器と復元が同じ関数を使うことは静的に確認します。Windowを生成して動かさず、未初期化のインスタンスを使います。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void CloseEntrySelection_UsesOwnerShutdownForThreeTypes()
    {
        foreach (Type type in new[] { typeof(BeMusicSeeker.Views.SettingsWindow), typeof(BeMusicSeeker.Views.PlaylistPropertyDialog), typeof(BeMusicSeeker.Views.PlaylistSummaryBulkEditDialog) })
        {
            var window = (Window)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            Action entry = HostResidueRestoration.SelectCloseEntry(window);
            Assert.AreEqual("CloseForOwnerShutdown", entry.Method.Name, type.Name);
            Assert.AreSame(window, entry.Target, type.Name);
        }
        var plain = (Window)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Window));
        Action plainEntry = HostResidueRestoration.SelectCloseEntry(plain);
        Assert.AreEqual(nameof(Window.Close), plainEntry.Method.Name);
        Assert.AreSame(plain, plainEntry.Target);
    }

    /// <summary>
    /// 閉鎖を取り消す未準備のモーダルを検出器が閉じようとしたとき、取消が失敗（close-failed、理由canceled）として記録されることを確認します。
    /// 記録の確認は検出器の閉鎖の試行の後（取消を受けたNormalの操作）で行い、確認が失敗しても取消を解除して閉じ、<c>ShowDialog</c>を戻らせます。
    /// 取消を黙って成功にする、記録しない、取消で停止したままにする実装を検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void UnpreparedModalWindow_CancelledClose_IsRecordedAsFailure()
    {
        IReadOnlyList<HostObservation> atCancel = [];
        IReadOnlyList<HostObservation> detectorFailures = [];
        bool rescued = false;
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var window = new CancelingModalWindow { Title = "u1e-marker" };
            PlaceOffscreen(window);
            window.OnCancel = () => window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    atCancel = HostObservationLog.TakeCurrentCase("B", record => record.Detail.Contains("title='u1e-marker'"));
                    detectorFailures = HostObservationLog.TakeCurrentCase("X-detector");
                }
                finally
                {
                    window.AllowClose = true;
                    window.Close();
                }
            }));
            (Func<bool> wasRescued, Action stop) = StartRescueWatchdog(window, () =>
            {
                window.AllowClose = true;
                window.Close();
            });
            try { window.ShowDialog(); }
            finally { stop(); }
            rescued = wasRescued();
        });

        Assert.IsFalse(rescued);
        Assert.IsEmpty(detectorFailures, string.Join(" / ", detectorFailures.Select(record => record.Format())));
        Assert.HasCount(2, atCancel);
        Assert.AreEqual(1, atCancel.Count(record => record.Detail.Contains("action=close-scheduled")));
        HostObservation failed = atCancel.Single(record => record.Detail.Contains("action=close-failed"));
        StringAssert.Contains(failed.Detail, "reason=canceled");
        StringAssert.Contains(failed.Detail, typeof(CancelingModalWindow).FullName ?? throw new AssertFailedException("The type has no full name."));
    }

    /// <summary>
    /// 閉鎖の処理（OnClosing）で例外を投げる未準備のモーダルを検出器が閉じようとしたとき、その例外が検出器の閉鎖の失敗（close-failed）にならず、
    /// 投げた例外オブジェクトのまま一件だけ、このケースのDispatcherの未処理例外として記録されることを確認します。
    /// WPFがWindowを閉じて、見張りの救済もテストの解除もなしにShowDialogが戻り、自分のBは閉鎖の予約（close-scheduled）付きの一件だけで、X-detectorが0件であることも確認します。
    /// 記録と未処理例外は、ShowDialogが戻った直後、テストが例外を解除する前に受け取ります。
    /// 例外を横取り・握りつぶす実装、別の例外へ包む・置き換える実装、二重に記録する実装、閉じたWindowにclose-failedを記録する実装、
    /// 例外をX-detectorにする実装、閉鎖を試みない実装、モーダルのまま停止させる・Windowを残す実装を検出します。
    /// </summary>
    /// <remarks>
    /// 表示済みのWindowのCloseはWM_CLOSEを同期に送り、その配送の中でOnClosingを呼ぶため、例外はDispatcherの未処理例外に達し、検出器の呼出しへは返りません。
    /// この実モーダル固有の経路は、未表示のWindowの同期の例外を扱う
    /// DispatcherHostTests.Restore_ContinuesAfterCancelledOrThrowingClose_AndKeepsTheOriginalException では代用できません。
    /// close-failedを記録する取消の経路は UnpreparedModalWindow_CancelledClose_IsRecordedAsFailure が担います。
    /// </remarks>
    [TestMethod]
    [DoNotParallelize]
    public void UnpreparedModalWindow_ClosingException_IsRecordedAsUnhandledExceptionAndWindowCloses()
    {
        const string title = "closing-exception-modal-marker";
        string explanation = "(no snapshot)";
        IReadOnlyList<HostObservation> ownRecords = [];
        IReadOnlyList<HostObservation> detectorFailures = [];
        IReadOnlyList<Exception> unhandled = [];
        IReadOnlyList<ClosingRejectedException> thrown = [];
        bool rescued = false;
        Exception? showDialogFailure = null;
        bool listedAtCapture = true;

        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var window = new ThrowingClosingModalWindow { Title = title };
            PlaceOffscreen(window);

            void Release()
            {
                window.ReleaseClose = true;
                if (IsListed(window)) { window.Close(); }
            }

            (Func<bool> wasRescued, Action stop) = StartRescueWatchdog(window, Release);
            try { window.ShowDialog(); }
            catch (Exception ex) { showDialogFailure = ex; }
            finally
            {
                try
                {
                    stop();
                    rescued = wasRescued();
                    listedAtCapture = IsListed(window);
                    thrown = [.. window.Thrown];
                    ownRecords = HostObservationLog.TakeCurrentCase("B", record => record.Detail.Contains($"title='{title}'"));
                    detectorFailures = HostObservationLog.TakeCurrentCase("X-detector");
                    unhandled = TestUiDispatcherHost.TakeUnhandledExceptions();
                    explanation = string.Join(
                        Environment.NewLine,
                        $"path={(rescued ? "after-rescue" : "self-returned")} listedAtCapture={listedAtCapture} thrownCount={thrown.Count}",
                        "B: " + string.Join(" / ", ownRecords.Select(record => record.Format())),
                        "X-detector: " + string.Join(" / ", detectorFailures.Select(record => record.Format())),
                        "unhandled: " + string.Join(Environment.NewLine, unhandled.Select(ex => ex.ToString())));
                }
                finally { Release(); }
            }
        });

        string context = $"{explanation}{Environment.NewLine}rescued={rescued} showDialogFailure={showDialogFailure}";
        Assert.IsFalse(rescued, "The modal window must close and return without the test's rescue. " + context);
        Assert.IsNull(showDialogFailure, context);
        Assert.IsEmpty(detectorFailures, context);
        Assert.IsNotEmpty(thrown, "The detector never attempted to close the window. " + context);
        Assert.HasCount(1, unhandled, context);
        Assert.AreSame(thrown[0], unhandled[0], context);
        Assert.HasCount(1, ownRecords, context);
        StringAssert.Contains(ownRecords[0].Detail, "action=close-scheduled", context);
        Assert.IsFalse(ownRecords[0].Detail.Contains("action=close-failed"), context);
        Assert.IsFalse(listedAtCapture, context);
        TestUiDispatcherHost.AssertNoUnhandledExceptions("after the closing-exception modal returned");
    }

    /// <summary>閉鎖の処理を、<c>ReleaseClose</c>が真になるまでテスト固有の例外で失敗させるテスト用Windowです。投げた例外を順に<c>Thrown</c>へ保持します。</summary>
    private sealed class ThrowingClosingModalWindow : Window
    {
        internal bool ReleaseClose { get; set; }

        internal List<ClosingRejectedException> Thrown { get; } = [];

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (ReleaseClose) { return; }
            var thrown = new ClosingRejectedException();
            Thrown.Add(thrown);
            throw thrown;
        }
    }

    /// <summary>閉鎖の処理の例外を、WPFやホストが投げる例外と区別し、参照で照合するためのテスト固有の例外です。</summary>
    private sealed class ClosingRejectedException() : Exception("closing-rejected-by-test-window");

    /// <summary>閉鎖を<c>AllowClose</c>が真になるまで取り消すテスト用Windowです。</summary>
    private sealed class CancelingModalWindow : Window
    {
        internal bool AllowClose { get; set; }

        internal Action? OnCancel { get; set; }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (AllowClose) { return; }
            e.Cancel = true;
            OnCancel?.Invoke();
        }
    }
}
