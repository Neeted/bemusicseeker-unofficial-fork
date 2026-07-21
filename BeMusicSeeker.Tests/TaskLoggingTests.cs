using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TaskLoggingTests
{
    [TestMethod]
    public async Task LoggingAndPropagate_PreservesSuccessfulCompletion()
    {
        await Task.CompletedTask.LoggingAndPropagate();
    }

    [TestMethod]
    public async Task LoggingAndPropagate_RethrowsOriginalFailure()
    {
        var failure = new InvalidOperationException("workflow failed");

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Task.FromException(failure).LoggingAndPropagate());

        Assert.AreSame(failure, exception);
    }
}
