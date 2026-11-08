using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>
/// 共有WPF Dispatcher上で一つのテストが作る実Window・Popupとその表示・回収を所有します。
/// </summary>
internal sealed class TestWindowPresentationScope
{
    private const uint MonitorDefaultToNull = 0;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const long WsExNoActivate = 0x08000000L;
    // どの表示範囲でも準備を通したWindowの印です。未準備のWindowの表示記録（HostObservationLog）が参照し、弱参照で保持します。
    private static readonly ConditionalWeakTable<Window, object> preparedWindows = new();
    private readonly Application application;
    private readonly uint nativeThreadId;
    private readonly HashSet<Window> baselineWindows;
    private readonly HashSet<nint> baselineNativeWindows;
    private readonly List<WindowRegistration> trackedWindows = [];
    private readonly List<PopupRegistration> trackedPopups = [];
    private readonly List<(ToolTip ToolTip, RoutedEventHandler Opened)> trackedToolTips = [];
    private readonly List<ExceptionDispatchInfo> presentationFailures = [];
    private Exception? registeredCleanupFailure;

    /// <summary>開始時点のWindow・native HWNDを除外基準として、一回の表示と回収を所有します。</summary>
    internal TestWindowPresentationScope(Application application, uint nativeThreadId)
    {
        this.application = application;
        this.nativeThreadId = nativeThreadId;
        baselineWindows = application.Windows.Cast<Window>().ToHashSet();
        baselineNativeWindows = EnumerateNativeWindows(nativeThreadId).ToHashSet();
    }

    /// <summary>
    /// 非アクティブ・画面外に表示し、HWNDを持つ画面の実際の描画完了通知を待ちます。
    /// アニメーション中にも発生する描画完了を、Dispatcher全体のidle到達から推測しません。
    /// </summary>
    /// <param name="window">このscopeが表示と回収を所有するWindow。</param>
    /// <param name="renderNotificationTimeout">Loaded後の描画通知だけに適用する期限。省略時は通常の表示観測期限を使います。</param>
    internal void ShowAndWaitForContentRendered(
        Window window, TimeSpan? renderNotificationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (renderNotificationTimeout is { } timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        }
        PrepareForOwnedPresentation(window);

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler loadedHandler = (_, _) => loaded.TrySetResult();
        var presentationCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => presentationCompleted.TrySetResult(true);
        DependencyPropertyChangedEventHandler visibilityChanged = (_, _) =>
        {
            if (!window.IsVisible)
            {
                loaded.TrySetResult();
                presentationCompleted.TrySetResult(false);
            }
        };
        window.Loaded += loadedHandler;
        window.ContentRendered += handler;
        window.IsVisibleChanged += visibilityChanged;
        try
        {
            window.Show();
            window.UpdateLayout();
            if (!window.IsVisible)
            {
                loaded.TrySetResult();
                presentationCompleted.TrySetResult(false);
            }
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(loaded.Task, $"{window.GetType().Name}.Loaded");
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(
                presentationCompleted.Task,
                $"{window.GetType().Name}.ContentRendered", renderNotificationTimeout);

            if (!presentationCompleted.Task.GetAwaiter().GetResult())
            {
                throw new InvalidOperationException(
                    $"The displayed {window.GetType().Name} did not reach ContentRendered.");
            }

            nint handle = new WindowInteropHelper(window).Handle;
            if (!window.IsLoaded || handle == 0 || window.ActualWidth <= 0 || window.ActualHeight <= 0)
            {
                throw new InvalidOperationException(
                    $"The displayed {window.GetType().Name} did not produce a loaded, non-empty HWND-backed layout.");
            }

            VerifyPresentationPolicy(handle);
        }
        finally
        {
            window.Loaded -= loadedHandler;
            window.ContentRendered -= handler;
            window.IsVisibleChanged -= visibilityChanged;
        }
    }

