using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistShutdownCoordinatorTests
{
    [TestMethod]
    public void RequestStopsBmtBeforeHydrationCleanupAndRethrowsFirstFailure()
    {
        PlaylistShutdownCoordinator coordinator = new();
        List<string> callbacks = [];
        InvalidOperationException hydrationFailure = new("hydration cleanup failed");

        InvalidOperationException thrown = Assert.ThrowsException<InvalidOperationException>(
            () => coordinator.Request(
                "window-closing",
                () => callbacks.Add("bmt"),
                () =>
                {
                    callbacks.Add("hydration");
                    throw hydrationFailure;
                },
                _ => callbacks.Add("log")));

        Assert.AreSame(hydrationFailure, thrown);
        CollectionAssert.AreEqual(new[] { "bmt", "hydration", "log" }, callbacks);
        Assert.IsTrue(coordinator.IsRequested);
    }

    [TestMethod]
    public void RequestRunsOnlyOnceAfterShutdownSignalIsPublished()
    {
        PlaylistShutdownCoordinator coordinator = new();
        int callbackCount = 0;

        bool firstRequest = coordinator.Request(
            "first",
            () => callbackCount++,
            () => callbackCount++,
            _ => callbackCount++);
        bool secondRequest = coordinator.Request(
            "second",
            () => callbackCount++,
            () => callbackCount++,
            _ => callbackCount++);

        Assert.IsTrue(firstRequest);
        Assert.IsFalse(secondRequest);
        Assert.AreEqual(3, callbackCount);
    }
}
