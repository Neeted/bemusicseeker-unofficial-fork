using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalPlayerProcessGatewayTests
{
    [TestMethod]
    public void DiscoveryRequestNormalizesAndCopiesProcessNames()
    {
        ExternalPlayerProcessDiscoveryRequest request = ExternalPlayerProcessDiscoveryRequest.Create(
            " LR2body ",
            "LRHbody",
            "lr2body");

        CollectionAssert.AreEqual(
            new[] { "LR2body", "LRHbody" },
            request.ProcessNames.ToArray());
    }

    [TestMethod]
    public void RequestFactoriesRejectMissingValues()
    {
        Assert.ThrowsException<ArgumentException>(() => ExternalPlayerProcessDiscoveryRequest.Create(" "));
        Assert.ThrowsException<ArgumentException>(() => ExternalPlayerProcessDiscoveryRequest.Create());
        Assert.ThrowsException<ArgumentException>(() => ExternalPlayerProcessLaunchRequest.Create(" ", "args"));
    }

    [TestMethod]
    public void LaunchRequestPreservesExecutableArgumentsAndWindowStyle()
    {
        ExternalPlayerProcessLaunchRequest request = ExternalPlayerProcessLaunchRequest.Create(
            @"C:\Players\LR2body.exe",
            @"-A -NS ""C:\Songs\alpha.bms""",
            ProcessWindowStyle.Hidden);

        Assert.AreEqual(@"C:\Players\LR2body.exe", request.ExecutablePath);
        Assert.AreEqual(@"-A -NS ""C:\Songs\alpha.bms""", request.Arguments);
        Assert.AreEqual(ProcessWindowStyle.Hidden, request.WindowStyle);
    }

    [TestMethod]
    public void WindowsGatewaySubscribesBeforeStartingPreparedSession()
    {
        string commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
        {
            Assert.Inconclusive("The Windows command processor is unavailable.");
        }

        var gateway = new WindowsExternalPlayerProcessGateway();
        IExternalPlayerProcessSession session = gateway.Prepare(
            ExternalPlayerProcessLaunchRequest.Create(
                commandProcessor,
                "/c exit 0",
                ProcessWindowStyle.Hidden));
        bool exitObserved = false;
        session.Exited += (_, _) => exitObserved = true;

        session.Start();

        Assert.IsTrue(SpinWait.SpinUntil(() => session.HasExited, TimeSpan.FromSeconds(5)));
        Assert.IsTrue(SpinWait.SpinUntil(() => exitObserved, TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void Lr2bodyIssuesDiscoveryAndLaunchRequestsThroughGateway()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configDirectory = Path.Combine(root, "LR2files", "Config");
            Directory.CreateDirectory(configDirectory);
            string configPath = Path.Combine(configDirectory, "config.xml");
            File.WriteAllText(
                configPath,
                "<config><system><windowsize_x>800</windowsize_x><windowsize_y>600</windowsize_y><screenmode>1</screenmode></system><sound><volumemaster>100</volumemaster></sound></config>");
            var gateway = new RecordingExternalPlayerProcessGateway();
            var player = new LR2body(
                executablePath,
                new LR2Config(configPath),
                new SettingsPlayerSettingsGateway(() => Settings.Default),
                gateway);

            Assert.ThrowsException<TimeoutException>(() => player.PlayStart(chartPath, (EventHandler)null!));

            Assert.AreEqual(2, gateway.DiscoveryRequest.ProcessNames.Count);
            Assert.AreEqual("LR2body", gateway.DiscoveryRequest.ProcessNames[0]);
            Assert.AreEqual("LRHbody", gateway.DiscoveryRequest.ProcessNames[1]);
            Assert.AreEqual(executablePath, gateway.LaunchRequest.ExecutablePath);
            Assert.AreEqual("-A -NS \"" + chartPath + "\"", gateway.LaunchRequest.Arguments);
            Assert.AreEqual(ProcessWindowStyle.Hidden, gateway.LaunchRequest.WindowStyle);
            Assert.IsTrue(gateway.Session.Started);
        });
    }

    [TestMethod]
    public void BmiIdxViewIssuesDiscoveryAndLaunchRequestsThroughGateway()
    {
        WithTemporaryPlayerFiles("BMIIDXView2015_64.exe", (root, executablePath, chartPath) =>
        {
            var gateway = new RecordingExternalPlayerProcessGateway();
            var player = new BMIIDXView2015(
                executablePath,
                new SettingsPlayerSettingsGateway(() => Settings.Default),
                gateway);

            Assert.ThrowsException<InvalidOperationException>(() => player.PlayStart(chartPath, (EventHandler)null!));

            Assert.AreEqual(2, gateway.DiscoveryRequest.ProcessNames.Count);
            Assert.AreEqual("BMIIDXView2015", gateway.DiscoveryRequest.ProcessNames[0]);
            Assert.AreEqual("BMIIDXView2015_64", gateway.DiscoveryRequest.ProcessNames[1]);
            Assert.AreEqual(executablePath, gateway.LaunchRequest.ExecutablePath);
            Assert.AreEqual("-S \"" + chartPath + "\"", gateway.LaunchRequest.Arguments);
            Assert.AreEqual(ProcessWindowStyle.Minimized, gateway.LaunchRequest.WindowStyle);
            Assert.IsTrue(gateway.Session.Started);
        });
    }

    [TestMethod]
    public void ExternalPlayersUseProcessGatewayInsteadOfOwningRawProcessRoutes()
    {
        string[] playerSources =
        [
            SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Models", "uBMplay.cs"),
            SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Models", "BMIIDXView2015.cs"),
            SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Models", "LR2", "LR2body.cs")
        ];

        foreach (string source in playerSources)
        {
            StringAssert.Contains(source, "IExternalPlayerProcessGateway");
            StringAssert.Contains(source, "ExternalPlayerProcessDiscoveryRequest");
            StringAssert.Contains(source, "ExternalPlayerProcessLaunchRequest");
            Assert.IsFalse(source.Contains("Process.GetProcessesByName"));
            Assert.IsFalse(source.Contains("ProcessStartInfo"));
            Assert.IsFalse(source.Contains("new Process"));
            Assert.IsFalse(source.Contains("ProcessThread"));
        }

        StringAssert.Contains(playerSources[0], "ExternalPlayerProcessDiscoveryRequest.Create(\"uBMplay\")");
        StringAssert.Contains(playerSources[0], "-SP");
        StringAssert.Contains(playerSources[0], "bmsFilePath");
        StringAssert.Contains(playerSources[1], "ExternalPlayerProcessDiscoveryRequest.Create(\"BMIIDXView2015\", \"BMIIDXView2015_64\")");
        StringAssert.Contains(playerSources[1], "-S");
        StringAssert.Contains(playerSources[2], "ExternalPlayerProcessDiscoveryRequest.Create(\"LR2body\", \"LRHbody\")");
        StringAssert.Contains(playerSources[2], "-A -NS");
    }

    private static void WithTemporaryPlayerFiles(string executableName, Action<string, string, string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ExternalPlayerGatewayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executablePath = Path.Combine(root, executableName);
        string chartPath = Path.Combine(root, "chart.bms");
        File.WriteAllText(executablePath, string.Empty);
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n");
        try
        {
            action(root, executablePath, chartPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingExternalPlayerProcessGateway : IExternalPlayerProcessGateway
    {
        internal ExternalPlayerProcessDiscoveryRequest DiscoveryRequest { get; private set; } = null!;

        internal ExternalPlayerProcessLaunchRequest LaunchRequest { get; private set; } = null!;

        internal RecordingExternalPlayerProcessSession Session { get; } = new();

        public IReadOnlyList<IExternalPlayerProcessSession> FindExisting(ExternalPlayerProcessDiscoveryRequest request)
        {
            DiscoveryRequest = request;
            return Array.Empty<IExternalPlayerProcessSession>();
        }

        public IExternalPlayerProcessSession Prepare(ExternalPlayerProcessLaunchRequest request)
        {
            LaunchRequest = request;
            return Session;
        }
    }

    private sealed class RecordingExternalPlayerProcessSession : IExternalPlayerProcessSession
    {
        private EventHandler? exitHandlers;

        internal bool Started { get; private set; }

        public event EventHandler Exited
        {
            add => exitHandlers += value;
            remove => exitHandlers -= value;
        }

        public bool HasExited => Started;

        public IntPtr MainWindowHandle => IntPtr.Zero;

        public IReadOnlyList<int> ThreadIds => Array.Empty<int>();

        public void Start()
        {
            Started = true;
        }

        public void CloseMainWindow()
        {
        }

        public void Kill()
        {
            Started = true;
        }
    }
}