    /// <summary>
    /// 所有ウィンドウまたはモーダルの表示前に、非アクティブ・画面外表示と破棄の管理を登録します。
    /// </summary>
    internal void PrepareForOwnedPresentation(
        Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (trackedWindows.Any(registration => ReferenceEquals(registration.Window, window)))
        {
            throw new InvalidOperationException("The window is already prepared by this presentation scope.");
        }

        preparedWindows.AddOrUpdate(window, preparedWindows);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2048d;
        window.Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 2048d;

        var registration = new WindowRegistration(window);
        registration.SourceInitialized = (_, _) =>
        {
            registration.Handle = new WindowInteropHelper(window).Handle;
            EnsureNonActivatingStyle(registration.Handle, window.GetType().Name);
        };
        registration.Loaded = (_, _) => ObservePresentation(registration);
        registration.Closing = (_, _) => ObservePresentation(registration);
        window.SourceInitialized += registration.SourceInitialized;
        window.Loaded += registration.Loaded;
        window.Closing += registration.Closing;
        trackedWindows.Add(registration);
    }

    /// <summary>指定のWindowが、いずれかの表示範囲で <see cref="PrepareForOwnedPresentation"/> を通したかを返します。</summary>
    internal static bool IsPrepared(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return preparedWindows.TryGetValue(window, out _);
    }

    /// <summary>
    /// Popupを非アクティブ・画面外に配置し、所有ウィンドウより先に閉じます。
    /// </summary>
    internal void TrackPopup(Popup popup)
    {
        ArgumentNullException.ThrowIfNull(popup);
        if (trackedPopups.Any(registration => ReferenceEquals(registration.Popup, popup)))
        {
            return;
        }

        if (popup.IsOpen)
        {
            throw new InvalidOperationException("A popup must be tracked before it is opened.");
        }

        RegisterPopup(popup);
    }

    /// <summary>ToolTipが自身で生成するPopupを公開の論理親から取得し、表示位置・非アクティブ表示と閉鎖を同じscopeで管理します。</summary>
    /// <param name="toolTip">開く前のToolTip。内部Popupの生成・接続はWPFへ任せます。</param>
    internal void TrackToolTip(ToolTip toolTip)
    {
        ArgumentNullException.ThrowIfNull(toolTip);
        if (trackedToolTips.Any(registration => ReferenceEquals(registration.ToolTip, toolTip))) { return; }
        if (toolTip.IsOpen) { throw new InvalidOperationException("A tooltip must be tracked before it is opened."); }
        RoutedEventHandler opened = (_, _) =>
        {
            try
            {
                if (toolTip.Parent is not Popup popup)
                {
                    throw new InvalidOperationException("The opened tooltip has no logical Popup parent.");
                }
                if (trackedPopups.Any(registration => ReferenceEquals(registration.Popup, popup))) { return; }
                // 初回のPopup.Openedは既に配送中なので、その一回だけここで同じ観測を行います。
                ObservePopupPresentation(RegisterPopup(popup));
            }
            catch (Exception failure) { presentationFailures.Add(ExceptionDispatchInfo.Capture(failure)); }
        };
        toolTip.Opened += opened;
        trackedToolTips.Add((toolTip, opened));
    }

    private PopupRegistration RegisterPopup(Popup popup)
    {
        var popupRegistration = new PopupRegistration(popup);
        popupRegistration.Opened = (_, _) => ObservePopupPresentation(popupRegistration);
        popup.Opened += popupRegistration.Opened;
        trackedPopups.Add(popupRegistration);
        return popupRegistration;
    }

    /// <summary>
    /// 共通host自身の失敗優先順位検査で、確実に発生させる回収失敗を登録します。
    /// </summary>
    internal void RegisterCleanupFailureForTesting(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (registeredCleanupFailure != null)
        {
            throw new InvalidOperationException("A cleanup failure is already registered for this scope.");
        }

        registeredCleanupFailure = failure;
    }

    /// <summary>
    /// 共通host自身の失敗優先順位検査で、確実に発生させる表示観測失敗を登録します。
    /// </summary>
    internal void RegisterPresentationFailureForTesting(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        presentationFailures.Add(ExceptionDispatchInfo.Capture(failure));
    }

