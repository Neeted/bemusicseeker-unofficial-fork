namespace BeMusicSeeker.Views;

internal enum PlaybackPreviousButtonAction
{
    None,
    Restart,
    Previous
}

/// <summary>
/// Owns the delayed single-click versus immediate double-click decision for the previous button.
/// </summary>
internal sealed class PlaybackPreviousButtonGesture
{
    private bool isActive;

    private bool restartPending;

    internal void Activate()
    {
        isActive = true;
    }

    internal void Deactivate()
    {
        isActive = false;
        restartPending = false;
    }

    internal PlaybackPreviousButtonAction HandleClick(int clickCount)
    {
        if (!isActive)
        {
            return PlaybackPreviousButtonAction.None;
        }
        if (clickCount >= 2)
        {
            restartPending = false;
            return PlaybackPreviousButtonAction.Previous;
        }

        restartPending = true;
        return PlaybackPreviousButtonAction.None;
    }

    internal PlaybackPreviousButtonAction HandleDelayElapsed()
    {
        if (!isActive || !restartPending)
        {
            return PlaybackPreviousButtonAction.None;
        }

        restartPending = false;
        return PlaybackPreviousButtonAction.Restart;
    }
}
