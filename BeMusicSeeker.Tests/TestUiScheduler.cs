using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;

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

internal static class TestUiDispatcherHost
{
    private static readonly Lazy<Dispatcher> dispatcher = new(CreateDispatcher, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static Dispatcher Dispatcher => dispatcher.Value;

    internal static void Drain()
    {
        Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static Dispatcher CreateDispatcher()
    {
        Dispatcher capturedDispatcher = null!;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            capturedDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return capturedDispatcher;
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