    /// <summary>
    /// 表示元のHWNDを取得し、未生成ならゼロを返します。
    /// </summary>
    internal static nint GetNativeHandle(Visual visual)
        => (PresentationSource.FromVisual(visual) as HwndSource)?.Handle ?? 0;

    /// <summary>
    /// 指定HWNDのnative Windowが現在も存在するかを返します。
    /// </summary>
    internal static bool NativeWindowExists(nint handle)
        => handle != 0 && IsWindow(handle);

    /// <summary>
    /// 指定HWNDの矩形が全monitorの外にあるかを返します。
    /// </summary>
    internal static bool IsOutsideAllMonitors(nint handle)
        => handle != 0 && MonitorFromWindow(handle, MonitorDefaultToNull) == 0;

    /// <summary>
    /// 現在の前面HWNDを取得します。
    /// </summary>
    internal static nint ForegroundWindow => GetForegroundWindow();

    /// <summary>
    /// native拡張styleから、表示HWNDの非活性styleが維持されているかを返します。
    /// </summary>
    /// <param name="handle">検査するHWND。</param>
    /// <returns>native styleが<c>WS_EX_NOACTIVATE</c>を含む場合にtrue。</returns>
    /// <exception cref="InvalidOperationException">HWNDまたはnative styleの読取りが利用できない場合。</exception>
    internal static bool HasNoActivateStyle(nint handle)
        => (ReadExtendedWindowStyle(handle, "non-activating test") & WsExNoActivate) != 0;

    /// <summary>Window/HWNDを所有する現在のWin32 thread IDを取得します。</summary>
    internal static uint GetCurrentNativeThreadId() => GetCurrentThreadId();

