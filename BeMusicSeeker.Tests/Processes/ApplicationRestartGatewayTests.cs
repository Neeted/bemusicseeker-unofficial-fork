using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ApplicationRestartGatewayTests
{
    /// <summary>捕捉済みargvを受け取り、空引数・空白・引用符・末尾backslashをWindows形式で保持します。</summary>
    [TestMethod]
    public void RestartArgumentsPreserveCanonicalWindowsQuoting()
    {
        string arguments = ApplicationRestartArgumentsPolicy.BuildCommandLineArguments(
            new[]
            {
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

    /// <summary>空argvから空の再起動引数を作り、実行ファイルを引数として加えません。</summary>
    [TestMethod]
    public void EmptyRestartArgumentsRemainEmpty()
    {
        Assert.AreEqual(
            string.Empty,
            ApplicationRestartArgumentsPolicy.BuildCommandLineArguments(
                System.Array.Empty<string>()));
    }

    /// <summary>再起動前にmutexを解放し、実行ファイル・引数・作業先を注入gatewayへ渡して新プロセスの受付競合を防ぎます。</summary>
    [TestMethod]
    public void RestartCoordinatorPreservesRequestAndMutexOrdering()
    {
        var events = new List<string>();
        var gateway = new RecordingApplicationRestartGateway(events);
        ApplicationRestartCoordinator coordinator = new(
            ApplicationPathSnapshot.FromExecutablePath(@"C:\BeMusicSeeker\BeMusicSeeker.exe"),
            gateway,
            () => "--log-level info",
            () => events.Add("release"));

        coordinator.Restart();

        CollectionAssert.AreEqual(new[] { "release", "restart" }, events);
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
