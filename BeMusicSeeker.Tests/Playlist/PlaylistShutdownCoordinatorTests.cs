using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistShutdownCoordinatorTests
{
    /// <summary>終了markを先に公開し、複数callback失敗でも全後続を実行して最初の元例外とstackを返し、再要求は再実行しないことを確認します。</summary>
    [TestMethod]
    public void RequestRunsAllCallbacksAndRethrowsFirstFailureWithOriginalStack()
    {
        PlaylistShutdownCoordinator coordinator = new();
        List<string> callbacks = [];
        InvalidOperationException primaryFailure = new("readiness cancellation failed");
        InvalidOperationException laterFailure = new("hydration cleanup failed");

        InvalidOperationException thrown = Assert.ThrowsException<InvalidOperationException>(
            () => coordinator.Request(
                "window-closing",
                () =>
                {
                    Assert.IsTrue(coordinator.IsRequested);
                    callbacks.Add("readiness");
                    ThrowPrimaryFailure(primaryFailure);
                },
                () =>
                {
                    Assert.IsTrue(coordinator.IsRequested);
                    callbacks.Add("bmt");
                },
                () =>
                {
                    Assert.IsTrue(coordinator.IsRequested);
                    callbacks.Add("hydration");
                    throw laterFailure;
                },
                _ =>
                {
                    Assert.IsTrue(coordinator.IsRequested);
                    callbacks.Add("log");
                }));

        Assert.AreSame(primaryFailure, thrown);
        StringAssert.Contains(thrown.StackTrace ?? string.Empty, nameof(ThrowPrimaryFailure));
        CollectionAssert.AreEqual(new[] { "readiness", "bmt", "hydration", "log" }, callbacks);
        Assert.IsTrue(coordinator.IsRequested);

        List<string> callbackSnapshot = [.. callbacks];
        Assert.IsFalse(coordinator.Request(
            "second",
            () => callbacks.Add("second-readiness"),
            () => callbacks.Add("second-bmt"),
            () => callbacks.Add("second-hydration"),
            _ => callbacks.Add("second-log")));
        CollectionAssert.AreEqual(callbackSnapshot, callbacks);
    }

    /// <summary>正常終了要求はcallback前にmarkを公開して全callbackを一回だけ実行し、次要求を拒否して終了作業の重複を検出します。</summary>
    [TestMethod]
    public void RequestRunsOnlyOnceAfterShutdownSignalIsPublished()
    {
        PlaylistShutdownCoordinator coordinator = new();
        int callbackCount = 0;

        bool firstRequest = coordinator.Request(
            "first",
            () =>
            {
                Assert.IsTrue(coordinator.IsRequested);
                callbackCount++;
            },
            () =>
            {
                Assert.IsTrue(coordinator.IsRequested);
                callbackCount++;
            },
            () =>
            {
                Assert.IsTrue(coordinator.IsRequested);
                callbackCount++;
            },
            _ =>
            {
                Assert.IsTrue(coordinator.IsRequested);
                callbackCount++;
            });
        bool secondRequest = coordinator.Request(
            "second",
            () => callbackCount++,
            () => callbackCount++,
            () => callbackCount++,
            _ => callbackCount++);

        Assert.IsTrue(firstRequest);
        Assert.IsFalse(secondRequest);
        Assert.AreEqual(4, callbackCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowPrimaryFailure(Exception failure)
    {
        throw failure;
    }
}