    /// <summary>
    /// 所有するPopupとWindowの実閉鎖を待ち、購読と残留HWNDを回収します。
    /// </summary>
    /// <remarks>
    /// 開始済みの機能処理Taskは呼出側が回収します。共有Dispatcher全体のidleは
    /// 閉鎖や処理終端の証明にせず、閉鎖通知とWindow/HWNDの残留を確認します。
    /// </remarks>
    internal void Cleanup()
    {
        List<Exception> failures = [];
        foreach ((ToolTip toolTip, RoutedEventHandler opened) in trackedToolTips)
        {
            toolTip.Opened -= opened;
            try { toolTip.IsOpen = false; }
            catch (Exception failure) { failures.Add(failure); }
        }
        foreach (PopupRegistration registration in trackedPopups.AsEnumerable().Reverse())
        {
            Popup popup = registration.Popup;
            nint handle = popup.Child is Visual child ? GetNativeHandle(child) : 0;
            bool waitForClosed = NativeWindowExists(handle);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler onClosed = (_, _) => closed.TrySetResult();
            popup.Closed += onClosed;
            try
            {
                popup.IsOpen = false;
                if (waitForClosed)
                {
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "presentation-cleanup.popup-closed");
                }
                if (popup.IsOpen || NativeWindowExists(handle))
                {
                    throw new InvalidOperationException("The tracked popup did not finish closing.");
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            finally
            {
                popup.Closed -= onClosed;
                popup.Opened -= registration.Opened;
            }
        }

        Window[] createdWindows = application.Windows.Cast<Window>()
            .Where(window => !baselineWindows.Contains(window))
            .ToArray();
        Window[] cleanupWindows = OrderForClosing(trackedWindows.Select(registration => registration.Window).Concat(createdWindows));

        foreach (Window window in cleanupWindows)
        {
            bool waitForClosed = application.Windows.Cast<Window>().Contains(window);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler onClosed = (_, _) => closed.TrySetResult();
            window.Closed += onClosed;
            try
            {
                if (window is BeMusicSeeker.Views.SettingsWindow settingsWindow)
                {
                    settingsWindow.CloseForOwnerShutdown();
                }
                else
                {
                    window.Close();
                }
                if (waitForClosed)
                {
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "presentation-cleanup.window-closed");
                }
            }
            catch (InvalidOperationException) when (!waitForClosed && !window.IsLoaded && new WindowInteropHelper(window).Handle == 0)
            {
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
            finally
            {
                window.Closed -= onClosed;
            }
        }

        foreach (WindowRegistration registration in trackedWindows)
        {
            registration.Window.SourceInitialized -= registration.SourceInitialized;
            registration.Window.Loaded -= registration.Loaded;
            registration.Window.Closing -= registration.Closing;
        }

        TestUiDispatcherHost.ProcessQueuedPresentation();

        // 開始時に未開だった既存親の後発HWNDは、その親を所有する外scopeへ帰属します。
        // 明示追跡した対象は従来どおり当scopeが閉じ、新規/未所属nativeの検査も維持します。
        var otherOwnerHandles = baselineWindows
            .Where(window => !trackedWindows.Any(registration => ReferenceEquals(registration.Window, window)))
            .Select(window => new WindowInteropHelper(window).Handle)
            .Where(handle => handle != 0)
            .ToHashSet();
        // ShowInTaskbar=falseの生存親には、Show後に生成されたWPF native ownerがあります。
        // そのownerの破棄も親を連鎖閉鎖するため、実所有関係だけを有限に辿って保護します。
        foreach (nint windowHandle in otherOwnerHandles.ToArray())
        {
            for (nint owner = GetWindow(windowHandle, GwOwner);
                owner != 0 && otherOwnerHandles.Add(owner);
                owner = GetWindow(owner, GwOwner))
            {
            }
        }
        nint[] leakedHandles = EnumerateNativeWindows(nativeThreadId)
            .Where(handle => !baselineNativeWindows.Contains(handle) && !otherOwnerHandles.Contains(handle))
            .ToArray();
        foreach (nint handle in leakedHandles)
        {
            if (IsWindow(handle) && !DestroyWindow(handle))
            {
                failures.Add(new InvalidOperationException($"Failed to destroy leaked test HWND 0x{handle:X}."));
            }
        }

        TestUiDispatcherHost.ProcessQueuedPresentation();
        Window[] leakedWindows = application.Windows.Cast<Window>()
            .Where(window => !baselineWindows.Contains(window))
            .ToArray();
        nint[] remainingHandles = EnumerateNativeWindows(nativeThreadId)
            .Where(handle => !baselineNativeWindows.Contains(handle) && !otherOwnerHandles.Contains(handle))
            .ToArray();
        if (leakedWindows.Length != 0 || remainingHandles.Length != 0)
        {
            failures.Add(new InvalidOperationException(
                $"Window test leaked {leakedWindows.Length} managed window(s) and {remainingHandles.Length} dispatcher-thread HWND(s)."));
        }

        if (registeredCleanupFailure != null)
        {
            failures.Add(registeredCleanupFailure);
        }

        if (failures.Count != 0)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("Window presentation cleanup failed.", failures);
        }
    }

    private static void VerifyPresentationPolicy(nint handle)
    {
        long extendedStyle = ReadExtendedWindowStyle(handle, "non-activating test");
        if ((extendedStyle & WsExNoActivate) == 0)
        {
            throw new InvalidOperationException(
                $"Non-activating test HWND 0x{handle:X} does not have WS_EX_NOACTIVATE in its native extended style.");
        }

        if (!IsOutsideAllMonitors(handle))
        {
            throw new InvalidOperationException($"Non-activating test HWND 0x{handle:X} intersects a monitor.");
        }

        if (GetForegroundWindow() == handle)
        {
            throw new InvalidOperationException($"Non-activating test HWND 0x{handle:X} became the foreground window.");
        }
    }

    private static void ApplyAndVerifyNonActivatingPosition(Window window, nint handle)
    {
        int left = GetSystemMetrics(SmXVirtualScreen) + GetSystemMetrics(SmCxVirtualScreen) + 2048;
        int top = GetSystemMetrics(SmYVirtualScreen) + GetSystemMetrics(SmCyVirtualScreen) + 2048;
        window.Left = left;
        window.Top = top;
        ApplyAndVerifyNonActivatingPosition(handle, window.GetType().Name, left, top);
    }

