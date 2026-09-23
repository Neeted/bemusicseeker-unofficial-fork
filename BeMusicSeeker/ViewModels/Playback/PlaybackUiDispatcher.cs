using System;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Dispatches playback state notifications to the UI owner.
/// </summary>
internal interface IPlaybackUiDispatcher
{
    void Dispatch(Action action);

    Task DispatchAsync(Action action);
}
