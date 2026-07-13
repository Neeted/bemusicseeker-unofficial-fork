using System;
using System.Windows.Threading;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// Preserves the WPF dispatcher timing used by playback state notifications.
/// </summary>
internal sealed class WpfPlaybackUiDispatcher : IPlaybackUiDispatcher
{
    private readonly Func<Dispatcher> dispatcherProvider;

    internal WpfPlaybackUiDispatcher(Func<Dispatcher> dispatcherProvider)
    {
        this.dispatcherProvider = dispatcherProvider ?? throw new ArgumentNullException(nameof(dispatcherProvider));
    }

    public void Dispatch(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        Dispatcher dispatcher = dispatcherProvider();
        if (dispatcher == null || dispatcher.CheckAccess())
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