    private static void EnsureNonActivatingStyle(nint handle, string presentationName)
    {
        long existingStyle = ReadExtendedWindowStyle(handle, presentationName);
        long requiredStyle = existingStyle | WsExNoActivate;
        if (requiredStyle != existingStyle)
        {
            Marshal.SetLastPInvokeError(0);
            nint previousStyle = SetWindowLongPtr(handle, GwlExStyle, (nint)requiredStyle);
            int error = Marshal.GetLastPInvokeError();
            if (previousStyle == 0 && error != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to add WS_EX_NOACTIVATE to non-activating {presentationName} HWND 0x{handle:X}; Win32 error {error}.");
            }
        }

        long observedStyle = ReadExtendedWindowStyle(handle, presentationName);
        if ((observedStyle & existingStyle) != existingStyle)
        {
            throw new InvalidOperationException(
                $"Adding WS_EX_NOACTIVATE changed existing extended styles on non-activating {presentationName} HWND 0x{handle:X}.");
        }

        if ((observedStyle & WsExNoActivate) == 0)
        {
            throw new InvalidOperationException(
                $"Non-activating {presentationName} HWND 0x{handle:X} did not retain WS_EX_NOACTIVATE after native style update.");
        }
    }

    private static long ReadExtendedWindowStyle(nint handle, string presentationName)
    {
        if (!IsWindow(handle))
        {
            throw new InvalidOperationException(
                $"The non-activating {presentationName} presentation has no valid HWND for native style verification.");
        }

        Marshal.SetLastPInvokeError(0);
        nint style = GetWindowLongPtr(handle, GwlExStyle);
        int error = Marshal.GetLastPInvokeError();
        if (style == 0 && error != 0)
        {
            throw new InvalidOperationException(
                $"Failed to read the extended style for non-activating {presentationName} HWND 0x{handle:X}; Win32 error {error}.");
        }

        return style.ToInt64();
    }

    private static void ApplyAndVerifyNonActivatingPosition(
        nint handle,
        string presentationName,
        int left,
        int top)
    {
        if (!IsWindow(handle))
        {
            throw new InvalidOperationException(
                $"The non-activating {presentationName} presentation lost its HWND before policy verification.");
        }

        EnsureNonActivatingStyle(handle, presentationName);

        if (!SetWindowPos(
            handle,
            0,
            left,
            top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate))
        {
            throw new InvalidOperationException(
                $"Failed to position non-activating test HWND 0x{handle:X} outside the virtual screen; Win32 error {Marshal.GetLastWin32Error()}.");
        }

        VerifyPresentationPolicy(handle);
    }

    private void ObservePopupPresentation(PopupRegistration registration)
    {
        try
        {
            DependencyObject? placementTarget = registration.Popup.PlacementTarget
                ?? registration.Popup.TemplatedParent;
            Window? placementWindow = placementTarget == null
                ? null
                : Window.GetWindow(placementTarget);
            WindowRegistration? ownerRegistration = trackedWindows.LastOrDefault(
                candidate => ReferenceEquals(candidate.Window, placementWindow));
            if (ownerRegistration == null)
            {
                throw new InvalidOperationException(
                    "An opened popup must have a placement target in a window prepared by this presentation scope.");
            }

            nint handle = registration.Popup.Child == null
                ? 0
                : GetNativeHandle(registration.Popup.Child);
            int left = GetSystemMetrics(SmXVirtualScreen) + GetSystemMetrics(SmCxVirtualScreen) + 2048;
            int top = GetSystemMetrics(SmYVirtualScreen) + GetSystemMetrics(SmCyVirtualScreen) + 2048;
            ApplyAndVerifyNonActivatingPosition(handle, nameof(Popup), left, top);
        }
        catch (Exception ex)
        {
            presentationFailures.Add(ExceptionDispatchInfo.Capture(ex));
        }
    }

