using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models.Localization;

namespace BeMusicSeeker.Models;

/// <summary>
/// Provides the UI scheduler needed by presentation and collection owners.
/// </summary>
internal interface IUiScheduler
{
    bool IsAvailable { get; }

    bool CanExecuteInline { get; }

    bool CheckAccess();

    IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal);

    void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal);

    T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal);

    Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal);

    Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal);
}

internal enum UiSchedulePriority
{
    Normal,
    Background,
    ContextIdle,
    ApplicationIdle,
    DataBind
}

internal interface IUiScheduledOperation
{
    bool IsAccepted { get; }

    bool IsCompleted { get; }

    bool IsAborted { get; }

    string RejectionReason { get; }

    Task Completion { get; }

    void Abort();
}

/// <summary>
/// Adapts a WPF dispatcher provider at the application composition boundary.
/// </summary>
internal sealed class WpfUiScheduler : IUiScheduler
{
    private readonly Dispatcher dispatcher;

    internal WpfUiScheduler(Func<Dispatcher> dispatcherProvider)
    {
        this.dispatcher = (dispatcherProvider
            ?? throw new ArgumentNullException(nameof(dispatcherProvider)))();
    }

    public bool IsAvailable
        => dispatcher != null
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished;

    public bool CanExecuteInline => dispatcher == null || (IsAvailable && CheckAccess());

    public bool CheckAccess() => dispatcher?.CheckAccess() == true;

    public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (dispatcher == null)
        {
            action();
            return CompletedUiScheduledOperation.Instance;
        }
        if (!IsAvailable)
        {
            return RejectedUiScheduledOperation.Instance;
        }

        try
        {
            return new WpfUiScheduledOperation(dispatcher.BeginInvoke(
                ToDispatcherPriority(priority),
                action));
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            return new RejectedUiScheduledOperation(ex.Message);
        }
    }

    public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        if (CanExecuteInline)
        {
            action();
            return;
        }

        dispatcher.Invoke(action, ToDispatcherPriority(priority));
    }

    public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        if (CanExecuteInline)
        {
            return action();
        }

        return (T)dispatcher.Invoke(action, ToDispatcherPriority(priority));
    }

    public async Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        IUiScheduledOperation operation = Schedule(action, priority);
        EnsureAccepted(operation);
        await operation.Completion.ConfigureAwait(false);
    }

    public async Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        if (CanExecuteInline)
        {
            await action().ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource<object>();
        IUiScheduledOperation operation = Schedule(() =>
        {
            Task task;
            try
            {
                task = action() ?? Task.CompletedTask;
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                return;
            }
            _ = task.ContinueWith(
                completedTask =>
                {
                    if (completedTask.IsCanceled)
                    {
                        completion.TrySetCanceled();
                    }
                    else if (completedTask.IsFaulted)
                    {
                        completion.TrySetException(completedTask.Exception.InnerExceptions);
                    }
                    else
                    {
                        completion.TrySetResult(null);
                    }
                },
                TaskScheduler.Default);
        }, priority);
        EnsureAccepted(operation);
        _ = operation.Completion.ContinueWith(
            completedOperation =>
            {
                if (operation.IsAborted)
                {
                    completion.TrySetCanceled();
                }
            },
            TaskScheduler.Default);
        await completion.Task.ConfigureAwait(false);
    }

    private static void EnsureAccepted(IUiScheduledOperation operation)
    {
        if (operation == null || !operation.IsAccepted)
        {
            throw new InvalidOperationException("The UI scheduler rejected the operation.");
        }
    }

    private static DispatcherPriority ToDispatcherPriority(UiSchedulePriority priority)
        => priority switch
        {
            UiSchedulePriority.Background => DispatcherPriority.Background,
            UiSchedulePriority.ContextIdle => DispatcherPriority.ContextIdle,
            UiSchedulePriority.ApplicationIdle => DispatcherPriority.ApplicationIdle,
            UiSchedulePriority.DataBind => DispatcherPriority.DataBind,
            _ => DispatcherPriority.Normal
        };
}

internal sealed class WpfUiScheduledOperation : IUiScheduledOperation
{
    private readonly DispatcherOperation operation;

    internal WpfUiScheduledOperation(DispatcherOperation operation)
    {
        this.operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    public bool IsAccepted => true;

    public bool IsCompleted
        => operation.Status == DispatcherOperationStatus.Completed
            || operation.Status == DispatcherOperationStatus.Aborted;

    public bool IsAborted => operation.Status == DispatcherOperationStatus.Aborted;

    public string RejectionReason => null;

    public Task Completion => operation.Task;

    public void Abort() => operation.Abort();
}

internal sealed class CompletedUiScheduledOperation : IUiScheduledOperation
{
    internal static readonly CompletedUiScheduledOperation Instance = new();

    private CompletedUiScheduledOperation()
    {
    }

    public bool IsAccepted => true;

    public bool IsCompleted => true;

    public bool IsAborted => false;

    public string RejectionReason => null;

    public Task Completion => Task.CompletedTask;

    public void Abort()
    {
    }
}

internal sealed class RejectedUiScheduledOperation : IUiScheduledOperation
{
    internal static readonly RejectedUiScheduledOperation Instance = new(null);

    internal RejectedUiScheduledOperation(string reason)
    {
        RejectionReason = reason;
    }

    public string RejectionReason { get; }

    public bool IsAccepted => false;

    public bool IsCompleted => true;

    public bool IsAborted => true;

    public Task Completion => Task.CompletedTask;

    public void Abort()
    {
    }
}

/// <summary>
/// Owns application-level lifecycle actions consumed by ViewModels.
/// </summary>
internal interface IApplicationLifetimePort
{
    bool IsFirstStartup { get; }

    void CompleteFirstStartup();

    void MarkCoordinatedShutdownStarted(string reason);

    void RequestShutdown();

    void RestartApplication();
}

/// <summary>
/// Supplies the culture names and persisted language values available to the UI.
/// </summary>
internal interface ICultureCatalog
{
    IReadOnlyDictionary<string, string> Cultures { get; }
}

internal sealed class JsonCultureCatalog : ICultureCatalog
{
    private readonly IReadOnlyDictionary<string, string> cultures =
        JsonLanguageCatalog.GetLanguagesSnapshot();

    public IReadOnlyDictionary<string, string> Cultures => cultures;
}
