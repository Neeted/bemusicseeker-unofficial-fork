using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Keeps headless tests deterministic without changing the production WPF scheduler contract.
/// </summary>
internal sealed class TestUiScheduler : IUiScheduler
{
    private readonly WpfUiScheduler scheduler;

    internal TestUiScheduler(Func<Dispatcher> dispatcherProvider)
    {
        scheduler = new WpfUiScheduler(dispatcherProvider ?? throw new ArgumentNullException(nameof(dispatcherProvider)));
    }

    public bool IsAvailable => scheduler.IsAvailable;

    public bool CanExecuteInline => scheduler.CanExecuteInline;

    public bool CheckAccess() => scheduler.CheckAccess();

    public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        => scheduler.Schedule(action, priority);

    public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        => scheduler.Invoke(action, priority);

    public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        => scheduler.Invoke(action, priority);

    public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        => scheduler.InvokeAsync(action, priority);

    public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        => scheduler.InvokeAsync(action, priority);
}

/// <summary>
/// Serializes test ownership of the process-global operating-system cursor.
/// </summary>
internal sealed class TestProcessGlobalCursorScope : IDisposable
{
    private static readonly object syncRoot = new();

    private bool entered;

    private TestProcessGlobalCursorScope()
    {
        Monitor.Enter(syncRoot);
        entered = true;
    }

    /// <summary>
    /// Acquires exclusive cursor ownership until the returned scope is disposed.
    /// </summary>
    /// <returns>The exclusive process-global cursor scope.</returns>
    internal static TestProcessGlobalCursorScope Enter() => new();

    /// <summary>
    /// Releases cursor ownership exactly once.
    /// </summary>
    public void Dispose()
    {
        if (!entered)
        {
            return;
        }
        entered = false;
        Monitor.Exit(syncRoot);
    }
}

/// <summary>
/// Owns the test assembly's single WPF application and its dedicated STA dispatcher.
/// </summary>
internal static class TestUiDispatcherHost
{
    private static readonly Lazy<HostState> host = new(
        CreateHost,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the dispatcher owned by the shared WPF test application.
    /// </summary>
    internal static Dispatcher Dispatcher
        => host.Value.Dispatcher;

    /// <summary>
    /// Runs an action synchronously on the shared WPF application dispatcher.
    /// </summary>
    internal static void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        host.Value.Dispatcher.Invoke(action);
    }

