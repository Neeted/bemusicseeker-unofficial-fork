using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaybackPreviousButtonGestureTests
{
    [TestMethod]
    public void UnloadedGesture_DoesNotRestartAfterPendingSingleClick()
    {
        var gesture = new PlaybackPreviousButtonGesture();
        gesture.Activate();

        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleClick(1));
        gesture.Deactivate();

        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleDelayElapsed());
    }

    [TestMethod]
    public void ActiveGesture_RestartsOnceAfterSingleClickDelay()
    {
        var gesture = new PlaybackPreviousButtonGesture();
        gesture.Activate();

        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleClick(1));
        Assert.AreEqual(PlaybackPreviousButtonAction.Restart, gesture.HandleDelayElapsed());
        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleDelayElapsed());
    }

    [TestMethod]
    public void DoubleClick_SelectsPreviousAndCancelsPendingRestart()
    {
        var gesture = new PlaybackPreviousButtonGesture();
        gesture.Activate();

        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleClick(1));
        Assert.AreEqual(PlaybackPreviousButtonAction.Previous, gesture.HandleClick(2));
        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleDelayElapsed());
    }
}
