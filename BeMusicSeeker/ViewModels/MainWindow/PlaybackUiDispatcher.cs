using System;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Dispatches playback state notifications to the UI owner.
/// </summary>
internal interface IPlaybackUiDispatcher
{
    void Dispatch(Action action);
}
