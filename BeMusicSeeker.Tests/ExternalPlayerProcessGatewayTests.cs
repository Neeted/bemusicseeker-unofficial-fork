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
using Ribbit.Media.Audio;
using Ribbit.Windows;

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
            ((IExternalWindowPlayer)player).AttachWindowHost(new Win32ExternalPlayerWindowHost(IntPtr.Zero));

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
            ((IExternalWindowPlayer)player).AttachWindowHost(new Win32ExternalPlayerWindowHost(IntPtr.Zero));

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

    [TestMethod]
    public void ExternalPlayersUseWindowHostForEmbeddingOperations()
    {
        string[] playerSources =
        [
            SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Models", "uBMplay.cs"),
            SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Models", "BMIIDXView2015.cs"),
            SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Models", "LR2", "LR2body.cs")
        ];

        foreach (string source in playerSources)
        {
            StringAssert.Contains(source, "IExternalWindowPlayer");
            StringAssert.Contains(source, "IExternalPlayerWindowHost");
            Assert.IsFalse(source.Contains("Win32API.SetParent"));
            Assert.IsFalse(source.Contains("Win32API.SetWindowLong"));
            Assert.IsFalse(source.Contains("Win32API.GetWindowLong"));
            Assert.IsFalse(source.Contains("Win32API.SetWindowPos"));
            Assert.IsFalse(source.Contains("Win32API.GetForegroundWindow"));
            Assert.IsFalse(source.Contains("Win32API.SetForegroundWindow"));
            Assert.IsFalse(source.Contains("Win32API.SetFocus"));
            Assert.IsFalse(source.Contains("Win32API.GetWindowPlacement"));
            Assert.IsFalse(source.Contains("Win32API.SetWindowPlacement"));
            Assert.IsFalse(source.Contains("Win32API.GetWindowText"));
            Assert.IsFalse(source.Contains("Win32API.GetClassName"));
            Assert.IsFalse(source.Contains("Win32API.PostMessage"));
            Assert.IsFalse(source.Contains("DirectInputSendKey"));
            Assert.IsFalse(source.Contains("ThreadWindowHandles"));
            Assert.IsFalse(source.Contains("IntPtr"));
        }
    }

    [TestMethod]
    public void BmiIdxEmbedsReadyWindowThroughWindowHost()
    {
        WithTemporaryPlayerFiles("BMIIDXView2015_64.exe", (root, executablePath, chartPath) =>
        {
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(17));
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)));
            var player = new BMIIDXView2015(
                executablePath,
                new SettingsPlayerSettingsGateway(() => Settings.Default),
                gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);

            Assert.AreEqual(
                "GetForegroundWindow,AttachBmiIdxWindow,NotifyBmiIdxPlaybackStarted",
                string.Join(",", windowHost.Operations));

            gateway.Session.KeepRunning = false;
            player.CloseProcess();
        });
    }

    [TestMethod]
    public void Lr2AppliesWindowStyleAndPlacementThroughWindowHost()
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
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                Lr2WindowStyleApplied = false
            };
            var settingsGateway = new RecordingPlayerSettingsGateway();
            var player = new LR2body(
                executablePath,
                new LR2Config(configPath),
                settingsGateway,
                gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (EventHandler)null!);

            CollectionAssert.Contains(windowHost.Operations, "ApplyLr2WindowStyle");
            CollectionAssert.Contains(windowHost.Operations, "ApplyWindowPlacement");
            Assert.IsNotNull(windowHost.AppliedPlacement);

            gateway.Session.KeepRunning = false;
            player.CloseProcess();
            Assert.IsTrue(settingsGateway.SaveCalled);
        });
    }

    [TestMethod]
    public void UbmplayUsesWindowHostForAttachAndKeyMapping()
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executablePath, chartPath) =>
        {
            File.WriteAllText(Path.Combine(root, "ubm.ini"), "[Main]\r\n");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            var windowHandle = new ExternalWindowHandle(new IntPtr(31));
            gateway.Session.MainWindowHandle = windowHandle;
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = new[] { windowHandle },
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chartPath
            };
            var player = new uBMplay(
                executablePath,
                new SettingsPlayerSettingsGateway(() => Settings.Default),
                gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);
            player.PausePlayingBMSfileToggle();
            player.PausePlayingBMSfileToggle();
            player.FastForwardPlayingBMSfileStart();
            player.FastForwardPlayingBMSfileEnd();

            CollectionAssert.Contains(windowHost.Operations, "AttachUbmplayWindow");
            Assert.AreEqual(6, windowHost.Operations.Count(operation => operation == "SendKey"));
            CollectionAssert.Contains(windowHost.KeyEvents, "11:False:True");
            CollectionAssert.Contains(windowHost.KeyEvents, "80:True:True");

            windowHost.LegacyWindowEmbedding = true;
            gateway.ExistingProcesses = new[] { gateway.Session };
            gateway.Session.KeepRunning = false;
            Assert.ThrowsException<InvalidOperationException>(() => player.PlayStart(chartPath, (Action<object, EventArgs>)null!));
            CollectionAssert.Contains(windowHost.Operations, "DetachUbmplayWindow");
        });
    }

    [TestMethod]
    public void SavedWindowPlacementRestoreNormalizesFlagsAndSize()
    {
        var placement = new WindowPlacement(7, (int)Win32API.ShowWindowCommands.ShowMaximized, 1, 2, 3, 4, 10, 20, 810, 620);

        Win32API.WINDOWPLACEMENT native = Win32WindowPlacementAdapter.ToNativeForRestore(placement, 800, 600);

        Assert.AreEqual(0, native.Flags);
        Assert.AreEqual(Win32API.ShowWindowCommands.Normal, native.ShowCmd);
        Assert.AreEqual(800, native.NormalPosition.Width);
        Assert.AreEqual(600, native.NormalPosition.Height);
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

    private sealed class RecordingPlayerSettingsGateway : IPlayerSettingsGateway
    {
        private readonly PlayerSettingsSnapshot snapshot = new(
            AudioDriver.Invalid,
            string.Empty,
            string.Empty,
            SampleRate.AUTO,
            SampleFormat.AUTO,
            0,
            false,
            100,
            new PlayerResolution(800, 600),
            true,
            new WindowPlacement(0, 1, 0, 0, 0, 0, 0, 0, 800, 600));

        internal bool SaveCalled { get; private set; }

        public PlayerSettingsSnapshot CaptureSnapshot() => snapshot;

        public void ApplyNegotiatedAudioSettings(
            AudioDriver playerDriver,
            string playerDevice,
            string playerDeviceName,
            SampleRate playerSampleRate,
            SampleFormat playerFormat)
        {
        }

        public void SaveWindowPlacement(WindowPlacement windowPlacement)
        {
            SaveCalled = true;
        }
    }

    private sealed class RecordingExternalPlayerProcessGateway : IExternalPlayerProcessGateway
    {
        internal ExternalPlayerProcessDiscoveryRequest DiscoveryRequest { get; private set; } = null!;

        internal ExternalPlayerProcessLaunchRequest LaunchRequest { get; private set; } = null!;

        internal RecordingExternalPlayerProcessSession Session { get; } = new();

        internal IReadOnlyList<IExternalPlayerProcessSession> ExistingProcesses { get; set; } = Array.Empty<IExternalPlayerProcessSession>();

        public IReadOnlyList<IExternalPlayerProcessSession> FindExisting(ExternalPlayerProcessDiscoveryRequest request)
        {
            DiscoveryRequest = request;
            return ExistingProcesses;
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

        internal bool KeepRunning { get; set; }

        public event EventHandler Exited
        {
            add => exitHandlers += value;
            remove => exitHandlers -= value;
        }

        public bool HasExited => Started && !KeepRunning;

        public ExternalWindowHandle MainWindowHandle { get; set; }

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
            KeepRunning = false;
            Started = true;
        }
    }

    private sealed class RecordingExternalPlayerWindowHost : IExternalPlayerWindowHost
    {
        private ExternalWindowHandle foregroundWindow;

        internal RecordingExternalPlayerWindowHost(ExternalWindowHandle parentHandle)
        {
            ParentHandle = parentHandle;
        }

        internal List<string> Operations { get; } = new();

        internal List<string> KeyEvents { get; } = new();

        internal IReadOnlyList<ExternalWindowHandle> EnumeratedWindows { get; set; } = Array.Empty<ExternalWindowHandle>();

        internal bool LegacyWindowEmbedding { get; set; }

        internal string WindowClassName { get; set; } = string.Empty;

        internal string WindowTitle { get; set; } = string.Empty;

        internal bool Lr2WindowStyleApplied { get; set; }

        internal WindowPlacement? CapturedPlacement { get; private set; }

        internal WindowPlacement? AppliedPlacement { get; private set; }

        public ExternalWindowHandle ParentHandle { get; }

        public bool UsesLegacyWindowEmbedding => LegacyWindowEmbedding;

        public ExternalWindowHandle GetForegroundWindow()
        {
            Operations.Add("GetForegroundWindow");
            return foregroundWindow;
        }

        public bool IsWindow(ExternalWindowHandle window) => true;

        public bool SetForegroundWindow(ExternalWindowHandle window)
        {
            Operations.Add("SetForegroundWindow");
            foregroundWindow = window;
            return true;
        }

        public void SetFocus(ExternalWindowHandle window)
        {
            Operations.Add("SetFocus");
        }

        public IReadOnlyList<ExternalWindowHandle> EnumerateThreadWindows(IReadOnlyList<int> threadIds)
            => EnumeratedWindows;

        public string GetClassName(ExternalWindowHandle window) => WindowClassName;

        public string GetWindowText(ExternalWindowHandle window) => WindowTitle;

        public void SendKey(short scanCode, bool extended, bool keyDown)
        {
            Operations.Add("SendKey");
            KeyEvents.Add($"{scanCode}:{extended}:{keyDown}");
        }

        public void AttachBmiIdxWindow(ExternalWindowHandle childWindow)
        {
            Operations.Add("AttachBmiIdxWindow");
        }

        public void AttachUbmplayWindow(ExternalWindowHandle childWindow, bool legacyWindowStyle)
        {
            Operations.Add("AttachUbmplayWindow");
        }

        public void MoveExternalWindowOffscreen(ExternalWindowHandle childWindow)
        {
            Operations.Add("MoveExternalWindowOffscreen");
        }

        public void DetachUbmplayWindow(ExternalWindowHandle childWindow)
        {
            Operations.Add("DetachUbmplayWindow");
        }

        public bool IsLr2WindowStyleApplied(ExternalWindowHandle childWindow)
            => Lr2WindowStyleApplied;

        public void ApplyLr2WindowStyle(ExternalWindowHandle childWindow)
        {
            Operations.Add("ApplyLr2WindowStyle");
            Lr2WindowStyleApplied = true;
        }

        public void NotifyBmiIdxPlaybackStarted(ExternalWindowHandle childWindow)
        {
            Operations.Add("NotifyBmiIdxPlaybackStarted");
        }

        public WindowPlacement CaptureWindowPlacement(ExternalWindowHandle childWindow)
        {
            Operations.Add("CaptureWindowPlacement");
            CapturedPlacement = new WindowPlacement(7, 2, 0, 0, 0, 0, 10, 20, 810, 620);
            return CapturedPlacement;
        }

        public void ApplyWindowPlacement(ExternalWindowHandle childWindow, WindowPlacement placement, int width, int height)
        {
            Operations.Add("ApplyWindowPlacement");
            AppliedPlacement = placement;
        }
    }
}
