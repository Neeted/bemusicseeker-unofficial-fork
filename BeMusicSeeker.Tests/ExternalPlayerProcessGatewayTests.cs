using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
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
    public void WindowsProcessSessionPublishesItselfAsExitSender()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c exit 0",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        var session = new WindowsExternalPlayerProcessSession(process, started: false);
        using var exited = new ManualResetEventSlim(false);
        object? observedSender = null;
        session.Exited += (sender, _) =>
        {
            observedSender = sender;
            exited.Set();
        };

        session.Start();

        Assert.IsTrue(SpinWait.SpinUntil(
            () => session.HasExited && exited.IsSet,
            TimeSpan.FromSeconds(5)));
        Assert.AreSame(session, observedSender);
    }

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
    public void BmiIdxViewInvokesSuppliedExitHandlerWhenProcessRaisesExited()
    {
        WithTemporaryPlayerFiles("BMIIDXView2015_64.exe", (root, executablePath, chartPath) =>
        {
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(17));
            var player = new BMIIDXView2015(
                executablePath,
                new SettingsPlayerSettingsGateway(() => Settings.Default),
                gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));
            object? observedSender = null;
            EventArgs? observedArgs = null;
            int callbackCount = 0;
            EventHandler suppliedExitHandler = (sender, args) =>
            {
                observedSender = sender;
                observedArgs = args;
                callbackCount++;
            };

            player.PlayStart(chartPath, suppliedExitHandler);

            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();

            Assert.AreEqual(1, callbackCount);
            Assert.AreSame(gateway.Session, observedSender);
            Assert.AreSame(EventArgs.Empty, observedArgs);
        });
    }

    [TestMethod]
    public void ExternalPlayerWaitPolicyFailsInsteadOfWaitingIndefinitely()
    {
        var policy = new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(25));
        var stopwatch = Stopwatch.StartNew();

        TimeoutException exception = Assert.ThrowsException<TimeoutException>(
            () => policy.WaitUntil(
                completed: () => false,
                aborted: () => false,
                attempt: () => { },
                timeoutMessage: "bounded wait expired",
                pollMilliseconds: 1));

        stopwatch.Stop();
        Assert.AreEqual("bounded wait expired", exception.Message);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
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
    public void BmiIdxCloseTimeoutRetainsProcessForAVisibleRetry()
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
                gateway,
                new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);
            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);
            gateway.Session.KillException = new InvalidOperationException("kill rejected");

            InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                player.CloseProcess);
            StringAssert.Contains(failure.Message, "could not be terminated");
            Assert.IsTrue(gateway.Session.KeepRunning);

            gateway.Session.KillException = null;
            player.CloseProcess();
            Assert.IsFalse(gateway.Session.KeepRunning);
            Assert.IsTrue(gateway.Session.KillCount >= 2);
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
            Assert.IsTrue(settingsGateway.PlacementUpdated);
            Assert.AreSame(windowHost.CapturedPlacement, settingsGateway.Placement);
            Assert.IsTrue(gateway.Session.HasExited);
            player.CloseProcess();
        });
    }

    [TestMethod]
    public void Lr2ReportsFailureWhenProcessExitsDuringWindowStyleApply()
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
                Lr2WindowStyleApplied = false,
                CompleteLr2WindowStyleApply = false,
                Lr2WindowStyleApplyAttempt = () => gateway.Session.KeepRunning = false
            };
            var player = new LR2body(
                executablePath,
                new LR2Config(configPath),
                new RecordingPlayerSettingsGateway(),
                gateway,
                new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(100)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => player.PlayStart(chartPath, (EventHandler)null!));

            StringAssert.Contains(exception.Message, "window style");
            CollectionAssert.Contains(windowHost.Operations, "ApplyLr2WindowStyle");
            CollectionAssert.DoesNotContain(windowHost.Operations, "ApplyWindowPlacement");
            XDocument restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("1", ReadLr2Value(restoredDocument, "system", "screenmode"));
            Assert.AreEqual("100", ReadLr2Value(restoredDocument, "sound", "volumemaster"));
        });
    }

    [TestMethod]
    public void Lr2PreviewRestoresSavedFieldsAndKeepsPlayerConfigInstanceUnchanged()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode><customfolder>保存済み</customfolder></system><sound><volumemaster>23</volumemaster></sound><jukebox /></config>");
            LR2Config playerConfig = new(configPath);
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            gateway.Session.BeforeStart = () =>
            {
                XDocument temporaryDocument = XDocument.Load(configPath);
                Assert.AreEqual("800", ReadLr2Value(temporaryDocument, "system", "windowsize_x"));
                Assert.AreEqual("600", ReadLr2Value(temporaryDocument, "system", "windowsize_y"));
                Assert.AreEqual("1", ReadLr2Value(temporaryDocument, "system", "screenmode"));
                Assert.AreEqual("100", ReadLr2Value(temporaryDocument, "sound", "volumemaster"));
                Assert.AreEqual("1", ReadLr2Value(temporaryDocument, "sound", "volumeflag"));

                LR2Config settingsDraft = new(configPath);
                Assert.AreEqual("640", ReadLr2Value(settingsDraft, "system", "windowsize_x"));
                Assert.AreEqual("480", ReadLr2Value(settingsDraft, "system", "windowsize_y"));
                Assert.AreEqual("0", ReadLr2Value(settingsDraft, "system", "screenmode"));
                Assert.AreEqual("23", ReadLr2Value(settingsDraft, "sound", "volumemaster"));
                Assert.IsNull(settingsDraft.Element("config")?.Element("sound")?.Element("volumeflag"));
            };
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)));
            var player = new LR2body(
                executablePath,
                playerConfig,
                new RecordingPlayerSettingsGateway(),
                gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            player.PlayStart(chartPath, (EventHandler)null!);

            Assert.AreEqual("640", ReadLr2Value(playerConfig, "system", "windowsize_x"));
            Assert.AreEqual("480", ReadLr2Value(playerConfig, "system", "windowsize_y"));
            Assert.AreEqual("0", ReadLr2Value(playerConfig, "system", "screenmode"));
            Assert.AreEqual("23", ReadLr2Value(playerConfig, "sound", "volumemaster"));
            Assert.IsNull(playerConfig.Element("config")?.Element("sound")?.Element("volumeflag"));
            XDocument restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("保存済み", ReadLr2Value(restoredDocument, "system", "customfolder"));
            Assert.AreEqual("640", ReadLr2Value(restoredDocument, "system", "windowsize_x"));
            Assert.AreEqual("480", ReadLr2Value(restoredDocument, "system", "windowsize_y"));
            Assert.AreEqual("0", ReadLr2Value(restoredDocument, "system", "screenmode"));
            Assert.AreEqual("23", ReadLr2Value(restoredDocument, "sound", "volumemaster"));
            Assert.IsNull(restoredDocument.Element("config")?.Element("sound")?.Element("volumeflag"));

            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();

            XDocument exitDocument = XDocument.Load(configPath);
            Assert.AreEqual("640", ReadLr2Value(exitDocument, "system", "windowsize_x"));
            Assert.AreEqual("480", ReadLr2Value(exitDocument, "system", "windowsize_y"));
            Assert.AreEqual("0", ReadLr2Value(exitDocument, "system", "screenmode"));
            Assert.AreEqual("23", ReadLr2Value(exitDocument, "sound", "volumemaster"));
            Assert.IsNull(exitDocument.Element("config")?.Element("sound")?.Element("volumeflag"));
        });
    }

    [TestMethod]
    public void Lr2PreviewUsesTheSameDraftForRootSaveAfterStartupBoundary()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            Directory.CreateDirectory(Path.Combine(root, "RootA"));
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster></sound><jukebox><path>RootA\\</path></jukebox></config>");
            LR2Config playerConfig = new(configPath);
            LR2Config settingsDraft = null;
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            gateway.Session.BeforeStart = () =>
            {
                settingsDraft = new LR2Config(configPath);
                Assert.AreEqual("640", ReadLr2Value(settingsDraft, "system", "windowsize_x"));
                Assert.AreEqual("23", ReadLr2Value(settingsDraft, "sound", "volumemaster"));
            };
            var player = new LR2body(
                executablePath,
                playerConfig,
                new RecordingPlayerSettingsGateway(),
                gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(
                new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));

            player.PlayStart(chartPath, (EventHandler)null!);

            Assert.IsNotNull(settingsDraft);
            Assert.IsTrue(settingsDraft.RemoveBMSSearchDirectoriesAndSave([Path.Combine(root, "RootA")]));
            XDocument savedAfterRootEdit = XDocument.Load(configPath);
            Assert.IsNull(savedAfterRootEdit.Element("config")?.Element("jukebox")?.Element("path"));
            Assert.AreEqual("640", ReadLr2Value(savedAfterRootEdit, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(savedAfterRootEdit, "sound", "volumemaster"));

            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();
            XDocument savedAfterExit = XDocument.Load(configPath);
            Assert.IsNull(savedAfterExit.Element("config")?.Element("jukebox")?.Element("path"));
            Assert.AreEqual("640", ReadLr2Value(savedAfterExit, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(savedAfterExit, "sound", "volumemaster"));
        });
    }

    [TestMethod]
    public void Lr2StartFailureBeforeProcessStartRestoresPublishedPreviewWithoutProcessCleanup()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster><volumeflag>0</volumeflag></sound></config>");
            LR2Config playerConfig = new(configPath);
            var startFailure = new InvalidOperationException("start rejected");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.StartException = startFailure;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            var player = new LR2body(
                executablePath,
                playerConfig,
                new RecordingPlayerSettingsGateway(),
                gateway);
            ((IExternalWindowPlayer)player).AttachWindowHost(
                new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99))));

            InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                () => player.PlayStart(chartPath, (EventHandler)null!));

            Assert.AreSame(startFailure, failure);
            Assert.IsFalse(gateway.Session.Started);
            Assert.AreEqual(0, gateway.Session.HasExitedReadCount);
            Assert.AreEqual(0, gateway.Session.CloseMainWindowCount);
            Assert.AreEqual(0, gateway.Session.KillCount);
            XDocument restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("640", ReadLr2Value(restoredDocument, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(restoredDocument, "sound", "volumemaster"));
            Assert.AreEqual("0", ReadLr2Value(restoredDocument, "sound", "volumeflag"));

            gateway.Session.StartException = null;
            gateway.Session.KeepRunning = true;
            player.PlayStart(chartPath, (EventHandler)null!);
            gateway.Session.KeepRunning = false;
            player.CloseProcess();
        });
    }

    [TestMethod]
    public void Lr2StartFailureAfterProcessStartRetainsUnkillableProcessForExplicitClose()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster><volumeflag>0</volumeflag></sound></config>");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            gateway.Session.KillException = new InvalidOperationException("kill rejected");
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                CompleteLr2WindowStyleApply = false
            };
            var player = new LR2body(
                executablePath,
                new LR2Config(configPath),
                new RecordingPlayerSettingsGateway(),
                gateway,
                new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(80)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

            InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                () => player.PlayStart(chartPath, (EventHandler)null!));

            StringAssert.Contains(failure.Message, configPath);
            StringAssert.Contains(failure.Message, "window style");
            StringAssert.Contains(failure.Message, "did not terminate");
            Assert.IsTrue(gateway.Session.Started);
            Assert.IsTrue(gateway.Session.KillCount > 0);
            Assert.IsTrue(gateway.Session.HasExitedReadCount > 0);
            XDocument restoredDocument = XDocument.Load(configPath);
            Assert.AreEqual("640", ReadLr2Value(restoredDocument, "system", "windowsize_x"));
            Assert.AreEqual("23", ReadLr2Value(restoredDocument, "sound", "volumemaster"));

            gateway.Session.KillException = null;
            player.CloseProcess();

            Assert.IsFalse(gateway.Session.KeepRunning);
            Assert.AreEqual("640", ReadLr2Value(XDocument.Load(configPath), "system", "windowsize_x"));
        });
    }

    [TestMethod]
    public void Lr2PreviewRestoreFailureKeepsPrimaryFailureAndConfigPath()
    {
        WithTemporaryPlayerFiles("LR2body.exe", (root, executablePath, chartPath) =>
        {
            string configPath = CreateLr2Config(
                root,
                "<config><system><windowsize_x>640</windowsize_x><windowsize_y>480</windowsize_y><screenmode>0</screenmode></system><sound><volumemaster>23</volumemaster><volumeflag>0</volumeflag></sound></config>");
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.Session.KeepRunning = true;
            gateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
            FileStream lockedConfig = null;
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                CompleteLr2WindowStyleApply = false,
                Lr2WindowStyleApplyAttempt = () =>
                {
                    gateway.Session.KeepRunning = false;
                    lockedConfig = new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            };
            try
            {
                var player = new LR2body(
                    executablePath,
                    new LR2Config(configPath),
                    new RecordingPlayerSettingsGateway(),
                    gateway,
                    new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(80)));
                ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);

                InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                    () => player.PlayStart(chartPath, (EventHandler)null!));

                StringAssert.Contains(failure.Message, configPath);
                StringAssert.Contains(failure.Message, "window style");
                AggregateException aggregate = failure.InnerException as AggregateException;
                Assert.IsNotNull(aggregate);
                Assert.IsTrue(aggregate!.InnerExceptions.Count >= 2);
            }
            finally
            {
                lockedConfig?.Dispose();
            }
        });
    }

    private static string CreateLr2Config(string root, string xml)
    {
        string configDirectory = Path.Combine(root, "LR2files", "Config");
        Directory.CreateDirectory(configDirectory);
        string configPath = Path.Combine(configDirectory, "config.xml");
        File.WriteAllText(configPath, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return configPath;
    }

    private static string ReadLr2Value(XDocument document, string sectionName, string fieldName)
    {
        return document.Element("config")?.Element(sectionName)?.Element(fieldName)?.Value;
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

            Assert.AreEqual(1, gateway.DiscoveryRequest.ProcessNames.Count);
            Assert.AreEqual("uBMplay", gateway.DiscoveryRequest.ProcessNames[0]);
            Assert.AreEqual(executablePath, gateway.LaunchRequest.ExecutablePath);
            Assert.AreEqual("-SP \"" + chartPath + "\"", gateway.LaunchRequest.Arguments);
            Assert.AreEqual(ProcessWindowStyle.Normal, gateway.LaunchRequest.WindowStyle);
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
    public void UbmplayRestoresTemporarySettingsOnCloseAndStartupFailure()
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executablePath, chartPath) =>
        {
            const string original =
                "[Main]\r\n"
                + "AlwaysOnTop=True\r\n"
                + "VSYNC=True\r\n"
                + "[Option]\r\n"
                + "BGA=1\r\n"
                + "AutoSeparate=False\r\n"
                + "SkinType=2\r\n"
                + "Volume=12\r\n";
            string iniPath = Path.Combine(root, "ubm.ini");
            File.WriteAllText(iniPath, original, Encoding.GetEncoding("shift_jis"));
            byte[] originalBytes = File.ReadAllBytes(iniPath);

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
            CollectionAssert.DoesNotContain(
                File.ReadAllLines(iniPath, Encoding.GetEncoding("shift_jis")),
                "AlwaysOnTop=True");

            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));

            gateway.Session.KeepRunning = true;
            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);
            StringAssert.Contains(
                File.ReadAllText(iniPath, Encoding.GetEncoding("shift_jis")),
                "AlwaysOnTop=False");

            gateway.Session.KeepRunning = false;
            player.CloseProcess();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));

            Assert.ThrowsException<InvalidOperationException>(
                () => player.PlayStart(chartPath, (Action<object, EventArgs>)null!));
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
        });
    }

    [TestMethod]
    public void UbmplayCloseTimeoutRetainsSettingsUntilOwnedProcessEventuallyExits()
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executablePath, chartPath) =>
        {
            const string original =
                "[Main]\r\nAlwaysOnTop=True\r\nVSYNC=True\r\n"
                + "[Option]\r\nBGA=1\r\nAutoSeparate=False\r\nSkinType=2\r\nVolume=12\r\n";
            string iniPath = Path.Combine(root, "ubm.ini");
            File.WriteAllText(iniPath, original, Encoding.GetEncoding("shift_jis"));
            byte[] originalBytes = File.ReadAllBytes(iniPath);
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
                gateway,
                new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);
            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);
            gateway.Session.KillException = new InvalidOperationException("kill rejected");

            Assert.ThrowsException<InvalidOperationException>(player.CloseProcess);

            File.WriteAllText(iniPath, "late process write", Encoding.GetEncoding("shift_jis"));
            gateway.Session.KeepRunning = false;
            gateway.Session.RaiseExited();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
        });
    }

    [TestMethod]
    public void UbmplayRequestTimeoutRetainsTransientProcessUntilEventualExit()
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executablePath, chartPath) =>
        {
            const string original =
                "[Main]\r\nAlwaysOnTop=True\r\nVSYNC=True\r\n"
                + "[Option]\r\nBGA=1\r\nAutoSeparate=False\r\nSkinType=2\r\nVolume=12\r\n";
            string iniPath = Path.Combine(root, "ubm.ini");
            File.WriteAllText(iniPath, original, Encoding.GetEncoding("shift_jis"));
            byte[] originalBytes = File.ReadAllBytes(iniPath);
            var mainSession = new RecordingExternalPlayerProcessSession
            {
                KeepRunning = true,
                MainWindowHandle = new ExternalWindowHandle(new IntPtr(31))
            };
            var requestSession = new RecordingExternalPlayerProcessSession
            {
                KeepRunning = true,
                KillException = new InvalidOperationException("request kill rejected")
            };
            var gateway = new RecordingExternalPlayerProcessGateway();
            gateway.EnqueuePreparedSession(mainSession);
            gateway.EnqueuePreparedSession(requestSession);
            var windowHost = new RecordingExternalPlayerWindowHost(new ExternalWindowHandle(new IntPtr(99)))
            {
                EnumeratedWindows = new[] { mainSession.MainWindowHandle },
                WindowClassName = "ThunderRT6FormDC",
                WindowTitle = chartPath
            };
            var player = new uBMplay(
                executablePath,
                new SettingsPlayerSettingsGateway(() => Settings.Default),
                gateway,
                new ExternalPlayerWaitPolicy(TimeSpan.FromMilliseconds(40)));
            ((IExternalWindowPlayer)player).AttachWindowHost(windowHost);
            player.PlayStart(chartPath, (Action<object, EventArgs>)null!);

            Assert.ThrowsException<AggregateException>(
                () => player.PlayStart(chartPath, (Action<object, EventArgs>)null!));
            Assert.IsTrue(requestSession.KillCount >= 1);

            File.WriteAllText(iniPath, "late request write", Encoding.GetEncoding("shift_jis"));
            requestSession.KeepRunning = false;
            requestSession.RaiseExited();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
        });
    }

    [TestMethod]
    public void UbmplayKeepsRewriteWhenReplacingExistingProcess()
    {
        WithTemporaryPlayerFiles("uBMplay.exe", (root, executablePath, chartPath) =>
        {
            const string original =
                "[Main]\r\n"
                + "AlwaysOnTop=True\r\n"
                + "VSYNC=True\r\n"
                + "[Option]\r\n"
                + "BGA=1\r\n"
                + "AutoSeparate=False\r\n"
                + "SkinType=2\r\n"
                + "Volume=12\r\n";
            string iniPath = Path.Combine(root, "ubm.ini");
            File.WriteAllText(iniPath, original, Encoding.GetEncoding("shift_jis"));
            byte[] originalBytes = File.ReadAllBytes(iniPath);

            var oldSession = new RecordingExternalPlayerProcessSession();
            oldSession.Start();
            var gateway = new RecordingExternalPlayerProcessGateway
            {
                ExistingProcesses = new[] { oldSession }
            };
            gateway.Session.KeepRunning = true;
            var windowHandle = new ExternalWindowHandle(new IntPtr(41));
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
            StringAssert.Contains(File.ReadAllText(iniPath, Encoding.GetEncoding("shift_jis")), "AlwaysOnTop=False");

            gateway.Session.KeepRunning = false;
            player.CloseProcess();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
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

    internal sealed class RecordingPlayerSettingsGateway : IPlayerSettingsGateway
    {
        private readonly PlayerSettingsSnapshot snapshot;

        internal RecordingPlayerSettingsGateway(PlayerSettingsSnapshot? snapshot = null)
        {
            this.snapshot = snapshot ?? new PlayerSettingsSnapshot(
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
        }

        internal bool PlacementUpdated { get; private set; }

        internal WindowPlacement? Placement { get; private set; }

        public PlayerSettingsSnapshot CaptureSnapshot() => snapshot;

        public void UpdateWindowPlacement(WindowPlacement windowPlacement)
        {
            PlacementUpdated = true;
            Placement = windowPlacement;
        }
    }

    internal sealed class RecordingExternalPlayerProcessGateway : IExternalPlayerProcessGateway
    {
        private readonly Queue<IExternalPlayerProcessSession> preparedSessions = new();

        internal ExternalPlayerProcessDiscoveryRequest DiscoveryRequest { get; private set; } = null!;

        internal ExternalPlayerProcessLaunchRequest LaunchRequest { get; private set; } = null!;

        internal RecordingExternalPlayerProcessSession Session { get; } = new();

        internal IReadOnlyList<IExternalPlayerProcessSession> ExistingProcesses { get; set; } = Array.Empty<IExternalPlayerProcessSession>();

        internal void EnqueuePreparedSession(IExternalPlayerProcessSession session)
        {
            preparedSessions.Enqueue(session);
        }

        public IReadOnlyList<IExternalPlayerProcessSession> FindExisting(ExternalPlayerProcessDiscoveryRequest request)
        {
            DiscoveryRequest = request;
            return ExistingProcesses;
        }

        public IExternalPlayerProcessSession Prepare(ExternalPlayerProcessLaunchRequest request)
        {
            LaunchRequest = request;
            return preparedSessions.Count > 0 ? preparedSessions.Dequeue() : Session;
        }
    }

    internal sealed class RecordingExternalPlayerProcessSession : IExternalPlayerProcessSession
    {
        private EventHandler? exitHandlers;

        internal bool Started { get; private set; }

        internal bool KeepRunning { get; set; }

        internal Exception? StartException { get; set; }

        internal Action? BeforeStart { get; set; }

        internal int HasExitedReadCount { get; private set; }

        internal int CloseMainWindowCount { get; private set; }

        internal Exception? KillException { get; set; }

        internal int KillCount { get; private set; }

        public event EventHandler Exited
        {
            add => exitHandlers += value;
            remove => exitHandlers -= value;
        }

        public bool HasExited
        {
            get
            {
                HasExitedReadCount++;
                return Started && !KeepRunning;
            }
        }

        public ExternalWindowHandle MainWindowHandle { get; set; }

        public IReadOnlyList<int> ThreadIds => Array.Empty<int>();

        public void Start()
        {
            BeforeStart?.Invoke();
            if (StartException != null)
            {
                throw StartException;
            }
            Started = true;
        }

        internal void RaiseExited()
        {
            exitHandlers?.Invoke(this, EventArgs.Empty);
        }

        public void CloseMainWindow()
        {
            CloseMainWindowCount++;
        }

        public void Kill()
        {
            KillCount++;
            if (KillException != null)
            {
                throw KillException;
            }
            KeepRunning = false;
            Started = true;
        }
    }

    internal sealed class RecordingExternalPlayerWindowHost : IExternalPlayerWindowHost
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

        internal bool CompleteLr2WindowStyleApply { get; set; } = true;

        internal Action? Lr2WindowStyleApplyAttempt { get; set; }

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
            Lr2WindowStyleApplyAttempt?.Invoke();
            if (CompleteLr2WindowStyleApply)
            {
                Lr2WindowStyleApplied = true;
            }
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
