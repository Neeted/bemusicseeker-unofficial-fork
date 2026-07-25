using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Keeps headless tests deterministic without changing the production WPF scheduler contract.
/// </summary>
internal sealed class TestUiScheduler : IUiScheduler
{
    private readonly Dispatcher dispatcher;

    internal TestUiScheduler(Func<Dispatcher> dispatcherProvider)
    {
        dispatcher = (dispatcherProvider ?? throw new ArgumentNullException(nameof(dispatcherProvider)))();
    }

    public Dispatcher Dispatcher => dispatcher;

    public bool CheckAccess() => Dispatcher?.CheckAccess() == true;
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

    public void RestartApplication()
    {
    }
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
