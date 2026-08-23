using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationRestartGatewayTests
{
    [TestMethod]
    public void RestartArgumentsExcludeExecutableAndPreserveCanonicalWindowsQuoting()
    {
        string arguments = ApplicationRestartArgumentsPolicy.BuildCommandLineArguments(
            new[]
            {
                @"C:\BeMusicSeeker\BeMusicSeeker.exe",
                "first",
                string.Empty,
                "two words",
                "quote\"inside",
                "trail \\",
            });

        string quotedValue = new string(new[]
        {
            '"', 'q', 'u', 'o', 't', 'e', '\\', '"', 'i', 'n', 's', 'i', 'd', 'e', '"'
        });
        string trailingSlashValue = new string(new[]
        {
            '"', 't', 'r', 'a', 'i', 'l', ' ', '\\', '\\', '"'
        });

        Assert.AreEqual(
            string.Join(" ", "first", "\"\"", "\"two words\"", quotedValue, trailingSlashValue),
            arguments);
    }

    [TestMethod]
    public void RestartArgumentsWithOnlyExecutableAreEmpty()
    {
        Assert.AreEqual(
            string.Empty,
            ApplicationRestartArgumentsPolicy.BuildCommandLineArguments(
                new[] { @"C:\BeMusicSeeker\BeMusicSeeker.exe" }));
    }

    [TestMethod]
    public void RestartRequestPreservesExecutableArgumentsAndWorkingDirectory()
    {
        ApplicationRestartRequest request = ApplicationRestartRequest.Create(
            @"C:\BeMusicSeeker\BeMusicSeeker.exe",
            "--log-level info",
            @"C:\BeMusicSeeker");

        Assert.AreEqual(@"C:\BeMusicSeeker\BeMusicSeeker.exe", request.ExecutablePath);
        Assert.AreEqual("--log-level info", request.Arguments);
        Assert.AreEqual(@"C:\BeMusicSeeker", request.WorkingDirectory);
    }

    [TestMethod]
    public void RestartCoordinatorPreservesRequestAndShutdownOrdering()
    {
        var events = new List<string>();
        var gateway = new RecordingApplicationRestartGateway(events);
        ApplicationRestartCoordinator coordinator = new(
            ApplicationPathSnapshot.FromExecutablePath(@"C:\BeMusicSeeker\BeMusicSeeker.exe"),
            gateway,
            () => "--log-level info",
            () => events.Add("release"),
            () => events.Add("shutdown"));

        coordinator.Restart();

        CollectionAssert.AreEqual(new[] { "release", "restart", "shutdown" }, events);
        Assert.AreEqual(@"C:\BeMusicSeeker\BeMusicSeeker.exe", gateway.Request.ExecutablePath);
        Assert.AreEqual("--log-level info", gateway.Request.Arguments);
        Assert.AreEqual(@"C:\BeMusicSeeker", gateway.Request.WorkingDirectory);
    }

    private sealed class RecordingApplicationRestartGateway : IApplicationRestartGateway
    {
        private readonly IList<string> events;

        internal RecordingApplicationRestartGateway(IList<string> events)
        {
            this.events = events;
        }

        internal ApplicationRestartRequest Request { get; private set; } = null!;

        public void Restart(ApplicationRestartRequest request)
        {
            Request = request;
            events.Add("restart");
        }
    }
}
