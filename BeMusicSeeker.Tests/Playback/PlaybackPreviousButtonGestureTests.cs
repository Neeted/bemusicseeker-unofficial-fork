using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaybackPreviousButtonGestureTests
{
    /// <summary>
    /// 保証：単クリック待機中に画面から外れたジェスチャーは、後で遅延通知が来ても再生位置を戻さない。
    /// </summary>
    [TestMethod]
    public void UnloadedGesture_DoesNotRestartAfterPendingSingleClick()
    {
        var gesture = new PlaybackPreviousButtonGesture();
        gesture.Activate();

        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleClick(1));
        gesture.Deactivate();

        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleDelayElapsed());
    }

    /// <summary>
    /// 保証：有効な単クリックの遅延通知で一度だけ再開要求を出し、重複通知で再開を繰り返さない。
    /// </summary>
    [TestMethod]
    public void ActiveGesture_RestartsOnceAfterSingleClickDelay()
    {
        var gesture = new PlaybackPreviousButtonGesture();
        gesture.Activate();

        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleClick(1));
        Assert.AreEqual(PlaybackPreviousButtonAction.Restart, gesture.HandleDelayElapsed());
        Assert.AreEqual(PlaybackPreviousButtonAction.None, gesture.HandleDelayElapsed());
    }

    /// <summary>
    /// 保証：二重クリックは前曲要求を出して単クリックの保留を取消し、後続遅延通知で現在曲へ戻らない。
    /// </summary>
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
