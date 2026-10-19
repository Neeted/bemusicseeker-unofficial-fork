using System;
using System.ComponentModel;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;

/// <summary>UI consumerのないmodel fixtureで、実score生産を通常queueから一回だけ観測します。</summary>
internal static class ScoreSnapshotTestSupport
{
    internal static PropertyChangedEventHandler CreateChangeHandler(BMSLibrary library, Action<ScoreSnapshotChange> observe)
    {
        int handled = library.NormalLibraryRefreshNotificationVersion;
        return (_, args) =>
        {
            if (args.PropertyName != nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion)) { return; }
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handled);
            handled = batch.LatestVersion;
            foreach (ScoreSnapshotChange change in batch.ScoreChanges) { observe(change); }
        };
    }
}