    private void ObservePresentation(WindowRegistration registration)
    {
        if (registration.ObservationCompleted)
        {
            return;
        }

        registration.ObservationCompleted = true;
        try
        {
            nint handle = registration.Handle;
            if (handle == 0)
            {
                handle = new WindowInteropHelper(registration.Window).Handle;
                registration.Handle = handle;
            }

            ApplyAndVerifyNonActivatingPosition(registration.Window, handle);
        }
        catch (Exception ex)
        {
            presentationFailures.Add(ExceptionDispatchInfo.Capture(ex));
        }
    }

    /// <summary>
    /// 最初の表示観測失敗を取得し、後続失敗を診断として保持します。
    /// </summary>
    internal ExceptionDispatchInfo? GetPresentationFailure()
    {
        if (presentationFailures.Count == 0)
        {
            return null;
        }

        ExceptionDispatchInfo primary = presentationFailures[0];
        if (presentationFailures.Count > 1)
        {
            primary.SourceException.Data["TestWindowPresentationSecondaryFailures"] =
                new AggregateException(
                    "Additional window presentation observations failed.",
                    presentationFailures.Skip(1).Select(failure => failure.SourceException))
                .ToString();
        }

        return primary;
    }

    /// <summary>
    /// 閉じる順序を返します。所有の深いWindowを先に、同じ深さでは列の後ろ（後に作ったもの）を先にします。重複は取り除きます。
    /// </summary>
    /// <remarks>表示範囲の後片付けと、共有ホストの境界での復元が同じ順序を使います。閉鎖の完了の確かめ方は共通化しません。</remarks>
    internal static Window[] OrderForClosing(IEnumerable<Window> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows
            .Distinct()
            .Select((window, index) => new { Window = window, Index = index, Depth = GetOwnerDepth(window) })
            .OrderByDescending(item => item.Depth)
            .ThenByDescending(item => item.Index)
            .Select(item => item.Window)
            .ToArray();
    }

    private static int GetOwnerDepth(Window window)
    {
        int depth = 0;
        for (Window? owner = window.Owner; owner != null; owner = owner.Owner)
        {
            depth++;
        }

        return depth;
    }

    /// <summary>指定HWNDのnative ownerを返します。なければゼロです。</summary>
    internal static nint GetNativeOwner(nint handle) => GetWindow(handle, GwOwner);

    /// <summary>指定HWNDが表示状態（<c>WS_VISIBLE</c>が親まで有効）かを返します。</summary>
    internal static bool IsNativeWindowVisible(nint handle) => IsWindowVisible(handle);

    /// <summary>指定HWNDのnative window class名を返します。取得できなければ空文字列です。</summary>
    internal static string GetNativeClassName(nint handle)
    {
        char[] buffer = new char[256];
        int length = GetClassName(handle, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>指定threadが所有するトップレベルHWNDを列挙します。</summary>
    internal static IReadOnlyList<nint> EnumerateNativeWindows(uint threadId)
    {
        List<nint> handles = [];
        if (!EnumThreadWindows(threadId, (handle, _) =>
            {
                handles.Add(handle);
                return true;
            }, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new InvalidOperationException($"EnumThreadWindows failed with Win32 error {error}.");
            }
        }

        return handles;
    }

    private sealed class WindowRegistration(Window window)
    {
        internal Window Window { get; } = window;

        internal EventHandler SourceInitialized { get; set; } = null!;

        internal RoutedEventHandler Loaded { get; set; } = null!;

        internal CancelEventHandler Closing { get; set; } = null!;

        internal nint Handle { get; set; }

        internal bool ObservationCompleted { get; set; }
    }

    private sealed class PopupRegistration(Popup popup)
    {
        internal Popup Popup { get; } = popup;

        internal EventHandler Opened { get; set; } = null!;
    }

    private delegate bool EnumThreadWindowsCallback(nint window, nint parameter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(
        uint threadId,
        EnumThreadWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
