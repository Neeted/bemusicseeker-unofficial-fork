using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
    /// Processes queued dispatcher work through application-idle priority.
    /// </summary>
    internal static void Drain()
    {
        Dispatcher dispatcher = host.Value.Dispatcher;
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
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
            () => terminalFailure);
    }

    private sealed class HostState(
        Application application,
        Dispatcher dispatcher,
        Thread thread,
        ManualResetEventSlim completed,
        string originalAppearanceTheme,
        Func<Exception?> terminalFailureProvider)
    {
        internal Application Application { get; } = application;

        internal Dispatcher Dispatcher { get; } = dispatcher;

        internal Thread Thread { get; } = thread;

        internal ManualResetEventSlim Completed { get; } = completed;

        internal string OriginalAppearanceTheme { get; } = originalAppearanceTheme;

        internal Exception? TerminalFailure => terminalFailureProvider();
    }
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
