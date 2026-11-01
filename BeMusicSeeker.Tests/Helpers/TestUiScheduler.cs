using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    /// <summary>Dispatcherがない場合は本番アダプターのインライン実行契約で動くテスト窓口を作ります。</summary>
    internal TestUiScheduler(Func<Dispatcher?> dispatcherProvider)
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

    public void ExitForUnrestoredDestination(System.Exception failure)
        => throw new System.InvalidOperationException("Unexpected unrestored destination exit.", failure);

    /// <summary>テストが所有する一時入力は各fixtureで回収し、アプリ全体の回収を行いません。</summary>
    public Task CompleteTemporaryDirectoriesAsync(Action<string, Exception> logWarning) => Task.CompletedTask;

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