    /// <summary>
    /// Runs a window presentation test on the shared dispatcher and enforces deterministic presentation cleanup.
    /// </summary>
    internal static void RunWindowTest(Action<TestWindowPresentationScope> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        HostState state = host.Value;
        state.Dispatcher.Invoke(() =>
        {
            var scope = new TestWindowPresentationScope(state.Application, state.NativeThreadId);
            ExceptionDispatchInfo? bodyFailure = null;
            ExceptionDispatchInfo? presentationFailure;
            Exception? cleanupFailure = null;
            try
            {
                action(scope);
            }
            catch (Exception ex)
            {
                bodyFailure = ExceptionDispatchInfo.Capture(ex);
            }

            try
            {
                scope.Cleanup();
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }

            presentationFailure = scope.GetPresentationFailure();

            if (bodyFailure != null)
            {
                if (presentationFailure != null)
                {
                    bodyFailure.SourceException.Data["TestWindowPresentationFailure"] =
                        presentationFailure.SourceException.ToString();
                }
                if (cleanupFailure != null)
                {
                    bodyFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] = cleanupFailure.ToString();
                }

                bodyFailure.Throw();
            }

            if (presentationFailure != null)
            {
                if (cleanupFailure != null)
                {
                    presentationFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] =
                        cleanupFailure.ToString();
                }

                presentationFailure.Throw();
            }

            if (cleanupFailure != null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        });
    }

    /// <summary>
    /// Processes queued dispatcher work through application-idle priority.
    /// </summary>
    internal static void Drain()
    {
        Dispatcher dispatcher = host.Value.Dispatcher;
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    /// <summary>
    /// Pumps the host dispatcher until an explicit asynchronous outcome completes.
    /// </summary>
    /// <param name="task">The task that represents the outcome under test.</param>
    /// <param name="operationName">A diagnostic name used when the outcome does not complete.</param>
    /// <remarks>
    /// The watchdog only detects a missing outcome; it is not part of the normal completion path.
    /// </remarks>
    internal static void AwaitTaskOnDispatcher(Task task, string operationName)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        Dispatcher dispatcher = host.Value.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "AwaitTaskOnDispatcher must be called from the test application dispatcher.");
        }

        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        bool timedOut = false;
        ExceptionDispatchInfo? dispatchFailure = null;
        var watchdog = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Send,
            (_, _) =>
            {
                timedOut = true;
                frame.Continue = false;
            },
            dispatcher);

        _ = task.ContinueWith(
            _ =>
            {
                try
                {
                    dispatcher.BeginInvoke(
                        DispatcherPriority.Send,
                        new Action(() => frame.Continue = false));
                }
                catch (Exception ex)
                {
                    dispatchFailure = ExceptionDispatchInfo.Capture(ex);
                    frame.Continue = false;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        watchdog.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            watchdog.Stop();
        }

        if (dispatchFailure != null)
        {
            dispatchFailure.Throw();
        }

        if (timedOut && !task.IsCompleted)
        {
            throw new TimeoutException(
                $"The dispatcher outcome '{operationName}' did not complete within 5 seconds. "
                + $"Task status: {task.Status}.");
        }

        task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Restores process-local test settings and deterministically stops the WPF application thread.
    /// </summary>
    internal static void ShutdownApplication()
    {
        if (!host.IsValueCreated)
        {
            return;
        }

        HostState state = host.Value;
        Exception? cleanupFailure = null;

        try
        {
            state.Dispatcher.Invoke(() =>
            {
                try
                {
                    Settings.Default.AppearanceTheme = state.OriginalAppearanceTheme;
                    AppThemeService.ApplyTheme(state.OriginalAppearanceTheme);
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                }
                finally
                {
                    try
                    {
                        state.Application.Shutdown();
                    }
                    finally
                    {
                        if (!state.Dispatcher.HasShutdownStarted)
                        {
                            state.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                        }
                    }
                }
            });
        }
        catch (Exception ex)
        {
            cleanupFailure ??= ex;
        }

        state.Completed.Wait();
        state.Thread.Join();
        cleanupFailure ??= state.TerminalFailure;
        if (cleanupFailure != null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private static HostState CreateHost()
    {
        using var ready = new ManualResetEventSlim();
        var completed = new ManualResetEventSlim();
        Application? application = null;
        Dispatcher? dispatcher = null;
        string? originalAppearanceTheme = null;
        uint nativeThreadId = 0;
        Exception? terminalFailure = null;
        var applicationThread = new Thread(() =>
        {
            try
            {
                if (Application.Current != null)
                {
                    throw new InvalidOperationException(
                        "A conflicting WPF Application already exists before the shared test host starts.");
                }

                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                dispatcher = Dispatcher.CurrentDispatcher;
                nativeThreadId = TestWindowPresentationScope.GetCurrentNativeThreadId();
                originalAppearanceTheme = Settings.Default.AppearanceTheme;
                Settings.Default.AppearanceTheme = AppThemeService.Light;
                AppThemeService.ApplyTheme(AppThemeService.Light);
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                terminalFailure = ex;
                ready.Set();
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = "BeMusicSeeker.Tests shared WPF application"
        };
        applicationThread.SetApartmentState(ApartmentState.STA);
        applicationThread.Start();
        ready.Wait();

        if (terminalFailure != null)
        {
            applicationThread.Join();
            ExceptionDispatchInfo.Capture(terminalFailure).Throw();
        }

        return new HostState(
            application!,
            dispatcher!,
            applicationThread,
            completed,
            originalAppearanceTheme!,
            nativeThreadId,
            () => terminalFailure);
    }

    private sealed class HostState(
        Application application,
        Dispatcher dispatcher,
        Thread thread,
        ManualResetEventSlim completed,
        string originalAppearanceTheme,
        uint nativeThreadId,
        Func<Exception?> terminalFailureProvider)
    {
        internal Application Application { get; } = application;

        internal Dispatcher Dispatcher { get; } = dispatcher;

        internal Thread Thread { get; } = thread;

        internal ManualResetEventSlim Completed { get; } = completed;

        internal string OriginalAppearanceTheme { get; } = originalAppearanceTheme;

        internal uint NativeThreadId { get; } = nativeThreadId;

        internal Exception? TerminalFailure => terminalFailureProvider();
    }
}

/// <summary>
/// Selects whether a real WPF test window may participate in foreground interaction.
/// </summary>
internal enum TestWindowActivation
{
    /// <summary>
    /// Presents the window outside all current monitors without activating it.
    /// </summary>
    NonActivating,

    /// <summary>
    /// Allows the test to interact with the window through the foreground input path.
    /// </summary>
    ForegroundInteraction
}

/// <summary>
/// Owns real windows and popups created by one test on the shared WPF dispatcher.
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
    private const long WsExNoActivate = 0x08000000L;
    private readonly Application application;
    private readonly uint nativeThreadId;
    private readonly HashSet<Window> baselineWindows;
    private readonly HashSet<nint> baselineNativeWindows;
    private readonly List<WindowRegistration> trackedWindows = [];
    private readonly List<PopupRegistration> trackedPopups = [];
    private readonly List<ExceptionDispatchInfo> presentationFailures = [];
    private Exception? registeredCleanupFailure;

    internal TestWindowPresentationScope(Application application, uint nativeThreadId)
    {
        this.application = application;
        this.nativeThreadId = nativeThreadId;
        baselineWindows = application.Windows.Cast<Window>().ToHashSet();
        baselineNativeWindows = EnumerateNativeWindows(nativeThreadId).ToHashSet();
    }

    /// <summary>
    /// Applies the selected presentation policy, shows the window, and waits for a rendered HWND-backed layout.
    /// </summary>
    internal void ShowAndWaitForContentRendered(
        Window window,
        TestWindowActivation activation = TestWindowActivation.NonActivating)
    {
        ArgumentNullException.ThrowIfNull(window);
        PrepareForOwnedPresentation(window, activation);

        bool contentRendered = false;
        EventHandler handler = (_, _) => contentRendered = true;
        window.ContentRendered += handler;
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpUntil(window.Dispatcher, () => contentRendered || !window.IsVisible);

            if (!contentRendered)
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

            VerifyPresentationPolicy(handle, activation);
        }
        finally
        {
            window.ContentRendered -= handler;
        }
    }

    /// <summary>
    /// Tracks a window and applies its presentation policy immediately before an owned or modal presentation.
    /// </summary>
    internal void PrepareForOwnedPresentation(
        Window window,
        TestWindowActivation activation = TestWindowActivation.NonActivating)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (trackedWindows.Any(registration => ReferenceEquals(registration.Window, window)))
        {
            throw new InvalidOperationException("The window is already prepared by this presentation scope.");
        }

        if (activation == TestWindowActivation.NonActivating)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2048d;
            window.Top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 2048d;
        }

        var registration = new WindowRegistration(window, activation);
        registration.SourceInitialized = (_, _) =>
        {
            registration.Handle = new WindowInteropHelper(window).Handle;
            if (activation == TestWindowActivation.NonActivating)
            {
                EnsureNonActivatingStyle(registration.Handle, window.GetType().Name);
            }
        };
        registration.Loaded = (_, _) => ObservePresentation(registration);
        registration.Closing = (_, _) => ObservePresentation(registration);
        window.SourceInitialized += registration.SourceInitialized;
        window.Loaded += registration.Loaded;
        window.Closing += registration.Closing;
        trackedWindows.Add(registration);
    }

    /// <summary>
    /// Tracks a popup, applies its placement target's activation policy when opened, and closes it before its owning window.
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

        var popupRegistration = new PopupRegistration(popup);
        popupRegistration.Opened = (_, _) => ObservePopupPresentation(popupRegistration);
        popup.Opened += popupRegistration.Opened;
        trackedPopups.Add(popupRegistration);
    }

    /// <summary>
    /// Registers a deterministic cleanup failure for the host's own failure-composition tests.
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
    /// Registers a deterministic presentation-observation failure for the host's failure-composition tests.
    /// </summary>
    internal void RegisterPresentationFailureForTesting(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        presentationFailures.Add(ExceptionDispatchInfo.Capture(failure));
    }

    /// <summary>
    /// Gets the HWND for a presentation source, or zero when no HWND exists.
    /// </summary>
    internal static nint GetNativeHandle(Visual visual)
        => (PresentationSource.FromVisual(visual) as HwndSource)?.Handle ?? 0;

    /// <summary>
    /// Returns whether a native window still exists.
    /// </summary>
    internal static bool NativeWindowExists(nint handle)
        => handle != 0 && IsWindow(handle);

    /// <summary>
    /// Returns whether a window rectangle lies outside every current monitor.
    /// </summary>
    internal static bool IsOutsideAllMonitors(nint handle)
        => handle != 0 && MonitorFromWindow(handle, MonitorDefaultToNull) == 0;

    /// <summary>
    /// Gets the current foreground HWND.
    /// </summary>
    internal static nint ForegroundWindow => GetForegroundWindow();

    /// <summary>
    /// Reads the native extended style and reports whether a presentation HWND has persistent non-activation.
    /// </summary>
    /// <param name="handle">The HWND to inspect.</param>
    /// <returns><see langword="true"/> when the native style contains <c>WS_EX_NOACTIVATE</c>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the HWND or native style read is unavailable.</exception>
    internal static bool HasNoActivateStyle(nint handle)
        => (ReadExtendedWindowStyle(handle, "non-activating test") & WsExNoActivate) != 0;

    internal static uint GetCurrentNativeThreadId() => GetCurrentThreadId();

    internal void Cleanup()
    {
        List<Exception> failures = [];
        foreach (PopupRegistration registration in trackedPopups.AsEnumerable().Reverse())
        {
            try
            {
                registration.Popup.IsOpen = false;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        // Complete popup teardown before closing its placement target or any owned window.
        try
        {
            DrainDispatcher(application.Dispatcher);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        finally
        {
            foreach (PopupRegistration registration in trackedPopups)
            {
                registration.Popup.Opened -= registration.Opened;
            }
        }

        Window[] createdWindows = application.Windows.Cast<Window>()
            .Where(window => !baselineWindows.Contains(window))
            .ToArray();
        Window[] cleanupWindows = trackedWindows.Select(registration => registration.Window)
            .Concat(createdWindows)
            .Distinct()
            .Select((window, index) => new { Window = window, Index = index, Depth = GetOwnerDepth(window) })
            .OrderByDescending(item => item.Depth)
            .ThenByDescending(item => item.Index)
            .Select(item => item.Window)
            .ToArray();

        foreach (Window window in cleanupWindows)
        {
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
            }
            catch (InvalidOperationException) when (!window.IsLoaded && new WindowInteropHelper(window).Handle == 0)
            {
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        foreach (WindowRegistration registration in trackedWindows)
        {
            registration.Window.SourceInitialized -= registration.SourceInitialized;
            registration.Window.Loaded -= registration.Loaded;
            registration.Window.Closing -= registration.Closing;
        }

        DrainDispatcher(application.Dispatcher);

        nint[] leakedHandles = EnumerateNativeWindows(nativeThreadId)
            .Where(handle => !baselineNativeWindows.Contains(handle))
            .ToArray();
        foreach (nint handle in leakedHandles)
        {
            if (IsWindow(handle) && !DestroyWindow(handle))
            {
                failures.Add(new InvalidOperationException($"Failed to destroy leaked test HWND 0x{handle:X}."));
            }
        }

        DrainDispatcher(application.Dispatcher);
        Window[] leakedWindows = application.Windows.Cast<Window>()
            .Where(window => !baselineWindows.Contains(window))
            .ToArray();
        nint[] remainingHandles = EnumerateNativeWindows(nativeThreadId)
            .Where(handle => !baselineNativeWindows.Contains(handle))
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

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> completed)
    {
        for (int barrier = 0; barrier < 8 && !completed(); barrier++)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void DrainDispatcher(Dispatcher dispatcher)
    {
        dispatcher.Invoke(DispatcherPriority.Input, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static void VerifyPresentationPolicy(nint handle, TestWindowActivation activation)
    {
        if (activation != TestWindowActivation.NonActivating)
        {
            return;
        }

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

        VerifyPresentationPolicy(handle, TestWindowActivation.NonActivating);
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
            if (ownerRegistration.Activation != TestWindowActivation.NonActivating)
            {
                return;
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
        if (registration.ObservationCompleted
            || registration.Activation != TestWindowActivation.NonActivating)
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
    /// Gets the first presentation-observation failure and retains any later failures as diagnostics.
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

    private static int GetOwnerDepth(Window window)
    {
        int depth = 0;
        for (Window? owner = window.Owner; owner != null; owner = owner.Owner)
        {
            depth++;
        }

        return depth;
    }

    private static IReadOnlyList<nint> EnumerateNativeWindows(uint threadId)
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

    private sealed class WindowRegistration(Window window, TestWindowActivation activation)
    {
        internal Window Window { get; } = window;

        internal TestWindowActivation Activation { get; } = activation;

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

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

internal static class TestApplicationContext
{
    internal static IApplicationLifetimePort CreateLifetime(bool firstStartup = false)
        => new TestApplicationLifetime(firstStartup);

    internal static ICultureCatalog CreateCultureCatalog()
        => new TestCultureCatalog();
}

internal sealed class TestApplicationLifetime : IApplicationLifetimePort
{
    private bool firstStartup;

    internal TestApplicationLifetime(bool firstStartup = false)
    {
        this.firstStartup = firstStartup;
    }

    public bool IsFirstStartup => firstStartup;

    public void CompleteFirstStartup() => firstStartup = false;

    public void MarkCoordinatedShutdownStarted(string reason)
    {
    }

    public void RequestShutdown()
    {
    }

    public Task RestartApplicationAsync() => Task.CompletedTask;
}

internal sealed class TestCultureCatalog : ICultureCatalog
{
    internal TestCultureCatalog(IReadOnlyDictionary<string, string>? cultures = null)
    {
        Cultures = cultures
            ?? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
            {
                ["Default(日本語)"] = "ja-JP",
                ["English"] = "en-US"
            });
    }

    public IReadOnlyDictionary<string, string> Cultures { get; }
}
