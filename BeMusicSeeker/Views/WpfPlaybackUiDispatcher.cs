using System;
using System.Windows.Threading;
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

        Dispatcher dispatcher = uiScheduler.Dispatcher;
        if (dispatcher == null || uiScheduler.CheckAccess())
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);
    }
}
