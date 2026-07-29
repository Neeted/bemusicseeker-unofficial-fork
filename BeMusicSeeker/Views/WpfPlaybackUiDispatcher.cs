using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// Preserves the WPF dispatcher timing used by playback state notifications.
/// </summary>
internal sealed class WpfPlaybackUiDispatcher : IPlaybackUiDispatcher
{
    private readonly IUiScheduler uiScheduler;

    internal WpfPlaybackUiDispatcher(IUiScheduler uiScheduler)
    {
        this.uiScheduler = uiScheduler ?? throw new ArgumentNullException(nameof(uiScheduler));
    }

    public void Dispatch(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (uiScheduler.CanExecuteInline)
        {
            action();
            return;
        }

        if (!uiScheduler.IsAvailable)
        {
            return;
        }

        uiScheduler.Schedule(action, UiSchedulePriority.DataBind);
    }

    public Task DispatchAsync(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        if (uiScheduler.CanExecuteInline)
        {
            action();
            return Task.CompletedTask;
        }
        if (!uiScheduler.IsAvailable)
        {
            return Task.FromException(
                new InvalidOperationException("Playback UI dispatcher is unavailable."));
        }
        IUiScheduledOperation operation =
            uiScheduler.Schedule(action, UiSchedulePriority.DataBind);
        return operation?.Completion
            ?? Task.FromException(
                new InvalidOperationException("Playback UI dispatcher rejected the operation."));
    }
}
