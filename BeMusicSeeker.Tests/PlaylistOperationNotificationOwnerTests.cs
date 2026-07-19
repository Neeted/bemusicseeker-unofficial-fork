using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistOperationNotificationOwnerTests
{
    [TestMethod]
    public void NestedScopesPreserveParentOrderAndFlushEachReceiptOnce()
    {
        var owner = new PlaylistOperationNotificationOwner();
        using PlaylistOperationNotificationOwner.OperationNotificationScope outer = owner.BeginScope();

        owner.QueueInformation("outer-before", "info");
        using (PlaylistOperationNotificationOwner.OperationNotificationScope inner = owner.BeginScope())
        {
            owner.QueueWarning("inner", "warning");
            Assert.AreEqual(1, inner.Notifications.Count);
            Assert.AreEqual(PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning, inner.Notifications[0].Severity);

            var presented = new List<string>();
            inner.Flush(notification => presented.Add(notification.Message));
            inner.Flush(notification => presented.Add(notification.Message));
            CollectionAssert.AreEqual(new[] { "inner" }, presented);
        }

        owner.QueueError("outer-after", "error");
        var outerPresented = new List<string>();
        outer.Flush(notification => outerPresented.Add(notification.Message));
        CollectionAssert.AreEqual(new[] { "outer-before", "outer-after" }, outerPresented);
        Assert.AreEqual(0, outer.Notifications.Count);
    }

    [TestMethod]
    public void QueueWithoutScopeFailsClosedAndEmptyScopeFlushesWithoutCallback()
    {
        var owner = new PlaylistOperationNotificationOwner();
        Assert.ThrowsException<InvalidOperationException>(() => owner.QueueWarning("outside"));

        using PlaylistOperationNotificationOwner.OperationNotificationScope scope = owner.BeginScope();
        int callbackCount = 0;
        scope.Flush(_ => callbackCount++);
        Assert.AreEqual(0, callbackCount);
    }

    [TestMethod]
    public void FlushCallbackFailureDoesNotReplayCommittedReceipt()
    {
        var owner = new PlaylistOperationNotificationOwner();
        using PlaylistOperationNotificationOwner.OperationNotificationScope scope = owner.BeginScope();
        owner.QueueWarning("warning");

        Assert.ThrowsException<InvalidOperationException>(() => scope.Flush(_ => throw new InvalidOperationException("presentation failed")));
        Assert.AreEqual(0, scope.Notifications.Count);
        scope.Flush(_ => Assert.Fail("A flushed notification must not be presented again."));
    }
}
